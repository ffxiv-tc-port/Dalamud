#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Configuration.Internal;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.Windows.PluginInstaller;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Profiles;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;

using Xunit;
using Xunit.Abstractions;

namespace Dalamud.Test.Interface.PluginInstaller
{
    // The plugin installer looks these up for every row it draws, every frame. Each loop must answer exactly what the
    // LINQ expression it replaced answered (copied below as the reference), and must not allocate.
    // LocalPlugin, PluginManager, Profile and DalamudConfiguration are created without their constructors
    // (those need the service container). Results are compared by reference, never printed.
    public class PluginInstallerLookupTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly string[] Names = ["A", "B", "C", "a", string.Empty, "Ä"];
        private static readonly string?[] Urls = [null, string.Empty, "https://r1/", "https://r2/", "https://R1/"];
        private static readonly string?[] SortKeys =
            [null, string.Empty, "a", "A", "b", "B", "ä", "https://x/1", "https://x/10", "https://x/2", "-", "a b", "ab"];

        private readonly ITestOutputHelper output;

        public PluginInstallerLookupTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        [Fact]
        public void LookupsAnswerWhatTheLinqTheyReplacedAnswered()
        {
            var random = new Random(1234);
            var checkedCases = 0;
            for (var round = 0; round < 400; round++)
            {
                var installed = Enumerable.Range(0, random.Next(0, 12)).Select(_ => RandomPlugin(random)).ToList();
                var available = Enumerable.Range(0, random.Next(0, 12)).Select(_ => RandomManifest(random)).ToList();
                var updates = Enumerable.Range(0, random.Next(0, 6))
                                        .Select(_ => new AvailablePluginUpdate(installed.Count > 0 && random.Next(4) > 0 ? installed[random.Next(installed.Count)] : RandomPlugin(random), RandomManifest(random), random.Next(2) == 0))
                                        .ToList();
                var optIns = random.Next(8) == 0 ? null : Enumerable.Range(0, random.Next(0, 6)).Select(_ => new PluginTestingOptIn(Pick(random, Names))).ToList();
                var statuses = Enumerable.Range(0, random.Next(0, 6)).Select(_ => new PluginUpdateStatus { InternalName = Pick(random, Names) }).ToList();

                foreach (var plugin in installed.Append(RandomPlugin(random)))
                {
                    SameRef(updates.FirstOrDefault(up => up.InstalledPlugin == plugin), PluginInstallerLookup.FindUpdate(updates, plugin));
                    SameRef(
                        available.FirstOrDefault(rm => rm.InternalName == plugin.Manifest.InternalName && rm.RepoUrl == plugin.Manifest.RepoUrl),
                        PluginInstallerLookup.FindManifest(available, plugin.Manifest.InternalName, plugin.Manifest.RepoUrl));
                    Assert.Equal(
                        available.Any(x => x.InternalName == plugin.InternalName && x.IsAvailableForTesting),
                        PluginInstallerLookup.HasTestingAvailable(available, plugin.InternalName));
                    SameRef(
                        optIns?.FirstOrDefault(x => x.InternalName == plugin.Manifest.InternalName),
                        PluginInstallerLookup.FindTestingOptIn(optIns, plugin.Manifest.InternalName));
                    SameRef(
                        statuses.FirstOrDefault(update => update.InternalName == plugin.Manifest.InternalName),
                        PluginInstallerLookup.FindUpdateStatus(statuses, plugin.Manifest.InternalName));
                    checkedCases++;
                }

                foreach (var availableManifest in available.Append(RandomManifest(random)))
                {
                    SameRef(
                        installed.FirstOrDefault(plugin => plugin.Manifest.InternalName == availableManifest.InternalName &&
                                                           plugin.Manifest.RepoUrl == availableManifest.RepoUrl &&
                                                           !plugin.IsDev),
                        PluginInstallerLookup.FindInstalledNonDev(installed, availableManifest));
                    checkedCases++;
                }
            }

            this.output.WriteLine($"checked {checkedCases} cases");
        }

