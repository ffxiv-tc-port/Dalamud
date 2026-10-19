using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Threading;

using Dalamud.Logging.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace Dalamud.Plugin.Internal.Loader;

/// <summary>
/// Removes one plugin's types from the static reflection caches that every plugin shares through Dalamud's
/// Newtonsoft.Json, System.ComponentModel.TypeDescriptor and System.Text.Json, so that its collectible load context can be collected.
/// </summary>
/// <remarks>
/// Only entries whose key (or, for caches of types, value) involves the context's own assemblies are removed; System.Text.Json
/// has no such way in, so its hot reload handler empties its caches for every plugin. Private members of Newtonsoft.Json 13.0.3
/// and .NET 9 are used; one that is not found is skipped after one warning. Never throws, and may run on any thread while
/// other plugins serialize: concurrent dictionaries are changed only through their own removal or clearing, the TypeDescriptor
/// hashtables only under the lock all their writers take. That locking was read from .NET 9's System.ComponentModel; on any
/// other major version those hashtables are left alone (one warning).
/// </remarks>
internal static class SharedTypeCacheRelease
{
    /// <summary>Count reported for a cache that is not released at all (not found, or failed once).</summary>
    public const int Unavailable = -1;

    /// <summary>Count reported for a cache whose lock could not be taken in time; it is tried again on the next unload.</summary>
    public const int Busy = -2;

    /// <summary>Count reported for a TypeDescriptor hashtable left alone because this runtime's locking was not verified.</summary>
    public const int Unverified = -3;

    /// <summary>Count reported for a cache that cannot remove one plugin's entries and was emptied for every plugin instead.</summary>
    public const int ClearedAll = -4;

    /// <summary>The System.ComponentModel major version whose hashtable writers were verified to hold its common lock.</summary>
    public const int VerifiedTypeDescriptorMajor = 9;

    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    // Bounds how long an unload waits behind TypeDescriptor or contract resolver work on other threads.
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(1);

    private static readonly ModuleLog Log = new("PLUGINALC");
    private static readonly Lazy<Cache[]> Caches = new(CreateCaches, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<int> TypeDescriptorMajor = new(() => typeof(TypeDescriptor).Assembly.GetName().Version?.Major ?? 0);

    private static int failureWarned;
    private static int unverifiedWarned;

    /// <summary>Removes the context's types from the shared caches and logs one line with the counts.</summary>
    /// <param name="context">The plugin's load context, about to be unloaded, with its assemblies still loaded.</param>
    /// <returns>Entries removed per cache, or <see cref="Unavailable"/> / <see cref="Busy"/> / <see cref="Unverified"/> / <see cref="ClearedAll"/>.</returns>
    public static IReadOnlyList<KeyValuePair<string, int>> Release(AssemblyLoadContext context) => ReleaseCore(context, null);

    /// <summary>As <see cref="Release"/>, acting as if System.ComponentModel had the given major version (tests only).</summary>
    /// <param name="context">The plugin's load context, about to be unloaded, with its assemblies still loaded.</param>
    /// <param name="typeDescriptorMajor">The major version to act as.</param>
    /// <returns>Entries removed per cache, as <see cref="Release"/>.</returns>
    internal static IReadOnlyList<KeyValuePair<string, int>> ReleaseAs(AssemblyLoadContext context, int typeDescriptorMajor) =>
        ReleaseCore(context, typeDescriptorMajor);

    private static IReadOnlyList<KeyValuePair<string, int>> ReleaseCore(AssemblyLoadContext context, int? typeDescriptorMajor)
    {
        var counts = new List<KeyValuePair<string, int>>();
        var name = context.Name ?? "?";
        try
        {
            var start = Stopwatch.GetTimestamp();
            var owner = new Owner(context);
            var major = typeDescriptorMajor ?? TypeDescriptorMajor.Value;
            var lockingVerified = major == VerifiedTypeDescriptorMajor;
            foreach (var cache in Caches.Value)
                counts.Add(new(cache.Name, cache.Release(owner, lockingVerified)));

            if (!lockingVerified && Interlocked.Exchange(ref unverifiedWarned, 1) == 0)
            {
                Log.Warning(
                    "TypeDescriptor caches {Caches} are not released on System.ComponentModel {Major}: their locking was verified on {Verified} only; unloaded plugins that used TypeDescriptor (also through Newtonsoft.Json) stay in memory",
                    string.Join(", ", counts.Where(c => c.Value == Unverified).Select(c => c.Key)),
                    major,
                    VerifiedTypeDescriptorMajor);
            }

            Log.Information(
                "{Context}: released {Total} shared reflection cache entries in {Elapsed:0.0} ms ({Detail})",
                name,
                counts.Sum(c => Math.Max(c.Value, 0)),
                Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                Describe(counts));
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref failureWarned, 1) == 0)
                Log.Warning(ex, "Could not release shared reflection caches for {Context}; unloaded plugins may stay in memory", name);
        }

