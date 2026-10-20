#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Dalamud.Game;
using Dalamud.Game.Gui;

using FFXIVClientStructs.FFXIV.Client.UI.Agent;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class GameGuiHookTargetsTests
    {
        [Fact]
        public void WithoutItsSignatureTheResolverLeavesHandleImmZero()
        {
            var resolver = new GameGuiAddressResolver();

            resolver.Setup(new Kit.FakeScanner(new Dictionary<string, nint>()));

            Assert.Equal(nint.Zero, resolver.HandleImm);
        }

        [Fact]
        public void EveryOptionalHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            Assert.Equal(nint.Zero, AgentItemDetail.Addresses.StaticVirtualTable.Value);
            var resolver = new GameGuiAddressResolver();
            resolver.Setup(new Kit.FakeScanner(new Dictionary<string, nint>()));
            var targets = Kit.NoSections("GameGui");

            var hooks = GameGui.FindHookTargets(targets, resolver);

            Assert.Equal(default, hooks);
            Assert.Equal(
                [
                    "AgentItemDetail.Update", "AgentItemDetail.ReceiveEvent", "HandleActionHover", "AgentActionDetail.ReceiveEvent",
                    "HandleImm", "RaptureAtkModule.SetUiVisibility", "RaptureAtkModule.Update",
                ],
                targets.SkippedMembers);
        }

        [Fact]
        public void TheConstructorChecksTheRequiredStringHookBeforeAnythingElse()
        {
            Kit.AssertConstructorUsesHookTargets(typeof(GameGui), nameof(GameGui.FindHookTargets));

            var ctor = typeof(GameGui).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
            var called = Kit.CalledMethods(ctor);
            var inCode = Array.FindIndex(called, m => m?.Name == "InCode");
            var find = Array.FindIndex(called, m => m?.Name == nameof(GameGui.FindHookTargets));

            Assert.InRange(inCode, 0, find - 1);
            Assert.Contains(called, m => m is ConstructorInfo c && c.DeclaringType == typeof(InvalidOperationException));
        }
    }
}
