using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Logging.Internal;

using TerraFX.Interop.Windows;

using static TerraFX.Interop.Windows.Windows;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Counts, once per window, the threads of this process whose start address lies in the game executable, and logs
/// how many there are, how many ended or appeared since the last count, and how many were created late.
/// </summary>
/// <remarks>
/// Not thread safe: <see cref="Tick"/> and <see cref="ReportUnfinished"/> from one thread at a time (the framework
/// thread, or the thread unloading it while the framework thread waits). The hourly count runs on the thread pool,
/// since listing threads walks every process on the machine (about 10 ms). Never throws: a failed count logs one line.
/// Diagnostic for users to report back: game threads that run plugin hooks are not covered by Dalamud's guard against
/// a runtime bug that corrupts the GC heap when a thread that touched an unloaded plugin's thread-local storage ends.
/// </remarks>
internal sealed unsafe partial class GameNativeThreadReport
{
    private const uint ThreadQueryInformation = 0x0040;
    private const int ThreadQuerySetWin32StartAddress = 9;
    private const int ErrorInvalidParameter = 87;
    private const int MaxStartsListed = 5;
    private const int MaxGoneListed = 8;
    private static readonly long LateAfterGameStart = TimeSpan.FromMinutes(5).Ticks;

    private static readonly ModuleLog Log = new("PerfDiag");

    private readonly long windowTicks;
    private readonly string imageName;
    private readonly nuint imageBase;
    private readonly long gameStartFileTime;
    private readonly Func<Snapshot> read;
    private readonly object countLock = new();

    // Identity includes start address and creation time, so a thread id handed to a new thread is not mistaken
    // for the old thread still running. Null when the last count failed.
    private HashSet<NativeThread>? lastCount;

    private long windowStart;
    private long windowEnd;

    /// <summary>
    /// Initializes a new instance of the <see cref="GameNativeThreadReport"/> class and counts once right away.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <param name="window">How often to report.</param>
    /// <param name="imageName">The image's name, as the log shows it.</param>
    /// <param name="imageBase">The image's base address; start addresses are logged as offsets from it.</param>
    /// <param name="gameStartFileTime">When the game started, as a UTC FILETIME.</param>
    /// <param name="read">Lists the threads starting in the image.</param>
    internal GameNativeThreadReport(long now, TimeSpan window, string imageName, nuint imageBase, long gameStartFileTime, Func<Snapshot> read)
    {
        this.windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
        this.imageName = imageName;
        this.imageBase = imageBase;
        this.gameStartFileTime = gameStartFileTime;
        this.read = read;
        this.CountAndLog("at startup");
        this.StartWindow(now);
    }

    private enum ReadOutcome
    {
        Read,
        Exited,
        Unreadable,
    }

    /// <summary>
    /// Gets the result of the last count; null when it failed. For tests.
    /// </summary>
    internal Result? LastResult { get; private set; }

    /// <summary>
    /// Gets the hourly count last started on the thread pool. For tests.
    /// </summary>
    internal Task? HourlyCount { get; private set; }

