using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

using Dalamud.Interface.Utility.Raii;
using Dalamud.Logging.Internal;

using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Samples the runtime's allocation ticks (one per about 100 KB allocated anywhere in the process) and logs, once per
/// window, how the sampled bytes split by thread and by type.
/// </summary>
/// <remarks>
/// Events arrive on the runtime's dispatch thread; one lock guards the tallies and is never held around logging.
/// Construct, <see cref="Tick"/> and <see cref="ReportNow"/> on the framework thread only: the constructing thread
/// counts as the main thread. Diagnostic: where the allocations outside plugin callbacks come from; runs only while
/// turned on in settings, since each allocation tick costs the allocating thread time.
/// </remarks>
internal sealed class AllocationSampler : EventListener
{
    /// <summary>How many (thread, type) pairs of other threads a window keeps apart; later pairs fold into "(other types)" of their thread.</summary>
    internal const int MaxThreadTypes = 4096;

    private const string RuntimeProvider = "Microsoft-Windows-DotNETRuntime";
    private const EventKeywords GcKeyword = (EventKeywords)0x1;
    private const int AllocationTickEventId = 10;
    private const int MaxTypes = 4096;
    private const int MaxThreadNames = 256;
    private const int MaxCachedThreads = 1024;
    private const long ThreadNameLifetimeMs = 10_000;
    private const double BytesPerMegabyte = 1024 * 1024;
    private const string OtherTypes = "(other types)";
    private const string UnknownType = "(unknown type)";
    private const uint ThreadQueryLimitedInformation = 0x0800;
    private const int MaxModulePath = 1024;
    private const int TypedOtherThreads = 3;
    private const int TypesPerOtherThread = 5;

    private static readonly ModuleLog Log = new("PerfDiag");

    private static volatile bool threadNamesUnavailable;

    // Field initializers run before the base constructor, which may already deliver events.
    private readonly object gate = new();
    private readonly uint mainThreadId = GetCurrentThreadId();
    private readonly ThreadNames threads = new(NewThreadNameLookUp(), ThreadNameLifetimeMs);
    private readonly TypeLabels labels = TypeLabels.ForDalamudImRaii(GeneratedTypeOwners.Start());
    private readonly long windowTicks;
    private readonly int maxTypeLines;
    private readonly int maxThreadLines;
    private readonly int maxMainThreadTypeLines;

    private Window current = new();
    private int payloadVersion = -1;
    private int amountIndex = -1;
    private int typeNameIndex = -1;
    private int typeIdIndex = -1;
    private volatile bool sourceEnabled;
    private volatile string? failure;
    private long windowStart;
    private long windowEnd;
    private long windowStartProcessBytes;
    private bool failureLogged;

    /// <summary>
    /// Initializes a new instance of the <see cref="AllocationSampler"/> class: top 8 types, 5 threads,
    /// 25 types by the main thread alone and 25 by the thread pool alone, and the top 5 types of each of the
    /// top 3 other threads, once an hour.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public AllocationSampler(long now)
        : this(now, TimeSpan.FromHours(1), 8, 5, 25)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AllocationSampler"/> class and starts sampling.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="window">How often to report.</param>
    /// <param name="maxTypeLines">How many types to list, one line each.</param>
    /// <param name="maxThreadLines">How many other threads to list, one line each.</param>
    /// <param name="maxMainThreadTypeLines">How many types to list again by their main-thread bytes, and as many by their thread-pool bytes; 0 = none.</param>
    public AllocationSampler(long now, TimeSpan window, int maxTypeLines, int maxThreadLines, int maxMainThreadTypeLines)
    {
        this.windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        this.maxTypeLines = Math.Max(1, maxTypeLines);
        this.maxThreadLines = Math.Max(0, maxThreadLines);
        this.maxMainThreadTypeLines = Math.Max(0, maxMainThreadTypeLines);
        this.windowStart = now;
        this.windowEnd = now + this.windowTicks;
        this.windowStartProcessBytes = GC.GetTotalAllocatedBytes(false);
    }

    /// <summary>
    /// Reads the name of a thread.
    /// </summary>
    /// <param name="osThreadId">The OS thread id.</param>
    /// <param name="known">The name read for this id last time, if any; handed back as is when it still applies.</param>
    /// <returns>The name, <see cref="ThreadNames.Unnamed"/>, or <see cref="ThreadNames.Exited"/> when no thread has this id now.</returns>
    internal delegate string ThreadNameLookUp(uint osThreadId, string? known);

