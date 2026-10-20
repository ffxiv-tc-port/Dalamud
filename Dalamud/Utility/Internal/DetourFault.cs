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

    private readonly string name;
    private readonly string feature;
    private long failures;

    /// <summary>
    /// Initializes a new instance of the <see cref="DetourFault"/> class.
    /// </summary>
    /// <param name="name">The detour, as written in the log and used as the chat notice key.</param>
    /// <param name="feature">What the detour does, in the user's language, for the chat line.</param>
    internal DetourFault(string name, string feature)
    {
        this.name = name;
        this.feature = feature;
    }

    /// <summary>Gets how many failures were reported.</summary>
    internal long Failures => Interlocked.Read(ref this.failures);

    /// <summary>Gets the chat line printed for the first failure.</summary>
    internal string ChatLine =>
        $"Dalamud：{this.feature}時出錯，已略過 Dalamud 這部分的處理，遊戲照常運作（多半是遊戲改版所致，請等 Dalamud 更新）。詳情請輸入 /xllog 查看。";

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
            FeatureDisabledEcho.Report($"Detour.{this.name}", this.ChatLine);
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
            FeatureDisabledEcho.Report($"Detour.{this.name}", this.ChatLine);
        }
        catch (Exception)
        {
            // Nothing may escape into the detour's catch block.
        }
    }
}
