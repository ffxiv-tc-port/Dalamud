#nullable enable

using Dalamud.Game.Addon.Events;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class AddonEventManagerHookTargetsTests
    {
        [Fact]
        public void TheCursorHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            var targets = Kit.NoSections("AddonEventManager");

            Assert.Equal(nint.Zero, AddonEventManager.FindHookTarget(targets));
            Assert.Equal(["AtkUnitManager.UpdateCursor"], targets.SkippedMembers);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(AddonEventManager), nameof(AddonEventManager.FindHookTarget));
    }
}
