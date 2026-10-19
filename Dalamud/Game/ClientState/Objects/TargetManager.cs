using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Player;
using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.Game.Control;

using CSGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

#pragma warning disable CS0618

namespace Dalamud.Game.ClientState.Objects;

/// <summary>
/// Get and set various kinds of targets for the player.
/// </summary>
[PluginInterface]
[ServiceManager.EarlyLoadedService]
#pragma warning disable SA1015
[ResolveVia<ITargetManager>]
#pragma warning restore SA1015
internal sealed unsafe class TargetManager : IServiceType, ITargetManager
{
    [ServiceManager.ServiceDependency]
    private readonly ObjectTable objectTable = Service<ObjectTable>.Get();

    [ServiceManager.ServiceDependency]
    private readonly PlayerState playerState = Service<PlayerState>.Get();

    private ReferenceCache hardTarget;
    private ReferenceCache mouseOverTarget;
    private ReferenceCache focusTarget;
    private ReferenceCache previousTarget;
    private ReferenceCache softTarget;
    private ReferenceCache gposeTarget;
    private ReferenceCache mouseOverNameplateTarget;

    [ServiceManager.ServiceConstructor]
    private TargetManager()
    {
    }

    /// <inheritdoc/>
    public IGameObject? Target
    {
        get => this.GetReference((IntPtr)Struct->GetHardTarget(), ref this.hardTarget);
        set => Struct->SetHardTarget((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)value?.Address);
    }

    /// <inheritdoc/>
    public IGameObject? MouseOverTarget
    {
        get => this.GetReference((IntPtr)Struct->MouseOverTarget, ref this.mouseOverTarget);
        set => Struct->MouseOverTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)value?.Address;
    }

    /// <inheritdoc/>
    public IGameObject? FocusTarget
    {
        get => this.GetReference((IntPtr)Struct->FocusTarget, ref this.focusTarget);
        set => Struct->FocusTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)value?.Address;
    }

    /// <inheritdoc/>
    public IGameObject? PreviousTarget
    {
        get => this.GetReference((IntPtr)Struct->PreviousTarget, ref this.previousTarget);
        set => Struct->PreviousTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)value?.Address;
    }

    /// <inheritdoc/>
    public IGameObject? SoftTarget
    {
        get => this.GetReference((IntPtr)Struct->GetSoftTarget(), ref this.softTarget);
        set => Struct->SetSoftTarget((FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)value?.Address);
    }

    /// <inheritdoc/>
    public IGameObject? GPoseTarget
    {
        get => this.GetReference((IntPtr)Struct->GPoseTarget, ref this.gposeTarget);
        set => Struct->GPoseTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)value?.Address;
    }

    /// <inheritdoc/>
    public IGameObject? MouseOverNameplateTarget
    {
        get => this.GetReference((IntPtr)Struct->MouseOverNameplateTarget, ref this.mouseOverNameplateTarget);
        set => Struct->MouseOverNameplateTarget = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)value?.Address;
    }

    private TargetSystem* Struct => TargetSystem.Instance();

    /// <summary>
    /// Same result as <see cref="ObjectTable.CreateObjectReference"/>, except that on the main thread the wrapper
    /// from the previous call is handed back while the address and native object kind are both unchanged.
    /// Wrappers carry no state besides their address and a cached wrapper is never re-pointed, so a caller
    /// holding an earlier result still sees the address it was created with.
    /// </summary>
    private IGameObject? GetReference(nint address, ref ReferenceCache cache)
    {
        // Off the main thread the cache is left alone: the original path runs, including its warning.
        if (!ThreadSafety.IsMainThread)
            return this.objectTable.CreateObjectReference(address);

        if (address == nint.Zero || !this.playerState.IsLoaded)
            return null;

        var kind = (byte)((CSGameObject*)address)->ObjectKind;
        if (cache.Object is { } cached && cache.Address == address && cache.Kind == kind)
            return cached;

        var obj = this.objectTable.CreateObjectReference(address);
        cache = new ReferenceCache { Address = address, Kind = kind, Object = obj };
        return obj;
    }

    private struct ReferenceCache
    {
        public nint Address;
        public byte Kind;
        public IGameObject? Object;
    }
}
