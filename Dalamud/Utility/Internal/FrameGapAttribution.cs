using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;

using Dalamud.Game.Addon.Lifecycle;

namespace Dalamud.Utility.Internal;

/// <summary>
/// What the time between two framework updates went to besides the update itself, for the FRAMEGAP line:
/// Dalamud's ImGui frame (plugin drawing inside it) and the addon lifecycle listeners.
/// </summary>
/// <remarks>
/// Records only on the thread that last called <see cref="Reset"/> (the framework thread); other threads are ignored.
/// Spans opened with <see cref="Enter"/> nest: one inside another counts only for the outermost, so the parts never
/// overlap. Plugin draws are not spans: they run inside the ImGui frame and only break it down.
/// Recording allocates nothing and cannot throw; text is built only in <see cref="Describe"/>.
/// </remarks>
internal sealed class FrameGapAttribution
{
    private static long lastGeneration;

    private readonly Func<long>? clock;

    private int threadId;
    private long generation;
    private int depth;

    private long uiTicks;
    private int uiFrames;

    private long drawTicks;
    private int draws;
    private Tally? slowestDraw;
    private long slowestDrawTicks;

    private long listenerTicks;
    private long listenerCalls;
    private Tally? slowestListener;
    private long slowestListenerTicks;
    private string? slowestCallPlugin;
    private AddonEvent slowestCallEvent;
    private string? slowestCallAddon;
    private long slowestCallTicks;

    /// <summary>Initializes a new instance of the <see cref="FrameGapAttribution"/> class.</summary>
    /// <param name="clock">Replaces <see cref="Stopwatch.GetTimestamp"/>, for tests; null for the real one.</param>
    internal FrameGapAttribution(Func<long>? clock = null) => this.clock = clock;

    /// <summary>Gets the instance Dalamud records into.</summary>
    public static FrameGapAttribution Shared { get; } = new();

    /// <summary>Gets the tally of Dalamud's own addon lifecycle listeners.</summary>
    public static Tally DalamudListeners { get; } = new("Dalamud");

    /// <summary>Starts a new gap on this thread; call first thing in every framework update, after the line was judged.</summary>
    public void Reset()
    {
        this.threadId = Environment.CurrentManagedThreadId;
        this.generation = Interlocked.Increment(ref lastGeneration);
        this.depth = 0;
        this.uiTicks = 0;
        this.uiFrames = 0;
        this.drawTicks = 0;
        this.draws = 0;
        this.slowestDraw = null;
        this.slowestDrawTicks = 0;
        this.listenerTicks = 0;
        this.listenerCalls = 0;
        this.slowestListener = null;
        this.slowestListenerTicks = 0;
        this.slowestCallPlugin = null;
        this.slowestCallAddon = null;
        this.slowestCallTicks = 0;
    }

    /// <summary>Opens a span: the framework update, the ImGui frame, or one listener call.</summary>
    /// <returns>The value to pass to the matching exit; 0 when this thread does not record.</returns>
    public long Enter()
    {
        if (Environment.CurrentManagedThreadId != this.threadId)
            return 0;

        this.depth++;
        return Math.Max(1, this.Now());
    }

    /// <summary>Closes the framework update span; its time is already on the line.</summary>
    /// <param name="start">What <see cref="Enter"/> returned.</param>
    public void ExitUpdate(long start) => this.Exit(start, out _);

    /// <summary>Closes the ImGui frame span.</summary>
    /// <param name="start">What <see cref="Enter"/> returned.</param>
    public void ExitUiFrame(long start)
    {
        if (!this.Exit(start, out var ticks))
            return;

        this.uiTicks += ticks;
        this.uiFrames++;
    }

    /// <summary>Closes one listener call.</summary>
    /// <param name="start">What <see cref="Enter"/> returned.</param>
    /// <param name="plugin">The owning plugin's tally, or null for Dalamud's own listener.</param>
    /// <param name="eventType">The event.</param>
    /// <param name="addonFilter">The addon name the listener asked for; empty for any.</param>
    public void ExitListener(long start, Tally? plugin, AddonEvent eventType, string? addonFilter)
    {
        if (!this.Exit(start, out var ticks))
            return;

        plugin ??= DalamudListeners;
        this.listenerTicks += ticks;
        this.listenerCalls++;
        this.Add(plugin, ticks, ref this.slowestListener, ref this.slowestListenerTicks);
        if (ticks > this.slowestCallTicks)
        {
            this.slowestCallTicks = ticks;
            this.slowestCallPlugin = plugin.Name;
            this.slowestCallEvent = eventType;
            this.slowestCallAddon = addonFilter;
        }
    }

    /// <summary>Takes the start of one plugin's draw.</summary>
    /// <returns>The value to pass to <see cref="EndDraw"/>; 0 when this thread does not record.</returns>
    public long BeginDraw() =>
        Environment.CurrentManagedThreadId == this.threadId ? Math.Max(1, this.Now()) : 0;

