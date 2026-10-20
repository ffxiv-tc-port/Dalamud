using System.Collections.Generic;
using System.Threading;

using Dalamud.Logging.Internal;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Failure record of one of Dalamud's own detours.
/// </summary>
/// <remarks>
/// A detour catches its own exceptions (one escaping into the game crashes it) and reports them here: the first is
/// logged as an error and echoed to chat once, later ones are only counted, so a detour failing every frame does not
/// flood the log. Thread-safe and never throws, so it can be called from a detour's catch block.
/// </remarks>
internal sealed class DetourFault
{
    private static readonly ModuleLog Log = new("DETOUR");

    // Every instance ever made; reportedFailures is read and written only under this lock.
    private static readonly List<DetourFault> Registry = new();

    private readonly string name;
    private readonly string feature;
    private long failures;
    private long reportedFailures;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetourFault"/> class.
    /// </summary>
    /// <param name="name">The detour, as written in the log and used as the chat notice key.</param>
    /// <param name="feature">What the detour does, in the user's language, for the chat line.</param>
    internal DetourFault(string name, string feature)
    {
        this.name = name;
        this.feature = feature;
        lock (Registry)
            Registry.Add(this);
    }

    /// <summary>Gets how many failures were reported.</summary>
    internal long Failures => Interlocked.Read(ref this.failures);

    /// <summary>
    /// Takes the failures counted since the last call, for every detour that had any.
    /// </summary>
    /// <param name="faults">The detours to look at; all of them if null.</param>
    /// <returns>One line per detour that failed since the last call.</returns>
    internal static List<SummaryLine> TakeSummary(IReadOnlyList<DetourFault>? faults = null)
    {
        var lines = new List<SummaryLine>();
        lock (Registry)
        {
            foreach (var fault in faults ?? Registry)
            {
                var session = fault.Failures;
                if (session <= fault.reportedFailures)
                    continue;

                lines.Add(new SummaryLine(fault.name, session - fault.reportedFailures, session));
                fault.reportedFailures = session;
            }
        }

        return lines;
    }

    /// <summary>
    /// Logs, as Information, how often each detour failed since the last summary. Nothing if none did. Never throws.
    /// </summary>
    /// <param name="minutes">How long the summary covers.</param>
    /// <param name="unloading">Whether this is the last summary, written while Dalamud unloads.</param>
    internal static void WriteSummary(double minutes, bool unloading)
    {
        try
        {
            foreach (var line in TakeSummary())
            {
                Log.Information(
                    "summary {Detour}: failed {Window} time(s) in the last {Minutes:0.0} min, {Session} this session{Unloading:l}",
                    line.Name,
                    line.Window,
                    minutes,
                    line.Session,
                    unloading ? " (unloading)" : string.Empty);
            }
        }
        catch (Exception ex)
        {
            try
            {
                Log.Error(ex, "Could not write the detour failure summary");
            }
            catch
            {
                // Logging is all that is left to try.
            }
        }
    }

    /// <summary>
    /// Gets the chat line printed for the first failure.
    /// </summary>
    /// <remarks>
    /// Only a missing game address is surely the game update; any other exception can as well come from what a plugin
    /// handed in (a null SeString and the like), so that line names both.
    /// </remarks>
    /// <param name="exception">What the detour threw, or null when it reported something missing.</param>
    /// <returns>The line.</returns>
    internal string ChatLineFor(Exception? exception)
        => exception == null || UnresolvedAddressLog.TryGetUnresolvedAddresses(exception, out _)
               ? $"Dalamud：{this.feature}時出錯，已略過 Dalamud 這部分的處理，遊戲照常運作（多半是遊戲改版所致，請等 Dalamud 更新）。詳情請輸入 /xllog 查看。"
               : $"Dalamud：{this.feature}時出錯，已略過 Dalamud 這部分的處理，遊戲照常運作（可能是遊戲改版，也可能是某個插件傳入的內容有問題）。詳情請輸入 /xllog 查看。";

    /// <summary>
    /// Records a failure. Never throws.
    /// </summary>
    /// <param name="exception">What the detour threw.</param>
    internal void Report(Exception exception)
    {
        if (Interlocked.Increment(ref this.failures) != 1)
            return;

        try
        {
            Log.Error(exception, "{Detour} failed and fell back for this call; later failures of it are counted, not logged", this.name);
            FeatureDisabledEcho.Report($"Detour.{this.name}", this.ChatLineFor(exception));
        }
        catch (Exception)
        {
            // Nothing may escape into the detour's catch block.
        }
    }

    /// <summary>
    /// Records that the detour skipped its own work because something it needs is missing. Never throws.
    /// </summary>
    /// <param name="reason">What is missing.</param>
    internal void Report(string reason)
    {
        if (Interlocked.Increment(ref this.failures) != 1)
            return;

        try
        {
            Log.Error("{Detour} skips its own handling: {Reason}", this.name, reason);
            FeatureDisabledEcho.Report($"Detour.{this.name}", this.ChatLineFor(null));
        }
        catch (Exception)
        {
            // Nothing may escape into the detour's catch block.
        }
    }

    /// <summary>One detour's failures for a summary.</summary>
    /// <param name="Name">The detour.</param>
    /// <param name="Window">Failures since the previous summary.</param>
    /// <param name="Session">Failures since Dalamud started.</param>
    internal readonly record struct SummaryLine(string Name, long Window, long Session);
}
