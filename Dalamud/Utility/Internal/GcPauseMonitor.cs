using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Threading;

using Dalamud.Logging.Internal;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Watches the runtime's garbage collections and logs the ones that paused the game for long.
/// </summary>
/// <remarks>
/// Not thread safe: create it anywhere, then call <see cref="Poll"/> from one thread only (the framework thread).
/// At most <c>maxLinesPerWindow</c> lines per window, the window summary included.
/// Diagnostic for users to report back: is the periodic stutter a GC pause, and of which kind.
/// </remarks>
internal sealed class GcPauseMonitor
{
    private const double BytesPerMegabyte = 1024 * 1024;

    private static readonly ModuleLog Log = new("PerfDiag");

    private readonly TimeSpan longPause;
    private readonly int maxLinesPerWindow;
    private readonly long pollIntervalTicks;
    private readonly long windowTicks;
    private readonly GcPauseDistribution distribution = new();
    private readonly Gen0SizeStats gen0SizesBefore = new();

    private long nextPoll;
    private long lastAnyIndex;
    private long lastFullBlockingIndex;
    private long lastBackgroundIndex;
    private int lastGen0;
    private int lastGen1;
    private int lastGen2;
    private TimeSpan lastTotalPause;
    private TimeSpan lastFramePause;
    private long lastDistributionIndex;

    private long windowStart;
    private long windowEnd;
    private int windowStartGen0;
    private int windowStartGen1;
    private int windowStartGen2;
    private TimeSpan windowStartPause;
    private int windowBackground;
    private int windowFullBlocking;
    private int windowLongCollections;
    private int windowLongChecks;
    private int windowLines;
    private int windowSuppressed;
    private TimeSpan windowLongest;

    /// <summary>
    /// Initializes a new instance of the <see cref="GcPauseMonitor"/> class with the shipped limits,
    /// and logs the GC settings once.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public GcPauseMonitor(long now)
        : this(now, TimeSpan.FromMilliseconds(100), 60, TimeSpan.FromMilliseconds(500), TimeSpan.FromHours(1))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GcPauseMonitor"/> class, and logs the GC settings once.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="longPause">A collection pausing at least this long gets its own line.</param>
    /// <param name="maxLinesPerWindow">Line budget per window, the window summary included.</param>
    /// <param name="pollInterval">How often <see cref="Poll"/> actually looks.</param>
    /// <param name="window">Length of a summary window.</param>
    public GcPauseMonitor(long now, TimeSpan longPause, int maxLinesPerWindow, TimeSpan pollInterval, TimeSpan window)
    {
        this.longPause = longPause;
        this.maxLinesPerWindow = Math.Max(1, maxLinesPerWindow);
        this.pollIntervalTicks = (long)(pollInterval.TotalSeconds * Stopwatch.Frequency);
        this.windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);

        this.lastAnyIndex = GC.GetGCMemoryInfo(GCKind.Any).Index;
        this.lastFullBlockingIndex = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
        this.lastBackgroundIndex = GC.GetGCMemoryInfo(GCKind.Background).Index;
        this.lastGen0 = GC.CollectionCount(0);
        this.lastGen1 = GC.CollectionCount(1);
        this.lastGen2 = GC.CollectionCount(2);
        this.lastTotalPause = GC.GetTotalPauseDuration();
        this.lastFramePause = this.lastTotalPause;
        this.lastDistributionIndex = this.lastAnyIndex;
        this.nextPoll = now + this.pollIntervalTicks;
        this.StartWindow(now);

