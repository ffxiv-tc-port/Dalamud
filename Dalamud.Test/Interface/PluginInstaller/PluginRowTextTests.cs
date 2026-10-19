#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

using CheapLoc;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Interface.Internal.Windows.PluginInstaller;
using Dalamud.Plugin.Internal.Types.Manifest;

using Xunit;
using Xunit.Abstractions;

using Locs = Dalamud.Interface.Internal.Windows.PluginInstaller.PluginInstallerWindow.Locs;
using InstalledMods = Dalamud.Interface.Internal.Windows.PluginInstaller.PluginInstallerWindow.InstalledTitleMods;
using AvailableMods = Dalamud.Interface.Internal.Windows.PluginInstaller.PluginInstallerWindow.AvailableTitleMods;

namespace Dalamud.Test.Interface.PluginInstaller
{
    // What every plugin row of the installer draws each frame. Each text must be what the code it replaced built
    // (copied below as the reference), and a frame that changes nothing must not allocate. The child id is left out of
    // the frame: ImU8String boxes a Guid until its formatting code is tiered up, then allocates nothing.
    [Collection(CheapLocCollection.Name)]
    public class PluginRowTextTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const int PunchlineLength = 200;

        private readonly ITestOutputHelper output;

        public PluginRowTextTests(ITestOutputHelper output)
        {
            this.output = output;
            Loc.SetupWithFallbacks(typeof(PluginRowTextCache).Assembly);
        }

        [Fact]
        public void InstalledTitlesAppendTheModifiersInTheOriginalOrder()
        {
            var count = Enum.GetValues<InstalledMods>().Length - 1;
            Assert.Equal(13, count);
            for (var bits = 0; bits < 1 << count; bits++)
                Assert.Equal(InstalledTitleBefore("Name", (InstalledMods)bits), PluginInstallerWindow.BuildInstalledTitle("Name", (InstalledMods)bits));
        }

        [Fact]
        public void AvailableTitlesAppendTheModifiersInTheOriginalOrder()
        {
            foreach (var testing in new[] { 0, 1, 2, 3 })
            {
                foreach (var incompatible in new[] { false, true })
                {
                    var mods = testing switch
                    {
                        1 => AvailableMods.TestingVersion,
                        2 => AvailableMods.TestingExclusive,
                        3 => AvailableMods.TestingAvailable,
                        _ => AvailableMods.None,
                    };
                    if (incompatible)
                        mods |= AvailableMods.Incompatible;
                    Assert.Equal(AvailableTitleBefore("Name", testing, incompatible), PluginInstallerWindow.BuildAvailableTitle("Name", mods));
                }
            }
        }

        [Fact]
        public void ALabelIsBuiltAgainOnlyWhenWhatItIsMadeOfChanges()
        {
            var cache = new PluginRowTextCache(cacheLocalized: true);
            var row = new object();
            var builds = 0;
            Func<string, int, string> build = (name, mods) =>
            {
                builds++;
                return PluginInstallerWindow.BuildInstalledTitle(name, (InstalledMods)mods);
            };

            var first = cache.GetLabel(row, "Name", (int)InstalledMods.Disabled, build);
            Assert.Equal("Name" + Locs.PluginTitleMod_Disabled, first);
            Assert.Same(first, cache.GetLabel(row, "Name", (int)InstalledMods.Disabled, build));
            Assert.Equal(1, builds);

            Assert.Equal("Name" + Locs.PluginTitleMod_HasUpdate, cache.GetLabel(row, "Name", (int)InstalledMods.HasUpdate, build));
            Assert.Equal("Other" + Locs.PluginTitleMod_HasUpdate, cache.GetLabel(row, "Other", (int)InstalledMods.HasUpdate, build));
            Assert.Equal(3, builds);

            cache.GetLabel(new object(), "Other", (int)InstalledMods.HasUpdate, build);
            Assert.Equal(4, builds);

            cache.InvalidateLocalized();
            cache.GetLabel(row, "Other", (int)InstalledMods.HasUpdate, build);
            Assert.Equal(5, builds);

            var uncached = new PluginRowTextCache(cacheLocalized: false);
            uncached.GetLabel(row, "Name", 0, build);
            uncached.GetLabel(row, "Name", 0, build);
            Assert.Equal(7, builds);
        }