    /// <summary>
    /// Reads the Win32 start address of a thread.
    /// </summary>
    /// <param name="osThreadId">The OS thread id.</param>
    /// <param name="start">The start address.</param>
    /// <returns>Whether it was read.</returns>
    internal delegate bool StartAddressRead(uint osThreadId, out nuint start);

    /// <summary>
    /// Writes one report line.
    /// </summary>
    /// <param name="messageTemplate">The message template.</param>
    /// <param name="values">Values to log.</param>
    internal delegate void ReportLine(string messageTemplate, params object?[] values);

    /// <summary>
    /// The kind of thread a sample was allocated on.
    /// </summary>
    internal enum ThreadKind
    {
        /// <summary>The thread that constructed the sampler.</summary>
        Main,

        /// <summary>A thread pool thread.</summary>
        Pool,

        /// <summary>Any other thread.</summary>
        Other,

        /// <summary>The thread that delivers the sampler's events.</summary>
        Self,
    }

    /// <summary>
    /// Gets a value indicating whether sampling broke; it then stays off, and the next <see cref="Tick"/> logs why once.
    /// </summary>
    public bool Failed => this.failure is not null;

    /// <summary>
    /// Reports when the window is over. Call it once per framework update.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void Tick(long now)
    {
        this.LogFailureOnce();
        if (now < this.windowEnd)
            return;

        this.EndWindow(now);
    }

    /// <summary>
    /// Reports the window so far without waiting for it to end, as when sampling is being turned off; the next window
    /// starts now. Does nothing once sampling has failed.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void ReportNow(long now)
    {
        this.LogFailureOnce();
        if (this.failure is null)
            this.EndWindow(now);
    }

    /// <summary>
    /// <see cref="ReportNow"/> for a framework that is going away: one line in front says that the window is
    /// unfinished and how long it was.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void ReportUnfinished(long now)
    {
        this.LogFailureOnce();
        if (this.failure is not null)
            return;

        Log.Information(
            "Allocation samples of an unfinished window, {Minutes:0.0} min, reported early because the framework is unloading:",
            (now - this.windowStart) / (double)Stopwatch.Frequency / 60);
        this.EndWindow(now);
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        base.Dispose();
        this.labels.Owners?.Dispose();
    }

    /// <summary>
    /// Picks the types to list for the main thread alone: most main-thread bytes first, none without any.
    /// </summary>
    /// <param name="types">Tallies by type name; only read.</param>
    /// <param name="count">How many to pick at most.</param>
    /// <returns>The picked types, in listing order.</returns>
    internal static List<KeyValuePair<string, TypeTally>> TopByMainThread(IEnumerable<KeyValuePair<string, TypeTally>> types, int count) =>
        types.Where(x => x.Value.Main > 0).OrderByDescending(x => x.Value.Main).Take(count).ToList();

    /// <summary>
    /// Picks the types to list for the thread pool alone: most thread-pool bytes first, none without any.
    /// </summary>
    /// <param name="types">Tallies by type name; only read.</param>
    /// <param name="count">How many to pick at most.</param>
    /// <returns>The picked types, in listing order.</returns>
    internal static List<KeyValuePair<string, TypeTally>> TopByPool(IEnumerable<KeyValuePair<string, TypeTally>> types, int count) =>
        types.Where(x => x.Value.Pool > 0).OrderByDescending(x => x.Value.Pool).Take(count).ToList();