        return counts;
    }

    private static string Describe(List<KeyValuePair<string, int>> counts)
    {
        var parts = counts.Where(c => c.Value != 0)
                          .Select(c => c.Value switch
                          {
                              Unavailable => c.Key + "=off",
                              Busy => c.Key + "=busy",
                              Unverified => c.Key + "=skip",
                              ClearedAll => c.Key + "=all",
                              _ => c.Key + "=" + c.Value,
                          })
                          .ToList();
        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }

    private static Cache[] CreateCaches()
    {
        var caches = new List<Cache>();
        var missing = new List<string>();
        AddNewtonsoft(caches, missing);
        AddTypeDescriptor(caches, missing);
        AddSystemTextJson(caches, missing);
        if (missing.Count > 0)
        {
            Log.Warning(
                "Shared reflection caches not released, their fields differ in this Newtonsoft.Json/.NET version: {Missing}; unloaded plugins that used them may stay in memory",
                string.Join(", ", missing));
        }

        return caches.Concat(missing.Select(name => new MissingCache(name))).ToArray();
    }

    private static void AddNewtonsoft(List<Cache> caches, List<string> missing)
    {
        var json = typeof(JsonConvert).Assembly;

        // ThreadSafeStore<TKey, TValue> keeps its entries in a ConcurrentDictionary and only ever calls GetOrAdd on it.
        void AddStore(string name, Type? declaring, string field, bool values, FieldInfo? instance = null)
        {
            var store = declaring?.GetField(field, instance == null ? StaticFlags : InstanceFlags);
            var inner = store?.FieldType.GetField("_concurrentStore", InstanceFlags);
            if (store == null || inner == null || !IsConcurrent(inner.FieldType) || (instance != null && !instance.IsStatic))
            {
                missing.Add(name);
                return;
            }

            caches.Add(new DictionaryCache(name, values, () =>
            {
                var target = instance?.GetValue(null);
                if (instance != null && target == null)
                    return null;
                return store.GetValue(target) is { } s ? inner.GetValue(s) as IDictionary : null;
            }));
        }

        AddStore("nj.contracts", typeof(DefaultContractResolver), "_contractCache", false, typeof(DefaultContractResolver).GetField("_instance", StaticFlags));

        var getter = json.GetType("Newtonsoft.Json.Serialization.CachedAttributeGetter`1");
        foreach (var attribute in new[]
                 {
                     typeof(DataContractAttribute), typeof(DataMemberAttribute), typeof(JsonArrayAttribute),
                     typeof(JsonContainerAttribute), typeof(JsonConverterAttribute), typeof(JsonObjectAttribute),
                 })
        {
            AddStore("nj.attr." + attribute.Name.Replace("Attribute", string.Empty), getter?.MakeGenericType(attribute), "TypeAttributeCache", false);
        }

        var reflector = json.GetType("Newtonsoft.Json.Serialization.JsonTypeReflector");
        AddStore("nj.creators", reflector, "CreatorCache", false);
        AddStore("nj.metadataTypes", reflector, "AssociatedMetadataTypesCache", true);
        AddStore("nj.castConverters", json.GetType("Newtonsoft.Json.Utilities.ConvertUtils"), "CastConverters", false);
        AddStore("nj.enums", json.GetType("Newtonsoft.Json.Utilities.EnumUtils"), "ValuesAndNamesPerEnum", false);
        AddStore("nj.keyValuePairs", typeof(KeyValuePairConverter), "ReflectionObjectPerType", false);
        AddStore("nj.binderTypes", typeof(DefaultSerializationBinder), "_typeCache", true, typeof(DefaultSerializationBinder).GetField("Instance", StaticFlags));

        // Shared by every CamelCasePropertyNamesContractResolver: a dictionary replaced (never changed) under a lock.
        var camel = typeof(CamelCasePropertyNamesContractResolver);
        var camelLock = camel.GetField("TypeContractCacheLock", StaticFlags)?.GetValue(null);
        var camelCache = camel.GetField("_contractCache", StaticFlags);
        var copy = camelCache is { FieldType.IsGenericType: true, IsInitOnly: false }
                       ? camelCache.FieldType.GetConstructor([typeof(IDictionary<,>).MakeGenericType(camelCache.FieldType.GetGenericArguments())])
                       : null;
        if (camelLock == null || camelCache == null || copy == null || !typeof(IDictionary).IsAssignableFrom(camelCache.FieldType))
            missing.Add("nj.camelContracts");
        else
            caches.Add(new CopyOnWriteCache("nj.camelContracts", camelLock, camelCache, copy));
    }

    private static void AddTypeDescriptor(List<Cache> caches, List<string> missing)
    {
        var descriptor = typeof(TypeDescriptor);
        var reflect = descriptor.Assembly.GetType("System.ComponentModel.ReflectTypeDescriptionProvider");

        FieldInfo? Dictionary(Type? declaring, string field, BindingFlags flags, bool concurrent)
        {
            var info = declaring?.GetField(field, flags);
            return info != null && (concurrent ? IsConcurrent(info.FieldType) : typeof(IDictionary).IsAssignableFrom(info.FieldType)) ? info : null;
        }

        // Changed here without TypeDescriptor's lock, which is safe only while they are ConcurrentDictionary.
        // TypeDescriptor itself clears the provider type table whenever a provider is added.
        foreach (var (name, field) in new[] { ("td.defaultProviders", "s_defaultProviderInitialized"), ("td.providerTypes", "s_providerTypeTable") })
        {
            if (Dictionary(descriptor, field, StaticFlags, true) is { } info)
                caches.Add(new DictionaryCache(name, false, () => info.GetValue(null) as IDictionary));
            else
                missing.Add(name);
        }

        var sync = descriptor.GetField("s_commonSyncObject", StaticFlags)?.GetValue(null);
        var providerTable = Dictionary(descriptor, "s_providerTable", StaticFlags, false);
        var node = descriptor.GetNestedType("TypeDescriptionNode", BindingFlags.NonPublic);
        var next = node?.GetField("Next", InstanceFlags);
        var provider = node?.GetField("Provider", InstanceFlags);
        var typeData = Dictionary(reflect, "_typeData", InstanceFlags, true);

        if (sync != null && providerTable != null)
            caches.Add(new LockedTableCache("td.providers", sync, providerTable));
        else
            missing.Add("td.providers");

        if (sync != null && providerTable != null && node != null && next != null && provider != null && typeData != null)
            caches.Add(new TypeDataCache("td.typeData", sync, providerTable, node, next, provider, reflect!, typeData));
        else
            missing.Add("td.typeData");

        foreach (var (name, field) in new[]
                 {
                     ("td.attributes", "s_attributeCache"), ("td.properties", "s_propertyCache"),
                     ("td.events", "s_eventCache"), ("td.extendedProperties", "s_extendedPropertyCache"),
                 })
        {
            if (sync != null && Dictionary(reflect, field, StaticFlags, false) is { } info)
                caches.Add(new LockedTableCache(name, sync, info));
            else
                missing.Add(name);
        }
    }

    // Metadata cached in every JsonSerializerOptions (the static Default too) and in one shared member accessor: only the
    // hot reload handler reaches them all. It clears ConcurrentDictionaries and nulls fields that each reader reads only once.
    private static void AddSystemTextJson(List<Cache> caches, List<string> missing)
    {
        var handler = typeof(System.Text.Json.JsonSerializer).Assembly.GetType("System.Text.Json.JsonSerializerOptionsUpdateHandler");
        var clear = handler?.GetMethod("ClearCache", StaticFlags, [typeof(Type[])]);
        if (clear != null && clear.ReturnType == typeof(void))
            caches.Add(new ClearAllCache("stj", clear));
        else
            missing.Add("stj");
    }

    private static bool IsConcurrent(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConcurrentDictionary<,>);

    private static int RemoveOwned(IDictionary? dictionary, Owner owner, bool values)
    {
        if (dictionary == null)
            return 0;

        var owned = CollectOwned(dictionary, owner, values);
        if (owned == null)
            return 0;

        foreach (var key in owned)
            dictionary.Remove(key);
        return owned.Count;
    }

    private static List<object>? CollectOwned(IDictionary dictionary, Owner owner, bool values)
    {
        List<object>? owned = null;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (owner.Owns(entry.Key) || (values && owner.Owns(entry.Value)))
                (owned ??= []).Add(entry.Key);
        }

        return owned;
    }

    /// <summary>Decides whether a cache key or value belongs to the plugin; used by one releasing thread only.</summary>
    private sealed class Owner(AssemblyLoadContext context)
    {
        private readonly HashSet<Assembly> assemblies = new(context.Assemblies, ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Type, bool> types = [];
        private readonly Dictionary<Type, FieldInfo[]> structFields = [];

        public bool Owns(object? value, int depth = 0) => value switch
        {
            null or string => false,
            Type type => this.OwnsType(type),
            MemberInfo member => this.OwnsMember(member),
            ParameterInfo parameter => parameter.Member is { } member && this.OwnsMember(member),
            Assembly assembly => this.assemblies.Contains(assembly),
            Module module => this.assemblies.Contains(module.Assembly),
            _ => this.OwnsType(value.GetType()) || (depth < 2 && this.StructOwns(value, depth)),
        };

        private bool OwnsMember(MemberInfo member) =>
            (member.DeclaringType is { } declaring && this.OwnsType(declaring)) ||
            (member.ReflectedType is { } reflected && this.OwnsType(reflected)) ||
            this.assemblies.Contains(member.Module.Assembly);

        // A type built from a plugin type (List<T>, T[]) lives in the plugin's context but reports another assembly.
        private bool OwnsType(Type type)
        {
            if (!type.IsCollectible)
                return false;
            if (this.types.TryGetValue(type, out var owned))
                return owned;

            owned = this.assemblies.Contains(type.Assembly) ||
                    (type.HasElementType && type.GetElementType() is { } element && this.OwnsType(element)) ||
                    (type.IsConstructedGenericType && type.GenericTypeArguments.Any(this.OwnsType));
            this.types[type] = owned;
            return owned;
        }

        private bool StructOwns(object value, int depth)
        {
            var type = value.GetType();
            if (!type.IsValueType || type.IsPrimitive || type.IsEnum)
                return false;
            if (!this.structFields.TryGetValue(type, out var fields))
                this.structFields[type] = fields = type.GetFields(InstanceFlags);
            foreach (var field in fields)
            {
                if (this.Owns(field.GetValue(value), depth + 1))
                    return true;
            }

            return false;
        }
    }

    private abstract class Cache(string name)
    {
        private int broken;

        public string Name => name;

        // Whether removing entries is only safe because of locking verified on one System.ComponentModel version.
        protected virtual bool NeedsVerifiedLocking => false;

        public int Release(Owner owner, bool lockingVerified)
        {
            if (Volatile.Read(ref this.broken) != 0)
                return Unavailable;
            if (this.NeedsVerifiedLocking && !lockingVerified)
                return Unverified;
            try
            {
                return this.ReleaseCore(owner);
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref this.broken, 1) == 0)
                    Log.Warning(ex, "Shared reflection cache {Cache} does not have the expected shape and is no longer released", name);
                return Unavailable;
            }
        }

        protected abstract int ReleaseCore(Owner owner);
    }

    /// <summary>A cache whose fields were not found; listed so that every unload line shows it as off.</summary>
    private sealed class MissingCache(string name) : Cache(name)
    {
        protected override int ReleaseCore(Owner owner) => Unavailable;
    }

    /// <summary>A ConcurrentDictionary: enumerating and removing are safe beside any reader or writer.</summary>
    private sealed class DictionaryCache(string name, bool values, Func<IDictionary?> get) : Cache(name)
    {
        protected override int ReleaseCore(Owner owner) => RemoveOwned(get(), owner, values);
    }

    /// <summary>A Hashtable that its owner reads without a lock and writes only under <paramref name="sync"/> (one writer at a time).</summary>
    private sealed class LockedTableCache(string name, object sync, FieldInfo field) : Cache(name)
    {
        protected override bool NeedsVerifiedLocking => true;

        protected override int ReleaseCore(Owner owner)
        {
            if (!Monitor.TryEnter(sync, LockTimeout))
                return Busy;
            try
            {
                return RemoveOwned(field.GetValue(null) as IDictionary, owner, false);
            }
            finally
            {
                Monitor.Exit(sync);
            }
        }
    }

    /// <summary>
    /// The per-provider type data of every ReflectTypeDescriptionProvider registered in TypeDescriptor's provider table
    /// (a ConcurrentDictionary each); the table itself is walked under its lock.
    /// </summary>
    private sealed class TypeDataCache(
        string name, object sync, FieldInfo table, Type node, FieldInfo next, FieldInfo provider, Type reflect, FieldInfo typeData)
        : Cache(name)
    {
        protected override int ReleaseCore(Owner owner)
        {
            var providers = new List<object>();
            if (!Monitor.TryEnter(sync, LockTimeout))
                return Busy;
            try
            {
                if (table.GetValue(null) is IDictionary entries)
                {
                    foreach (DictionaryEntry entry in entries)
                    {
                        for (var current = entry.Value; current != null && node.IsInstanceOfType(current); current = next.GetValue(current))
                        {
                            if (provider.GetValue(current) is { } p && reflect.IsInstanceOfType(p) && !providers.Any(x => ReferenceEquals(x, p)))
                                providers.Add(p);
                        }
                    }
                }
            }
            finally
            {
                Monitor.Exit(sync);
            }

            return providers.Sum(p => RemoveOwned(typeData.GetValue(p) as IDictionary, owner, false));
        }
    }

    /// <summary>A cache with no per-type removal, emptied for every plugin through a static clear(Type[]) method.</summary>
    private sealed class ClearAllCache(string name, MethodInfo clear) : Cache(name)
    {
        // null means "any type may have changed": a runtime that filters by the given types still drops List<PluginType> and the like.
        protected override int ReleaseCore(Owner owner)
        {
            clear.Invoke(null, [null]);
            return ClearedAll;
        }
    }

    /// <summary>A dictionary that readers use without a lock and writers replace with a changed copy under <paramref name="sync"/>.</summary>
    private sealed class CopyOnWriteCache(string name, object sync, FieldInfo field, ConstructorInfo copy) : Cache(name)
    {
        protected override int ReleaseCore(Owner owner)
        {
            if (!Monitor.TryEnter(sync, LockTimeout))
                return Busy;
            try
            {
                if (field.GetValue(null) is not IDictionary current || CollectOwned(current, owner, false) is not { } owned)
                    return 0;

                var replacement = (IDictionary)copy.Invoke([current]);
                foreach (var key in owned)
                    replacement.Remove(key);
                field.SetValue(null, replacement);
                return owned.Count;
            }
            finally
            {
                Monitor.Exit(sync);
            }
        }
    }
}
