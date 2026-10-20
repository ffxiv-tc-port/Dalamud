using System;
using System.Linq;

using Dalamud.Interface.Internal.Asserts;

using Xunit;

namespace Dalamud.Test.Interface.Asserts
{
    // Pure counting and formatting: the captures are built by hand, nothing touches a native ImGui context.
    public class ImGuiStackReportTests
    {
        private const nint Context = 0x1000_0000;
        private const nint OtherContext = 0x1800_0000;
        private const nint MainWindow = 0x2000_0000;
        private const nint OtherWindow = 0x3000_0000;

        private readonly ImGuiStackReport report = new(TimeSpan.FromHours(1));

        [Fact]
        public void ALeakedTabBarIsCountedForThatPluginAndThatStackOnly()
        {
            var leaky = this.report.CreateMeter("Leaky");
            var clean = this.report.CreateMeter("Clean");
            for (var i = 0; i < 3; i++)
            {
                leaky.Record(At((ImGuiStackDepths.TabBar, i)), At((ImGuiStackDepths.TabBar, i + 1)));
                clean.Record(At((ImGuiStackDepths.TabBar, i + 1)), At((ImGuiStackDepths.TabBar, i + 1)));
            }

            var window = this.report.CloseWindow(Now());

            Assert.Equal(6, window.Draws);
            Assert.Equal(2, window.PluginsDrawn);
            var plugin = Assert.Single(window.Plugins);
            Assert.Equal("Leaky", plugin.Plugin);
            Assert.Equal(3, plugin.Draws);
            Assert.Equal("TabBar +3 in 3 draws", ImGuiStackReport.Describe(plugin));
        }

        [Fact]
        public void DrawsThatLeaveEveryStackAsFoundReportNothingChanged()
        {
            var meter = this.report.CreateMeter("Clean");
            var depths = At((ImGuiStackDepths.Color, 2), (ImGuiStackDepths.Font, 1), (ImGuiStackDepths.Window, 1));
            for (var i = 0; i < 5; i++)
                meter.Record(depths, depths);

            var window = this.report.CloseWindow(Now());

            Assert.Empty(window.Plugins);
            Assert.Equal(5, window.Draws);
            Assert.Equal(1, window.PluginsDrawn);
        }

        [Fact]
        public void DeeperAndShallowerAreCountedApart()
        {
            var meter = this.report.CreateMeter("Both");
            meter.Record(At((ImGuiStackDepths.TabBar, 0)), At((ImGuiStackDepths.TabBar, 2)));
            meter.Record(At((ImGuiStackDepths.TabBar, 2)), At((ImGuiStackDepths.TabBar, 1)));
            meter.Record(At((ImGuiStackDepths.Disabled, 1)), At((ImGuiStackDepths.Disabled, 0)));

            var plugin = Assert.Single(this.report.CloseWindow(Now()).Plugins);

            Assert.Equal("TabBar +2/-1 in 2 draws, Disabled -1 in 1 draw", ImGuiStackReport.Describe(plugin));
        }

        [Fact]
        public void EveryStackIsComparedAndNamedInReportOrder()
        {
            var meter = this.report.CreateMeter("All");
            var before = new int[ImGuiStackDepths.Count];
            var after = Enumerable.Range(1, ImGuiStackDepths.Count).ToArray();
            meter.Record(new ImGuiStackDepths(Context, MainWindow, before), new ImGuiStackDepths(Context, MainWindow, after));

            var plugin = Assert.Single(this.report.CloseWindow(Now()).Plugins);

            Assert.Equal(ImGuiStackDepths.Count, ImGuiStackDepths.Names.Length);
            Assert.Equal(ImGuiStackDepths.Names.ToArray(), plugin.Stacks.Select(x => x.Name));
            Assert.Equal(after.Select(x => (long)x), plugin.Stacks.Select(x => x.Added));
            Assert.Equal("Window", ImGuiStackDepths.Names[ImGuiStackDepths.Window]);
            Assert.Equal("Id", ImGuiStackDepths.Names[ImGuiStackDepths.Id]);
            Assert.Equal("TabBar", ImGuiStackDepths.Names[ImGuiStackDepths.TabBar]);
            Assert.Equal("Font", ImGuiStackDepths.Names[ImGuiStackDepths.Font]);
        }

        [Fact]
        public void TheIdStackIsOnlyComparedWithinOneWindow()
        {
            var meter = this.report.CreateMeter("Windows");

            // Left a window open: the current window after the draw is another one, its ID stack is not comparable.
            meter.Record(At((ImGuiStackDepths.Window, 1), (ImGuiStackDepths.Id, 2)), At(OtherWindow, (ImGuiStackDepths.Window, 2), (ImGuiStackDepths.Id, 5)));
            meter.Record(At((ImGuiStackDepths.Id, 2)), At((ImGuiStackDepths.Id, 1)));
            meter.Record(At(0, (ImGuiStackDepths.Id, 2)), At(0, (ImGuiStackDepths.Id, 1)));

            var plugin = Assert.Single(this.report.CloseWindow(Now()).Plugins);

            Assert.Equal("Window +1 in 1 draw, Id -1 in 1 draw", ImGuiStackReport.Describe(plugin));
        }

