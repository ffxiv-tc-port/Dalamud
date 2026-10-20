#nullable enable

using Dalamud.Game.Command;
using Dalamud.Utility.Internal;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class CommandManagerHookTargetsTests
    {
        [Fact]
        public void TheCommandHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            var targets = Kit.NoSections("CommandManager");

            Assert.Equal(nint.Zero, CommandManager.FindHookTarget(targets));
            Assert.Equal(["ShellCommands.TryInvokeDebugCommand"], targets.SkippedMembers);

            // /xllog is a typed command too, so the notice tells the other ways in instead.
            var line = targets.FormatChatLine(CommandManager.CommandsOffHint)!;
            Assert.DoesNotContain(HookTargets.DefaultChatTail, line);
            Assert.EndsWith(CommandManager.CommandsOffHint, line);
            Assert.Contains("ESC", line);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(CommandManager), nameof(CommandManager.FindHookTarget));
    }
}
