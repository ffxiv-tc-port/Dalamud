#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Dalamud.Game.Gui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC.Internal;

using Lumina.Text.Payloads;

using Xunit;

using LinkData = FFXIVClientStructs.FFXIV.Component.GUI.LinkData;
using LSeStringBuilder = Lumina.Text.SeStringBuilder;

namespace Dalamud.Test.Game.Gui
{
    // The game's EventTutorial links share the Dalamud link type. Clicks on them go back to the game; a click on
    // anything Dalamud encoded never reaches the game's handler, registered or not.
    public sealed class ChatGuiLinkClickTests : IDisposable
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // EventTutorial 5 (同行) and 10 (護衛) as the game puts them in the chat log.
        private static readonly byte[] Companion = Convert.FromHexString("02270D0F060101FF07E5908CE8A18C03");
        private static readonly byte[] Escort = Convert.FromHexString("02270D0F0B0101FF07E8ADB7E8A19B03");

        private static readonly byte[] Terminator = RawPayload.LinkTerminator.Data;

        private readonly List<nint> blocks = [];

        public ChatGuiLinkClickTests()
        {
            // Reporting a fault looks up the Framework service, whose locator entry waits for the container.
            Service<ServiceContainer>.Provide(new ServiceContainer());
        }

        public void Dispose()
        {
            foreach (var block in this.blocks)
                Marshal.FreeHGlobal(block);
        }