    /// <summary>
    /// Writes the report of one window.
    /// </summary>
    /// <param name="window">The finished window; only read.</param>
    /// <param name="minutes">How long the window was.</param>
    /// <param name="processBytes">What the whole process allocated in the window.</param>
    /// <param name="sourceEnabled">Whether the runtime event source was found.</param>
    /// <param name="limits">How many lines each table may have: types, other threads, types per thread kind; and how many
    /// other threads get their own types listed, with how many types each.</param>
    /// <param name="write">Writes one line.</param>
    internal static void Report(
        Window window,
        double minutes,
        long processBytes,
        bool sourceEnabled,
        (int Types, int Threads, int PerKind, int TypedThreads, int TypesPerThread) limits,
        ReportLine write)
    {
        if (window.Samples == 0)
        {
            write(
                "Allocation samples over the last {Minutes:0} min: none received (runtime event source {SourceState}, {OtherEvents} other runtime events); the process allocated {ProcessMb:0.0} MB",
                minutes,
                sourceEnabled ? "enabled" : "not found",
                window.OtherEvents,
                Megabytes(processBytes));
            return;
        }

        var typed = window.Bytes - window.Self;
        write(
            "Allocation samples over the last {Minutes:0} min: {SampledMb:0.0} MB in {Samples} samples (the process allocated {ProcessMb:0.0} MB); main thread {MainMb:0.0} MB ({MainPct:0}%), thread pool {PoolMb:0.0} MB ({PoolPct:0}%), other threads {OtherMb:0.0} MB ({OtherPct:0}%), this sampler {SelfMb:0.0} MB ({SelfPct:0.0}%); {OtherEvents} other runtime events",
            minutes,
            Megabytes(window.Bytes),
            window.Samples,
            Megabytes(processBytes),
            Megabytes(window.Main),
            PercentOf(window.Main, window.Bytes),
            Megabytes(window.Pool),
            PercentOf(window.Pool, window.Bytes),
            Megabytes(window.Other),
            PercentOf(window.Other, window.Bytes),
            Megabytes(window.Self),
            PercentOf(window.Self, window.Bytes),
            window.OtherEvents);

        var rank = 0;
        foreach (var (type, tally) in window.Types.OrderByDescending(x => x.Value.Total).Take(limits.Types))
        {
            write(
                "Allocation samples over the last {Minutes:0} min: type #{Rank} {Type} {TypeMb:0.0} MB ({TypePct:0.0}%): main thread {MainMb:0.0} MB, thread pool {PoolMb:0.0} MB, other threads {OtherMb:0.0} MB",
                minutes,
                ++rank,
                type,
                Megabytes(tally.Total),
                PercentOf(tally.Total, typed),
                Megabytes(tally.Main),
                Megabytes(tally.Pool),
                Megabytes(tally.Other));
        }

        rank = 0;
        foreach (var (thread, bytes) in window.OtherThreads.OrderByDescending(x => x.Value).Take(limits.Threads))
        {
            write(
                "Allocation samples over the last {Minutes:0} min: other thread #{Rank} {Thread} {ThreadMb:0.0} MB ({ThreadPct:0.0}%)",
                minutes,
                ++rank,
                thread,
                Megabytes(bytes),
                PercentOf(bytes, window.Bytes));
        }

        // Per-frame allocation is on the main thread, and pool-heavy types can fill the table ranked by total.
        rank = 0;
        foreach (var (type, tally) in TopByMainThread(window.Types, limits.PerKind))
        {
            write(
                "Allocation samples over the last {Minutes:0} min: main thread only #{Rank} {Type} {MainMb:0.0} MB ({MainPct:0.0}% of the main thread), {TotalMb:0.0} MB on all threads",
                minutes,
                ++rank,
                type,
                Megabytes(tally.Main),
                PercentOf(tally.Main, window.Main),
                Megabytes(tally.Total));
        }

        // A pool-heavy type below the table ranked by total would otherwise have no line at all.
        rank = 0;
        foreach (var (type, tally) in TopByPool(window.Types, limits.PerKind))
        {
            write(
                "Allocation samples over the last {Minutes:0} min: thread pool only #{Rank} {Type} {PoolMb:0.0} MB ({PoolPct:0.0}% of the thread pool), {TotalMb:0.0} MB on all threads",
                minutes,
                ++rank,
                type,
                Megabytes(tally.Pool),
                PercentOf(tally.Pool, window.Pool),
                Megabytes(tally.Total));
        }

        // Last, and worded apart from the "other thread #" lines, so that parsers of the older lines skip these.
        rank = 0;
        foreach (var (thread, threadBytes) in window.OtherThreads.OrderByDescending(x => x.Value).Take(limits.TypedThreads))
        {
            ++rank;
            var typeRank = 0;
            foreach (var (pair, bytes) in TopTypesOfThread(window.OtherThreadTypes, thread, limits.TypesPerThread))
            {
                write(
                    "Allocation samples over the last {Minutes:0} min: types of other thread rank {Rank} {Thread}: #{TypeRank} {Type} {TypeMb:0.0} MB ({TypePct:0.0}% of that thread)",
                    minutes,
                    rank,
                    thread,
                    ++typeRank,
                    pair.Type,
                    Megabytes(bytes),
                    PercentOf(bytes, threadBytes));
            }
        }
    }

