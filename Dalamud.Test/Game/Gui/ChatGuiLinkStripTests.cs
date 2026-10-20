#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Dalamud.Game.Gui;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC.Internal;
using Dalamud.Utility.Internal;

using Xunit;

using Utf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String;

namespace Dalamud.Test.Game.Gui
{
    // The detour side of DalamudLinkStripper: which session strips, and what happens when the write-back fails.
    public sealed class ChatGuiLinkStripTests : IDisposable
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly List<nint> blocks = [];

        public ChatGuiLinkStripTests()
        {
            // Reporting a fault looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        [Fact]
        public void HookedSessionPassesLinksThrough()
        {
            var (chat, original) = NewChat(linkClickHooked: true);
            var message = this.Utf8(WithLink());

            Assert.Equal(42u, Invoke(chat, this.Utf8("光之戰士"u8.ToArray()), message));

            var call = Assert.Single(original.Calls);
            Assert.Equal(message, call[3]);
            Assert.Equal(WithLink(), Read(message));
            Assert.False(LinkStripLogged(chat));
        }

        [Fact]
        public void DegradedSessionPassesLinklessLinesThrough()
        {
            var (chat, original) = NewChat(linkClickHooked: false);
            var plain = new SeString(new ItemPayload(5333, false), new TextPayload("物品"), RawPayload.LinkTerminator).Encode();
            var message = this.Utf8(plain);

            Assert.Equal(42u, Invoke(chat, this.Utf8("光之戰士"u8.ToArray()), message));

            var call = Assert.Single(original.Calls);
            Assert.Equal(message, call[3]);
            Assert.Equal(plain, Read(message));
            Assert.False(LinkStripLogged(chat));
        }

        [Theory]
        [InlineData(false, true)]
        [InlineData(true, false)]
        public void DegradedSessionDoesNotPrintALineWhoseLinksCouldNotBeRemoved(bool inSender, bool inMessage)
        {
            // Outside the game Utf8String.SetString throws; it stands in for any failure of the write-back.
            var (chat, original) = NewChat(linkClickHooked: false);
            var seen = new List<SeString>();
            chat.ChatMessage += (XivChatType _, int _, ref SeString _, ref SeString msg, ref bool _) => seen.Add(msg);
            var faults = LinkStripFaults();

            var ret = Invoke(chat, this.Utf8(inSender ? WithLink() : "光之戰士"u8.ToArray()), this.Utf8(inMessage ? WithLink() : "沒有連結"u8.ToArray()));

            Assert.Equal(0u, ret);
            Assert.Empty(original.Calls);
            Assert.Equal(faults + 1, LinkStripFaults());
            Assert.True(LinkStripLogged(chat));
            Assert.Equal(inMessage, Assert.Single(seen).Payloads.OfType<DalamudLinkPayload>().Any());
        }

        public void Dispose()
        {
            foreach (var block in this.blocks)
                Marshal.FreeHGlobal(block);
        }

        private static byte[] WithLink() => new SeString(
            new TextPayload("插件："),
            new DalamudLinkPayload { Plugin = "TestPlugin", CommandId = 1 },
            new TextPayload("點這裡"),
            RawPayload.LinkTerminator).Encode();

        private static (ChatGui Chat, OriginalRecorder Original) NewChat(bool linkClickHooked)
        {
            var chat = (ChatGui)RuntimeHelpers.GetUninitializedObject(typeof(ChatGui));
            var original = DetourHarness.InstallFakeHook(chat, "printMessageHook", 42u);
            if (linkClickHooked)
                DetourHarness.InstallFakeHook(chat, "handleLinkClickHook");
            return (chat, original);
        }

        private static object? Invoke(ChatGui chat, nint sender, nint message)
            => DetourHarness.Invoke(chat, "HandlePrintMessageDetour", nint.Zero, XivChatType.Echo, sender, message, 0, (byte)0);

        private static bool LinkStripLogged(ChatGui chat)
            => (bool)typeof(ChatGui).GetField("linkStripLogged", AnyInstance)!.GetValue(chat)!;

        private static long LinkStripFaults()
            => ((DetourFault)DetourHarness.GetStaticField(typeof(ChatGui), "LinkStripFault")!).Failures;

        private static int Offset(string field)
            => typeof(Utf8String).GetField(field, AnyInstance)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;

        private static byte[] Read(nint str)
        {
            var data = new byte[Marshal.ReadInt64(str, Offset(nameof(Utf8String.StringLength)))];
            Marshal.Copy(Marshal.ReadIntPtr(str, Offset(nameof(Utf8String.StringPtr))), data, 0, data.Length);
            return data;
        }

        private nint Utf8(byte[] data)
        {
            var text = DetourHarness.Bytes([.. data, 0]);
            this.blocks.Add(text);
            var str = DetourHarness.Zeroed(Unsafe.SizeOf<Utf8String>());
            this.blocks.Add(str);
            Marshal.WriteIntPtr(str, Offset(nameof(Utf8String.StringPtr)), text);
            Marshal.WriteInt64(str, Offset(nameof(Utf8String.BufSize)), data.Length + 1);
            Marshal.WriteInt64(str, Offset(nameof(Utf8String.BufUsed)), data.Length + 1);
            Marshal.WriteInt64(str, Offset(nameof(Utf8String.StringLength)), data.Length);
            return str;
        }
    }
}
