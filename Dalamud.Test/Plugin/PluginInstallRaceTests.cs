#nullable enable

using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Plugin;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;

using Xunit;

namespace Dalamud.Test.Plugin
{
    // Two update rounds of the same plugin at once must not delete the files of the instance the other round loaded.
    // PluginManager and LocalPlugin are created without their constructors (those need the service container), and
    // nothing here may reach `new LocalPlugin`, which does.
    public class PluginInstallRaceTests : IDisposable
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Version NewVersion = new(1, 0, 0, 1);

        private readonly DirectoryInfo pluginDir;
        private readonly PluginManager manager;

        public PluginInstallRaceTests()
        {
            this.pluginDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "dalamud_test_" + Guid.NewGuid().ToString("N"), "installedPlugins"));
            this.pluginDir.Create();

            this.manager = (PluginManager)RuntimeHelpers.GetUninitializedObject(typeof(PluginManager));
            SetField(this.manager, "pluginListLock", new object());
            SetField(this.manager, "installedPluginsList", new System.Collections.Generic.List<LocalPlugin>());
            SetField(this.manager, "pluginDirectory", this.pluginDir);
            SetField(this.manager, "pluginOperationGates", new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase));
        }

        private string VersionDir => Path.Combine(this.pluginDir.FullName, "RaceX", NewVersion.ToString());

        private System.Collections.Generic.List<LocalPlugin> Installed
            => (System.Collections.Generic.List<LocalPlugin>)GetField(this.manager, "installedPluginsList");

        private object ListLock => GetField(this.manager, "pluginListLock");

        public void Dispose()
        {
            try
            {
                this.pluginDir.Parent!.Delete(true);
            }
            catch (IOException)
            {
            }
        }

        [Fact]
        public async Task InstallingOverAnInstalledPluginsDirectoryLeavesItAlone()
        {
            var dll = Path.Combine(this.VersionDir, "RaceX.dll");
            Directory.CreateDirectory(this.VersionDir);
            File.WriteAllText(dll, "LOADED");
            lock (this.ListLock)
                this.Installed.Add(this.Plugin(NewVersion, Guid.NewGuid()));

            var error = await this.InstallAsync(new MemoryStream(Zip("SECOND")));

            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal("LOADED", File.ReadAllText(dll));
        }

        [Fact]
        public async Task LosingTheRaceDoesNotCleanUpTheWinnersDirectory()
        {
            var winner = this.Plugin(NewVersion, Guid.NewGuid());
            var zip = new ClaimOnFirstRead(Zip("SAME"), () =>
            {
                lock (this.ListLock)
                    this.Installed.Add(winner);
            });

            var error = await this.InstallAsync(zip);

            Assert.True(zip.Claimed);
            Assert.IsType<InvalidOperationException>(error);
            Assert.True(File.Exists(Path.Combine(this.VersionDir, "RaceX.dll")));
            Assert.False(File.Exists(Path.Combine(this.pluginDir.FullName, "RaceX", ".broken")));
        }

        [Fact]
        public async Task AFailedInstallStillCleansUpItsOwnDirectory()
        {
            Directory.CreateDirectory(this.VersionDir);
            File.WriteAllText(Path.Combine(this.VersionDir, "leftover.txt"), "stale");

            var error = await this.InstallAsync(new MemoryStream("not a zip archive"u8.ToArray()));

            Assert.IsType<InvalidDataException>(error);
            Assert.False(Directory.Exists(this.VersionDir));
            Assert.False(File.Exists(Path.Combine(this.pluginDir.FullName, "RaceX", ".broken")));
        }

        [Fact]
        public async Task SecondOperationOnTheSamePluginWaitsForTheFirst()
        {
            var first = await this.EnterAsync("RaceX");
            Assert.True(this.manager.IsPluginOperationInFlight("racex"));
            Assert.True(this.manager.IsAnyPluginOperationInFlight);

            var second = this.EnterAsync("RACEX");
            var other = this.EnterAsync("Other");
            await Task.Delay(200);
            Assert.False(second.IsCompleted);
            Assert.True(other.IsCompletedSuccessfully);

            first.Dispose();
            (await second.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
            (await other).Dispose();
            first.Dispose();

            Assert.False(this.manager.IsAnyPluginOperationInFlight);
            Assert.False(this.manager.IsPluginOperationInFlight("RaceX"));
        }

        [Fact]
        public async Task QueuedUpdateSeesTheEarlierRoundsResult()
        {
            var workingId = Guid.NewGuid();
            var oldPlugin = this.Plugin(new Version(1, 0, 0, 0), workingId);
            var update = new AvailablePluginUpdate(oldPlugin, Remote(), false);
            lock (this.ListLock)
                this.Installed.Add(oldPlugin);

            var firstRound = await this.EnterAsync("RaceX");
            var second = this.manager.UpdateSinglePluginAsync(update, false, true);
            await Task.Delay(200);
            Assert.False(second.IsCompleted);

            lock (this.ListLock)
            {
                this.Installed.Remove(oldPlugin);
                this.Installed.Add(this.Plugin(NewVersion, workingId));
            }

            firstRound.Dispose();
            var status = await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(PluginUpdateStatus.StatusKind.AlreadyUpToDate, status.Status);
        }

        private static RemotePluginManifest Remote()
        {
            var repo = (PluginRepository)RuntimeHelpers.GetUninitializedObject(typeof(PluginRepository));
            SetField(repo, "<PluginMasterUrl>k__BackingField", "https://example.invalid/pluginmaster.json");
            return new RemotePluginManifest { InternalName = "RaceX", Name = "RaceX", AssemblyVersion = NewVersion, SourceRepo = repo };
        }

        private static byte[] Zip(string dllContent)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("RaceX.dll").Open());
                writer.Write(dllContent);
            }

            return ms.ToArray();
        }

        private static object GetField(object target, string name)
            => target.GetType().GetField(name, Any)!.GetValue(target)!;

        private static void SetField(object target, string name, object value)
            => target.GetType().GetField(name, Any)!.SetValue(target, value);

        private LocalPlugin Plugin(Version version, Guid workingId)
        {
            var manifest = new LocalPluginManifest { InternalName = "RaceX", Name = "RaceX", AssemblyVersion = version, WorkingPluginId = workingId };
            var plugin = (LocalPlugin)RuntimeHelpers.GetUninitializedObject(typeof(LocalPlugin));
            typeof(LocalPlugin).GetField("manifest", Any)!.SetValue(plugin, manifest);
            var dll = Path.Combine(this.pluginDir.FullName, "RaceX", version.ToString(), "RaceX.dll");
            typeof(LocalPlugin).GetField("<DllFile>k__BackingField", Any)!.SetValue(plugin, new FileInfo(dll));
            typeof(LocalPlugin).GetField("<State>k__BackingField", Any)!.SetValue(plugin, PluginState.Loaded);
            return plugin;
        }

        private async Task<IDisposable> EnterAsync(string internalName)
        {
            var method = typeof(PluginManager).GetMethod("EnterPluginOperationAsync", Any)!;
            return await (Task<IDisposable>)method.Invoke(this.manager, [internalName])!;
        }

        private async Task<Exception?> InstallAsync(Stream zip)
        {
            var method = typeof(PluginManager).GetMethod("InstallPluginInternalAsync", Any)!;
            try
            {
                await (Task<LocalPlugin>)method.Invoke(this.manager, [Remote(), false, PluginLoadReason.Update, zip, (Guid?)Guid.NewGuid()])!;
                return null;
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                return ex.InnerException;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private sealed class ClaimOnFirstRead : MemoryStream
        {
            private readonly Action claim;
            private int claimed;

            public ClaimOnFirstRead(byte[] data, Action claim)
                : base(data)
            {
                this.claim = claim;
            }

            public bool Claimed => Volatile.Read(ref this.claimed) != 0;

            public override int Read(byte[] buffer, int offset, int count)
            {
                this.Claim();
                return base.Read(buffer, offset, count);
            }

            public override int Read(Span<byte> buffer)
            {
                this.Claim();
                return base.Read(buffer);
            }

            public override long Seek(long offset, SeekOrigin loc)
            {
                this.Claim();
                return base.Seek(offset, loc);
            }

            private void Claim()
            {
                if (Interlocked.Exchange(ref this.claimed, 1) == 0)
                    this.claim();
            }
        }
    }
}
