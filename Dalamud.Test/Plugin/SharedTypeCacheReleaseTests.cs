#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Threading;

using Dalamud.Plugin.Internal.Loader;
using Dalamud.Plugin.Internal.Types;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

using Xunit;

namespace Dalamud.Test.Plugin
{
    // Loads a copy of this assembly as a plugin, with Newtonsoft.Json shared like a real plugin that does not ship its own.
    public class SharedTypeCacheReleaseTests
    {
        private static readonly string[] CachesThePluginSideFills =
        [
            "nj.contracts", "nj.camelContracts", "nj.attr.DataContract", "nj.attr.DataMember", "nj.attr.JsonContainer",
            "nj.attr.JsonConverter", "nj.attr.JsonObject", "nj.creators", "nj.metadataTypes", "nj.castConverters", "nj.enums",
            "nj.keyValuePairs", "td.defaultProviders", "td.typeData", "td.attributes", "td.properties", "td.events",
        ];

        // Released only on the System.ComponentModel version whose locking was verified.
        private static readonly string[] LockedTables = ["td.providers", "td.attributes", "td.properties", "td.events", "td.extendedProperties"];

        [Fact]
        public void SharedCachesKeepContextAliveUnlessReleased()
        {
            // Control: proves that this test can tell a pinned context from a collected one.
            var pinned = LoadAndDispose(null, UseSharedCaches);
            Assert.True(IsStillAlive(pinned), "Newtonsoft/TypeDescriptor no longer pin the context by themselves; revisit this test.");

            IReadOnlyList<KeyValuePair<string, int>>? counts = null;
            var released = LoadAndDispose(context => counts = SharedTypeCacheRelease.Release(context), UseSharedCaches);
            Assert.False(IsStillAlive(released), "Shared reflection caches kept the unloaded plugin context alive: " + Describe(counts));
        }

        [Fact]
        public void EveryCacheIsFoundAndTheExpectedOnesAreReleased()
        {
            IReadOnlyList<KeyValuePair<string, int>>? counts = null;
            LoadAndDispose(context => counts = SharedTypeCacheRelease.Release(context), UseSharedCaches);

            Assert.NotNull(counts);
            Assert.DoesNotContain(counts!, c => c.Value == SharedTypeCacheRelease.Unavailable);
            foreach (var name in CachesThePluginSideFills)
                Assert.True(counts!.Any(c => c.Key == name && c.Value > 0), name + " released nothing: " + Describe(counts));
            Assert.Contains(counts!, c => c.Key == "stj" && c.Value == SharedTypeCacheRelease.ClearedAll);
        }

        [Fact]
        public void SystemTextJsonKeepsContextAliveUnlessReleased()
        {
            // Control: the plugin side only uses System.Text.Json, so nothing else can be what pins it.
            var pinned = LoadAndDispose(null, UseSystemTextJson);
            Assert.True(IsStillAlive(pinned), "System.Text.Json no longer pins the context by itself; revisit this test.");

            IReadOnlyList<KeyValuePair<string, int>>? counts = null;
            var released = LoadAndDispose(context => counts = SharedTypeCacheRelease.Release(context), UseSystemTextJson);
            Assert.False(IsStillAlive(released), "System.Text.Json kept the unloaded plugin context alive: " + Describe(counts));
            Assert.False(IsStillAlive(pinned), "Clearing System.Text.Json for one plugin did not free the one unloaded before it.");
        }

