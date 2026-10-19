using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

using Dalamud.Logging.Internal;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Logs what each plugin load allocated and how long it took, one line per load, and a ranked summary per batch.
/// </summary>
/// <remarks>
/// Thread safe: loads finish on any thread and in parallel. Nothing here throws into the load path, and nothing
/// waits except for a short lock around the open batches. Diagnostic for users to report back: who allocates
/// at game start and on updates.
/// </remarks>
internal static class PluginLoadReport
{
    /// <summary>How many plugins the batch summary names; the rest are summed up.</summary>
    internal const int TopCount = 15;

    private const double BytesPerMegabyte = 1024 * 1024;

    private static readonly ModuleLog Log = new("PerfDiag");
    private static readonly object Gate = new();
    private static readonly List<Batch> OpenBatches = [];

    /// <summary>
    /// How a load ended.
    /// </summary>
    internal enum Outcome
    {
        /// <summary>The constructor returned.</summary>
        Loaded,

        /// <summary>The constructor threw.</summary>
        ConstructorThrew,

        /// <summary>The load failed before the constructor ran.</summary>
        ConstructorDidNotRun,
    }

    /// <summary>
    /// Takes the readings at the start of a load. Cannot throw.
    /// </summary>
    /// <returns>The readings.</returns>
    public static Probe Start() => new(Stopwatch.GetTimestamp(), GC.GetTotalAllocatedBytes(false));

    /// <summary>
    /// Opens a batch: every load reported until <see cref="Batch.End"/> goes into its summary.
    /// </summary>
    /// <param name="name">Name of the batch in the summary, for example "boot".</param>
    /// <returns>The batch; ending it logs the summary.</returns>
    public static Batch BeginBatch(string name)
    {
        var batch = new Batch(name);
        lock (Gate)
            OpenBatches.Add(batch);
        return batch;
    }

