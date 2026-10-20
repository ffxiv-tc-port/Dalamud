using System.Buffers.Binary;

using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Dalamud.Game.Addon.Lifecycle;

/// <summary>
/// Checks that a vfunc call site AddonLifecycle replaces still reads the vtable slot ClientStructs gives the function its detour calls.
/// </summary>
/// <remarks>
/// The signatures wildcard the vtable displacement, so after a patch they can still match while the game reads another slot;
/// the detours call through ClientStructs' slot, so hooking such a site would call the wrong function for every addon.
/// Pure: the caller supplies the bytes (in game: the instruction at the scan result; offline: the executable).
/// </remarks>
internal static unsafe class AddonLifecycleCallsiteCheck
{
    /// <summary>Opcode and ModRM of <c>call qword ptr [rax+disp32]</c>.</summary>
    internal static readonly byte[] CallRaxDisp32 = [0xFF, 0x90];

    /// <summary>REX, opcode and ModRM of <c>mov r9, qword ptr [rax+disp32]</c>.</summary>
    internal static readonly byte[] MovR9RaxDisp32 = [0x4C, 0x8B, 0x88];

    /// <summary>Gets ClientStructs' byte offset of <see cref="AtkUnitBase.OnSetup"/> in the vtable.</summary>
    internal static readonly int OnSetupSlot;

    /// <summary>Gets ClientStructs' byte offset of <see cref="AtkUnitBase.Draw"/> in the vtable.</summary>
    internal static readonly int DrawSlot;

    /// <summary>Gets ClientStructs' byte offset of <see cref="AtkUnitBase.Update"/> in the vtable.</summary>
    internal static readonly int UpdateSlot;

    /// <summary>Gets ClientStructs' byte offset of <see cref="AtkUnitBase.OnRequestedUpdate"/> in the vtable.</summary>
    internal static readonly int OnRequestedUpdateSlot;

    static AddonLifecycleCallsiteCheck()
    {
        var table = default(AtkUnitBase.AtkUnitBaseVirtualTable);
        var start = (byte*)&table;
        OnSetupSlot = (int)((byte*)&table.OnSetup - start);
        DrawSlot = (int)((byte*)&table.Draw - start);
        UpdateSlot = (int)((byte*)&table.Update - start);
        OnRequestedUpdateSlot = (int)((byte*)&table.OnRequestedUpdate - start);
    }

    /// <summary>Gets the length of the instruction a check reads.</summary>
    /// <param name="opcode">The instruction bytes before its disp32.</param>
    /// <returns>The length in bytes.</returns>
    internal static int InstructionLength(ReadOnlySpan<byte> opcode) => opcode.Length + sizeof(int);

    /// <summary>
    /// Checks one call site.
    /// </summary>
    /// <param name="code">The bytes at the call site; only the first <see cref="InstructionLength"/> are read.</param>
    /// <param name="opcode">The instruction bytes before its disp32.</param>
    /// <param name="slot">ClientStructs' vtable byte offset of the function the detour calls.</param>
    /// <param name="evidence">What was read, for the log.</param>
    /// <returns>Whether the instruction is the expected one and reads that slot.</returns>
    internal static bool Matches(ReadOnlySpan<byte> code, ReadOnlySpan<byte> opcode, int slot, out string evidence)
    {
        var length = InstructionLength(opcode);
        if (code.Length < length)
        {
            evidence = $"only {code.Length} of {length} bytes readable";
            return false;
        }

        if (!code[..opcode.Length].SequenceEqual(opcode))
        {
            evidence = $"instruction is {Convert.ToHexString(code[..length])}, expected {Convert.ToHexString(opcode)} + disp32";
            return false;
        }

        var displacement = BinaryPrimitives.ReadInt32LittleEndian(code[opcode.Length..length]);
        evidence = $"game reads vtable+0x{displacement:X}, ClientStructs slot is +0x{slot:X}";
        return displacement == slot;
    }
}
