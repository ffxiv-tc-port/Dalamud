#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Dalamud.Utility.Internal;

using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

using Xunit;

using Kind = Dalamud.Utility.Internal.AllocationSampler.ThreadKind;

namespace Dalamud.Test.Utility
{
    // Lines are rendered the way the log file renders a message ({Message:lj}), in the invariant culture.
    public class AllocationSamplerReportTests
    {
        private const long Mb = 1024 * 1024;
        private const string Prefix = "Allocation samples over the last 60 min: ";
        private static readonly (int, int, int, int, int) ShippedLimits = (8, 5, 25, 3, 5);

        [Fact]
        public void EveryLineOfAWindowWithAllThreadKinds()
        {
            var lines = Report(MixedWindow(), ShippedLimits);

            Assert.Equal(
                new[]
                {
                    "17.5 MB in 7 samples (the process allocated 20.0 MB); main thread 4.0 MB (23%), thread pool 12.0 MB (69%), other threads 1.0 MB (6%), this sampler 0.5 MB (2.9%); 3 other runtime events",
                    "type #1 PoolA 6.0 MB (35.3%): main thread 0.0 MB, thread pool 6.0 MB, other threads 0.0 MB",
                    "type #2 Mixed 5.0 MB (29.4%): main thread 1.0 MB, thread pool 4.0 MB, other threads 0.0 MB",
                    "type #3 MainType 3.0 MB (17.6%): main thread 3.0 MB, thread pool 0.0 MB, other threads 0.0 MB",
                    "type #4 PoolB 2.0 MB (11.8%): main thread 0.0 MB, thread pool 2.0 MB, other threads 0.0 MB",
                    "type #5 OtherType 1.0 MB (5.9%): main thread 0.0 MB, thread pool 0.0 MB, other threads 1.0 MB",
                    "other thread #1 Worker 1.0 MB (5.7%)",
                    "main thread only #1 MainType 3.0 MB (75.0% of the main thread), 3.0 MB on all threads",
                    "main thread only #2 Mixed 1.0 MB (25.0% of the main thread), 5.0 MB on all threads",
                    "thread pool only #1 PoolA 6.0 MB (50.0% of the thread pool), 6.0 MB on all threads",
                    "thread pool only #2 Mixed 4.0 MB (33.3% of the thread pool), 5.0 MB on all threads",
                    "thread pool only #3 PoolB 2.0 MB (16.7% of the thread pool), 2.0 MB on all threads",
                    "types of other thread rank 1 Worker: #1 OtherType 1.0 MB (100.0% of that thread)",
                }.Select(x => Prefix + x),
                lines);
        }

        [Fact]
        public void PoolLinesStayOutOfTheOlderLineShapes()
        {
            var pool = Report(MixedWindow(), ShippedLimits).Where(x => x.Contains(" thread pool only #", StringComparison.Ordinal)).ToList();

            Assert.Equal(3, pool.Count);
            foreach (var shape in new[] { "type #", "other thread #", "main thread", " MB = " })
                Assert.DoesNotContain(pool, x => x.Contains(shape, StringComparison.Ordinal));
        }

        [Fact]
        public void NoPoolSamplesMeansNoPoolLines()
        {
            var window = new AllocationSampler.Window();
            window.Add(Kind.Main, null, "MainType", 3 * Mb);
            window.Add(Kind.Other, "Worker", "OtherType", Mb);

            var lines = Report(window, ShippedLimits);

            Assert.Equal(6, lines.Count);
            Assert.DoesNotContain(lines, x => x.Contains("thread pool only", StringComparison.Ordinal));
            Assert.Contains(Prefix + "main thread only #1 MainType 3.0 MB (100.0% of the main thread), 3.0 MB on all threads", lines);
        }

        [Fact]
        public void PoolLinesStopAtTheSameLimitAsTheMainThreadLines()
        {
            var window = new AllocationSampler.Window();
            for (var i = 1; i <= 40; i++)
            {
                window.Add(Kind.Pool, null, $"P{i:00}", i * Mb);
                window.Add(Kind.Main, null, $"M{i:00}", i * Mb);
            }

            var lines = Report(window, ShippedLimits);
            var pool = lines.Where(x => x.Contains(" thread pool only #", StringComparison.Ordinal)).ToList();

            Assert.Equal(25, lines.Count(x => x.Contains(" main thread only #", StringComparison.Ordinal)));
            Assert.Equal(25, pool.Count);
            for (var r = 1; r <= 25; r++)
                Assert.StartsWith(Prefix + $"thread pool only #{r} P{41 - r:00} {41 - r}.0 MB (", pool[r - 1]);
            Assert.Equal(1 + 8 + 25 + 25, lines.Count);
            Assert.DoesNotContain(Report(window, (8, 5, 0, 3, 5)), x => x.Contains(" only #", StringComparison.Ordinal));
        }

        [Fact]
        public void EmptyWindowIsOneLine()
        {
            var window = new AllocationSampler.Window { OtherEvents = 2 };

            var lines = Report(window, ShippedLimits);

            Assert.Equal(
                new[] { Prefix + "none received (runtime event source enabled, 2 other runtime events); the process allocated 20.0 MB" },
                lines);
        }

        [Fact]
        public void PoolTableRanksByPoolBytesAndLeavesOutTypesWithoutAny()
        {
            var types = new Dictionary<string, AllocationSampler.TypeTally>
            {
                ["MainHeavy"] = new() { Main = 9000, Pool = 10 },
                ["PoolSmall"] = new() { Pool = 200 },
                ["PoolBig"] = new() { Pool = 700, Other = 1 },
                ["OtherOnly"] = new() { Other = 4000 },
            };

            var picked = AllocationSampler.TopByPool(types, 25);

            Assert.Equal(new[] { "PoolBig", "PoolSmall", "MainHeavy" }, picked.Select(x => x.Key));
            Assert.Equal(9010, picked[2].Value.Total);
            Assert.Equal(new[] { "PoolBig" }, AllocationSampler.TopByPool(types, 1).Select(x => x.Key));
            Assert.Empty(AllocationSampler.TopByPool(types, 0));
            Assert.Empty(AllocationSampler.TopByPool(new Dictionary<string, AllocationSampler.TypeTally>(), 25));
        }

        private static AllocationSampler.Window MixedWindow()
        {
            var window = new AllocationSampler.Window { OtherEvents = 3 };
            window.Add(Kind.Main, null, "MainType", 3 * Mb);
            window.Add(Kind.Pool, null, "PoolA", 6 * Mb);
            window.Add(Kind.Pool, null, "PoolB", 2 * Mb);
            window.Add(Kind.Main, null, "Mixed", Mb);
            window.Add(Kind.Pool, null, "Mixed", 4 * Mb);
            window.Add(Kind.Other, "Worker", "OtherType", Mb);
            window.Add(Kind.Self, null, "Ignored", Mb / 2);
            return window;
        }

        internal static List<string> Report(AllocationSampler.Window window, (int, int, int, int, int) limits)
        {
            var sink = new CaptureSink();
            using (var logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger())
                AllocationSampler.Report(window, 60, 20 * Mb, true, limits, (template, values) => logger.Information(template, values));
            return sink.Lines;
        }

        internal sealed class CaptureSink : ILogEventSink
        {
            private readonly MessageTemplateTextFormatter formatter = new("{Message:lj}", CultureInfo.InvariantCulture);

            public List<string> Lines { get; } = [];

            public void Emit(LogEvent logEvent)
            {
                using var writer = new StringWriter();
                this.formatter.Format(logEvent, writer);
                this.Lines.Add(writer.ToString());
            }
        }
    }
}
