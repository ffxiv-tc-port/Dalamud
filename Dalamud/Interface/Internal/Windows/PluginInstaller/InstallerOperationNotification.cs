using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Dalamud.Interface.ImGuiNotification;
using Dalamud.Logging.Internal;
using Dalamud.Plugin.Internal.Types;

namespace Dalamud.Interface.Internal.Windows.PluginInstaller;

/// <summary>
/// Shows an install or update started from the plugin installer while the installer is closed, with the same
/// progress text as automatic updates, and replaces it with the outcome when the operation ends.
/// </summary>
/// <remarks>
/// <see cref="FollowAsync"/>, then <see cref="TryFinish"/> and <see cref="Close"/>, run one after another on a single
/// flow, which is the only writer of the notification; the draw thread only renders it. Nothing is shown while the
/// installer is open, because its own overlay already carries the same progress.
/// The notification can be dismissed at any time, so no path can leave one on screen for good.
/// </remarks>
internal sealed class InstallerOperationNotification
{
    private static readonly ModuleLog Log = new("PLUGINW");

    private static readonly TimeSpan RefreshInterval =
        TimeSpan.FromMilliseconds(PluginUpdateProgressDisplay.RefreshIntervalMs);

    private static readonly TimeSpan OutcomeDuration = TimeSpan.FromSeconds(30);

    private readonly string title;
    private readonly string idleText;
    private readonly string? pluginInternalName;
    private readonly PluginUpdateProgressDisplay display;
    private readonly Func<Notification, IActiveNotification> addNotification;
    private readonly Func<IReadOnlyList<PluginDownloadProgress>> activeDownloads;
    private readonly Func<bool> isInstallerOpen;

    private IActiveNotification? notification;
    private bool broken;

    // Set from the draw thread, which is where a notification gets dismissed.
    private volatile bool dismissedByUser;

    /// <summary>
    /// Initializes a new instance of the <see cref="InstallerOperationNotification"/> class.
    /// </summary>
    /// <param name="title">The title while the operation runs.</param>
    /// <param name="idleText">The text shown while there is no download to report, such as while a plugin loads.</param>
    /// <param name="batch">The batch of an "update all", or <c>null</c> for a single plugin.</param>
    /// <param name="pluginInternalName">For a single plugin, its internal name, so other downloads in flight stay out.</param>
    /// <param name="addNotification">Shows a notification, normally the notification manager's AddNotification.</param>
    /// <param name="activeDownloads">Reads the downloads in flight, normally from the plugin manager.</param>
    /// <param name="isInstallerOpen">Tells whether the installer window is open.</param>
    public InstallerOperationNotification(
        string title,
        string idleText,
        PluginUpdateBatchProgress? batch,
        string? pluginInternalName,
        Func<Notification, IActiveNotification> addNotification,
        Func<IReadOnlyList<PluginDownloadProgress>> activeDownloads,
        Func<bool> isInstallerOpen)
    {
        this.title = title;
        this.idleText = idleText;
        this.pluginInternalName = pluginInternalName;
        this.display = new PluginUpdateProgressDisplay(batch);
        this.addNotification = addNotification;
        this.activeDownloads = activeDownloads;
        this.isInstallerOpen = isInstallerOpen;
    }

    /// <summary>
    /// Keeps the notification up to date until <paramref name="operation"/> ends. Never throws.
    /// </summary>
    /// <param name="operation">The install or update being shown.</param>
    /// <returns>A task that completes once the operation has.</returns>
    public async Task FollowAsync(Task operation)
    {
        try
        {
            while (!operation.IsCompleted)
            {
                this.Refresh();
                await Task.WhenAny(operation, Task.Delay(RefreshInterval));
            }
        }
        catch (Exception ex)
        {
            // The operation's own completion handling must still run, so the display gives up instead.
            Log.Error(ex, "Could not show the progress of an installer operation");
            this.broken = true;
            this.Close();
            await Task.WhenAny(operation);
        }
    }

    /// <summary>
    /// Shows the outcome in the notification, if the installer is closed. Never throws, and never leaves a progress
    /// notification behind, so callers make it the first thing they do once the operation has ended.
    /// </summary>
    /// <param name="describe">Builds the outcome; called at most once.</param>
    /// <returns>Whether the outcome is shown; if not, the caller reports it the way it always has.</returns>
    public bool TryFinish(Func<Outcome> describe)
    {
        try
        {
            if (this.broken || this.dismissedByUser || this.isInstallerOpen())
            {
                this.Close();
                return false;
            }

            var outcome = describe();
            var shown = this.notification ?? this.Show();
            this.notification = null;
            shown.Title = outcome.Title;
            shown.Content = outcome.Content;
            shown.Type = outcome.Type;
            shown.Progress = 1;
            shown.HardExpiry = DateTime.Now + OutcomeDuration;
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not show the outcome of an installer operation");
            this.Close();
            return false;
        }
    }

    /// <summary>
    /// Takes down a notification that still shows progress. Does nothing after <see cref="TryFinish"/>.
    /// </summary>
    public void Close()
    {
        if (this.notification is null)
            return;

        // An expired notification is dismissed by the draw thread on its next frame.
        this.notification.HardExpiry = DateTime.Now;
        this.notification = null;
    }

    private void Refresh()
    {
        var downloads = this.activeDownloads();
        if (this.pluginInternalName is { } name)
            downloads = downloads.Where(x => x.InternalName == name).ToArray();

        this.display.Refresh(downloads, force: true);

        if (this.dismissedByUser || this.isInstallerOpen())
        {
            this.Close();
            return;
        }

        this.notification ??= this.Show();
        this.notification.Content = this.display.Lines.Count > 0 ? this.display.Text : this.idleText;
        if (this.display.Fraction is { } fraction)
            this.notification.Progress = fraction;
    }

    private IActiveNotification Show()
    {
        var shown = this.addNotification(new Notification
        {
            Title = this.title,
            Content = this.idleText,
            Type = NotificationType.Info,
            Icon = INotificationIcon.From(FontAwesomeIcon.Download),
            InitialDuration = TimeSpan.MaxValue,
            ShowIndeterminateIfNoExpiry = false,
            Minimized = false,
            Progress = 0,
            UserDismissable = true,
        });

        shown.Dismiss += args =>
        {
            if (args.Reason == NotificationDismissReason.Manual)
                this.dismissedByUser = true;
        };

        return shown;
    }

    /// <summary>
    /// How an operation ended, as shown to the user.
    /// </summary>
    /// <param name="Title">The notification title.</param>
    /// <param name="Content">The notification text.</param>
    /// <param name="Type">The notification type.</param>
    internal readonly record struct Outcome(string Title, string Content, NotificationType Type);
}