    /// <summary>
    /// Logs one load and adds it to the open batches. Never throws.
    /// </summary>
    /// <param name="internalName">Internal name of the plugin.</param>
    /// <param name="version">Version of the plugin.</param>
    /// <param name="start">What <see cref="Start"/> returned when the load began.</param>
    /// <param name="constructor">What the constructor cost; not run when the load failed before it.</param>
    /// <param name="constructorThrew">Whether the load failed in or after the constructor.</param>
    public static void Report(string internalName, Version? version, Probe start, ConstructorCost? constructor, bool constructorThrew)
    {
        try
        {
            var ran = constructor is { Ran: true };
            var entry = new Entry(
                internalName,
                version?.ToString(),
                !ran ? Outcome.ConstructorDidNotRun : constructorThrew ? Outcome.ConstructorThrew : Outcome.Loaded,
                ran ? constructor!.ThreadBytes : 0,
                ran ? constructor!.Ticks : 0,
                Stopwatch.GetTimestamp() - start.Timestamp,
                GC.GetTotalAllocatedBytes(false) - start.ProcessBytes);

            Log.Information("{Line:l}", DescribeLoad(entry));
            lock (Gate)
            {
                foreach (var batch in OpenBatches)
                    batch.Add(entry);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not report a plugin load");
        }
    }

    /// <summary>
    /// Describes one load, as logged.
    /// </summary>
    /// <param name="entry">The load.</param>
    /// <returns>The log line without the module prefix.</returns>
    internal static string DescribeLoad(in Entry entry)
    {
        var who = $"{entry.Name ?? "?"} {entry.Version ?? "?"}";
        var constructor = entry.Outcome switch
        {
            Outcome.Loaded => Invariant($"constructor allocated {Megabytes(entry.ConstructorBytes):0.0} MB on its thread in {Milliseconds(entry.ConstructorTicks):0} ms"),
            Outcome.ConstructorThrew => Invariant($"FAILED, constructor allocated {Megabytes(entry.ConstructorBytes):0.0} MB on its thread in {Milliseconds(entry.ConstructorTicks):0} ms"),
            _ => "FAILED before its constructor ran",
        };
        return Invariant(
            $"Plugin load: {who} {constructor}; whole load {Milliseconds(entry.LoadTicks):0} ms, process +{Megabytes(entry.ProcessBytes):0.0} MB meanwhile (an upper bound: includes plugins loading in parallel and background work)");
    }

    /// <summary>
    /// Orders loads for the summary: most bytes allocated by the constructor on its thread first, ties by name.
    /// </summary>
    /// <param name="entries">The loads.</param>
    /// <returns>All loads, in summary order.</returns>
    internal static List<Entry> Rank(IEnumerable<Entry> entries) =>
        entries.OrderByDescending(x => x.ConstructorBytes).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Describes a finished batch, as logged: totals over every load, the biggest by name, the rest summed up.
    /// </summary>
    /// <param name="name">Name of the batch.</param>
    /// <param name="entries">Every load of the batch.</param>
    /// <param name="elapsedTicks">How long the batch was open, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="processBytes">What the process allocated while the batch was open.</param>
    /// <param name="top">How many loads to name.</param>
    /// <returns>The log line without the module prefix.</returns>
    internal static string DescribeBatch(string name, IReadOnlyCollection<Entry> entries, long elapsedTicks, long processBytes, int top)
    {
        var ranked = Rank(entries);
        var named = Math.Max(0, Math.Min(top, ranked.Count));
        var text = new StringBuilder();
        text.Append(Invariant(
            $"Plugin load batch \"{name}\": {ranked.Count} plugins in {elapsedTicks / (double)Stopwatch.Frequency:0.0} s ({ranked.Count(x => x.Outcome != Outcome.Loaded)} failed); constructors allocated {Megabytes(ranked.Sum(x => x.ConstructorBytes)):0.0} MB on their threads, the process {Megabytes(processBytes):0.0} MB meanwhile. By constructor allocation:"));
        for (var i = 0; i < named; i++)
        {
            var x = ranked[i];
            text.Append(i == 0 ? " " : ", ");
            text.Append(Invariant($"{x.Name ?? "?"} {Megabytes(x.ConstructorBytes):0.0} MB/{Milliseconds(x.ConstructorTicks):0} ms"));
            if (x.Outcome != Outcome.Loaded)
                text.Append(" (failed)");
        }

        var rest = ranked.Count - named;
        if (rest > 0)
            text.Append(Invariant($"; the other {rest} plugins {Megabytes(ranked.Skip(named).Sum(x => x.ConstructorBytes)):0.0} MB together"));
        return text.ToString();
    }

    private static double Megabytes(long bytes) => bytes / BytesPerMegabyte;

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Readings at the start of a load.
    /// </summary>
    /// <param name="Timestamp">The <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="ProcessBytes">What the process had allocated.</param>
    internal readonly record struct Probe(long Timestamp, long ProcessBytes);

    /// <summary>
    /// One reported load.
    /// </summary>
    /// <param name="Name">Internal name of the plugin.</param>
    /// <param name="Version">Version of the plugin.</param>
    /// <param name="Outcome">How the load ended.</param>
    /// <param name="ConstructorBytes">Bytes the constructor allocated on its own thread.</param>
    /// <param name="ConstructorTicks">How long the constructor ran, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="LoadTicks">How long the whole load took, in <see cref="Stopwatch"/> ticks.</param>
    /// <param name="ProcessBytes">What the whole process allocated during the load.</param>
    internal readonly record struct Entry(
        string? Name, string? Version, Outcome Outcome, long ConstructorBytes, long ConstructorTicks, long LoadTicks, long ProcessBytes);

    /// <summary>
    /// What a plugin constructor cost on the thread that ran it.
    /// </summary>
    /// <remarks>
    /// Written once by the constructor's thread; read after awaiting the task that ran it, which orders the reads.
    /// </remarks>
    internal sealed class ConstructorCost
    {
        /// <summary>Gets a value indicating whether the constructor ran, returning or throwing.</summary>
        public bool Ran { get; private set; }

        /// <summary>Gets the bytes the constructor allocated on its thread.</summary>
        public long ThreadBytes { get; private set; }

        /// <summary>Gets how long the constructor ran, in <see cref="Stopwatch"/> ticks.</summary>
        public long Ticks { get; private set; }

        /// <summary>
        /// Records the cost. Only the thread that ran the constructor.
        /// </summary>
        /// <param name="threadBytes">Bytes allocated on the thread.</param>
        /// <param name="ticks">How long it ran, in <see cref="Stopwatch"/> ticks.</param>
        public void Record(long threadBytes, long ticks)
        {
            this.ThreadBytes = threadBytes;
            this.Ticks = ticks;
            this.Ran = true;
        }
    }

    /// <summary>
    /// Loads reported while it was open; ending it logs the summary once.
    /// </summary>
    internal sealed class Batch : IDisposable
    {
        private readonly string name;
        private readonly Probe start = Start();
        private readonly List<Entry> entries = [];
        private int ended;

        /// <summary>
        /// Initializes a new instance of the <see cref="Batch"/> class.
        /// </summary>
        /// <param name="name">Name of the batch.</param>
        internal Batch(string name) => this.name = name;

        /// <summary>
        /// Closes the batch and logs its summary unless no load was reported. Only the first call does anything;
        /// never throws.
        /// </summary>
        /// <returns>The logged line, or null when nothing was logged.</returns>
        public string? End()
        {
            if (Interlocked.Exchange(ref this.ended, 1) != 0)
                return null;

            try
            {
                Entry[] taken;
                lock (Gate)
                {
                    OpenBatches.Remove(this);
                    taken = [.. this.entries];
                }

                if (taken.Length == 0)
                    return null;

                var line = DescribeBatch(
                    this.name,
                    taken,
                    Stopwatch.GetTimestamp() - this.start.Timestamp,
                    GC.GetTotalAllocatedBytes(false) - this.start.ProcessBytes,
                    TopCount);
                Log.Information("{Line:l}", line);
                return line;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not report a plugin load batch");
                return null;
            }
        }

        /// <inheritdoc cref="End"/>
        public void Dispose() => this.End();

        /// <summary>Adds a load. Only under <see cref="Gate"/>.</summary>
        /// <param name="entry">The load.</param>
        internal void Add(Entry entry) => this.entries.Add(entry);
    }
}
