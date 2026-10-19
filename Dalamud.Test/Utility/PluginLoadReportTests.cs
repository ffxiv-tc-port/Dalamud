#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.IoC.Internal;
using Dalamud.Utility.Internal;

using Xunit;

using Outcome = Dalamud.Utility.Internal.PluginLoadReport.Outcome;

namespace Dalamud.Test.Utility
{
    // Batches are process-wide; every test that opens one lives in this class, so they never run in parallel.
    public class PluginLoadReportTests
    {
        // The constructors below allocate whole mebibytes; the slack covers the reflection call and array headers.
        private const long Mebibyte = 1024 * 1024;
        private const long Slack = 256 * 1024;

        [Fact]
        public async Task PluginConstructorIsMeasuredOnTheLongRunningThreadThatRunsIt()
        {
            var cost = new PluginLoadReport.ConstructorCost();

            var created = await new ServiceContainer().CreateAsync(
                typeof(AllocatingPlugin), ObjectInstanceVisibility.ExposedToPlugins, [], null, cost);

            var plugin = Assert.IsType<AllocatingPlugin>(created);
            Assert.True(cost.Ran);
            Assert.InRange(cost.ThreadBytes, AllocatingPlugin.Bytes, AllocatingPlugin.Bytes + Slack);
            Assert.InRange(cost.Ticks, 1, long.MaxValue);
            Assert.Equal(".NET Long Running Task", plugin.ThreadName);
            Assert.NotEqual(Environment.CurrentManagedThreadId, plugin.ThreadId);
        }

        [Fact]
        public async Task ThrowingConstructorIsStillMeasuredAndStillThrows()
        {
            var cost = new PluginLoadReport.ConstructorCost();

            var thrown = await Assert.ThrowsAsync<AggregateException>(() => new ServiceContainer().CreateAsync(
                typeof(ThrowingPlugin), ObjectInstanceVisibility.ExposedToPlugins, [], null, cost));

            Assert.IsType<TargetInvocationException>(thrown.InnerException);
            Assert.True(cost.Ran);
            Assert.InRange(cost.ThreadBytes, ThrowingPlugin.Bytes, ThrowingPlugin.Bytes + Slack);
        }

        [Fact]
        public async Task UnmeasuredCreateStillWorks()
        {
            var created = await new ServiceContainer().CreateAsync(
                typeof(AllocatingPlugin), ObjectInstanceVisibility.ExposedToPlugins, []);

            Assert.IsType<AllocatingPlugin>(created);
        }

        [Fact]
        public async Task BatchSummaryCarriesTheMeasuredLoad()
        {
            var batch = PluginLoadReport.BeginBatch("test-measured");
            var start = PluginLoadReport.Start();
            var cost = new PluginLoadReport.ConstructorCost();
            await new ServiceContainer().CreateAsync(
                typeof(AllocatingPlugin), ObjectInstanceVisibility.ExposedToPlugins, [], null, cost);
            PluginLoadReport.Report("Measured", new Version(1, 2, 3, 4), start, cost, false);

            var line = batch.End();

            Assert.NotNull(line);
            Assert.StartsWith("Plugin load batch \"test-measured\": 1 plugins in ", line);
            Assert.Contains("(0 failed); constructors allocated 8.0 MB on their threads", line);
            Assert.Contains("By constructor allocation: Measured 8.0 MB/", line);
            Assert.DoesNotContain("the other", line);
        }

        [Fact]
        public void LoadLinesSayWhatTheConstructorCostOrThatItFailed()
        {
            var ms = Stopwatch.Frequency / 1000;
            var loaded = new PluginLoadReport.Entry("Big", "1.0.0.1", Outcome.Loaded, 1200 * Mebibyte, 2100 * ms, 2500 * ms, 1300 * Mebibyte);

            Assert.Equal(
                "Plugin load: Big 1.0.0.1 constructor allocated 1200.0 MB on its thread in 2100 ms; whole load 2500 ms, process +1300.0 MB meanwhile (an upper bound: includes plugins loading in parallel and background work)",
                PluginLoadReport.DescribeLoad(loaded));
            Assert.Equal(
                "Plugin load: Big 1.0.0.1 FAILED, constructor allocated 1200.0 MB on its thread in 2100 ms; whole load 2500 ms, process +1300.0 MB meanwhile (an upper bound: includes plugins loading in parallel and background work)",
                PluginLoadReport.DescribeLoad(loaded with { Outcome = Outcome.ConstructorThrew }));
            Assert.Equal(
                "Plugin load: ? ? FAILED before its constructor ran; whole load 0 ms, process +0.0 MB meanwhile (an upper bound: includes plugins loading in parallel and background work)",
                PluginLoadReport.DescribeLoad(new PluginLoadReport.Entry(null, null, Outcome.ConstructorDidNotRun, 0, 0, 0, 0)));
        }

