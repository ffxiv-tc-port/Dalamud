namespace Dalamud.Utility.Internal;

/// <summary>
/// One line per cooldown for spans over a limit; counts the ones left out so the next line can report them.
/// </summary>
/// <remarks>Not thread safe: one thread per instance. Timestamps are <see cref="System.Diagnostics.Stopwatch"/> ticks.</remarks>
internal sealed class HitchCooldown
{
    private readonly long cooldownTicks;
    private bool loggedBefore;
    private long lastLogged;
    private int suppressed;
    private double suppressedMaxMs;

    /// <summary>Initializes a new instance of the <see cref="HitchCooldown"/> class.</summary>
    /// <param name="cooldownTicks">Least time between two lines; the first line is never held back.</param>
    public HitchCooldown(long cooldownTicks) => this.cooldownTicks = cooldownTicks;

    /// <summary>Takes one span that was over the limit.</summary>
    /// <param name="now">When the span ended.</param>
    /// <param name="milliseconds">How long the span was.</param>
    /// <param name="leftOut">When logging: spans over the limit left out since the previous line.</param>
    /// <param name="leftOutMaxMs">When logging: the longest of those, 0 when none.</param>
    /// <returns>Whether to log this span; when false it is counted for the next line instead.</returns>
    public bool TryLog(long now, double milliseconds, out int leftOut, out double leftOutMaxMs)
    {
        if (this.loggedBefore && now - this.lastLogged <= this.cooldownTicks)
        {
            this.suppressed++;
            if (milliseconds > this.suppressedMaxMs)
                this.suppressedMaxMs = milliseconds;

            leftOut = 0;
            leftOutMaxMs = 0;
            return false;
        }

        leftOut = this.suppressed;
        leftOutMaxMs = this.suppressedMaxMs;
        this.loggedBefore = true;
        this.lastLogged = now;
        this.suppressed = 0;
        this.suppressedMaxMs = 0;
        return true;
    }
}