        Log.Information(
            "GC settings: server GC {ServerGc}, latency mode {LatencyMode}, LOH compaction {LohCompaction}, {Processors} logical processors, {AvailableMb:0} MB available to the GC; runtime GC configuration: {Configuration}",
            GCSettings.IsServerGC,
            GCSettings.LatencyMode.ToString(),
            GCSettings.LargeObjectHeapCompactionMode.ToString(),
            Environment.ProcessorCount,
            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / BytesPerMegabyte,
            string.Join(", ", GC.GetConfigurationVariables().OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}")));

        // The runtime reads these only from the environment and never lists them in its GC configuration.
        Log.Information(
            "GC gen0 budget environment: DOTNET_GCgen0MaxBudget={Gen0MaxBudget}, DOTNET_GCgen0size={Gen0Size} (hexadecimal bytes; unset or 0 means the runtime default); the hourly GC summary shows the gen0 size actually reached",
            Environment.GetEnvironmentVariable("DOTNET_GCgen0MaxBudget") ?? "(unset)",
            Environment.GetEnvironmentVariable("DOTNET_GCgen0size") ?? "(unset)");

        Log.Information(
            "Thread pool: idle threads retire {Retire} (configured System.Threading.ThreadPool.ThreadTimeoutMs={Configured}, DOTNET_ThreadPool_ThreadTimeoutMs={Environment}); never retiring keeps hot-reloaded plugins from corrupting the GC heap (dotnet/runtime#135441)",
            DescribeThreadPoolTimeout(),
            AppContext.GetData("System.Threading.ThreadPool.ThreadTimeoutMs") ?? "(unset)",
            Environment.GetEnvironmentVariable("DOTNET_ThreadPool_ThreadTimeoutMs") ?? "(unset)");
    }

    /// <summary>Gets the gen0 sizes before the gen0 collections seen in the current window.</summary>
    public Gen0SizeStats Gen0SizesBefore => this.gen0SizesBefore;