        [Fact]
        public void OnAnotherRuntimeTheLockedTablesAreLeftAloneAndTheRestReleased()
        {
            List<KeyValuePair<string, int>>? asOther = null, asVerified = null;
            int attributesAfterOther = -1, contractsAfterOther = -1, attributesAfterVerified = -1;
            var weak = LoadAndDispose(
                context =>
                {
                    asOther = SharedTypeCacheRelease.ReleaseAs(context, SharedTypeCacheRelease.VerifiedTypeDescriptorMajor + 1).ToList();
                    attributesAfterOther = CountOwned(AttributeCache(), context, true);
                    contractsAfterOther = CountOwned(ContractCache(), context, false);
                    asVerified = SharedTypeCacheRelease.ReleaseAs(context, SharedTypeCacheRelease.VerifiedTypeDescriptorMajor).ToList();
                    attributesAfterVerified = CountOwned(AttributeCache(), context, true);
                },
                UseSharedCaches);

            foreach (var name in LockedTables)
                Assert.True(asOther!.Any(c => c.Key == name && c.Value == SharedTypeCacheRelease.Unverified), name + " was not skipped: " + Describe(asOther));
            Assert.DoesNotContain(asOther!, c => !LockedTables.Contains(c.Key) && c.Value < 0 && c.Value != SharedTypeCacheRelease.ClearedAll);
            foreach (var name in new[] { "nj.contracts", "nj.camelContracts", "nj.enums", "td.defaultProviders", "td.typeData" })
                Assert.True(asOther!.Any(c => c.Key == name && c.Value > 0), name + " was not released: " + Describe(asOther));

            Assert.True(attributesAfterOther > 0, "The skipped TypeDescriptor attribute cache lost the plugin's entries anyway.");
            Assert.Equal(0, contractsAfterOther);
            Assert.Contains(asVerified!, c => c.Key == "td.attributes" && c.Value == attributesAfterOther);
            Assert.Equal(0, attributesAfterVerified);
            Assert.False(IsStillAlive(weak), "The context stayed alive after the verified release: " + Describe(asVerified));
        }

        [Fact]
        public void ReleaseKeepsEntriesOfOtherAssemblies()
        {
            Assert.Equal("{\"A\":1}", JsonConvert.SerializeObject(new HostProbe()));
            Assert.Equal(typeof(TypeConverter), TypeDescriptor.GetConverter(typeof(HostProbe)).GetType());

            IReadOnlyList<KeyValuePair<string, int>>? counts = null;
            LoadAndDispose(context => counts = SharedTypeCacheRelease.Release(context), UseSharedCaches);

            Assert.True(counts!.Sum(c => Math.Max(c.Value, 0)) > 0);
            Assert.True(ContractCache().Contains(typeof(HostProbe)), "The host type's contract was removed.");
            Assert.True(DefaultProviderTable().Contains(typeof(HostProbe)), "The host type's TypeDescriptor entry was removed.");
        }

        [Fact]
        public void ReleaseWhileOthersUseTheSameCachesStaysCorrect()
        {
            var (weak, errors, operations, rounds, removed) = ReleaseRepeatedlyWhileInUse(nameof(CacheProbePluginSide.UseSharedCaches), NewtonsoftHostIsCorrect);

            Assert.Empty(errors);
            Assert.True(operations > 0 && rounds > 0 && removed > 0, $"operations={operations} rounds={rounds} removed={removed}");
            Assert.False(IsStillAlive(weak), "The context stayed alive after the concurrent use stopped and it was released.");
        }

        [Fact]
        public void ReleaseWhileOthersUseSystemTextJsonStaysCorrect()
        {
            var (weak, errors, operations, rounds, _) = ReleaseRepeatedlyWhileInUse(nameof(CacheProbePluginSide.UseSystemTextJson), SystemTextJsonHostIsCorrect);

            Assert.Empty(errors);
            Assert.True(operations > 0 && rounds > 0, $"operations={operations} rounds={rounds}");
            Assert.False(IsStillAlive(weak), "The context stayed alive after the concurrent use stopped and it was released.");
        }

