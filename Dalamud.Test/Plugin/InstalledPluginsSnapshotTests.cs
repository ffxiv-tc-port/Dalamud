#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;

using Xunit;

namespace Dalamud.Test.Plugin
{
    // InstalledPlugins hands out one shared read-only copy until the list changes. PluginManager and LocalPlugin are
    // created without their constructors (those need the service container); the list is changed through the same
    // two private methods the manager itself uses. Plugins are compared by a unique tag in their name, never handed
    // to an assertion: printing one for a failure message reads properties that wait for services forever.
    public class InstalledPluginsSnapshotTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly HashSet<string> ListWriters =
        [
            "Add", "AddRange", "Clear", "Insert", "InsertRange", "Remove", "RemoveAll", "RemoveAt", "RemoveRange",
            "Reverse", "Sort", "set_Item",
        ];

        private static int nextTag;

        private readonly PluginManager manager;
        private readonly List<LocalPlugin> model = [];

        public InstalledPluginsSnapshotTests()
        {
            this.manager = (PluginManager)RuntimeHelpers.GetUninitializedObject(typeof(PluginManager));
            SetField(this.manager, "pluginListLock", new object());
            SetField(this.manager, "installedPluginsList", new List<LocalPlugin>());
            SetField(this.manager, "availablePluginsList", new List<RemotePluginManifest>());
            SetField(this.manager, "updatablePluginsList", new List<AvailablePluginUpdate>());
        }

        private List<LocalPlugin> RawList => (List<LocalPlugin>)GetField(this.manager, "installedPluginsList");

        [Fact]
        public void EveryStepOfALoadUnloadRemoveSequenceReadsBackAsTheList()
        {
            this.AssertMatches();

            var a = Plugin("A");
            var b = Plugin("B");
            var c = Plugin("C");
            this.Add(a);
            this.Add(b);
            this.Add(c);

            // Unload and reload change the plugin's state, not the list.
            SetState(b, PluginState.Unloaded);
            this.AssertMatches();
            SetState(b, PluginState.Loaded);
            this.AssertMatches();

            this.Remove(b);
            var b2 = Plugin("B");
            this.Add(b2);

            // Two entries with one internal name, as during an update or with a dev plugin next to the installed one.
            var a2 = Plugin("A");
            this.Add(a2);
            this.Remove(a);
            this.Remove(a);
            this.Add(a);

            foreach (var plugin in this.model.ToArray())
                this.Remove(plugin);

            Assert.Empty(Tags(this.manager.InstalledPlugins));
        }

        [Fact]
        public void RandomAddsAndRemovesAlwaysReadBackAsTheList()
        {
            var random = new Random(20261011);
            var pool = Enumerable.Range(0, 12).Select(i => Plugin("P" + (i % 8))).ToArray();
            for (var step = 0; step < 3000; step++)
            {
                var plugin = pool[random.Next(pool.Length)];
                switch (random.Next(4))
                {
                    case 0 when !this.model.Contains(plugin):
                        this.Add(plugin);
                        break;
                    case 1:
                        this.Remove(plugin);
                        break;
                    default:
                        this.AssertMatches();
                        break;
                }
            }
        }

        [Fact]
        public void TheSameInstanceIsReturnedUntilTheListChanges()
        {
            this.Add(Plugin("A"));
            var first = this.manager.InstalledPlugins;
            Assert.True(ReferenceEquals(first, this.manager.InstalledPlugins));

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                _ = this.manager.InstalledPlugins;
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

            this.Add(Plugin("B"));
            var second = this.manager.InstalledPlugins;
            Assert.False(ReferenceEquals(first, second));
            Assert.True(ReferenceEquals(second, this.manager.InstalledPlugins));
        }

        [Fact]
        public void ACopyTakenEarlierIsNotChangedByLaterWrites()
        {
            var a = Plugin("A");
            var b = Plugin("B");
            this.Add(a);
            this.Add(b);

            var earlier = this.manager.InstalledPlugins;
            using var enumerator = earlier.GetEnumerator();
            Assert.True(enumerator.MoveNext());

            this.Remove(a);
            this.Add(Plugin("C"));

            Assert.True(enumerator.MoveNext());
            Assert.True(ReferenceEquals(b, enumerator.Current));
            Assert.False(enumerator.MoveNext());
            Assert.Equal(Tags([a, b]), Tags(earlier));
        }