    /// <summary>Records one plugin's draw.</summary>
    /// <param name="start">What <see cref="BeginDraw"/> returned.</param>
    /// <param name="plugin">The plugin's tally.</param>
    public void EndDraw(long start, Tally plugin)
    {
        if (start == 0 || Environment.CurrentManagedThreadId != this.threadId)
            return;

        var ticks = this.Now() - start;
        this.drawTicks += ticks;
        this.draws++;
        this.Add(plugin, ticks, ref this.slowestDraw, ref this.slowestDrawTicks);
    }

    /// <summary>Describes the gap recorded since the last <see cref="Reset"/>. Never throws.</summary>
    /// <param name="gapTicks">The whole gap.</param>
    /// <param name="updateTicks">The framework update inside it, already on the line.</param>
    /// <returns>Like <c>between the two updates: Dalamud's ImGui frame 8.2 ms (plugin drawing 7.9 ms in 40 draws, slowest A 5.1 ms), …</c>.</returns>
    public string Describe(long gapTicks, long updateTicks)
    {
        try
        {
            var text = new StringBuilder("between the two updates: ");
            if (this.uiFrames == 0)
                text.Append("no Dalamud ImGui frame");
            else
                text.Append("Dalamud's ImGui frame").Append(this.uiFrames == 1 ? string.Empty : "s (" + this.uiFrames.ToString(CultureInfo.InvariantCulture) + ")").Append(' ').Append(Ms(this.uiTicks)).Append(" ms");

            text.Append(" (");
            if (this.draws == 0)
                text.Append("no plugin drew");
            else
                text.Append("plugin drawing ").Append(Ms(this.drawTicks)).Append(" ms in ").Append(this.draws).Append(" draws, slowest ").Append(this.slowestDraw?.Name).Append(' ').Append(Ms(this.slowestDrawTicks)).Append(" ms");

            text.Append("), ");
            if (this.listenerCalls == 0)
            {
                text.Append("no addon event listener ran");
            }
            else
            {
                text.Append("addon event listeners ").Append(Ms(this.listenerTicks)).Append(" ms in ").Append(this.listenerCalls).Append(" calls (slowest ")
                    .Append(this.slowestListener?.Name).Append(' ').Append(Ms(this.slowestListenerTicks)).Append(" ms; longest call ")
                    .Append(this.slowestCallPlugin).Append(' ').Append(this.slowestCallEvent.ToString()).Append(' ')
                    .Append(string.IsNullOrEmpty(this.slowestCallAddon) ? "any addon" : this.slowestCallAddon).Append(' ').Append(Ms(this.slowestCallTicks)).Append(" ms)");
            }

            var rest = Math.Max(0, gapTicks - updateTicks - this.uiTicks - this.listenerTicks);
            return text.Append(", the rest (the game's own work and anything not named) ").Append(Ms(rest)).Append(" ms").ToString();
        }
        catch (Exception ex)
        {
            return "between the two updates: ? (" + ex.GetType().Name + ")";
        }
    }

    private static string Ms(long ticks) => HitchCounters.Milliseconds(ticks).ToString("0.0", CultureInfo.InvariantCulture);

    private long Now() => this.clock == null ? Stopwatch.GetTimestamp() : this.clock();

    // False for a nested span, another thread, or a span a Reset cut through: its time belongs to someone else.
    private bool Exit(long start, out long ticks)
    {
        ticks = 0;
        if (start == 0 || Environment.CurrentManagedThreadId != this.threadId || this.depth == 0)
            return false;

        if (--this.depth != 0)
            return false;

        ticks = this.Now() - start;
        return true;
    }

    // A tally only grows within a gap, so the largest running total seen is the slowest at the end.
    private void Add(Tally tally, long ticks, ref Tally? slowest, ref long slowestTicks)
    {
        if (tally.Generation != this.generation)
        {
            tally.Generation = this.generation;
            tally.Ticks = 0;
        }

        tally.Ticks += ticks;
        if (tally.Ticks > slowestTicks)
        {
            slowest = tally;
            slowestTicks = tally.Ticks;
        }
    }

    /// <summary>One plugin's running total within one gap, kept by its owner so recording needs no lookup.</summary>
    internal sealed class Tally
    {
        /// <summary>Initializes a new instance of the <see cref="Tally"/> class.</summary>
        /// <param name="name">The plugin's internal name, as printed.</param>
        public Tally(string name) => this.Name = name;

        /// <summary>Gets the name printed for it.</summary>
        public string Name { get; }

        /// <summary>Gets or sets the gap this total belongs to.</summary>
        internal long Generation { get; set; }

        /// <summary>Gets or sets the total in that gap.</summary>
        internal long Ticks { get; set; }
    }
}