        [Fact]
        public void LinesUseInvariantNumbersWhateverTheCulture()
        {
            var culture = CultureInfo.CurrentCulture;
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            try
            {
                var line = PluginLoadReport.DescribeLoad(
                    new PluginLoadReport.Entry("P", "1", Outcome.Loaded, 3 * Mebibyte / 2, 0, 0, 0));
                Assert.Contains("allocated 1.5 MB", line);
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        [Fact]
        public void RankingIsByConstructorBytesThenByName()
        {
            var entries = new[] { Entry("b", 5), Entry("c", 9), Entry("a", 5), Entry("d", 0) };

            Assert.Equal(new[] { "c", "a", "b", "d" }, PluginLoadReport.Rank(entries).Select(x => x.Name));
        }

        [Fact]
        public void SummaryNamesTheTopAndSumsUpEveryOtherLoad()
        {
            // Plugin i allocates i MiB; 20 plugins, 1 of them failed.
            var entries = Enumerable.Range(1, 20).Select(i => Entry($"P{i:00}", i * Mebibyte)).ToList();
            entries[0] = entries[0] with { Outcome = Outcome.ConstructorDidNotRun };

            var line = PluginLoadReport.DescribeBatch("boot", entries, Stopwatch.Frequency * 3 / 2, 400 * Mebibyte, 3);

            Assert.Equal(
                "Plugin load batch \"boot\": 20 plugins in 1.5 s (1 failed); constructors allocated 210.0 MB on their threads, the process 400.0 MB meanwhile. By constructor allocation: P20 20.0 MB/0 ms, P19 19.0 MB/0 ms, P18 18.0 MB/0 ms; the other 17 plugins 153.0 MB together",
                line);
        }

        [Fact]
        public void SummaryMarksFailedLoadsItNames()
        {
            var entries = new[] { Entry("Ok", 2 * Mebibyte), Entry("Broken", 3 * Mebibyte) with { Outcome = Outcome.ConstructorThrew } };

            var line = PluginLoadReport.DescribeBatch("update", entries, 0, 0, PluginLoadReport.TopCount);

            Assert.EndsWith("By constructor allocation: Broken 3.0 MB/0 ms (failed), Ok 2.0 MB/0 ms", line);
        }

        [Fact]
        public void ReportNeverThrowsAndBatchesEndOnce()
        {
            var batch = PluginLoadReport.BeginBatch("test-safety");
            PluginLoadReport.Report(null!, null, default, null, false);
            PluginLoadReport.Report("Unrun", null, PluginLoadReport.Start(), new PluginLoadReport.ConstructorCost(), true);

            var line = batch.End();
            PluginLoadReport.Report("AfterEnd", null, PluginLoadReport.Start(), null, false);

            Assert.NotNull(line);
            Assert.Contains("2 plugins", line);
            Assert.Contains("(2 failed)", line);
            Assert.DoesNotContain("AfterEnd", line);
            Assert.Null(batch.End());
            Assert.Null(PluginLoadReport.BeginBatch("test-empty").End());
        }

        [Fact]
        public void LoadsReportedFromManyThreadsAllReachTheBatch()
        {
            using var batch = PluginLoadReport.BeginBatch("test-parallel");
            Parallel.For(0, 200, i => PluginLoadReport.Report($"P{i:000}", null, PluginLoadReport.Start(), null, false));

            var line = batch.End();

            Assert.NotNull(line);
            Assert.Contains("200 plugins", line);
            Assert.Contains("the other 185 plugins", line);
        }

        private static PluginLoadReport.Entry Entry(string name, long constructorBytes) =>
            new(name, "1", Outcome.Loaded, constructorBytes, 0, 0, 0);

        public sealed class AllocatingPlugin
        {
            public const long Bytes = 8 * Mebibyte;

            private readonly List<byte[]> kept = [];

            public AllocatingPlugin()
            {
                for (var i = 0; i < 8; i++)
                    this.kept.Add(new byte[Mebibyte]);
                this.ThreadName = Thread.CurrentThread.Name;
                this.ThreadId = Environment.CurrentManagedThreadId;
            }

            public string? ThreadName { get; }

            public int ThreadId { get; }
        }

        public sealed class ThrowingPlugin
        {
            public const long Bytes = 4 * Mebibyte;

            public ThrowingPlugin()
            {
                for (var i = 0; i < 4; i++)
                    GC.KeepAlive(new byte[Mebibyte]);
                throw new InvalidOperationException("test");
            }
        }
    }
}
