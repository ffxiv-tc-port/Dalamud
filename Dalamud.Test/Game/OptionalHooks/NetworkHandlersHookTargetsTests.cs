#nullable enable

using System.Collections.Generic;

using Dalamud.Game.Network.Internal;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class NetworkHandlersHookTargetsTests
    {
        private static readonly NetworkHandlers.HookAddresses All = new(0x1100, 0x1200, 0x1300, 0x1400, 0x1500, 0x1600, 0x1700);

        public static IEnumerable<object[]> MarketMembers()
        {
            yield return ["Purchase"];
            yield return ["History"];
            yield return ["ItemRequestStart"];
            yield return ["Offerings"];
            yield return ["SendPurchaseRequest"];
        }

        [Fact]
        public void WithoutItsSignatureTheResolverLeavesCustomTalkZero()
        {
            var resolver = new NetworkHandlersAddressResolver();

            resolver.Setup(new Kit.FakeScanner(new Dictionary<string, nint>()));

            Assert.Equal(nint.Zero, resolver.CustomTalkEventResponsePacketHandler);
        }

        [Fact]
        public void EveryHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            var resolver = new NetworkHandlersAddressResolver();
            resolver.Setup(new Kit.FakeScanner(new Dictionary<string, nint>()));
            var targets = Kit.NoSections("NetworkHandlers");

            var hooks = NetworkHandlers.FindHookTargets(targets, resolver);

            Assert.Equal(default, hooks);
            Assert.False(hooks.Market);
            Assert.Equal(7, targets.SkippedMembers.Count);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(NetworkHandlers), nameof(NetworkHandlers.FindHookTargets));

        [Fact]
        public void AllMarketHooksTogetherAreKept()
        {
            var targets = Kit.Synthetic("NetworkHandlers");

            Assert.Equal(All, NetworkHandlers.DecideMarket(targets, All));
            Assert.True(All.Market);
            Assert.Empty(targets.SkippedMembers);
        }

        // A partial set would upload part of a query, so one missing market hook drops all five; tax and CfPop stay.
        [Theory]
        [MemberData(nameof(MarketMembers))]
        public void OneMissingMarketHookDropsAllFive(string missing)
        {
            var targets = Kit.Synthetic("NetworkHandlers");
            var partial = missing switch
            {
                "Purchase" => All with { Purchase = 0 },
                "History" => All with { History = 0 },
                "ItemRequestStart" => All with { ItemRequestStart = 0 },
                "Offerings" => All with { Offerings = 0 },
                _ => All with { SendPurchaseRequest = 0 },
            };

            var decided = NetworkHandlers.DecideMarket(targets, partial);

            Assert.Equal(new NetworkHandlers.HookAddresses(0, 0, 0, 0, 0, All.CustomTalk, All.CfPop), decided);
            Assert.False(decided.Market);
            Assert.Equal(4, targets.SkippedMembers.Count);
        }
    }
}
