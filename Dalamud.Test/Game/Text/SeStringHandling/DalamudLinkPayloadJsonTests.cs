#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

using Dalamud.Game.Gui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;

using Lumina.Text.Payloads;

using Newtonsoft.Json;

using Xunit;

using LSeStringBuilder = Lumina.Text.SeStringBuilder;

namespace Dalamud.Test.Game.Text.SeStringHandling
{
    // Tests that set JsonConvert.DefaultSettings, which is process-wide: nothing else may run meanwhile.
    [CollectionDefinition(Name, DisableParallelization = true)]
    public class GlobalJsonSettingsCollection
    {
        public const string Name = "JsonConvert.DefaultSettings";
    }

    // DalamudLinkPayload writes and reads the plugin name with its own Newtonsoft settings: the bytes JsonConvert wrote
    // under Newtonsoft's defaults, and still a JSON array whatever a plugin puts in JsonConvert.DefaultSettings.
    [Collection(GlobalJsonSettingsCollection.Name)]
    public sealed class DalamudLinkPayloadJsonTests
    {
        private const string Backslash = "\\";

        // TestPlugin, command 1, as Dalamud has always encoded it.
        private static readonly byte[] Pinned = Convert.FromHexString("0227180F020101FF125B2254657374506C7567696E222C22225D03");

        // The game's EventTutorial 5 (同行).
        private static readonly byte[] Companion = Convert.FromHexString("02270D0F060101FF07E5908CE8A18C03");

        private static readonly Func<JsonSerializerSettings>[] Hijacks =
        [
            () => new JsonSerializerSettings { Converters = [new FlatStringArrayConverter()] },
            () => new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All,
                Formatting = Formatting.Indented,
                StringEscapeHandling = StringEscapeHandling.EscapeNonAscii,
            },
            () => new JsonSerializerSettings { Converters = [new ThrowingStringArrayConverter()] },
        ];

        [Fact]
        public void EncodingIsByteForByteWhatJsonConvertWrote()
        {
            Assert.Null(JsonConvert.DefaultSettings);
            Assert.Equal(Pinned, new DalamudLinkPayload { Plugin = "TestPlugin", CommandId = 1 }.Encode());

            foreach (var link in Samples(new Random(1021), 2000))
                Assert.Equal(Convert.ToHexString(OldEncode(link)), Convert.ToHexString(link.Encode()));
        }

        [Fact]
        public void DecodingIsWhatJsonConvertRead()
        {
            Assert.Null(JsonConvert.DefaultSettings);
            var deserialize = typeof(DalamudLinkPayload).GetMethod("DeserializeExtraData", BindingFlags.Static | BindingFlags.NonPublic)!;

            foreach (var json in Corpus())
            {
                var expected = Outcome(() => JsonConvert.DeserializeObject<string[]>(json));
                var actual = Outcome(() => (string[]?)Invoke(deserialize, json));
                Assert.True(expected == actual, $"{json}: {expected} / {actual}");

                // The whole link: values only from an array of at least two; otherwise an empty link, no exception.
                // The builder writes UTF-8, which turns a lone surrogate into U+FFFD; JsonConvert reads what was written.
                string[]? array = null;
                try
                {
                    array = JsonConvert.DeserializeObject<string[]>(Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(json)));
                }
                catch (Exception)
                {
                    // Not an array: an empty link.
                }

                var decoded = Assert.IsType<DalamudLinkPayload>(SeString.Parse(Link(7, json)).Payloads[0]);
                Assert.Equal(array is { Length: >= 2 } ? array[0] : string.Empty, decoded.Plugin);
                Assert.Equal(array is { Length: >= 2 } ? 7u : 0u, decoded.CommandId);
            }

            var legacy = new LSeStringBuilder()
                         .BeginMacro(MacroCode.Link)
                         .AppendIntExpression((int)DalamudLinkStripper.DalamudLinkType)
                         .AppendStringExpression("TestPlugin")
                         .AppendUIntExpression(7)
                         .EndMacro()
                         .ToArray();
            var legacyLink = Assert.IsType<DalamudLinkPayload>(SeString.Parse(legacy).Payloads[0]);
            Assert.Equal("TestPlugin", legacyLink.Plugin);
            Assert.Equal(7u, legacyLink.CommandId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void AHijackedDefaultSettingsChangesNothing(int hijack)
        {
            var previous = JsonConvert.DefaultSettings;
            JsonConvert.DefaultSettings = Hijacks[hijack];
            try
            {
                // Calibration: the hijack does change what JsonConvert writes.
                Assert.NotEqual("ok:[\"TestPlugin\",\"\"]", Outcome(() => JsonConvert.SerializeObject(new[] { "TestPlugin", string.Empty })));

                Assert.Equal(Pinned, new DalamudLinkPayload { Plugin = "TestPlugin", CommandId = 1 }.Encode());
                foreach (var link in Samples(new Random(hijack), 200))
                    Assert.Contains((byte)'[', link.Encode());

                var decoded = Assert.IsType<DalamudLinkPayload>(SeString.Parse(Pinned).Payloads[0]);
                Assert.Equal("TestPlugin", decoded.Plugin);
                Assert.Equal(1u, decoded.CommandId);
                Assert.Equal(string.Empty, Assert.IsType<DalamudLinkPayload>(SeString.Parse(Companion).Payloads[0]).Plugin);

                Assert.False(DalamudLinkStripper.IsGameEventTutorialLink(Pinned));
                Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(Companion));
            }
            finally
            {
                JsonConvert.DefaultSettings = previous;
            }
        }