    /// <summary>
    /// Picks the types to list for one other thread: most bytes first.
    /// </summary>
    /// <param name="pairs">Bytes by thread and type name; only read.</param>
    /// <param name="thread">The thread name.</param>
    /// <param name="count">How many to pick at most.</param>
    /// <returns>The picked pairs, in listing order.</returns>
    internal static List<KeyValuePair<(string Thread, string Type), long>> TopTypesOfThread(
        IEnumerable<KeyValuePair<(string Thread, string Type), long>> pairs, string thread, int count) =>
        pairs.Where(x => string.Equals(x.Key.Thread, thread, StringComparison.Ordinal))
             .OrderByDescending(x => x.Value)
             .Take(count)
             .ToList();

    /// <summary>
    /// Creates the thread name look-up the sampler uses: the thread description; for a thread without one, the module
    /// it started in, as "(unnamed, coreclr.dll)"; else <see cref="ThreadNames.Unnamed"/>.
    /// </summary>
    /// <returns>The look-up, with its own cache of module names.</returns>
    internal static ThreadNameLookUp NewThreadNameLookUp()
    {
        var unnamed = new UnnamedThreadLabels(GameNativeThreadReport.TryReadStartAddress, ModuleFileNameAt);
        return (osThreadId, known) =>
        {
            var name = LookUpThreadName(osThreadId, known);
            return name == ThreadNames.Unnamed ? unnamed.Label(osThreadId) : name;
        };
    }

    /// <summary>
    /// Gets the file name of the module that contains an address.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns>The file name, as "coreclr.dll"; null when no module contains the address or its name cannot be read.</returns>
    internal static unsafe string? ModuleFileNameAt(nuint address)
    {
        HMODULE module;
        const uint flags = GET.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET.GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT;
        if (address == 0 || !GetModuleHandleExW(flags, (ushort*)address, &module))
            return null;

        var buffer = stackalloc char[MaxModulePath];
        var length = GetModuleFileNameW(module, (ushort*)buffer, MaxModulePath);
        if (length == 0 || length >= MaxModulePath)
            return null;

        var file = Path.GetFileName(new ReadOnlySpan<char>(buffer, (int)length));
        return file.IsEmpty ? null : new string(file);
    }

