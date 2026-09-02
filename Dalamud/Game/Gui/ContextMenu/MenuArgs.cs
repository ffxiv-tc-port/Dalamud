using System.Collections.Generic;

using Dalamud.Memory;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Dalamud.Game.Gui.ContextMenu;

/// <summary>
/// Interface representing a context menus args.
/// </summary>
public interface IMenuArgs
{
    /// <summary>
    /// Gets a list of AtkEventInterface pointers associated with the context menu.
    /// Only available with <see cref="ContextMenuType.Default"/>.
    /// Almost always an agent pointer. You can use this to find out what type of context menu it is.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the context menu is not a <see cref="ContextMenuType.Default"/>.</exception>
    public IReadOnlySet<nint> EventInterfaces { get; }

    /// <summary>
    /// Gets the name of the addon that opened the context menu.
    /// </summary>
    public string? AddonName { get; }

    /// <summary>
    /// Gets the memory pointer of the addon that opened the context menu.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a snapshot of the address taken when the context menu was opened; it is never
    /// re-resolved. It is safe for the duration of the callback that received these args, and
    /// must not be stored and used on a later frame: an addon's memory is released by
    /// <c>AtkUnitManager::FinalizeAddon</c> from the per-frame unit manager update, so once a
    /// frame boundary has passed this address may point at memory that has already been freed
    /// and reused. Dereferencing it then raises an AccessViolationException, a corrupted-state
    /// exception that no managed <c>try</c>/<c>catch</c> can contain.
    /// </para>
    /// <para>
    /// To hold on to the addon, store <see cref="AddonName"/> and re-resolve it on each frame
    /// via <c>RaptureAtkUnitManager.GetAddonByName</c>. An address that still resolves that way
    /// has not been finalized, so it is safe to dereference within that same call; note that it
    /// does not prove the addon is still open, because closing an addon does not remove it from
    /// the list that lookup searches.
    /// </para>
    /// </remarks>
    public nint AddonPtr { get; }

    /// <summary>
    /// Gets the memory pointer of the agent that opened the context menu.
    /// </summary>
    public nint AgentPtr { get; }

    /// <summary>
    /// Gets the type of the context menu.
    /// </summary>
    public ContextMenuType MenuType { get; }

    /// <summary>
    /// Gets the target info of the context menu. The actual type depends on <see cref="MenuType"/>.
    /// <see cref="ContextMenuType.Default"/> signifies a <see cref="MenuTargetDefault"/>.
    /// <see cref="ContextMenuType.Inventory"/> signifies a <see cref="MenuTargetInventory"/>.
    /// </summary>
    public MenuTarget Target { get; }
}

/// <summary>
/// Base class for <see cref="IContextMenu"/> menu args.
/// </summary>
internal abstract unsafe class MenuArgs : IMenuArgs
{
    private IReadOnlySet<nint>? eventInterfaces;

    /// <summary>
    /// Initializes a new instance of the <see cref="MenuArgs"/> class.
    /// </summary>
    /// <param name="addon">Addon associated with the context menu.</param>
    /// <param name="agent">Agent associated with the context menu.</param>
    /// <param name="type">The type of context menu.</param>
    /// <param name="eventInterfaces">List of AtkEventInterfaces associated with the context menu.</param>
    protected internal MenuArgs(AtkUnitBase* addon, AgentInterface* agent, ContextMenuType type, IReadOnlySet<nint>? eventInterfaces)
    {
        this.AddonName = addon != null ? NativeStringUtil.GetAddonName(addon) : null;
        this.AddonPtr = (nint)addon;
        this.AgentPtr = (nint)agent;
        this.MenuType = type;
        this.eventInterfaces = eventInterfaces;
        this.Target = type switch
        {
            ContextMenuType.Default => new MenuTargetDefault((AgentContext*)agent),
            ContextMenuType.Inventory => new MenuTargetInventory((AgentInventoryContext*)agent),
            _ => throw new ArgumentException("Invalid context menu type", nameof(type)),
        };
    }

    /// <inheritdoc/>
    public string? AddonName { get; }

    /// <inheritdoc/>
    public nint AddonPtr { get; }

    /// <inheritdoc/>
    public nint AgentPtr { get; }

    /// <inheritdoc/>
    public ContextMenuType MenuType { get; }

    /// <inheritdoc/>
    public MenuTarget Target { get; }

    /// <inheritdoc/>
    public IReadOnlySet<nint> EventInterfaces 
    {
        get
        {
            if (this.MenuType is ContextMenuType.Default)
            {
                return this.eventInterfaces ?? new HashSet<nint>();
            }
            else
            {
                throw new InvalidOperationException("Not a default context menu");
            }
        }
    }
}
