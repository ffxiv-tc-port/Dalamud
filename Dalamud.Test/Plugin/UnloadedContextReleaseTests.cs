#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Plugin.Internal.Loader;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Ipc.Internal;
using Dalamud.Support;

using Xunit;

namespace Dalamud.Test.Plugin
{
    // Loads a copy of this assembly as a plugin; each holder is first shown to keep the context alive by itself (control).
    public class UnloadedContextReleaseTests
    {
        private static readonly string Side = typeof(ReleaseProbePluginSide).FullName!;

        [Fact]
        public void LastExceptionKeepsContextAliveUnlessForgotten()
        {
            var pinned = LoadAndDispose(null, asm => CallSide(asm, nameof(ReleaseProbePluginSide.LogOwnException)));
            Assert.True(IsStillAlive(pinned), "A stored exception no longer pins the context by itself; revisit this test.");

            var released = LoadAndDispose(
                _ => Troubleshooting.ForgetLastException(),
                asm => CallSide(asm, nameof(ReleaseProbePluginSide.LogOwnException)));
            Assert.Null(Troubleshooting.LastException);
            Assert.False(IsStillAlive(released), "Troubleshooting.LastException kept the unloaded plugin context alive.");
        }

        [Fact]
        public void IpcDelegatesKeepContextAliveUnlessReleased()
        {
            var gate = NewCallGate();
            var (subscribed, provided) = (gate.GetOrCreateChannel("Probe.Sub"), gate.GetOrCreateChannel("Probe.Provided"));
            var other = gate.GetOrCreateChannel("Probe.Other");
            var hostCalls = new int[2];
            Action<int> hostDirect = n => Interlocked.Add(ref hostCalls[0], n);
            Action<int> hostInMulticast = n => Interlocked.Add(ref hostCalls[1], n);
            subscribed.Subscribe(hostDirect);
            other.Func = new Func<int, int>(n => n * 2);

            var pinned = LoadAndDispose(null, asm => CallSide(asm, nameof(ReleaseProbePluginSide.UseIpc), subscribed, provided, hostInMulticast));
            Assert.True(IsStillAlive(pinned), "IPC delegates no longer pin the context by themselves; revisit this test.");
            ReleaseAllButHost(subscribed, provided, hostDirect, hostInMulticast);

            var removed = -1;
            var released = LoadAndDispose(
                context => removed = UnloadedContextHolders.Release(context, gate),
                asm => CallSide(asm, nameof(ReleaseProbePluginSide.UseIpc), subscribed, provided, hostInMulticast));
            Assert.False(IsStillAlive(released), "IPC delegates kept the unloaded plugin context alive.");

            // Static method, closure and the plugin half of the multicast; the action and the function.
            Assert.Equal(5, removed);
            Assert.Equal(2, subscribed.Subscriptions.Count);
            Assert.Contains(hostDirect, subscribed.Subscriptions);
            Assert.Contains(hostInMulticast, subscribed.Subscriptions);
            subscribed.SendMessage([3]);
            Assert.Equal(3, hostCalls[0]);
            Assert.Equal(3, hostCalls[1]);
            Assert.Null(provided.Action);
            Assert.Null(provided.Func);
            Assert.Throws<IpcNotReadyError>(() => provided.InvokeAction([1]));
            Assert.Equal(8, other.InvokeFunc<int>([4]));
        }

