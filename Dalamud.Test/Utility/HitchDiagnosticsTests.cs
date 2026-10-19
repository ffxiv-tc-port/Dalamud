#nullable enable

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;
using Dalamud.Utility;
using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    // Pure parts of the hitch and GC pause diagnostics: ranking, cooldown counting, bucket edges, task naming.
    public class HitchDiagnosticsTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [Fact]
        public void SlowestThreeKeepsTheThreeLongestLongestFirst()
        {
            var top = default(SlowestThree);
            top.Offer("a", 5);
            top.Offer("b", 50);
            top.Offer("c", 1);
            top.Offer("d", 30);
            top.Offer("e", 2);
            top.Offer("f", 40);

            Assert.Equal(3, top.Count);
            Assert.Equal(("b", 50L), top[0]);
            Assert.Equal(("f", 40L), top[1]);
            Assert.Equal(("d", 30L), top[2]);
        }

        [Fact]
        public void SlowestThreeRanksTheFirstOfEqualLengthsHigher()
        {
            var top = default(SlowestThree);
            top.Offer("first", 10);
            top.Offer("second", 10);
            top.Offer("third", 10);
            top.Offer("fourth", 10);

            Assert.Equal("first", top[0].Name);
            Assert.Equal("second", top[1].Name);
            Assert.Equal("third", top[2].Name);
        }

        [Fact]
        public void SlowestThreeWithFewerEntriesHoldsOnlyThose()
        {
            var top = default(SlowestThree);
            Assert.Equal(0, top.Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => top[0]);

            top.Offer("small", 1);
            top.Offer("big", 9);
            Assert.Equal(2, top.Count);
            Assert.Equal(("big", 9L), top[0]);
            Assert.Equal(("small", 1L), top[1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => top[2]);
        }

        [Fact]
        public void SlowestThreeCopiesRankWithoutTouchingTheOriginal()
        {
            var top = default(SlowestThree);
            top.Offer("a", 3);
            var copy = top;
            copy.Offer("b", 9);

            Assert.Equal(1, top.Count);
            Assert.Equal(("b", 9L), copy[0]);
        }

        [Fact]
        public void CooldownLogsTheFirstAndCountsTheLeftOutForTheNextLine()
        {
            var cooldown = new HitchCooldown(100);

            Assert.True(cooldown.TryLog(1000, 60, out var leftOut, out var leftOutMax));
            Assert.Equal(0, leftOut);
            Assert.Equal(0, leftOutMax);

            Assert.False(cooldown.TryLog(1050, 70, out _, out _));
            Assert.False(cooldown.TryLog(1080, 200, out _, out _));
            Assert.False(cooldown.TryLog(1100, 55, out _, out _));

            Assert.True(cooldown.TryLog(1101, 51, out leftOut, out leftOutMax));
            Assert.Equal(3, leftOut);
            Assert.Equal(200, leftOutMax);

            Assert.True(cooldown.TryLog(1300, 51, out leftOut, out leftOutMax));
            Assert.Equal(0, leftOut);
            Assert.Equal(0, leftOutMax);
        }

        [Fact]
        public void CooldownLogsTheFirstEvenAtTimestampZero()
        {
            var cooldown = new HitchCooldown(100);
            Assert.True(cooldown.TryLog(0, 60, out _, out _));
            Assert.False(cooldown.TryLog(0, 60, out _, out _));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(9.9999, 0)]
        [InlineData(10, 1)]
        [InlineData(16.9999, 1)]
        [InlineData(17, 2)]
        [InlineData(32.9999, 2)]
        [InlineData(33, 3)]
        [InlineData(49.9999, 3)]
        [InlineData(50, 4)]
        [InlineData(99.9999, 4)]
        [InlineData(100, 5)]
        [InlineData(361, 5)]
        public void PauseBucketEdgesBelongToTheUpperBucket(double milliseconds, int bucket)
        {
            Assert.Equal(bucket, GcPauseDistribution.BucketOf(TimeSpan.FromTicks((long)Math.Round(milliseconds * TimeSpan.TicksPerMillisecond))));
        }

        [Fact]
        public void PauseDistributionCountsEachScreenAndItsCoverage()
        {
            var distribution = new GcPauseDistribution();
            distribution.Add(GameScreen.InGame, TimeSpan.FromMilliseconds(14), 1);
            distribution.Add(GameScreen.InGame, TimeSpan.FromMilliseconds(36), 3);
            distribution.Add(GameScreen.Loading, TimeSpan.FromMilliseconds(107), 1);
            distribution.Add(GameScreen.Title, TimeSpan.FromMilliseconds(2), 0);

            Assert.Equal("0 / 1 / 0 / 1 / 0 / 0 (seen 2 of 4 collections)", distribution.Describe(GameScreen.InGame));
            Assert.Equal("0 / 0 / 0 / 0 / 0 / 1 (seen 1 of 1 collections)", distribution.Describe(GameScreen.Loading));
            Assert.Equal("1 / 0 / 0 / 0 / 0 / 0 (seen 1 of 1 collections)", distribution.Describe(GameScreen.Title));

            distribution.Reset();
            Assert.Equal("0 / 0 / 0 / 0 / 0 / 0 (seen 0 of 0 collections)", distribution.Describe(GameScreen.InGame));
            Assert.Equal(0, distribution.Collections(GameScreen.Loading));
        }

        [Fact]
        public void AttributionRanksPluginsTasksAndTheRestTogether()
        {
            var attribution = new FrameworkUpdateAttribution();
            attribution.Begin();
            attribution.AddPlugin("Small", Ticks(1));
            attribution.AddPlugin("Big", Ticks(40));
            attribution.AddPlugin("Medium", Ticks(5));
            attribution.SetTasks(Ticks(20), null, null, 0);

            Assert.Equal(
                "Big 40.0 ms, framework thread tasks 20.0 ms, Dalamud and the rest 9.0 ms",
                attribution.Describe(Ticks(75)));
        }

        [Fact]
        public void AttributionRecordsOnlyBetweenBeginAndEnd()
        {
            var attribution = new FrameworkUpdateAttribution();
            attribution.AddPlugin("Before", Ticks(30));
            attribution.Begin();
            attribution.AddPlugin("During", Ticks(10));
            attribution.End();
            attribution.AddPlugin("After", Ticks(30));
            attribution.SetTasks(Ticks(30), null, null, 0);

            Assert.Equal(
                "During 10.0 ms, Dalamud and the rest 2.0 ms, framework thread tasks 0.0 ms",
                attribution.Describe(Ticks(12)));
        }

        [Fact]
        public void TheRestIsNeverNegative()
        {
            var attribution = new FrameworkUpdateAttribution();
            attribution.Begin();
            attribution.AddPlugin("Only", Ticks(10));

            Assert.Equal(
                "Only 10.0 ms, framework thread tasks 0.0 ms, Dalamud and the rest 0.0 ms",
                attribution.Describe(Ticks(5)));
        }

        [Fact]
        public void ARunOnTickTaskIsNamedByTheCallersDelegateNotDalamudsWrapper()
        {
            var (framework, scheduler) = NewFramework();
            var ran = false;
            framework.RunOnTick(() => { ran = true; });

            scheduler.RunTimed(out var slowest, out var action, out _);

            Assert.True(ran);
            Assert.NotNull(slowest);
            var name = FrameworkUpdateAttribution.DescribeTask(slowest!, action);
            Assert.Contains(nameof(this.ARunOnTickTaskIsNamedByTheCallersDelegateNotDalamudsWrapper), name);
            Assert.EndsWith("(Dalamud.Test)", name);
        }

        [Fact]
        public void AnAwaitContinuationIsNamedByItsStateMachine()
        {
            // The test runner's synchronization context would take the continuation; the framework thread has none.
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                this.AwaitContinuationWithoutContext();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(context);
            }
        }

        private void AwaitContinuationWithoutContext()
        {
            var scheduler = new ThreadBoundTaskScheduler();
            var factory = new TaskFactory(CancellationToken.None, TaskCreationOptions.None, TaskContinuationOptions.None, scheduler);
            var gate = new TaskCompletionSource();
            var done = false;
            factory.StartNew(async () =>
            {
                await gate.Task;
                done = true;
            });

            scheduler.RunTimed(out _, out _, out _);
            gate.SetResult();
            var deadline = Stopwatch.StartNew();
            Task? slowest = null;
            Delegate? action = null;
            while (!done && deadline.ElapsedMilliseconds < 5000)
            {
                scheduler.RunTimed(out var ran, out var ranAction, out _);
                if (slowest == null)
                    (slowest, action) = (ran, ranAction);
                Thread.Sleep(1);
            }

            Assert.True(done);
            Assert.NotNull(slowest);
            var name = FrameworkUpdateAttribution.DescribeTask(slowest!, action);
            Assert.Contains(nameof(this.AwaitContinuationWithoutContext), name);
            Assert.EndsWith("(Dalamud.Test)", name);
        }

        [Fact]
        public void RunTimedRunsEveryQueuedTaskAndReportsTheLongest()
        {
            var scheduler = new ThreadBoundTaskScheduler();
            var factory = new TaskFactory(CancellationToken.None, TaskCreationOptions.None, TaskContinuationOptions.None, scheduler);
            var order = 0;
            var quick = factory.StartNew(() => order++);
            var slow = factory.StartNew(() => Thread.Sleep(30));

            var total = scheduler.RunTimed(out var slowest, out _, out var slowestTicks);

            Assert.True(quick.IsCompletedSuccessfully && slow.IsCompletedSuccessfully);
            Assert.Same(slow, slowest);
            Assert.True(slowestTicks <= total);
            Assert.True(HitchCounters.Milliseconds(slowestTicks) >= 25);
            Assert.Empty(scheduler.GetQueuedTasks());
        }

        [Fact]
        public void APlainRunTaskIsNamedEvenThoughTheFinishedTaskDroppedItsDelegate()
        {
            var (framework, scheduler) = NewFramework();
            var task = framework.Run(NamedTaskBody);

            scheduler.RunTimed(out var slowest, out var action, out _);

            Assert.Same(task, slowest);
            Assert.True(task.IsCompletedSuccessfully);
            Assert.Null(FrameworkUpdateAttribution.ActionOf(task));
            Assert.EndsWith(nameof(NamedTaskBody) + " (Dalamud.Test)", FrameworkUpdateAttribution.DescribeTask(task, action));
            Assert.Equal("Task", FrameworkUpdateAttribution.DescribeTask(task, null));
        }

        [Fact]
        public void FrameGapsAreJudgedOnlyWhenBothEndsWereVisibleAndTheWindowIsInFront()
        {
            var monitor = new FrameIntervalMonitor(100, 0);
            var counters = HitchCounters.Read();
            var inFront = true;
            Func<bool> inFrontNow = () => inFront;
            var step = Ticks(150);

            monitor.Frame(step, counters, false, 0, 0, inFrontNow);
            monitor.Frame(step * 2, counters, true, 0, 0, inFrontNow);
            Assert.Equal(0, monitor.LinesWritten);

            monitor.Frame(step * 3, counters, true, 0, 0, inFrontNow);
            Assert.Equal(1, monitor.LinesWritten);

            monitor.Frame(step * 4, counters, false, 0, 0, inFrontNow);
            monitor.Frame(step * 5, counters, true, 0, 0, inFrontNow);
            Assert.Equal(1, monitor.LinesWritten);

            inFront = false;
            monitor.Frame(step * 6, counters, true, 0, 0, inFrontNow);
            Assert.Equal(1, monitor.LinesWritten);

            inFront = true;
            monitor.Frame((step * 6) + Ticks(100), counters, true, 0, 0, inFrontNow);
            Assert.Equal(1, monitor.LinesWritten);
            monitor.Frame((step * 6) + Ticks(201), counters, true, 0, 0, inFrontNow);
            Assert.Equal(2, monitor.LinesWritten);
        }

        [Fact]
        public void ASteadyFrameAllocatesNothing()
        {
            var monitor = new FrameIntervalMonitor(100, Stopwatch.Frequency);
            var attribution = new FrameworkUpdateAttribution();
            var detector = new HitchDetector("AllocTest", 1_000_000);
            var distribution = new GcPauseDistribution();
            Func<bool> inFrontNow = () => true;
            var tick = Ticks(16);
            Frame(0);

            var before = AllocatedBytes.Start();
            for (var i = 1; i <= 1000; i++)
                Frame(i);
            Assert.Equal(0, AllocatedBytes.Since(before));

            void Frame(int i)
            {
                monitor.Frame(1 + (i * tick), HitchCounters.Read(), true, 0, 0, inFrontNow);
                detector.Start();
                attribution.Begin();
                attribution.AddPlugin("A", i);
                attribution.AddPlugin("B", 1000 - i);
                attribution.SetTasks(5, null, null, 0);
                attribution.End();
                detector.Stop();
                distribution.Add(GameScreen.InGame, TimeSpan.FromMilliseconds(i % 120), 1);
                PerfFrameState.Publish(GameScreen.InGame);
            }
        }

        [Fact]
        public void StopWithoutStartLogsNothingAndMeasuresNothing()
        {
            var detector = new HitchDetector("NeverStarted", 0);
            detector.Stop();
            Assert.Equal(0, detector.LastElapsedTicks);
        }

        private static void NamedTaskBody()
        {
        }

        private static long Ticks(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);

        private static (Framework Framework, ThreadBoundTaskScheduler Scheduler) NewFramework()
        {
            var framework = (Framework)RuntimeHelpers.GetUninitializedObject(typeof(Framework));
            var destroy = new CancellationTokenSource();
            var scheduler = new ThreadBoundTaskScheduler();
            typeof(Framework).GetField("frameworkDestroy", Any)!.SetValue(framework, destroy);
            typeof(Framework).GetField("frameworkThreadTaskScheduler", Any)!.SetValue(framework, scheduler);
            typeof(Framework).GetField("<FrameworkThreadTaskFactory>k__BackingField", Any)!.SetValue(
                framework,
                new TaskFactory(destroy.Token, TaskCreationOptions.None, TaskContinuationOptions.None, scheduler));
            return (framework, scheduler);
        }
    }
}
