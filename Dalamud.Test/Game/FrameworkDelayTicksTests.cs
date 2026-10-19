#nullable enable

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;

using Xunit;

namespace Dalamud.Test.Game
{
    // The framework completes DelayTicks tasks once per update. The framework itself is created without its
    // constructor (that one hooks the game); only the fields this path reads are filled in.
    public sealed class FrameworkDelayTicksTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly Framework framework;
        private readonly Action completeDelayedTicks;
        private readonly FieldInfo tickCounter = typeof(Framework).GetField("tickCounter", Any)!;
        private readonly ICollection pending;

        public FrameworkDelayTicksTests()
        {
            this.framework = (Framework)RuntimeHelpers.GetUninitializedObject(typeof(Framework));
            typeof(Framework).GetField("frameworkDestroy", Any)!.SetValue(this.framework, new CancellationTokenSource());
            typeof(Framework).GetField("tickDelayedLock", Any)!.SetValue(this.framework, new object());
            var entries = typeof(Framework).GetField("tickDelayedEntries", Any)!;
            this.pending = (ICollection)Activator.CreateInstance(entries.FieldType)!;
            entries.SetValue(this.framework, this.pending);
            var due = typeof(Framework).GetField("tickDelayedDue", Any)!;
            due.SetValue(this.framework, Activator.CreateInstance(due.FieldType));
            this.completeDelayedTicks = typeof(Framework).GetMethod("CompleteDelayedTicks", Any)!.CreateDelegate<Action>(this.framework);
        }

        [Fact]
        public void ATaskCompletesOnTheUpdateItAskedFor()
        {
            var one = this.framework.DelayTicks(1);
            var three = this.framework.DelayTicks(3);
            Assert.Same(Task.CompletedTask, this.framework.DelayTicks(0));
            Assert.Equal(2, this.pending.Count);

            this.Update();
            Assert.True(one.IsCompletedSuccessfully);
            Assert.False(three.IsCompleted);
            Assert.Single(this.pending);

            this.Update();
            Assert.False(three.IsCompleted);

            this.Update();
            Assert.True(three.IsCompletedSuccessfully);
            Assert.Empty(this.pending);
        }

        [Fact]
        public void ManyTasksDueOnDifferentAndSameUpdatesEachCompleteOnTheirOwn()
        {
            var rng = new Random(1234);
            var tasks = new List<(int Ticks, Task Task)>();
            for (var i = 0; i < 300; i++)
            {
                var ticks = 1 + rng.Next(20);
                tasks.Add((ticks, this.framework.DelayTicks(ticks)));
            }

            for (var update = 1; update <= 21; update++)
            {
                this.Update();
                foreach (var (ticks, task) in tasks)
                    Assert.Equal(ticks <= update, task.IsCompletedSuccessfully);
            }

            Assert.Empty(this.pending);
        }

        [Fact]
        public void ACanceledTaskIsCompletedAsCanceledOnTheNextUpdate()
        {
            using var cancel = new CancellationTokenSource();
            var canceled = this.framework.DelayTicks(100, cancel.Token);
            var kept = this.framework.DelayTicks(2);

            this.Update();
            Assert.False(canceled.IsCompleted);

            cancel.Cancel();
            Assert.False(canceled.IsCompleted);
            this.Update();
            Assert.True(canceled.IsCanceled);
            Assert.True(kept.IsCompletedSuccessfully);
            Assert.Empty(this.pending);
        }

        [Fact]
        public void CancellationWinsOverExpiryAndCarriesTheToken()
        {
            using var cancel = new CancellationTokenSource();
            var task = this.framework.DelayTicks(1, cancel.Token);
            cancel.Cancel();
            this.Update();

            Assert.True(task.IsCanceled);
            var ex = Assert.ThrowsAny<OperationCanceledException>(() => task.GetAwaiter().GetResult());
            Assert.Equal(cancel.Token, ex.CancellationToken);
        }

