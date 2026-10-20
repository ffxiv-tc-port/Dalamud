using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Common;
using Dalamud.Configuration.Internal;
using Dalamud.Game;
using Dalamud.Plugin.Internal;
using Dalamud.Storage;
using Dalamud.Utility;
using Dalamud.Utility.Internal;
using Dalamud.Utility.Timing;
using Serilog;
using Windows.Win32.Foundation;
using Windows.Win32.Security;

#if DEBUG
[assembly: InternalsVisibleTo("Dalamud.CorePlugin")]
#endif

[assembly: InternalsVisibleTo("Dalamud.Test")]
[assembly: InternalsVisibleTo("Dalamud.DevHelpers")]

namespace Dalamud;

/// <summary>
/// The main Dalamud class containing all subsystems.
/// </summary>
[ServiceManager.ProvidedService]
internal sealed unsafe class Dalamud : IServiceType
{
    #region Internals

    private static int shownServiceError = 0;
    private readonly ManualResetEvent unloadSignal;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="Dalamud"/> class.
    /// </summary>
    /// <param name="info">DalamudStartInfo instance.</param>
    /// <param name="fs">ReliableFileStorage instance.</param>
    /// <param name="configuration">The Dalamud configuration.</param>
    /// <param name="mainThreadContinueEvent">Event used to signal the main thread to continue.</param>
    public Dalamud(DalamudStartInfo info, ReliableFileStorage fs, DalamudConfiguration configuration, IntPtr mainThreadContinueEvent)
    {
        this.StartInfo = info;

        this.unloadSignal = new ManualResetEvent(false);
        this.unloadSignal.Reset();

        // Directory resolved signatures(CS, our own) will be cached in
        var cacheDir = new DirectoryInfo(Path.Combine(this.StartInfo.WorkingDirectory!, "cachedSigs"));
        if (!cacheDir.Exists)
            cacheDir.Create();

        // Set up the SigScanner for our target module
        TargetSigScanner scanner;
        using (Timings.Start("SigScanner Init"))
        {
            scanner = new TargetSigScanner(
                true, new FileInfo(Path.Combine(cacheDir.FullName, $"{this.StartInfo.GameVersion}.json")));
        }

        ServiceManager.InitializeProvidedServices(
            this,
            fs,
            configuration,
            scanner,
            Localization.FromAssets(info.AssetDirectory!, configuration.LanguageOverride));

        // Set up FFXIVClientStructs
        this.SetupClientStructsResolver(cacheDir);

        if (!CoreAddressPreflight.Check(scanner, this.StartInfo.GameVersion, StartupDialog.ShowError))
        {
            KickoffGameThread();
            return;
        }

        this.ServicesStarted = true;

        void KickoffGameThread()
        {
            Log.Verbose("=============== GAME THREAD KICKOFF ===============");
            Timings.Event("Game thread kickoff");
            Windows.Win32.PInvoke.SetEvent(new HANDLE(mainThreadContinueEvent));
        }

        void HandleServiceInitFailure(Task t)
        {
            Log.Error(t.Exception!, "Service initialization failure");

            if (Interlocked.CompareExchange(ref shownServiceError, 1, 0) != 0)
                return;

            Util.Fatal(ServiceFailureNotice.Format(t.Exception), ServiceFailureNotice.Caption, false);
        }

        ServiceManager.InitializeEarlyLoadableServices()
                      .ContinueWith(
                          t =>
                          {
                              if (t.IsCompletedSuccessfully)
                                  return;

                              HandleServiceInitFailure(t);
                          });

        ServiceManager.BlockingResolved.ContinueWith(
            t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    KickoffGameThread();
                    return;
                }

                HandleServiceInitFailure(t);
            });

        this.DefaultExceptionFilter = SetExceptionHandler(nint.Zero);
        SetExceptionHandler(this.DefaultExceptionFilter);
        Log.Debug($"SE default exception filter at {new IntPtr(this.DefaultExceptionFilter):X}");

        // The services are already loading: a throw here would end this thread with nobody left to unload them, so a
        // missing filter only disables its developer menu item.
        const string debugSig = "40 55 53 57 48 8D AC 24 70 AD FF FF";
        if (scanner.TryScanText(debugSig, out var debugFilter))
        {
            this.DebugExceptionFilter = debugFilter;
            Log.Debug($"SE debug exception filter at {this.DebugExceptionFilter.ToInt64():X}");
        }
        else
        {
            Log.Warning("SE debug exception filter not found; the developer menu cannot switch to it this session");
        }
    }

    /// <summary>
    /// Gets the start information for this Dalamud instance.
    /// </summary>
    internal DalamudStartInfo StartInfo { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the services were started; false when the core address check stopped Dalamud,
    /// in which case nothing is hooked and there is nothing to unload.
    /// </summary>
    internal bool ServicesStarted { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the game's debug exception filter was found.
    /// </summary>
    internal bool HasDebugExceptionFilter => this.DebugExceptionFilter != 0;

    /// <summary>
    /// Gets location of stored assets.
    /// </summary>
    internal DirectoryInfo AssetDirectory => new(this.StartInfo.AssetDirectory!);

    /// <summary>
    /// Gets the in-game default exception filter.
    /// </summary>
    private nint DefaultExceptionFilter { get; }

    /// <summary>
    /// Gets the in-game debug exception filter.
    /// </summary>
    private nint DebugExceptionFilter { get; }

    /// <summary>
    /// Signal to the crash handler process that we should restart the game.
    /// </summary>
    public static void RestartGame()
    {
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern void RaiseException(uint dwExceptionCode, uint dwExceptionFlags, uint nNumberOfArguments, IntPtr lpArguments);

        RaiseException(0x12345678, 0, 0, IntPtr.Zero);
        Process.GetCurrentProcess().Kill();
    }

    /// <summary>
    /// Queue an unload of Dalamud when it gets the chance.
    /// </summary>
    public void Unload()
    {
        Log.Information("Trigger unload");

        var reportCrashesSetting = Service<DalamudConfiguration>.GetNullable()?.ReportShutdownCrashes ?? true;
        var pmHasDevPlugins = Service<PluginManager>.GetNullable()?.InstalledPlugins.Any(x => x.IsDev) ?? false;
        if (!reportCrashesSetting && !pmHasDevPlugins)
        {
            // Leaking on purpose for now
            var attribs = default(SECURITY_ATTRIBUTES);
            attribs.nLength = (uint)Unsafe.SizeOf<SECURITY_ATTRIBUTES>();
            Windows.Win32.PInvoke.CreateMutex(attribs, false, "DALAMUD_CRASHES_NO_MORE");
        }

        this.unloadSignal.Set();
    }

    /// <summary>
    /// Wait for an unload request to start.
    /// </summary>
    public void WaitForUnload()
    {
        this.unloadSignal.WaitOne();
    }

    /// <summary>
    /// Replace the current exception handler with the default one.
    /// </summary>
    internal void UseDefaultExceptionHandler() =>
        SetExceptionHandler(this.DefaultExceptionFilter);

    /// <summary>
    /// Replace the current exception handler with a debug one. Does nothing if the game's debug filter was not found.
    /// </summary>
    internal void UseDebugExceptionHandler()
    {
        if (!this.HasDebugExceptionFilter)
        {
            Log.Warning("SE debug exception filter was not found; keeping the current exception handler");
            return;
        }

        SetExceptionHandler(this.DebugExceptionFilter);
    }

    /// <summary>
    /// Disable the current exception handler.
    /// </summary>
    internal void UseNoExceptionHandler() =>
        SetExceptionHandler(nint.Zero);

    /// <summary>
    /// Helper function to set the exception handler.
    /// </summary>
    private static nint SetExceptionHandler(nint newFilter)
    {
        var oldFilter =
            Windows.Win32.PInvoke.SetUnhandledExceptionFilter((delegate* unmanaged[Stdcall]<global::Windows.Win32.System.Diagnostics.Debug.EXCEPTION_POINTERS*, int>)newFilter);
        Log.Debug("Set ExceptionFilter to {0}, old: {1}", newFilter, (nint)oldFilter);
        return (nint)oldFilter;
    }

    private void SetupClientStructsResolver(DirectoryInfo cacheDir)
    {
        using (Timings.Start("CS Resolver Init"))
        {
            // the resolver tracks version as a field in the json
            InteropGenerator.Runtime.Resolver.GetInstance.Setup(Service<TargetSigScanner>.Get().SearchBase, $"{this.StartInfo.GameVersion}", new FileInfo(Path.Combine(cacheDir.FullName, "cs.json")));
            FFXIVClientStructs.Interop.Generated.Addresses.Register();
            InteropGenerator.Runtime.Resolver.GetInstance.Resolve();
        }

        ClientStructsAddressReport.Report(InteropGenerator.Runtime.Resolver.GetInstance.Addresses);
    }
}
