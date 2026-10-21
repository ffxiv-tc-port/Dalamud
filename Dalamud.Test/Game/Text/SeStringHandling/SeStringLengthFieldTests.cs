#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using Xunit;

namespace Dalamud.Test.Game.Text.SeStringHandling
{
    // Length fields inside a SeString are untrusted: a value past the end of the data must not size a buffer,
    // and must decode exactly like the smallest overrun did before (reads past the end were already cut short).
    public class SeStringLengthFieldTests
    {
        private const long AllocationCeiling = 1 << 20;

        private static readonly uint[] HugeLengths = [0x00100000, 0x20000000, 0x7F000000, 0x7FFFFFC7, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF];

        public static IEnumerable<object[]> HugeLengthData => HugeLengths.Select(v => new object[] { v });

        private static byte[] Text(string s) => new TextPayload(s).Encode();

        private static byte[] LegitCorpus()
        {
            var chunks = new List<byte[]>
            {
                Text("Hello 世界"),
                new PlayerPayload("塔塔露", 4028).Encode(),
                new StatusPayload(1).Encode(),
                new QuestPayload(65637).Encode(),
                new PartyFinderPayload(123456, PartyFinderPayload.PartyFinderLinkType.LimitedToHomeWorld).Encode(),
                new PartyFinderPayload(123456, PartyFinderPayload.PartyFinderLinkType.NotSpecified).Encode(),
                new AutoTranslatePayload(49, 209).Encode(),
                new UIForegroundPayload(525).Encode(),
                new UIGlowPayload(525).Encode(),
                UIForegroundPayload.UIForegroundOff.Encode(),
                EmphasisItalicPayload.ItalicsOn.Encode(),
                EmphasisItalicPayload.ItalicsOff.Encode(),
                NewLinePayload.Payload.Encode(),
                SeHyphenPayload.Payload.Encode(),
                new IconPayload(BitmapFontIcon.Dice).Encode(),
                new ItemPayload(5000, false, "ABCD").Encode(),
                new ItemPayload(5000, true, "ABCD").Encode(),
                new ItemPayload(5000, false, null).Encode(),
                RawPayload.LinkTerminator.Encode(),
                new byte[] { 0x02, 0x27, 0x0C, 0x04, 0xF4, 0x84, 0x02, 0xF2, 0x2A, 0xF8, 0xF1, 0x2B, 0xFF, 0x01, 0x03 },
                Raw(0x08, Enumerable.Range(0, 300).Select(i => (byte)(0x20 + (i % 90))).ToArray()),
                Text("後"),
            };
            return chunks.SelectMany(x => x).ToArray();
        }

        [Fact]
        public void LegitStringsRoundTripByteForByte()
        {
            var bytes = LegitCorpus();
            var parsed = SeString.Parse(bytes);

            // PlayerPayload encodes as link + name text + link terminator, so 22 chunks parse into 24 payloads.
            Assert.Equal(24, parsed.Payloads.Count);
            Assert.Equal(bytes, parsed.Encode());
            Assert.Equal(bytes, parsed.Payloads.SelectMany(p => p.Encode()).ToArray());

            var item = parsed.Payloads.OfType<ItemPayload>().ToArray();
            Assert.Equal(3, item.Length);
            Assert.Equal(item[0].Encode(), item[0].Encode(true));
            Assert.Equal(item[1].Encode(), item[1].Encode(true));
            Assert.True(item[1].IsHQ);

            var raw = parsed.Payloads.OfType<RawPayload>().Last();
            Assert.Equal(raw.Data, raw.Encode(true));
            Assert.Equal(2 + 3 + 300 + 1, raw.Data.Length);
        }