        [Fact]
        public void AnAlreadyCanceledTokenIsCompletedOnTheFirstUpdate()
        {
            var task = this.framework.DelayTicks(1000, new CancellationToken(true));
            Assert.False(task.IsCompleted);
            this.Update();
            Assert.True(task.IsCanceled);
            Assert.Empty(this.pending);
        }

        [Fact]
        public void TasksAddedAfterEverythingCompletedAreStillSeen()
        {
            for (var round = 0; round < 50; round++)
            {
                var tasks = new List<Task>();
                for (var i = 1; i <= 4; i++)
                    tasks.Add(this.framework.DelayTicks(i));

                for (var i = 0; i < 4; i++)
                {
                    Assert.False(tasks[i].IsCompleted);
                    this.Update();
                    Assert.True(tasks[i].IsCompletedSuccessfully);
                }

                Assert.Empty(this.pending);
                this.Update();
            }
        }

        [Fact]
        public async Task TasksAddedFromOtherThreadsWhileUpdatingAllComplete()
        {
            const int Producers = 4;
            const int PerProducer = 2000;
            var all = new ConcurrentBag<Task>();
            var producers = new Task[Producers];
            for (var p = 0; p < Producers; p++)
            {
                producers[p] = Task.Run(() =>
                {
                    for (var i = 0; i < PerProducer; i++)
                        all.Add(this.framework.DelayTicks(1 + (i % 3)));
                });
            }

            var producing = Task.WhenAll(producers);
            while (!producing.IsCompleted)
                this.Update();
            await producing;

            for (var i = 0; i < 4; i++)
                this.Update();

            Assert.Equal(Producers * PerProducer, all.Count);
            Assert.All(all, task => Assert.True(task.IsCompletedSuccessfully));
            Assert.Empty(this.pending);
        }

        [Fact]
        public async Task TasksAddedAndCanceledFromOtherThreadsWhileUpdatingAllEndAsAsked()
        {
            const int Producers = 4;
            const int PerProducer = 5000;
            var results = new ConcurrentBag<(Task Task, bool Canceled)>();
            var producers = new Task[Producers];
            for (var p = 0; p < Producers; p++)
            {
                var seed = p;
                producers[p] = Task.Run(() =>
                {
                    var rng = new Random(seed);
                    for (var i = 0; i < PerProducer; i++)
                    {
                        if (rng.Next(2) == 0)
                        {
                            results.Add((this.framework.DelayTicks(1 + rng.Next(5)), false));
                            continue;
                        }

                        var cts = new CancellationTokenSource();
                        results.Add((this.framework.DelayTicks(1_000_000, cts.Token), true));
                        cts.Cancel();
                    }
                });
            }

            var producing = Task.WhenAll(producers);
            while (!producing.IsCompleted)
                this.Update();
            await producing;

            for (var i = 0; i < 6; i++)
                this.Update();

            Assert.Equal(Producers * PerProducer, results.Count);
            Assert.All(results, r => Assert.True(r.Canceled ? r.Task.IsCanceled : r.Task.IsCompletedSuccessfully));
            Assert.Empty(this.pending);
        }

        [Fact]
        public void AnUpdateWithNothingPendingAllocatesNothing()
        {
            var task = this.framework.DelayTicks(1);
            this.Update();
            Assert.True(task.IsCompletedSuccessfully);

            for (var i = 0; i < 2000; i++)
                this.completeDelayedTicks();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                this.completeDelayedTicks();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Fact]
        public void AnUpdateWithTasksWaitingAllocatesNothing()
        {
            using var cancel = new CancellationTokenSource();
            var waiting = Enumerable.Range(0, 3).Select(_ => this.framework.DelayTicks(1_000_000, cancel.Token)).ToList();
            for (var i = 0; i < 2000; i++)
                this.completeDelayedTicks();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                this.completeDelayedTicks();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.All(waiting, task => Assert.False(task.IsCompleted));
        }

        // What the update hook does around the call: count the tick, then complete what is due.
        private void Update()
        {
            this.tickCounter.SetValue(this.framework, (ulong)this.tickCounter.GetValue(this.framework)! + 1);
            this.completeDelayedTicks();
        }
    }
}
