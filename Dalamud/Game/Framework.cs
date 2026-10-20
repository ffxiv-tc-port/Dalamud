using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Configuration.Internal;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Gui;
using Dalamud.Game.Gui.Toast;
using Dalamud.Hooking;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Internal.Asserts;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Utility.Internal;

using ClientStateService = Dalamud.Game.ClientState.ClientState;
using CSFramework = FFXIVClientStructs.FFXIV.Client.System.Framework.Framework;
using HWND = TerraFX.Interop.Windows.HWND;
using Win32 = TerraFX.Interop.Windows.Windows;

namespace Dalamud.Game;

/// <summary>
/// This class represents the Framework of the native game client and grants access to various subsystems.
/// </summary>
[ServiceManager.EarlyLoadedService]
internal sealed class Framework : IInternalDisposableService, IFramework
{
    // A gap between two updates longer than this, while the player sees the game, gets a [FRAMEGAP] line.
    private const double FrameIntervalHitchMs = 100;

    // GetForegroundWindow costs about half a microsecond; a gap about to be logged is rechecked on the spot.
    private const int ForegroundSampleFrames = 8;

    private static readonly ModuleLog Log = new("Framework");

    private static readonly Stopwatch StatsStopwatch = new();

    private readonly Stopwatch updateStopwatch = new();
    private readonly HitchDetector hitchDetector;
    private readonly FrameIntervalMonitor frameIntervalMonitor;
    private readonly Func<bool> gameWindowInFront;

    private readonly Hook<CSFramework.Delegates.Tick> updateHook;
    private readonly Hook<CSFramework.Delegates.Destroy> destroyHook;

    [ServiceManager.ServiceDependency]
    private readonly GameLifecycle lifecycle = Service<GameLifecycle>.Get();

    [ServiceManager.ServiceDependency]
    private readonly DalamudConfiguration configuration = Service<DalamudConfiguration>.Get();

    private readonly CancellationTokenSource frameworkDestroy;
    private readonly ThreadBoundTaskScheduler frameworkThreadTaskScheduler;

    // DelayTicks entries, guarded by tickDelayedLock. A list walked by index, so a frame allocates nothing
    // while entries wait (Dalamud's own texture release loop always keeps one waiting).
    private readonly object tickDelayedLock = new();
    private readonly List<(TaskCompletionSource Source, ulong Expire, CancellationToken CancellationToken)>
        tickDelayedEntries = new();

    // Taken out of tickDelayedEntries this frame, completed outside the lock. Framework thread only.
    private readonly List<(TaskCompletionSource Source, CancellationToken CancellationToken, bool Canceled)>
        tickDelayedDue = new();

    private readonly GcPauseMonitor gcPauseMonitor;
    private readonly PluginAllocationReport allocationReport;
    private readonly GameNativeThreadReport? gameThreadReport;
    private readonly IpcJsonConversionReport ipcJsonReport;

    // tickDelayedEntries.Count, written under tickDelayedLock. Zero lets a frame skip the lock.
    private int tickDelayedCount;

    private ulong tickCounter;
    private bool perfDiagnosticsFailed;
    private bool allocationSamplerFailed;
    private AllocationSampler? allocationSampler;
    private int unfinishedPerfWindowsReported;

    // Frame interval diagnostics; framework thread only.
    private bool frameStateFailed;
    private ulong frameStateCount;
    private bool gameInFront;
    private HWND gameWindow;
    private long lastUpdateTicks;
    private long lastGameTickTicks;

