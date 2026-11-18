using System.Diagnostics;
using System.Globalization;

using Dalamud.Utility.Internal;
using Serilog;

namespace Dalamud.Utility;

/// <summary>
/// Utility class to detect hitches.
/// </summary>
/// <remarks>
/// One thread per instance. A logged line also tells how much of the span went to GC pauses and to this thread's
/// JIT, and how many spans over the limit the cooldown left out since the previous line.
/// </remarks>
public class HitchDetector
{
    /// <summary>What a cooldown-end line says when none of the left-out spans was long enough to keep its detail.</summary>
    internal static readonly string NoDetail = FormattableString.Invariant($"; no detail kept, it is kept from {HitchCooldown.DefaultDetailMs:0} ms");

    private static readonly long CooldownTicks = 30 * Stopwatch.Frequency;

    private readonly string name;
    private readonly double millisecondsMax;
    private readonly HitchCooldown cooldown = new(CooldownTicks);
    private readonly Func<long, string>? describeSlowest;

    private long startTimestamp;
    private HitchCounters startCounters;

    /// <summary>
    /// Initializes a new instance of the <see cref="HitchDetector"/> class.
    /// </summary>
    /// <param name="name">Name to log.</param>
    /// <param name="millisecondsMax">Milliseconds to print a warning for.</param>
    public HitchDetector(string name, double millisecondsMax = 20)
        : this(name, millisecondsMax, null)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="HitchDetector"/> class.
    /// </summary>
    /// <param name="name">Name to log.</param>
    /// <param name="millisecondsMax">Milliseconds to print a warning for.</param>
    /// <param name="describeSlowest">
    /// Called only for a line being written, on the measuring thread, with the span's length in
    /// <see cref="Stopwatch"/> ticks; returns what took the time. It must not throw.
    /// </param>
    internal HitchDetector(string name, double millisecondsMax, Func<long, string>? describeSlowest)
    {
        this.name = name;
        this.millisecondsMax = millisecondsMax;
        this.describeSlowest = describeSlowest;
    }

    /// <summary>
    /// Gets the length of the last span that was stopped, in <see cref="Stopwatch"/> ticks.
    /// </summary>
    internal long LastElapsedTicks { get; private set; }

    /// <summary>
    /// Start the time tracking.
    /// </summary>
    public void Start()
    {
        this.startCounters = HitchCounters.Read();
        this.startTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Stop the time tracking, and print a warning, if applicable.
    /// </summary>
    public void Stop()
    {
        // Never started: nothing was measured.
        if (this.startTimestamp == 0)
            return;

        var now = Stopwatch.GetTimestamp();
        var elapsed = now - this.startTimestamp;
        this.LastElapsedTicks = elapsed;

        var milliseconds = HitchCounters.Milliseconds(elapsed);
        if (milliseconds <= this.millisecondsMax)
        {
            if (this.cooldown.FlushDue(now))
                this.WriteLeftOut();
            return;
        }

        if (this.cooldown.TryLog(now, milliseconds, out var leftOut, out var leftOutMaxMs, out var leftOutDetail, out var wantDetail))
            this.Write(elapsed, milliseconds, leftOut, leftOutMaxMs, leftOutDetail);
        else if (wantDetail)
            this.cooldown.KeepDetail(this.DescribeLeftOut(elapsed));
    }

    /// <summary>Appends the parts a hitch line has only sometimes: the longest left out, and plugin loads under way.</summary>
    /// <param name="leftOutDetail">The kept detail of the longest left out, or null.</param>
    /// <param name="loading">What <see cref="PluginLoadReport.DescribeLoading"/> returned, or null.</param>
    /// <returns>Empty when neither, so the line reads as it always did.</returns>
    internal static string Extra(string? leftOutDetail, string? loading)
    {
        if (leftOutDetail == null && loading == null)
            return string.Empty;

        var text = leftOutDetail == null ? string.Empty : "; longest left out " + leftOutDetail;
        return loading == null ? text : text + "; " + loading;
    }

    /// <summary>Describes the counters of a span, as the hitch lines print them, for a left-out span.</summary>
    /// <param name="during">The counters over the span.</param>
    /// <param name="thread">Whose JIT, as printed: "this thread" or "the framework thread".</param>
    /// <returns>Like <c>ended at 15:57:21.832 (GC paused 117.1 ms, …; JIT on this thread 13.5 ms for 120 methods</c>, no closing parenthesis.</returns>
    internal static string DescribeEnded(in HitchCounters during, string thread) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"ended at {DateTime.Now:HH:mm:ss.fff} (GC paused {during.GcPauseMs:0.0} ms, collections gen0 +{during.Gen0} gen1 +{during.Gen1} gen2 +{during.Gen2}; JIT on {thread} {during.JitMs:0.0} ms for {during.JitMethods} methods");