    /// <inheritdoc/>
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name != RuntimeProvider)
            return;

        this.EnableEvents(eventSource, EventLevel.Verbose, GcKeyword);
        this.sourceEnabled = true;
    }

    /// <inheritdoc/>
    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (this.failure is not null)
            return;

        try
        {
            if (eventData.EventId != AllocationTickEventId)
            {
                lock (this.gate)
                    this.current.OtherEvents++;
                return;
            }

            this.Sample(eventData);
        }
        catch (Exception ex)
        {
            // Never let an exception out onto the runtime's dispatch thread.
            this.failure = $"{ex.GetType().Name}: {ex.Message}";
        }
    }

    private static unsafe string LookUpThreadName(uint osThreadId, string? known)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393) || threadNamesUnavailable)
            return ThreadNames.Unnamed;

        try
        {
            return LookUpThreadNameUnchecked(osThreadId, known);
        }
        catch (EntryPointNotFoundException)
        {
            // Some Wine versions lack GetThreadDescription; the other threads then share one "(unnamed)" row.
            threadNamesUnavailable = true;
            return ThreadNames.Unnamed;
        }
    }

    // An unchanged name comes back as the same string, so reading it again allocates nothing.
    [SupportedOSPlatform("windows10.0.14393")]
    private static unsafe string LookUpThreadNameUnchecked(uint osThreadId, string? known)
    {
        var handle = OpenThread(ThreadQueryLimitedInformation, BOOL.FALSE, osThreadId);
        if (handle == HANDLE.NULL)
            return ThreadNames.Exited;

        try
        {
            ushort* description = null;
            if (GetThreadDescription(handle, &description).FAILED || description == null)
                return ThreadNames.Unnamed;

            try
            {
                var name = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((char*)description);
                if (name.Length == 0)
                    return ThreadNames.Unnamed;

                return known is not null && name.SequenceEqual(known) ? known : new string(name);
            }
            finally
            {
                LocalFree((HLOCAL)description);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static double Megabytes(long bytes) => bytes / BytesPerMegabyte;

    private static double PercentOf(long part, long whole) => whole > 0 ? 100.0 * part / whole : 0;

    private void LogFailureOnce()
    {
        if (this.failure is { } why && !this.failureLogged)
        {
            this.failureLogged = true;
            Log.Error("Allocation sampling failed and is off until it is turned off and on again in settings: {Reason}", why);
        }
    }

    private void EndWindow(long now)
    {
        Window finished;
        lock (this.gate)
        {
            finished = this.current;
            this.current = new Window();
        }

        this.threads.Clear();

        var processBytes = GC.GetTotalAllocatedBytes(false) - this.windowStartProcessBytes;
        var minutes = (now - this.windowStart) / (double)Stopwatch.Frequency / 60;
        this.windowStart = now;
        this.windowEnd = now + this.windowTicks;
        this.windowStartProcessBytes = GC.GetTotalAllocatedBytes(false);
        var limits = (this.maxTypeLines, this.maxThreadLines, this.maxMainThreadTypeLines, TypedOtherThreads, TypesPerOtherThread);
        Report(finished, minutes, processBytes, this.sourceEnabled, limits, Log.Information);
    }

    private void Sample(EventWrittenEventArgs eventData)
    {
        var payload = eventData.Payload;
        if (payload is null)
            return;

        if (eventData.Version != this.payloadVersion)
        {
            var names = eventData.PayloadNames;
            this.amountIndex = names?.IndexOf("AllocationAmount64") ?? -1;
            this.typeNameIndex = names?.IndexOf("TypeName") ?? -1;
            this.typeIdIndex = names?.IndexOf("TypeID") ?? -1;
            this.payloadVersion = eventData.Version;
        }

        long amount = this.amountIndex >= 0 && this.amountIndex < payload.Count && payload[this.amountIndex] is ulong amount64
                          ? (long)amount64
                          : payload.Count > 0 && payload[0] is uint amount32 ? amount32 : 0;
        var typeName = this.typeNameIndex >= 0 && this.typeNameIndex < payload.Count ? payload[this.typeNameIndex] as string : null;
        var typeId = this.typeIdIndex >= 0 && this.typeIdIndex < payload.Count && payload[this.typeIdIndex] is nint id ? id : 0;
        var osThreadId = (uint)eventData.OSThreadId;

        var kind = ThreadKind.Other;
        string? threadName = null;
        if (osThreadId == this.mainThreadId)
        {
            kind = ThreadKind.Main;
        }
        else if (osThreadId == GetCurrentThreadId())
        {
            kind = ThreadKind.Self;
        }
        else
        {
            var thread = this.threads.Get(osThreadId, Environment.TickCount64);
            kind = thread.IsPool ? ThreadKind.Pool : ThreadKind.Other;
            threadName = thread.Name;
        }

        lock (this.gate)
            this.current.Add(kind, threadName, this.labels.Label(typeName ?? UnknownType, typeId), amount);
    }

    /// <summary>
    /// Sampled bytes of one type, split by the kind of thread that allocated them.
    /// </summary>
    internal struct TypeTally
    {
        /// <summary>Bytes allocated on the main thread.</summary>
        public long Main;

        /// <summary>Bytes allocated on thread pool threads.</summary>
        public long Pool;

        /// <summary>Bytes allocated on any other thread.</summary>
        public long Other;

        /// <summary>Gets the bytes allocated on all threads.</summary>
        public readonly long Total => this.Main + this.Pool + this.Other;
    }

    /// <summary>
    /// Names of threads by OS thread id. The OS hands an id to a new thread once its thread has exited, so a name
    /// is trusted only for a while and then read again, and an id without a thread is not remembered at all.
    /// </summary>
    /// <remarks>
    /// <see cref="Get"/> on the event dispatch thread, <see cref="Clear"/> on any thread. A name that is still
    /// trusted costs one dictionary read and no allocation; so does reading an unchanged name again.
    /// </remarks>
    internal sealed class ThreadNames
    {
        /// <summary>Name of a thread without a description.</summary>
        public const string Unnamed = "(unnamed)";

        /// <summary>What the look-up answers when no thread has the id now.</summary>
        public const string Exited = "(exited)";

        private static readonly Entry ExitedEntry = new(Exited, 0);

        private readonly ConcurrentDictionary<uint, Entry> entries = new();
        private readonly ThreadNameLookUp lookUp;
        private readonly long lifetimeMs;

        /// <summary>
        /// Initializes a new instance of the <see cref="ThreadNames"/> class.
        /// </summary>
        /// <param name="lookUp">Reads the name of a thread.</param>
        /// <param name="lifetimeMs">How long a name is trusted before it is read again.</param>
        public ThreadNames(ThreadNameLookUp lookUp, long lifetimeMs)
        {
            this.lookUp = lookUp;
            this.lifetimeMs = lifetimeMs;
        }

        /// <summary>
        /// Gets the name of the thread that has this id now.
        /// </summary>
        /// <param name="osThreadId">The OS thread id.</param>
        /// <param name="nowMs">A millisecond clock that never goes back, as <see cref="Environment.TickCount64"/>.</param>
        /// <returns>The name and whether it is a thread pool thread.</returns>
        public Entry Get(uint osThreadId, long nowMs)
        {
            if (this.entries.TryGetValue(osThreadId, out var entry) && nowMs - entry.CheckedAtMs < this.lifetimeMs)
                return entry;

            var name = this.lookUp(osThreadId, entry?.Name);
            if (name == Exited)
            {
                if (entry is not null)
                    this.entries.TryRemove(osThreadId, out _);
                return ExitedEntry;
            }

            if (entry is not null && name == entry.Name)
            {
                entry.CheckedAtMs = nowMs;
                return entry;
            }

            var replaces = entry is not null;
            entry = new Entry(name, nowMs);
            if (replaces || this.entries.Count < MaxCachedThreads)
                this.entries[osThreadId] = entry;
            return entry;
        }

        /// <summary>Forgets every name.</summary>
        public void Clear() => this.entries.Clear();

        /// <summary>
        /// A thread name and when it was last read.
        /// </summary>
        internal sealed class Entry
        {
            private long checkedAtMs;

            /// <summary>
            /// Initializes a new instance of the <see cref="Entry"/> class.
            /// </summary>
            /// <param name="name">The thread name.</param>
            /// <param name="checkedAtMs">When it was read.</param>
            public Entry(string name, long checkedAtMs)
            {
                this.Name = name;
                this.IsPool = name.StartsWith(".NET TP ", StringComparison.Ordinal);
                this.checkedAtMs = checkedAtMs;
            }

            /// <summary>Gets the thread name.</summary>
            public string Name { get; }

            /// <summary>Gets a value indicating whether the name is that of a thread pool thread.</summary>
            public bool IsPool { get; }

            /// <summary>Gets or sets when the name was last read, on the clock given to <see cref="Get"/>.</summary>
            public long CheckedAtMs
            {
                get => Volatile.Read(ref this.checkedAtMs);
                set => Volatile.Write(ref this.checkedAtMs, value);
            }
        }
    }

    /// <summary>
    /// Labels a thread without a description by the module its start address lies in, as "(unnamed, coreclr.dll)".
    /// </summary>
    /// <remarks>
    /// Any thread. The module is looked up once per start address and its label kept, so a label read again is the
    /// same string and costs no allocation. Answers <see cref="ThreadNames.Unnamed"/> when the start address or its
    /// module cannot be read, as for an id without a thread; never throws for that.
    /// </remarks>
    internal sealed class UnnamedThreadLabels
    {
        /// <summary>How many start addresses are kept; past that the cache starts over.</summary>
        public const int MaxStarts = 256;

        private readonly ConcurrentDictionary<nuint, string> byStart = new();
        private readonly StartAddressRead readStart;
        private readonly Func<nuint, string?> moduleAt;
        private volatile bool unavailable;

        /// <summary>
        /// Initializes a new instance of the <see cref="UnnamedThreadLabels"/> class.
        /// </summary>
        /// <param name="readStart">Reads the start address of a thread.</param>
        /// <param name="moduleAt">Gets the file name of the module containing an address; null for none.</param>
        public UnnamedThreadLabels(StartAddressRead readStart, Func<nuint, string?> moduleAt)
        {
            this.readStart = readStart;
            this.moduleAt = moduleAt;
        }

        /// <summary>Gets the number of start addresses kept.</summary>
        public int Count => this.byStart.Count;

        /// <summary>
        /// Gets the label of a thread without a description.
        /// </summary>
        /// <param name="osThreadId">The OS thread id.</param>
        /// <returns>"(unnamed, module)", or <see cref="ThreadNames.Unnamed"/>.</returns>
        public string Label(uint osThreadId)
        {
            if (this.unavailable)
                return ThreadNames.Unnamed;

            try
            {
                if (!this.readStart(osThreadId, out var start) || start == 0)
                    return ThreadNames.Unnamed;

                if (this.byStart.TryGetValue(start, out var label))
                    return label;

                var module = this.moduleAt(start);
                label = string.IsNullOrEmpty(module) ? ThreadNames.Unnamed : "(unnamed, " + module + ")";
                if (this.byStart.Count >= MaxStarts)
                    this.byStart.Clear();
                this.byStart[start] = label;
                return label;
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                // As for thread descriptions under Wine: the other threads then share one "(unnamed)" row.
                this.unavailable = true;
                return ThreadNames.Unnamed;
            }
        }
    }

    /// <summary>
    /// Marks the names of a few known types, so that they read apart from types of the same short name elsewhere,
    /// as Dalamud's ImRaii objects from those of OtterGui; and compiler-generated types with their owner, as
    /// "&lt;&gt;c__DisplayClass8_0 (Assembly:Namespace.Type)".
    /// </summary>
    /// <remarks>
    /// One thread at a time. A type id is only ever compared as an integer: once a plugin unloads, the type data
    /// behind its ids is freed, so an id must never be read through or turned back into a type.
    /// </remarks>
    internal sealed class TypeLabels
    {
        /// <summary>What the names of Dalamud's own types get after them.</summary>
        public const string DalamudSuffix = " (Dalamud)";

        private const int MaxOwnedLabels = 4096;

        private readonly nint[] ids;
        private readonly string?[] names;
        private readonly string?[] labelled;
        private readonly string suffix;
        private readonly Dictionary<nint, (string Owner, string Name, string Label)> ownedLabels = [];

        /// <summary>
        /// Initializes a new instance of the <see cref="TypeLabels"/> class.
        /// </summary>
        /// <param name="ids">The type ids to mark, as <see cref="RuntimeTypeHandle.Value"/>; 0 is ignored.</param>
        /// <param name="suffix">What a marked name gets after it.</param>
        /// <param name="owners">Owners of compiler-generated types, if they are to be labelled; not disposed here.</param>
        public TypeLabels(IEnumerable<nint> ids, string suffix, GeneratedTypeOwners? owners = null)
        {
            this.ids = ids.Where(x => x != 0).Distinct().ToArray();
            this.names = new string?[this.ids.Length];
            this.labelled = new string?[this.ids.Length];
            this.suffix = suffix;
            this.Owners = owners;
        }

        /// <summary>
        /// Gets the owners of compiler-generated types that these labels use, if any.
        /// </summary>
        public GeneratedTypeOwners? Owners { get; }

        /// <summary>
        /// Builds the labels for the types nested in Dalamud's <see cref="ImRaii"/>.
        /// </summary>
        /// <param name="owners">Owners of compiler-generated types, if they are to be labelled.</param>
        /// <returns>The labels.</returns>
        public static TypeLabels ForDalamudImRaii(GeneratedTypeOwners? owners = null) =>
            new(
                typeof(ImRaii).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                              .Where(x => !x.IsInterface && !x.ContainsGenericParameters)
                              .Select(x => x.TypeHandle.Value),
                DalamudSuffix,
                owners);

        /// <summary>
        /// Gets the name to tally a sample under.
        /// </summary>
        /// <param name="typeName">The type name the event gave.</param>
        /// <param name="typeId">The type id the event gave; 0 when it gave none.</param>
        /// <returns>The marked name for a marked id, else <paramref name="typeName"/>; the same string again for the same input.</returns>
        public string Label(string typeName, nint typeId)
        {
            if (typeId == 0)
                return typeName;

            for (var i = 0; i < this.ids.Length; i++)
            {
                if (this.ids[i] != typeId)
                    continue;

                if (this.labelled[i] is not { } label || !string.Equals(this.names[i], typeName, StringComparison.Ordinal))
                {
                    label = typeName + this.suffix;
                    this.names[i] = typeName;
                    this.labelled[i] = label;
                }

                return label;
            }

            if (this.Owners is { } owners && typeName.Length > 1 && typeName[0] == '<')
                return this.Owned(owners, typeName, typeId);

            return typeName;
        }

        // Until its owner is found, a type is tallied under its short name, as before.
        private string Owned(GeneratedTypeOwners owners, string typeName, nint typeId)
        {
            if (!owners.TryGetOwner(typeId, out var owner))
            {
                owners.Request(typeName, typeId);
                return typeName;
            }

            if (this.ownedLabels.TryGetValue(typeId, out var known)
                && ReferenceEquals(known.Owner, owner)
                && string.Equals(known.Name, typeName, StringComparison.Ordinal))
            {
                return known.Label;
            }

            if (this.ownedLabels.Count >= MaxOwnedLabels)
                this.ownedLabels.Clear();

            var label = typeName + " (" + owner + ")";
            this.ownedLabels[typeId] = (owner, typeName, label);
            return label;
        }
    }

    /// <summary>
    /// Sampled bytes of one window, by type and by thread.
    /// </summary>
    internal sealed class Window
    {
        /// <summary>Gets the tallies by type name.</summary>
        public Dictionary<string, TypeTally> Types { get; } = new(StringComparer.Ordinal);

        /// <summary>Gets the bytes by thread name, for threads neither main nor pool.</summary>
        public Dictionary<string, long> OtherThreads { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// Gets the bytes by thread and type name, for the threads in <see cref="OtherThreads"/>; at most
        /// <see cref="MaxThreadTypes"/> pairs, plus one "(other types)" pair per thread for the types that came later.
        /// </summary>
        public Dictionary<(string Thread, string Type), long> OtherThreadTypes { get; } = new();

        /// <summary>Gets the number of samples.</summary>
        public long Samples { get; private set; }

        /// <summary>Gets the bytes on all threads.</summary>
        public long Bytes { get; private set; }

        /// <summary>Gets the bytes on the main thread.</summary>
        public long Main { get; private set; }

        /// <summary>Gets the bytes on thread pool threads.</summary>
        public long Pool { get; private set; }

        /// <summary>Gets the bytes on other threads.</summary>
        public long Other { get; private set; }

        /// <summary>Gets the bytes on the thread that delivers the sampler's events.</summary>
        public long Self { get; private set; }

        /// <summary>Gets or sets the number of runtime events other than allocation ticks.</summary>
        public long OtherEvents { get; set; }

        /// <summary>Counts one sample; the sampler's own bytes are counted but kept out of the type table.</summary>
        /// <param name="kind">The kind of thread it was allocated on.</param>
        /// <param name="threadName">The thread name, for <see cref="ThreadKind.Other"/>.</param>
        /// <param name="typeName">The name to tally it under.</param>
        /// <param name="amount">The sampled bytes.</param>
        public void Add(ThreadKind kind, string? threadName, string typeName, long amount)
        {
            this.Samples++;
            this.Bytes += amount;
            switch (kind)
            {
                case ThreadKind.Self:
                    this.Self += amount;
                    return;
                case ThreadKind.Main:
                    this.Main += amount;
                    break;
                case ThreadKind.Pool:
                    this.Pool += amount;
                    break;
                default:
                    this.Other += amount;
                    var name = threadName ?? "(unnamed)";
                    if (this.OtherThreads.Count < MaxThreadNames || this.OtherThreads.ContainsKey(name))
                    {
                        CollectionsMarshal.GetValueRefOrAddDefault(this.OtherThreads, name, out _) += amount;
                        var pair = (name, typeName);
                        if (this.OtherThreadTypes.Count >= MaxThreadTypes && !this.OtherThreadTypes.ContainsKey(pair))
                            pair = (name, OtherTypes);
                        CollectionsMarshal.GetValueRefOrAddDefault(this.OtherThreadTypes, pair, out _) += amount;
                    }

                    break;
            }

            var key = this.Types.Count < MaxTypes || this.Types.ContainsKey(typeName) ? typeName : OtherTypes;
            ref var tally = ref CollectionsMarshal.GetValueRefOrAddDefault(this.Types, key, out _);
            switch (kind)
            {
                case ThreadKind.Main:
                    tally.Main += amount;
                    break;
                case ThreadKind.Pool:
                    tally.Pool += amount;
                    break;
                default:
                    tally.Other += amount;
                    break;
            }
        }
    }
}
