using System.Diagnostics;
using System.Globalization;

using Serilog;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Logs long gaps between two framework updates, the game's own work and GC pauses included, while the gap is one
/// the player saw: logged in, not on a loading screen, game window in front, at both ends of the gap.
/// Gaps the player could not see are only counted, into one line per summary window.
/// </summary>
/// <remarks>
/// Framework thread only; call <see cref="Frame"/> first thing in every update. Steady frames allocate nothing.
/// Diagnostic for users to report back: does a GC pause stop the picture, and how much of a long gap is the game's.
/// </remarks>
internal sealed class FrameIntervalMonitor
{
    private static readonly long DefaultSummaryTicks = 3600 * Stopwatch.Frequency;

    private readonly double thresholdMs;
    private readonly HitchCooldown cooldown;
    private readonly long summaryTicks;

    private long lastTimestamp;
    private HitchCounters lastCounters;
    private GameScreen lastScreen;
    private bool lastInFront;

    private long summaryStart;
    private HiddenCount loading;
    private HiddenCount loggedOut;
    private HiddenCount behind;

    /// <summary>Initializes a new instance of the <see cref="FrameIntervalMonitor"/> class.</summary>
    /// <param name="thresholdMs">A gap longer than this is logged.</param>
    /// <param name="cooldownTicks">Least time between two lines, in <see cref="Stopwatch"/> ticks.</param>
    public FrameIntervalMonitor(double thresholdMs, long cooldownTicks)
        : this(thresholdMs, cooldownTicks, DefaultSummaryTicks)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FrameIntervalMonitor"/> class.</summary>
    /// <param name="thresholdMs">A gap longer than this is logged.</param>
    /// <param name="cooldownTicks">Least time between two lines, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="summaryTicks">Window of the line about gaps the player could not see, in <see cref="Stopwatch"/> ticks.</param>
    public FrameIntervalMonitor(double thresholdMs, long cooldownTicks, long summaryTicks)
    {
        this.thresholdMs = thresholdMs;
        this.cooldown = new HitchCooldown(cooldownTicks);
        this.summaryTicks = summaryTicks;
    }

    /// <summary>Gets the number of lines written.</summary>
    public int LinesWritten { get; private set; }

    /// <summary>Gets what a line tells about the time between the two updates; null leaves that part out.</summary>
    public FrameGapAttribution? GapAttribution { get; init; }

    /// <summary>Gets that part of the last line or kept detail, or null.</summary>
    internal string? LastGapText { get; private set; }

    /// <summary>Takes the start of one framework update.</summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="counters">Runtime counters read now, on the framework thread.</param>
    /// <param name="screen">What the game shows now.</param>
    /// <param name="inFront">Whether the game window was in front when last sampled.</param>
    /// <param name="previousUpdateTicks">How long Dalamud and plugins took in the previous update.</param>
    /// <param name="previousGameTickTicks">How long the game's own framework tick took in the previous update.</param>
    /// <param name="inFrontNow">Asked only for a gap over the threshold seen at both ends: is the game window in front right now.</param>
    public void Frame(long now, in HitchCounters counters, GameScreen screen, bool inFront, long previousUpdateTicks, long previousGameTickTicks, Func<bool> inFrontNow)
    {
        if (this.lastTimestamp == 0)
        {
            this.summaryStart = now;
        }
        else
        {
            var wrote = false;
            var gapMs = HitchCounters.Milliseconds(now - this.lastTimestamp);
            if (gapMs > this.thresholdMs)
            {
                var visible = this.lastScreen == GameScreen.InGame && this.lastInFront && screen == GameScreen.InGame && inFront;
                if (visible && inFrontNow())
                    wrote = this.Visible(now, now - this.lastTimestamp, gapMs, counters, previousUpdateTicks, previousGameTickTicks);
                else
                    this.Hidden(screen, gapMs);
            }

            if (!wrote && this.cooldown.FlushDue(now))
                this.WriteLeftOut();
        }

        if (now - this.summaryStart >= this.summaryTicks)
            this.WriteHiddenSummary(now);

        this.lastTimestamp = now;
        this.lastCounters = counters;
        this.lastScreen = screen;
        this.lastInFront = inFront;
    }

