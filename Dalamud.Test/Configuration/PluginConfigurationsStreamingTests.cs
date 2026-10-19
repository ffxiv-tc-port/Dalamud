#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;

using Dalamud.Configuration;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC.Internal;
using Dalamud.Storage;
using Dalamud.Utility.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

using Xunit;

namespace Dalamud.Test.Configuration
{
    // The oracle for every comparison is the code these paths replaced: JsonConvert.DeserializeObject<T>(File.ReadAllText)
    // for loading, Encoding.UTF8.GetBytes(SerializeConfig) for saving.
    public class PluginConfigurationsStreamingTests
    {
        private static readonly Lazy<ReliableFileStorage> Rfs = new(() =>
        {
            // Service<T> waits for the container before it accepts anything.
            Service<ServiceContainer>.Provide(new ServiceContainer());
            var storage = new ReliableFileStorage(CreateTempDir());
            Service<ReliableFileStorage>.Provide(storage);
            return storage;
        });

        public enum Mode
        {
            A,
            B = 5,
            C,
        }

        public static IEnumerable<object[]> Cultures => new[] { "", "de-DE", "ar-SA", "tr-TR", "zh-TW" }.Select(c => new object[] { c });

        [Theory]
        [MemberData(nameof(Cultures))]
        public void SaveBytesMatchStringPath(string culture)
        {
            WithCulture(culture, () =>
            {
                foreach (var config in SampleConfigs())
                    Assert.Equal(OracleSaveBytes(config), PluginConfigurations.SerializeConfigToUtf8(config));
            });
        }

        [Fact]
        public void SaveBytesMatchStringPathForNull() =>
            Assert.Equal(OracleSaveBytes(null), PluginConfigurations.SerializeConfigToUtf8(null));

        [Fact]
        public void SaveSurrogatesAcrossWriterBufferBoundaries()
        {
            var config = new RichConfig();
            for (var i = 0; i < 1100; i++)
            {
                config.Tags.Add(new string('x', i) + "\U0001F600" + i);
                config.Map[$"lone{i}"] = new Inner { Name = new string('y', i) + "\uD800" + (i % 2 == 0 ? "z" : "\uDC00\uDC00") };
            }

            config.Text = "end-high\uD83D";
            Assert.Equal(OracleSaveBytes(config), PluginConfigurations.SerializeConfigToUtf8(config));
        }

        [Fact]
        public void SaveFailuresMatchStringPath()
        {
            AssertSameFailure(
                () => OracleSaveBytes(new ThrowingConfig()),
                () => PluginConfigurations.SerializeConfigToUtf8(new ThrowingConfig()));

            var loop = new LoopConfig();
            loop.Self = loop;
            AssertSameFailure(() => OracleSaveBytes(loop), () => PluginConfigurations.SerializeConfigToUtf8(loop));
        }

        [Fact]
        public void SaveWritesFileAndBackupWithStringPathBytes()
        {
            var rfs = Rfs.Value;
            var configs = new PluginConfigurations(CreateTempDir());
            var id = Guid.NewGuid();
            var config = BigConfig(2000);

            configs.Save(config, "StreamTest", id);

            var path = configs.GetConfigFile("StreamTest").FullName;
            var expected = OracleSaveBytes(config);
            Assert.Equal(expected, File.ReadAllBytes(path));
            Assert.Equal(expected, rfs.ReadAllBytesAsync(path, true, id).GetAwaiter().GetResult());
            Assert.Equal(
                SerializeOracle(PluginConfigurations.DeserializeConfig(Encoding.UTF8.GetString(expected))),
                SerializeOracle(configs.Load("StreamTest", id)));
        }

        [Fact]
        public void SaveFailureLeavesNoFile()
        {
            _ = Rfs.Value;
            var configs = new PluginConfigurations(CreateTempDir());
            AssertSameFailure(
                () => OracleSaveBytes(new ThrowingConfig()),
                () => configs.Save(new ThrowingConfig(), "Throws", Guid.NewGuid()));
            Assert.False(configs.GetConfigFile("Throws").Exists);
        }

