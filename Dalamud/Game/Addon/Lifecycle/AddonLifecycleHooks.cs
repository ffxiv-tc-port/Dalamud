using System.Collections.Generic;

namespace Dalamud.Game.Addon.Lifecycle;

/// <summary>
/// Which of AddonLifecycle's hooks are, or can be, installed.
/// </summary>
/// <param name="Setup">The OnSetup call site.</param>
/// <param name="Finalize">The addon finalize function.</param>
/// <param name="Draw">The Draw call site.</param>
/// <param name="Update">The Update call site.</param>
/// <param name="Refresh">RaptureAtkUnitManager's RefreshAddon.</param>
/// <param name="RequestedUpdate">The OnRequestedUpdate call site.</param>
internal readonly record struct AddonLifecycleHooks(bool Setup, bool Finalize, bool Draw, bool Update, bool Refresh, bool RequestedUpdate)
{
    /// <summary>
    /// Decides which hooks to install out of those that can be.
    /// </summary>
    /// <remarks>
    /// PreFinalize is the only event that tells listeners an addon is going away. Without it, a listener that keeps
    /// an addon or node pointer learned from any other event would use it after the game freed it, so nothing is hooked.
    /// </remarks>
    /// <param name="usable">The hooks whose address was found and checked.</param>
    /// <returns>The hooks to install.</returns>
    internal static AddonLifecycleHooks Decide(AddonLifecycleHooks usable) => usable.Finalize ? usable : default;

    /// <summary>
    /// Gets the chat names of the events that are not hooked, in hook order.
    /// </summary>
    /// <returns>The names.</returns>
    internal IEnumerable<string> MissingChatNames()
    {
        if (!this.Setup)
            yield return "開啟";
        if (!this.Finalize)
            yield return "關閉";
        if (!this.Draw)
            yield return "繪製";
        if (!this.Update)
            yield return "更新";
        if (!this.Refresh)
            yield return "重新整理";
        if (!this.RequestedUpdate)
            yield return "資料更新";
    }

    /// <summary>
    /// Gets the chat names of every event plugins will not get, ReceiveEvent's included.
    /// </summary>
    /// <param name="unitBaseVtableFound">Whether ClientStructs has the AtkUnitBase vtable.</param>
    /// <returns><see cref="MissingChatNames"/>, then 操作 when ReceiveEvent is not hooked: its hooks are made only in the OnSetup detour.</returns>
    internal IEnumerable<string> DisabledChatNames(bool unitBaseVtableFound)
    {
        foreach (var name in this.MissingChatNames())
            yield return name;
        if (!unitBaseVtableFound || !this.Setup)
            yield return "操作";
    }
}
