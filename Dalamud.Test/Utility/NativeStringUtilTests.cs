using System;
using System.Text;

using Dalamud.Utility;

using Xunit;

namespace Dalamud.Test.Utility
{
    public class NativeStringUtilTests
    {
        private const char Replacement = (char)0xFFFD;

        [Theory]
        [InlineData("Talk", "Talk", true)]
        [InlineData("Talk", "talk", false)]
        [InlineData("Talk", "Tal", false)]
        [InlineData("Tal", "Talk", false)]
        [InlineData("", "", true)]
        [InlineData("", "Talk", false)]
        [InlineData("Talk", "", false)]
        [InlineData("_ParameterWidget", "_ParameterWidget", true)]
        [InlineData("測試", "測試", true)]
        [InlineData("測試", "測", false)]
        [InlineData("Talk", "Tälk", false)]
        public void MatchesDecodeThenCompare(string stored, string value, bool expected)
        {
            var buffer = new byte[32];
            Encoding.UTF8.GetBytes(stored, buffer);

            Assert.Equal(expected, NativeStringUtil.FixedBufferEquals(buffer, value));
            Assert.Equal(NativeStringUtil.ReadFixedBuffer(buffer) == value, NativeStringUtil.FixedBufferEquals(buffer, value));
        }

        [Fact]
        public void StopsAtTheFirstNulAndReadsAFullBufferWithoutOne()
        {
            var buffer = new byte[32];
            Encoding.ASCII.GetBytes("Talk\0Junk", buffer);
            Assert.True(NativeStringUtil.FixedBufferEquals(buffer, "Talk"));
            Assert.False(NativeStringUtil.FixedBufferEquals(buffer, "Talk\0Junk"));

            var full = new byte[32];
            full.AsSpan().Fill((byte)'A');
            Assert.True(NativeStringUtil.FixedBufferEquals(full, new string('A', 32)));
            Assert.False(NativeStringUtil.FixedBufferEquals(full, new string('A', 31)));
        }

        [Fact]
        public void InvalidBytesCompareAsTheirReplacementCharacter()
        {
            var buffer = new byte[32];
            buffer[0] = 0xFF;
            buffer[1] = (byte)'A';

            Assert.True(NativeStringUtil.FixedBufferEquals(buffer, $"{Replacement}A"));
            Assert.False(NativeStringUtil.FixedBufferEquals(buffer, "A"));
        }

        [Fact]
        public void NullNeverMatches()
        {
            Assert.False(NativeStringUtil.FixedBufferEquals(new byte[32], null));
            Assert.False(NativeStringUtil.FixedBufferEquals(new byte[] { (byte)'A', 0 }, null));
        }

        // Random buffers against decoded strings, their neighbours and their prefixes: the fast path must never disagree.
        [Fact]
        public void AgreesWithDecodeThenCompareOnRandomInput()
        {
            var random = new Random(20260929);
            var alphabet = new byte[] { (byte)'A', (byte)'z', (byte)'_', (byte)'0', 0x7F, 0x80, 0xC3, 0xA4, 0xE6, 0xB8, 0xAC, 0xFF };
            var buffer = new byte[32];
            var mismatches = 0;
            for (var i = 0; i < 20_000; i++)
            {
                var length = random.Next(0, 33);
                for (var j = 0; j < buffer.Length; j++)
                    buffer[j] = j < length ? alphabet[random.Next(alphabet.Length)] : (byte)0;
                if (length < buffer.Length && random.Next(4) == 0)
                    buffer[length] = alphabet[random.Next(alphabet.Length)];

                var decoded = NativeStringUtil.ReadFixedBuffer(buffer);
                var candidates = new[]
                {
                    decoded,
                    decoded.Length > 0 ? decoded[..^1] : "x",
                    decoded + "A",
                    decoded.Length > 0 ? (char)(decoded[0] ^ 1) + decoded[1..] : string.Empty,
                    decoded.Replace(Replacement, '?'),
                };
                foreach (var candidate in candidates)
                {
                    if (NativeStringUtil.FixedBufferEquals(buffer, candidate) != (decoded == candidate))
                        mismatches++;
                }
            }

            Assert.Equal(0, mismatches);
        }

        [Fact]
        public void AsciiComparisonAllocatesNothing()
        {
            var buffer = new byte[32];
            Encoding.ASCII.GetBytes("SelectYesno", buffer);
            NativeStringUtil.FixedBufferEquals(buffer, "SelectYesno");

            var before = GC.GetAllocatedBytesForCurrentThread();
            var hits = 0;
            for (var i = 0; i < 1000; i++)
            {
                if (NativeStringUtil.FixedBufferEquals(buffer, "SelectYesno"))
                    hits++;
                if (NativeStringUtil.FixedBufferEquals(buffer, "Talk"))
                    hits++;
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.Equal(1000, hits);
        }
    }
}
