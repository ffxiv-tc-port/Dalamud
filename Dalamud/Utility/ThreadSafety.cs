using System.Diagnostics;
using System.Runtime.CompilerServices;

using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Serilog;

namespace Dalamud.Utility;

/// <summary>
/// Helpers for working with thread safety.
/// </summary>
public static class ThreadSafety
{
    private const int NonMainThreadWarningCheckIntervalMs = 10000;

    private static readonly ConditionalWeakTable<LocalPlugin, object> NonMainThreadWarnedPlugins = new();

    [ThreadStatic]
    private static bool threadStaticIsMainThread;

    private static long nextNonMainThreadWarningCheck;

    /// <summary>
    /// Gets a value indicating whether the current thread is the main thread.
    /// </summary>
    public static bool IsMainThread => threadStaticIsMainThread;

    /// <summary>
    /// Logs a throttled warning when the current thread is not the main thread.
    /// </summary>
    /// <param name="message">The message to be included in the warning, if one is to be logged.</param>
    /// <remarks>
    /// This used to throw an <see cref="InvalidOperationException"/>, and still does so upstream. It only warns
    /// here, because <see cref="IsMainThread"/> had been hardcoded to <c>true</c> for a long time on this fork:
    /// every one of these assertions was inert, so plugins may have come to depend on off-main-thread access
    /// being tolerated. Warning instead of throwing makes those call sites observable without breaking them.
    /// <para>
    /// Warnings are throttled: at most one stack trace is taken per
    /// <see cref="NonMainThreadWarningCheckIntervalMs"/> milliseconds, and each plugin is only ever named once.
    /// This matters because callers such as the <c>ObjectTable</c> indexer sit on per-frame hot paths.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertMainThread(string? message = null)
    {
        if (!threadStaticIsMainThread)
        {
            WarnNotMainThread(message);
        }
    }

    /// <summary>
    /// Throws an exception when the current thread is the main thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the current thread is the main thread.</exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AssertNotMainThread()
    {
        if (threadStaticIsMainThread)
        {
            throw new InvalidOperationException("On main thread!");
        }
    }

    /// <summary><see cref="AssertMainThread"/>, but only on debug compilation mode.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void DebugAssertMainThread()
    {
#if DEBUG
        AssertMainThread();
#endif
    }

    /// <summary>
    /// Marks a thread as the main thread.
    /// </summary>
    internal static void MarkMainThread()
    {
        threadStaticIsMainThread = true;
    }

    /// <summary>
    /// Logs a throttled warning naming whoever called a main-thread-only API off the main thread.
    /// </summary>
    /// <param name="message">The message supplied by the assertion, if any.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WarnNotMainThread(string? message)
    {
        // Capturing a stack trace is expensive and some callers are on per-frame hot paths, so bail out cheaply
        // until the throttle interval has elapsed.
        if (Environment.TickCount64 < nextNonMainThreadWarningCheck)
            return;

        nextNonMainThreadWarningCheck = Environment.TickCount64 + NonMainThreadWarningCheckIntervalMs;

        var stack = new StackTrace();
        var detail = message ?? "A main thread only API was accessed.";
        if (Service<PluginManager>.GetNullable()?.FindCallingPlugin(stack) is { } plugin)
        {
            // Only ever name a given plugin once, so that a misbehaving plugin cannot flood the log.
            if (NonMainThreadWarnedPlugins.TryGetValue(plugin, out _))
                return;

            NonMainThreadWarnedPlugins.Add(plugin, new object());
            Log.Warning(
                "[ThreadSafety] {pluginName}: {detail} Caller is not on main thread, which is not safe.\n{stack}",
                plugin.Name,
                detail,
                stack);
        }
        else
        {
            Log.Warning(
                "[ThreadSafety] Dalamud internal: {detail} Caller is not on main thread, which is not safe.\n{stack}",
                detail,
                stack);
        }
    }
}
