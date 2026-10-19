using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Owners of compiler-generated nested types (closures, iterators, async state machines) by type id, so that a
/// sampled "&lt;&gt;c__DisplayClass8_0" can say which assembly and type it belongs to.
/// </summary>
/// <remarks>
/// <see cref="TryGetOwner"/> and <see cref="Request"/> on any thread, without locking. Only requested ids are looked
/// up, in the background: loading every such type up front costs tens of MB. A type id is only ever compared as an
/// integer, and the ids of a load context are dropped once it starts to unload, before its types may be freed and
/// their addresses reused. Generic ones are never found: samples name instantiations, which exist only at run time.
/// </remarks>
internal sealed class GeneratedTypeOwners : IDisposable
{
    /// <summary>Most ids requested and not found before requests are ignored.</summary>
    public const int MaxRequested = 4096;

    // Failing type loads throw, which is slow; an assembly whose dependencies are missing is given up on.
    private const int MaxFailuresPerAssembly = 16;

    private static readonly ConditionalWeakTable<AssemblyLoadContext, object> UnloadingContexts = new();
    private static readonly object Marker = new();

    private readonly ConcurrentDictionary<nint, string> owners = new();
    private readonly ConcurrentDictionary<nint, string> requested = new();
    private readonly ConcurrentQueue<(nint Id, string Name)> pending = new();
    private readonly ConditionalWeakTable<AssemblyLoadContext, List<KeyValuePair<nint, string>>> byContext = new();
    private readonly object gate = new();

    private int draining;
    private int requestedCount;
    private int lookUps;
    private int typesLoaded;
    private int failures;
    private long lookUpTicks;
    private volatile bool disposed;

    private GeneratedTypeOwners()
    {
    }

    private static event Action<AssemblyLoadContext>? ContextUnloading;

    /// <summary>
    /// Gets how many types have an owner now.
    /// </summary>
    public int Count => this.owners.Count;

    /// <summary>
    /// Gets what the look-ups so far cost.
    /// </summary>
    public Cost Spent => new(
        Volatile.Read(ref this.lookUps),
        Volatile.Read(ref this.typesLoaded),
        Volatile.Read(ref this.failures),
        Stopwatch.GetElapsedTime(0, Interlocked.Read(ref this.lookUpTicks)));

    /// <summary>
    /// Starts following load contexts that unload. Reads nothing until something is requested.
    /// </summary>
    /// <returns>The owners.</returns>
    public static GeneratedTypeOwners Start()
    {
        var owners = new GeneratedTypeOwners();
        ContextUnloading += owners.Forget;
        return owners;
    }

    /// <summary>
    /// Drops the owners of a load context's types; call it right before the context is unloaded. Never throws.
    /// </summary>
    /// <param name="context">The context.</param>
    public static void MarkUnloading(AssemblyLoadContext context)
    {
        try
        {
            UnloadingContexts.AddOrUpdate(context, Marker);
            ContextUnloading?.Invoke(context);
        }
        catch (Exception)
        {
            // Diagnostics must never break unloading.
        }
    }

    /// <summary>
    /// Gets the owner of a compiler-generated type.
    /// </summary>
    /// <param name="typeId">The type id, as <see cref="RuntimeTypeHandle.Value"/>.</param>
    /// <param name="owner">"Assembly:Namespace.Type+Nested"; one string per id while it stays known.</param>
    /// <returns>Whether the owner is known.</returns>
    public bool TryGetOwner(nint typeId, out string owner) => this.owners.TryGetValue(typeId, out owner!);

    /// <summary>
    /// Asks for the owner of a type to be looked up in the background. The same id is looked up once, until some
    /// load context unloads.
    /// </summary>
    /// <param name="typeName">The short type name the runtime gave.</param>
    /// <param name="typeId">The type id.</param>
    public void Request(string typeName, nint typeId)
    {
        // "<>c" and "<>O" hold cached instances, created once per class: never worth a look-up.
        if (this.disposed || typeId == 0 || typeName is "<>c" or "<>O" || typeName.Contains('`')
            || this.requested.ContainsKey(typeId) || Volatile.Read(ref this.requestedCount) >= MaxRequested
            || !this.requested.TryAdd(typeId, typeName))
        {
            return;
        }

        Interlocked.Increment(ref this.requestedCount);

        this.pending.Enqueue((typeId, typeName));
        if (Interlocked.CompareExchange(ref this.draining, 1, 0) == 0)
            Task.Run(this.Drain);
    }

    /// <summary>
    /// Looks up what is pending now, on this thread. For tests.
    /// </summary>
    public void DrainNow()
    {
        lock (this.pending)
            this.LookUpPending();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.disposed = true;
        ContextUnloading -= this.Forget;
        this.pending.Clear();
        this.requested.Clear();
        lock (this.gate)
            this.owners.Clear();
    }

    private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
            return FullName(reader, declaring) + "+" + name;