    [ServiceManager.ServiceConstructor]
    private unsafe Framework()
    {
        this.UpdateAttribution = new FrameworkUpdateAttribution();
        this.hitchDetector = new HitchDetector("FrameworkUpdate", this.configuration.FrameworkUpdateHitch, this.UpdateAttribution.Describe);
        this.frameIntervalMonitor = new FrameIntervalMonitor(FrameIntervalHitchMs, 30 * Stopwatch.Frequency);
        this.gameWindowInFront = this.IsGameWindowInFront;
        this.gcPauseMonitor = new GcPauseMonitor(Stopwatch.GetTimestamp());
        this.allocationReport = new PluginAllocationReport(Stopwatch.GetTimestamp());
        this.gameThreadReport = GameNativeThreadReport.ForGame(Stopwatch.GetTimestamp());
        this.ipcJsonReport = IpcJsonConversionReport.Hourly(Stopwatch.GetTimestamp());

        this.frameworkDestroy = new();
        this.frameworkThreadTaskScheduler = new();
        this.FrameworkThreadTaskFactory = new(
            this.frameworkDestroy.Token,
            TaskCreationOptions.None,
            TaskContinuationOptions.None,
            this.frameworkThreadTaskScheduler);

        this.updateHook = Hook<CSFramework.Delegates.Tick>.FromAddress((nint)CSFramework.StaticVirtualTablePointer->Tick, this.HandleFrameworkUpdate);
        this.destroyHook = Hook<CSFramework.Delegates.Destroy>.FromAddress((nint)CSFramework.StaticVirtualTablePointer->Destroy, this.HandleFrameworkDestroy);

        this.updateHook.Enable();
        this.destroyHook.Enable();
    }

    /// <inheritdoc/>
    public event IFramework.OnUpdateDelegate? Update;

    /// <summary>
    /// Executes during FrameworkUpdate before all <see cref="Update"/> delegates.
    /// </summary>
    internal event IFramework.OnUpdateDelegate? BeforeUpdate;

    /// <summary>
    /// Gets or sets a value indicating whether the collection of stats is enabled.
    /// </summary>
    public static bool StatsEnabled { get; set; }

    /// <summary>
    /// Gets the stats history mapping.
    /// </summary>
    public static Dictionary<string, List<double>> StatsHistory { get; } = new();

    /// <inheritdoc/>
    public DateTime LastUpdate { get; private set; } = DateTime.MinValue;

    /// <inheritdoc/>
    public DateTime LastUpdateUTC { get; private set; } = DateTime.MinValue;

    /// <inheritdoc/>
    public TimeSpan UpdateDelta { get; private set; } = TimeSpan.Zero;

    /// <inheritdoc/>
    public bool IsInFrameworkUpdateThread => this.frameworkThreadTaskScheduler.IsOnBoundThread;

    /// <inheritdoc/>
    public bool IsFrameworkUnloading => this.frameworkDestroy.IsCancellationRequested;

    /// <summary>
    /// Gets the list of update sub-delegates that didn't get updated this frame.
    /// </summary>
    internal List<string> NonUpdatedSubDelegates { get; private set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether to dispatch update events.
    /// </summary>
    internal bool DispatchUpdateEvents { get; set; } = true;

    /// <summary>
    /// Gets what the current update spends its time on; the per-plugin Update forwards record into it.
    /// </summary>
    internal FrameworkUpdateAttribution UpdateAttribution { get; }

    private TaskFactory FrameworkThreadTaskFactory { get; }

    /// <inheritdoc/>
    public TaskFactory GetTaskFactory() => this.FrameworkThreadTaskFactory;

    /// <inheritdoc/>
    public Task DelayTicks(long numTicks, CancellationToken cancellationToken = default)
    {
        if (this.frameworkDestroy.IsCancellationRequested)
            return Task.FromCanceled(this.frameworkDestroy.Token);
        if (numTicks <= 0)
            return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this.tickDelayedLock)
        {
            // Checked again under the lock: the destroy hook cancels everything it finds here, nothing added later.
            if (this.frameworkDestroy.IsCancellationRequested)
                return Task.FromCanceled(this.frameworkDestroy.Token);

            this.tickDelayedEntries.Add((tcs, this.tickCounter + (ulong)numTicks, cancellationToken));
            Volatile.Write(ref this.tickDelayedCount, this.tickDelayedEntries.Count);
        }

        return tcs.Task;
    }

