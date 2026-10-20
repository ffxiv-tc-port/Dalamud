#nullable enable

using System.Collections.Generic;

using Dalamud.Game.Gui.NamePlate;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class NamePlateGuiHookTargetsTests
    {
        [Fact]
        public void WithoutItsSignatureTheHookIsLeftOut()
        {
            var resolver = new NamePlateGuiAddressResolver();
            resolver.Setup(new Kit.FakeScanner(new Dictionary<string, nint>()));
            var targets = Kit.Synthetic("NamePlateGui");

            Assert.Equal(nint.Zero, resolver.OnRequestedUpdate);
            Assert.Equal(nint.Zero, NamePlateGui.FindHookTarget(targets, resolver));
            Assert.Equal(["OnRequestedUpdate"], targets.SkippedMembers);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(NamePlateGui), nameof(NamePlateGui.FindHookTarget));
    }
}
