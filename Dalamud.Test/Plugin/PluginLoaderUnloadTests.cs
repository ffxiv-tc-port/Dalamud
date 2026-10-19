#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

using Dalamud.Plugin.Internal.Loader;
using Dalamud.Plugin.Internal.Types;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files.Excel;
using Lumina.Excel;

using Xunit;

namespace Dalamud.Test.Plugin
{
    // Each test loads a copy of this assembly through the production loader configuration, as if it were a plugin.
    // BeforeUnload is always replaced: the production callback resolves Service<DataManager>, which tests do not provide.
    public class PluginLoaderUnloadTests
    {
        [Fact]
        public void ProductionConfigSetsBeforeUnload()
        {
            var config = new LoaderConfig(typeof(PluginLoaderUnloadTests).Assembly.Location);
            LocalPlugin.SetupLoaderConfig(config);
            Assert.NotNull(config.BeforeUnload);
        }

        [Fact]
        public void BeforeUnloadRunsOnTheLoadedContextBeforeItUnloads()
        {
            var events = new List<string>();
            var weak = LoadAndDispose(
                context => events.Add("before:" + context.IsCollectible + ":" + HasPluginAssembly(context)),
                (assembly, context) => context.Unloading += unloading => events.Add("unloading"));

            Assert.Equal(new[] { "before:True:True", "unloading" }, events);
            Assert.False(IsStillAlive(weak), "The plugin context is still referenced after unloading.");
        }

        [Fact]
        public void ThrowingBeforeUnloadStillUnloads()
        {
            var weak = LoadAndDispose(_ => throw new InvalidOperationException("test"));
            Assert.False(IsStillAlive(weak), "A throwing BeforeUnload kept the plugin context from unloading.");
        }

        [Fact]
        public void ReloadRunsBeforeUnloadForTheReplacedContext()
        {
            var calls = new List<bool>();
            var (replaced, current) = LoadReloadAndDispose(context => calls.Add(HasPluginAssembly(context)));

            // Reload builds a fresh context and loads nothing into it, hence false for the second call.
            Assert.Equal(new[] { true, false }, calls);
            Assert.False(IsStillAlive(replaced), "The context replaced by Reload is still referenced.");
            Assert.False(IsStillAlive(current), "The reloaded context is still referenced after unloading.");
        }

        [RequiresGameDataFact]
        public void OwnSheetTypeKeepsContextAliveUnlessReleased()
        {
            var excel = CreateExcel();

            // Control: proves that this test can tell a pinned context from a collected one.
            var pinned = LoadAndDispose(null, (assembly, _) => ReadOwnSheet(assembly, excel));
            Assert.True(IsStillAlive(pinned), "Lumina no longer pins the context by itself; revisit this test.");

            var released = LoadAndDispose(
                context => LocalPlugin.ReleaseExcelTypeCache(context, excel),
                (assembly, _) => ReadOwnSheet(assembly, excel));
            Assert.False(IsStillAlive(released), "Lumina's type cache kept the unloaded plugin context alive.");

            GC.KeepAlive(excel);
        }

        private static bool HasPluginAssembly(AssemblyLoadContext context) =>
            context.Assemblies.Any(a => a.GetName().Name == typeof(PluginLoaderUnloadTests).Assembly.GetName().Name);

        private static void ReadOwnSheet(Assembly pluginAssembly, ExcelModule excel)
        {
            var side = pluginAssembly.GetType(typeof(UnloadProbePluginSide).FullName!)!;
            Assert.True((bool)side.GetMethod(nameof(UnloadProbePluginSide.RowTypeIsCollectible))!.Invoke(null, null)!);
            Assert.True((bool)side.GetMethod(nameof(UnloadProbePluginSide.ReadSheet))!.Invoke(null, [excel])!);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static PluginLoader CreateLoader(Action<AssemblyLoadContext>? beforeUnload) =>
            PluginLoader.CreateFromAssemblyFile(
                typeof(PluginLoaderUnloadTests).Assembly.Location,
                config =>
                {
                    LocalPlugin.SetupLoaderConfig(config);
                    config.BeforeUnload = beforeUnload;
                });

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference LoadAndDispose(
            Action<AssemblyLoadContext>? beforeUnload,
            Action<Assembly, AssemblyLoadContext>? whileLoaded = null)
        {
            var loader = CreateLoader(beforeUnload);
            var assembly = loader.LoadDefaultAssembly();
            whileLoaded?.Invoke(assembly, loader.LoadContext);
            var weak = new WeakReference(loader.LoadContext);
            loader.Dispose();
            return weak;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference Replaced, WeakReference Current) LoadReloadAndDispose(Action<AssemblyLoadContext> beforeUnload)
        {
            var loader = CreateLoader(beforeUnload);
            loader.LoadDefaultAssembly();
            var replaced = new WeakReference(loader.LoadContext);
            loader.Reload();
            var current = new WeakReference(loader.LoadContext);
            loader.Dispose();
            return (replaced, current);
        }

        private static bool IsStillAlive(WeakReference weak)
        {
            for (var i = 0; weak.IsAlive && i < 30; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            return weak.IsAlive;
        }

        // Single-language clients do not ship Lumina's default English.
        private static ExcelModule CreateExcel()
        {
            var gameData = new GameData(RequiresGameDataFact.SqpackPath, new LuminaOptions { PanicOnSheetChecksumMismatch = false });
            var languages = gameData.GetFile<ExcelHeaderFile>("exd/Addon.exh")!.Languages;
            if (!languages.Contains(gameData.Options.DefaultExcelLanguage))
                gameData.Options.DefaultExcelLanguage = languages.First(l => l != Language.None);
            return gameData.Excel;
        }
    }

    public sealed class RequiresGameDataFact : FactAttribute
    {
        // Same variable as Lumina's tests: the sqpack directory of any client.
        public static readonly string SqpackPath = Environment.GetEnvironmentVariable("LUMINA_TEST_SQPACK") ?? string.Empty;

        public RequiresGameDataFact()
        {
            if (!Directory.Exists(SqpackPath))
                this.Skip = "Set LUMINA_TEST_SQPACK to a game sqpack directory to run this test.";
        }
    }

    [Sheet("Addon")]
    public readonly struct UnloadProbeAddon(ExcelPage page, uint offset, uint row) : IExcelRow<UnloadProbeAddon>
    {
        public ExcelPage ExcelPage => page;

        public uint RowOffset => offset;

        public uint RowId => row;

        static UnloadProbeAddon IExcelRow<UnloadProbeAddon>.Create(ExcelPage page, uint offset, uint row) => new(page, offset, row);
    }

    // Runs inside the collectible copy of this assembly.
    public static class UnloadProbePluginSide
    {
        public static bool RowTypeIsCollectible() => typeof(UnloadProbeAddon).IsCollectible;

        public static bool ReadSheet(ExcelModule excel) => excel.GetSheet<UnloadProbeAddon>().Count > 0;
    }
}