        [Fact]
        public void WithJsonConvertAHijackWouldHaveWrittenAGameShapedLink()
        {
            var link = new DalamudLinkPayload { Plugin = "TestPlugin", CommandId = 1 };
            var previous = JsonConvert.DefaultSettings;
            JsonConvert.DefaultSettings = Hijacks[0];
            try
            {
                var old = OldEncode(link);
                Assert.DoesNotContain((byte)'[', old);
                Assert.True(DalamudLinkStripper.IsGameEventTutorialLink(old, _ => string.Empty));
                Assert.NotEqual(old, link.Encode());
            }
            finally
            {
                JsonConvert.DefaultSettings = previous;
            }
        }

        // The encoder this replaced, verbatim apart from the payload being passed in.
        private static byte[] OldEncode(DalamudLinkPayload link)
            => new LSeStringBuilder()
               .BeginMacro(MacroCode.Link)
               .AppendIntExpression((int)Payload.EmbeddedInfoType.DalamudLink - 1)
               .AppendUIntExpression(link.CommandId)
               .AppendIntExpression(link.Extra1)
               .AppendIntExpression(link.Extra2)
               .BeginStringExpression()
               .Append(JsonConvert.SerializeObject(new[] { link.Plugin, link.ExtraString }))
               .EndExpression()
               .EndMacro()
               .ToArray();

        private static byte[] Link(uint commandId, string json)
            => new LSeStringBuilder()
               .BeginMacro(MacroCode.Link)
               .AppendIntExpression((int)Payload.EmbeddedInfoType.DalamudLink - 1)
               .AppendUIntExpression(commandId)
               .AppendIntExpression(0)
               .AppendIntExpression(0)
               .AppendStringExpression(json)
               .EndMacro()
               .ToArray();

        private static IEnumerable<DalamudLinkPayload> Samples(Random random, int count)
        {
            string[] pieces =
            [
                "a", "Z", "同", "行", "[", "]", "\"", Backslash, "<", "&", "'", "\t", "\n", ((char)1).ToString(), ((char)0x1F).ToString(),
                ((char)0x7F).ToString(), ((char)0x85).ToString(), ((char)0x2028).ToString(), ((char)0xD800).ToString(),
                char.ConvertFromUtf32(0x1F600),
            ];
            string Pick() => string.Concat(Enumerable.Range(0, random.Next(0, 8)).Select(_ => pieces[random.Next(pieces.Length)]));

            yield return new DalamudLinkPayload { Plugin = null!, CommandId = 3 };
            for (var i = 0; i < count; i++)
            {
                yield return new DalamudLinkPayload
                {
                    Plugin = Pick(),
                    CommandId = (uint)random.NextInt64(0, uint.MaxValue + 1L),
                    Extra1 = random.Next(int.MinValue, int.MaxValue),
                    Extra2 = random.Next(int.MinValue, int.MaxValue),
                    ExtraString = Pick(),
                };
            }
        }

        private static IEnumerable<string> Corpus()
        {
            string[] fixedOnes =
            [
                @"[""TestPlugin"",""""]",
                @"[""a"",""b"",""c""]",
                @" [ ""a"" , ""b"" ] ",
                "[\n  \"a\",\n  \"b\"\n]",
                @"/*c*/[""a"",""b""]",
                @"[""a"",null]",
                @"[null,""b""]",
                @"[1,2]",
                @"[true,""b""]",
                "[\"" + Backslash + "u540c" + Backslash + "u884c\",\"x\"]",
                @"[""a"",""b""]x",
                "null",
                string.Empty,
                " ",
                "[]",
                @"[""a""]",
                @"{""$type"":""System.String[], System.Private.CoreLib"",""$values"":[""a"",""b""]}",
                @"""a|b""",
                "同行",
                "[",
                @"[""a"",[""b""]]",
                @"[""a"",{""b"":1}]",
            ];

            foreach (var json in fixedOnes)
                yield return json;

            foreach (var link in Samples(new Random(7), 300))
                yield return JsonConvert.SerializeObject(new[] { link.Plugin, link.ExtraString });
        }

        private static object? Invoke(MethodInfo method, string json)
        {
            try
            {
                return method.Invoke(null, [json]);
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException!;
            }
        }

        private static string Outcome(Func<object?> run)
        {
            try
            {
                return run() switch
                {
                    null => "null",
                    string s => "ok:" + s,
                    string[] a => "ok:" + string.Join("|", a.Select(x => x ?? "<null>")) + "#" + a.Length,
                    var other => "ok:" + other,
                };
            }
            catch (Exception ex)
            {
                return "throws:" + ex.GetType().FullName;
            }
        }

        private sealed class FlatStringArrayConverter : JsonConverter<string[]>
        {
            public override void WriteJson(JsonWriter writer, string[]? value, JsonSerializer serializer)
                => writer.WriteValue(string.Join("|", value!));

            public override string[] ReadJson(JsonReader reader, Type objectType, string[]? existingValue, bool hasExistingValue, JsonSerializer serializer)
                => ((string)reader.Value!).Split('|');
        }

        private sealed class ThrowingStringArrayConverter : JsonConverter<string[]>
        {
            public override void WriteJson(JsonWriter writer, string[]? value, JsonSerializer serializer)
                => throw new InvalidOperationException("hijacked");

            public override string[] ReadJson(JsonReader reader, Type objectType, string[]? existingValue, bool hasExistingValue, JsonSerializer serializer)
                => throw new InvalidOperationException("hijacked");
        }
    }
}
