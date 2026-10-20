#nullable enable

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Utility.Internal;

using FFXIVClientStructs.FFXIV.Client.UI;

using Xunit;

using ClientStateService = Dalamud.Game.ClientState.ClientState;
using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class ClientStateHookTargetsTests
    {
        private const string ZoneInitSig = ClientStateAddressResolver.HandleZoneInitPacketSignature;
        private const string SetCurrentInstanceSig = "E8 ?? ?? ?? ?? 0F B6 55 ?? 48 8D 0D ?? ?? ?? ?? C0 EA";
        private const string KeyboardStateSig = "48 8D 0C 85 ?? ?? ?? ?? 8B 04 31 85 C2 0F 85";
        private const string KeyboardIndexSig = "0F B6 94 33 ?? ?? ?? ?? 84 D2";

        [Fact]
        public void OnlyZoneInitIsRequiredFromTheResolver()
        {
            var resolver = Resolve(new() { [ZoneInitSig] = 0x1234 });

            Assert.Equal((nint)0x1234, resolver.HandleZoneInitPacket);
            Assert.Equal(["SetCurrentInstance", "KeyboardState", "KeyboardStateIndexArray"], resolver.NotFound);

            // Not 4: the +4 to the displacement is applied only to a match.
            Assert.Equal(nint.Zero, resolver.KeyboardState);
            Assert.Equal(nint.Zero, resolver.KeyboardStateIndexArray);
            Assert.Equal(nint.Zero, resolver.SetCurrentInstance);
        }

        [Fact]
        public void WithoutZoneInitTheResolverStillThrows()
        {
            Assert.Throws<KeyNotFoundException>(() => Resolve(new()));
        }

        [Fact]
        public void AMatchKeepsItsAddressAndDisplacementShift()
        {
            var resolver = Resolve(new()
            {
                [ZoneInitSig] = 0x1234,
                [SetCurrentInstanceSig] = 0x2000,
                [KeyboardStateSig] = 0x3000,
                [KeyboardIndexSig] = 0x4000,
            });

            Assert.Empty(resolver.NotFound);
            Assert.Equal((nint)0x2000, resolver.SetCurrentInstance);
            Assert.Equal((nint)0x3004, resolver.KeyboardState);
            Assert.Equal((nint)0x4004, resolver.KeyboardStateIndexArray);
        }

        [Fact]
        public void OptionalHooksAreLeftOutWhileClientStructsIsUnresolved()
        {
            Assert.Equal(nint.Zero, UIModule.Addresses.StaticVirtualTable.Value);
            var targets = Kit.NoSections("ClientState");

            var (handlePacket, setCurrentInstance) = ClientStateService.FindHookTargets(targets, Resolve(new() { [ZoneInitSig] = 0x1234 }));

            Assert.Equal(nint.Zero, handlePacket);
            Assert.Equal(nint.Zero, setCurrentInstance);
            Assert.Equal(["UIModule.HandlePacket", "SetCurrentInstance"], targets.SkippedMembers);
            Assert.Equal(2, targets.DisabledFeatures.Count);
        }

        [Fact]
        public void TheConstructorsGoThroughHookTargets()
        {
            Kit.AssertConstructorUsesHookTargets(typeof(ClientStateService), nameof(ClientStateService.FindHookTargets));
            Kit.AssertConstructorUsesHookTargets(typeof(KeyState), nameof(KeyState.FindKeyState));
        }

        [Fact]
        public void KeyStateIsUsedOnlyWithBothItsAddressesInsideData()
        {
            const nint moduleBase = 0x10000;
            var code = Marshal.AllocHGlobal(8);
            try
            {
                var layout = Kit.Layout with { Text = new(code, 8), Data = new(moduleBase + 0x100, 0x1000) };
                var resolver = Resolve(new() { [ZoneInitSig] = 0x1234, [KeyboardStateSig] = code - 4, [KeyboardIndexSig] = code });

                // Both inside .data: buffer 0x100..0x500, index 0x600..0x6F0.
                Marshal.WriteInt32(code, 0x100);
                Marshal.WriteInt32(code + 4, 0x600);
                var both = new HookTargets("KeyState", layout);
                Assert.Equal((moduleBase + 0x100, moduleBase + 0x600), KeyState.FindKeyState(both, moduleBase, resolver));
                Assert.Empty(both.SkippedMembers);

                // The index array would end past .data: neither is used.
                Marshal.WriteInt32(code + 4, 0x1050);
                var one = new HookTargets("KeyState", layout);
                Assert.Equal((nint.Zero, nint.Zero), KeyState.FindKeyState(one, moduleBase, resolver));
                Assert.Equal(["KeyboardStateIndexArray", "KeyboardState"], one.SkippedMembers);
            }
            finally
            {
                Marshal.FreeHGlobal(code);
            }
        }

        [Fact]
        public void KeyStateIsNotUsedWhenItsSignaturesDidNotMatch()
        {
            var targets = Kit.Synthetic("KeyState");

            Assert.Equal((nint.Zero, nint.Zero), KeyState.FindKeyState(targets, 0x10000, Resolve(new() { [ZoneInitSig] = 0x1234 })));
            Assert.Equal(["KeyboardState", "KeyboardStateIndexArray"], targets.SkippedMembers);
        }

        [Fact]
        public void WithoutTheGamesKeyStateEveryKeyIsReleasedAndWritesAreIgnored()
        {
            var keys = (KeyState)RuntimeHelpers.GetUninitializedObject(typeof(KeyState));

            Assert.False(keys[VirtualKey.A]);
            Assert.Equal(0, keys.GetRawValue(VirtualKey.A));
            Assert.False(keys.IsVirtualKeyValid(VirtualKey.A));
            Assert.Empty(keys.GetValidVirtualKeys());
            keys[VirtualKey.A] = false;
            keys.SetRawValue(VirtualKey.ESCAPE, 0);
            keys.ClearAll();

            // The contract on pressing keys is unchanged.
            Assert.Throws<ArgumentOutOfRangeException>(() => keys.SetRawValue(VirtualKey.A, 1));
        }

        private static ClientStateAddressResolver Resolve(Dictionary<string, nint> found)
        {
            var resolver = new ClientStateAddressResolver();
            resolver.Setup(new Kit.FakeScanner(found));
            return resolver;
        }
    }
}
