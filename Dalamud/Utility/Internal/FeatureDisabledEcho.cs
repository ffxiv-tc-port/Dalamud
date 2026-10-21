using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;
using Dalamud.Game.Gui;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Interface.ImGuiNotification.Internal;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Services;

using ClientStateService = Dalamud.Game.ClientState.ClientState;

namespace Dalamud.Utility.Internal;

/// <summary>
/// Tells the user once in chat that a Dalamud feature switched itself off (for example a game layout it could not derive after a patch).
/// </summary>
/// <remarks>
/// <see cref="Report"/> may be called from any thread, including service constructors, and never throws; printing runs on the framework thread.
/// Chat waits until the player has been logged in for <see cref="ChatDelayAfterLogin"/>; nothing is printed while the framework is unloading.
/// Each key is printed at most once per session. Without a working chat (ChatGui failed, or its PrintMessage hook is
/// not installed) and for a line whose printing throws, the line becomes an ImGui notification instead.
/// </remarks>
internal sealed class FeatureDisabledEcho
{
    /// <summary>How long the player must have been logged in before chat lines are printed.</summary>
    internal static readonly TimeSpan ChatDelayAfterLogin = TimeSpan.FromSeconds(5);

    private static readonly ModuleLog Log = new("FEATUREECHO");

    private static readonly Func<string, bool> Notify = ShowNotification;

    private readonly object gate = new();
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly List<string> pending = new();

    // Made once: Pump runs every frame while lines wait for the login delay.
    private readonly Action<string> printChat;

    private int pendingCount;
    private int subscribed;

    // Pump thread only.
    private DateTime? loggedInSince;
    private ChatGui? pumpChat;

    /// <summary>
    /// Initializes a new instance of the <see cref="FeatureDisabledEcho"/> class.
    /// </summary>
    internal FeatureDisabledEcho() => this.printChat = this.PrintChat;

    /// <summary>Whether chat can carry the notices.</summary>
    internal enum ChatState
    {
        /// <summary>ChatGui is still loading; wait for it.</summary>
        Pending,

        /// <summary>Chat works.</summary>
        Ready,

        /// <summary>ChatGui failed or cannot print; use notifications.</summary>
        Unavailable,
    }

    /// <summary>Gets the process-wide instance.</summary>
    internal static FeatureDisabledEcho Instance { get; } = new();

    /// <summary>
    /// Queues a chat line for display. Never throws.
    /// </summary>
    /// <param name="key">Identifies the feature; a key already reported this session is ignored.</param>
    /// <param name="chatLine">The line to print.</param>
    public static void Report(string key, string chatLine)
    {
        try
        {
            if (Instance.Enqueue(key, chatLine))
                Instance.EnsureSubscribed();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not queue the feature notice for {Key}", key);
        }
    }

    /// <summary>
    /// Records a chat line unless its key was already reported this session.
    /// </summary>
    /// <param name="key">Identifies the feature.</param>
    /// <param name="chatLine">The line to print.</param>
    /// <returns>Whether it was queued.</returns>
    internal bool Enqueue(string key, string chatLine)
    {
        lock (this.gate)
        {
            if (!this.reported.Add(key))
                return false;

            this.pending.Add(chatLine);
            Volatile.Write(ref this.pendingCount, this.pending.Count);
        }

        return true;
    }

