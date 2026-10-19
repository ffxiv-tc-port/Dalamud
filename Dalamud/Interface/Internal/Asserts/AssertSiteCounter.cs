using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.Logging.Internal;

namespace Dalamud.Interface.Internal.Asserts;

/// <summary>
/// Counts ImGui assertion failures per assert site and logs every site's count once per window.
/// </summary>
/// <remarks>
/// Thread safe. <see cref="Count"/> allocates only the first time it sees a site; after that it is a lookup under a
/// lock. Only strings are kept, never types, methods or assemblies, so no plugin load context is held alive.
/// Diagnostic for users to report back: which code keeps failing ImGui asserts that are otherwise silent.
/// </remarks>
internal sealed class AssertSiteCounter
{
    /// <summary>How many sites are told apart; failures at sites beyond these are only summed up.</summary>
    public const int MaxSites = 64;

    private const int MaxLinesPerWindow = 10;
    private const int MaxTextLength = 300;
    private const int MaxContextLength = 600;
    private const string Unreadable = "(unreadable)";

    private static readonly ModuleLog Log = new("ImGuiAssert");

    private readonly object sync = new();
    private readonly Dictionary<SiteKey, Site> sites = new();
    private readonly long windowTicks;

    private bool started;
    private long windowStart;
    private long windowEnd;
    private long windowFrames;
    private long windowUntracked;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssertSiteCounter"/> class.
    /// </summary>
    /// <param name="window">How often to report. The first window starts at the first <see cref="Tick"/>.</param>
    public AssertSiteCounter(TimeSpan window)
    {
        this.windowTicks = (long)(window.TotalSeconds * Stopwatch.Frequency);
    }

    /// <summary>Gets the counter that ImGui's assert callback reports to, ticked by the framework.</summary>
    public static AssertSiteCounter Shared { get; } = new(TimeSpan.FromHours(1));

    /// <summary>
    /// Counts one failure at the site of the given <c>IM_ASSERT</c>. Reads the two strings only the first time.
    /// </summary>
    /// <param name="expr">The failed expression, as handed over by ImGui.</param>
    /// <param name="file">The source file, as handed over by ImGui.</param>
    /// <param name="line">The source line.</param>
    /// <returns>True for the first failure at this site in this window: the caller should pass a description of
    /// where it came from to <see cref="Describe"/>.</returns>
    public bool Count(nint expr, nint file, int line)
    {
        lock (this.sync)
        {
            var key = new SiteKey(expr, file, line);
            if (!this.sites.TryGetValue(key, out var site))
            {
                if (this.sites.Count >= MaxSites)
                {
                    this.windowUntracked++;
                    return false;
                }

                site = new Site(ReadLiteral(expr), FileNameOf(ReadLiteral(file)), line);
                this.sites.Add(key, site);
            }

            site.WindowCount++;
            site.SessionCount++;
            if (site.ContextClaimed)
                return false;

            site.ContextClaimed = true;
            return true;
        }
    }

    /// <summary>
    /// Stores where the first failure at a site in this window came from.
    /// </summary>
    /// <param name="expr">The failed expression, as passed to <see cref="Count"/>.</param>
    /// <param name="file">The source file, as passed to <see cref="Count"/>.</param>
    /// <param name="line">The source line.</param>
    /// <param name="context">The description.</param>
    public void Describe(nint expr, nint file, int line, string context)
    {
        lock (this.sync)
        {
            if (this.sites.TryGetValue(new SiteKey(expr, file, line), out var site) && site.Context == null)
                site.Context = Truncate(context, MaxContextLength);
        }
    }

    /// <summary>
    /// Counts a frame and logs the window when it is over. Call it once per framework update.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void Tick(long now)
    {
        WindowReport? report;
        lock (this.sync)
        {
            if (!this.started)
            {
                this.started = true;
                this.StartWindow(now);
            }

            this.windowFrames++;
            if (now < this.windowEnd)
                return;

            report = this.CloseWindow(now);
        }

        if (report != null)
            Write(report, false);
    }

    /// <summary>
    /// Logs the window so far without waiting for it to end, for a framework that is going away.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    public void ReportUnfinished(long now)
    {
        WindowReport? report;
        lock (this.sync)
        {
            if (!this.started)
                return;

            report = this.CloseWindow(now);
        }

        if (report != null)
            Write(report, true);
    }

