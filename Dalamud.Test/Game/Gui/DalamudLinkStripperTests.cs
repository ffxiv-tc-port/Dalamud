#nullable enable

using System;
using System.Linq;
using System.Text;

using Dalamud.Game.Gui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

using Xunit;

using LSeStringBuilder = Lumina.Text.SeStringBuilder;

namespace Dalamud.Test.Game.Gui
{
    public sealed class DalamudLinkStripperTests
    {
        private static readonly byte[] Terminator = RawPayload.LinkTerminator.Data;

        // EventTutorial 5 (同行) as the game puts it in the chat log; it shares Dalamud's link type.
        private static readonly byte[] Companion = Convert.FromHexString("02270D0F060101FF07E5908CE8A18C03");

        [Fact]
        public void GameTutorialLinkAtTopLevelIsKept()
        {
            var tutorial = Cat(Companion, Text("同行"), Terminator);
            Assert.True(DalamudLinkStripper.ContainsDalamudLink(tutorial));
            Assert.Null(DalamudLinkStripper.Strip(tutorial));

            var input = Cat(Text("可以"), tutorial, Text("、"), DLink(1), Text("插件"), Terminator);
            var result = DalamudLinkStripper.Strip(input);

            Assert.NotNull(result);
            Assert.Equal(Convert.ToHexString(Cat(Text("可以"), tutorial, Text("、插件"))), Convert.ToHexString(result[..^1]));
        }

        [Fact]
        public void OnlyTheGamesTutorialShapeAtTopLevelIsKept()
        {
            // Closest a Dalamud link gets: no plugin, ID 0. Its JSON tail does not fit the tutorial shape.
            var empty = new DalamudLinkPayload { Plugin = string.Empty, CommandId = 0 }.Encode();
            AssertStripped(Cat(empty, Text("插件"), Terminator), Text("插件"));

            var bracket = new LSeStringBuilder().PushLink(LinkMacroPayloadType.EventTutorial, 5, 0, 0, "[同行]").ToArray();
            AssertStripped(Cat(bracket, Text("同行"), Terminator), Text("同行"));

            AssertStripped(Cat(Text("前"), If(Cat(Companion, Text("同行"), Terminator)), Text("後")), Text("前後"));
        }

        [Fact]
        public void TerminatorOfALinkRemovedInsideAGameLinkClosesTheGameLink()
        {
            // The game would have closed the item link where the Dalamud link starts; with that gone, its terminator
            // is what still closes the item link.
            AssertStripped(
                Cat(Item(5333), Text("物品"), DLink(1), Text("插件"), Terminator, Text("其餘")),
                Cat(Item(5333), Text("物品插件"), Terminator, Text("其餘")));
            AssertStripped(
                Cat(Item(5333), Text("物品"), DLink(1), Text("一"), DLink(2), Text("二"), Terminator, Text("其餘")),
                Cat(Item(5333), Text("物品一二"), Terminator, Text("其餘")));

            // Negative control: an item link already closed leaves the removed link's terminator to go with it.
            AssertStripped(
                Cat(Item(5333), Text("物品"), Terminator, DLink(1), Text("插件"), Terminator, Text("其餘")),
                Cat(Item(5333), Text("物品"), Terminator, Text("插件其餘")));
        }

        [Fact]
        public void NothingToRemoveReturnsNull()
        {
            Assert.Null(DalamudLinkStripper.Strip(ReadOnlySpan<byte>.Empty));
            Assert.Null(DalamudLinkStripper.Strip(Text("光之戰士發動了「暴風斬」。")));
            Assert.Null(DalamudLinkStripper.Strip(Cat(Color(500), Text("紅字"), Color(0), new IconPayload(BitmapFontIcon.Dice).Encode())));
            Assert.Null(DalamudLinkStripper.Strip(Cat(Item(5333), Text("物品"), Terminator, Map(), Text("地圖"), Terminator)));
            Assert.Null(DalamudLinkStripper.Strip(Cat(Text("孤立的結尾"), Terminator)));
        }

        [Fact]
        public void SingleLinkKeepsItsText()
        {
            var input = Cat(Text("插件："), Color(500), DLink(1), Text("點這裡"), Terminator, Color(0), Text("。"));

            AssertStripped(input, Cat(Text("插件："), Color(500), Text("點這裡"), Color(0), Text("。")));
        }

        [Fact]
        public void EveryLinkIsRemoved()
        {
            var input = Cat(DLink(1), Text("一"), Terminator, Text("、"), DLink(2), Text("二"), Terminator, Text("、"), DLink(3), Text("三"), Terminator);

            AssertStripped(input, Text("一、二、三"));
        }

        [Fact]
        public void GameLinksAreKeptByteForByte()
        {
            // 0x227 encodes as F2 02 27: a start byte and the Link macro code inside the item link.
            var item = Cat(Item(0x227), Text("物品"), Terminator);
            var map = Cat(Map(), Text("地圖"), Terminator);
            var input = Cat(item, Text(" "), DLink(1), Text("插件"), Terminator, Text(" "), map);

            AssertStripped(input, Cat(item, Text(" 插件 "), map));
            Assert.Null(DalamudLinkStripper.Strip(Cat(item, map)));
        }