    /// <inheritdoc/>
    public Task Run(Action action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken == default)
            cancellationToken = this.FrameworkThreadTaskFactory.CancellationToken;
        return this.FrameworkThreadTaskFactory.StartNew(action, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken == default)
            cancellationToken = this.FrameworkThreadTaskFactory.CancellationToken;
        return this.FrameworkThreadTaskFactory.StartNew(action, cancellationToken);
    }

    /// <inheritdoc/>
    public Task Run(Func<Task> action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken == default)
            cancellationToken = this.FrameworkThreadTaskFactory.CancellationToken;
        return this.FrameworkThreadTaskFactory.StartNew(action, cancellationToken).Unwrap();
    }

    /// <inheritdoc/>
    public Task<T> Run<T>(Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        if (cancellationToken == default)
            cancellationToken = this.FrameworkThreadTaskFactory.CancellationToken;
        return this.FrameworkThreadTaskFactory.StartNew(action, cancellationToken).Unwrap();
    }

    /// <inheritdoc/>
    public Task<T> RunOnFrameworkThread<T>(Func<T> func) =>
        this.IsInFrameworkUpdateThread || this.IsFrameworkUnloading ? Task.FromResult(func()) : this.RunOnTick(func);

    /// <inheritdoc/>
    public Task RunOnFrameworkThread(Action action)
    {
        if (this.IsInFrameworkUpdateThread || this.IsFrameworkUnloading)
        {
            try
            {
                action();
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                return Task.FromException(ex);
            }
        }
        else
        {
            return this.RunOnTick(action);
        }
    }

    /// <inheritdoc/>
    public Task<T> RunOnFrameworkThread<T>(Func<Task<T>> func) =>
        this.IsInFrameworkUpdateThread || this.IsFrameworkUnloading ? func() : this.RunOnTick(func);

    /// <inheritdoc/>
    public Task RunOnFrameworkThread(Func<Task> func) =>
        this.IsInFrameworkUpdateThread || this.IsFrameworkUnloading ? func() : this.RunOnTick(func);

    /// <inheritdoc/>
    public Task<T> RunOnTick<T>(Func<T> func, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default)
    {
        if (this.IsFrameworkUnloading)
        {
            if (delay == default && delayTicks == default)
                return this.RunOnFrameworkThread(func);

            var cts = new CancellationTokenSource();
            cts.Cancel();
            return Task.FromCanceled<T>(cts.Token);
        }

        if (cancellationToken == default)
            cancellationToken = this.FrameworkThreadTaskFactory.CancellationToken;
        return this.FrameworkThreadTaskFactory.ContinueWhenAll(
            new[]
            {
                Task.Delay(delay, cancellationToken),
                this.DelayTicks(delayTicks, cancellationToken),
            },
            _ => func(),
            cancellationToken,
            TaskContinuationOptions.HideScheduler,
            this.frameworkThreadTaskScheduler);
    }

    /// <inheritdoc/>
    public Task RunOnTick(Action action, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default)
    {
        if (this.IsFrameworkUnloading)
        {
            if (delay == default && delayTicks == default)
                return this.RunOnFrameworkThread(action);

            var cts = new CancellationTokenSource();
            cts.Cancel();
            return Task.FromCanceled(cts.Token);
        }

        if (cancellationToken == default)
            cancellationToken = this.FrameworkThreadTaskFactory.CancellationToken;
        return this.FrameworkThreadTaskFactory.ContinueWhenAll(
            new[]
            {
                Task.Delay(delay, cancellationToken),
                this.DelayTicks(delayTicks, cancellationToken),
            },
            _ => action(),
            cancellationToken,
            TaskContinuationOptions.HideScheduler,
            this.frameworkThreadTaskScheduler);
    }

    /// <inheritdoc/>
    public Task<T> RunOnTick<T>(Func<Task<T>> func, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default)
    {
        if (this.IsFrameworkUnloading)
        {
            if (delay == default && delayTicks == default)
                return this.RunOnFrameworkThread(func);

            var cts = new CancellationTokenSource();
            cts.Cancel();
            return Task.FromCanceled<T>(cts.Token);
        }

        if (cancellationToken == default)
            cancellationToken = this.FrameworkThreadTaskFactory.CancellationToken;
        return this.FrameworkThreadTaskFactory.ContinueWhenAll(
            new[]
            {
                Task.Delay(delay, cancellationToken),
                this.DelayTicks(delayTicks, cancellationToken),
            },
            _ => func(),
            cancellationToken,
            TaskContinuationOptions.HideScheduler,
            this.frameworkThreadTaskScheduler).Unwrap();
    }

    /// <inheritdoc/>
    public Task RunOnTick(Func<Task> func, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default)
    {
        if (this.IsFrameworkUnloading)
        {
            if (delay == default && delayTicks == default)
                return this.RunOnFrameworkThread(func);

            var cts = new CancellationTokenSource();
            cts.Cancel();
            return Task.FromCanceled(cts.Token);
        }

        if (cancellationToken == default)
            cancellationToken = this.FrameworkThreadTaskFactory.CancellationToken;
        return this.FrameworkThreadTaskFactory.ContinueWhenAll(
            new[]
            {
                Task.Delay(delay, cancellationToken),
                this.DelayTicks(delayTicks, cancellationToken),
            },
            _ => func(),
            cancellationToken,
            TaskContinuationOptions.HideScheduler,
            this.frameworkThreadTaskScheduler).Unwrap();
    }

    /// <summary>
    /// Dispose of managed and unmanaged resources.
    /// </summary>
    void IInternalDisposableService.DisposeService()
    {
        this.RunOnFrameworkThread(() =>
        {
            // Unloading without the game closing never reaches the destroy hook.
            this.ReportUnfinishedPerfWindows();

            // ReSharper disable once AccessToDisposedClosure
            this.updateHook.Disable();

            // ReSharper disable once AccessToDisposedClosure
            this.destroyHook.Disable();
        }).Wait();

        this.updateHook.Dispose();
        this.destroyHook.Dispose();
        this.StopAllocationSampler();

        this.updateStopwatch.Reset();
        StatsStopwatch.Reset();
    }

    /// <summary>
    /// Adds a update time to the stats history.
    /// </summary>
    /// <param name="key">Delegate Name.</param>
    /// <param name="ms">Runtime.</param>
    internal static void AddToStats(string key, double ms)
    {
        if (!StatsHistory.ContainsKey(key))
            StatsHistory.Add(key, new List<double>());

        StatsHistory[key].Add(ms);

        if (StatsHistory[key].Count > 1000)
        {
            StatsHistory[key].RemoveRange(0, StatsHistory[key].Count - 1000);
        }
    }

    /// <summary>Copies the work still waiting for the framework thread: queued tasks and <see cref="DelayTicks"/> waits.</summary>
    /// <returns>The tasks; a wait's continuations hang off its task.</returns>
    internal List<Task> GetPendingWork()
    {
        var pending = new List<Task>(this.frameworkThreadTaskScheduler.GetQueuedTasks());
        lock (this.tickDelayedLock)
        {
            foreach (var entry in this.tickDelayedEntries)
                pending.Add(entry.Source.Task);
        }

        return pending;
    }

    /// <summary>
    /// Profiles each sub-delegate in the eventDelegate and logs to StatsHistory.
    /// </summary>
    /// <param name="eventDelegate">The Delegate to Profile.</param>
    /// <param name="frameworkInstance">The Framework Instance to pass to delegate.</param>
    /// <param name="plugin">Internal name of the plugin the handlers belong to, or null for Dalamud's own.</param>
    internal void ProfileAndInvoke(IFramework.OnUpdateDelegate? eventDelegate, IFramework frameworkInstance, string? plugin = null)
    {
        // Individually invoke OnUpdate handlers and time them.
        foreach (var d in Delegate.EnumerateInvocationList(eventDelegate))
        {
            var stopwatch = Stopwatch.StartNew();
            try
            {
                d(frameworkInstance);
            }
            catch (Exception ex)
            {
                if (plugin == null || !UnresolvedAddressLog.Instance.TryAbsorb(ex, plugin, $"{nameof(IFramework)}::{nameof(IFramework.Update)}"))
                    Log.Error(ex, "Exception while dispatching Framework::Update event.");
            }

            stopwatch.Stop();

            var key = $"{d.Target}::{d.Method.Name}";
            this.NonUpdatedSubDelegates.Remove(key);

            AddToStats(key, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    // Framework thread only: reads game memory.
    private static GameScreen ReadGameScreen()
    {
        var condition = Service<Condition>.GetNullable(Service<Condition>.ExceptionPropagationMode.None);
        if (condition != null && (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51]))
            return GameScreen.Loading;

        var clientState = Service<ClientStateService>.GetNullable(Service<ClientStateService>.ExceptionPropagationMode.None);
        return clientState is { IsLoggedIn: true } ? GameScreen.InGame : GameScreen.Title;
    }

    private unsafe bool HandleFrameworkUpdate(CSFramework* thisPtr)
    {
        this.frameworkThreadTaskScheduler.BoundThread ??= Thread.CurrentThread;

        ThreadSafety.MarkMainThread();
        this.TickFrameState();
        this.TickPerfDiagnostics();

        this.BeforeUpdate?.InvokeSafely(this);

        this.hitchDetector.Start();
        this.UpdateAttribution.Begin();

        try
        {
            var chatGui = Service<ChatGui>.GetNullable();
            var toastGui = Service<ToastGui>.GetNullable();
            var config = Service<DalamudConfiguration>.GetNullable();
            if (chatGui == null || toastGui == null)
                goto original;

            chatGui.UpdateQueue();
            toastGui.UpdateQueue();

            config?.Update();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Exception while handling Framework::Update hook.");
        }

        if (this.DispatchUpdateEvents)
        {
            this.updateStopwatch.Stop();
            this.UpdateDelta = TimeSpan.FromMilliseconds(this.updateStopwatch.ElapsedMilliseconds);
            this.updateStopwatch.Restart();

            this.LastUpdate = DateTime.Now;
            this.LastUpdateUTC = DateTime.UtcNow;
            this.tickCounter++;
            this.CompleteDelayedTicks();

            if (StatsEnabled)
            {
                StatsStopwatch.Restart();
                this.frameworkThreadTaskScheduler.Run();
                StatsStopwatch.Stop();

                AddToStats(nameof(this.frameworkThreadTaskScheduler), StatsStopwatch.Elapsed.TotalMilliseconds);
            }
            else
            {
                var taskTicks = this.frameworkThreadTaskScheduler.RunTimed(out var slowestTask, out var slowestAction, out var slowestTaskTicks);
                this.UpdateAttribution.SetTasks(taskTicks, slowestTask, slowestAction, slowestTaskTicks);
            }

            if (StatsEnabled && this.Update != null)
            {
                // Stat Tracking for Framework Updates
                this.NonUpdatedSubDelegates = StatsHistory.Keys.ToList();
                this.ProfileAndInvoke(this.Update, this);

                // Cleanup handlers that are no longer being called
                foreach (var key in this.NonUpdatedSubDelegates)
                {
                    if (key == nameof(this.FrameworkThreadTaskFactory))
                        continue;

                    if (StatsHistory[key].Count > 0)
                    {
                        StatsHistory[key].RemoveAt(0);
                    }
                    else
                    {
                        StatsHistory.Remove(key);
                    }
                }
            }
            else
            {
                this.Update?.InvokeSafely(this);
            }
        }

        this.hitchDetector.Stop();
        this.UpdateAttribution.End();
        this.lastUpdateTicks = this.hitchDetector.LastElapsedTicks;

    original:
        return this.CallOriginalUpdate(thisPtr);
    }

    private unsafe bool CallOriginalUpdate(CSFramework* thisPtr)
    {
        var start = Stopwatch.GetTimestamp();
        var result = this.updateHook.OriginalDisposeSafe(thisPtr);
        this.lastGameTickTicks = Stopwatch.GetTimestamp() - start;
        return result;
    }

    // Diagnostic for users to report back: the gap between two updates, and the screen the GC pause buckets use.
    // First in the hook so the gap runs entry to entry. Nothing may escape a native hook; a failure turns it off.
    private void TickFrameState()
    {
        if (this.frameStateFailed)
            return;

        try
        {
            var now = Stopwatch.GetTimestamp();
            var counters = HitchCounters.Read();
            var screen = ReadGameScreen();
            PerfFrameState.Publish(screen);

            if (this.frameStateCount++ % ForegroundSampleFrames == 0)
                this.gameInFront = this.IsGameWindowInFront();

            this.frameIntervalMonitor.Frame(
                now,
                counters,
                screen == GameScreen.InGame && this.gameInFront,
                this.lastUpdateTicks,
                this.lastGameTickTicks,
                this.gameWindowInFront);
            this.lastUpdateTicks = 0;
            this.lastGameTickTicks = 0;
        }
        catch (Exception ex)
        {
            this.frameStateFailed = true;
            Log.Error(ex, "Frame interval diagnostics failed and are off for the rest of this session");
        }
    }

    // False until the game window is known.
    private bool IsGameWindowInFront()
    {
        if (this.gameWindow == default)
        {
            var interfaceManager = Service<InterfaceManager>.GetNullable(Service<InterfaceManager>.ExceptionPropagationMode.None);
            if (interfaceManager == null)
                return false;

            this.gameWindow = interfaceManager.GameWindowHandle;
            if (this.gameWindow == default)
                return false;
        }

        return Win32.GetForegroundWindow() == this.gameWindow;
    }

    // Completes the DelayTicks tasks that are due or canceled. Framework thread only.
    // Every entry's token is still polled every frame; completion happens outside the lock, in insertion order.
    private void CompleteDelayedTicks()
    {
        if (Volatile.Read(ref this.tickDelayedCount) == 0)
            return;

        var due = this.tickDelayedDue;
        lock (this.tickDelayedLock)
        {
            var entries = this.tickDelayedEntries;
            var kept = 0;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.CancellationToken.IsCancellationRequested)
                    due.Add((entry.Source, entry.CancellationToken, true));
                else if (entry.Expire <= this.tickCounter)
                    due.Add((entry.Source, entry.CancellationToken, false));
                else
                    entries[kept++] = entry;
            }

            if (kept != entries.Count)
            {
                entries.RemoveRange(kept, entries.Count - kept);
                Volatile.Write(ref this.tickDelayedCount, kept);
            }
        }

        try
        {
            for (var i = 0; i < due.Count; i++)
            {
                var (source, ct, canceled) = due[i];
                if (canceled)
                    source.SetCanceled(ct);
                else
                    source.SetResult();
            }
        }
        finally
        {
            due.Clear();
        }
    }

    // 要使用者回報的診斷：確認週期性卡頓是否為 GC、各插件配置了多少記憶體。
    // 跑在 native hook 裡，例外絕不能逸出；失敗一次就記一行 Error 並關掉，不每幀洗 log。
    private void TickPerfDiagnostics()
    {
        if (this.perfDiagnosticsFailed)
            return;

        try
        {
            var now = Stopwatch.GetTimestamp();
            this.gcPauseMonitor.Poll(now);
            this.allocationReport.Tick(now);
            this.gameThreadReport?.Tick(now);
            this.ipcJsonReport.Tick(now);
            AssertSiteCounter.Shared.Tick(now);
            this.TickAllocationSampler(now);
        }
        catch (Exception ex)
        {
            this.perfDiagnosticsFailed = true;
            this.StopAllocationSampler();
            Log.Error(ex, "Performance diagnostics failed and are off for the rest of this session");
        }
    }

    // Follows the setting every tick. Built only here, on the framework thread, because the sampler counts its
    // constructing thread as the main thread. If only the sampler breaks, only the sampler goes off, until the
    // setting is turned off and on again.
    private void TickAllocationSampler(long now)
    {
        if (!this.configuration.PerfDiagAllocationSampling)
        {
            this.allocationSamplerFailed = false;
            if (this.allocationSampler is { } running)
                this.TurnOffAllocationSampler(running, now);
            return;
        }

        if (this.allocationSamplerFailed)
            return;

        var sampler = this.allocationSampler;
        if (sampler is null)
        {
            try
            {
                sampler = this.allocationSampler = new AllocationSampler(now);
            }
            catch (Exception ex)
            {
                this.allocationSamplerFailed = true;
                Log.Error(ex, "Allocation sampling could not start and is off until it is turned off and on again in settings");
                return;
            }

            Log.Information("Allocation sampling turned on in settings; it reports hourly and once more when turned off");
        }

        try
        {
            sampler.Tick(now);
        }
        catch (Exception ex)
        {
            this.allocationSamplerFailed = true;
            Log.Error(ex, "Allocation sampling failed and is off until it is turned off and on again in settings");
            this.StopAllocationSampler();
            return;
        }

        if (sampler.Failed)
        {
            this.allocationSamplerFailed = true;
            this.StopAllocationSampler();
        }
    }

    // Stops sampling first so the partial report covers everything up to now.
    private void TurnOffAllocationSampler(AllocationSampler sampler, long now)
    {
        this.StopAllocationSampler();
        Log.Information("Allocation sampling turned off in settings");
        try
        {
            sampler.ReportNow(now);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Allocation sampling could not write its last report");
        }
    }

    private void StopAllocationSampler()
    {
        var sampler = this.allocationSampler;
        this.allocationSampler = null;
        sampler?.Dispose();
    }

    // Logs what the hourly reports hold so far; a session shorter than their window would leave no numbers.
    // Once per framework. Called on the way out, the first time inside a native hook: nothing may escape, and
    // nothing here waits; it reads counters and writes log lines. Callers: the framework thread, or the thread
    // unloading the framework while the framework thread waits for it.
    private void ReportUnfinishedPerfWindows()
    {
        if (Interlocked.Exchange(ref this.unfinishedPerfWindowsReported, 1) != 0 || this.perfDiagnosticsFailed)
            return;

        try
        {
            var now = Stopwatch.GetTimestamp();

            // These two never throw, so they go first: a failing report below cannot take them down with it.
            this.gameThreadReport?.ReportUnfinished(now);
            this.ipcJsonReport?.ReportUnfinished(now);
            this.allocationReport.ReportUnfinished(now);
            AssertSiteCounter.Shared.ReportUnfinished(now);
            if (!this.allocationSamplerFailed)
                this.allocationSampler?.ReportUnfinished(now);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not write the unfinished performance reports");
        }
    }

    private unsafe bool HandleFrameworkDestroy(CSFramework* thisPtr)
    {
        // First, while every plugin is still loaded and its counters are still registered.
        this.ReportUnfinishedPerfWindows();

        this.frameworkDestroy.Cancel();
        this.DispatchUpdateEvents = false;
        lock (this.tickDelayedLock)
        {
            foreach (var entry in this.tickDelayedEntries)
                entry.Source.SetCanceled(this.frameworkDestroy.Token);
            this.tickDelayedEntries.Clear();
            Volatile.Write(ref this.tickDelayedCount, 0);
        }

        // All the same, for now...
        this.lifecycle.SetShuttingDown();
        this.lifecycle.SetUnloading();

        Log.Information("Framework::Destroy!");
        Service<Dalamud>.Get().Unload();
        this.frameworkThreadTaskScheduler.Run();
        ServiceManager.WaitForServiceUnload();
        Log.Information("Framework::Destroy OK!");

        return this.destroyHook.OriginalDisposeSafe(thisPtr);
    }
}

