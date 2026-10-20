using System.Diagnostics;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Writes how often Dalamud's detours failed (<c>[DETOUR] summary …</c>) and how often unresolved ClientStructs
/// addresses were hit (<c>[UNRESOLVED] summary …</c>) once per window and once more on unload; nothing when all
/// counts stayed still.
/// </summary>
/// <remarks>
/// The failures themselves log only their first occurrence, so without this a log cannot tell one failure from a
/// million. Not thread safe: <see cref="Tick"/> and <see cref="ReportUnfinished"/> from one thread at a time.
/// Never throws.
/// </remarks>
internal sealed class FaultSummaryReport
{
    private readonly long windowTicks;
    private readonly UnresolvedAddressLog unresolved;
    private long windowStart;

    /// <summary>
    /// Initializes a new instance of the <see cref="FaultSummaryReport"/> class.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch"/> timestamp; the first window starts here.</param>
    /// <param name="window">How long each window lasts.</param>
    /// <param name="unresolved">The unresolved-address counts to summarize.</param>
    internal FaultSummaryReport(long now, TimeSpan window, UnresolvedAddressLog unresolved)
    {
        this.windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        this.unresolved = unresolved;
        this.windowStart = now;
    }

    /// <summary>
    /// Creates the hourly report over the process-wide counters.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch"/> timestamp.</param>
    /// <returns>The report.</returns>
    internal static FaultSummaryReport Hourly(long now) => new(now, TimeSpan.FromHours(1), UnresolvedAddressLog.Instance);

    /// <summary>
    /// Writes the summary when the window is over. Once per frame.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch"/> timestamp.</param>
    internal void Tick(long now)
    {
        if (now - this.windowStart < this.windowTicks)
            return;

        this.Write(now, false);
    }

    /// <summary>
    /// Writes what the current window holds so far, while Dalamud unloads.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch"/> timestamp.</param>
    internal void ReportUnfinished(long now) => this.Write(now, true);

    private void Write(long now, bool unloading)
    {
        var minutes = (now - this.windowStart) / (double)Stopwatch.Frequency / 60;
        this.windowStart = now;
        DetourFault.WriteSummary(minutes, unloading);
        this.unresolved.WriteSummary(minutes, unloading);
    }
}