        [Fact]
        public void TheCopyIsAReadOnlyNonGenericList()
        {
            var a = Plugin("A");
            this.Add(a);

            // ECommons and others reach this property by reflection and cast it to the non-generic IList.
            var list = typeof(PluginManager).GetProperty("InstalledPlugins")!.GetValue(this.manager) as IList;
            Assert.True(list != null, "not an IList");
            Assert.Equal(1, list.Count);
            Assert.True(ReferenceEquals(a, list[0]));
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Add(a));
            Assert.Throws<NotSupportedException>(() => list.Remove(a));
            Assert.Throws<NotSupportedException>(() => list.Clear());
            Assert.Throws<NotSupportedException>(() => list[0] = a);
            this.AssertMatches();
        }

        [Fact]
        public void RemovePluginIsVisibleOnTheNextRead()
        {
            var a = Plugin("A");
            this.Add(a);
            Assert.Equal(Tags([a]), Tags(this.manager.InstalledPlugins));

            this.manager.RemovePlugin(a);

            Assert.Empty(Tags(this.manager.InstalledPlugins));
        }

        [Fact]
        public async Task ReadersNeverSeeAListBeingChanged()
        {
            var fixedPlugins = Enumerable.Range(0, 20).Select(i => Plugin("F" + i)).ToArray();
            foreach (var plugin in fixedPlugins)
                this.Add(plugin);
            var extra = Plugin("X");

            using var stop = new CancellationTokenSource();
            var writer = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    this.Invoke("AddInstalledPlugin", extra);
                    this.Invoke("RemoveInstalledPlugin", extra);
                }
            });

            try
            {
                for (var i = 0; i < 20000; i++)
                {
                    var count = 0;
                    foreach (var plugin in this.manager.InstalledPlugins)
                    {
                        Assert.True(ReferenceEquals(count < fixedPlugins.Length ? fixedPlugins[count] : extra, plugin));
                        count++;
                    }

                    Assert.InRange(count, fixedPlugins.Length, fixedPlugins.Length + 1);
                }
            }
            finally
            {
                stop.Cancel();
                await writer;
            }
        }

        // A write that bypasses the two methods would leave every reader on the old copy without any error.
        [Fact]
        public void OnlyTheTwoMethodsThatDropTheCopyWriteToTheList()
        {
            var writers = new SortedSet<string>();
            var types = new Queue<Type>([typeof(PluginManager)]);
            var scanned = 0;
            while (types.TryDequeue(out var type))
            {
                foreach (var nested in type.GetNestedTypes(Any))
                    types.Enqueue(nested);

                foreach (var method in type.GetMembers(Any | BindingFlags.DeclaredOnly).OfType<MethodBase>())
                {
                    scanned++;
                    if (CallsAListWriter(method))
                        writers.Add($"{type.Name}.{method.Name}");
                }
            }

            Assert.True(scanned > 100, $"only {scanned} methods scanned");
            Assert.Equal(new[] { "PluginManager.AddInstalledPlugin", "PluginManager.RemoveInstalledPlugin" }, writers);
        }

        private static bool CallsAListWriter(MethodBase method)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il == null)
                return false;

            // Every offset is tried as a call or callvirt; an operand that only looks like one resolves to nothing
            // or to something that is not a list method.
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] is not (0x28 or 0x6F))
                    continue;

                try
                {
                    var callee = method.Module.ResolveMethod(
                        BitConverter.ToInt32(il, i + 1),
                        method.DeclaringType!.IsGenericType ? method.DeclaringType.GetGenericArguments() : null,
                        method.IsGenericMethod ? method.GetGenericArguments() : null);
                    if (callee?.DeclaringType == typeof(List<LocalPlugin>) && ListWriters.Contains(callee.Name))
                        return true;
                }
                catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
                {
                }
            }

            return false;
        }

        private static LocalPlugin Plugin(string internalName)
        {
            var manifest = new LocalPluginManifest
            {
                InternalName = internalName,
                Name = $"{internalName}#{Interlocked.Increment(ref nextTag)}",
                AssemblyVersion = new Version(1, 0, 0, 0),
                WorkingPluginId = Guid.NewGuid(),
            };
            var plugin = (LocalPlugin)RuntimeHelpers.GetUninitializedObject(typeof(LocalPlugin));
            typeof(LocalPlugin).GetField("manifest", Any)!.SetValue(plugin, manifest);
            SetState(plugin, PluginState.Loaded);
            return plugin;
        }

        private static void SetState(LocalPlugin plugin, PluginState state)
            => typeof(LocalPlugin).GetField("<State>k__BackingField", Any)!.SetValue(plugin, state);

        private static string[] Tags(IEnumerable<LocalPlugin> plugins) => plugins.Select(x => x.Name).ToArray();

        private static object GetField(object target, string name)
            => target.GetType().GetField(name, Any)!.GetValue(target)!;

        private static void SetField(object target, string name, object value)
            => target.GetType().GetField(name, Any)!.SetValue(target, value);

        private void Invoke(string method, LocalPlugin plugin)
            => typeof(PluginManager).GetMethod(method, Any)!.Invoke(this.manager, [plugin]);

        private void Add(LocalPlugin plugin)
        {
            this.Invoke("AddInstalledPlugin", plugin);
            this.model.Add(plugin);
            this.AssertMatches();
        }

        private void Remove(LocalPlugin plugin)
        {
            this.Invoke("RemoveInstalledPlugin", plugin);
            this.model.Remove(plugin);
            this.AssertMatches();
        }

        // The old getter returned a fresh copy of the list on every read; the model is that copy.
        private void AssertMatches()
        {
            Assert.Equal(Tags(this.model), Tags(this.RawList));
            Assert.Equal(Tags(this.model), Tags(this.manager.InstalledPlugins));
        }
    }
}
