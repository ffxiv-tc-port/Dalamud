using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace Dalamud.Plugin.Internal.Loader;

/// <summary>System.Threading.Timer instances still scheduled in the runtime's timer queues.</summary>
/// <remarks>
/// A scheduled timer is rooted by its queue, and with it its callback and state, until disposed. A timer whose callback
/// is defined in the unloaded context is disposed the way Timer.Dispose does it. The queues are walked under the lock
/// their own code takes, read on <see cref="VerifiedRuntimeMajor"/> only. Example: Microsoft.Data.Sqlite's pool pruning timer.
/// </remarks>
internal static partial class UnloadedContextHolders
{
    private static readonly Lazy<TimerQueues?> Timers = new(TimerQueues.Create, LazyThreadSafetyMode.ExecutionAndPublication);

    private static int ReleaseTimers(string name, ContextOwnership ownership, int runtimeMajor)
    {
        if (!IsVerifiedRuntime(runtimeMajor) || Timers.Value is not { } timers)
            return 0;

        var count = 0;
        foreach (var (timer, callback, _) in timers.Scheduled())
        {
            if (!Array.Exists(callback.GetInvocationList(), ownership.Defines))
                continue;
            timers.Dispose(timer);
            Log.Information("{Context}: disposed timer ({Delegate:l})", name, Describe(callback));
            count++;
        }

        return count;
    }

    private static void CensusTimers(ContextOwnership ownership, List<string> found)
    {
        if (RuntimeMajor.Value != VerifiedRuntimeMajor || Timers.Value is not { } timers)
            return;

        foreach (var (_, callback, state) in timers.Scheduled())
        {
            if (Array.Exists(callback.GetInvocationList(), ownership.Involves))
                found.Add($"timer ({Describe(callback)})");
            else if (ownership.FindReachable(state) is { } what)
                found.Add($"timer state ({Describe(callback)}: {what})");
        }
    }

    /// <summary>The private members of TimerQueue and TimerQueueTimer this file uses.</summary>
    private sealed class TimerQueues(
        PropertyInfo instances, PropertyInfo sharedLock, FieldInfo[] lists, FieldInfo next, FieldInfo callback, FieldInfo state, MethodInfo dispose)
    {
        public static TimerQueues? Create()
        {
            var queue = typeof(Timer).Assembly.GetType("System.Threading.TimerQueue");
            var timer = typeof(Timer).Assembly.GetType("System.Threading.TimerQueueTimer");
            var instances = queue?.GetProperty("Instances", AnyStatic);
            var sharedLock = queue?.GetProperty("SharedLock", AnyInstance);
            var shortTimers = queue?.GetField("_shortTimers", AnyInstance);
            var longTimers = queue?.GetField("_longTimers", AnyInstance);
            var next = timer?.GetField("_next", AnyInstance);
            var callback = timer?.GetField("_timerCallback", AnyInstance);
            var state = timer?.GetField("_state", AnyInstance);
            var dispose = timer?.GetMethod("Dispose", AnyInstance, Type.EmptyTypes);
            if (queue == null || timer == null || instances?.PropertyType != queue.MakeArrayType() || sharedLock?.PropertyType != typeof(Lock) ||
                shortTimers?.FieldType != timer || longTimers?.FieldType != timer || next?.FieldType != timer ||
                callback?.FieldType != typeof(TimerCallback) || state == null || dispose == null)
            {
                Log.Warning("Timers are not released, the timer queue differs in this .NET version; unloaded plugins that left a timer running stay in memory");
                return null;
            }

            return new TimerQueues(instances, sharedLock, [shortTimers, longTimers], next, callback, state, dispose);
        }

        // Copied under each queue's lock; Dispose takes that lock itself, so it is called after.
        public List<(object Timer, Delegate Callback, object? State)> Scheduled()
        {
            var scheduled = new List<(object Timer, Delegate Callback, object? State)>();
            foreach (var queue in (Array)instances.GetValue(null)!)
            {
                var queueLock = (Lock)sharedLock.GetValue(queue)!;
                using (queueLock.EnterScope())
                {
                    foreach (var list in lists)
                    {
                        for (var timer = list.GetValue(queue); timer != null; timer = next.GetValue(timer))
                        {
                            if (callback.GetValue(timer) is Delegate timerCallback)
                                scheduled.Add((timer, timerCallback, state.GetValue(timer)));
                        }
                    }
                }
            }

            return scheduled;
        }

        public void Dispose(object timer) => dispose.Invoke(timer, null);
    }
}
