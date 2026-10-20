#nullable enable

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

using Dalamud.Game.Gui;
using Dalamud.Game.Text;

using FFXIVClientStructs.FFXIV.Client.UI.Misc;

using Xunit;

using Kit = Dalamud.Test.Game.HookTargetsTestKit;

namespace Dalamud.Test.Game.OptionalHooks
{
    public class ChatGuiHookTargetsTests
    {
        [Fact]
        public void EveryHookIsLeftOutWhileClientStructsIsUnresolved()
        {
            Assert.Equal(nint.Zero, RaptureLogModule.Addresses.PrintMessage.Value);
            var targets = Kit.NoSections("ChatGui");

            var (printMessage, inventoryItemCopy, handleLinkClick) = ChatGui.FindHookTargets(targets);

            Assert.Equal(nint.Zero, printMessage);
            Assert.Equal(nint.Zero, inventoryItemCopy);
            Assert.Equal(nint.Zero, handleLinkClick);
            Assert.Equal(["PrintMessage", "InventoryItem.Copy", "HandleLinkClick"], targets.SkippedMembers);
            Assert.Equal(3, targets.DisabledFeatures.Count);
        }

        [Fact]
        public void TheConstructorGoesThroughHookTargets()
            => Kit.AssertConstructorUsesHookTargets(typeof(ChatGui), nameof(ChatGui.FindHookTargets));

        [Fact]
        public void WithoutThePrintHookQueuedLinesAreDroppedInsteadOfKept()
        {
            var chat = (ChatGui)RuntimeHelpers.GetUninitializedObject(typeof(ChatGui));
            var queue = new ConcurrentQueue<XivChatEntry>();
            DetourHarness.SetField(chat, "chatQueue", queue);

            Assert.False(chat.IsPrintAvailable);

            chat.Print(new XivChatEntry { MessageBytes = "first"u8.ToArray() });
            chat.UpdateQueue();
            chat.Print(new XivChatEntry { MessageBytes = "second"u8.ToArray() });
            chat.UpdateQueue();

            Assert.True(queue.IsEmpty);
        }
    }
}
