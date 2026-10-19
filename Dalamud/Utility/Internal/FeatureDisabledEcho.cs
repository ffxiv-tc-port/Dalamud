using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Dalamud.Game;
using Dalamud.Game.Gui;
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
/// Each key is printed at most once per session.
/// </remarks>
internal sealed class FeatureDisabledEcho
{
    /// <summary>How long the player must have been logged in before chat lines are printed.</summary>
    internal static readonly TimeSpan ChatDelayAfterLogin = TimeSpan.FromSeconds(5);

    private static readonly ModuleLog Log = new("FEATUREECHO");

    private readonly object gate = new();
    private readonly HashSet<string> reported = new(StringComparer.Ordinal);
    private readonly List<string> pending = new();

    private int pendingCount;
    private int subscribed;

    // Pump thread only.
    private DateTime? loggedInSince;

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
    /// Prints what is due. Call from a single thread.
    /// </summary>
    /// <param name="unloading">Whether the framework is unloading; nothing is printed if so.</param>
    /// <param name="loggedIn">Whether the player is logged in and chat can be printed to.</param>
    /// <param name="now">Current time.</param>
    /// <param name="printChat">Prints one chat line.</param>
    /// <returns>How many chat lines were printed.</returns>
    internal int Pump(bool unloading, bool loggedIn, DateTime now, Action<string> printChat)
    {
        if (Volatile.Read(ref this.pendingCount) == 0 || unloading)
            return 0;

        if (!loggedIn)
            this.loggedInSince = null;
        else
            this.loggedInSince ??= now;

        if (this.loggedInSince is not { } since || now - since < ChatDelayAfterLogin)
            return 0;

        string[] snapshot;
        lock (this.gate)
            snapshot = this.pending.ToArray();

        var printed = 0;
        try
        {
            foreach (var line in snapshot)
            {
                // Counted before printing, so a line that throws is dropped instead of repeating every frame.
                printed++;
                printChat(line);
            }
        }
        finally
        {
            lock (this.gate)
            {
                this.pending.RemoveRange(0, printed);
                Volatile.Write(ref this.pendingCount, this.pending.Count);
                if (this.pending.Count == 0)
                    this.loggedInSince = null;
            }
        }

        return printed;
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

            this.Pump(
                framework.IsFrameworkUnloading,
                chatGui != null && clientState is { IsLoggedIn: true },
                DateTime.UtcNow,
                line =>
                {
                    chatGui!.PrintError(line);
                    Log.Information("Feature notice printed to chat: {Line}", line);
                });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not deliver feature notices");
        }
    }
}