        [Fact]
        public void TheGamesTutorialLinksAreTheGames()
        {
            Assert.Equal(DalamudLinkStripper.DalamudLinkType, (uint)LinkMacroPayloadType.EventTutorial);
            Assert.Equal(Companion, new LSeStringBuilder().PushLink(LinkMacroPayloadType.EventTutorial, 5, 0, 0, "同行").ToArray());
            Assert.Equal(Escort, new LSeStringBuilder().PushLink(LinkMacroPayloadType.EventTutorial, 10, 0, 0, "護衛").ToArray());

            Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(Companion));
            Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(Escort));
            Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(Cat(Companion, Text("同行"), Terminator, Text("。"))));
            Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(new LSeStringBuilder().PushLink(LinkMacroPayloadType.EventTutorial, 5, 0, 0).ToArray()));

            // What Dalamud made of them before: a link of no plugin with ID 0, and the click was dropped.
            var link = Assert.IsType<DalamudLinkPayload>(SeString.Parse(Companion).Payloads[0]);
            Assert.Equal(string.Empty, link.Plugin);
            Assert.Equal(0u, link.CommandId);

            // Display strings that are JSON null or empty used to make Dalamud's decoder throw.
            foreach (var text in new[] { string.Empty, "null", "[]", "[\"x\"]" })
            {
                var decoded = Assert.IsType<DalamudLinkPayload>(SeString.Parse(Tutorial(5, text)).Payloads[0]);
                Assert.Equal(string.Empty, decoded.Plugin);
            }

            Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(Tutorial(5, string.Empty)));
            Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(Tutorial(5, "null")));
        }

        [Fact]
        public void BothConditionsMustHold()
        {
            Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(Companion, _ => string.Empty));

            // Whatever Dalamud's decoder reads a plugin from stays Dalamud's, even in the game's shape.
            Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(Companion, _ => "TestPlugin"));
            Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(Companion, _ => null));
            Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(Companion, _ => throw new InvalidOperationException()));

            // The decoder sees exactly the link, not what follows it.
            var seen = new List<byte[]>();
            DalamudLinkStripper.IsGameEventTutorialLink(Cat(Companion, Text("同行"), Terminator), b => { seen.Add(b); return string.Empty; });
            Assert.Equal(Companion, Assert.Single(seen));

            // The shape is checked first: a Dalamud link never reaches the decoder.
            seen.Clear();
            Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(new DalamudLinkPayload { Plugin = "TestPlugin", CommandId = 1 }.Encode(), b => { seen.Add(b); return string.Empty; }));
            Assert.Empty(seen);
        }

        [Fact]
        public void AnOverflowingStringLengthIsNotTheGamesAndNeverThrows()
        {
            // Length 0x7FFFFFFF: Lumina's TryDecodeString overflows on it and throws from Slice.
            byte[][] inputs =
            [
                Convert.FromHexString("02270B0F010101FFFE7FFFFFFF03"),
                Convert.FromHexString("02270B0F010101FFFE7FFFFFFA03"),
                Convert.FromHexString("02270B0F010101FFFEFFFFFFF603"),
                Link(0x0F, 0x01, 0x01, 0x01, 0xFF, 0x10),
            ];

            foreach (var data in inputs)
            {
                Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(data), Convert.ToHexString(data));
                Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(data, _ => string.Empty), Convert.ToHexString(data));
            }
        }

        [Fact]
        public void NoDalamudLinkIsTheGames()
        {
            DalamudLinkPayload[] links =
            [
                new() { Plugin = "TestPlugin", CommandId = 1 },
                new() { Plugin = "同行", CommandId = 5 },
                new() { Plugin = string.Empty, CommandId = 0 },
                new() { Plugin = "TestPlugin", CommandId = uint.MaxValue, Extra1 = -1, Extra2 = int.MaxValue, ExtraString = "護衛" },
                new() { Plugin = "a]b", CommandId = 10, ExtraString = "\"[" },
            ];

            foreach (var link in links)
            {
                var encoded = link.Encode();
                Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(encoded), Convert.ToHexString(encoded));
                Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(Cat(encoded, Text("點這裡"), Terminator)));
            }

            var legacy = new LSeStringBuilder()
                         .BeginMacro(MacroCode.Link)
                         .AppendIntExpression((int)DalamudLinkStripper.DalamudLinkType)
                         .AppendStringExpression("TestPlugin")
                         .AppendUIntExpression(7)
                         .EndMacro()
                         .ToArray();
            Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(legacy));
        }

        [Fact]
        public void RandomDalamudLinksAreNeverTheGames()
        {
            var random = new Random(20261021);
            string[] pieces = ["a", "Z", "同", "行", "[", "]", "\"", "{", "}", ",", " ", "\n", char.ConvertFromUtf32(0x1F600)];
            string Pick() => string.Concat(Enumerable.Range(0, random.Next(0, 8)).Select(_ => pieces[random.Next(pieces.Length)]));

            for (var round = 0; round < 3000; round++)
            {
                var link = new DalamudLinkPayload
                {
                    Plugin = Pick(),
                    CommandId = (uint)random.NextInt64(0, uint.MaxValue + 1L),
                    Extra1 = random.Next(int.MinValue, int.MaxValue),
                    Extra2 = random.Next(int.MinValue, int.MaxValue),
                    ExtraString = Pick(),
                };
                var encoded = link.Encode();

                Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(encoded), Convert.ToHexString(encoded));
            }
        }

        [Fact]
        public void OnlyTheTutorialShapeIsTheGames()
        {
            byte[][] notTheGames =
            [
                [],
                [0x02],
                Companion[..^1],
                Cat([0x00], Companion),
                Tutorial(5, "[同行]"),
                Tutorial(5, "同[行"),
                Link(0x0F, 0x06, 0x01, 0x01, 0xFF, 0x07, 0xE5, 0x90, 0x8C, 0xE8, 0xA1, 0x8C, 0x01),
                Link(0x0F, 0x06, 0x01, 0x01, 0xFF, 0x01, 0xFF, 0x01),
                Link(0x0F, 0x06, 0x01, 0xFF, 0x01, 0x01),
                Link(0x0F, 0xFF, 0x01, 0x06),
                Link(0x0F, 0x06, 0x01),
                Link(0x0F, 0x06, 0x01, 0xD8),
                Link(0x0F),
                Link(),
                Link(0x04, 0x06, 0x01, 0x01, 0xFF, 0x07, 0xE5, 0x90, 0x8C, 0xE8, 0xA1, 0x8C),
                Cat([0x02, (byte)MacroCode.If, 0x0D], Companion[3..]),
                new LSeStringBuilder().PushLinkItem(5333, "物品").ToArray(),
            ];

            foreach (var data in notTheGames)
                Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(data), Convert.ToHexString(data));

            // Any encoding of the numbers fits the shape. Dalamud's decoder only knows the one-byte type, so with a
            // longer one it reads no Dalamud link at all and the click stays where it always went.
            var longType = Link(0xF0, 0x0E, 0xF0, 0x05, 0x01, 0xFE, 0xFF, 0xFF, 0xFF, 0xFF);
            Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(longType, _ => string.Empty));
            Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(longType));
        }

        // The shape alone, without the decoding condition, already never passes anything Dalamud could dispatch.
        [Fact]
        public void WhateverIsTheGamesDalamudCannotDispatch()
        {
            var random = new Random(1021);
            byte[][] seeds = [Companion, Escort, new DalamudLinkPayload { Plugin = "TestPlugin", CommandId = 1 }.Encode()];

            for (var round = 0; round < 20000; round++)
            {
                var data = seeds[random.Next(seeds.Length)].ToArray();
                for (var flips = random.Next(1, 4); flips > 0; flips--)
                    data[random.Next(data.Length)] = (byte)random.Next(256);

                if (!DalamudLinkStripper.IsGameEventTutorialLink(data, _ => string.Empty))
                    continue;

                DalamudLinkPayload? decoded = null;
                try
                {
                    decoded = SeString.Parse(data).Payloads.FirstOrDefault() as DalamudLinkPayload;
                }
                catch (Exception)
                {
                    // Dalamud's decoder gave up: nothing it could dispatch.
                }

                Assert.True(decoded == null || decoded.Plugin.Length == 0, Convert.ToHexString(data));
            }
        }

        [Fact]
        public void LinkClicksGoWhereTheyBelong()
        {
            Assert.Equal("O", DetourHarness.OriginalCallSites(typeof(ChatGui), "HandleLinkClickDetour"));

            var chat = (ChatGui)RuntimeHelpers.GetUninitializedObject(typeof(ChatGui));
            var original = DetourHarness.InstallFakeHook(chat, "handleLinkClickHook");
            DetourHarness.SetField(chat, "dalamudLinkHandlers", new Dictionary<(string PluginName, uint CommandId), Action<uint, SeString>>());
            var clicks = new List<uint>();
            var registered = chat.AddChatLinkHandler("TestPlugin", 7, (id, _) => clicks.Add(id)).Encode();
            var neverRegistered = new DalamudLinkPayload { Plugin = "GoneOrNeverLoaded", CommandId = 7 }.Encode();

            // The game's tutorial link: handed back, once.
            var tutorial = this.Click(chat, (byte)LinkMacroPayloadType.EventTutorial, Cat(Companion, Text("同行"), Terminator));
            Assert.Equal(tutorial, Assert.Single(original.Calls)[1]);

            // Dalamud's links stay with Dalamud, with a handler, without one, and after the plugin removed it.
            this.Click(chat, (byte)LinkMacroPayloadType.EventTutorial, Cat(registered, Text("點這裡"), Terminator));
            Assert.Equal(new[] { 7u }, clicks);
            this.Click(chat, (byte)LinkMacroPayloadType.EventTutorial, Cat(neverRegistered, Text("點這裡"), Terminator));
            chat.RemoveChatLinkHandler("TestPlugin");
            this.Click(chat, (byte)LinkMacroPayloadType.EventTutorial, Cat(registered, Text("點這裡"), Terminator));
            Assert.Equal(new[] { 7u }, clicks);
            Assert.Single(original.Calls);

            // Other link types are the game's, as before.
            var item = new LSeStringBuilder().PushLinkItem(5333, "物品").ToArray();
            this.Click(chat, (byte)LinkMacroPayloadType.Item, Cat(item, Text("物品"), Terminator));
            Assert.Equal(2, original.Calls.Count);
        }

        private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

        private static byte[] Tutorial(uint id, string text)
            => new LSeStringBuilder().PushLink(LinkMacroPayloadType.EventTutorial, id, 0, 0, text).ToArray();

        private static byte[] Link(params byte[] body) => Cat([0x02, (byte)MacroCode.Link, (byte)(body.Length + 1)], body, [0x03]);

        private static int Offset(string field)
            => typeof(LinkData).GetField(field, AnyInstance)!.GetCustomAttribute<FieldOffsetAttribute>()!.Value;

        private nint Click(ChatGui chat, byte linkType, byte[] payload)
        {
            var bytes = this.Track(DetourHarness.Bytes(Cat(payload, [0x00])));
            var linkData = this.Track(DetourHarness.Zeroed(Unsafe.SizeOf<LinkData>()));
            Marshal.WriteIntPtr(linkData, Offset(nameof(LinkData.Payload)), bytes);
            Marshal.WriteByte(linkData, Offset(nameof(LinkData.LinkType)), linkType);

            DetourHarness.Invoke(chat, "HandleLinkClickDetour", nint.Zero, linkData);
            return linkData;
        }

        private nint Track(nint block)
        {
            this.blocks.Add(block);
            return block;
        }
    }
}
