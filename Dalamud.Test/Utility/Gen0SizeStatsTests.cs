#nullable enable

using System;
using System.Diagnostics;

using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    // The hourly "gen0 size before the gen0 collections seen" figure: the statistic itself, and which collections feed it.
    public class Gen0SizeStatsTests
    {
        private const long MB = 1024 * 1024;

        [Fact]
        public void NothingSeenSaysSoInsteadOfZero()
        {
            var stats = new Gen0SizeStats();

            Assert.Equal(-1, stats.MedianBucket());
            Assert.Equal("none seen, 0 samples", stats.Describe());
        }

        [Fact]
        public void MedianMinAndMaxOfThree()
        {
            var stats = new Gen0SizeStats();
            stats.Add((long)(16.1 * MB));
            stats.Add(48 * MB);
            stats.Add((long)(47.5 * MB));

            Assert.Equal("median 47-48 MB, min 16.1 MB, max 48.0 MB, 3 samples", stats.Describe());
        }

        [Fact]
        public void EvenCountTakesTheLowerMedian()
        {
            var stats = new Gen0SizeStats();
            stats.Add(10 * MB);
            stats.Add(20 * MB);

            Assert.Equal(10, stats.MedianBucket());
        }

        [Fact]
        public void HugeSizesShareTheLastBucketAndKeepTheirExactMax()
        {
            var stats = new Gen0SizeStats();
            stats.Add(300 * MB);
            stats.Add(-5);

            Assert.Equal(0, stats.MedianBucket());
            stats.Add(400 * MB);
            Assert.Equal("median 255 MB or more, min 0.0 MB, max 400.0 MB, 3 samples", stats.Describe());
        }

        [Fact]
        public void ResetForgetsEverything()
        {
            var stats = new Gen0SizeStats();
            stats.Add(48 * MB);
            stats.Reset();
            stats.Add(16 * MB);

            Assert.Equal("median 16-17 MB, min 16.0 MB, max 16.0 MB, 1 samples", stats.Describe());
        }

        [Fact]
        public void AddingAllocatesNothing()
        {
            var stats = new Gen0SizeStats();
            stats.Add(1);

            var before = AllocatedBytes.Start();
            for (var i = 0; i < 10_000; i++)
                stats.Add(i * 37L * 1024);
            Assert.Equal(0, AllocatedBytes.Since(before));
        }

        [Fact]
        public void AGen0CollectionIsSampled()
        {
            var monitor = new GcPauseMonitor(Stopwatch.GetTimestamp());

            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            monitor.Poll(Stopwatch.GetTimestamp());

            if (GC.GetGCMemoryInfo(GCKind.Any).Generation != 0)
                return; // Another thread's higher collection came last; the frame only ever looks at the latest one.

            Assert.Equal(1, monitor.Gen0SizesBefore.Count);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void AHigherCollectionIsNotSampled(int generation)
        {
            var monitor = new GcPauseMonitor(Stopwatch.GetTimestamp());

            GC.Collect(generation, GCCollectionMode.Forced, blocking: true);
            var mine = GC.GetGCMemoryInfo(GCKind.Any);
            monitor.Poll(Stopwatch.GetTimestamp());

            if (mine.Generation != generation || GC.GetGCMemoryInfo(GCKind.Any).Index != mine.Index)
                return; // Another thread's collection got in between; nothing to conclude.

            Assert.Equal(0, monitor.Gen0SizesBefore.Count);
        }
    }
}
