#nullable enable

using System.Collections.Generic;

using Dalamud.Game.Network;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class GameNetworkHookTargetsTests
    {
        [Fact]
        public void BothHooksAreLeftOutWhenNothingResolves()
        {
            var resolver = new GameNetworkAddressResolver();
            resolver.Setup(new Kit.FakeScanner(new Dictionary<string, nint>()));
            var targets = Kit.NoSections("GameNetwork");

            Assert.Equal(nint.Zero, resolver.ProcessZonePacketUp);
            Assert.Equal((nint.Zero, nint.Zero), GameNetwork.FindHookTargets(targets, resolver));
            Assert.Equal(["PacketDispatcher.OnReceivePacket", "ProcessZonePacketUp"], targets.SkippedMembers);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(GameNetwork), nameof(GameNetwork.FindHookTargets));
    }
}