/// <summary>
/// Plugin-scoped version of a Framework service.
/// </summary>
[PluginInterface]
[ServiceManager.ScopedService]
#pragma warning disable SA1015
[ResolveVia<IFramework>]
#pragma warning restore SA1015
internal class FrameworkPluginScoped : IInternalDisposableService, IFramework
{
    private readonly PluginErrorHandler pluginErrorHandler;
    private readonly PluginAllocationMeter updateAllocations;
    private readonly FrameworkUpdateAttribution? updateAttribution;

    [ServiceManager.ServiceDependency]
    private readonly Framework frameworkService = Service<Framework>.Get();

    // Argument array for the Update forward, kept between frames. Never written after it is created
    // (a different argument gets a new array), so a re-entrant or concurrent forward can share it.
    private object[]? updateArgs;

    /// <summary>
    /// Initializes a new instance of the <see cref="FrameworkPluginScoped"/> class.
    /// </summary>
    /// <param name="pluginErrorHandler">Error handler instance.</param>
    /// <param name="plugin">The plugin this instance belongs to.</param>
    internal FrameworkPluginScoped(PluginErrorHandler pluginErrorHandler, LocalPlugin plugin)
    {
        this.pluginErrorHandler = pluginErrorHandler;
        this.updateAllocations = new PluginAllocationMeter(plugin.InternalName, PluginAllocationMeter.Update);
        this.updateAttribution = this.frameworkService.UpdateAttribution;
        this.frameworkService.Update += this.OnUpdateForward;
    }

