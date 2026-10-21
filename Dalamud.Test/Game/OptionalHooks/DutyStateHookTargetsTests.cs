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
        public void TheNoticeSaysWhatIsDutyStartedFollowsWithoutTheHook()
        {
            var resolver = new DutyStateAddressResolver();
            resolver.Setup(new Kit.FakeScanner(new Dictionary<string, nint>()));
            var targets = Kit.Synthetic("DutyState");
            DutyState.FindHookTarget(targets, resolver);

            var line = targets.FormatChatLine(DutyState.DisabledChatTail);

            Assert.NotNull(line);
            Assert.Contains("副本開始、全滅、完成事件", line);
            Assert.EndsWith(DutyState.DisabledChatTail, line);
            Assert.Contains("副本完成後到離開副本前仍算進行中", line);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(DutyState), nameof(DutyState.FindHookTarget));
    }
}