        [Fact]
        public void TheRandomDataHitsEveryBranch()
        {
            // Calibration: a reference that never matches would make the comparison above vacuous.
            var random = new Random(1234);
            int updateHits = 0, manifestHits = 0, testingHits = 0, optInHits = 0, nonDevHits = 0, devSkips = 0;
            for (var round = 0; round < 400; round++)
            {
                var installed = Enumerable.Range(0, random.Next(0, 12)).Select(_ => RandomPlugin(random)).ToList();
                var available = Enumerable.Range(0, random.Next(0, 12)).Select(_ => RandomManifest(random)).ToList();
                var updates = installed.Select(p => new AvailablePluginUpdate(p, RandomManifest(random), false)).ToList();
                var optIns = installed.Select(p => new PluginTestingOptIn(p.Manifest.InternalName)).ToList();
                foreach (var plugin in installed)
                {
                    updateHits += PluginInstallerLookup.FindUpdate(updates, plugin) != null ? 1 : 0;
                    manifestHits += PluginInstallerLookup.FindManifest(available, plugin.Manifest.InternalName, plugin.Manifest.RepoUrl) != null ? 1 : 0;
                    testingHits += PluginInstallerLookup.HasTestingAvailable(available, plugin.InternalName) ? 1 : 0;
                    optInHits += PluginInstallerLookup.FindTestingOptIn(optIns, plugin.Manifest.InternalName) != null ? 1 : 0;
                }

                foreach (var manifest in available)
                {
                    var found = PluginInstallerLookup.FindInstalledNonDev(installed, manifest);
                    nonDevHits += found != null ? 1 : 0;
                    if (found == null && installed.Any(p => p.IsDev && p.Manifest.InternalName == manifest.InternalName && p.Manifest.RepoUrl == manifest.RepoUrl))
                        devSkips++;
                }
            }

            this.output.WriteLine($"hits: update {updateHits}, manifest {manifestHits}, testing {testingHits}, optIn {optInHits}, nonDev {nonDevHits}, devSkipped {devSkips}");
            Assert.True(updateHits > 0 && manifestHits > 0 && testingHits > 0 && optInHits > 0 && nonDevHits > 0 && devSkips > 0);
        }

        [Fact]
        public void FindCategoryIsFirstIncludingTheThrow()
        {
            var kinds = Enum.GetValues<PluginCategoryManager.CategoryKind>();
            var categories = kinds.Take(kinds.Length - 1)
                                  .SelectMany((k, i) => new[] { Category(k, $"t{i}"), Category(k, $"dup{i}") })
                                  .ToArray();
            foreach (var kind in kinds.Take(kinds.Length - 1))
            {
                var expected = categories.First(x => x.CategoryKind == kind);
                var actual = PluginInstallerLookup.FindCategory(categories, kind);
                Assert.Equal(expected.CategoryKind, actual.CategoryKind);
                Assert.Equal(expected.Tag, actual.Tag);
            }

            var missing = kinds[^1];
            var expectedThrow = Assert.Throws<InvalidOperationException>(() => categories.First(x => x.CategoryKind == missing));
            var actualThrow = Assert.Throws<InvalidOperationException>(() => PluginInstallerLookup.FindCategory(categories, missing));
            Assert.Equal(expectedThrow.Message, actualThrow.Message);
        }

        [Fact]
        public void HasTestingOptInIsTheAnyItReplaced()
        {
            var random = new Random(99);
            var configuration = (DalamudConfiguration)RuntimeHelpers.GetUninitializedObject(typeof(DalamudConfiguration));
            var manager = Manager(configuration);
            for (var round = 0; round < 300; round++)
            {
                configuration.PluginTestingOptIns = Enumerable.Range(0, random.Next(0, 6)).Select(_ => new PluginTestingOptIn(Pick(random, Names))).ToList();
                var manifest = RandomManifest(random);
                Assert.Equal(configuration.PluginTestingOptIns!.Any(x => x.InternalName == manifest.InternalName), manager.HasTestingOptIn(manifest));
            }

            configuration.PluginTestingOptIns = null!;
            var expected = Assert.Throws<ArgumentNullException>(() => configuration.PluginTestingOptIns!.Any(x => x.InternalName == "A"));
            var actual = Assert.Throws<ArgumentNullException>(() => manager.HasTestingOptIn(RandomManifest(random)));
            Assert.Equal(expected.ParamName, actual.ParamName);
        }

