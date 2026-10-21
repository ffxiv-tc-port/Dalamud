using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Ipc.Internal;
using Dalamud.Support;

namespace Dalamud.Plugin.Internal.Loader;

/// <summary>
/// What Dalamud and the runtime still hold for one plugin's load context after the plugin was disposed: removed when
/// the context unloads, and listed when an unloaded context stays in memory.
/// </summary>
/// <remarks>
/// Removal only takes delegates whose method, or whose target's type, is defined in the context, so they could only run
/// the unloaded plugin's code. Listing also matches generic arguments and objects reachable through fields, and changes
/// nothing. Neither ever throws, and both may run on any thread.
/// </remarks>
internal static partial class UnloadedContextHolders
{
    /// <summary>Text logged when a census finds nothing.</summary>
    public const string NoVisibleHolder = "not held by any Dalamud-visible holder (thread / GCHandle / native callback?)";

    private const int MaxListed = 20;

    private static readonly ModuleLog Log = new("PLUGINALC");

    private static int releaseWarned;
    private static int censusWarned;

    /// <summary>Removes the context's IPC delegates, runtime event handlers and timers, logging one line per removal.</summary>
    /// <param name="context">The plugin's load context, about to be unloaded; the plugin has been disposed.</param>
    /// <param name="callGate">The IPC call gate, if it exists.</param>
    /// <returns>The number of delegates removed and timers disposed.</returns>
    public static int Release(AssemblyLoadContext context, CallGate? callGate) => ReleaseCore(context, callGate, RuntimeMajor.Value);

    /// <summary>Lists every holder Dalamud can see that still references the context.</summary>
    /// <param name="context">An unloaded context that is still in memory.</param>
    /// <param name="sources">Where to look.</param>
    /// <returns>One description per holder, at most <see cref="MaxListed"/> and a count of the rest.</returns>
    public static List<string> Census(AssemblyLoadContext context, Sources sources)
    {
        var found = new List<string>();
        var ownership = new ContextOwnership(context);
        Look("IPC", found, () => CensusIpc(ownership, sources.CallGate, found));
        Look("DataShare", found, () => CensusDataShare(ownership, sources.DataShare, found));
        Look("LastException", found, () =>
        {
            if (Troubleshooting.LastException is { } exception && ownership.Involves(exception, 0))
                found.Add($"Troubleshooting.LastException ({exception.GetType().FullName})");
        });
        Look("Framework", found, () => CensusPendingWork(ownership, sources.PendingWork, found));
        Look("static events", found, () => CensusStaticEvents(ownership, found));
        Look("timers", found, () => CensusTimers(ownership, found));

        if (found.Count > MaxListed)
        {
            var more = found.Count - MaxListed;
            found.RemoveRange(MaxListed, more);
            found.Add($"{more} more");
        }

        return found;
    }

    /// <summary>As <see cref="Release"/>, acting as if the runtime had the given major version (tests only).</summary>
    /// <param name="context">The plugin's load context, about to be unloaded; the plugin has been disposed.</param>
    /// <param name="callGate">The IPC call gate, if it exists.</param>
    /// <param name="runtimeMajor">The major version to act as.</param>
    /// <returns>The number of delegates removed.</returns>
    internal static int ReleaseAs(AssemblyLoadContext context, CallGate? callGate, int runtimeMajor) =>
        ReleaseCore(context, callGate, runtimeMajor);

    /// <summary>Describes a delegate the way a census or a removal line shows it.</summary>
    /// <param name="part">A single-cast delegate.</param>
    /// <returns>Its method's declaring type and name.</returns>
    internal static string Describe(Delegate part) =>
        $"{part.Method.DeclaringType?.FullName ?? "?"}::{part.Method.Name}";

    private static int ReleaseCore(AssemblyLoadContext context, CallGate? callGate, int runtimeMajor)
    {
        var name = context.Name ?? "?";
        var ownership = new ContextOwnership(context);
        return Remove(name, () => callGate == null ? 0 : ReleaseIpc(name, ownership, callGate)) +
               Remove(name, () => ReleaseStaticEvents(name, ownership, runtimeMajor)) +
               Remove(name, () => ReleaseTimers(name, ownership, runtimeMajor));
    }