    // Right at Stop: the next Start overwrites the counters, the next frame the attribution.
    private string DescribeLeftOut(long elapsed)
    {
        var text = DescribeEnded(HitchCounters.Read().Since(this.startCounters), "this thread");
        if (this.describeSlowest != null)
            text += "; slowest: " + this.describeSlowest(elapsed);
        if (PluginLoadReport.DescribeLoading() is { } loading)
            text += "; " + loading;
        return text + ")";
    }

    private void WriteLeftOut()
    {
        this.cooldown.TakeFlush(out var leftOut, out var leftOutMaxMs, out var leftOutDetail);
        Log.Warning(
            "[HITCH] Long {Name}: {LeftOut} more over {Max}ms left out in the {Cooldown} s after the last line, reported as that cooldown ended (longest {LeftOutMaxMs:0.0} ms){Extra:l}",
            this.name,
            leftOut,
            this.millisecondsMax,
            this.cooldown.CooldownSeconds,
            leftOutMaxMs,
            leftOutDetail == null ? NoDetail : "; longest left out " + leftOutDetail);
    }

    private void Write(long elapsed, double milliseconds, int leftOut, double leftOutMaxMs, string? leftOutDetail)
    {
        var during = HitchCounters.Read().Since(this.startCounters);
        var extra = Extra(leftOutDetail, PluginLoadReport.DescribeLoading());
        if (this.describeSlowest == null)
        {
            Log.Warning(
                "[HITCH] Long {Name} detected, {Total}ms > {Max}ms - check in the plugin stats window. During it: GC paused {GcPauseMs:0.0} ms, collections gen0 +{Gen0} gen1 +{Gen1} gen2 +{Gen2}; JIT on this thread {JitMs:0.0} ms for {JitMethods} methods; {LeftOut} more over the limit left out since the last line (longest {LeftOutMaxMs:0.0} ms){Extra:l}",
                this.name,
                milliseconds,
                this.millisecondsMax,
                during.GcPauseMs,
                during.Gen0,
                during.Gen1,
                during.Gen2,
                during.JitMs,
                during.JitMethods,
                leftOut,
                leftOutMaxMs,
                extra);
            return;
        }

        Log.Warning(
            "[HITCH] Long {Name} detected, {Total}ms > {Max}ms - check in the plugin stats window. During it: GC paused {GcPauseMs:0.0} ms, collections gen0 +{Gen0} gen1 +{Gen1} gen2 +{Gen2}; JIT on this thread {JitMs:0.0} ms for {JitMethods} methods; {LeftOut} more over the limit left out since the last line (longest {LeftOutMaxMs:0.0} ms); slowest: {Slowest}{Extra:l}",
            this.name,
            milliseconds,
            this.millisecondsMax,
            during.GcPauseMs,
            during.Gen0,
            during.Gen1,
            during.Gen2,
            during.JitMs,
            during.JitMethods,
            leftOut,
            leftOutMaxMs,
            this.describeSlowest(elapsed),
            extra);
    }
}
