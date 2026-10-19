using System.Collections.Generic;
using System.Linq;

using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    public class PerfDiagRankingTests
    {
        [Fact]
        public void MainThreadTableRanksByMainThreadBytesNotByTotal()
        {
            var types = new Dictionary<string, AllocationSampler.TypeTally>
            {
                ["PoolHeavy"] = new() { Main = 10, Pool = 9000, Other = 500 },
                ["MainSmall"] = new() { Main = 200 },
                ["MainBig"] = new() { Main = 700, Pool = 1 },
            };

            var picked = AllocationSampler.TopByMainThread(types, 25);

            Assert.Equal(new[] { "MainBig", "MainSmall", "PoolHeavy" }, picked.Select(x => x.Key));
            Assert.Equal(9510, picked[2].Value.Total);
        }

        [Fact]
        public void MainThreadTableLeavesOutTypesNeverSeenOnTheMainThread()
        {
            var types = new Dictionary<string, AllocationSampler.TypeTally>
            {
                ["PoolOnly"] = new() { Pool = 9000 },
                ["OtherOnly"] = new() { Other = 4000 },
                ["Main"] = new() { Main = 1 },
            };

            var picked = AllocationSampler.TopByMainThread(types, 25);

            Assert.Equal(new[] { "Main" }, picked.Select(x => x.Key));
        }

        [Fact]
        public void MainThreadTableStopsAtTheLimit()
        {
            var types = Enumerable.Range(1, 40).ToDictionary(i => $"T{i:00}", i => new AllocationSampler.TypeTally { Main = i });

            var picked = AllocationSampler.TopByMainThread(types, 25);

            Assert.Equal(25, picked.Count);
            Assert.Equal("T40", picked[0].Key);
            Assert.Equal("T16", picked[24].Key);
            Assert.Empty(AllocationSampler.TopByMainThread(types, 0));
        }

        [Fact]
        public void MainThreadTableListsEverythingWhenUnderTheLimit()
        {
            var types = new Dictionary<string, AllocationSampler.TypeTally>
            {
                ["A"] = new() { Main = 3 },
                ["B"] = new() { Main = 5 },
            };

            Assert.Equal(new[] { "B", "A" }, AllocationSampler.TopByMainThread(types, 25).Select(x => x.Key));
            Assert.Empty(AllocationSampler.TopByMainThread(new Dictionary<string, AllocationSampler.TypeTally>(), 25));
        }

        [Fact]
        public void PluginTableRanksByTheSumOfAllCallbacksAndStopsAtTheLimit()
        {
            // Plugin i allocates i bytes in each of two callback columns; the last one only in the fourth column.
            var perPlugin = Enumerable.Range(1, 19).ToDictionary(i => $"P{i:00}", i => new long[] { i, 0, i, 0 });
            perPlugin["ChatOnly"] = [0, 0, 0, 1000];

            var picked = PluginAllocationReport.Rank(perPlugin, 15);

            Assert.Equal(15, picked.Count);
            Assert.Equal("ChatOnly", picked[0].Key);
            Assert.Equal("P19", picked[1].Key);
            Assert.Equal("P06", picked[14].Key);
            Assert.Equal(new[] { "ChatOnly", "P19" }, PluginAllocationReport.Rank(perPlugin, 2).Select(x => x.Key));
        }

        [Fact]
        public void PluginTableListsEverythingWhenUnderTheLimit()
        {
            var perPlugin = new Dictionary<string, long[]>
            {
                ["Small"] = [1, 1, 1, 1],
                ["Big"] = [0, 50, 0, 0],
            };

            Assert.Equal(new[] { "Big", "Small" }, PluginAllocationReport.Rank(perPlugin, 15).Select(x => x.Key));
        }
    }
}
