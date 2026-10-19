#nullable enable

using System;
using System.Collections.Generic;

using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    // The sampler names other threads by OS thread id, and the OS gives the id of an exited thread to a new one.
    public class AllocationSamplerThreadNamesTests
    {
        private const long Lifetime = 10_000;

        private readonly Dictionary<uint, string> running = new();
        private readonly List<string> lookUps = new();
        private readonly AllocationSampler.ThreadNames names;

        public AllocationSamplerThreadNamesTests()
        {
            this.names = new AllocationSampler.ThreadNames(this.LookUp, Lifetime);
        }

        [Fact]
        public void ANameIsReadOnceWhileItIsTrusted()
        {
            this.running[7] = "Worker";

            var first = this.names.Get(7, 1_000);
            var again = this.names.Get(7, 1_000 + Lifetime - 1);

            Assert.Equal("Worker", first.Name);
            Assert.False(first.IsPool);
            Assert.Same(first, again);
            Assert.Equal(["7:"], this.lookUps);
        }

        [Fact]
        public void AnIdTakenOverByAnotherThreadGetsTheNewNameOnceTheOldOneIsNoLongerTrusted()
        {
            this.running[7] = ".NET Long Running Task";
            Assert.Equal(".NET Long Running Task", this.names.Get(7, 0).Name);

            // The long running thread exits and a pool thread is given its id.
            this.running[7] = ".NET TP Worker";
            Assert.Equal(".NET Long Running Task", this.names.Get(7, Lifetime - 1).Name);

            var taken = this.names.Get(7, Lifetime);
            Assert.Equal(".NET TP Worker", taken.Name);
            Assert.True(taken.IsPool);
            Assert.Same(taken, this.names.Get(7, Lifetime + 1));
            Assert.Equal(["7:", "7:.NET Long Running Task"], this.lookUps);
        }

        [Fact]
        public void AnUnchangedNameIsTrustedAgainFromTheMomentItWasReadAgain()
        {
            this.running[7] = "Worker";
            var first = this.names.Get(7, 0);

            Assert.Same(first, this.names.Get(7, Lifetime + 5));
            Assert.Same(first, this.names.Get(7, (2 * Lifetime) + 4));
            Assert.Equal(2, this.lookUps.Count);

            Assert.Same(first, this.names.Get(7, (2 * Lifetime) + 5));
            Assert.Equal(["7:", "7:Worker", "7:Worker"], this.lookUps);
        }

        [Fact]
        public void AnIdWithoutAThreadIsNotRemembered()
        {
            Assert.Equal("(exited)", this.names.Get(9, 0).Name);
            Assert.False(this.names.Get(9, 1).IsPool);
            Assert.Equal(2, this.lookUps.Count);

            this.running[9] = ".NET TP Worker";
            Assert.True(this.names.Get(9, 2).IsPool);
            Assert.Equal(3, this.lookUps.Count);
        }

        [Fact]
        public void AThreadFoundExitedIsForgotten()
        {
            this.running[7] = "Worker";
            this.names.Get(7, 0);
            this.running.Remove(7);

            Assert.Equal("Worker", this.names.Get(7, Lifetime - 1).Name);
            Assert.Equal("(exited)", this.names.Get(7, Lifetime).Name);

            this.running[7] = "Successor";
            Assert.Equal("Successor", this.names.Get(7, Lifetime + 1).Name);
            Assert.Equal(["7:", "7:Worker", "7:"], this.lookUps);
        }

        [Fact]
        public void ClearMakesEveryNameBeReadAgain()
        {
            this.running[7] = "Worker";
            this.running[8] = ".NET TP Worker";
            this.names.Get(7, 0);
            this.names.Get(8, 0);

            this.names.Clear();
            this.running[7] = "Renamed";

            Assert.Equal("Renamed", this.names.Get(7, 1).Name);
            Assert.True(this.names.Get(8, 1).IsPool);
            Assert.Equal(4, this.lookUps.Count);
        }

        [Fact]
        public void ATrustedNameAndAnUnchangedNameReadAgainAllocateNothing()
        {
            var quiet = new AllocationSampler.ThreadNames(static (_, known) => known ?? "Worker", Lifetime);
            quiet.Get(7, 0);
            for (var i = 0; i < 2000; i++)
                quiet.Get(7, i);

            var before = GC.GetAllocatedBytesForCurrentThread();
            long now = 0;
            for (var i = 0; i < 1000; i++)
            {
                quiet.Get(7, now);
                now += Lifetime;
                quiet.Get(7, now);
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        private string LookUp(uint osThreadId, string? known)
        {
            this.lookUps.Add($"{osThreadId}:{known}");
            if (!this.running.TryGetValue(osThreadId, out var name))
                return AllocationSampler.ThreadNames.Exited;
            return name == known ? known : name;
        }
    }
}
