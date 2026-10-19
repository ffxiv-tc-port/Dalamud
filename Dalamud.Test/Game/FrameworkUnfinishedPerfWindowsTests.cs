#nullable enable

using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Game;
using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Game
{
    // On its way out the framework writes the unfinished hourly reports once. This runs inside a native hook, so
    // whatever goes wrong must stay inside. The framework is created without its constructor (that one hooks the
    // game); what the report wrote is read from the window start it moved.
    public sealed class FrameworkUnfinishedPerfWindowsTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo WindowStart = typeof(PluginAllocationReport).GetField("windowStart", Any)!;

        private readonly Framework framework = (Framework)RuntimeHelpers.GetUninitializedObject(typeof(Framework));
        private readonly Action reportUnfinished;
        private readonly long constructedAt = Stopwatch.GetTimestamp() - (600 * Stopwatch.Frequency);

        public FrameworkUnfinishedPerfWindowsTests()
        {
            this.reportUnfinished = typeof(Framework).GetMethod("ReportUnfinishedPerfWindows", Any)!.CreateDelegate<Action>(this.framework);
        }

        [Fact]
        public void TheUnfinishedWindowIsReportedOnceAndANewOneStarts()
        {
            var report = this.GiveReport();

            this.reportUnfinished();
            var afterFirst = (long)WindowStart.GetValue(report)!;
            Assert.True(afterFirst > this.constructedAt);

            this.reportUnfinished();
            Assert.Equal(afterFirst, (long)WindowStart.GetValue(report)!);
        }

        [Fact]
        public void NothingIsReportedOnceTheDiagnosticsHaveFailed()
        {
            var report = this.GiveReport();
            typeof(Framework).GetField("perfDiagnosticsFailed", Any)!.SetValue(this.framework, true);

            this.reportUnfinished();

            Assert.Equal(this.constructedAt, (long)WindowStart.GetValue(report)!);
        }

        [Fact]
        public void AFailureWhileReportingStaysInside()
        {
            // No report object at all: the call inside throws, and nothing may come out.
            this.reportUnfinished();
            this.reportUnfinished();
        }

        [Fact]
        public void TheGameThreadCountIsReportedEvenWhenAnotherReportFails()
        {
            var reads = 0;
            var threads = new GameNativeThreadReport(
                this.constructedAt,
                TimeSpan.FromHours(1),
                "test.exe",
                0,
                0,
                () =>
                {
                    reads++;
                    return new GameNativeThreadReport.Snapshot([], 0, 0);
                });
            typeof(Framework).GetField("gameThreadReport", Any)!.SetValue(this.framework, threads);

            // No allocation report: that one throws.
            this.reportUnfinished();

            Assert.Equal(2, reads);
        }

        private PluginAllocationReport GiveReport()
        {
            var report = new PluginAllocationReport(this.constructedAt);
            typeof(Framework).GetField("allocationReport", Any)!.SetValue(this.framework, report);
            return report;
        }
    }
}