    // The effective value lives in a private static of the runtime's thread pool; "unknown" when that layout changes.
    private static string DescribeThreadPoolTimeout()
    {
        try
        {
            var field = typeof(ThreadPool).Assembly.GetType("System.Threading.PortableThreadPool")
                ?.GetField("ThreadPoolThreadTimeoutMs", BindingFlags.NonPublic | BindingFlags.Static);
            return field?.GetValue(null) is int ms ? (ms < 0 ? "never" : $"after {ms} ms idle") : "unknown";
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    /// <summary>
    /// Looks for collections since the previous look, at most once per poll interval. Call it every frame.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void Poll(long now)
    {
        // Every frame: the total pause only moves when a pause ends, so a collection is looked at right after it.
        var framePause = GC.GetTotalPauseDuration();
        if (framePause != this.lastFramePause)
        {
            this.lastFramePause = framePause;
            this.SeeLatestCollection();
        }

        if (now < this.nextPoll)
            return;

        this.nextPoll = now + this.pollIntervalTicks;

        var gen0 = GC.CollectionCount(0);
        var gen1 = GC.CollectionCount(1);
        var gen2 = GC.CollectionCount(2);
        var totalPause = GC.GetTotalPauseDuration();
        this.Examine(gen0 - this.lastGen0, gen1 - this.lastGen1, gen2 - this.lastGen2, totalPause - this.lastTotalPause);
        this.lastGen0 = gen0;
        this.lastGen1 = gen1;
        this.lastGen2 = gen2;
        this.lastTotalPause = totalPause;

        if (now >= this.windowEnd)
        {
            this.WriteSummary(now);
            this.StartWindow(now);
        }
    }

    private static TimeSpan LongestPause(in GCMemoryInfo info)
    {
        var longest = TimeSpan.Zero;
        foreach (var pause in info.PauseDurations)
        {
            if (pause > longest)
                longest = pause;
        }

        return longest;
    }

    private static string KindOf(in GCMemoryInfo info) =>
        info.Generation < 2 ? "ephemeral" : info.Concurrent ? "background" : "full blocking";

    private static string Megabytes(long bytes) => (bytes / BytesPerMegabyte).ToString("0.0", CultureInfo.InvariantCulture);

    private static string PausesOf(in GCMemoryInfo info)
    {
        var parts = new List<string>();
        foreach (var pause in info.PauseDurations)
        {
            if (pause > TimeSpan.Zero)
                parts.Add(pause.TotalMilliseconds.ToString("0.0", CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "0" : string.Join(" + ", parts);
    }

    private static string GenerationSizesOf(in GCMemoryInfo info)
    {
        var parts = new List<string>();
        foreach (var generation in info.GenerationInfo)
            parts.Add(Megabytes(generation.SizeAfterBytes));

        return string.Join(" / ", parts);
    }

    private static double PercentOf(long part, long whole) => whole > 0 ? 100.0 * part / whole : 0;

    // Only the latest collection is visible; the ones before it since the previous sighting count as not seen.
    // The screen is the one this frame published, never read from game memory here.
    private void SeeLatestCollection()
    {
        var latest = GC.GetGCMemoryInfo(GCKind.Any);
        if (latest.Index <= this.lastDistributionIndex)
            return;

        this.distribution.Add(PerfFrameState.Screen, LongestPause(latest), latest.Index - this.lastDistributionIndex);
        this.lastDistributionIndex = latest.Index;

        // Only gen0 collections: gen0 filling up starts them, while a higher one may be started by another generation's budget.
        var generations = latest.GenerationInfo;
        if (latest.Generation == 0 && generations.Length > 0)
            this.gen0SizesBefore.Add(generations[0].SizeBeforeBytes);
    }

    private void Examine(int gen0, int gen1, int gen2, TimeSpan pauseSinceLastLook)
    {
        var any = GC.GetGCMemoryInfo(GCKind.Any);
        var fullBlocking = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        var background = GC.GetGCMemoryInfo(GCKind.Background);

        // The same collection can be the latest of two kinds at once; each index is looked at once.
        var newFullBlocking = fullBlocking.Index > this.lastFullBlockingIndex;
        var newBackground = background.Index > this.lastBackgroundIndex;
        var newAny = any.Index > this.lastAnyIndex &&
                     !(newFullBlocking && any.Index == fullBlocking.Index) &&
                     !(newBackground && any.Index == background.Index);
        this.lastFullBlockingIndex = Math.Max(this.lastFullBlockingIndex, fullBlocking.Index);
        this.lastBackgroundIndex = Math.Max(this.lastBackgroundIndex, background.Index);
        this.lastAnyIndex = Math.Max(this.lastAnyIndex, any.Index);

        if (newFullBlocking)
            this.windowFullBlocking++;
        if (newBackground)
            this.windowBackground++;

        var longest = TimeSpan.Zero;
        var anyLong = false;
        if (newFullBlocking)
            anyLong |= this.Consider(fullBlocking, gen0, gen1, gen2, pauseSinceLastLook, ref longest);
        if (newBackground)
            anyLong |= this.Consider(background, gen0, gen1, gen2, pauseSinceLastLook, ref longest);
        if (newAny)
            anyLong |= this.Consider(any, gen0, gen1, gen2, pauseSinceLastLook, ref longest);

        if (longest > this.windowLongest)
            this.windowLongest = longest;

        // Several short collections between two looks can still add up to one long stall.
        if (!anyLong && pauseSinceLastLook >= this.longPause && pauseSinceLastLook > TimeSpan.Zero)
        {
            this.windowLongChecks++;
            if (this.TryTakeLine())
            {
                Log.Information(
                    "GC paused the game for {PauseMs:0.0} ms in total since the last check, no single collection that long (longest seen {LongestMs:0.0} ms); collection counts gen0 +{Gen0}, gen1 +{Gen1}, gen2 +{Gen2}",
                    pauseSinceLastLook.TotalMilliseconds,
                    longest.TotalMilliseconds,
                    gen0,
                    gen1,
                    gen2);
            }
        }
    }

    private bool Consider(in GCMemoryInfo info, int gen0, int gen1, int gen2, TimeSpan pauseSinceLastLook, ref TimeSpan longest)
    {
        var pause = LongestPause(info);
        if (pause > longest)
            longest = pause;

        if (pause < this.longPause)
            return false;

        this.windowLongCollections++;
        if (this.TryTakeLine())
        {
            Log.Information(
                "GC #{Index} paused the game for {PauseMs:0.0} ms: gen{Generation} {Kind}, compacted {Compacted}, concurrent {Concurrent}, pauses {Pauses} ms; heap {HeapMb:0.0} MB, fragmented {FragmentedMb:0.0} MB, promoted {PromotedMb:0.0} MB, pinned objects {Pinned}, finalization pending {FinalizationPending}, committed {CommittedMb:0.0} MB, memory load {MemoryLoadPct:0}% (high at {HighLoadPct:0}%); after it gen0/gen1/gen2/LOH/POH {GenerationSizes} MB; since the last check collection counts gen0 +{Gen0}, gen1 +{Gen1}, gen2 +{Gen2}, paused {PauseSinceLastMs:0.0} ms in total",
                info.Index,
                pause.TotalMilliseconds,
                info.Generation,
                KindOf(info),
                info.Compacted,
                info.Concurrent,
                PausesOf(info),
                info.HeapSizeBytes / BytesPerMegabyte,
                info.FragmentedBytes / BytesPerMegabyte,
                info.PromotedBytes / BytesPerMegabyte,
                info.PinnedObjectsCount,
                info.FinalizationPendingCount,
                info.TotalCommittedBytes / BytesPerMegabyte,
                PercentOf(info.MemoryLoadBytes, info.TotalAvailableMemoryBytes),
                PercentOf(info.HighMemoryLoadThresholdBytes, info.TotalAvailableMemoryBytes),
                GenerationSizesOf(info),
                gen0,
                gen1,
                gen2,
                pauseSinceLastLook.TotalMilliseconds);
        }

        return true;
    }

    private bool TryTakeLine()
    {
        // One line of the budget stays reserved for the window summary.
        if (this.windowLines < this.maxLinesPerWindow - 1)
        {
            this.windowLines++;
            return true;
        }

        this.windowSuppressed++;
        return false;
    }

    private void StartWindow(long now)
    {
        this.windowStart = now;
        this.windowEnd = now + this.windowTicks;
        this.windowStartGen0 = this.lastGen0;
        this.windowStartGen1 = this.lastGen1;
        this.windowStartGen2 = this.lastGen2;
        this.windowStartPause = this.lastTotalPause;
        this.windowBackground = 0;
        this.windowFullBlocking = 0;
        this.windowLongCollections = 0;
        this.windowLongChecks = 0;
        this.windowLines = 0;
        this.windowSuppressed = 0;
        this.windowLongest = TimeSpan.Zero;
        this.distribution.Reset();
        this.gen0SizesBefore.Reset();
    }

    private void WriteSummary(long now)
    {
        var minutes = (now - this.windowStart) / (double)Stopwatch.Frequency / 60;
        var paused = this.lastTotalPause - this.windowStartPause;
        Log.Information(
            "GC over the last {Minutes:0} min: collection counts gen0 +{Gen0}, gen1 +{Gen1}, gen2 +{Gen2} (seen {Background} background, {FullBlocking} full blocking), paused {PausedMs:0} ms in total ({PausedPct:0.00}% of the time), longest single pause seen {LongestMs:0.0} ms; reaching {LongPauseMs:0} ms or more: {LongCollections} single collections seen, {LongChecks} checks where shorter collections only added up to it; {Logged} lines logged, {Suppressed} left out by the {MaxLines} lines per window limit; latency mode now {LatencyMode}; gen0 size before the gen0 collections seen: {Gen0SizeBefore}",
            minutes,
            this.lastGen0 - this.windowStartGen0,
            this.lastGen1 - this.windowStartGen1,
            this.lastGen2 - this.windowStartGen2,
            this.windowBackground,
            this.windowFullBlocking,
            paused.TotalMilliseconds,
            minutes > 0 ? paused.TotalMinutes / minutes * 100 : 0,
            this.windowLongest.TotalMilliseconds,
            this.longPause.TotalMilliseconds,
            this.windowLongCollections,
            this.windowLongChecks,
            this.windowLines,
            this.windowSuppressed,
            this.maxLinesPerWindow,
            GCSettings.LatencyMode.ToString(),
            this.gen0SizesBefore.Describe());

        Log.Information(
            "GC pauses over the last {Minutes:0} min by screen, each collection counted once by its longest pause, buckets <10 / 10-17 / 17-33 / 33-50 / 50-100 / >=100 ms: loading screen {Loading}, in game {InGame}, title screen {Title}",
            minutes,
            this.distribution.Describe(GameScreen.Loading),
            this.distribution.Describe(GameScreen.InGame),
            this.distribution.Describe(GameScreen.Title));
    }
}
