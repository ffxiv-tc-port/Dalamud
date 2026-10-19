using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.ImGuiNotification.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Services;

using ClientStateService = Dalamud.Game.ClientState.ClientState;

namespace Dalamud.Plugin.Internal;

/// <summary>
/// Tells the user in chat (and with a notification) that a plugin failed to load, since a dead plugin cannot say so itself.
/// </summary>
/// <remarks>
/// <see cref="Report"/> may be called from any thread and never throws; delivery runs on the framework thread.
/// Output only: plugin state and the load flow are not touched.
/// Chat waits until the player has been logged in for <see cref="ChatDelayAfterLogin"/>; nothing is delivered while the framework is unloading.
/// Each plugin version is reported at most once per session, so a hot update to a new version that fails again is reported again.
/// </remarks>
internal sealed class PluginLoadFailureEcho
{
    /// <summary>Longest exception text shown, in UTF-16 units, ellipsis included.</summary>
    internal const int MaxDetailLength = 160;

    /// <summary>How long the player must have been logged in before chat lines are printed.</summary>
    internal static readonly TimeSpan ChatDelayAfterLogin = TimeSpan.FromSeconds(5);

    private static readonly ModuleLog Log = new("PLUGINECHO");

    private readonly object gate = new();
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly List<Entry> pending = new();

    private int pendingCount;
    private int subscribed;

    // Pump thread only.
    private DateTime? loggedInSince;

    /// <summary>Gets the process-wide instance used by <see cref="LocalPlugin"/>.</summary>
    internal static PluginLoadFailureEcho Instance { get; } = new();

    /// <summary>
    /// Queues a load failure for display. Never throws.
    /// </summary>
    /// <param name="plugin">The plugin that failed to load.</param>
    /// <param name="exception">What the load threw.</param>
    public static void Report(LocalPlugin plugin, Exception exception)
    {
        try
        {
            if (Instance.Enqueue(plugin.InternalName, plugin.Name, plugin.EffectiveVersion?.ToString() ?? "?", exception))
                Instance.EnsureSubscribed();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not queue the load failure notice for {Plugin}", plugin?.InternalName);
        }
    }

    /// <summary>
    /// Reduces an exception to one short line: wrappers peeled off, first line only, capped at <see cref="MaxDetailLength"/>.
    /// </summary>
    /// <param name="exception">The exception.</param>
    /// <returns>The line to show.</returns>
    internal static string Describe(Exception exception)
    {
        var inner = Unwrap(exception);
        var message = inner.Message ?? string.Empty;
        var lineEnd = message.IndexOfAny(['\r', '\n']);
        var line = (lineEnd >= 0 ? message[..lineEnd] : message).Trim();
        if (line.Length == 0)
            line = inner.GetType().Name;

        if (line.Length > MaxDetailLength)
        {
            var cut = MaxDetailLength - 1;
            if (char.IsHighSurrogate(line[cut - 1]))
                cut--;
            line = line[..cut] + "…";
        }

        return line;
    }

    /// <summary>Gets the chat line for a failure.</summary>
    /// <param name="pluginName">Display name of the plugin.</param>
    /// <param name="detail">Output of <see cref="Describe"/>.</param>
    /// <returns>The chat line.</returns>
    internal static string FormatChat(string pluginName, string detail)
        => $"插件「{pluginName}」載入失敗，目前無法使用：{detail}（詳情請輸入 /xllog 查看）";

    /// <summary>
    /// Records a failure unless this plugin version was already reported this session.
    /// </summary>
    /// <param name="internalName">Internal name of the plugin.</param>
    /// <param name="displayName">Display name of the plugin.</param>
    /// <param name="version">Version that failed.</param>
    /// <param name="exception">What the load threw.</param>
    /// <returns>Whether it was queued.</returns>
    internal bool Enqueue(string internalName, string displayName, string version, Exception exception)
    {
        var entry = new Entry(displayName, Describe(exception));
        lock (this.gate)
        {
            if (!this.reported.Add($"{internalName}@{version}"))
                return false;

            this.pending.Add(entry);
            Volatile.Write(ref this.pendingCount, this.pending.Count);
        }

        return true;
    }