    /// <summary>
    /// Ends the window: returns what it counted and starts the next one. Null when nothing failed.
    /// </summary>
    /// <param name="now">The current <see cref="Stopwatch.GetTimestamp"/>.</param>
    /// <returns>The window's counts, or null.</returns>
    internal WindowReport? CloseWindow(long now)
    {
        lock (this.sync)
        {
            var active = this.sites.Values.Where(x => x.WindowCount > 0).ToList();
            var total = active.Sum(x => x.WindowCount);
            var report = total == 0 && this.windowUntracked == 0
                             ? null
                             : new WindowReport(
                                 (now - this.windowStart) / (double)Stopwatch.Frequency / 60,
                                 this.windowFrames,
                                 total,
                                 active.Count,
                                 this.windowUntracked,
                                 active.OrderByDescending(x => x.WindowCount)
                                       .Take(MaxLinesPerWindow)
                                       .Select(x => new SiteReport(x.Expr, x.File, x.Line, x.WindowCount, x.SessionCount, x.Context))
                                       .ToList());

            // Sites quiet for a whole window make room for new ones.
            if (this.sites.Count >= MaxSites)
            {
                foreach (var quiet in this.sites.Where(x => x.Value.WindowCount == 0).Select(x => x.Key).ToList())
                    this.sites.Remove(quiet);
            }

            foreach (var site in this.sites.Values)
            {
                site.WindowCount = 0;
                site.Context = null;
                site.ContextClaimed = false;
            }

            this.StartWindow(now);
            return report;
        }
    }

    private static void Write(WindowReport report, bool unfinished)
    {
        if (unfinished)
        {
            Log.Information(
                "ImGui assertion failures of an unfinished window, {Minutes:0.0} min and {Frames} frames, reported early because the framework is unloading:",
                report.Minutes,
                report.Frames);
        }

        var frames = Math.Max(1, report.Frames);
        for (var i = 0; i < report.Sites.Count; i++)
        {
            var site = report.Sites[i];
            if (i == 0)
            {
                Log.Information(
                    "ImGui assertion failures over the last {Minutes:0} min: {Total} at {SiteCount} site(s) over {Frames} frames, {TotalPerFrame:0.00} per frame. #1 {Count} times ({PerFrame:0.00} per frame, {SessionCount} this session) at {File}:{Line} {Expr}; first one this window: {Context:l}",
                    report.Minutes,
                    report.Total,
                    report.SiteCount,
                    report.Frames,
                    report.Total / (double)frames,
                    site.Count,
                    site.Count / (double)frames,
                    site.SessionCount,
                    site.File,
                    site.Line,
                    site.Expr,
                    site.Context ?? "(not captured)");
            }
            else
            {
                Log.Information(
                    "ImGui assertion failures over the last {Minutes:0} min: #{Rank} {Count} times ({PerFrame:0.00} per frame, {SessionCount} this session) at {File}:{Line} {Expr}; first one this window: {Context:l}",
                    report.Minutes,
                    i + 1,
                    site.Count,
                    site.Count / (double)frames,
                    site.SessionCount,
                    site.File,
                    site.Line,
                    site.Expr,
                    site.Context ?? "(not captured)");
            }
        }

        if (report.Untracked > 0)
        {
            Log.Information(
                "ImGui assertion failures over the last {Minutes:0} min: {Untracked} more at sites past the first {MaxSites}, not told apart",
                report.Minutes,
                report.Untracked,
                MaxSites);
        }
    }

    // Only IM_ASSERT's own string literals arrive here; a pointer outside the user address range is not read.
    private static string ReadLiteral(nint pointer)
    {
        var value = (nuint)pointer;
        if (value < 0x10000 || value > 0x00007FFFFFFFFFFF)
            return Unreadable;

        return Truncate(Marshal.PtrToStringAnsi(pointer) ?? Unreadable, MaxTextLength);
    }

    private static string FileNameOf(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "...";

    private void StartWindow(long now)
    {
        this.windowStart = now;
        this.windowEnd = now + this.windowTicks;
        this.windowFrames = 0;
        this.windowUntracked = 0;
    }

    /// <summary>
    /// What one window counted at one site.
    /// </summary>
    /// <param name="Expr">The failed expression.</param>
    /// <param name="File">The source file name.</param>
    /// <param name="Line">The source line.</param>
    /// <param name="Count">Failures in the window.</param>
    /// <param name="SessionCount">Failures since the site was first seen.</param>
    /// <param name="Context">Where the first failure in the window came from, if it was described.</param>
    internal readonly record struct SiteReport(string Expr, string File, int Line, long Count, long SessionCount, string? Context);

    private readonly record struct SiteKey(nint Expr, nint File, int Line);

    /// <summary>
    /// What one window counted.
    /// </summary>
    /// <param name="Minutes">How long the window was.</param>
    /// <param name="Frames">Framework updates in the window.</param>
    /// <param name="Total">Failures at the told-apart sites.</param>
    /// <param name="SiteCount">Sites that failed at least once.</param>
    /// <param name="Untracked">Failures at sites past <see cref="MaxSites"/>.</param>
    /// <param name="Sites">The sites with the most failures, most first.</param>
    internal sealed record WindowReport(double Minutes, long Frames, long Total, int SiteCount, long Untracked, List<SiteReport> Sites);

    private sealed class Site
    {
        public Site(string expr, string file, int line)
        {
            this.Expr = expr;
            this.File = file;
            this.Line = line;
        }

        public string Expr { get; }

        public string File { get; }

        public int Line { get; }

        public long WindowCount { get; set; }

        public long SessionCount { get; set; }

        public string? Context { get; set; }

        public bool ContextClaimed { get; set; }
    }
}