    /// <inheritdoc/>
    public event IFramework.OnUpdateDelegate? Update;

    /// <inheritdoc/>
    public DateTime LastUpdate => this.frameworkService.LastUpdate;

    /// <inheritdoc/>
    public DateTime LastUpdateUTC => this.frameworkService.LastUpdateUTC;

    /// <inheritdoc/>
    public TimeSpan UpdateDelta => this.frameworkService.UpdateDelta;

    /// <inheritdoc/>
    public bool IsInFrameworkUpdateThread => this.frameworkService.IsInFrameworkUpdateThread;

    /// <inheritdoc/>
    public bool IsFrameworkUnloading => this.frameworkService.IsFrameworkUnloading;

    /// <inheritdoc/>
    void IInternalDisposableService.DisposeService()
    {
        this.frameworkService.Update -= this.OnUpdateForward;

        this.Update = null;
        this.updateAllocations.Retire();
    }

    /// <inheritdoc/>
    public TaskFactory GetTaskFactory() => this.frameworkService.GetTaskFactory();

    /// <inheritdoc/>
    public Task DelayTicks(long numTicks, CancellationToken cancellationToken = default) =>
        this.frameworkService.DelayTicks(numTicks, cancellationToken);

    /// <inheritdoc/>
    public Task Run(Action action, CancellationToken cancellationToken = default) =>
        this.frameworkService.Run(action, cancellationToken);