    /// <summary>
    /// Delivers what is due. Call from a single thread.
    /// </summary>
    /// <param name="unloading">Whether the framework is unloading; nothing is delivered if so.</param>
    /// <param name="loggedIn">Whether the player is logged in and chat can be printed to.</param>
    /// <param name="now">Current time.</param>
    /// <param name="printChat">Prints one chat line.</param>
    /// <param name="notify">Shows a notification (name, detail); returns false if notifications are not available yet.</param>
    /// <returns>How many chat lines were printed.</returns>
    internal int Pump(bool unloading, bool loggedIn, DateTime now, Action<string> printChat, Func<string, string, bool> notify)
    {
        if (Volatile.Read(ref this.pendingCount) == 0 || unloading)
            return 0;

        if (!loggedIn)
            this.loggedInSince = null;
        else
            this.loggedInSince ??= now;

        var chatReady = this.loggedInSince is { } since && now - since >= ChatDelayAfterLogin;

        Entry[] snapshot;
        lock (this.gate)
            snapshot = this.pending.ToArray();

        var printed = new List<Entry>();
        try
        {
            foreach (var entry in snapshot)
            {
                if (!entry.Notified)
                    entry.Notified = notify(entry.PluginName, entry.Detail);

                if (!chatReady)
                    continue;

                // Removed even if printing throws, so one bad line cannot repeat every frame.
                printed.Add(entry);
                printChat(FormatChat(entry.PluginName, entry.Detail));
            }
        }
        finally
        {
            if (printed.Count > 0)
            {
                lock (this.gate)
                {
                    foreach (var entry in printed)
                        this.pending.Remove(entry);

                    Volatile.Write(ref this.pendingCount, this.pending.Count);
                    if (this.pending.Count == 0)
                        this.loggedInSince = null;
                }
            }
        }

        return printed.Count;
    }

    private static Exception Unwrap(Exception exception)
    {
        var current = exception;
        for (var depth = 0; depth < 16; depth++)
        {
            var inner = current switch
            {
                AggregateException { InnerExceptions.Count: 1 } aggregate => aggregate.InnerExceptions[0],
                TargetInvocationException invocation => invocation.InnerException,
                TypeInitializationException typeInit => typeInit.InnerException,
                _ => null,
            };

            if (inner == null)
                break;

            current = inner;
        }

        return current;
    }

    private void EnsureSubscribed()
    {
        if (Interlocked.Exchange(ref this.subscribed, 1) != 0)
            return;

        Service<Framework>.GetAsync().ContinueWith(
            task =>
            {
                if (task.IsCompletedSuccessfully)
                    task.Result.Update += this.OnFrameworkUpdate;
            },
            TaskContinuationOptions.ExecuteSynchronously);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (Volatile.Read(ref this.pendingCount) == 0)
            return;

        try
        {
            var clientState = Service<ClientStateService>.GetNullable(Service<ClientStateService>.ExceptionPropagationMode.None);
            var chatGui = Service<ChatGui>.GetNullable(Service<ChatGui>.ExceptionPropagationMode.None);
            var notifications = Service<NotificationManager>.GetNullable(Service<NotificationManager>.ExceptionPropagationMode.None);

            this.Pump(
                framework.IsFrameworkUnloading,
                chatGui != null && clientState is { IsLoggedIn: true },
                DateTime.UtcNow,
                line =>
                {
                    chatGui!.PrintError(line);
                    Log.Information("Load failure notice printed to chat: {Line}", line);
                },
                (pluginName, detail) =>
                {
                    if (notifications == null)
                        return false;

                    notifications.AddNotification(new Notification
                    {
                        Title = "插件載入失敗",
                        Content = $"「{pluginName}」：{detail}\n詳情請輸入 /xllog 查看。",
                        MinimizedText = $"插件「{pluginName}」載入失敗",
                        Type = NotificationType.Error,
                        InitialDuration = TimeSpan.FromSeconds(15),
                    });
                    return true;
                });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not deliver plugin load failure notices");
        }
    }

    private sealed class Entry(string pluginName, string detail)
    {
        public string PluginName { get; } = pluginName;

        public string Detail { get; } = detail;

        // Pump thread only.
        public bool Notified { get; set; }
    }
}
