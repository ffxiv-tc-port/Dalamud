using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Logging.Internal;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Logs, once per window, which IPC endpoints converted arguments or return values through JSON most often.
/// </summary>
/// <remarks>
/// Not thread safe: <see cref="Tick"/> and <see cref="ReportUnfinished"/> from one thread at a time; the counts come
/// from a <see cref="Counter"/>, which any thread may add to. Diagnostic for users to report back: a call gate whose
/// two ends declare different types serializes and parses JSON on every call, which shows up as Newtonsoft garbage.
/// </remarks>
internal sealed class IpcJsonConversionReport
{
    private const int MaxLines = 5;

    private static readonly ModuleLog Log = new("PerfDiag");

    private readonly Counter counter;
    private readonly long windowTicks;

    private long windowStart;
    private long windowEnd;

    /// <summary>
    /// Initializes a new instance of the <see cref="IpcJsonConversionReport"/> class.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="window">How often to report.</param>
    /// <param name="counter">Where the conversions are counted; emptied at each report, so the first window also holds what was counted before it.</param>
    public IpcJsonConversionReport(long now, TimeSpan window, Counter counter)
    {
        this.windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        this.counter = counter;
        this.StartWindow(now);
    }

    /// <summary>
    /// Writes one report line.
    /// </summary>
    /// <param name="messageTemplate">The message template.</param>
    /// <param name="values">Values to log.</param>
    internal delegate void ReportLine(string messageTemplate, params object?[] values);

    /// <summary>
    /// Which value went through JSON.
    /// </summary>
    internal enum Direction
    {
        /// <summary>An argument of a message sent to a subscriber.</summary>
        MessageArgument,

        /// <summary>An argument of a registered action.</summary>
        ActionArgument,

        /// <summary>An argument of a registered function.</summary>
        FunctionArgument,

        /// <summary>The value a registered function returned, converted to the type the caller asked for.</summary>
        FunctionResult,
    }

    /// <summary>
    /// Creates the hourly report of the counter that the call gates use.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <returns>The report.</returns>
    public static IpcJsonConversionReport Hourly(long now) => new(now, TimeSpan.FromHours(1), Counter.Shared);

    /// <summary>
    /// Reports when the window is over. Call it once per framework update.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void Tick(long now)
    {
        if (now < this.windowEnd)
            return;

        this.EndWindow(now, "over the last {0:0} min");
    }

    /// <summary>
    /// Reports the window so far, for a framework that is going away. The next window starts now.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void ReportUnfinished(long now) =>
        this.EndWindow(now, "over an unfinished window of {0:0.0} min, reported early because the framework is unloading");

    /// <summary>
    /// Writes the report of one window.
    /// </summary>
    /// <param name="counts">Conversions by endpoint, direction and types; only read.</param>
    /// <param name="window">The window, as "over the last 60 min".</param>
    /// <param name="seconds">How long the window was.</param>
    /// <param name="maxLines">How many keys to list, one line each.</param>
    /// <param name="write">Writes one line.</param>
    internal static void Report(IReadOnlyDictionary<Key, long> counts, string window, double seconds, int maxLines, ReportLine write)
    {
        var total = counts.Values.Sum();
        if (total == 0)
        {
            write("IPC JSON conversions {Window:l}: none", window);
            return;
        }

        write(
            "IPC JSON conversions {Window:l}: {Total} in total ({PerSecond:0.0} per second) over {Keys} endpoint, direction and type combinations",
            window,
            total,
            total / Math.Max(1, seconds),
            counts.Count);

        var rank = 0;
        foreach (var (key, count) in counts.OrderByDescending(x => x.Value).ThenBy(x => x.Key.Endpoint, StringComparer.Ordinal).Take(maxLines))
        {
            write(
                "IPC JSON conversions {Window:l}: #{Rank} {Endpoint:l} {Direction:l} {From:l} to {To:l}: {Count} times ({PerSecond:0.0} per second)",
                window,
                ++rank,
                key.Endpoint,
                Describe(key.Direction),
                key.From,
                key.To,
                count,
                count / Math.Max(1, seconds));
        }
    }

    private static string Describe(Direction direction) => direction switch
    {
        Direction.MessageArgument => "message argument",
        Direction.ActionArgument => "action argument",
        Direction.FunctionArgument => "function argument",
        Direction.FunctionResult => "function result",
        _ => direction.ToString(),
    };

    private void StartWindow(long now)
    {
        this.windowStart = now;
        this.windowEnd = now + this.windowTicks;
    }

    private void EndWindow(long now, string windowFormat)
    {
        var counts = this.counter.Take();
        var seconds = (now - this.windowStart) / (double)Stopwatch.Frequency;
        this.StartWindow(now);
        Report(counts, string.Format(System.Globalization.CultureInfo.InvariantCulture, windowFormat, seconds / 60), seconds, MaxLines, Log.Information);
    }

    /// <summary>
    /// What one count is kept under. Type names only, so that a count never keeps a plugin's types alive.
    /// </summary>
    /// <param name="Endpoint">The call gate name.</param>
    /// <param name="Direction">Which value went through JSON.</param>
    /// <param name="From">The name of the type the value had.</param>
    /// <param name="To">The name of the type it was converted to.</param>
    internal readonly record struct Key(string Endpoint, Direction Direction, string From, string To);

    /// <summary>
    /// Counts JSON conversions by endpoint, direction and types.
    /// </summary>
    /// <remarks>
    /// Any thread. Holds at most <see cref="MaxKeys"/> keys; past that, new keys are counted under
    /// <see cref="OtherEndpoints"/> with their direction. Counting an existing key allocates nothing.
    /// </remarks>
    internal sealed class Counter
    {
        /// <summary>How many keys are kept apart.</summary>
        public const int MaxKeys = 1024;

        /// <summary>The endpoint name new keys are counted under once the table is full.</summary>
        public const string OtherEndpoints = "(other endpoints)";

        private readonly object gate = new();
        private Dictionary<Key, long> counts = new();

        /// <summary>Gets the counter that the call gates use.</summary>
        public static Counter Shared { get; } = new();

        /// <summary>
        /// Counts one conversion.
        /// </summary>
        /// <param name="endpoint">The call gate name.</param>
        /// <param name="direction">Which value went through JSON.</param>
        /// <param name="from">The type the value had.</param>
        /// <param name="to">The type it was converted to.</param>
        public void Count(string endpoint, Direction direction, Type from, Type to)
        {
            var key = new Key(endpoint, direction, from.Name, to.Name);
            lock (this.gate)
            {
                if (this.counts.Count >= MaxKeys && !this.counts.ContainsKey(key))
                    key = new Key(OtherEndpoints, direction, "?", "?");
                CollectionsMarshal.GetValueRefOrAddDefault(this.counts, key, out _)++;
            }
        }

        /// <summary>
        /// Gets the counts so far and starts over.
        /// </summary>
        /// <returns>The counts; no longer touched by this counter.</returns>
        public Dictionary<Key, long> Take()
        {
            lock (this.gate)
            {
                var taken = this.counts;
                this.counts = new Dictionary<Key, long>();
                return taken;
            }
        }

        /// <summary>
        /// Gets the count of one key so far.
        /// </summary>
        /// <param name="key">The key.</param>
        /// <returns>The count; 0 when it was never counted.</returns>
        public long Peek(Key key)
        {
            lock (this.gate)
                return this.counts.GetValueOrDefault(key);
        }
    }
}
