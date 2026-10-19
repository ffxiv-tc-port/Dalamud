using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Dalamud.Logging.Internal;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Adds up the <see cref="PluginAllocationMeter"/> totals per plugin and logs the biggest allocators once per window.
/// </summary>
/// <remarks>
/// Not thread safe: call <see cref="Tick"/> from the framework thread only. Meters are only read here.
/// Diagnostic for users to report back: who allocates enough to drive the GC.
/// </remarks>
internal sealed class PluginAllocationReport
{
    private const double BytesPerMegabyte = 1024 * 1024;
    private const int Columns = 4;

    private static readonly ModuleLog Log = new("PerfDiag");

    private readonly long windowTicks;
    private readonly int maxLines;
    private readonly Dictionary<PluginAllocationMeter, long> alreadyCounted = new();

    private long windowStart;
    private long windowEnd;
    private long windowFrames;
    private long windowStartProcessBytes;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAllocationReport"/> class: top 15, once an hour.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public PluginAllocationReport(long now)
        : this(now, TimeSpan.FromHours(1), 15)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginAllocationReport"/> class.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="window">How often to report.</param>
    /// <param name="maxLines">How many plugins to list, one line each.</param>
    public PluginAllocationReport(long now, TimeSpan window, int maxLines)
    {
        this.windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        this.maxLines = Math.Max(1, maxLines);
        this.StartWindow(now);
    }

    /// <summary>
    /// Counts a frame and reports when the window is over. Call it once per framework update.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void Tick(long now)
    {
        this.windowFrames++;
        if (now >= this.windowEnd)
        {
            this.Report(now);
            this.StartWindow(now);
        }
    }

    /// <summary>
    /// Reports the window so far without waiting for it to end, for a framework that is going away: one line in
    /// front says that the window is unfinished and how long it was. The next window starts now.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void ReportUnfinished(long now)
    {
        Log.Information(
            "Allocations of an unfinished window, {Minutes:0.0} min and {Frames} frames, reported early because the framework is unloading:",
            (now - this.windowStart) / (double)Stopwatch.Frequency / 60,
            this.windowFrames);
        this.Report(now);
        this.StartWindow(now);
    }

    /// <summary>
    /// Picks the plugins to list: biggest total over all callbacks first.
    /// </summary>
    /// <param name="perPlugin">Bytes per callback column, by plugin name.</param>
    /// <param name="maxLines">How many to pick at most.</param>
    /// <returns>The picked plugins, in listing order.</returns>
    internal static List<KeyValuePair<string, long[]>> Rank(IEnumerable<KeyValuePair<string, long[]>> perPlugin, int maxLines) =>
        perPlugin.OrderByDescending(x => x.Value.Sum()).Take(maxLines).ToList();

    private static int ColumnOf(string callback) => callback switch
    {
        PluginAllocationMeter.Draw => 0,
        PluginAllocationMeter.Update => 1,
        PluginAllocationMeter.AddonEvents => 2,
        _ => 3,
    };

    private void StartWindow(long now)
    {
        this.windowStart = now;
        this.windowEnd = now + this.windowTicks;
        this.windowFrames = 0;
        this.windowStartProcessBytes = GC.GetTotalAllocatedBytes(false);
    }

    private void Report(long now)
    {
        var perPlugin = new Dictionary<string, long[]>(StringComparer.Ordinal);
        var finished = new List<PluginAllocationMeter>();
        foreach (var meter in PluginAllocationMeter.Registered)
        {
            // A retired meter is read one last time and dropped; anything it counts after this is lost.
            var retired = meter.IsRetired;
            var total = meter.Bytes;
            this.alreadyCounted.TryGetValue(meter, out var counted);
            if (retired)
            {
                finished.Add(meter);
                this.alreadyCounted.Remove(meter);
            }
            else
            {
                this.alreadyCounted[meter] = total;
            }

            var added = total - counted;
            if (added <= 0)
                continue;

            if (!perPlugin.TryGetValue(meter.PluginName, out var sums))
                perPlugin[meter.PluginName] = sums = new long[Columns];
            sums[ColumnOf(meter.Callback)] += added;
        }

        PluginAllocationMeter.Unregister(finished);

        var minutes = (now - this.windowStart) / (double)Stopwatch.Frequency / 60;
        var processBytes = GC.GetTotalAllocatedBytes(false) - this.windowStartProcessBytes;
        var frames = Math.Max(1, this.windowFrames);
        var ranked = Rank(perPlugin, this.maxLines);
        if (ranked.Count == 0)
        {
            Log.Information(
                "Allocations over the last {Minutes:0} min: the process allocated {ProcessMb:0.0} MB; no measured plugin callback (Draw, Update, addon events, chat) allocated anything over {Frames} frames",
                minutes,
                processBytes / BytesPerMegabyte,
                this.windowFrames);
            return;
        }

        var pluginsBytes = perPlugin.Values.Sum(x => x.Sum());
        var outsideBytes = Math.Max(0, processBytes - pluginsBytes);
        for (var i = 0; i < ranked.Count; i++)
        {
            var (name, sums) = (ranked[i].Key, ranked[i].Value);
            var total = sums.Sum();
            if (i == 0)
            {
                Log.Information(
                    "Allocations over the last {Minutes:0} min: the process allocated {ProcessMb:0.0} MB, plugin callbacks {PluginsMb:0.0} MB, outside them {OutsideMb:0.0} MB ({OutsidePct:0}%) over {Frames} frames. #1 {Plugin} {TotalMb:0.0} MB = {PerFrameKb:0.0} KB per frame (Draw {DrawMb:0.0} MB, Update {UpdateMb:0.0} MB, addon events {AddonEventsMb:0.0} MB, chat {ChatMb:0.0} MB)",
                    minutes,
                    processBytes / BytesPerMegabyte,
                    pluginsBytes / BytesPerMegabyte,
                    outsideBytes / BytesPerMegabyte,
                    processBytes > 0 ? 100.0 * outsideBytes / processBytes : 0,
                    this.windowFrames,
                    name,
                    total / BytesPerMegabyte,
                    total / 1024.0 / frames,
                    sums[0] / BytesPerMegabyte,
                    sums[1] / BytesPerMegabyte,
                    sums[2] / BytesPerMegabyte,
                    sums[3] / BytesPerMegabyte);
            }
            else
            {
                Log.Information(
                    "Allocations over the last {Minutes:0} min: #{Rank} {Plugin} {TotalMb:0.0} MB = {PerFrameKb:0.0} KB per frame (Draw {DrawMb:0.0} MB, Update {UpdateMb:0.0} MB, addon events {AddonEventsMb:0.0} MB, chat {ChatMb:0.0} MB)",
                    minutes,
                    i + 1,
                    name,
                    total / BytesPerMegabyte,
                    total / 1024.0 / frames,
                    sums[0] / BytesPerMegabyte,
                    sums[1] / BytesPerMegabyte,
                    sums[2] / BytesPerMegabyte,
                    sums[3] / BytesPerMegabyte);
            }
        }
    }
}