        [Fact]
        public void WatchReportsCollectedAndPinnedContexts()
        {
            // Its own watch: the shared one also holds what other tests (and their pinned contexts) tracked.
            var watch = new UnloadedContextWatch.Watch();
            var released = LoadAndDispose(
                context =>
                {
                    SharedTypeCacheRelease.Release(context);
                    watch.Track(context);
                },
                UseSharedCaches);
            var pinned = LoadAndDispose(watch.Track, UseSharedCaches);

            Assert.False(IsStillAlive(released));
            for (var i = 0; i < UnloadedContextWatch.MinimumFullCollections; i++)
                GC.Collect();
            Assert.True(pinned.IsAlive);

            var observations = watch.ReportWith(NoCensus).Where(o => o.Name == "Dalamud.Test").ToList();
            Assert.Single(observations, o => o.Collected);
            Assert.Single(observations, o => !o.Collected && o.FullCollections >= UnloadedContextWatch.MinimumFullCollections);

            // A context still alive is not reported again until another full collection (other tests may run one).
            var fullCollections = GC.CollectionCount(2);
            var again = watch.ReportWith(NoCensus);
            if (GC.CollectionCount(2) == fullCollections)
                Assert.DoesNotContain(again, o => o.Name == "Dalamud.Test");
        }

        // Not the Dalamud census: it waits for Dalamud's services, which only exist here once some other test made them.
        private static List<string> NoCensus(AssemblyLoadContext context) => [];

        private static string Describe(IReadOnlyList<KeyValuePair<string, int>>? counts) =>
            counts == null ? "<not called>" : string.Join(", ", counts.Select(c => c.Key + "=" + c.Value));

