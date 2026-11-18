using System.Diagnostics;

namespace Dalamud.Utility.Internal;

/// <summary>
/// One line per cooldown for spans over a limit; counts the ones left out so the next line can report them.
/// </summary>
/// <remarks>
/// Not thread safe: one thread per instance. Timestamps are <see cref="Stopwatch"/> ticks.
/// Which spans are logged, and what the next line counts, never depends on <see cref="KeepDetail"/> or
/// <see cref="TakeFlush"/>: a flush only reports early, the next line still counts the same spans.
/// </remarks>
internal sealed class HitchCooldown
{
    /// <summary>A left-out span this long or longer, if it is the longest so far, gets its detail kept.</summary>
    public const double DefaultDetailMs = 250;

    private readonly long cooldownTicks;
    private readonly double detailMs;
    private bool loggedBefore;
    private long lastLogged;
    private int suppressed;
    private double suppressedMaxMs;
    private string? suppressedMaxDetail;
    private bool flushed;

    /// <summary>Initializes a new instance of the <see cref="HitchCooldown"/> class.</summary>
    /// <param name="cooldownTicks">Least time between two lines; the first line is never held back.</param>
    /// <param name="detailMs">Shortest left-out span whose detail is worth keeping.</param>
    public HitchCooldown(long cooldownTicks, double detailMs = DefaultDetailMs)
    {
        this.cooldownTicks = cooldownTicks;
        this.detailMs = detailMs;
    }

    /// <summary>Gets the cooldown in whole seconds, as printed.</summary>
    public long CooldownSeconds => this.cooldownTicks / Stopwatch.Frequency;

    /// <summary>Takes one span that was over the limit.</summary>
    /// <param name="now">When the span ended.</param>
    /// <param name="milliseconds">How long the span was.</param>
    /// <param name="leftOut">When logging: spans over the limit left out since the previous line.</param>
    /// <param name="leftOutMaxMs">When logging: the longest of those, 0 when none.</param>
    /// <returns>Whether to log this span; when false it is counted for the next line instead.</returns>
    public bool TryLog(long now, double milliseconds, out int leftOut, out double leftOutMaxMs) =>
        this.TryLog(now, milliseconds, out leftOut, out leftOutMaxMs, out _, out _);

    /// <summary>Takes one span that was over the limit.</summary>
    /// <param name="now">When the span ended.</param>
    /// <param name="milliseconds">How long the span was.</param>
    /// <param name="leftOut">When logging: spans over the limit left out since the previous line.</param>
    /// <param name="leftOutMaxMs">When logging: the longest of those, 0 when none.</param>
    /// <param name="leftOutDetail">When logging: the kept detail of the longest left out; null when none was kept or a flush gave it.</param>
    /// <param name="wantDetail">When not logging: this span is now the longest left out and long enough; describe it now, on this thread, and pass that to <see cref="KeepDetail"/>.</param>
    /// <returns>Whether to log this span; when false it is counted for the next line instead.</returns>
    public bool TryLog(long now, double milliseconds, out int leftOut, out double leftOutMaxMs, out string? leftOutDetail, out bool wantDetail)
    {
        if (this.loggedBefore && now - this.lastLogged <= this.cooldownTicks)
        {
            this.suppressed++;
            wantDetail = false;
            if (milliseconds > this.suppressedMaxMs)
            {
                this.suppressedMaxMs = milliseconds;

                // A detail always belongs to the longest; an older one would name the wrong span.
                this.suppressedMaxDetail = null;
                wantDetail = milliseconds >= this.detailMs;
            }

            leftOut = 0;
            leftOutMaxMs = 0;
            leftOutDetail = null;
            return false;
        }

        leftOut = this.suppressed;
        leftOutMaxMs = this.suppressedMaxMs;
        leftOutDetail = this.flushed ? null : this.suppressedMaxDetail;
        wantDetail = false;
        this.loggedBefore = true;
        this.lastLogged = now;
        this.suppressed = 0;
        this.suppressedMaxMs = 0;
        this.suppressedMaxDetail = null;
        this.flushed = false;
        return true;
    }

    /// <summary>Keeps the detail of the span the last <c>TryLog</c> asked about.</summary>
    /// <param name="detail">What the span was spent on, as printed.</param>
    public void KeepDetail(string detail) => this.suppressedMaxDetail = detail;

    /// <summary>Gets whether spans were left out, the cooldown after the last line is over, and they are not reported yet.</summary>
    /// <param name="now">The current timestamp.</param>
    /// <returns>Whether <see cref="TakeFlush"/> has something to report.</returns>
    public bool FlushDue(long now) => this.suppressed != 0 && !this.flushed && now - this.lastLogged > this.cooldownTicks;

    /// <summary>Reports the left-out spans early, once; the next line still counts them.</summary>
    /// <param name="leftOut">Spans over the limit left out since the last line.</param>
    /// <param name="leftOutMaxMs">The longest of those.</param>
    /// <param name="leftOutDetail">Its kept detail, or null.</param>
    public void TakeFlush(out int leftOut, out double leftOutMaxMs, out string? leftOutDetail)
    {
        this.flushed = true;
        leftOut = this.suppressed;
        leftOutMaxMs = this.suppressedMaxMs;
        leftOutDetail = this.suppressedMaxDetail;
    }
}