        [Fact]
        public void WantsPluginIsTheFirstOrDefaultItReplaced()
        {
            var random = new Random(7);
            var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).Append(Guid.Empty).ToArray();
            var model = new ProfileModelV1();
            var profile = ProfileOf(model);
            var hits = 0;
            for (var round = 0; round < 300; round++)
            {
                model.Plugins = Enumerable.Range(0, random.Next(0, 8))
                                          .Select(_ => new ProfileModelV1.ProfileModelV1Plugin { WorkingPluginId = Pick(random, ids), IsEnabled = random.Next(2) == 0 })
                                          .ToList();
                foreach (var id in ids)
                {
                    var expected = model.Plugins.FirstOrDefault(x => x.WorkingPluginId == id)?.IsEnabled;
                    Assert.Equal(expected, profile.WantsPlugin(id));
                    hits += expected == true ? 1 : 0;
                }
            }

            Assert.True(hits > 0);

            model.Plugins = null!;
            var expectedThrow = Assert.Throws<ArgumentNullException>(() => model.Plugins!.FirstOrDefault(x => x.WorkingPluginId == ids[0]));
            var actualThrow = Assert.Throws<ArgumentNullException>(() => profile.WantsPlugin(ids[0]));
            Assert.Equal(expectedThrow.ParamName, actualThrow.ParamName);
        }

        [Fact]
        public void SorterGivesTheOrderOfOrderBy()
        {
            var random = new Random(4321);
            var sorter = new StableStringKeySorter<Item>();
            var keyOf = (Func<Item, string?>)(x => x.Key);
            for (var round = 0; round < 3000; round++)
            {
                // Lengths around 3 and above 16 are where an unstable List.Sort shows; all-equal keys test stability.
                var length = round % 50;
                var sameKey = round % 7 == 0;
                var list = Enumerable.Range(0, length).Select(i => new Item(sameKey ? "same" : Pick(random, SortKeys), i)).ToList();
                var expected = list.OrderBy(keyOf).ToList();

                sorter.Sort(list, keyOf);

                Assert.Equal(expected.Count, list.Count);
                for (var i = 0; i < list.Count; i++)
                    SameRef(expected[i], list[i]);
            }
        }

        [Fact]
        public void SorterWithTheInstalledListKeyMatchesTheInstalledListOrderBy()
        {
            var random = new Random(55);
            var sorter = new StableStringKeySorter<LocalPlugin>();
            for (var round = 0; round < 1000; round++)
            {
                var list = Enumerable.Range(0, random.Next(0, 40)).Select(_ => RandomPlugin(random)).ToList();
                var expected = list.OrderBy(x => !x.IsDev ? x.Manifest.InstalledFromUrl : string.Empty).ToList();

                sorter.Sort(list, static x => !x.IsDev ? x.Manifest.InstalledFromUrl : string.Empty);

                for (var i = 0; i < list.Count; i++)
                    SameRef(expected[i], list[i]);
            }
        }

        [Fact]
        public void SorterLetsGoOfTheElements()
        {
            var sorter = new StableStringKeySorter<Item>();
            var list = Enumerable.Range(0, 20).Select(i => new Item(Pick(new Random(i), SortKeys), i)).ToList();
            sorter.Sort(list, static x => x.Key);

            foreach (var name in new[] { "keys", "elements" })
            {
                var buffer = (Array)typeof(StableStringKeySorter<Item>).GetField(name, Any)!.GetValue(sorter)!;
                Assert.All(buffer.Cast<object?>(), x => Assert.Null(x));
            }
        }

        [Fact]
        public void LookupsAndSorterDoNotAllocate()
        {
            var random = new Random(3);
            var installed = Enumerable.Range(0, 60).Select(_ => RandomPlugin(random)).ToList();
            var available = Enumerable.Range(0, 80).Select(_ => RandomManifest(random)).ToList();
            var updates = installed.Take(20).Select(p => new AvailablePluginUpdate(p, RandomManifest(random), false)).ToList();
            var optIns = installed.Take(10).Select(p => new PluginTestingOptIn(p.Manifest.InternalName)).ToList();
            var statuses = installed.Take(10).Select(p => new PluginUpdateStatus { InternalName = p.Manifest.InternalName }).ToList();
            var categories = Enum.GetValues<PluginCategoryManager.CategoryKind>().Select(k => Category(k, "t")).ToArray();
            var configuration = (DalamudConfiguration)RuntimeHelpers.GetUninitializedObject(typeof(DalamudConfiguration));
            configuration.PluginTestingOptIns = optIns;
            var manager = Manager(configuration);
            var model = new ProfileModelV1 { Plugins = installed.Select(p => new ProfileModelV1.ProfileModelV1Plugin { WorkingPluginId = p.Manifest.WorkingPluginId, IsEnabled = true }).ToList() };
            var profile = ProfileOf(model);
            var sorter = new StableStringKeySorter<LocalPlugin>();
            var buffer = new List<LocalPlugin>(installed.Count);

            var hits = 0;
            void Frame()
            {
                foreach (var plugin in installed)
                {
                    hits += PluginInstallerLookup.FindUpdate(updates, plugin) != null ? 1 : 0;
                    hits += PluginInstallerLookup.FindManifest(available, plugin.Manifest.InternalName, plugin.Manifest.RepoUrl) != null ? 1 : 0;
                    hits += PluginInstallerLookup.HasTestingAvailable(available, plugin.InternalName) ? 1 : 0;
                    hits += PluginInstallerLookup.FindTestingOptIn(optIns, plugin.Manifest.InternalName) != null ? 1 : 0;
                    hits += PluginInstallerLookup.FindUpdateStatus(statuses, plugin.Manifest.InternalName) != null ? 1 : 0;
                    hits += manager.HasTestingOptIn(plugin.Manifest) ? 1 : 0;
                    hits += profile.WantsPlugin(plugin.Manifest.WorkingPluginId) == true ? 1 : 0;
                }

                foreach (var manifest in available)
                    hits += PluginInstallerLookup.FindInstalledNonDev(installed, manifest) != null ? 1 : 0;
                foreach (var category in categories)
                    hits += (int)PluginInstallerLookup.FindCategory(categories, category.CategoryKind).CategoryKind;

                buffer.AddRange(installed);
                sorter.Sort(buffer, static x => !x.IsDev ? x.Manifest.InstalledFromUrl : string.Empty);
                buffer.Clear();
            }

            for (var i = 0; i < 200; i++)
                Frame();

            // Steady state: a run may still see one-time JIT work (a full-suite run once did), a later one must not.
            var allocated = long.MaxValue;
            for (var attempt = 0; attempt < 5 && allocated != 0; attempt++)
            {
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 2000; i++)
                    Frame();
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                this.output.WriteLine($"attempt {attempt}: allocated {allocated} B over 2000 frames of 60 rows");
            }

            Assert.True(hits > 0);
            Assert.Equal(0, allocated);
        }

        // BEFORE: what the replaced expressions cost, in the closure shapes they had. Printed for the record; the
        // assertion only checks that each did allocate, so the zero above is not a measurement that cannot see.
        [Fact]
        public void TheReplacedExpressionsAllocated()
        {
            var random = new Random(3);
            var installed = Enumerable.Range(0, 60).Select(_ => RandomPlugin(random)).ToList();
            var available = Enumerable.Range(0, 80).Select(_ => RandomManifest(random)).ToList();
            var updates = installed.Take(20).Select(p => new AvailablePluginUpdate(p, RandomManifest(random), false)).ToList();
            var optIns = installed.Take(10).Select(p => new PluginTestingOptIn(p.Manifest.InternalName)).ToList();
            var model = new ProfileModelV1 { Plugins = installed.Select(p => new ProfileModelV1.ProfileModelV1Plugin { WorkingPluginId = p.Manifest.WorkingPluginId, IsEnabled = true }).ToList() };
            var plugin = installed[5];
            var sink = 0;

            // Each in its own method taking the plugin as a parameter, so its closure is made per call as it was.
            var costs = new (string Name, Action Body)[]
            {
                ("updates.FirstOrDefault(up => up.InstalledPlugin == plugin)", () => sink += UpdateBefore(updates, plugin) != null ? 1 : 0),
                ("available.FirstOrDefault(name && repo)", () => sink += ManifestBefore(available, plugin) != null ? 1 : 0),
                ("available.Any(name && testing)", () => sink += TestingAvailableBefore(available, plugin) ? 1 : 0),
                ("optIns?.FirstOrDefault(name)", () => sink += OptInBefore(optIns, plugin) != null ? 1 : 0),
                ("Profile.WantsPlugin body", () => sink += WantsPluginBefore(model, plugin.Manifest.WorkingPluginId) == true ? 1 : 0),
                ("installed list Where/OrderBy/ToList (60)", () => sink += this.InstalledListBefore(installed).Count),
            };

            foreach (var (name, body) in costs)
            {
                for (var i = 0; i < 200; i++)
                    body();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var i = 0; i < 1000; i++)
                    body();
                var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000.0;
                this.output.WriteLine($"{perCall,8:0.#} B  {name}");
                Assert.True(perCall > 0, name);
            }

            Assert.True(sink > 0);
        }

        private static AvailablePluginUpdate? UpdateBefore(List<AvailablePluginUpdate> updates, LocalPlugin plugin)
            => updates.FirstOrDefault(up => up.InstalledPlugin == plugin);

        private static RemotePluginManifest? ManifestBefore(List<RemotePluginManifest> available, LocalPlugin plugin)
            => available.FirstOrDefault(rm => rm.InternalName == plugin.Manifest.InternalName && rm.RepoUrl == plugin.Manifest.RepoUrl);

        private static bool TestingAvailableBefore(List<RemotePluginManifest> available, LocalPlugin plugin)
            => available.Any(x => x.InternalName == plugin.InternalName && x.IsAvailableForTesting);

        private static PluginTestingOptIn? OptInBefore(List<PluginTestingOptIn>? optIns, LocalPlugin plugin)
            => optIns?.FirstOrDefault(x => x.InternalName == plugin.Manifest.InternalName);

        // The filter called an instance method, so its delegate was made per call; reading this.output keeps that shape.
        private List<LocalPlugin> InstalledListBefore(List<LocalPlugin> installed)
            => installed.Where(p => this.output != null && p.Manifest.InternalName != "?").OrderBy(x => !x.IsDev ? x.Manifest.InstalledFromUrl : string.Empty).ToList();

        private static bool? WantsPluginBefore(ProfileModelV1 model, Guid workingPluginId)
        {
            var entry = model.Plugins.FirstOrDefault(x => x.WorkingPluginId == workingPluginId);
            return entry?.IsEnabled;
        }

        // Assert.Same would print a failing LocalPlugin, and its properties wait for services forever.
        private static void SameRef(object? expected, object? actual)
            => Assert.True(ReferenceEquals(expected, actual), "not the same element as the reference");

        private static T Pick<T>(Random random, IReadOnlyList<T> values) => values[random.Next(values.Count)];

        private static PluginCategoryManager.CategoryInfo Category(PluginCategoryManager.CategoryKind kind, string tag)
            => new(kind, tag, () => tag);

        private static PluginManager Manager(DalamudConfiguration configuration)
        {
            var manager = (PluginManager)RuntimeHelpers.GetUninitializedObject(typeof(PluginManager));
            typeof(PluginManager).GetField("configuration", Any)!.SetValue(manager, configuration);
            return manager;
        }

        private static Profile ProfileOf(ProfileModelV1 model)
        {
            var profile = (Profile)RuntimeHelpers.GetUninitializedObject(typeof(Profile));
            typeof(Profile).GetField("modelV1", Any)!.SetValue(profile, model);
            return profile;
        }

        private static RemotePluginManifest RandomManifest(Random random)
        {
            var version = new Version(1, random.Next(3));
            return new RemotePluginManifest
            {
                InternalName = Pick(random, Names),
                RepoUrl = Pick(random, Urls),
                AssemblyVersion = version,
                TestingAssemblyVersion = random.Next(3) switch { 0 => null, 1 => new Version(1, 1), _ => new Version(1, 5) },
                TestingDalamudApiLevel = random.Next(3) == 0 ? PluginManager.DalamudApiLevel - 1 : PluginManager.DalamudApiLevel,
            };
        }

        private static LocalPlugin RandomPlugin(Random random)
        {
            var manifest = new LocalPluginManifest
            {
                InternalName = Pick(random, Names),
                RepoUrl = Pick(random, Urls),
                InstalledFromUrl = Pick(random, SortKeys) ?? string.Empty,
                AssemblyVersion = new Version(1, 0),
                WorkingPluginId = Guid.NewGuid(),
            };
            var type = random.Next(5) == 0 ? typeof(LocalDevPlugin) : typeof(LocalPlugin);
            var plugin = (LocalPlugin)RuntimeHelpers.GetUninitializedObject(type);
            typeof(LocalPlugin).GetField("manifest", Any)!.SetValue(plugin, manifest);
            return plugin;
        }

        private sealed class Item(string? key, int index)
        {
            public string? Key { get; } = key;

            public int Index { get; } = index;
        }
    }
}