        [Fact]
        public void AnAuthorLineIsWhatLocsBuiltAndFollowsCountAuthorCultureAndLanguage()
        {
            var cache = new PluginRowTextCache(cacheLocalized: true);
            var row = new object();
            var builds = 0;
            Func<string, long, string> build = (author, count) =>
            {
                builds++;
                return AuthorLineBefore(author, count);
            };

            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
                var line = cache.GetAuthorLine(row, "Someone", 1234567, build);
                Assert.Equal(" by Someone (1,234,567 downloads)", line);
                Assert.Same(line, cache.GetAuthorLine(row, "Someone", 1234567, build));
                Assert.Equal(" by Someone", cache.GetAuthorLine(row, "Someone", 0, build));
                Assert.Equal(" by Other", cache.GetAuthorLine(row, "Other", 0, build));
                Assert.Equal(3, builds);

                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                Assert.Equal(" by Other (1.000 downloads)", cache.GetAuthorLine(row, "Other", 1000, build));
                Assert.Equal(4, builds);
                cache.GetAuthorLine(row, "Other", 1000, build);
                Assert.Equal(4, builds);

                cache.InvalidateLocalized();
                cache.GetAuthorLine(row, "Other", 1000, build);
                Assert.Equal(5, builds);
            }
            finally
            {
                CultureInfo.CurrentCulture = culture;
            }
        }

        [Fact]
        public void TheFirstLineIsWhatSplitGave()
        {
            var random = new Random(5);
            var parts = new[] { "a", "bc", "\r", "\n", "\r\n", "\n\r", " ", "中文", "\ud83d\ude00", new string('x', 150), new string('y', PunchlineLength) };
            var cases = new List<string> { string.Empty, "\r", "\n", "x", new string('z', PunchlineLength - 1), new string('z', PunchlineLength), new string('z', PunchlineLength + 1) };
            for (var i = 0; i < 3000; i++)
                cases.Add(string.Concat(Enumerable.Range(0, random.Next(1, 8)).Select(_ => parts[random.Next(parts.Length)])));

            var cut = 0;
            foreach (var description in cases)
            {
                var expected = FirstLineBefore(description);
                Assert.Equal(expected, PluginRowTextCache.FirstLine(description, PunchlineLength).ToString());
                cut += expected.Length == PunchlineLength ? 1 : 0;
            }

            Assert.True(cut > 0, "No case reached the length limit.");
        }

        [Fact]
        public void TheChildIdIsTheSameTextWithoutAString()
        {
            var id = Guid.NewGuid();
            foreach (var label in new[] { "Name", "名前 (disabled)", string.Empty })
            {
                Guid? withPlugin = id;
                Guid? withoutPlugin = null;
                Assert.Equal(
                    Encoding.UTF8.GetBytes($"plugin_child_{label}_{withPlugin}_Internal"),
                    Bytes($"plugin_child_{label}_{id}_Internal"));
                Assert.Equal(
                    Encoding.UTF8.GetBytes($"plugin_child_{label}_{withoutPlugin}_Internal"),
                    Bytes($"plugin_child_{label}__Internal"));
            }
        }

        [Fact]
        public void CommandsAreListedInTheOriginalOrderAndFollowChanges()
        {
            var manager = NewCommandManager();
            var random = new Random(9);
            var list = new PluginCommandList();
            var infos = new List<CommandInfo>();

            // Soft hyphens are ignored when comparing: these keys tie, and the tie has to keep the table order.
            Assert.Equal(0, Comparer<string>.Default.Compare("/a1", "/a1\u00AD"));
            var ties = 0;
            for (var round = 0; round < 600; round++)
            {
                switch (random.Next(6))
                {
                    case 0 or 1 or 2:
                        var command = "/" + "abAB"[random.Next(4)] + random.Next(10) + new string('\u00AD', random.Next(3));
                        var info = new CommandInfo((_, _) => { }) { DisplayOrder = random.Next(0, 2), ShowInHelp = random.Next(6) != 0, HelpMessage = "h" + round };
                        if (manager.AddHandler(command, info, random.Next(5) == 0 ? "Other" : "Plugin"))
                            infos.Add(info);
                        break;
                    case 3 when manager.Commands.Count > 0:
                        manager.RemoveHandler(manager.Commands.Keys.ElementAt(random.Next(manager.Commands.Count)));
                        break;
                    default:
                        if (infos.Count > 0)
                        {
                            var changed = infos[random.Next(infos.Count)];
                            changed.DisplayOrder = random.Next(-1, 3);
                            changed.ShowInHelp = !changed.ShowInHelp;
                        }

                        break;
                }

                var expected = CommandsBefore(manager, "Plugin").Select(c => (c.Key, c.Value)).ToList();
                var actual = list.Get(manager, "Plugin").Where(e => e.Info.ShowInHelp).Select(e => (e.Command, e.Info)).ToList();
                Assert.Equal(expected, actual);
                for (var i = 1; i < expected.Count; i++)
                    ties += expected[i - 1].Value.DisplayOrder == expected[i].Value.DisplayOrder && Comparer<string>.Default.Compare(expected[i - 1].Key, expected[i].Key) == 0 ? 1 : 0;
            }

            Assert.True(ties > 0, "No tie was ever listed.");
        }

        [Fact]
        public void AFrameOfRowsThatChangeNothingDoesNotAllocate()
        {
            var cache = new PluginRowTextCache(cacheLocalized: true);
            var rows = Enumerable.Range(0, 60).Select(i => (Row: new object(), Name: "Plugin " + i, Author: "Author " + (i % 7), Count: (long)(i * 1000), Description: "Line one " + i + "\nline two")).ToArray();
            var manager = NewCommandManager();
            for (var i = 0; i < 12; i++)
                manager.AddHandler("/cmd" + i, new CommandInfo((_, _) => { }) { DisplayOrder = i % 3 }, i % 2 == 0 ? "Plugin" : "Other");
            var commands = new PluginCommandList();
            long sink = 0;

            void Frame()
            {
                foreach (var row in rows)
                {
                    var label = cache.GetLabel(row.Row, row.Name, (int)InstalledMods.Disabled, static (n, m) => PluginInstallerWindow.BuildInstalledTitle(n, (InstalledMods)m));
                    sink += label.Length;
                    sink += cache.GetAuthorLine(row.Row, row.Author, row.Count, static (a, c) => AuthorLineBefore(a, c)).Length;
                    sink += PluginRowTextCache.FirstLine(row.Description, PunchlineLength).Length;
                }

                sink += commands.Get(manager, "Plugin").Count;
            }

            for (var i = 0; i < 200; i++)
                Frame();

            var before = AllocatedBytes.Start();
            for (var i = 0; i < 2000; i++)
                Frame();
            var allocated = AllocatedBytes.Since(before);
            this.output.WriteLine($"AFTER: {allocated} B over 2000 frames of 60 rows and one plugin's commands");

            Assert.True(sink > 0);
            Assert.Equal(0, allocated);
        }

        // BEFORE: what one collapsed row built every frame, and one expanded row's command list. Printed for the
        // record; the assertion only checks that each did allocate, so the zero above is not a blind measurement.
        [Fact]
        public void TheReplacedExpressionsAllocated()
        {
            var manager = NewCommandManager();
            for (var i = 0; i < 12; i++)
                manager.AddHandler("/cmd" + i, new CommandInfo((_, _) => { }) { DisplayOrder = i % 3 }, i % 2 == 0 ? "Plugin" : "Other");
            Guid? id = Guid.NewGuid();
            var description = "A description without a punchline, whose first line is shown.\nAnd a second line.";
            long sink = 0;

            var costs = new (string Name, Action Body)[]
            {
                ("title: name + one modifier", () => sink += InstalledTitleBefore("Plugin", InstalledMods.Disabled).Length),
                ("child id string", () => sink += $"plugin_child_Plugin_{id}_Internal".Length),
                ("author line with downloads", () => sink += AuthorLineBefore("Author", 123456).Length),
                ("description first line", () => sink += FirstLineBefore(description).Length),
                ("command list of an expanded row (6 of 12)", () => sink += CommandsBefore(manager, "Plugin").Count()),
            };

            foreach (var (name, body) in costs)
            {
                for (var i = 0; i < 200; i++)
                    body();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 1000; i++)
                    body();
                var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;
                this.output.WriteLine($"BEFORE: {perCall,8:0.#} B  {name}");
                Assert.True(perCall > 0, name);
            }

            Assert.True(sink > 0);
        }

        // The installed row's title as DrawInstalledPlugin built it.
        private static string InstalledTitleBefore(string name, InstalledMods mods)
        {
            var label = name;
            if ((mods & InstalledMods.TestingVersion) != 0) label += Locs.PluginTitleMod_TestingVersion;
            if ((mods & InstalledMods.TestingAvailable) != 0) label += Locs.PluginTitleMod_TestingAvailable;
            if ((mods & InstalledMods.Installed) != 0) label += Locs.PluginTitleMod_Installed;
            if ((mods & InstalledMods.Disabled) != 0) label += Locs.PluginTitleMod_Disabled;
            if ((mods & InstalledMods.LoadError) != 0) label += Locs.PluginTitleMod_LoadError;
            if ((mods & InstalledMods.UnloadError) != 0) label += Locs.PluginTitleMod_UnloadError;
            if ((mods & InstalledMods.HasUpdate) != 0) label += Locs.PluginTitleMod_HasUpdate;
            if ((mods & InstalledMods.Updated) != 0) label += Locs.PluginTitleMod_Updated;
            if ((mods & InstalledMods.UpdateFailed) != 0) label += Locs.PluginTitleMod_UpdateFailed;
            if ((mods & InstalledMods.OutdatedError) != 0) label += Locs.PluginTitleMod_OutdatedError;
            if ((mods & InstalledMods.BannedError) != 0) label += Locs.PluginTitleMod_BannedError;
            if ((mods & InstalledMods.NoService) != 0) label += Locs.PluginTitleMod_NoService;
            if ((mods & InstalledMods.ScheduledForDeletion) != 0) label += Locs.PluginTitleMod_ScheduledForDeletion;
            return label;
        }

        // The available row's title as DrawAvailablePlugin built it; testing: 1 use testing, 2 exclusive, 3 can use.
        private static string AvailableTitleBefore(string name, int testing, bool incompatible)
        {
            var label = name;
            if (testing == 1)
                label += Locs.PluginTitleMod_TestingVersion;
            else if (testing == 2)
                label += Locs.PluginTitleMod_TestingExclusive;
            else if (testing == 3)
                label += Locs.PluginTitleMod_TestingAvailable;
            if (incompatible)
                label += Locs.PluginTitleMod_Incompatible;
            return label;
        }

        private static string AuthorLineBefore(string author, long count) =>
            count > 0 ? Locs.PluginBody_AuthorWithDownloadCount(author, count) : Locs.PluginBody_AuthorWithDownloadCountUnavailable(author);

        private static string FirstLineBefore(string description)
        {
            var firstLine = description.Split(new[] { '\r', '\n' })[0];
            return firstLine.Length < PunchlineLength ? firstLine : firstLine[..PunchlineLength];
        }

        private static IEnumerable<KeyValuePair<string, IReadOnlyCommandInfo>> CommandsBefore(CommandManager manager, string internalName) =>
            manager.Commands
                   .Where(c => c.Value is { ShowInHelp: true } && manager.GetHandlerAssemblyName(c.Key, c.Value) == internalName)
                   .OrderBy(c => c.Value.DisplayOrder)
                   .ThenBy(c => c.Key);

        private static byte[] Bytes(ImU8String text)
        {
            var bytes = text.Span.ToArray();
            text.Recycle();
            return bytes;
        }

        // Without its constructor, which hooks the game; only the two command tables are needed.
        private static CommandManager NewCommandManager()
        {
            var manager = (CommandManager)RuntimeHelpers.GetUninitializedObject(typeof(CommandManager));
            typeof(CommandManager).GetField("commandMap", Any)!.SetValue(manager, new ConcurrentDictionary<string, IReadOnlyCommandInfo>());
            typeof(CommandManager).GetField("commandAssemblyNameMap", Any)!.SetValue(manager, new ConcurrentDictionary<(string, IReadOnlyCommandInfo), string>());
            return manager;
        }
    }
}
