using System.Diagnostics;

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
            return;

        if (this.cooldown.TryLog(now, milliseconds, out var leftOut, out var leftOutMaxMs))
            this.Write(elapsed, milliseconds, leftOut, leftOutMaxMs);
    }

    private void Write(long elapsed, double milliseconds, int leftOut, double leftOutMaxMs)
    {
        var during = HitchCounters.Read().Since(this.startCounters);
        if (this.describeSlowest == null)
        {
            Log.Warning(
                "[HITCH] Long {Name} detected, {Total}ms > {Max}ms - check in the plugin stats window. During it: GC paused {GcPauseMs:0.0} ms, collections gen0 +{Gen0} gen1 +{Gen1} gen2 +{Gen2}; JIT on this thread {JitMs:0.0} ms for {JitMethods} methods; {LeftOut} more over the limit left out since the last line (longest {LeftOutMaxMs:0.0} ms)",
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
                leftOutMaxMs);
            return;
        }

        Log.Warning(
            "[HITCH] Long {Name} detected, {Total}ms > {Max}ms - check in the plugin stats window. During it: GC paused {GcPauseMs:0.0} ms, collections gen0 +{Gen0} gen1 +{Gen1} gen2 +{Gen2}; JIT on this thread {JitMs:0.0} ms for {JitMethods} methods; {LeftOut} more over the limit left out since the last line (longest {LeftOutMaxMs:0.0} ms); slowest: {Slowest}",
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
            this.describeSlowest(elapsed));
    }
}
