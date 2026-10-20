#nullable enable

using System;

using Dalamud.Game.Addon.Lifecycle;

using Xunit;

namespace Dalamud.Test.Game.Addon
{
    public class AddonLifecycleCallsiteCheckTests
    {
        [Theory]
        [InlineData("OnSetup")]
        [InlineData("Draw")]
        [InlineData("Update")]
        [InlineData("OnRequestedUpdate")]
        public void Tc725CallsitesReadTheClientStructsSlots(string name)
        {
            var (code, opcode, slot) = Site(name);

            Assert.True(AddonLifecycleCallsiteCheck.Matches(code, opcode, slot, out var evidence), evidence);
        }

        [Theory]
        [InlineData("OnSetup", 8)]
        [InlineData("Draw", 8)]
        [InlineData("Update", -8)]
        [InlineData("OnRequestedUpdate", 8)]
        public void AShiftedSlotIsRefused(string name, int shift)
        {
            var (code, opcode, slot) = Site(name);

            Assert.False(AddonLifecycleCallsiteCheck.Matches(code, opcode, slot + shift, out var evidence));
            Assert.Contains($"+0x{slot:X}", evidence);
            Assert.Contains($"+0x{slot + shift:X}", evidence);
        }

        [Theory]
        [InlineData("E9 11 22 33 44 90", "FF 90")] // already patched into a jmp
        [InlineData("FF 50 60 90 90 90", "FF 90")] // call [rax+disp8]
        [InlineData("FF 91 60 01 00 00", "FF 90")] // call [rcx+disp32]
        [InlineData("4C 8B 89 88 01 00 00", "4C 8B 88")] // mov r9,[rcx+disp32]
        [InlineData("4C 8B 80 88 01 00 00", "4C 8B 88")] // mov r8,[rax+disp32]
        public void AnotherInstructionIsRefused(string code, string opcode)
        {
            var slot = BitConverter.ToInt32(Hex(code), Hex(opcode).Length);

            Assert.False(AddonLifecycleCallsiteCheck.Matches(Hex(code), Hex(opcode), slot, out _));
        }

        [Fact]
        public void ATruncatedInstructionIsRefused()
        {
            var (code, opcode, slot) = Site("Draw");

            Assert.False(AddonLifecycleCallsiteCheck.Matches(code.AsSpan(0, code.Length - 1), opcode, slot, out _));
            Assert.False(AddonLifecycleCallsiteCheck.Matches(ReadOnlySpan<byte>.Empty, opcode, slot, out _));
        }

        [Fact]
        public void BytesAfterTheInstructionAreIgnored()
        {
            var (code, opcode, slot) = Site("OnSetup");
            var longer = new byte[code.Length + 4];
            code.CopyTo(longer, 0);
            longer.AsSpan(code.Length).Fill(0xCC);

            Assert.True(AddonLifecycleCallsiteCheck.Matches(longer, opcode, slot, out _));
        }

        // The instruction at each call site signature's only match in the TC 7.25 executable.
        private static (byte[] Code, byte[] Opcode, int Slot) Site(string name) => name switch
        {
            "OnSetup" => (Hex("4C 8B 88 88 01 00 00"), AddonLifecycleCallsiteCheck.MovR9RaxDisp32, AddonLifecycleCallsiteCheck.OnSetupSlot),
            "Draw" => (Hex("FF 90 60 01 00 00"), AddonLifecycleCallsiteCheck.CallRaxDisp32, AddonLifecycleCallsiteCheck.DrawSlot),
            "Update" => (Hex("FF 90 58 01 00 00"), AddonLifecycleCallsiteCheck.CallRaxDisp32, AddonLifecycleCallsiteCheck.UpdateSlot),
            "OnRequestedUpdate" => (Hex("FF 90 A0 01 00 00"), AddonLifecycleCallsiteCheck.CallRaxDisp32, AddonLifecycleCallsiteCheck.OnRequestedUpdateSlot),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", string.Empty));
    }
}