        [Fact]
        public void ReleaseRacesWithInvokeAndSendMessage()
        {
            var gate = NewCallGate();
            var subscribed = gate.GetOrCreateChannel("Race.Sub");
            var provided = gate.GetOrCreateChannel("Race.Provided");
            var hostCalls = 0;
            Action<int> host = _ => Interlocked.Increment(ref hostCalls);
            Action<int> hostInMulticast = _ => { };
            subscribed.Subscribe(host);

            var stop = 0;
            var errors = new List<Exception>();
            var invocations = 0L;
            void Guard(Action body)
            {
                try
                {
                    body();
                }
                catch (Exception ex)
                {
                    lock (errors)
                        errors.Add(ex);
                }
            }

            var invoker = new Thread(() => Guard(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    subscribed.SendMessage([1]);
                    try
                    {
                        provided.InvokeAction([1]);
                        provided.InvokeFunc<int>([1]);
                    }
                    catch (IpcNotReadyError)
                    {
                    }

                    Interlocked.Increment(ref invocations);
                }
            }));
            Action<int> churn = _ => { };
            var subscriber = new Thread(() => Guard(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    subscribed.Subscribe(churn);
                    subscribed.Unsubscribe(churn);
                }
            }));
            invoker.Start();
            subscriber.Start();

            var contexts = new List<WeakReference>();
            for (var i = 0; i < 20; i++)
            {
                contexts.Add(LoadAndDispose(
                    context => UnloadedContextHolders.Release(context, gate),
                    asm => CallSide(asm, nameof(ReleaseProbePluginSide.UseIpc), subscribed, provided, hostInMulticast)));
                Assert.Contains(host, subscribed.Subscriptions);
                Assert.Null(provided.Action);
            }

            Volatile.Write(ref stop, 1);
            invoker.Join();
            subscriber.Join();

            Assert.Empty(errors);
            Assert.True(Interlocked.Read(ref invocations) > 0);
            Assert.Equal(2, subscribed.Subscriptions.Count);
            Assert.Contains(host, subscribed.Subscriptions);
            Assert.Contains(hostInMulticast, subscribed.Subscriptions);
            subscribed.SendMessage([1]);
            Assert.True(hostCalls > 0);
            Assert.All(contexts, c => Assert.False(IsStillAlive(c), "A context released while IPC was in use stayed in memory."));
        }

        [Fact]
        public void CensusNamesWhatDalamudHolds()
        {
            var gate = NewCallGate();
            var subscribed = gate.GetOrCreateChannel("Census.Sub");
            var dataShare = (DataShare)Activator.CreateInstance(typeof(DataShare), nonPublic: true)!;
            var waiting = new TaskCompletionSource();
            List<string>? holders = null;
            LoadAndDispose(
                context =>
                {
                    holders = UnloadedContextHolders.Census(context, new(gate, dataShare, () => [waiting.Task]));
                    UnloadedContextHolders.Release(context, gate);
                    Troubleshooting.ForgetLastException();
                },
                asm => CallSide(asm, nameof(ReleaseProbePluginSide.HoldEverywhere), subscribed, dataShare, waiting.Task));

            Assert.NotNull(holders);
            Assert.Contains($"IPC subscription 'Census.Sub' ({Side}::{nameof(ReleaseProbePluginSide.OnMessage)})", holders!);
            Assert.Contains(holders!, h => h.StartsWith("DataShare 'Census.Data'", StringComparison.Ordinal));
            Assert.Contains("Troubleshooting.LastException (System.InvalidOperationException)", holders!);
            Assert.Contains(holders!, h => h.StartsWith("framework work (", StringComparison.Ordinal) && h.Contains(Side, StringComparison.Ordinal));
            waiting.SetResult();
        }

        [Fact]
        public void RuntimeEventsKeepContextAliveUnlessReleased()
        {
            EventHandler hostExit = (_, _) => { };
            AppDomain.CurrentDomain.ProcessExit += hostExit;
            try
            {
                var pinned = LoadAndDispose(null, asm => CallSide(asm, nameof(ReleaseProbePluginSide.HookRuntimeEvents)));
                Assert.True(IsStillAlive(pinned), "Runtime events no longer pin the context by themselves; revisit this test.");
                Assert.Equal(0, ReleaseAfterUnload(pinned, 10));
                Assert.True(IsStillAlive(pinned), "Runtime events were released on a runtime whose fields were not verified.");
                Assert.Equal(6, ReleaseAfterUnload(pinned, UnloadedContextHolders.VerifiedRuntimeMajor));
                Assert.False(IsStillAlive(pinned), "Releasing after the unload did not free the context.");

                List<string>? holders = null;
                var removed = -1;
                var released = LoadAndDispose(
                    context =>
                    {
                        holders = UnloadedContextHolders.Census(context, new(null, null, null));
                        removed = UnloadedContextHolders.Release(context, null);
                    },
                    asm => CallSide(asm, nameof(ReleaseProbePluginSide.HookRuntimeEvents)));
                Assert.False(IsStillAlive(released), "Runtime event handlers kept the unloaded plugin context alive.");
                Assert.Equal(6, removed);
                foreach (var name in new[] { "AppDomain.ProcessExit", "AppDomain.DomainUnload", "AppDomain.FirstChanceException", "AppDomain.AssemblyResolve", "AssemblyLoadContext.Default.Resolving", "TaskScheduler.UnobservedTaskException" })
                    Assert.Contains(holders!, h => h.StartsWith($"event {name} ({Side}", StringComparison.Ordinal));
                Assert.Contains(hostExit, ProcessExitHandlers());
            }
            finally
            {
                AppDomain.CurrentDomain.ProcessExit -= hostExit;
            }
        }

        [Fact]
        public void TimersKeepContextAliveUnlessReleased()
        {
            var hostTicks = new int[1];
            using var hostTimer = new Timer(_ => Interlocked.Increment(ref hostTicks[0]), null, 10, 10);
            Assert.True(Advances(hostTicks), "The host timer never ran; the check below would prove nothing.");

            var pinned = LoadAndDispose(null, asm => CallSide(asm, nameof(ReleaseProbePluginSide.StartTimers)));
            Assert.True(IsStillAlive(pinned), "A running timer no longer pins the context by itself; revisit this test.");
            Assert.Equal(0, ReleaseAfterUnload(pinned, 10));
            Assert.True(IsStillAlive(pinned), "Timers were released on a runtime whose fields were not verified.");
            Assert.Equal(2, ReleaseAfterUnload(pinned, UnloadedContextHolders.VerifiedRuntimeMajor));
            Assert.False(IsStillAlive(pinned), "Disposing the timers after the unload did not free the context.");

            List<string>? holders = null;
            var removed = -1;
            var released = LoadAndDispose(
                context =>
                {
                    holders = UnloadedContextHolders.Census(context, new(null, null, null));
                    removed = UnloadedContextHolders.Release(context, null);
                },
                asm => CallSide(asm, nameof(ReleaseProbePluginSide.StartTimers)));
            Assert.False(IsStillAlive(released), "Timers kept the unloaded plugin context alive.");
            Assert.Equal(2, removed);
            Assert.Equal(2, holders!.Count(h => h.StartsWith($"timer ({Side}", StringComparison.Ordinal)));

            Assert.True(Advances(hostTicks), "Another plugin's timer was stopped.");
        }

        [Fact]
        public void ReportListsHoldersTheFirstTimeAContextIsAlive()
        {
            var watch = new UnloadedContextWatch.Watch();
            var gate = NewCallGate();
            var subscribed = gate.GetOrCreateChannel("Report.Sub");
            var pinnedByHost = LoadAndDispose(watch.Track, asm => HostStatic.Keep = CallSide(asm, nameof(ReleaseProbePluginSide.NewState)));
            var pinnedByIpc = LoadAndDispose(watch.Track, asm => CallSide(asm, nameof(ReleaseProbePluginSide.UseIpc), subscribed, gate.GetOrCreateChannel("Report.P"), new Action<int>(_ => { })));

            for (var i = 0; i <= UnloadedContextWatch.MinimumFullCollections; i++)
                GC.Collect();
            var first = watch.ReportWith(context => UnloadedContextHolders.Census(context, new(gate, null, null)));
            var alive = first.Where(o => !o.Collected).ToList();
            Assert.Equal(2, alive.Count);
            Assert.Contains(alive, o => o.Holders is { Count: 0 });
            Assert.Contains(alive, o => o.Holders is { Count: > 0 } h && h.Any(x => x.StartsWith("IPC subscription 'Report.Sub'", StringComparison.Ordinal)));

            GC.Collect();
            var second = watch.ReportWith(_ => throw new InvalidOperationException("listed twice"));
            Assert.All(second.Where(o => !o.Collected), o => Assert.Null(o.Holders));

            HostStatic.Keep = null;
            RemoveAll(gate);
            Assert.False(IsStillAlive(pinnedByHost));
            Assert.False(IsStillAlive(pinnedByIpc));
            Assert.Contains(watch.ReportWith(_ => []), o => o.Collected);
        }

        internal static object? CallSide(Assembly pluginAssembly, string method, params object?[] args) =>
            pluginAssembly.GetType(Side)!.GetMethod(method, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, args);

        internal static CallGate NewCallGate() => (CallGate)Activator.CreateInstance(typeof(CallGate), nonPublic: true)!;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static WeakReference LoadAndDispose(Action<AssemblyLoadContext>? beforeUnload, Action<Assembly>? whileLoaded)
        {
            var loader = PluginLoader.CreateFromAssemblyFile(
                typeof(UnloadedContextReleaseTests).Assembly.Location,
                config =>
                {
                    LocalPlugin.SetupLoaderConfig(config);
                    config.BeforeUnload = beforeUnload;
                });
            var assembly = loader.LoadDefaultAssembly();
            whileLoaded?.Invoke(assembly);
            var weak = new WeakReference(loader.LoadContext);
            loader.Dispose();
            return weak;
        }

        internal static bool IsStillAlive(WeakReference weak)
        {
            for (var i = 0; weak.IsAlive && i < 30; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            return weak.IsAlive;
        }

        // Waits for ticks, not for a fixed time: a loaded test host delays timer callbacks. Several ticks, because a
        // callback that was already running when its timer was disposed still counts once.
        private static bool Advances(int[] ticks, int by = 10, int limitMs = 10_000)
        {
            var target = Volatile.Read(ref ticks[0]) + by;
            return SpinWait.SpinUntil(() => Volatile.Read(ref ticks[0]) >= target, limitMs);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReleaseAfterUnload(WeakReference weak, int runtimeMajor) =>
            UnloadedContextHolders.ReleaseAs((AssemblyLoadContext)weak.Target!, null, runtimeMajor);

        private static Delegate[] ProcessExitHandlers() =>
            (typeof(AppDomain).GetField("ProcessExit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(AppDomain.CurrentDomain) as Delegate)?.GetInvocationList() ?? [];

        // In a method of its own: the removed delegates must not stay in a caller's frame (Tier 0 keeps temporaries alive).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RemoveAll(CallGate gate)
        {
            foreach (var channel in gate.Gates.Values)
                channel.RemoveDelegates(_ => true, []);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReleaseAllButHost(CallGateChannel subscribed, CallGateChannel provided, params Delegate[] host)
        {
            subscribed.RemoveDelegates(d => !host.Contains(d), []);
            provided.Action = null;
            provided.Func = null;
        }

        private static class HostStatic
        {
            public static object? Keep;
        }
    }

    // Runs inside the collectible copy of this assembly.
    internal static class ReleaseProbePluginSide
    {
        private static Timer[]? running;

        public static void LogOwnException()
        {
            try
            {
                throw new InvalidOperationException("thrown by the plugin");
            }
            catch (Exception ex)
            {
                Troubleshooting.LogException(ex, "test");
            }
        }

        public static object NewState() => new ProbeState();

        public static void OnMessage(int value)
        {
        }

        public static void UseIpc(CallGateChannel subscribed, CallGateChannel provided, Action<int> hostInMulticast)
        {
            var state = new ProbeState();
            subscribed.Subscribe(new Action<int>(OnMessage));
            subscribed.Subscribe(new Action<int>(n => state.Last = n));
            subscribed.Subscribe(Delegate.Combine(new Action<int>(n => state.Last = -n), hostInMulticast)!);
            provided.Action = new Action<int>(OnMessage);
            provided.Func = new Func<int, int>(n => n + state.Last);
        }

        public static void HookRuntimeEvents()
        {
            var state = new ProbeState();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => state.Last = 1;
            AppDomain.CurrentDomain.DomainUnload += OnDomainEvent;
            AppDomain.CurrentDomain.FirstChanceException += (_, _) => state.Last = 2;
            AppDomain.CurrentDomain.AssemblyResolve += (_, _) => null;
            AssemblyLoadContext.Default.Resolving += (_, _) => null;
            TaskScheduler.UnobservedTaskException += (_, _) => state.Last = 3;
        }

        public static void OnDomainEvent(object? sender, EventArgs e)
        {
        }

        public static void StartTimers()
        {
            var state = new ProbeState();
            running = [new Timer(OnTick, state, 30_000, 30_000), new Timer(_ => state.Last++, null, Timeout.Infinite, 1_000)];
            running[1].Change(60_000, 60_000);
        }

        public static void OnTick(object? state)
        {
        }

        public static void HoldEverywhere(CallGateChannel subscribed, DataShare dataShare, Task waiting)
        {
            subscribed.Subscribe(new Action<int>(OnMessage));
            dataShare.GetOrCreateData("Census.Data", () => new List<ProbeState> { new() });
            var state = new ProbeState();
            waiting.ContinueWith(_ => state.Last = 1, TaskScheduler.Default);
            LogOwnException();
        }

        public sealed class ProbeState
        {
            public int Last { get; set; }
        }
    }
}
