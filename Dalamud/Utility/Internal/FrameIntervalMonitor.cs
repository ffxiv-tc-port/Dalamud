using System.Diagnostics;

using Serilog;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Logs long gaps between two framework updates, the game's own work and GC pauses included, while the gap is one
/// the player saw: logged in, not on a loading screen, game window in front, at both ends of the gap.
/// </summary>
/// <remarks>
/// Framework thread only; call <see cref="Frame"/> first thing in every update. Steady frames allocate nothing.
/// Diagnostic for users to report back: does a GC pause stop the picture, and how much of a long gap is the game's.
/// </remarks>
internal sealed class FrameIntervalMonitor
{
    private readonly double thresholdMs;
    private readonly HitchCooldown cooldown;

    private long lastTimestamp;
    private HitchCounters lastCounters;
    private bool lastVisible;

    /// <summary>Initializes a new instance of the <see cref="FrameIntervalMonitor"/> class.</summary>
    /// <param name="thresholdMs">A gap longer than this is logged.</param>
    /// <param name="cooldownTicks">Least time between two lines, in <see cref="Stopwatch"/> ticks.</param>
    public FrameIntervalMonitor(double thresholdMs, long cooldownTicks)
    {
        this.thresholdMs = thresholdMs;
        this.cooldown = new HitchCooldown(cooldownTicks);
    }

    /// <summary>Gets the number of lines written.</summary>
    public int LinesWritten { get; private set; }

    /// <summary>Takes the start of one framework update.</summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="counters">Runtime counters read now, on the framework thread.</param>
    /// <param name="visible">Logged in, not loading, and the game window was in front when last sampled.</param>
    /// <param name="previousUpdateTicks">How long Dalamud and plugins took in the previous update.</param>
    /// <param name="previousGameTickTicks">How long the game's own framework tick took in the previous update.</param>
    /// <param name="inFrontNow">Asked only for a gap over the threshold: is the game window in front right now.</param>
    public void Frame(long now, in HitchCounters counters, bool visible, long previousUpdateTicks, long previousGameTickTicks, Func<bool> inFrontNow)
    {
        if (this.lastVisible && visible && this.lastTimestamp != 0)
        {
            var gapMs = HitchCounters.Milliseconds(now - this.lastTimestamp);
            if (gapMs > this.thresholdMs && inFrontNow())
            {
                if (this.cooldown.TryLog(now, gapMs, out var leftOut, out var leftOutMaxMs))
                    this.Write(gapMs, counters.Since(this.lastCounters), previousUpdateTicks, previousGameTickTicks, leftOut, leftOutMaxMs);
            }
        }

        this.lastTimestamp = now;
        this.lastCounters = counters;
        this.lastVisible = visible;
    }

    private void Write(double gapMs, in HitchCounters during, long updateTicks, long gameTickTicks, int leftOut, double leftOutMaxMs)
    {
        this.LinesWritten++;
        Log.Warning(
            "[FRAMEGAP] Long FrameInterval detected, {Total}ms > {Max}ms between two framework updates (logged in, not loading, game window in front); the update before it: Dalamud and plugins {UpdateMs:0.0} ms, the game's framework tick {GameTickMs:0.0} ms. During it: GC paused {GcPauseMs:0.0} ms, collections gen0 +{Gen0} gen1 +{Gen1} gen2 +{Gen2}; JIT on the framework thread {JitMs:0.0} ms for {JitMethods} methods; {LeftOut} more over the limit left out since the last line (longest {LeftOutMaxMs:0.0} ms)",
            gapMs,
            this.thresholdMs,
            HitchCounters.Milliseconds(updateTicks),
            HitchCounters.Milliseconds(gameTickTicks),
            during.GcPauseMs,
            during.Gen0,
            during.Gen1,
            during.Gen2,
            during.JitMs,
            during.JitMethods,
            leftOut,
            leftOutMaxMs);
    }
}