    /// <inheritdoc/>
    public Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken = default) =>
        this.frameworkService.Run(action, cancellationToken);

    /// <inheritdoc/>
    public Task Run(Func<Task> action, CancellationToken cancellationToken = default) =>
        this.frameworkService.Run(action, cancellationToken);

    /// <inheritdoc/>
    public Task<T> Run<T>(Func<Task<T>> action, CancellationToken cancellationToken = default) =>
        this.frameworkService.Run(action, cancellationToken);

    /// <inheritdoc/>
    public Task<T> RunOnFrameworkThread<T>(Func<T> func)
        => this.frameworkService.RunOnFrameworkThread(func);

    /// <inheritdoc/>
    public Task RunOnFrameworkThread(Action action)
        => this.frameworkService.RunOnFrameworkThread(action);

    /// <inheritdoc/>
    public Task<T> RunOnFrameworkThread<T>(Func<Task<T>> func)
        => this.frameworkService.RunOnFrameworkThread(func);

    /// <inheritdoc/>
    public Task RunOnFrameworkThread(Func<Task> func)
        => this.frameworkService.RunOnFrameworkThread(func);

    /// <inheritdoc/>
    public Task<T> RunOnTick<T>(Func<T> func, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default)
        => this.frameworkService.RunOnTick(func, delay, delayTicks, cancellationToken);

    /// <inheritdoc/>
    public Task RunOnTick(Action action, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default)
        => this.frameworkService.RunOnTick(action, delay, delayTicks, cancellationToken);

    /// <inheritdoc/>
    public Task<T> RunOnTick<T>(Func<Task<T>> func, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default)
        => this.frameworkService.RunOnTick(func, delay, delayTicks, cancellationToken);

    /// <inheritdoc/>
    public Task RunOnTick(Func<Task> func, TimeSpan delay = default, int delayTicks = default, CancellationToken cancellationToken = default)
        => this.frameworkService.RunOnTick(func, delay, delayTicks, cancellationToken);

    // 要使用者回報的診斷：量這個插件的 Update 配置了多少記憶體，找週期性 GC 的來源。
    // 量的是整個插件，兩種模式的例外處理都原封不動；args 在量測開始前取好，建它的那一次不算插件的。
    // The time it took goes to the framework's attribution, outside both modes' exception handling.
    private void OnUpdateForward(IFramework framework)
    {
        var start = Stopwatch.GetTimestamp();
        if (Framework.StatsEnabled && this.Update != null)
        {
            var allocatedBefore = PluginAllocationMeter.Begin();
            this.frameworkService.ProfileAndInvoke(this.Update, framework, this.updateAllocations.PluginName);
            this.updateAllocations.End(allocatedBefore);
        }
        else
        {
            var args = this.updateArgs;
            if (args is null || !ReferenceEquals(args[0], framework))
                this.updateArgs = args = [framework];

            var allocatedBefore = PluginAllocationMeter.Begin();
            this.pluginErrorHandler.InvokeAndCatch(this.Update, $"{nameof(IFramework)}::{nameof(IFramework.Update)}", args);
            this.updateAllocations.End(allocatedBefore);
        }

        this.updateAttribution?.AddPlugin(this.updateAllocations.PluginName, Stopwatch.GetTimestamp() - start);
    }
}
