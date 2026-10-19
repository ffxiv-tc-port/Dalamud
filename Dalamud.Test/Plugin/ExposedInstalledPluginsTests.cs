#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

using Dalamud.Plugin;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;

using Xunit;

namespace Dalamud.Test.Plugin
{
    // What IDalamudPluginInterface.InstalledPlugins hands to plugins: one wrapper per installed plugin, in list order.
    // It used to be InstalledPlugins.Select(p => new ExposedPlugin(p)), which is what every step is compared with.
    // Wrappers are compared as text: printing one for a failure message reads properties that wait for services forever.
    public class ExposedInstalledPluginsTests
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly PluginManager manager;

        public ExposedInstalledPluginsTests()
        {
            this.manager = (PluginManager)RuntimeHelpers.GetUninitializedObject(typeof(PluginManager));
            SetField(this.manager, "pluginListLock", new object());
            SetField(this.manager, "installedPluginsList", new List<LocalPlugin>());
        }

        [Fact]
        public void EveryStepMatchesWrappingTheInstalledPlugins()
        {
            this.AssertMatches();

            var a = Plugin("A", 1);
            var b = Plugin("B", 2);
            this.Change("AddInstalledPlugin", a);
            this.Change("AddInstalledPlugin", b);

            // State is read through the wrapper, so it needs no new copy.
            var before = this.manager.ExposedInstalledPlugins;
            SetState(b, PluginState.Unloaded);
            Assert.True(ReferenceEquals(before, this.manager.ExposedInstalledPlugins));
            Assert.Equal(new[] { true, false }, before.Select(x => x.IsLoaded));
            this.AssertMatches();

            this.Change("RemoveInstalledPlugin", a);
            this.Change("AddInstalledPlugin", Plugin("B", 3));
            this.Change("AddInstalledPlugin", a);
            this.Change("RemoveInstalledPlugin", b);
            Assert.Equal(new[] { "B", "A" }, this.manager.ExposedInstalledPlugins.Select(x => x.InternalName));
            Assert.Equal(new[] { 3, 1 }, this.manager.ExposedInstalledPlugins.Select(x => x.Version.Major));
        }

        [Fact]
        public void TheSameWrappersAreReturnedUntilTheListChanges()
        {
            this.Change("AddInstalledPlugin", Plugin("A", 1));
            var first = this.manager.ExposedInstalledPlugins;

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 1000; i++)
                _ = this.manager.ExposedInstalledPlugins;
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.True(ReferenceEquals(first, this.manager.ExposedInstalledPlugins));

            this.Change("AddInstalledPlugin", Plugin("B", 1));
            Assert.False(ReferenceEquals(first, this.manager.ExposedInstalledPlugins));
            Assert.Equal(1, first.Count);
        }

        [Fact]
        public void PluginsCannotChangeTheSharedCopy()
        {
            this.Change("AddInstalledPlugin", Plugin("A", 1));

            var list = this.manager.ExposedInstalledPlugins as IList<IExposedPlugin>;
            Assert.True(list != null, "not an IList<IExposedPlugin>");
            Assert.True(list.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => list.Clear());
            Assert.Throws<NotSupportedException>(() => list[0] = list[0]);
        }

        private static LocalPlugin Plugin(string internalName, int major)
        {
            var manifest = new LocalPluginManifest
            {
                InternalName = internalName,
                Name = internalName + " name",
                AssemblyVersion = new Version(major, 0, 0, 0),
                WorkingPluginId = Guid.NewGuid(),
            };
            var plugin = (LocalPlugin)RuntimeHelpers.GetUninitializedObject(typeof(LocalPlugin));
            typeof(LocalPlugin).GetField("manifest", Any)!.SetValue(plugin, manifest);
            SetState(plugin, PluginState.Loaded);
            return plugin;
        }

        private static void SetState(LocalPlugin plugin, PluginState state)
            => typeof(LocalPlugin).GetField("<State>k__BackingField", Any)!.SetValue(plugin, state);

        private static void SetField(object target, string name, object value)
            => target.GetType().GetField(name, Any)!.SetValue(target, value);

        private static string Describe(IExposedPlugin plugin)
            => $"{plugin.InternalName}|{plugin.Name}|{plugin.Version}|{plugin.IsLoaded}|{plugin.IsDev}|{plugin.IsTesting}";

        private void Change(string method, LocalPlugin plugin)
        {
            typeof(PluginManager).GetMethod(method, Any)!.Invoke(this.manager, [plugin]);
            this.AssertMatches();
        }

        private void AssertMatches()
        {
            var old = this.manager.InstalledPlugins.Select(p => (IExposedPlugin)new ExposedPlugin(p)).ToArray();
            var now = this.manager.ExposedInstalledPlugins.ToArray();
            Assert.Equal(old.Select(Describe), now.Select(Describe));
            Assert.True(old.Select(x => (object)x.Manifest).SequenceEqual(now.Select(x => (object)x.Manifest), ReferenceEqualityComparer.Instance));
        }
    }
}
