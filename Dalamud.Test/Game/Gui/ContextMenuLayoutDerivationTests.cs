#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

using Dalamud.Game.Gui.ContextMenu;

using Xunit;

namespace Dalamud.Test.Game.Gui
{
    public class ContextMenuLayoutDerivationTests
    {
        private const int NotDerived = ContextMenuLayoutDerivation.NotDerived;

        // Bytes at the first match of each source's first shape in the TC 7.25 executable.
        private static readonly byte[] AgentReset725 = Hex("B9 02 00 00 00 BA 07 00 00 00 33 FF 66 89 90 A0 F9 FF FF C6 80 A2 F9 FF FF 71");
        private static readonly byte[] Refresh725 = Hex("4D 8D 4F 70 8B 8F A8 02 00 00 45 8D 46 F9 88 5C 24");
        private static readonly byte[] InventoryOpen725 = Hex("C7 46 2C 08 00 00 00 48 8D 4E 48 E8 09 40 19 00 4C 89 6E 50 EB 07 C7 46 2C 07 00 00 00");

        [Fact]
        public void Tc725DerivesSevenFromAllThreeSources()
        {
            var index = Derive(new Game(), out var evidence);

            Assert.Equal(7, index);
            var parts = evidence.Split('；');
            Assert.Equal(3, parts.Length);
            Assert.All(parts, p => Assert.EndsWith("=7", p));
        }

        [Fact]
        public void Gen74LayoutDerivesEight()
        {
            var game = new Game { AgentReset = AgentReset(8), Refresh = null, RefreshDisp32 = RefreshDisp32(8), InventoryOpen = InventoryOpen(8) };

            Assert.Equal(8, Derive(game, out _));
        }

        [Fact]
        public void Gen74LayoutWithUnknownRefreshShapeStillDerivesEightFromTheOtherTwo()
        {
            var game = new Game { AgentReset = AgentReset(8), Refresh = null, InventoryOpen = InventoryOpen(8) };

            Assert.Equal(8, Derive(game, out _));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void AnySingleDisagreeingSourceBlocks(int which)
        {
            var game = new Game
            {
                AgentReset = which == 0 ? AgentReset(8) : AgentReset725,
                Refresh = which == 1 ? RefreshDisp8(6) : Refresh725,
                InventoryOpen = which == 2 ? InventoryOpen(8) : InventoryOpen725,
            };

            Assert.Equal(NotDerived, Derive(game, out _));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void OneSourceAloneIsNotEnough(int keep)
        {
            var game = new Game
            {
                AgentReset = keep == 0 ? AgentReset725 : null,
                Refresh = keep == 1 ? Refresh725 : null,
                InventoryOpen = keep == 2 ? InventoryOpen725 : null,
            };

            Assert.Equal(NotDerived, Derive(game, out _));
        }

        [Fact]
        public void InternallyInconsistentSourcesAreDropped()
        {
            // Each would read as 8 and contradict the other two if its consistency check were missing.
            var inventory = (byte[])InventoryOpen725.Clone();
            inventory[3] = 8;
            inventory[25] = 8;
            Assert.Equal(7, Derive(new Game { InventoryOpen = inventory }, out _));

            var agent = AgentReset(8);
            agent[21] ^= 0x10;
            Assert.Equal(7, Derive(new Game { AgentReset = agent }, out _));

            var refresh = (byte[])Refresh725.Clone();
            refresh[13] = unchecked((byte)-8);
            Assert.Equal(7, Derive(new Game { Refresh = refresh }, out var evidence));
            Assert.Contains("AddonContextMenu.OnRefresh=命中但內部不一致", evidence);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(20)]
        public void AgreeingValuesOutsideTheRangeAreRejected(int value)
        {
            var game = new Game { AgentReset = AgentReset(value), Refresh = null, InventoryOpen = InventoryOpen(value) };

            Assert.Equal(NotDerived, Derive(game, out _));
        }

        [Fact]
        public void BytesThatDoNotMatchTheSignatureCountAsNoMatch()
        {
            // A stale signature cache can hand back an unrelated address; a look-alike with one fixed byte off must not count either.
            var lookAlike = AgentReset(8);
            lookAlike[25] = 0x72;
            Assert.Equal(7, Derive(new Game { AgentReset = lookAlike }, out var evidence));
            Assert.Contains("命中處位元組與特徵碼不符", evidence);

            Assert.Equal(NotDerived, Derive(new Game { AgentReset = Refresh725.Concat(new byte[9]).ToArray(), InventoryOpen = lookAlike.Concat(new byte[3]).ToArray() }, out _));
        }

        [Fact]
        public void WrongLengthCountsAsNoMatch()
        {
            var game = new Game { AgentReset = AgentReset725[..^1], InventoryOpen = InventoryOpen725.Concat(new byte[1]).ToArray() };

            Assert.Equal(NotDerived, Derive(game, out _));
        }

        [Fact]
        public void EverySignatureCoversWhatItsDecoderReads()
        {
            Assert.Equal(3, ContextMenuLayoutDerivation.Sources.Length);
            var lengths = ContextMenuLayoutDerivation.Sources.SelectMany(s => s.Variants).Select(v => ContextMenuLayoutDerivation.SignatureLength(v.Signature)).ToArray();
            Assert.Equal(new[] { AgentReset725.Length, Refresh725.Length, RefreshDisp32(8).Length, InventoryOpen725.Length }, lengths);
        }

        private static int Derive(Game game, out string evidence)
        {
            var bySignature = new Dictionary<string, byte[]?>();
            var variants = ContextMenuLayoutDerivation.Sources.SelectMany(s => s.Variants).ToArray();
            bySignature[variants[0].Signature] = game.AgentReset;
            bySignature[variants[1].Signature] = game.Refresh;
            bySignature[variants[2].Signature] = game.RefreshDisp32;
            bySignature[variants[3].Signature] = game.InventoryOpen;
            return ContextMenuLayoutDerivation.Derive((signature, _) => bySignature[signature], out evidence);
        }

        private static byte[] AgentReset(int first)
        {
            var b = (byte[])AgentReset725.Clone();
            b[6] = (byte)first;
            return b;
        }

        private static byte[] InventoryOpen(int first)
        {
            var b = (byte[])InventoryOpen725.Clone();
            b[3] = (byte)(first + 1);
            b[25] = (byte)first;
            return b;
        }

        private static byte[] RefreshDisp8(int first)
        {
            var b = (byte[])Refresh725.Clone();
            b[3] = (byte)(first * 16);
            b[13] = unchecked((byte)(sbyte)-first);
            return b;
        }

        // 16 * first no longer fits a signed byte from 8 on, so the compiler switches lea r9 to a 32-bit displacement.
        private static byte[] RefreshDisp32(int first)
        {
            var b = new List<byte> { 0x4D, 0x8D, 0x8F };
            b.AddRange(BitConverter.GetBytes(first * 16));
            b.AddRange(Refresh725[4..]);
            b[16] = unchecked((byte)(sbyte)-first);
            return b.ToArray();
        }

        private static byte[] Hex(string text)
            => text.Split(' ').Select(t => Convert.ToByte(t, 16)).ToArray();

        private sealed class Game
        {
            public byte[]? AgentReset { get; set; } = AgentReset725;

            public byte[]? Refresh { get; set; } = Refresh725;

            public byte[]? RefreshDisp32 { get; set; }

            public byte[]? InventoryOpen { get; set; } = InventoryOpen725;
        }
    }
}
