using System.Diagnostics;
using System.Runtime;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Runtime counters read at the start of a timed span, to tell how much of a long span went to GC and to the JIT.
/// </summary>
/// <remarks>
/// GC numbers are process wide; JIT numbers are for the thread that read them, so both ends of a span must be read
/// on the same thread. Reading allocates nothing.
/// </remarks>
internal readonly struct HitchCounters
{
    private readonly long gcPauseTicks;
    private readonly long jitTicks;

    private HitchCounters(long gcPauseTicks, int gen0, int gen1, int gen2, long jitTicks, long jitMethods)
    {
        this.gcPauseTicks = gcPauseTicks;
        this.Gen0 = gen0;
        this.Gen1 = gen1;
        this.Gen2 = gen2;
        this.jitTicks = jitTicks;
        this.JitMethods = jitMethods;
    }

    /// <summary>Gets the GC pause time in milliseconds.</summary>
    public double GcPauseMs => TimeSpan.FromTicks(this.gcPauseTicks).TotalMilliseconds;

    /// <summary>Gets the gen0 collection count.</summary>
    public int Gen0 { get; }

    /// <summary>Gets the gen1 collection count.</summary>
    public int Gen1 { get; }

    /// <summary>Gets the gen2 collection count.</summary>
    public int Gen2 { get; }

    /// <summary>Gets the JIT compilation time of the reading thread in milliseconds.</summary>
    public double JitMs => TimeSpan.FromTicks(this.jitTicks).TotalMilliseconds;

    /// <summary>Gets the number of methods the reading thread compiled.</summary>
    public long JitMethods { get; }

    /// <summary>Reads the counters now.</summary>
    /// <returns>The counters.</returns>
    public static HitchCounters Read() => new(
        GC.GetTotalPauseDuration().Ticks,
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        JitInfo.GetCompilationTime(currentThread: true).Ticks,
        JitInfo.GetCompiledMethodCount(currentThread: true));

    /// <summary>Converts <see cref="Stopwatch"/> ticks to milliseconds, at the 100 ns resolution of <see cref="Stopwatch.Elapsed"/>.</summary>
    /// <param name="stopwatchTicks">Difference of two <see cref="Stopwatch.GetTimestamp"/> values.</param>
    /// <returns>The milliseconds, printed like the hitch lines always were (51.7299).</returns>
    public static double Milliseconds(long stopwatchTicks) =>
        new TimeSpan((long)(stopwatchTicks * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency))).TotalMilliseconds;

    /// <summary>Gets what changed from <paramref name="start"/> to this reading.</summary>
    /// <param name="start">An earlier reading on the same thread.</param>
    /// <returns>The differences.</returns>
    public HitchCounters Since(in HitchCounters start) => new(
        this.gcPauseTicks - start.gcPauseTicks,
        this.Gen0 - start.Gen0,
        this.Gen1 - start.Gen1,
        this.Gen2 - start.Gen2,
        this.jitTicks - start.jitTicks,
        this.JitMethods - start.JitMethods);
}
