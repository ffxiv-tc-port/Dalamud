namespace Dalamud.Game.Gui.NamePlate;

/// <summary>
/// An address resolver for the <see cref="NamePlateGui"/> class.
/// </summary>
/// <remarks>A signature that does not match leaves its address 0 instead of throwing.</remarks>
internal class NamePlateGuiAddressResolver : BaseAddressResolver
{
    /// <summary>
    /// Gets the address of the AddonNamePlate OnRequestedUpdate method. We need to use a hook for this because
    /// AddonNamePlate.Show calls OnRequestedUpdate directly, bypassing the AddonLifecycle callsite hook.
    /// </summary>
    public IntPtr OnRequestedUpdate { get; private set; }

    /// <inheritdoc/>
    protected override void Setup64Bit(ISigScanner sig)
    {
        this.OnRequestedUpdate = sig.TryScanText(
                                     "4C 8B DC 41 56 48 81 EC ?? ?? ?? ?? 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 ?? ?? ?? ?? 49 8B 40 20",
                                     out var onRequestedUpdate)
                                     ? onRequestedUpdate
                                     : 0;
    }
}