        [Fact]
        public void LinkAtStartOrEnd()
        {
            AssertStripped(Cat(DLink(1), Text("開頭"), Terminator, Text("其餘")), Text("開頭其餘"));
            AssertStripped(Cat(Text("其餘"), DLink(1), Text("結尾"), Terminator), Text("其餘結尾"));
            AssertStripped(Cat(Text("其餘"), DLink(1)), Text("其餘"));
            AssertStripped(DLink(1), []);
        }

        [Fact]
        public void TerminatorIsTakenOnlyWhenNoOtherLinkComesFirst()
        {
            // The terminator after the item link closes the item link, so it stays.
            AssertStripped(
                Cat(DLink(1), Text("沒結尾"), Item(5333), Text("物品"), Terminator),
                Cat(Text("沒結尾"), Item(5333), Text("物品"), Terminator));

            AssertStripped(Cat(DLink(1), Text("一"), DLink(2), Text("二"), Terminator), Text("一二"));
            AssertStripped(Cat(Terminator, Text("前"), DLink(1), Text("後"), Terminator), Cat(Terminator, Text("前後")));
            AssertStripped(Cat(DLink(1), Text("a"), Terminator, Terminator), Cat(Text("a"), Terminator));
        }

        [Fact]
        public void OtherEncodingsOfTheLinkTypeAreRemoved()
        {
            var legacy = new LSeStringBuilder()
                         .BeginMacro(MacroCode.Link)
                         .AppendIntExpression((int)DalamudLinkStripper.DalamudLinkType)
                         .AppendStringExpression("TestPlugin")
                         .AppendUIntExpression(7)
                         .EndMacro()
                         .ToArray();
            AssertStripped(Cat(legacy, Text("舊格式"), Terminator), Text("舊格式"));

            // F0 0E is a two-byte encoding of 14.
            AssertStripped(Cat(Macro(MacroCode.Link, 0xF0, 0x0E, 0x01, 0x01, 0x01), Text("長編碼"), Terminator), Text("長編碼"));
            AssertStripped(Cat(Link((LinkMacroPayloadType)0x10E), Text("超出位元組"), Terminator), Text("超出位元組"));
            AssertStripped(Cat(Macro(MacroCode.Link, 0xD8, 0x01, 0x01, 0x01), Text("不是數字"), Terminator), Text("不是數字"));
            AssertStripped(Cat(Macro(MacroCode.Link), Text("沒有運算式"), Terminator), Text("沒有運算式"));
        }

        [Fact]
        public void MacroHoldingALinkIsRemovedWhole()
        {
            var hidden = If(Cat(DLink(1), Text("藏起來"), Terminator));
            AssertStripped(Cat(Text("前"), hidden, Text("後")), Text("前後"));

            var deeper = If(Cat(Text("外"), If(Cat(DLink(1), Text("內"), Terminator))));
            AssertStripped(Cat(deeper, Text("後")), Text("後"));

            var gameLinkInside = If(Cat(Item(5333), Text("物品"), Terminator));
            Assert.Null(DalamudLinkStripper.Strip(Cat(Text("前"), gameLinkInside)));
        }

        [Fact]
        public void LinkOpenedByAMalformedStartByteIsRemovedOnALaterPass()
        {
            // 02 27 06 0F ... claims a body of five bytes; only once the link after it is gone does its end byte line up.
            var input = Cat([0x02, 0x27, 0x06, 0x0F], DLink(1), [0x01, 0x01, 0x01, 0x01, 0x03]);
            Assert.False(LuminaSeesLinkAt(input, 0));

            AssertStripped(input, []);

            // Three numbers and nothing after them is the game's tutorial shape, kept as a click on it would go to the
            // game with the HandleLinkClick hook installed.
            var tutorialShape = Cat([0x02, 0x27, 0x05, 0x0F], DLink(1), [0x01, 0x01, 0x01, 0x03]);
            Assert.Equal("0227050F01010103", Convert.ToHexString(DalamudLinkStripper.Strip(tutorialShape)![..^1]));
        }

        [Fact]
        public void LinkStartingInAMacroHeaderIsRemoved()
        {
            // 02 02 27 …: a macro with code 02 and a 38-byte body, whose code and length bytes open the link.
            var input = Cat([0x02], DLink(1), Text("0123456789abc"), [0x03]);
            Assert.True(LuminaSeesLinkAt(input, 1));

            AssertStripped(Cat(Text("前"), input, Text("後")), Text("前後"));
        }

