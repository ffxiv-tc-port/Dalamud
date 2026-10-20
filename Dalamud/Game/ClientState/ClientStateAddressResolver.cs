using System.Collections.Generic;

namespace Dalamud.Game.ClientState;

/// <summary>
/// Client state memory address resolver.
/// </summary>
/// <remarks>
/// Only the ZoneInit handler is required (it throws when not found; see <see cref="Utility.Internal.CoreAddressPreflight"/>).
/// Any other signature that does not match leaves its address 0 and its name in <see cref="NotFound"/>.
/// </remarks>
internal sealed class ClientStateAddressResolver : BaseAddressResolver
{
    /// <summary>The ZoneInit handler, also checked by <see cref="Utility.Internal.CoreAddressPreflight"/>.</summary>
    internal const string HandleZoneInitPacketSignature = "E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 44 0F B6 45";

    private readonly List<string> notFound = new();

    // Static offsets

    /// <summary>
    /// Gets the address of the keyboard state.
    /// </summary>
    public nint KeyboardState { get; private set; }

    /// <summary>
    /// Gets the address of the keyboard state index array which translates the VK enumeration to the key state.
    /// </summary>
    public nint KeyboardStateIndexArray { get; private set; }

    // Functions

    /// <summary>
    /// Gets the address of the method that handles the ZoneInit packet.
    /// </summary>
    public nint HandleZoneInitPacket { get; private set; }

    /// <summary>
    /// Gets the address of the method that sets the current public instance.
    /// </summary>
    public nint SetCurrentInstance { get; private set; }

    /// <summary>
    /// Gets the names of the optional addresses whose signature did not match; each of those is 0.
    /// </summary>
    public IReadOnlyList<string> NotFound => this.notFound;

    /// <summary>
    /// Scan for and setup any configured address pointers.
    /// </summary>
    /// <param name="sig">The signature scanner to facilitate setup.</param>
    protected override void Setup64Bit(ISigScanner sig)
    {
        this.HandleZoneInitPacket = sig.ScanText(HandleZoneInitPacketSignature);
        this.SetCurrentInstance = this.Scan(sig, nameof(this.SetCurrentInstance), "E8 ?? ?? ?? ?? 0F B6 55 ?? 48 8D 0D ?? ?? ?? ?? C0 EA"); // NetworkModuleProxy.SetCurrentInstance

        // These resolve to fixed offsets only, without the base address added in, so GetStaticAddressFromSig() can't be used.
        // lea   rcx, ds:1DB9F74h[rax*4]          KeyboardState
        // movzx edx, byte ptr [rbx+rsi+1D5E0E0h] KeyboardStateIndexArray
        this.KeyboardState = this.Scan(sig, nameof(this.KeyboardState), "48 8D 0C 85 ?? ?? ?? ?? 8B 04 31 85 C2 0F 85", 0x4);
        this.KeyboardStateIndexArray = this.Scan(sig, nameof(this.KeyboardStateIndexArray), "0F B6 94 33 ?? ?? ?? ?? 84 D2", 0x4);
    }

    private nint Scan(ISigScanner sig, string name, string signature, int offset = 0)
    {
        if (sig.TryScanText(signature, out var address))
            return address + offset;

        this.notFound.Add(name);
        return 0;
    }
}