        [Theory]
        [MemberData(nameof(Cultures))]
        public void LoadMatchesStringPath(string culture)
        {
            WithCulture(culture, () =>
            {
                var outcomes = LoadCases().ToDictionary(c => c.Name, c => AssertLoadMatches<RichConfig>(c.Bytes, c.Name));

                // Calibration: the cases must really cover values, nulls and failures.
                foreach (var name in new[] { "indented", "minified+bom", "utf16le+bom", "utf16be+bom", "big", "surrogates", "trailing comment+space", "invalid utf8", "own $type", "duplicate keys", "surrogate property", "comments inside", "single quotes" })
                    Assert.True(outcomes[name] == "value", $"{name}: {outcomes[name]}");
                foreach (var name in new[] { "empty", "bom only", "whitespace", "null literal" })
                    Assert.True(outcomes[name] == "null", $"{name}: {outcomes[name]}");
                foreach (var name in new[] { "additional object", "additional garbage", "truncated", "truncated big", "array root", "number root", "wrong field type" })
                    Assert.True(outcomes[name].StartsWith("Newtonsoft.Json."), $"{name}: {outcomes[name]}");
            });
        }

        [Fact]
        public void LoadOtherRootTypes()
        {
            AssertLoadMatches<LoopConfig>(Encoding.UTF8.GetBytes("{\"Version\": 2, \"Self\": {\"Version\": 3}}"), "nested");
            AssertLoadMatches<ThrowingConfig>(Encoding.UTF8.GetBytes("{\"Version\": 4}"), "throwing getter ignored on read");
        }

        [Fact]
        public void AttributesStillApplyThroughStreams()
        {
            var config = new RichConfig { Effective = 5, Ignored = "runtime only", ModeAsString = Mode.C, RenamedField = 8 };
            var text = Encoding.UTF8.GetString(PluginConfigurations.SerializeConfigToUtf8(config));
            Assert.Contains("\"Effective\": 0", text);
            Assert.DoesNotContain("runtime only", text);
            Assert.Contains("\"ModeAsString\": \"C\"", text);
            Assert.Contains("\"renamed\": 8", text);

            var configs = new PluginConfigurations(CreateTempDir());
            File.WriteAllText(configs.GetConfigFile("Attr").FullName, "{\"Effective\": 77, \"Ignored\": \"x\", \"ModeAsString\": \"B\", \"renamed\": 3}");
            var loaded = configs.LoadForType<RichConfig>("Attr");
            Assert.Equal(1077, loaded.Effective);
            Assert.Equal("ignored", loaded.Ignored);
            Assert.Equal(Mode.B, loaded.ModeAsString);
            Assert.Equal(3, loaded.RenamedField);
        }

        [Fact]
        public void LoadMissingFileReturnsDefault()
        {
            var configs = new PluginConfigurations(CreateTempDir());
            Assert.Null(configs.LoadForType<RichConfig>("Missing"));
        }

        [Fact]
        public void LoadLockedFileFailsLikeStringPath()
        {
            var configs = new PluginConfigurations(CreateTempDir());
            var path = configs.GetConfigFile("Locked").FullName;
            File.WriteAllBytes(path, OracleSaveBytes(BigConfig(3)));
            using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            AssertSameFailure(() => OracleLoad<RichConfig>(path), () => configs.LoadForType<RichConfig>("Locked"));
        }

        [Fact]
        public void LoadAndSaveAllocateFarLessThanStringPath()
        {
            var configs = new PluginConfigurations(CreateTempDir());
            var path = configs.GetConfigFile("Big").FullName;
            var config = BigConfig(20000);
            var bytes = OracleSaveBytes(config);
            File.WriteAllBytes(path, bytes);
            Assert.True(bytes.Length > 2_000_000, $"{bytes.Length}");

            // Warm both paths so JIT-time allocations stay out of the counts.
            OracleLoad<RichConfig>(path);
            configs.LoadForType<RichConfig>("Big");
            OracleSaveBytes(config);
            PluginConfigurations.SerializeConfigToUtf8(config);

            var start = AllocatedBytes.Start();
            OracleLoad<RichConfig>(path);
            var oracleLoad = AllocatedBytes.Since(start);
            start = AllocatedBytes.Start();
            configs.LoadForType<RichConfig>("Big");
            var streamLoad = AllocatedBytes.Since(start);

            start = AllocatedBytes.Start();
            OracleSaveBytes(config);
            var oracleSave = AllocatedBytes.Since(start);
            start = AllocatedBytes.Start();
            PluginConfigurations.SerializeConfigToUtf8(config);
            var streamSave = AllocatedBytes.Since(start);

            // The string path holds the whole file as UTF-16 at least twice; the stream path never does.
            Assert.True(oracleLoad - streamLoad > bytes.Length * 2L, $"load {oracleLoad} -> {streamLoad}, file {bytes.Length}");
            Assert.True(oracleSave - streamSave > bytes.Length * 2L, $"save {oracleSave} -> {streamSave}, file {bytes.Length}");
        }