        [Theory]
        [MemberData(nameof(HugeLengthData))]
        public void ChunkLengthPastEndDecodesLikeSmallOverrunWithoutLargeAllocation(uint huge)
        {
            var cases = new[]
            {
                new PlayerPayload("塔塔露", 4028).Encode(),
                new UIForegroundPayload(525).Encode(),
                new AutoTranslatePayload(49, 209).Encode(),
                Raw(0x08, Encoding.UTF8.GetBytes("raw chunk body")),
            };

            foreach (var chunk in cases)
            {
                Assert.True(chunk[2] < 0xD0);
                foreach (var tail in new[] { Array.Empty<byte>(), Text("後") })
                {
                    var body = chunk[3..];
                    var remaining = (uint)(body.Length + tail.Length);
                    var smallLen = MakeInteger(remaining + 1);
                    var small = SeString.Parse(Concat(Text("前"), [0x02, chunk[1]], smallLen, body, tail));
                    var input = Concat(Text("前"), [0x02, chunk[1]], MakeInteger(huge), body, tail);

                    var (parsed, allocated) = ParseMeasured(input);

                    Assert.True(allocated < AllocationCeiling, $"allocated {allocated} bytes for length {huge:X8}");
                    Assert.Equal(input, parsed.Encode());
                    Assert.Equal(small.Payloads.Count, parsed.Payloads.Count);
                    Assert.Equal(small.Payloads.Last().GetType(), parsed.Payloads.Last().GetType());
                    Assert.Equal(small.Payloads.Last().Encode()[(2 + smallLen.Length)..], parsed.Payloads.Last().Encode()[(2 + MakeInteger(huge).Length)..]);
                    Assert.Equal(small.Payloads.Last().Encode(true), parsed.Payloads.Last().Encode(true));
                }
            }
        }

        [Theory]
        [MemberData(nameof(HugeLengthData))]
        public void ItemNameLengthPastEndDoesNotAllocateOrThrow(uint huge)
        {
            var chunk = new ItemPayload(5000, false, "ABCD").Encode();
            var ff = Array.IndexOf(chunk, (byte)0xFF, 4);
            var head = chunk[3..(ff + 1)];
            var name = chunk[(ff + 2)..^1];

            byte[] Make(uint nameLen)
            {
                var body = Concat(head, MakeInteger(nameLen), name);
                return Concat(Text("前"), [0x02, 0x27], MakeInteger((uint)body.Length), body, [0x03], Text("後"));
            }

            var small = SeString.Parse(Make((uint)(name.Length + 1 + 3 + 1)));
            var (parsed, allocated) = ParseMeasured(Make(huge));

            Assert.True(allocated < AllocationCeiling, $"allocated {allocated} bytes for name length {huge:X8}");
            Assert.Equal(Make(huge), parsed.Encode());
            Assert.Equal(3, parsed.Payloads.Count);
            Assert.IsType<ItemPayload>(parsed.Payloads[1]);
            Assert.Equal(small.Payloads[1].Encode(true), parsed.Payloads[1].Encode(true));
        }

        [Theory]
        [MemberData(nameof(HugeLengthData))]
        public void DecodeOnMemoryStreamStopsAtEnd(uint huge)
        {
            var input = Concat([0x02, 0x08], MakeInteger(huge), Encoding.UTF8.GetBytes("abc"));
            using var stream = new MemoryStream(input, false);
            using var reader = new BinaryReader(stream);

            var before = GC.GetAllocatedBytesForCurrentThread();
            var payload = Payload.Decode(reader);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated < AllocationCeiling, $"allocated {allocated} bytes for length {huge:X8}");
            Assert.Equal(input.Length, stream.Position);
            Assert.Equal(input, payload.Encode());
        }

        private static (SeString Parsed, long Allocated) ParseMeasured(byte[] input)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var parsed = SeString.Parse(input);
            return (parsed, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        private static byte[] Raw(byte type, byte[] data) => Concat([0x02, type], MakeInteger((uint)data.Length), data, [0x03]);

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(x => x).ToArray();

        // Same encoding as Payload.MakeInteger, which is protected.
        private static byte[] MakeInteger(uint value)
        {
            if (value < 0xCF)
                return [(byte)(value + 1)];

            var bytes = BitConverter.GetBytes(value);
            var ret = new List<byte> { 0xF0 };
            for (var i = 3; i >= 0; i--)
            {
                if (bytes[i] != 0)
                {
                    ret.Add(bytes[i]);
                    ret[0] |= (byte)(1 << i);
                }
            }

            ret[0] -= 1;
            return ret.ToArray();
        }
    }
}
