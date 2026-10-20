using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

using Dalamud.IoC;
using Dalamud.IoC.Internal;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Dalamud.Utility.Internal;

using Serilog;

namespace Dalamud.Game.ClientState.Keys;

/// <summary>
/// Wrapper around the game keystate buffer, which contains the pressed state for all keyboard keys, indexed by virtual vkCode.
/// </summary>
/// <remarks>
/// The stored key state is actually a combination field, however the below ephemeral states are consumed each frame. Setting
/// the value may be mildly useful, however retrieving the value is largely pointless. In testing, it wasn't possible without
/// setting the statue manually.
/// index &amp; 0 = key pressed.
/// index &amp; 1 = key down (ephemeral).
/// index &amp; 2 = key up (ephemeral).
/// index &amp; 3 = short key press (ephemeral).
/// Without the game's key state (both addresses are needed) every key reads as released and writes are ignored.
/// </remarks>
[PluginInterface]
[ServiceManager.EarlyLoadedService]
#pragma warning disable SA1015
[ResolveVia<IKeyState>]
#pragma warning restore SA1015
internal class KeyState : IServiceType, IKeyState
{
    // The array is accessed in a way that this limit doesn't appear to exist
    // but there is other state data past this point, and keys beyond here aren't
    // generally valid for most things anyway
    private const int MaxKeyCode = 0xF0;

    // Indexed by a byte from the index array, 4 bytes per key.
    private const int BufferLength = 256 * sizeof(int);

    private const string Feature = "鍵盤狀態（插件讀取按鍵）";

    // Both 0 when the game's key state was not found.
    private readonly IntPtr bufferBase;
    private readonly IntPtr indexBase;
    private VirtualKey[]? validVirtualKeyCache;

    [ServiceManager.ServiceConstructor]
    private KeyState(TargetSigScanner sigScanner, ClientState clientState)
    {
        var targets = HookTargets.For(nameof(KeyState), sigScanner);
        (this.bufferBase, this.indexBase) = FindKeyState(targets, sigScanner.Module.BaseAddress, clientState.AddressResolver);
        targets.ReportDisabled();

        Log.Verbose($"Keyboard state buffer address {Util.DescribeAddress(this.bufferBase)}");
    }

    /// <inheritdoc/>
    public bool this[int vkCode]
    {
        get => this.GetRawValue(vkCode) != 0;
        set => this.SetRawValue(vkCode, value ? 1 : 0);
    }

    /// <inheritdoc/>
    public bool this[VirtualKey vkCode]
    {
        get => this[(int)vkCode];
        set => this[(int)vkCode] = value;
    }

    /// <inheritdoc/>
    public int GetRawValue(int vkCode)
        => this.bufferBase == 0 ? 0 : this.GetRefValue(vkCode);

    /// <inheritdoc/>
    public int GetRawValue(VirtualKey vkCode)
        => this.GetRawValue((int)vkCode);

    /// <inheritdoc/>
    public void SetRawValue(int vkCode, int value)
    {
        if (value != 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Dalamud does not support pressing keys, only preventing them via zero or False. If you have a valid use-case for this, please contact the dev team.");

        if (this.bufferBase == 0)
            return;

        this.GetRefValue(vkCode) = value;
    }

    /// <inheritdoc/>
    public void SetRawValue(VirtualKey vkCode, int value)
        => this.SetRawValue((int)vkCode, value);

    /// <inheritdoc/>
    public bool IsVirtualKeyValid(int vkCode)
        => this.ConvertVirtualKey(vkCode) != 0;

    /// <inheritdoc/>
    public bool IsVirtualKeyValid(VirtualKey vkCode)
        => this.IsVirtualKeyValid((int)vkCode);

    /// <inheritdoc/>
    public IEnumerable<VirtualKey> GetValidVirtualKeys()
        => this.validVirtualKeyCache ??= Enum.GetValues<VirtualKey>().Where(this.IsVirtualKeyValid).ToArray();

    /// <inheritdoc/>
    public void ClearAll()
    {
        foreach (var vk in this.GetValidVirtualKeys())
        {
            this[vk] = false;
        }
    }

    /// <summary>
    /// Finds the game's key state buffer and index array, both or neither.
    /// </summary>
    /// <param name="targets">The checks.</param>
    /// <param name="moduleBase">The game module's base address.</param>
    /// <param name="address">ClientState's resolved signatures.</param>
    /// <returns>The buffer and the index array, both 0 if either is missing.</returns>
    internal static (nint Buffer, nint Index) FindKeyState(HookTargets targets, nint moduleBase, ClientStateAddressResolver address)
    {
        var buffer = Locate(targets, nameof(address.KeyboardState), moduleBase, address.KeyboardState, BufferLength);
        var index = Locate(targets, nameof(address.KeyboardStateIndexArray), moduleBase, address.KeyboardStateIndexArray, MaxKeyCode);
        return targets.AllOrNone(Feature, (nameof(address.KeyboardState), buffer), (nameof(address.KeyboardStateIndexArray), index))
                   ? (buffer, index)
                   : (0, 0);
    }

    // The scanned instruction holds the address as a 32-bit offset from the module base; it is read only inside .text.
    private static nint Locate(HookTargets targets, string member, nint moduleBase, nint offsetAddress, int length)
    {
        if (offsetAddress == 0)
            return targets.Skip(member, Feature, HookTargets.SignatureNotFound);

        if (!targets.InCode(offsetAddress, sizeof(int)))
            return targets.Skip(member, Feature, $"0x{offsetAddress:X} is not in the game's code");

        return targets.Data(member, Feature, moduleBase + Marshal.ReadInt32(offsetAddress), length);
    }

    /// <summary>
    /// Converts a virtual key into the equivalent value that the game uses.
    /// Valid values are non-zero.
    /// </summary>
    /// <param name="vkCode">Virtual key.</param>
    /// <returns>Converted value.</returns>
    private unsafe byte ConvertVirtualKey(int vkCode)
    {
        if (vkCode <= 0 || vkCode >= MaxKeyCode || this.indexBase == 0)
            return 0;

        return *(byte*)(this.indexBase + vkCode);
    }

    /// <summary>
    /// Gets the raw value from the key state array.
    /// </summary>
    /// <param name="vkCode">Virtual key code.</param>
    /// <returns>A reference to the indexed array.</returns>
    private unsafe ref int GetRefValue(int vkCode)
    {
        vkCode = this.ConvertVirtualKey(vkCode);

        if (vkCode == 0)
            throw new ArgumentException($"Keycode state is only valid for certain values. Reference GetValidVirtualKeys for help.");

        return ref *(int*)(this.bufferBase + (4 * vkCode));
    }
}
