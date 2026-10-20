#nullable enable

using System.Collections.Generic;

using Dalamud.Game.DutyState;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class DutyStateHookTargetsTests
    {
        [Fact]
        public void WithoutItsSignatureTheHookIsLeftOut()
        {
            var resolver = new DutyStateAddressResolver();
            resolver.Setup(new Kit.FakeScanner(new Dictionary<string, nint>()));
            var targets = Kit.Synthetic("DutyState");

            Assert.Equal(nint.Zero, resolver.ContentDirectorNetworkMessage);
            Assert.Equal(nint.Zero, DutyState.FindHookTarget(targets, resolver));
            Assert.Equal(["ContentDirectorNetworkMessage"], targets.SkippedMembers);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(DutyState), nameof(DutyState.FindHookTarget));
    }
}