    /// <summary>
    /// Creates the report for the game executable, hourly. Logs one line and returns null when that is not possible.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <returns>The report, or null.</returns>
    public static GameNativeThreadReport? ForGame(long now)
    {
        try
        {
            MODULEINFO module;
            if (!K32GetModuleInformation(GetCurrentProcess(), GetModuleHandleW(null), &module, (uint)sizeof(MODULEINFO)))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastSystemError());

            FILETIME created, exited, kernel, user;
            if (!GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastSystemError());

            var imageBase = (nuint)module.lpBaseOfDll;
            var imageSize = (nuint)module.SizeOfImage;
            return new GameNativeThreadReport(
                now,
                TimeSpan.FromHours(1),
                Path.GetFileName(Environment.ProcessPath) ?? "the game executable",
                imageBase,
                ToLong(created),
                () => ReadThreadsIn(imageBase, imageSize));
        }
        catch (Exception ex)
        {
            Log.Information("Game native threads will not be counted this session: {Reason:l}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Counts and reports when the window is over. Call it once per framework update.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void Tick(long now)
    {
        if (now < this.windowEnd || this.HourlyCount is { IsCompleted: false })
            return;

        var window = $"over the last {(now - this.windowStart) / (double)Stopwatch.Frequency / 60:0} min";
        this.StartWindow(now);
        this.HourlyCount = this.CountOnThreadPool(window);
    }

    /// <summary>
    /// Counts and reports now, for a framework that is going away. The next window starts now.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void ReportUnfinished(long now)
    {
        var window = $"over an unfinished window of {(now - this.windowStart) / (double)Stopwatch.Frequency / 60:0.0} min, reported early because the framework is unloading";
        this.StartWindow(now);

        // Does not wait: the caller may be inside a native hook on the way out.
        if (!Monitor.TryEnter(this.countLock))
        {
            Log.Information("Game native threads {Window:l}: not counted, the hourly count is still running", window);
            return;
        }

        try
        {
            this.CountAndLog(window);
        }
        finally
        {
            Monitor.Exit(this.countLock);
        }
    }

    /// <summary>
    /// Lists the threads of this process whose Win32 start address lies in [imageBase, imageBase + imageSize).
    /// </summary>
    /// <param name="imageBase">Start of the image.</param>
    /// <param name="imageSize">Size of the image.</param>
    /// <returns>The threads, plus how many were looked at and how many could not be read.</returns>
    internal static Snapshot ReadThreadsIn(nuint imageBase, nuint imageSize)
    {
        var found = new List<NativeThread>();
        var scanned = 0;
        var unreadable = 0;
        using var process = Process.GetCurrentProcess();
        foreach (ProcessThread thread in process.Threads)
        {
            scanned++;
            switch (TryRead((uint)thread.Id, out var info))
            {
                case ReadOutcome.Read when info.Start - imageBase < imageSize:
                    found.Add(info);
                    break;
                case ReadOutcome.Unreadable:
                    unreadable++;
                    break;
            }
        }

        return new Snapshot(found, scanned, unreadable);
    }

    /// <summary>
    /// Compares a count with the one before it.
    /// </summary>
    /// <param name="before">The earlier count, or null if there is none.</param>
    /// <param name="now">The threads now.</param>
    /// <param name="lateAfterFileTime">Threads created after this UTC FILETIME count as late.</param>
    /// <returns>The comparison.</returns>
    internal static Result Compare(IReadOnlySet<NativeThread>? before, IReadOnlyList<NativeThread> now, long lateAfterFileTime)
    {
        var current = now.ToHashSet();
        var gone = before?.Where(x => !current.Contains(x)).OrderBy(x => x.CreatedFileTime).ToList();
        var added = before is null ? (int?)null : current.Count(x => !before.Contains(x));
        var late = current.Count(x => x.CreatedFileTime > lateAfterFileTime);
        return new Result(current, gone, added, late);
    }

    /// <summary>
    /// Reads the Win32 start address of a thread.
    /// </summary>
    /// <param name="osThreadId">The OS thread id.</param>
    /// <param name="start">The start address; 0 when it could not be read.</param>
    /// <returns>Whether it was read; false also when no thread has this id now.</returns>
    internal static bool TryReadStartAddress(uint osThreadId, out nuint start)
    {
        start = 0;
        var handle = OpenThread(ThreadQueryInformation, BOOL.FALSE, osThreadId);
        if (handle == HANDLE.NULL)
            return false;

        try
        {
            return TryReadStartAddress(handle, out start);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // The handle needs THREAD_QUERY_INFORMATION; the limited right is not enough for this information class.
    private static bool TryReadStartAddress(HANDLE handle, out nuint start)
    {
        nuint read;
        var ok = NtQueryInformationThread((nint)handle.Value, ThreadQuerySetWin32StartAddress, &read, (uint)sizeof(nuint), null) >= 0;
        start = ok ? read : 0;
        return ok;
    }

    private static ReadOutcome TryRead(uint id, out NativeThread info)
    {
        info = default;
        var handle = OpenThread(ThreadQueryInformation, BOOL.FALSE, id);
        if (handle == HANDLE.NULL)
            return Marshal.GetLastSystemError() == ErrorInvalidParameter ? ReadOutcome.Exited : ReadOutcome.Unreadable;

        try
        {
            if (!TryReadStartAddress(handle, out var start))
                return ReadOutcome.Unreadable;

            FILETIME created, exited, kernel, user;
            if (!GetThreadTimes(handle, &created, &exited, &kernel, &user))
                return ReadOutcome.Unreadable;

            info = new NativeThread(id, start, ToLong(created));
            return ReadOutcome.Read;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static long ToLong(FILETIME time) => ((long)time.dwHighDateTime << 32) | time.dwLowDateTime;

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationThread(nint thread, int infoClass, void* info, uint length, uint* returnLength);

    // Kept out of Tick: a lambda capturing a local there makes every call allocate the closure, even when not due.
    private Task CountOnThreadPool(string window) => Task.Run(() =>
    {
        lock (this.countLock)
            this.CountAndLog(window);
    });

    private void StartWindow(long now)
    {
        this.windowStart = now;
        this.windowEnd = now + this.windowTicks;
    }

    private void CountAndLog(string window)
    {
        try
        {
            var took = Stopwatch.GetTimestamp();
            var snapshot = this.read();
            var result = Compare(this.lastCount, snapshot.Threads, this.gameStartFileTime + LateAfterGameStart);
            took = Stopwatch.GetTimestamp() - took;
            this.lastCount = result.Current;
            this.LastResult = result;

            Log.Information(
                "Game native threads {Window:l}: {Now} now with a start address in {Image:l}, {Gone:l} gone and {New:l} new since the last count, {Late} of them created more than 5 min after the game started; starts {Starts:l}; gone {GoneList:l}; {Unreadable} of {Scanned} threads unreadable; took {Ms:0.0} ms",
                window,
                result.Current.Count,
                this.imageName,
                result.Gone?.Count.ToString() ?? "?",
                result.New?.ToString() ?? "?",
                result.Late,
                this.DescribeStarts(result.Current),
                result.Gone is null ? "unknown (no earlier count)" : this.DescribeGone(result.Gone),
                snapshot.Unreadable,
                snapshot.Scanned,
                took * 1000.0 / Stopwatch.Frequency);
        }
        catch (Exception ex)
        {
            this.lastCount = null;
            this.LastResult = null;
            Log.Information("Game native threads {Window:l}: could not count them ({Reason:l}); the next count has nothing to compare with", window, ex.Message);
        }
    }

    private string DescribeStarts(IReadOnlyCollection<NativeThread> threads)
    {
        if (threads.Count == 0)
            return "none";

        var groups = threads.GroupBy(x => x.Start).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).ToList();
        var listed = string.Join(", ", groups.Take(MaxStartsListed).Select(x => $"+0x{(ulong)(x.Key - this.imageBase):x} x{x.Count()}"));
        return groups.Count > MaxStartsListed ? $"{listed}, {groups.Count - MaxStartsListed} more addresses" : listed;
    }

    private string DescribeGone(IReadOnlyList<NativeThread> gone)
    {
        if (gone.Count == 0)
            return "none";

        var listed = string.Join(", ", gone.Take(MaxGoneListed).Select(x =>
            $"thread {x.Id} at +0x{(ulong)(x.Start - this.imageBase):x} created {(x.CreatedFileTime - this.gameStartFileTime) / (double)TimeSpan.TicksPerMinute:0.0} min after the game started"));
        return gone.Count > MaxGoneListed ? $"{listed}, {gone.Count - MaxGoneListed} more" : listed;
    }

    /// <summary>
    /// A thread as one count saw it.
    /// </summary>
    /// <param name="Id">OS thread id; the OS hands the id of an ended thread to a new one.</param>
    /// <param name="Start">Win32 start address.</param>
    /// <param name="CreatedFileTime">Creation time, UTC FILETIME.</param>
    internal readonly record struct NativeThread(uint Id, nuint Start, long CreatedFileTime);

    /// <summary>
    /// What one look at the thread list found.
    /// </summary>
    /// <param name="Threads">Threads starting in the image.</param>
    /// <param name="Scanned">Threads looked at, in any image.</param>
    /// <param name="Unreadable">Threads still running whose start address or creation time could not be read.</param>
    internal sealed record Snapshot(IReadOnlyList<NativeThread> Threads, int Scanned, int Unreadable);

    /// <summary>
    /// One count compared with the one before it.
    /// </summary>
    /// <param name="Current">Threads starting in the image now.</param>
    /// <param name="Gone">Threads of the earlier count that are no longer there; null without an earlier count.</param>
    /// <param name="New">Threads not in the earlier count; null without an earlier count.</param>
    /// <param name="Late">Threads now that were created late.</param>
    internal sealed record Result(HashSet<NativeThread> Current, List<NativeThread>? Gone, int? New, int Late);
}
