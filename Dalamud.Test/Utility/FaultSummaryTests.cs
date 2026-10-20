#nullable enable

using System;
using System.Diagnostics;
using System.Linq;

using Dalamud.IoC.Internal;
using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    // The failure counters log only their first occurrence; the summaries are what tell one failure from many.
    public class FaultSummaryTests
    {
        public FaultSummaryTests()
        {
            // The first reported failure looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        [Fact]
        public void DetourSummaryHoldsOnlyWhatChangedSinceTheLastOne()
        {
            var quiet = new DetourFault("Test.Quiet", "測試");
            var busy = new DetourFault("Test.Busy", "測試");
            DetourFault[] both = [quiet, busy];

            Assert.Empty(DetourFault.TakeSummary(both));

            for (var i = 0; i < 3; i++)
                busy.Report(new InvalidOperationException("x"));
            Assert.Equal(new[] { new DetourFault.SummaryLine("Test.Busy", 3, 3) }, DetourFault.TakeSummary(both));
            Assert.Empty(DetourFault.TakeSummary(both));

            busy.Report("missing");
            quiet.Report(new InvalidOperationException("y"));
            busy.Report(new InvalidOperationException("z"));
            Assert.Equal(
                new[] { new DetourFault.SummaryLine("Test.Quiet", 1, 1), new DetourFault.SummaryLine("Test.Busy", 2, 5) },
                DetourFault.TakeSummary(both));
        }

        [Fact]
        public void EveryDetourFaultIsInTheProcessWideSummary()
        {
            var fault = new DetourFault("Test.Registered", "測試");
            fault.Report(new InvalidOperationException("x"));

            // Taking the global summary consumes this fault's count, so its own view is empty afterwards.
            DetourFault.TakeSummary();
            Assert.Empty(DetourFault.TakeSummary([fault]));
        }

        [Fact]
        public void UnresolvedSummaryListsTheBusiestPairsAndCountsTheRest()
        {
            var log = new UnresolvedAddressLog(3, (_, _) => { });
            Assert.Equal(0, log.TakeSummary(10).Lines.Count);

            log.Record("A", ["X.One"]);
            for (var i = 0; i < 4; i++)
                log.Record(null, ["X.Two"]);
            log.Record("B", ["X.One", "X.Three"]);
            log.Record("C", ["X.Four"]);
            log.Record("C", ["X.Four"]);

            var first = log.TakeSummary(2);
            Assert.Equal(2, first.Lines.Count);
            Assert.Equal(new UnresolvedAddressLog.SummaryLine(null, "X.Two", 4, 4), first.Lines[0]);
            Assert.Equal(1, first.Lines[1].Window);
            Assert.Contains(first.Lines[1].Plugin, new[] { "A", "B" });
            Assert.Equal(1, first.Omitted);
            Assert.Equal(3, first.Untracked);

            var second = log.TakeSummary(10);
            Assert.Empty(second.Lines);
            Assert.Equal(0, second.Omitted);
            Assert.Equal(0, second.Untracked);

            log.Record(null, ["X.Two"]);
            Assert.Equal(new[] { new UnresolvedAddressLog.SummaryLine(null, "X.Two", 1, 5) }, log.TakeSummary(10).Lines);
        }

        [Fact]
        public void ReportWritesOnlyAfterTheWindowAndOnUnload()
        {
            var fault = new DetourFault("Test.Windowed", "測試");
            var log = new UnresolvedAddressLog(8, (_, _) => { });
            var start = Stopwatch.GetTimestamp();
            var report = new FaultSummaryReport(start, TimeSpan.FromMinutes(1), log);

            log.Record("P", ["X.One"]);
            report.Tick(start + Stopwatch.Frequency);
            Assert.Single(log.TakeSummary(10).Lines);

            log.Record("P", ["X.One"]);
            report.Tick(start + (61 * Stopwatch.Frequency));
            Assert.Empty(log.TakeSummary(10).Lines);

            log.Record("P", ["X.One"]);
            report.ReportUnfinished(start + (62 * Stopwatch.Frequency));
            Assert.Empty(log.TakeSummary(10).Lines);
            Assert.Equal(3, log.CountOf("P", "X.One"));
            GC.KeepAlive(fault);
        }
    }
}
