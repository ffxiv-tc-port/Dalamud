#nullable enable

using Dalamud.Game.Internal;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class DalamudAtkTweaksHookTargetsTests
    {
        [Fact]
        public void EveryHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            var targets = Kit.NoSections("DalamudAtkTweaks");

            Assert.Equal((nint.Zero, nint.Zero, nint.Zero), DalamudAtkTweaks.FindHookTargets(targets));
            Assert.Equal(["AgentHUD.OpenSystemMenu", "UIModule.ExecuteMainCommand", "AtkUnitBase.ReceiveGlobalEvent"], targets.SkippedMembers);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(DalamudAtkTweaks), nameof(DalamudAtkTweaks.FindHookTargets));

        [Fact]
        public void TheSystemMenuHooksAreKeptOnlyAsAPair()
        {
            var both = Kit.Synthetic("DalamudAtkTweaks");
            Assert.Equal((Kit.InText, Kit.InText + 0x10), DalamudAtkTweaks.DecideSystemMenu(both, Kit.InText, Kit.InText + 0x10));
            Assert.Empty(both.SkippedMembers);

            // Entries in the menu without the command handler would send the game command ids it does not have.
            var noCommand = Kit.Synthetic("DalamudAtkTweaks");
            Assert.Equal((nint.Zero, nint.Zero), DalamudAtkTweaks.DecideSystemMenu(noCommand, Kit.InText, 0));
            Assert.Equal(["AgentHUD.OpenSystemMenu"], noCommand.SkippedMembers);

            var noMenu = Kit.Synthetic("DalamudAtkTweaks");
            Assert.Equal((nint.Zero, nint.Zero), DalamudAtkTweaks.DecideSystemMenu(noMenu, 0, Kit.InText));
            Assert.Equal(["UIModule.ExecuteMainCommand"], noMenu.SkippedMembers);
        }
    }
}