    private bool Visible(long now, long gapTicks, double gapMs, in HitchCounters counters, long updateTicks, long gameTickTicks)
    {
        var during = counters.Since(this.lastCounters);
        if (this.cooldown.TryLog(now, gapMs, out var leftOut, out var leftOutMaxMs, out var leftOutDetail, out var wantDetail))
        {
            this.Write(gapMs, during, updateTicks, gameTickTicks, leftOut, leftOutMaxMs, leftOutDetail, this.DescribeGap(gapTicks, updateTicks));
            return true;
        }

        if (wantDetail)
        {
            var text = HitchDetector.DescribeEnded(during, "the framework thread") + string.Create(
                CultureInfo.InvariantCulture,
                $"; the update before it: Dalamud and plugins {HitchCounters.Milliseconds(updateTicks):0.0} ms, the game's framework tick {HitchCounters.Milliseconds(gameTickTicks):0.0} ms");
            if (this.DescribeGap(gapTicks, updateTicks) is { } gap)
                text += "; " + gap;
            if (PluginLoadReport.DescribeLoading() is { } loadingNow)
                text += "; " + loadingNow;
            this.cooldown.KeepDetail(text + ")");
        }

        return false;
    }

    // A loading screen at either end explains the gap best, then being logged out, then the window behind.
    private void Hidden(GameScreen screen, double gapMs)
    {
        if (this.lastScreen == GameScreen.Loading || screen == GameScreen.Loading)
            this.loading.Add(gapMs);
        else if (this.lastScreen == GameScreen.Title || screen == GameScreen.Title)
            this.loggedOut.Add(gapMs);
        else
            this.behind.Add(gapMs);
    }

    private void WriteHiddenSummary(long now)
    {
        var count = this.loading.Count + this.loggedOut.Count + this.behind.Count;
        if (count > 0)
        {
            Log.Information(
                "[FRAMEGAP] Gaps over {Max}ms the player could not see in the last {Minutes:0} min, not logged one by one: {Count} (longest {LongestMs:0.0} ms); loading screen {LoadingCount} (longest {LoadingMs:0.0} ms), not logged in {LoggedOutCount} (longest {LoggedOutMs:0.0} ms), game window not in front {BehindCount} (longest {BehindMs:0.0} ms)",
                this.thresholdMs,
                HitchCounters.Milliseconds(now - this.summaryStart) / 60_000,
                count,
                Math.Max(this.loading.LongestMs, Math.Max(this.loggedOut.LongestMs, this.behind.LongestMs)),
                this.loading.Count,
                this.loading.LongestMs,
                this.loggedOut.Count,
                this.loggedOut.LongestMs,
                this.behind.Count,
                this.behind.LongestMs);
        }

        this.summaryStart = now;
        this.loading = default;
        this.loggedOut = default;
        this.behind = default;
    }

    private void WriteLeftOut()
    {
        this.cooldown.TakeFlush(out var leftOut, out var leftOutMaxMs, out var leftOutDetail);
        this.LinesWritten++;
        Log.Warning(
            "[FRAMEGAP] Long FrameInterval: {LeftOut} more over {Max}ms left out in the {Cooldown} s after the last line, reported as that cooldown ended (longest {LeftOutMaxMs:0.0} ms){Extra:l}",
            leftOut,
            this.thresholdMs,
            this.cooldown.CooldownSeconds,
            leftOutMaxMs,
            leftOutDetail == null ? HitchDetector.NoDetail : "; longest left out " + leftOutDetail);
    }

    private string? DescribeGap(long gapTicks, long updateTicks)
    {
        var text = this.GapAttribution?.Describe(gapTicks, updateTicks);
        this.LastGapText = text;
        return text;
    }

    private void Write(double gapMs, in HitchCounters during, long updateTicks, long gameTickTicks, int leftOut, double leftOutMaxMs, string? leftOutDetail, string? gap)
    {
        this.LinesWritten++;
        Log.Warning(
            "[FRAMEGAP] Long FrameInterval detected, {Total}ms > {Max}ms between two framework updates (logged in, not loading, game window in front); the update before it: Dalamud and plugins {UpdateMs:0.0} ms, the game's framework tick {GameTickMs:0.0} ms. During it: GC paused {GcPauseMs:0.0} ms, collections gen0 +{Gen0} gen1 +{Gen1} gen2 +{Gen2}; JIT on the framework thread {JitMs:0.0} ms for {JitMethods} methods; {LeftOut} more over the limit left out since the last line (longest {LeftOutMaxMs:0.0} ms){Gap:l}{Extra:l}",
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
            leftOutMaxMs,
            gap == null ? string.Empty : "; " + gap,
            HitchDetector.Extra(leftOutDetail, PluginLoadReport.DescribeLoading()));
    }

    private struct HiddenCount
    {
        public int Count { get; private set; }

        public double LongestMs { get; private set; }

        public void Add(double gapMs)
        {
            this.Count++;
            if (gapMs > this.LongestMs)
                this.LongestMs = gapMs;
        }
    }
}