        // Separate frame: nothing that references the plugin's code may outlive it, or the context cannot be collected.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static (WeakReference Weak, List<string> Errors, long Operations, int Rounds, int Removed) ReleaseRepeatedlyWhileInUse(
            string pluginSideMethod, Func<bool> hostIsCorrect)
        {
            var loader = CreateLoader(null);
            var side = loader.LoadDefaultAssembly().GetType(typeof(CacheProbePluginSide).FullName!)!;
            var use = side.GetMethod(pluginSideMethod)!.CreateDelegate<Func<string>>();
            var expected = use();

            var stop = 0;
            var errors = new List<string>();
            long operations = 0;
            var workers = Enumerable.Range(0, 4).Select(i => new Thread(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    try
                    {
                        var plugin = use();
                        if (plugin != expected || !hostIsCorrect())
                            lock (errors) errors.Add("mismatch: " + plugin);
                        Interlocked.Increment(ref operations);
                    }
                    catch (Exception ex)
                    {
                        lock (errors) errors.Add(ex.ToString());
                    }
                }
            })).ToList();
            workers.ForEach(t => t.Start());

            var rounds = 0;
            var removed = 0;
            var until = DateTime.UtcNow.AddSeconds(1.5);
            while (DateTime.UtcNow < until)
            {
                removed += SharedTypeCacheRelease.Release(loader.LoadContext).Sum(c => Math.Max(c.Value, 0));
                rounds++;
            }

            Volatile.Write(ref stop, 1);
            workers.ForEach(t => t.Join());
            return (DisposeWithRelease(loader), errors, operations, rounds, removed);
        }

        private static void UseSharedCaches(Assembly pluginAssembly)
        {
            var side = pluginAssembly.GetType(typeof(CacheProbePluginSide).FullName!)!;
            Assert.True((bool)side.GetMethod(nameof(CacheProbePluginSide.TypesAreCollectible))!.Invoke(null, null)!);
            Assert.Equal(CacheProbePluginSide.UseSharedCaches(), (string)side.GetMethod(nameof(CacheProbePluginSide.UseSharedCaches))!.Invoke(null, null)!);
        }

        private static void UseSystemTextJson(Assembly pluginAssembly)
        {
            var side = pluginAssembly.GetType(typeof(CacheProbePluginSide).FullName!)!;
            Assert.True((bool)side.GetMethod(nameof(CacheProbePluginSide.TypesAreCollectible))!.Invoke(null, null)!);
            Assert.Equal(CacheProbePluginSide.UseSystemTextJson(), (string)side.GetMethod(nameof(CacheProbePluginSide.UseSystemTextJson))!.Invoke(null, null)!);
        }

        private static bool NewtonsoftHostIsCorrect()
        {
            var host = JsonConvert.SerializeObject(new HostProbe());
            return host == "{\"A\":1}" && JsonConvert.DeserializeObject<HostProbe>(host)!.A == 1 && TypeDescriptor.GetProperties(typeof(HostProbe)).Count == 1;
        }

        private static bool SystemTextJsonHostIsCorrect()
        {
            var host = System.Text.Json.JsonSerializer.Serialize(new HostProbe());
            return host == "{\"A\":1}" && System.Text.Json.JsonSerializer.Deserialize<HostProbe>(host)!.A == 1;
        }

        private static IDictionary ContractCache()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
            var resolver = typeof(DefaultContractResolver).GetField("_instance", flags)!.GetValue(null);
            var store = typeof(DefaultContractResolver).GetField("_contractCache", flags)!.GetValue(resolver)!;
            return (IDictionary)store.GetType().GetField("_concurrentStore", flags)!.GetValue(store)!;
        }

        private static IDictionary AttributeCache()
        {
            var reflect = typeof(TypeDescriptor).Assembly.GetType("System.ComponentModel.ReflectTypeDescriptionProvider")!;
            return (IDictionary)reflect.GetField("s_attributeCache", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        }

        // The attribute cache is a Hashtable written under TypeDescriptor's lock; enumerating it needs the same lock.
        private static int CountOwned(IDictionary cache, AssemblyLoadContext context, bool underTypeDescriptorLock)
        {
            var assemblies = context.Assemblies.ToHashSet();
            var sync = underTypeDescriptorLock
                           ? typeof(TypeDescriptor).GetField("s_commonSyncObject", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!
                           : new object();
            lock (sync)
                return cache.Keys.Cast<object>().Count(k => Owns(k, assemblies));
        }

        private static bool Owns(object key, HashSet<Assembly> assemblies) => key switch
        {
            Type t => assemblies.Contains(t.Assembly) ||
                      (t.HasElementType && Owns(t.GetElementType()!, assemblies)) ||
                      (t.IsConstructedGenericType && t.GenericTypeArguments.Any(a => Owns(a, assemblies))),
            MemberInfo m => (m.DeclaringType != null && Owns(m.DeclaringType, assemblies)) || (m.ReflectedType != null && Owns(m.ReflectedType, assemblies)),
            _ => false,
        };

        private static IDictionary DefaultProviderTable() =>
            (IDictionary)typeof(TypeDescriptor).GetField("s_defaultProviderInitialized", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static PluginLoader CreateLoader(Action<AssemblyLoadContext>? beforeUnload) =>
            PluginLoader.CreateFromAssemblyFile(
                typeof(SharedTypeCacheReleaseTests).Assembly.Location,
                config =>
                {
                    LocalPlugin.SetupLoaderConfig(config);
                    config.SharedAssemblies.Add((typeof(JsonConvert).Assembly.GetName(), false));
                    config.BeforeUnload = beforeUnload;
                });

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference LoadAndDispose(Action<AssemblyLoadContext>? beforeUnload, Action<Assembly> whileLoaded)
        {
            var loader = CreateLoader(beforeUnload);
            whileLoaded(loader.LoadDefaultAssembly());
            var weak = new WeakReference(loader.LoadContext, trackResurrection: true);
            loader.Dispose();
            return weak;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference DisposeWithRelease(PluginLoader loader)
        {
            SharedTypeCacheRelease.Release(loader.LoadContext);
            var weak = new WeakReference(loader.LoadContext, trackResurrection: true);
            loader.Dispose();
            return weak;
        }

        private static bool IsStillAlive(WeakReference weak)
        {
            for (var i = 0; weak.IsAlive && i < 30; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            return weak.IsAlive;
        }
    }

    public sealed class HostProbe
    {
        public int A { get; set; } = 1;
    }

    // Everything below runs inside the collectible copy of this assembly, through the shared Newtonsoft.Json.
    public static class CacheProbePluginSide
    {
        public static bool TypesAreCollectible() => typeof(CacheProbeConfig).IsCollectible;

        public static string UseSharedCaches()
        {
            var plain = JsonConvert.SerializeObject(new CacheProbeConfig());
            var camel = new JsonSerializerSettings { ContractResolver = new CamelCasePropertyNamesContractResolver(), Converters = { new StringEnumConverter() } };
            var camelText = JsonConvert.SerializeObject(new CacheProbeConfig(), camel);
            return string.Join(
                " ",
                JsonConvert.DeserializeObject<CacheProbeConfig>(plain)!.Converted.S,
                JsonConvert.DeserializeObject<CacheProbeConfig>(camelText, camel)!.Kind,
                JsonConvert.SerializeObject(new CacheProbeContract()),
                JsonConvert.DeserializeObject<CacheProbeId>("5").Value,
                TypeDescriptor.GetConverter(typeof(CacheProbeConfig)).GetType().Name,
                TypeDescriptor.GetProperties(typeof(CacheProbeConfig)).Count,
                TypeDescriptor.GetEvents(typeof(CacheProbeConfig)).Count);
        }

        // Only System.Text.Json: the default options, and options holding one of the plugin's own converters.
        public static string UseSystemTextJson()
        {
            var options = new System.Text.Json.JsonSerializerOptions
            {
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(), new CacheProbeStjConverter() },
            };
            var text = System.Text.Json.JsonSerializer.Serialize(new CacheProbeConfig(), options);
            var back = System.Text.Json.JsonSerializer.Deserialize<CacheProbeConfig>(text, options)!;
            var plain = System.Text.Json.JsonSerializer.Deserialize<CacheProbeItem>("{\"S\":\"plain\"}")!;
            return string.Join(" ", text, back.Kind, back.Converted.S, back.Pair.Value, plain.S);
        }
    }

    public enum CacheProbeKind
    {
        A,
        B,
    }

    public sealed class CacheProbeConfig
    {
        public int X { get; set; } = 1;

        public CacheProbeKind Kind { get; set; } = CacheProbeKind.B;

        public List<CacheProbeItem> Items { get; set; } = [new()];

        public Dictionary<string, CacheProbeKind> Map { get; set; } = new() { ["a"] = CacheProbeKind.A };

        public KeyValuePair<string, CacheProbeKind> Pair { get; set; } = new("p", CacheProbeKind.B);

        [JsonConverter(typeof(CacheProbeConverter))]
        public CacheProbeItem Converted { get; set; } = new() { S = "converted" };
    }

    public sealed class CacheProbeItem
    {
        public string S { get; set; } = "i";
    }

    [DataContract]
    public sealed class CacheProbeContract
    {
        [DataMember]
        public int Y { get; set; } = 2;
    }

    public readonly struct CacheProbeId(long value)
    {
        public long Value { get; } = value;

        public static implicit operator CacheProbeId(long value) => new(value);
    }

    public sealed class CacheProbeConverter : JsonConverter<CacheProbeItem>
    {
        public override void WriteJson(JsonWriter writer, CacheProbeItem? value, JsonSerializer serializer) => writer.WriteValue(value?.S);

        public override CacheProbeItem ReadJson(JsonReader reader, Type objectType, CacheProbeItem? existingValue, bool hasExistingValue, JsonSerializer serializer) =>
            new() { S = (string)reader.Value! };
    }

    public sealed class CacheProbeStjConverter : System.Text.Json.Serialization.JsonConverter<CacheProbeItem>
    {
        public override CacheProbeItem Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options) =>
            new() { S = reader.GetString()! };

        public override void Write(System.Text.Json.Utf8JsonWriter writer, CacheProbeItem value, System.Text.Json.JsonSerializerOptions options) =>
            writer.WriteStringValue(value.S);
    }
}
