using Dalamud.Plugin.Services;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Dalamud.Game.Addon.Events;

/// <summary>
/// This class represents a registered event that a plugin registers with a native ui node.
/// Contains all necessary information to track and clean up events automatically.
/// </summary>
internal unsafe class AddonEventEntry
{
    /// <summary>
    /// Name of an invalid addon.
    /// </summary>
    public const string InvalidAddonName = "NullAddon";

    private string? addonName;

    /// <summary>
    /// Gets the pointer to the addons AtkUnitBase.
    /// </summary>
    /// <remarks>
    /// This is a snapshot of the address taken when the event was registered; it is never
    /// re-resolved. The addon's memory is released by <c>AtkUnitManager::FinalizeAddon</c> from
    /// the per-frame unit manager update, so on any later frame this address may point at memory
    /// that has already been freed and reused, and dereferencing it then raises an
    /// AccessViolationException - a corrupted-state exception that no managed <c>try</c>/
    /// <c>catch</c> can contain. Treat it as an identity key, and re-resolve the addon by name
    /// via <c>RaptureAtkUnitManager.GetAddonByName</c> before dereferencing.
    /// </remarks>
    public required nint Addon { get; init; }

    /// <summary>
    /// Gets the name of the addon this args referrers to.
    /// </summary>
    public string AddonName => this.Addon == nint.Zero ? InvalidAddonName : this.addonName ??= NativeStringUtil.GetAddonName((AtkUnitBase*)this.Addon);

    /// <summary>
    /// Gets the pointer to the event source.
    /// </summary>
    public required nint Node { get; init; }

    /// <summary>
    /// Gets the delegate that gets called when this event is triggered.
    /// </summary>
    public required IAddonEventManager.AddonEventDelegate Delegate { get; init; }

    /// <summary>
    /// Gets the unique id for this event.
    /// </summary>
    public required uint ParamKey { get; init; }

    /// <summary>
    /// Gets the event type for this event.
    /// </summary>
    public required AddonEventType EventType { get; init; }

    /// <summary>
    /// Gets the event handle for this event.
    /// </summary>
    internal required IAddonEventHandle Handle { get; init; }

    /// <summary>
    /// Gets the formatted log string for this AddonEventEntry.
    /// </summary>
    internal string LogString => $"ParamKey: {this.ParamKey}, Addon: {this.AddonName}, Event: {this.EventType}, GUID: {this.Handle.EventGuid}";
}