        [Fact]
        public void ChunkStreamKeepsEveryByteInOrder()
        {
            var random = new Random(1234);
            var expected = new MemoryStream();
            using var stream = new PooledChunkWriteStream();
            for (var i = 0; i < 400; i++)
            {
                var piece = new byte[random.Next(0, i % 50 == 0 ? 300_000 : 5000)];
                random.NextBytes(piece);
                expected.Write(piece);
                if (piece.Length == 1)
                    stream.WriteByte(piece[0]);
                else
                    stream.Write(piece, 0, piece.Length);
            }

            Assert.Equal(expected.Length, stream.Length);
            Assert.Equal(expected.ToArray(), stream.ToArray());
            stream.Dispose();
            Assert.False(stream.CanWrite);
            Assert.Throws<ObjectDisposedException>(() => stream.WriteByte(1));
            Assert.Throws<ObjectDisposedException>(() => stream.ToArray());
        }

        private static IEnumerable<(string Name, byte[] Bytes)> LoadCases()
        {
            var rich = SampleConfigs().ToArray();
            var indented = OracleSaveBytes(rich[0]);
            var minified = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(rich[0], Formatting.None, new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects }));
            yield return ("indented", indented);
            yield return ("minified+bom", Encoding.UTF8.GetPreamble().Concat(minified).ToArray());
            yield return ("utf16le+bom", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(Encoding.UTF8.GetString(indented))).ToArray());
            yield return ("utf16be+bom", Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(Encoding.UTF8.GetString(minified))).ToArray());
            yield return ("big", OracleSaveBytes(BigConfig(20000)));
            yield return ("surrogates", OracleSaveBytes(rich[2]));
            yield return ("trailing comment+space", indented.Concat(Encoding.UTF8.GetBytes("\r\n/* trailing */ \r\n\t ")).ToArray());
            yield return ("additional object", indented.Concat(Encoding.UTF8.GetBytes(" {}")).ToArray());
            yield return ("additional garbage", indented.Concat(Encoding.UTF8.GetBytes("x")).ToArray());
            yield return ("empty", []);
            yield return ("bom only", Encoding.UTF8.GetPreamble());
            yield return ("whitespace", Encoding.UTF8.GetBytes(" \r\n\t "));
            yield return ("null literal", Encoding.UTF8.GetBytes("null"));
            yield return ("truncated", indented.Take(indented.Length / 2).ToArray());
            yield return ("truncated big", OracleSaveBytes(BigConfig(3000)).Take(150_000).ToArray());
            yield return ("array root", Encoding.UTF8.GetBytes("[1,2]"));
            yield return ("number root", Encoding.UTF8.GetBytes("42"));
            yield return ("wrong field type", Encoding.UTF8.GetBytes("{\"Version\": \"abc\"}"));
            yield return ("invalid utf8", Encoding.UTF8.GetBytes("{\"Text\": \"a").Concat(new byte[] { 0xC3, 0x28, 0xFF, 0xE2, 0x82 }).Concat(Encoding.UTF8.GetBytes("b\", \"Version\": 7}")).ToArray());
            yield return ("own $type", Encoding.UTF8.GetBytes($"{{\"$type\": \"{typeof(RichConfig).FullName}, Dalamud.Test\", \"Version\": 9}}"));
            yield return ("foreign $type", Encoding.UTF8.GetBytes("{\"$type\": \"Some.Plugin.Configuration, SomePlugin\", \"Version\": 9}"));
            yield return ("duplicate keys", Encoding.UTF8.GetBytes("{\"Version\": 1, \"Version\": 2, \"renamed\": 3, \"RenamedField\": 4}"));
            yield return ("surrogate property", Encoding.UTF8.GetBytes("{\"Effective\": 77, \"Ignored\": \"should stay default\"}"));
            yield return ("comments inside", Encoding.UTF8.GetBytes("{/*c*/\"Version\"://x\n 3}"));
            yield return ("single quotes", Encoding.UTF8.GetBytes("{'Version': 3, 'Text': 'q'}"));
        }

        private static IEnumerable<RichConfig> SampleConfigs()
        {
            yield return FullConfig();
            yield return new RichConfig();
            var surrogates = FullConfig();
            surrogates.Text = "a\uD800b\uDC00c\U0001F600d\u00E9\u4E2D\u0000\u001f\"\\/\u2028\u2029\uFFFF";
            yield return surrogates;
            yield return BigConfig(500);
        }

        private static RichConfig FullConfig()
        {
            var config = new RichConfig
            {
                Version = 7,
                Text = "插件設定 \"quoted\" \\ back\r\nline\ttab",
                Big = long.MinValue,
                UBig = ulong.MaxValue,
                Money = 12345.6789012345678901234567m,
                D = 1.5e-300,
                F = 3.4028235E+38f,
                When = new DateTime(2026, 10, 16, 1, 2, 3, 456, DateTimeKind.Utc).AddTicks(7),
                WhenLocal = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Local),
                WhenUnspecified = new DateTime(2025, 12, 31, 23, 59, 59),
                WhenOffset = new DateTimeOffset(2026, 10, 16, 1, 2, 3, TimeSpan.FromHours(8)),
                Span = TimeSpan.FromMilliseconds(-123456789.5),
                Id = Guid.Parse("6f1c1e1a-0000-4000-8000-00000000a40c"),
                Mode = Mode.C,
                ModeAsString = Mode.B,
                MaybeSet = -5,
                Blob = [0, 1, 2, 250, 255],
                Color = new Vector4(0.1f, 0.25f, 1f, float.Epsilon),
                Pos = new Vector2(-0f, 1e-7f),
                Arr = [int.MinValue, 0, int.MaxValue],
                Uints = [uint.MaxValue],
                Ch = '\u4E2D',
                Sb = sbyte.MinValue,
                RenamedField = 42,
                Effective = 99,
                Untyped = new Inner { Name = "boxed" },
                SeText = new SeStringBuilder().AddText("se ").AddItalics("string").Build(),
            };
            config.Shapes.Add(new Circle { Id = 1, R = 2.5f });
            config.Shapes.Add(new Square { Id = 2, Side = 0.1 + 0.2 });
            config.Map["k"] = new Inner { Name = "v", Values = { 1, 2, 3 }, ByMode = { [Mode.A] = double.NegativeInfinity, [Mode.B] = -0.0 } };
            config.Tags.Add("t1");
            config.Tags.Add("t2");
            return config;
        }

        private static RichConfig BigConfig(int count)
        {
            var config = FullConfig();
            for (var i = 0; i < count; i++)
            {
                config.Map[$"preset {i}"] = new Inner
                {
                    Name = $"海釣 preset #{i} \U0001F41F",
                    Values = Enumerable.Range(i, 20).ToList(),
                    ByMode = { [Mode.A] = i * 0.001, [Mode.C] = i / 3.0 },
                };
                config.Shapes.Add(i % 2 == 0 ? new Circle { Id = i, R = i } : new Square { Id = i, Side = i * 1.5 });
            }

            return config;
        }

        private static byte[] OracleSaveBytes(object? config) => Encoding.UTF8.GetBytes(PluginConfigurations.SerializeConfig(config));

        private static string SerializeOracle(object? config) => PluginConfigurations.SerializeConfig(config);

        private static T OracleLoad<T>(string path) where T : IPluginConfiguration
        {
            var converterType = typeof(PluginConfigurations).GetNestedType("DalamudAssemblyTypeNameForcingJsonConverter", BindingFlags.NonPublic)!;
            var converter = (JsonConverter)converterType.GetField("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), new JsonSerializerSettings { Converters = [converter] })!;
        }

        private static string AssertLoadMatches<T>(byte[] bytes, string name) where T : IPluginConfiguration
        {
            var configs = new PluginConfigurations(CreateTempDir());
            var path = configs.GetConfigFile("Case").FullName;
            File.WriteAllBytes(path, bytes);

            var (expected, expectedError) = Capture(() => OracleLoad<T>(path));
            var (actual, actualError) = Capture(() => configs.LoadForType<T>("Case"));
            AssertSameError(expectedError, actualError, name);
            DeepEqual(expected, actual, name);
            var (expectedText, expectedTextError) = Capture(() => SerializeOracle(expected));
            var (actualText, actualTextError) = Capture(() => SerializeOracle(actual));
            AssertSameError(expectedTextError, actualTextError, name + " reserialized");
            Assert.Equal(expectedText, actualText);
            return actualError?.GetType().FullName ?? (actual is null ? "null" : "value");
        }

        private static void AssertSameFailure(Func<object?> expected, Func<object?> actual)
        {
            var (_, expectedError) = Capture(expected);
            var (_, actualError) = Capture(actual);
            Assert.NotNull(expectedError);
            AssertSameError(expectedError, actualError, "failure");
        }

        private static void AssertSameFailure(Func<object?> expected, Action actual) =>
            AssertSameFailure(expected, () =>
            {
                actual();
                return null;
            });

        private static void AssertSameError(Exception? expected, Exception? actual, string name)
        {
            Assert.True(expected?.GetType() == actual?.GetType(), $"{name}: {expected} vs {actual}");
            if (expected is null)
                return;
            Assert.Equal(expected.Message, actual!.Message);
            Assert.Equal(expected.HResult, actual.HResult);
            Assert.Equal(expected.InnerException?.GetType(), actual.InnerException?.GetType());
            Assert.Equal(expected.InnerException?.Message, actual.InnerException?.Message);
        }

        private static (object? Value, Exception? Error) Capture(Func<object?> func)
        {
            try
            {
                return (func(), null);
            }
            catch (Exception e)
            {
                return (null, e);
            }
        }

        private static void DeepEqual(object? a, object? b, string path)
        {
            if (a is null || b is null)
            {
                Assert.True(a is null && b is null, $"{path}: {a ?? "null"} vs {b ?? "null"}");
                return;
            }

            var type = a.GetType();
            Assert.True(type == b.GetType(), $"{path}: {type} vs {b.GetType()}");
            if (type.IsPrimitive || type.IsEnum || a is string or decimal or DateTime or DateTimeOffset or TimeSpan or Guid)
            {
                Assert.True(a.Equals(b) && a.ToString() == b.ToString(), $"{path}: {a} vs {b}");
                return;
            }

            if (a is IEnumerable ea)
            {
                var la = ea.Cast<object?>().ToList();
                var lb = ((IEnumerable)b).Cast<object?>().ToList();
                Assert.True(la.Count == lb.Count, $"{path}: count {la.Count} vs {lb.Count}");
                for (var i = 0; i < la.Count; i++)
                    DeepEqual(la[i], lb[i], $"{path}[{i}]");
                return;
            }

            for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    DeepEqual(field.GetValue(a), field.GetValue(b), $"{path}.{field.Name}");
            }
        }

        private static void WithCulture(string culture, Action action)
        {
            var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture =
                    culture == string.Empty ? CultureInfo.InvariantCulture : new CultureInfo(culture);
                action();
            }
            finally
            {
                (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = saved;
            }
        }

        private static string CreateTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "dalamud_cfgstream_" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            return dir;
        }

        public class RichConfig : IPluginConfiguration
        {
            public string Text { get; set; } = "default";

            public long Big;
            public ulong UBig;
            public decimal Money;
            public double D;
            public float F;
            public double Nan = double.NaN;
            public double PosInf = double.PositiveInfinity;
            public DateTime When;
            public DateTime WhenLocal;
            public DateTime WhenUnspecified;
            public DateTimeOffset WhenOffset;
            public TimeSpan Span;
            public Guid Id;
            public Mode Mode;
            [JsonConverter(typeof(StringEnumConverter))]
            public Mode ModeAsString;
            public int? MaybeNull;
            public int? MaybeSet;
            public byte[]? Blob;
            public Vector4 Color;
            public Vector2 Pos;
            public int[]? Arr;
            public uint[]? Uints;
            public char Ch;
            public sbyte Sb;
            public List<Shape> Shapes = [];
            public Dictionary<string, Inner> Map = [];
            public HashSet<string> Tags = [];
            public Inner? NullInner;
            public object? Untyped;
            public SeString? SeText;

            [JsonIgnore]
            public string Ignored = "ignored";

            [JsonProperty("renamed")]
            public int RenamedField;

            private int userValue;

            public int Version { get; set; } = 3;

            // The IPC override shape: the runtime value is ignored, the user's value is written under its name.
            [JsonIgnore]
            public int Effective { get; set; }

            [JsonProperty(nameof(Effective))]
            private int EffectiveSurrogate
            {
                get => this.userValue;
                set
                {
                    this.userValue = value;
                    this.Effective = value + 1000;
                }
            }
        }

        public class Inner
        {
            public string? Name;
            public List<int> Values = [];
            public Dictionary<Mode, double> ByMode = [];
        }

        public class Shape
        {
            public int Id { get; set; }
        }

        public class Circle : Shape
        {
            public float R { get; set; }
        }

        public class Square : Shape
        {
            public double Side { get; set; }
        }

        public class ThrowingConfig : IPluginConfiguration
        {
            public int Version { get; set; }

            public string Boom => throw new InvalidOperationException("getter failed");
        }

        public class LoopConfig : IPluginConfiguration
        {
            public int Version { get; set; }

            public LoopConfig? Self { get; set; }
        }
    }
}
