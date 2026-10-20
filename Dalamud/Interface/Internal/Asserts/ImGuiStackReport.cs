using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;

using Dalamud.Logging.Internal;

namespace Dalamud.Interface.Internal.Asserts;

/// <summary>
/// Adds up the <see cref="ImGuiStackMeter"/> totals per plugin and logs, once per window, which plugins' draws left
/// ImGui's stacks deeper or shallower than they found them (<c>[ImGuiStack] summary …</c>); one line when none did.
/// </summary>
/// <remarks>
/// ImGui 1.88 keeps the tab bar, table, clipper, disabled, color, style and font stacks across frames, and a missing
/// End there raises no assert, so without this a log cannot show such a leak. Detection only: nothing is unwound.
/// <see cref="CreateMeter"/> from any thread; everything else from one thread at a time (the framework's).
/// </remarks>
internal sealed class ImGuiStackReport
{
    /// <summary>How many plugins are listed per window, most changes first.</summary>
    public const int MaxPlugins = 15;

    private static readonly ModuleLog Log = new("ImGuiStack");

    private readonly object registryLock = new();
    private readonly long windowTicks;
    private ImGuiStackMeter[] meters = [];

    private bool started;
    private long windowStart;
    private long windowEnd;
    private long windowFrames;

    /// <summary>
    /// Initializes a new instance of the <see cref="ImGuiStackReport"/> class.
    /// </summary>
    /// <param name="window">How often to report. The first window starts at the first <see cref="Tick"/>.</param>
    public ImGuiStackReport(TimeSpan window)
    {
        this.windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>Gets the report the UiBuilders' meters belong to, ticked by the framework.</summary>
    public static ImGuiStackReport Shared { get; } = new(TimeSpan.FromHours(1));

    /// <summary>
    /// Creates and registers the meter for one UiBuilder.
    /// </summary>
    /// <param name="pluginName">Internal name of the plugin.</param>
    /// <returns>The meter; <see cref="ImGuiStackMeter.Retire"/> it when the UiBuilder goes away.</returns>
    public ImGuiStackMeter CreateMeter(string pluginName)
    {
        var meter = new ImGuiStackMeter(pluginName);
        lock (this.registryLock)
            Volatile.Write(ref this.meters, [.. this.meters, meter]);
        return meter;
    }

    /// <summary>
    /// Counts a frame and logs the window when it is over. Call it once per framework update.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void Tick(long now)
    {
        if (!this.started)
        {
            this.started = true;
            this.StartWindow(now);
        }

        this.windowFrames++;
        if (now >= this.windowEnd)
            Write(this.CloseWindow(now), false);
    }

    /// <summary>
    /// Logs the window so far without waiting for it to end, for a framework that is going away.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void ReportUnfinished(long now)
    {
        if (this.started)
            Write(this.CloseWindow(now), true);
    }

    /// <summary>
    /// Describes what one plugin's draws changed, e.g. <c>TabBar +3 in 3 draws, Id -1 in 1 draw</c>.
    /// </summary>
    /// <param name="plugin">The plugin's counts.</param>
    /// <returns>The description.</returns>
    internal static string Describe(PluginReport plugin)
    {
        var sb = new StringBuilder();
        foreach (var s in plugin.Stacks)
        {
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append(s.Name).Append(' ');
            if (s.Added > 0)
                sb.Append('+').Append(s.Added);
            if (s.Added > 0 && s.Removed > 0)
                sb.Append('/');
            if (s.Removed > 0)
                sb.Append('-').Append(s.Removed);
            sb.Append(" in ").Append(s.Draws).Append(s.Draws == 1 ? " draw" : " draws");
        }

        if (plugin.Uncompared > 0)
        {
            if (sb.Length > 0)
                sb.Append(", ");
            sb.Append("another ImGui context after ").Append(plugin.Uncompared).Append(plugin.Uncompared == 1 ? " draw" : " draws").Append(" (not compared)");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Ends the window: returns what it counted and starts the next one. Retired meters are read a last time and
    /// dropped. Meters of one plugin name (a reloaded plugin) are added up.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <returns>The window's counts.</returns>
    internal WindowReport CloseWindow(long now)
    {
        const int n = ImGuiStackDepths.Count;
        var perPlugin = new Dictionary<string, long[]>(StringComparer.Ordinal);
        var finished = new List<ImGuiStackMeter>();
        var totals = new long[ImGuiStackMeter.TotalsLength];
        foreach (var meter in Volatile.Read(ref this.meters))
        {
            var retired = meter.IsRetired;
            meter.ReadTotals(totals);
            var counted = meter.Reported ?? new long[ImGuiStackMeter.TotalsLength];
            if (!perPlugin.TryGetValue(meter.PluginName, out var sums))
                perPlugin[meter.PluginName] = sums = new long[ImGuiStackMeter.TotalsLength];
            for (var i = 0; i < totals.Length; i++)
                sums[i] += totals[i] - counted[i];

            meter.Reported = (long[])totals.Clone();
            if (retired)
                finished.Add(meter);
        }

        if (finished.Count > 0)
        {
            lock (this.registryLock)
                Volatile.Write(ref this.meters, this.meters.Where(x => !finished.Contains(x)).ToArray());
        }

        var changed = new List<PluginReport>();
        foreach (var (name, sums) in perPlugin)
        {
            var stacks = new List<StackReport>();
            for (var i = 0; i < n; i++)
            {
                if (sums[(2 * n) + i] > 0)
                    stacks.Add(new StackReport(ImGuiStackDepths.Names[i], sums[i], sums[n + i], sums[(2 * n) + i]));
            }

            if (stacks.Count > 0 || sums[(3 * n) + 1] > 0)
                changed.Add(new PluginReport(name, sums[3 * n], sums[(3 * n) + 1], stacks));
        }

        var report = new WindowReport(
            (now - this.windowStart) / (double)Stopwatch.Frequency / 60,
            this.windowFrames,
            perPlugin.Values.Sum(x => x[3 * n]),
            perPlugin.Values.Count(x => x[3 * n] > 0),
            changed.OrderByDescending(x => x.Stacks.Sum(s => s.Added + s.Removed))
                   .ThenBy(x => x.Plugin, StringComparer.Ordinal)
                   .ToList());
        this.StartWindow(now);
        return report;
    }

    private static void Write(WindowReport report, bool unfinished)
    {
        var when = unfinished ? " (unfinished, the framework is unloading)" : string.Empty;
        if (report.Plugins.Count == 0)
        {
            Log.Information(
                "summary over the last {Minutes:0} min{When:l}: {Draws} plugin draws by {Drawn} plugins over {Frames} frames, none left an ImGui stack changed",
                report.Minutes,
                when,
                report.Draws,
                report.PluginsDrawn,
                report.Frames);
            return;
        }

        Log.Information(
            "summary over the last {Minutes:0} min{When:l}: {Changed} of {Drawn} plugins left ImGui stacks changed after their draws ({Draws} plugin draws over {Frames} frames)",
            report.Minutes,
            when,
            report.Plugins.Count,
            report.PluginsDrawn,
            report.Draws,
            report.Frames);
        foreach (var plugin in report.Plugins.Take(MaxPlugins))
            Log.Information("summary {Plugin} ({Draws} draws): {Changes:l}", plugin.Plugin, plugin.Draws, Describe(plugin));
        if (report.Plugins.Count > MaxPlugins)
            Log.Information("summary: {More} more plugins left stacks changed, not listed", report.Plugins.Count - MaxPlugins);
    }

    private void StartWindow(long now)
    {
        this.windowStart = now;
        this.windowEnd = now + this.windowTicks;
        this.windowFrames = 0;
    }

    /// <summary>
    /// What one plugin's draws did to one stack in a window.
    /// </summary>
    /// <param name="Name">The stack.</param>
    /// <param name="Added">Depth added, summed over the draws that left it deeper.</param>
    /// <param name="Removed">Depth removed, summed over the draws that left it shallower.</param>
    /// <param name="Draws">Draws that left it changed.</param>
    internal readonly record struct StackReport(string Name, long Added, long Removed, long Draws);

    /// <summary>
    /// What one plugin's draws changed in a window.
    /// </summary>
    /// <param name="Plugin">Internal name of the plugin.</param>
    /// <param name="Draws">Its draws in the window.</param>
    /// <param name="Uncompared">Draws after which another ImGui context, or none, was current.</param>
    /// <param name="Stacks">The stacks that changed, in <see cref="ImGuiStackDepths.Names"/> order.</param>
    internal sealed record PluginReport(string Plugin, long Draws, long Uncompared, List<StackReport> Stacks);

    /// <summary>
    /// What one window counted.
    /// </summary>
    /// <param name="Minutes">How long the window was.</param>
    /// <param name="Frames">Framework updates in the window.</param>
    /// <param name="Draws">Plugin draws in the window.</param>
    /// <param name="PluginsDrawn">Plugins that drew at least once.</param>
    /// <param name="Plugins">The plugins whose draws changed something, most changes first.</param>
    internal sealed record WindowReport(double Minutes, long Frames, long Draws, int PluginsDrawn, List<PluginReport> Plugins);
}
