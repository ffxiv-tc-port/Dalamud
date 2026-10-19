using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

namespace Dalamud.Plugin.Internal.Loader;

/// <summary>Process-wide events of the runtime that outlive every plugin.</summary>
/// <remarks>
/// Their handlers are read from the private fields behind them and removed through the public <c>-=</c>, which is
/// lock-free and safe beside any raise or subscription. The fields were read on <see cref="VerifiedRuntimeMajor"/> only.
/// Example: Microsoft.Data.Sqlite hooks ProcessExit and DomainUnload and offers no way to unhook.
/// </remarks>
internal static partial class UnloadedContextHolders
{
    /// <summary>The runtime major version whose private event and timer fields were read.</summary>
    public const int VerifiedRuntimeMajor = 9;

    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Lazy<int> RuntimeMajor = new(() => typeof(object).Assembly.GetName().Version?.Major ?? 0);
    private static readonly Lazy<StaticEvent[]> StaticEvents = new(CreateStaticEvents, LazyThreadSafetyMode.ExecutionAndPublication);

    private static int unverifiedWarned;

    private static bool IsVerifiedRuntime(int runtimeMajor)
    {
        if (runtimeMajor == VerifiedRuntimeMajor)
            return true;
        if (Interlocked.Exchange(ref unverifiedWarned, 1) == 0)
        {
            Log.Warning(
                "Runtime events and timers are not released on .NET {Major}: their private fields were read on {Verified} only; unloaded plugins that left handlers or timers there stay in memory",
                runtimeMajor,
                VerifiedRuntimeMajor);
        }

        return false;
    }

    private static int ReleaseStaticEvents(string name, ContextOwnership ownership, int runtimeMajor)
    {
        if (!IsVerifiedRuntime(runtimeMajor))
            return 0;

        var count = 0;
        foreach (var staticEvent in StaticEvents.Value)
        {
            if (staticEvent.Read() is not { } handlers)
                continue;
            foreach (var part in handlers.GetInvocationList())
            {
                if (!ownership.Defines(part))
                    continue;
                staticEvent.Remove(part);
                Log.Information("{Context}: removed {Event:l} handler ({Delegate:l})", name, staticEvent.Name, Describe(part));
                count++;
            }
        }

        return count;
    }

    private static void CensusStaticEvents(ContextOwnership ownership, List<string> found)
    {
        if (RuntimeMajor.Value != VerifiedRuntimeMajor)
            return;
        foreach (var staticEvent in StaticEvents.Value)
            AddDelegate(ownership, "event " + staticEvent.Name, staticEvent.Read(), found);
    }

    private static StaticEvent[] CreateStaticEvents()
    {
        var events = new List<StaticEvent>();
        var missing = new List<string>();
        var domain = AppDomain.CurrentDomain;
        var alc = typeof(AssemblyLoadContext);
        var defaultContext = AssemblyLoadContext.Default;

        void Add(string name, Type fieldOwner, string field, object? fieldTarget, Type eventOwner, object? eventTarget)
        {
            var info = fieldOwner.GetField(field, fieldTarget == null ? AnyStatic : AnyInstance);
            var publicEvent = eventOwner.GetEvent(name[(name.LastIndexOf('.') + 1)..], (eventTarget == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public);
            if (info == null || publicEvent?.RemoveMethod is not { IsPublic: true } || info.FieldType != publicEvent.EventHandlerType)
                missing.Add(name);
            else
                events.Add(new StaticEvent(name, info, fieldTarget, publicEvent, eventTarget));
        }

        // AppDomain forwards most of its events to AppContext and AssemblyLoadContext, where the handlers are kept.
        Add("AppDomain.ProcessExit", typeof(AppDomain), "ProcessExit", domain, typeof(AppDomain), domain);
        Add("AppDomain.DomainUnload", typeof(AppDomain), "DomainUnload", domain, typeof(AppDomain), domain);
        Add("AppDomain.ReflectionOnlyAssemblyResolve", typeof(AppDomain), "ReflectionOnlyAssemblyResolve", domain, typeof(AppDomain), domain);
        Add("AppDomain.UnhandledException", typeof(AppContext), "UnhandledException", null, typeof(AppDomain), domain);
        Add("AppDomain.FirstChanceException", typeof(AppContext), "FirstChanceException", null, typeof(AppDomain), domain);
        foreach (var name in new[] { "AssemblyLoad", "AssemblyResolve", "TypeResolve", "ResourceResolve" })
            Add("AppDomain." + name, alc, name, null, typeof(AppDomain), domain);
        Add("AssemblyLoadContext.Default.Resolving", alc, "_resolving", defaultContext, alc, defaultContext);
        Add("AssemblyLoadContext.Default.ResolvingUnmanagedDll", alc, "_resolvingUnmanagedDll", defaultContext, alc, defaultContext);
        Add("AssemblyLoadContext.Default.Unloading", alc, "_unloading", defaultContext, alc, defaultContext);
        Add("TaskScheduler.UnobservedTaskException", typeof(TaskScheduler), "UnobservedTaskException", null, typeof(TaskScheduler), null);

        if (missing.Count > 0)
        {
            Log.Warning(
                "Runtime events not released, their fields differ in this .NET version: {Missing}; unloaded plugins that left handlers there stay in memory",
                string.Join(", ", missing));
        }

        return events.ToArray();
    }

    /// <summary>One event: its handlers are read from <paramref name="field"/> and removed through <paramref name="publicEvent"/>.</summary>
    private sealed class StaticEvent(string name, FieldInfo field, object? fieldTarget, EventInfo publicEvent, object? eventTarget)
    {
        public string Name => name;

        public Delegate? Read() => field.GetValue(fieldTarget) as Delegate;

        public void Remove(Delegate part) => publicEvent.RemoveEventHandler(eventTarget, part);
    }
}