        [Fact]
        public void MalformedInputNeverThrows()
        {
            byte[][] inputs =
            [
                [0x02],
                [0x02, 0x27],
                [0x02, 0x27, 0x02, 0x0F],
                [0x02, 0x27, 0x10, 0x0F, 0x03],
                [0x02, 0x27, 0xFE, 0xFF, 0xFF, 0xFF, 0xF6, 0x0F, 0x03], // body length -10
                [0x02, 0x27, 0xFE, 0x7F, 0xFF, 0xFF, 0xFF, 0x0F, 0x03], // body length int.MaxValue
                [0x02, 0x02, 0x02, 0x02, 0x03, 0x03],
                [0x00, 0x02, 0x27, 0x00, 0x03],

                // String expressions of length int.MaxValue, where Lumina's TryDecodeString overflows.
                [0x02, 0x27, 0x0B, 0x0F, 0x01, 0x01, 0x01, 0xFF, 0xFE, 0x7F, 0xFF, 0xFF, 0xFF, 0x03],
                [0x02, 0x27, 0x0B, 0x04, 0x01, 0x01, 0x01, 0xFF, 0xFE, 0x7F, 0xFF, 0xFF, 0xFF, 0x03],
                [0x02, 0x08, 0x08, 0x02, 0xFF, 0xFE, 0x7F, 0xFF, 0xFF, 0xFF, 0x03],
            ];

            foreach (var input in inputs)
            {
                var result = DalamudLinkStripper.Strip(input);
                Assert.False(DalamudLinkStripper.ContainsDalamudLink(result == null ? input : result.AsSpan(0, result.Length - 1)));
            }
        }

        [Fact]
        public void RandomInputLeavesNoLinkAnywhere()
        {
            var random = new Random(20261021);
            byte[][] pieces =
            [
                DLink(1), Terminator, Item(5333), Map(), Color(500), Text("字"), If(DLink(2)),
                [0x02], [0x02, 0x27], [0x03], [0x0F], [0xFF], [0x00],
            ];

            for (var round = 0; round < 3000; round++)
            {
                var input = Cat(Enumerable.Range(0, random.Next(1, 12)).Select(_ => pieces[random.Next(pieces.Length)]).ToArray());
                var result = DalamudLinkStripper.Strip(input);
                var output = result == null ? input : result[..^1];

                Assert.False(LuminaSeesLinkAnywhere(output), Convert.ToHexString(input));
                Assert.False(DalamudLinkStripper.ContainsDalamudLink(output), Convert.ToHexString(input));
                Assert.True(result == null || (result[^1] == 0 && result.Length <= input.Length), Convert.ToHexString(input));
            }
        }

        private static void AssertStripped(byte[] input, byte[] expected)
        {
            Assert.True(LuminaSeesLinkAnywhere(input) || DalamudLinkStripper.ContainsDalamudLink(input));

            var result = DalamudLinkStripper.Strip(input);

            Assert.NotNull(result);
            Assert.Equal(0, result[^1]);
            Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(result.AsSpan(0, result.Length - 1)));
            Assert.False(LuminaSeesLinkAnywhere(result[..^1]));
            Assert.Empty(SeString.Parse(result[..^1]).Payloads.OfType<DalamudLinkPayload>());
        }

        // Independent of the stripper: Lumina's own parser, tried at every start byte.
        private static bool LuminaSeesLinkAnywhere(byte[] data)
            => Enumerable.Range(0, data.Length).Any(i => LuminaSeesLinkAt(data, i));

        private static bool LuminaSeesLinkAt(byte[] data, int offset)
        {
            if (data[offset] != 0x02)
                return false;

            try
            {
                foreach (var payload in new ReadOnlySeStringSpan(data.AsSpan(offset)))
                {
                    return payload.Type == ReadOnlySePayloadType.Macro &&
                           payload.MacroCode == MacroCode.Link &&
                           (!payload.TryGetExpression(out var type) || !type.TryGetUInt(out var value) || value == 14 || value > 0xFF);
                }
            }
            catch (Exception)
            {
                // Lumina throws on a negative or overflowing body length; nothing opens there.
            }

            return false;
        }

        private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

        private static byte[] Color(ushort color) => new UIForegroundPayload(color).Encode();

        private static byte[] DLink(uint commandId) => new DalamudLinkPayload { Plugin = "TestPlugin", CommandId = commandId }.Encode();

        private static byte[] Item(uint itemId) => new ItemPayload(itemId, false).Encode();

        private static byte[] Map() => new MapLinkPayload(132, 2, 11000, -2000).Encode();

        private static byte[] Link(LinkMacroPayloadType type) => new LSeStringBuilder().PushLink(type, 1, 0, 0).ToArray();

        private static byte[] If(byte[] whenTrue)
            => new LSeStringBuilder()
               .BeginMacro(MacroCode.If)
               .AppendIntExpression(1)
               .AppendStringExpression(whenTrue)
               .AppendStringExpression(""u8)
               .EndMacro()
               .ToArray();

        private static byte[] Macro(MacroCode code, params byte[] body)
            => Cat([0x02, (byte)code, (byte)(body.Length + 1)], body, [0x03]);
    }
}
