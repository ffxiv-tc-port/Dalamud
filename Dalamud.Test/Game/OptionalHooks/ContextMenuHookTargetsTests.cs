#nullable enable

using Dalamud.Game.Gui.ContextMenu;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class ContextMenuHookTargetsTests
    {
        [Fact]
        public void BothHooksAreLeftOutWhileClientStructsIsUnresolved()
        {
            var targets = Kit.NoSections("ContextMenu");

            Assert.Equal((nint.Zero, nint.Zero), ContextMenu.FindHookTargets(targets));
            Assert.Equal(["RaptureAtkModule.vf22", "AddonContextMenu.OnMenuSelected"], targets.SkippedMembers);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(ContextMenu), nameof(ContextMenu.FindHookTargets));

        [Fact]
        public void TheMenuHooksAreKeptOnlyAsAPair()
        {
            var both = Kit.Synthetic("ContextMenu");
            Assert.Equal((Kit.InText, Kit.InText + 0x10), ContextMenu.DecideMenu(both, Kit.InText, Kit.InText + 0x10));
            Assert.Empty(both.SkippedMembers);

            // Items added on opening but not handled on selection make the game index past its own event arrays.
            var noSelection = Kit.Synthetic("ContextMenu");
            Assert.Equal((nint.Zero, nint.Zero), ContextMenu.DecideMenu(noSelection, Kit.InText, 0));
            Assert.Equal(["RaptureAtkModule.vf22"], noSelection.SkippedMembers);

            var noOpening = Kit.Synthetic("ContextMenu");
            Assert.Equal((nint.Zero, nint.Zero), ContextMenu.DecideMenu(noOpening, 0, Kit.InText));
            Assert.Equal(["AddonContextMenu.OnMenuSelected"], noOpening.SkippedMembers);
        }
    }
}