        var space = reader.GetString(definition.Namespace);
        return space.Length == 0 ? name : space + "." + name;
    }

    private void Drain()
    {
        while (true)
        {
            lock (this.pending)
                this.LookUpPending();

            Volatile.Write(ref this.draining, 0);
            if (this.disposed || this.pending.IsEmpty || Interlocked.CompareExchange(ref this.draining, 1, 0) != 0)
                return;
        }
    }

    private void LookUpPending()
    {
        var wanted = new Dictionary<string, HashSet<nint>>(StringComparer.Ordinal);
        var left = 0;
        while (this.pending.TryDequeue(out var request))
        {
            if (this.owners.ContainsKey(request.Id))
                continue;

            if (!wanted.TryGetValue(request.Name, out var ids))
                wanted[request.Name] = ids = [];
            if (ids.Add(request.Id))
                left++;
        }

        if (left == 0 || this.disposed)
            return;

        var start = Stopwatch.GetTimestamp();
        Interlocked.Increment(ref this.lookUps);
        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (left == 0 || this.disposed)
                    break;

                if (assembly.IsDynamic)
                    continue;

                var context = AssemblyLoadContext.GetLoadContext(assembly);
                if (context is not null && UnloadingContexts.TryGetValue(context, out _))
                    continue;

                try
                {
                    left -= this.LookUpIn(assembly, context, wanted);
                }
                catch (Exception)
                {
                    Interlocked.Increment(ref this.failures);
                }
            }
        }
        finally
        {
            Interlocked.Add(ref this.lookUpTicks, Stopwatch.GetTimestamp() - start);
        }
    }

    private unsafe int LookUpIn(Assembly assembly, AssemblyLoadContext? context, Dictionary<string, HashSet<nint>> wanted)
    {
        if (!assembly.TryGetRawMetadata(out var blob, out var length))
            return 0;

        var reader = new MetadataReader(blob, length);
        List<KeyValuePair<nint, string>>? found = null;
        string? assemblyName = null;
        var failed = 0;
        foreach (var handle in reader.TypeDefinitions)
        {
            var definition = reader.GetTypeDefinition(handle);
            if (!definition.IsNested || !reader.StringComparer.StartsWith(definition.Name, "<"))
                continue;

            HashSet<nint>? ids = null;
            foreach (var (name, candidates) in wanted)
            {
                if (candidates.Count > 0 && reader.StringComparer.Equals(definition.Name, name))
                {
                    ids = candidates;
                    break;
                }
            }

            if (ids is null || definition.GetGenericParameters().Count > 0)
                continue;

            nint id;
            try
            {
                Interlocked.Increment(ref this.typesLoaded);
                id = assembly.ManifestModule.ResolveType(MetadataTokens.GetToken(handle)).TypeHandle.Value;
            }
            catch (Exception)
            {
                Interlocked.Increment(ref this.failures);
                if (++failed >= MaxFailuresPerAssembly)
                    break;
                continue;
            }

            if (!ids.Remove(id))
                continue;

            assemblyName ??= assembly.GetName().Name ?? "?";
            (found ??= []).Add(new(id, assemblyName + ":" + FullName(reader, definition.GetDeclaringType())));
        }

        GC.KeepAlive(assembly);
        if (found is null)
            return 0;

        this.Add(context, found);
        return found.Count;
    }

    // Shares the lock with Forget, so ids of a context that started to unload while it was read are never added.
    private void Add(AssemblyLoadContext? context, List<KeyValuePair<nint, string>> found)
    {
        var subscribe = false;
        lock (this.gate)
        {
            if (this.disposed)
                return;

            List<KeyValuePair<nint, string>>? ofContext = null;
            if (context is { IsCollectible: true })
            {
                if (UnloadingContexts.TryGetValue(context, out _))
                    return;

                if (!this.byContext.TryGetValue(context, out ofContext))
                {
                    this.byContext.Add(context, ofContext = []);
                    subscribe = true;
                }
            }

            foreach (var pair in found)
            {
                if (this.owners.TryAdd(pair.Key, pair.Value))
                    ofContext?.Add(pair);
            }
        }

        // Contexts unloaded outside the plugin loader are seen through their own event.
        if (subscribe)
            context!.Unloading += static unloading => MarkUnloading(unloading);
    }

    private void Forget(AssemblyLoadContext context)
    {
        lock (this.gate)
        {
            // A freed type's address may come back as another type, which must get its own look-up.
            this.requested.Clear();
            Volatile.Write(ref this.requestedCount, 0);
            if (!this.byContext.TryGetValue(context, out var ofContext))
                return;

            foreach (var pair in ofContext)
                this.owners.TryRemove(pair);
            this.byContext.Remove(context);
        }
    }

    /// <summary>
    /// What the look-ups so far cost.
    /// </summary>
    /// <param name="LookUps">Passes over the loaded assemblies.</param>
    /// <param name="TypesLoaded">Types loaded to compare their ids, found or not.</param>
    /// <param name="Failures">Types or assemblies that could not be read.</param>
    /// <param name="Time">Time spent looking up.</param>
    internal readonly record struct Cost(int LookUps, int TypesLoaded, int Failures, TimeSpan Time);
}