    private static int Remove(string name, Func<int> remove)
    {
        try
        {
            return remove();
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref releaseWarned, 1) == 0)
                Log.Warning(ex, "Could not release what Dalamud holds for {Context}; it may stay in memory", name);
            return 0;
        }
    }

    private static void Look(string what, List<string> found, Action look)
    {
        try
        {
            look();
        }
        catch (Exception ex)
        {
            found.Add($"({what} could not be searched: {ex.GetType().Name})");
            if (Interlocked.Exchange(ref censusWarned, 1) == 0)
                Log.Warning(ex, "Could not search {What} for references to an unloaded plugin", what);
        }
    }

    private static int ReleaseIpc(string name, ContextOwnership ownership, CallGate callGate)
    {
        var removed = new List<(string Kind, Delegate Part)>();
        var count = 0;
        foreach (var (gate, channel) in callGate.Gates)
        {
            removed.Clear();
            channel.RemoveDelegates(ownership.Defines, removed);
            foreach (var (kind, part) in removed)
                Log.Information("{Context}: removed IPC {Kind:l} '{Gate:l}' ({Delegate:l})", name, kind, gate, Describe(part));
            count += removed.Count;
        }

        return count;
    }

    private static void CensusIpc(ContextOwnership ownership, CallGate? callGate, List<string> found)
    {
        if (callGate == null)
            return;

        foreach (var (gate, channel) in callGate.Gates)
        {
            foreach (var subscription in channel.Subscriptions)
                AddDelegate(ownership, $"IPC subscription '{gate}'", subscription, found);
            AddDelegate(ownership, $"IPC action '{gate}'", channel.Action, found);
            AddDelegate(ownership, $"IPC function '{gate}'", channel.Func, found);
        }
    }

    private static void AddDelegate(ContextOwnership ownership, string holder, Delegate? registered, List<string> found)
    {
        if (registered == null)
            return;
        foreach (var part in registered.GetInvocationList())
        {
            if (ownership.Involves(part))
                found.Add($"{holder} ({Describe(part)})");
        }
    }

    private static void CensusDataShare(ContextOwnership ownership, DataShare? dataShare, List<string> found)
    {
        if (dataShare == null)
            return;

        foreach (var (tag, type, data) in dataShare.GetCreatedData())
        {
            if (ownership.Involves(type) || ownership.FindReachable(data) != null)
                found.Add($"DataShare '{tag}' ({type.FullName})");
        }
    }

    private static void CensusPendingWork(ContextOwnership ownership, Func<List<Task>>? pendingWork, List<string> found)
    {
        if (pendingWork == null)
            return;

        foreach (var task in pendingWork())
        {
            if (ownership.FindReachable(task) is { } what)
                found.Add($"framework work ({what})");
        }
    }

    /// <summary>The services a census looks into; any may be missing.</summary>
    /// <param name="CallGate">The IPC call gate.</param>
    /// <param name="DataShare">The data share.</param>
    /// <param name="PendingWork">Copies the work waiting for the framework thread.</param>
    internal readonly record struct Sources(CallGate? CallGate, DataShare? DataShare, Func<List<Task>>? PendingWork)
    {
        /// <summary>Gets the services that exist, without waiting for any.</summary>
        /// <returns>The sources.</returns>
        public static Sources FromServices() => new(
            Service<CallGate>.GetNullable(Service<CallGate>.ExceptionPropagationMode.None),
            Service<DataShare>.GetNullable(Service<DataShare>.ExceptionPropagationMode.None),
            Service<Framework>.GetNullable(Service<Framework>.ExceptionPropagationMode.None) is { } framework ? framework.GetPendingWork : null);
    }

    /// <summary>
    /// Decides whether something belongs to one load context. Used by one thread and dropped after one release or
    /// census: its caches hold the context's types.
    /// </summary>
    private sealed class ContextOwnership(AssemblyLoadContext context)
    {
        private const BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private const int MaxDepth = 4;
        private const int MaxObjects = 512;

        private readonly Dictionary<Type, bool> involved = [];
        private readonly Dictionary<Type, FieldInfo[]> fields = [];

        public bool Defines(Assembly assembly) =>
            assembly.IsCollectible && AssemblyLoadContext.GetLoadContext(assembly) == context;

        // A method in the context, or a target object of one of its types: a method in a shared assembly that only has
        // a generic argument from the context is not matched (Involves does).
        public bool Defines(Delegate part) =>
            this.Defines(part.Method.Module.Assembly) || (part.Target is { } target && this.Defines(target.GetType().Assembly));

        public bool Involves(Type type)
        {
            if (!type.IsCollectible)
                return false;
            if (this.involved.TryGetValue(type, out var result))
                return result;

            this.involved[type] = false;
            result = this.Defines(type.Assembly) ||
                     (type.HasElementType && type.GetElementType() is { } element && this.Involves(element)) ||
                     (type.IsConstructedGenericType && type.GenericTypeArguments.Any(this.Involves));
            this.involved[type] = result;
            return result;
        }

        public bool Involves(Delegate part)
        {
            var method = part.Method;
            return this.Defines(part) ||
                   (method.DeclaringType is { } declaring && this.Involves(declaring)) ||
                   (method.IsGenericMethod && method.GetGenericArguments().Any(this.Involves)) ||
                   (part.Target is { } target && this.Involves(target.GetType()));
        }

        public bool Involves(Exception exception, int depth)
        {
            if (this.Involves(exception.GetType()))
                return true;
            foreach (var frame in new StackTrace(exception, false).GetFrames())
            {
                if (frame.GetMethod() is { } method &&
                    (this.Defines(method.Module.Assembly) || (method.DeclaringType is { } type && this.Involves(type))))
                {
                    return true;
                }
            }

            if (depth >= MaxDepth)
                return false;
            if (exception is AggregateException aggregate)
                return aggregate.InnerExceptions.Any(e => this.Involves(e, depth + 1));
            return exception.InnerException is { } inner && this.Involves(inner, depth + 1);
        }

        // Breadth first through fields and array elements, a bounded number of steps and objects. Once the object budget
        // is spent no more children are read, so a large array is not walked (and its struct elements not boxed) in vain.
        public string? FindReachable(object? root)
        {
            if (root == null)
                return null;

            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance) { root };
            var queue = new Queue<(object Value, int Depth)>();
            queue.Enqueue((root, 0));
            while (queue.TryDequeue(out var item))
            {
                var (value, depth) = item;
                if (this.Match(value) is { } match)
                    return match;
                if (depth >= MaxDepth || visited.Count >= MaxObjects || value is string or MemberInfo)
                    continue;

                foreach (var next in this.Children(value))
                {
                    if (next == null || !visited.Add(next))
                        continue;
                    queue.Enqueue((next, depth + 1));
                    if (visited.Count >= MaxObjects)
                        break;
                }
            }

            return null;
        }

        private string? Match(object value)
        {
            switch (value)
            {
                case Delegate registered:
                    foreach (var part in registered.GetInvocationList())
                    {
                        if (this.Involves(part))
                            return Describe(part);
                    }

                    return null;
                case Type type:
                    return this.Involves(type) ? "type " + type.FullName : null;
                default:
                    return this.Involves(value.GetType()) ? value.GetType().FullName : null;
            }
        }

        private IEnumerable<object?> Children(object value)
        {
            if (value is Array array)
            {
                // Elements of a struct type with no field worth following hold no reference: Match already saw the array's type.
                if (array.GetType().GetElementType() is { IsPrimitive: false, IsPointer: false } elementType && array.Rank == 1 &&
                    !(elementType.IsValueType && this.FieldsOf(elementType).Length == 0))
                {
                    foreach (var element in array)
                        yield return element;
                }

                yield break;
            }

            if (value is Delegate registered)
            {
                foreach (var part in registered.GetInvocationList())
                    yield return part.Target;
                yield break;
            }

            foreach (var field in this.FieldsOf(value.GetType()))
                yield return field.GetValue(value);
        }

        private FieldInfo[] FieldsOf(Type type)
        {
            if (this.fields.TryGetValue(type, out var result))
                return result;

            var list = new List<FieldInfo>();
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var field in t.GetFields(InstanceFields))
                {
                    var fieldType = field.FieldType;
                    if (!fieldType.IsPrimitive && !fieldType.IsPointer && !fieldType.IsFunctionPointer && !fieldType.IsByRefLike && !fieldType.IsEnum)
                        list.Add(field);
                }
            }

            return this.fields[type] = list.ToArray();
        }
    }
}
