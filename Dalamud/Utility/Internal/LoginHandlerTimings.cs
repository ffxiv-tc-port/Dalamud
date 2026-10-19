using System.Collections.Generic;
using System.Linq;

using Dalamud.Logging.Internal;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Collects how long each login handler ran during one login, then logs at most <c>maxLines</c> lines about it.
/// </summary>
/// <remarks>
/// Not thread safe: one instance per login, used on the thread that raises the login event.
/// Diagnostic for users to report back: which handler makes the login frame slow.
/// </remarks>
internal sealed class LoginHandlerTimings
{
    private static readonly ModuleLog Log = new("PerfDiag");

    private readonly TimeSpan slow;
    private readonly int maxLines;
    private readonly List<(string Name, TimeSpan Elapsed)> handlers = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginHandlerTimings"/> class: 50 ms counts as slow, 3 lines at most.
    /// </summary>
    public LoginHandlerTimings()
        : this(TimeSpan.FromMilliseconds(50), 3)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginHandlerTimings"/> class.
    /// </summary>
    /// <param name="slow">A handler running at least this long gets its own line.</param>
    /// <param name="maxLines">Most lines to write for one login.</param>
    public LoginHandlerTimings(TimeSpan slow, int maxLines)
    {
        this.slow = slow;
        this.maxLines = Math.Max(1, maxLines);
    }

    /// <summary>
    /// Records one handler.
    /// </summary>
    /// <param name="name">Plugin or handler name.</param>
    /// <param name="elapsed">How long it ran.</param>
    public void Record(string name, TimeSpan elapsed) => this.handlers.Add((name, elapsed));

    /// <summary>
    /// Writes one line per slow handler, slowest first, or a single line saying none was slow.
    /// </summary>
    public void WriteToLog()
    {
        var total = TimeSpan.FromTicks(this.handlers.Sum(x => x.Elapsed.Ticks));
        var slowOnes = this.handlers.Where(x => x.Elapsed >= this.slow).OrderByDescending(x => x.Elapsed).ToList();
        if (slowOnes.Count == 0)
        {
            var slowest = this.handlers.Count == 0 ? (Name: "-", Elapsed: TimeSpan.Zero) : this.handlers.MaxBy(x => x.Elapsed);
            Log.Information(
                "Login handlers took {TotalMs:0.0} ms in total ({Count} handlers, none {SlowMs:0} ms or longer; slowest {Slowest} {SlowestMs:0.0} ms)",
                total.TotalMilliseconds,
                this.handlers.Count,
                this.slow.TotalMilliseconds,
                slowest.Name,
                slowest.Elapsed.TotalMilliseconds);
            return;
        }

        for (var i = 0; i < Math.Min(this.maxLines, slowOnes.Count); i++)
        {
            Log.Information(
                "Login handler {Name} took {Ms:0.0} ms (#{Rank} of {SlowCount} at {SlowMs:0} ms or longer; all {Count} handlers {TotalMs:0.0} ms)",
                slowOnes[i].Name,
                slowOnes[i].Elapsed.TotalMilliseconds,
                i + 1,
                slowOnes.Count,
                this.slow.TotalMilliseconds,
                this.handlers.Count,
                total.TotalMilliseconds);
        }
    }
}
