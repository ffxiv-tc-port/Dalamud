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

using Xunit;

using Utf8String = FFXIVClientStructs.FFXIV.Client.System.String.Utf8String;

namespace Dalamud.Test.Game.Gui
{
    // ChatGui skips the second encoding of sender/message when the bytes did not change. The oracle for every case is
    // what the detour did before: encode again and compare with the first encoding.
    public sealed class ChatGuiReencodeTests : IDisposable
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly List<nint> blocks = [];

        public ChatGuiReencodeTests()
        {
            // Reporting a fault looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        public static IEnumerable<object[]> Cases()
        {
            yield return Case("untouched", false, s => s);
            yield return Case("read only: TextValue, ToString, Encode, Payload.Encode", false, s =>
            {
                _ = s.TextValue;
                _ = s.ToString();
                _ = s.Encode();
                foreach (var p in s.Payloads)
                    _ = p.Encode();
                return s;
            });
            yield return Case("reference swapped, different text", true, _ => new SeString(new TextPayload("另一句")));
            yield return Case("reference swapped, same bytes re-parsed", false, s => SeString.Parse(s.Encode()));
            yield return Case("payload appended", true, s => s.Append(new TextPayload("！")));
            yield return Case("last payload removed", true, s =>
            {
                s.Payloads.RemoveAt(s.Payloads.Count - 1);
                return s;
            });
            yield return Case("payload inserted at the front", true, s =>
            {
                s.Payloads.Insert(0, new UIForegroundPayload(1));
                return s;
            });
            yield return Case("payload replaced by a different one", true, s =>
            {
                s.Payloads[2] = new TextPayload("暗黑騎士");
                return s;
            });
            yield return Case("payload replaced by an equal new object", false, s =>
            {
                s.Payloads[2] = new TextPayload(((TextPayload)s.Payloads[2]).Text);
                return s;
            });
            yield return Case("payloads cleared", true, s =>
            {
                s.Payloads.Clear();
                return s;
            });
            yield return Case("two payloads swapped", true, s =>
            {
                (s.Payloads[0], s.Payloads[2]) = (s.Payloads[2], s.Payloads[0]);
                return s;
            });
            yield return Case("text setter leaves the payload dirty", true, s =>
            {
                ((TextPayload)s.Payloads[2]).Text = "暗黑騎士";
                return s;
            });
            yield return Case("text setter, then encoded again (no longer dirty)", true, s =>
            {
                ((TextPayload)s.Payloads[2]).Text = "暗黑騎士";
                _ = s.Encode();
                return s;
            });
            yield return Case("text setter with the same value (dirty, same bytes)", false, s =>
            {
                var text = (TextPayload)s.Payloads[2];
                text.Text = text.Text;
                return s;
            });
            yield return Case("color setter", true, s =>
            {
                ((UIForegroundPayload)s.Payloads[0]).ColorKey = 501;
                return s;
            });
            yield return Case("setter that does not mark dirty (missed before too)", false, s =>
            {
                ((IconPayload)s.Payloads[^1]).Icon = BitmapFontIcon.Warning;
                return s;
            });
            yield return Case("cached bytes changed in place", true, s =>
            {
                s.Payloads[2].Encode()[0] ^= 0x20;
                return s;
            });
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void SkipDecisionMatchesEncodingAgain(string name, bool changed, Func<SeString, SeString> handler)
        {
            var parsed = SeString.Parse(Message());
            var terminated = parsed.EncodeWithNullTerminator();

            var after = handler(parsed);
            var skip = ChatGui.EncodesTo(after, terminated);
            var oracle = after.EncodeWithNullTerminator().AsSpan().SequenceEqual(terminated);

            Assert.True(skip == oracle, name);
            Assert.Equal(!changed, skip);
        }

        [Fact]
        public void EmptySenderStillCompares()
        {
            var empty = SeString.Parse(ReadOnlySpan<byte>.Empty);
            var terminated = empty.EncodeWithNullTerminator();

            Assert.True(ChatGui.EncodesTo(empty, terminated));
            Assert.False(ChatGui.EncodesTo(empty.Append(new TextPayload("a")), terminated));
            Assert.False(ChatGui.EncodesTo(SeString.Empty, []));
        }

        [Fact]
        public void PrefixOrLongerEncodingIsNotEqual()
        {
            var full = new SeString(new TextPayload("ab"), new TextPayload("cd"));
            var terminated = full.EncodeWithNullTerminator();

            Assert.False(ChatGui.EncodesTo(new SeString(new TextPayload("ab")), terminated));
            Assert.False(ChatGui.EncodesTo(new SeString(new TextPayload("ab"), new TextPayload("cd"), new TextPayload("e")), terminated));
            Assert.False(ChatGui.EncodesTo(full, terminated.Take(terminated.Length - 1).Append((byte)1).ToArray()));
            Assert.True(ChatGui.EncodesTo(new SeString(new TextPayload("abcd")), terminated));
        }

        [Fact]
        public void ThrowsWhereEncodingThrows()
        {
            var parsed = SeString.Parse(Message());
            var terminated = parsed.EncodeWithNullTerminator();
            parsed.Payloads.Insert(1, null!);

            Assert.Throws<NullReferenceException>(() => ChatGui.EncodesTo(parsed, terminated));
            Assert.Throws<NullReferenceException>(() => parsed.EncodeWithNullTerminator());
            Assert.Throws<NullReferenceException>(() => ChatGui.EncodesTo(null!, terminated));
        }

        [Fact]
        public void DetourWritesBackNothingWhenNothingChanged()
        {
            var (chat, original, unhandled) = this.NewChat();
            chat.ChatMessage += (XivChatType _, int _, ref SeString sender, ref SeString message, ref bool _) =>
            {
                _ = sender.TextValue;
                _ = message.Encode();
                message = SeString.Parse(message.Encode());
            };
            var sender = this.Utf8(Encoding.UTF8.GetBytes("光之戰士"));
            var message = this.Utf8(Message());

            Assert.Equal(42u, DetourHarness.Invoke(chat, "HandlePrintMessageDetour", nint.Zero, XivChatType.Say, sender, message, 0, (byte)0));

            var call = Assert.Single(original.Calls);
            Assert.Equal(sender, call[2]);
            Assert.Equal(message, call[3]);

            // Reached only without a write-back: outside the game Utf8String.SetString throws into the detour's catch.
            Assert.Equal(new[] { "光之戰士" }, unhandled.Select(u => u.Sender));
        }

        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        [InlineData(true, true)]
        public void DetourStillWritesBackWhenOneSideChanged(bool changeSender, bool changeMessage)
        {
            var (chat, original, unhandled) = this.NewChat();
            chat.ChatMessage += (XivChatType _, int _, ref SeString sender, ref SeString message, ref bool _) =>
            {
                if (changeSender)
                    ((TextPayload)sender.Payloads[0]).Text = "暗黑騎士";
                if (changeMessage)
                {
                    ((TextPayload)message.Payloads[2]).Text = "暗黑騎士";
                    _ = message.Encode();
                }
            };

            DetourHarness.Invoke(chat, "HandlePrintMessageDetour", nint.Zero, XivChatType.Say, this.Utf8(Encoding.UTF8.GetBytes("光之戰士")), this.Utf8(Message()), 0, (byte)0);

            Assert.Single(original.Calls);
            Assert.Empty(unhandled);
        }

        public void Dispose()
        {
            foreach (var block in this.blocks)
                Marshal.FreeHGlobal(block);
        }

        private static object[] Case(string name, bool changed, Func<SeString, SeString> handler) => [name, changed, handler];

        private static byte[] Message() => new SeString(
            new UIForegroundPayload(500),
            new UIGlowPayload(501),
            new TextPayload("光之戰士"),
            UIGlowPayload.UIGlowOff,
            UIForegroundPayload.UIForegroundOff,
            new TextPayload("發動了「暴風斬」，對木人造成了1234點傷害。"),
            new IconPayload(BitmapFontIcon.Dice)).Encode();

        private static int Offset(string field)
            => typeof(Utf8String).GetField(field, AnyInstance)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;

        private (ChatGui Chat, OriginalRecorder Original, List<(string Sender, string Message)> Unhandled) NewChat()
        {
            var chat = (ChatGui)RuntimeHelpers.GetUninitializedObject(typeof(ChatGui));
            var original = DetourHarness.InstallFakeHook(chat, "printMessageHook", 42u);
            var unhandled = new List<(string Sender, string Message)>();
            chat.ChatMessageUnhandled += (_, _, s, m) => unhandled.Add((s.TextValue, m.TextValue));
            return (chat, original, unhandled);
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
