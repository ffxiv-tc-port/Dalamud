#nullable enable

using System;
using System.Diagnostics;
using System.Threading;

using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Utility.Internal;

using Xunit;

namespace Dalamud.Test.Utility
{
    // The FRAMEGAP attribution: what a long gap between two framework updates went to besides the update itself.
    public class FrameGapAttributionTests
    {
        private const string BusyFrame =
            "between the two updates: Dalamud's ImGui frames (2) 12.0 ms (plugin drawing 10.0 ms in 3 draws, slowest A 7.0 ms), " +
            "addon event listeners 7.5 ms in 5 calls (slowest P 4.0 ms; longest call P PostSetup SelectString 4.0 ms), " +
            "the rest (the game's own work and anything not named) 128.5 ms";

        private long now;

        [Fact]
        public void DescribeSumsTheOutermostSpansAndNamesTheSlowestPerPlugin()
        {
            var gap = new FrameGapAttribution(() => this.now);
            gap.Reset();
            RecordBusyFrame(gap);

            Assert.Equal(BusyFrame, gap.Describe(Ticks(150), Ticks(2)));
        }

        [Fact]
        public void TheLongGapLineCarriesWhatTheGapWentTo()
        {
            var gap = new FrameGapAttribution(() => this.now);
            var monitor = new FrameIntervalMonitor(100, 0) { GapAttribution = gap };
            var counters = HitchCounters.Read();
            Func<bool> inFrontNow = () => true;

            this.now = Ticks(1000);
            monitor.Frame(this.now, counters, GameScreen.InGame, true, 0, 0, inFrontNow);
            gap.Reset();
            var start = this.now;

            // A short gap writes nothing and describes nothing.
            this.now = start + Ticks(16);
            monitor.Frame(this.now, counters, GameScreen.InGame, true, Ticks(1), Ticks(10), inFrontNow);
            gap.Reset();
            Assert.Equal(0, monitor.LinesWritten);
            Assert.Null(monitor.LastGapText);

            start = this.now;
            RecordBusyFrame(gap);
            this.now = start + Ticks(150);
            monitor.Frame(this.now, counters, GameScreen.InGame, true, Ticks(2), Ticks(140), inFrontNow);

            Assert.Equal(1, monitor.LinesWritten);
            Assert.Equal(BusyFrame, monitor.LastGapText);
        }

        [Fact]
        public void NothingOutsideTheRecordingThreadCounts()
        {
            var gap = new FrameGapAttribution(() => this.now);
            gap.Reset();
            var tally = new FrameGapAttribution.Tally("Other");

            var worker = new Thread(() =>
            {
                var span = gap.Enter();
                this.now += Ticks(5);
                gap.ExitListener(span, tally, AddonEvent.PreDraw, string.Empty);
                var draw = gap.BeginDraw();
                this.now += Ticks(5);
                gap.EndDraw(draw, tally);
                span = gap.Enter();
                this.now += Ticks(5);
                gap.ExitUiFrame(span);
            });
            worker.Start();
            worker.Join();

            Assert.Equal(
                "between the two updates: no Dalamud ImGui frame (no plugin drew), no addon event listener ran, the rest (the game's own work and anything not named) 120.0 ms",
                gap.Describe(Ticks(120), 0));
        }

        [Fact]
        public void ResetStartsANewGapForEveryTally()
        {
            var gap = new FrameGapAttribution(() => this.now);
            var a = new FrameGapAttribution.Tally("A");
            var b = new FrameGapAttribution.Tally("B");
            gap.Reset();
            this.Draw(gap, a, 9);
            gap.Reset();
            this.Draw(gap, a, 1);
            this.Draw(gap, b, 2);

            Assert.Contains("plugin drawing 3.0 ms in 2 draws, slowest B 2.0 ms", gap.Describe(Ticks(120), 0));
        }

        [Fact]
        public void SteadyFramesAllocateNothing()
        {
            var gap = new FrameGapAttribution();
            var monitor = new FrameIntervalMonitor(100, Stopwatch.Frequency) { GapAttribution = gap };
            var a = new FrameGapAttribution.Tally("A");
            var p = new FrameGapAttribution.Tally("P");
            Func<bool> inFrontNow = () => true;
            var tick = Ticks(16);
            Frame(0);

            var before = AllocatedBytes.Start();
            for (var i = 1; i <= 1000; i++)
                Frame(i);
            Assert.Equal(0, AllocatedBytes.Since(before));

            void Frame(int i)
            {
                monitor.Frame(1 + (i * tick), HitchCounters.Read(), GameScreen.InGame, true, 0, 0, inFrontNow);
                gap.Reset();
                gap.ExitUpdate(gap.Enter());
                var ui = gap.Enter();
                gap.EndDraw(gap.BeginDraw(), a);
                gap.ExitUiFrame(ui);
                gap.ExitListener(gap.Enter(), p, AddonEvent.PostSetup, "SelectString");
                gap.ExitListener(gap.Enter(), null, AddonEvent.PreDraw, string.Empty);
            }
        }

        private static long Ticks(double milliseconds) => (long)Math.Round(milliseconds * Stopwatch.Frequency / 1000.0);

        // 2 ImGui frames (12 ms) with draws A 3+4, B 3; listeners P 4, Q 1+1+1, Dalamud 0.5 outside them;
        // a listener inside the framework update and one inside a draw belong to those and are not counted.
        private void RecordBusyFrame(FrameGapAttribution gap)
        {
            var a = new FrameGapAttribution.Tally("A");
            var b = new FrameGapAttribution.Tally("B");
            var p = new FrameGapAttribution.Tally("P");
            var q = new FrameGapAttribution.Tally("Q");

            var update = gap.Enter();
            this.Listener(gap, q, AddonEvent.PostReceiveEvent, "Inventory", 50);
            this.now += Ticks(2);
            gap.ExitUpdate(update);

            var ui = gap.Enter();
            this.Draw(gap, a, 3);
            var draw = gap.BeginDraw();
            this.Listener(gap, p, AddonEvent.PreSetup, "Talk", 2);
            this.now += Ticks(1);
            gap.EndDraw(draw, b);
            this.now += Ticks(1);
            gap.ExitUiFrame(ui);

            this.Listener(gap, p, AddonEvent.PostSetup, "SelectString", 4);
            for (var i = 0; i < 3; i++)
                this.Listener(gap, q, AddonEvent.PreDraw, string.Empty, 1);
            this.Listener(gap, null, AddonEvent.PreFinalize, "Character", 0.5);

            ui = gap.Enter();
            this.Draw(gap, a, 4);
            this.now += Ticks(1);
            gap.ExitUiFrame(ui);
        }

        private void Draw(FrameGapAttribution gap, FrameGapAttribution.Tally tally, double ms)
        {
            var start = gap.BeginDraw();
            this.now += Ticks(ms);
            gap.EndDraw(start, tally);
        }

        private void Listener(FrameGapAttribution gap, FrameGapAttribution.Tally? tally, AddonEvent eventType, string addon, double ms)
        {
            var span = gap.Enter();
            this.now += Ticks(ms);
            gap.ExitListener(span, tally, eventType, addon);
        }
    }
}