    /// <summary>
    /// Delivers what is due. Call from a single thread.
    /// </summary>
    /// <param name="unloading">Whether the framework is unloading; nothing is delivered if so.</param>
    /// <param name="chat">Whether chat works.</param>
    /// <param name="loggedIn">Whether the player is logged in and chat can be printed to.</param>
    /// <param name="now">Current time.</param>
    /// <param name="printChat">Prints one chat line.</param>
    /// <param name="notify">Shows one line as a notification; returns false if notifications are not available.</param>
    /// <returns>How many lines were delivered or given up on.</returns>
    internal int Pump(bool unloading, ChatState chat, bool loggedIn, DateTime now, Action<string> printChat, Func<string, bool> notify)
    {
        if (Volatile.Read(ref this.pendingCount) == 0 || unloading || chat == ChatState.Pending)
            return 0;

        if (chat == ChatState.Unavailable)
            return this.PumpNotifications(notify);

        if (!loggedIn)
            this.loggedInSince = null;
        else
            this.loggedInSince ??= now;

        if (this.loggedInSince is not { } since || now - since < ChatDelayAfterLogin)
            return 0;

        string[] snapshot;
        lock (this.gate)
            snapshot = this.pending.ToArray();

        foreach (var line in snapshot)
        {
            try
            {
                printChat(line);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not print a feature notice to chat; trying a notification");
                TryNotify(line, notify);
            }
        }

        this.RemoveDelivered(snapshot.Length);
        return snapshot.Length;
    }

    private static void TryNotify(string line, Func<string, bool> notify)
    {
        try
        {
            if (!notify(line))
                Log.Warning("Feature notice lost, no chat and no notifications: {Line}", line);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not show a feature notice as a notification: {Line}", line);
        }
    }

    private static bool ShowNotification(string line)
    {
        var notifications = Service<NotificationManager>.GetNullable(Service<NotificationManager>.ExceptionPropagationMode.None);
        if (notifications == null)
            return false;

        // No expiry: the countdown would start before the interface can draw it.
        notifications.AddNotification(new Notification
        {
            Title = "Dalamud 功能停用",
            Content = line,
            Type = NotificationType.Warning,
            Minimized = false,
            InitialDuration = TimeSpan.MaxValue,
            ShowIndeterminateIfNoExpiry = false,
        });
        Log.Information("Feature notice shown as a notification: {Line}", line);
        return true;
    }

    /// <summary>
    /// Shows the pending lines as notifications, in order, stopping while notifications are not available yet.
    /// </summary>
    /// <param name="notify">Shows one line; returns false if notifications are not available.</param>
    /// <returns>How many lines were delivered or given up on.</returns>
    private int PumpNotifications(Func<string, bool> notify)
    {
        string[] snapshot;
        lock (this.gate)
            snapshot = this.pending.ToArray();

        var done = 0;
        foreach (var line in snapshot)
        {
            bool shown;
            try
            {
                shown = notify(line);
            }
            catch (Exception ex)
            {
                // Dropped, so a line that throws does not repeat every frame.
                Log.Error(ex, "Could not show a feature notice as a notification: {Line}", line);
                shown = true;
            }

            if (!shown)
                break;

            done++;
        }

        this.RemoveDelivered(done);
        return done;
    }

    private void RemoveDelivered(int count)
    {
        lock (this.gate)
        {
            this.pending.RemoveRange(0, count);
            Volatile.Write(ref this.pendingCount, this.pending.Count);
            if (this.pending.Count == 0)
                this.loggedInSince = null;
        }
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
            var chatTask = Service<ChatGui>.GetAsync();
            var chatGui = chatTask.IsCompletedSuccessfully ? chatTask.Result : null;
            var chat = chatGui is { IsPrintAvailable: true } ? ChatState.Ready
                       : chatGui != null || chatTask.IsFaulted || chatTask.IsCanceled ? ChatState.Unavailable
                       : ChatState.Pending;

            this.pumpChat = chatGui;
            this.Pump(framework.IsFrameworkUnloading, chat, clientState is { IsLoggedIn: true }, DateTime.UtcNow, this.printChat, Notify);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not deliver feature notices");
        }
        finally
        {
            this.pumpChat = null;
        }
    }

    // Pump calls this only when chat is Ready, so pumpChat is set.
    private void PrintChat(string line)
    {
        this.pumpChat!.PrintError(line);
        Log.Information("Feature notice printed to chat: {Line}", line);
    }
}