        [Fact]
        public void DrawsEndingInAnotherContextOrStartingInNoneAreNotCompared()
        {
            var meter = this.report.CreateMeter("Contexts");
            meter.Record(At((ImGuiStackDepths.TabBar, 0)), new ImGuiStackDepths(OtherContext, MainWindow, Enumerable.Repeat(9, ImGuiStackDepths.Count).ToArray()));
            meter.Record(default, At((ImGuiStackDepths.TabBar, 4)));

            var plugin = Assert.Single(this.report.CloseWindow(Now()).Plugins);

            Assert.Empty(plugin.Stacks);
            Assert.Equal(2, plugin.Uncompared);
            Assert.Equal("another ImGui context after 2 draws (not compared)", ImGuiStackReport.Describe(plugin));
        }

        [Fact]
        public void AReloadedPluginsMetersAddUpAndARetiredMeterIsReadOnceThenDropped()
        {
            var old = this.report.CreateMeter("Reloaded");
            old.Record(At((ImGuiStackDepths.TabBar, 0)), At((ImGuiStackDepths.TabBar, 1)));
            old.Retire();
            var current = this.report.CreateMeter("Reloaded");
            current.Record(At((ImGuiStackDepths.TabBar, 1)), At((ImGuiStackDepths.TabBar, 2)));
            current.Record(At((ImGuiStackDepths.TabBar, 2)), At((ImGuiStackDepths.TabBar, 3)));

            var first = Assert.Single(this.report.CloseWindow(Now()).Plugins);
            Assert.Equal("TabBar +3 in 3 draws", ImGuiStackReport.Describe(first));
            Assert.Equal(3, first.Draws);

            // Whatever the retired meter counts after its last read is no longer reported.
            old.Record(At((ImGuiStackDepths.Font, 0)), At((ImGuiStackDepths.Font, 1)));
            current.Record(At((ImGuiStackDepths.TabBar, 3)), At((ImGuiStackDepths.TabBar, 4)));
            var second = Assert.Single(this.report.CloseWindow(Now()).Plugins);
            Assert.Equal("TabBar +1 in 1 draw", ImGuiStackReport.Describe(second));
        }

        [Fact]
        public void EachWindowReportsOnlyItsOwnDraws()
        {
            var meter = this.report.CreateMeter("Once");
            meter.Record(At((ImGuiStackDepths.Clipper, 0)), At((ImGuiStackDepths.Clipper, 1)));
            Assert.Single(this.report.CloseWindow(Now()).Plugins);

            var quiet = this.report.CloseWindow(Now());

            Assert.Empty(quiet.Plugins);
            Assert.Equal(0, quiet.Draws);
            Assert.Equal(0, quiet.PluginsDrawn);
        }

        [Fact]
        public void PluginsAreListedMostChangesFirst()
        {
            var small = this.report.CreateMeter("Small");
            var big = this.report.CreateMeter("Big");
            small.Record(At((ImGuiStackDepths.Table, 0)), At((ImGuiStackDepths.Table, 1)));
            big.Record(At((ImGuiStackDepths.TabBar, 0)), At((ImGuiStackDepths.TabBar, 3)));
            big.Record(At((ImGuiStackDepths.Color, 2)), At((ImGuiStackDepths.Color, 0)));

            var window = this.report.CloseWindow(Now());

            Assert.Equal(new[] { "Big", "Small" }, window.Plugins.Select(x => x.Plugin));
            Assert.Equal("TabBar +3 in 1 draw, Color -2 in 1 draw", ImGuiStackReport.Describe(window.Plugins[0]));
        }

        [Fact]
        public void RecordingADrawAllocatesNothing()
        {
            var meter = this.report.CreateMeter("Alloc");
            var before = At((ImGuiStackDepths.TabBar, 1), (ImGuiStackDepths.Id, 2));
            var after = At((ImGuiStackDepths.TabBar, 2), (ImGuiStackDepths.Id, 2));
            for (var i = 0; i < 100; i++)
                meter.Record(before, after);

            var start = AllocatedBytes.Start();
            for (var i = 0; i < 10_000; i++)
                meter.Record(before, after);

            Assert.Equal(0, AllocatedBytes.Since(start));
            Assert.Equal(10_100, Assert.Single(this.report.CloseWindow(Now()).Plugins).Stacks[0].Added);
        }

        private static long Now() => System.Diagnostics.Stopwatch.GetTimestamp();

        private static ImGuiStackDepths At(params (int Index, int Depth)[] depths) => At(MainWindow, depths);

        private static ImGuiStackDepths At(nint window, params (int Index, int Depth)[] depths)
        {
            var d = new int[ImGuiStackDepths.Count];
            foreach (var (index, depth) in depths)
                d[index] = depth;
            return new ImGuiStackDepths(Context, window, d);
        }
    }
}
