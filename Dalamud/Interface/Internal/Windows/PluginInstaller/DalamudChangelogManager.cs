using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Dalamud.Networking.Http;
using Dalamud.Plugin.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;
using Dalamud.Utility;
using Serilog;

namespace Dalamud.Interface.Internal.Windows.PluginInstaller;

/// <summary>
/// Class responsible for managing Dalamud changelogs.
/// </summary>
internal class DalamudChangelogManager
{
    private readonly PluginManager manager;

    /// <summary>
    /// Initializes a new instance of the <see cref="DalamudChangelogManager"/> class.
    /// </summary>
    /// <param name="manager">The responsible PluginManager.</param>
    public DalamudChangelogManager(PluginManager manager)
    {
        this.manager = manager;
    }

    /// <summary>
    /// Gets a list of all available changelogs.
    /// </summary>
    public IReadOnlyList<IChangelogEntry>? Changelogs { get; private set; }

    /// <summary>
    /// Reload the changelog list.
    /// </summary>
    /// <remarks>
    /// The upstream implementation fetched per-version history from the official
    /// plugin API and skipped anything third-party (<c>!plugin.IsThirdParty</c>).
    /// Neither half works here: this fork has no official API to talk to, and every
    /// plugin we ship comes from a third-party repository - so that page was
    /// structurally guaranteed to stay empty, which is exactly what users saw.
    ///
    /// The manifest already carries everything the entry needs, and Dalamud already
    /// ships a constructor that reads it, so build the list locally instead. No
    /// network call means this cannot fail or hang; the task stays async-shaped
    /// only to keep the call site unchanged.
    /// </remarks>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public Task ReloadChangelogAsync()
    {
        try
        {
            // The installed manifest only carries LastUpdate if it was present in the
            // repository at the time that version was installed, so on an existing
            // install it reads back as 0 and every entry would land on 1970-01-01.
            // The repository listing we already hold has the real publication time, so
            // consult it before falling back.
            var remoteByName = this.manager.AvailablePlugins
                                   .GroupBy(m => m.InternalName)
                                   .ToDictionary(g => g.Key, g => g.First());

            var entries = this.manager.InstalledPlugins
                              .Where(plugin => !plugin.Manifest.Changelog.IsNullOrEmpty())
                              .Select(plugin => new PluginChangelogEntry(
                                          plugin,
                                          new PluginHistory.PluginVersion
                                          {
                                              Version = plugin.EffectiveVersion.ToString(),
                                              Changelog = plugin.Manifest.Changelog,
                                              PublishedBy = plugin.Manifest.Author,
                                              PublishedAt = ResolvePublishedAt(plugin, remoteByName),
                                          }))
                              .Cast<IChangelogEntry>()
                              .ToList();

            // Installed manifests alone can only ever describe what the user is already
            // running, so the page froze at whatever was installed when the game last
            // started. Anything newer sitting on the repository was invisible until the
            // user updated and restarted. Add those versions too.
            entries.AddRange(BuildAvailableUpdateEntries(this.manager.UpdatablePlugins));

            this.Changelogs = entries;
        }
        catch (Exception ex)
        {
            // Never leave Changelogs null on failure - the window treats null as
            // "still loading" and would sit on the spinner forever.
            Log.Error(ex, "Failed to build the plugin changelog list.");
            this.Changelogs = [];
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Work out when a plugin version was published, preferring real publication
    /// times over anything derived from the local filesystem.
    /// </summary>
    /// <param name="plugin">The installed plugin.</param>
    /// <param name="remoteByName">Repository listing, keyed by internal name.</param>
    /// <returns>The best available publication time.</returns>
    private static DateTime ResolvePublishedAt(
        LocalPlugin plugin, IReadOnlyDictionary<string, RemotePluginManifest> remoteByName)
    {
        // Every branch below must return LOCAL time: the window renders this through
        // DateTimeSpanExtensions, which computes `DateTime.Now - when`. Returning
        // DateTimeOffset.DateTime hands back the UTC value, so the difference is inflated by
        // the machine's UTC offset - eight hours on a TC client, which made this morning's
        // releases read as "13 hours ago". LastWriteTime below is already local, which is
        // why only the LastUpdate branches were wrong.

        // Written into the manifest at install time - accurate, but absent on anything
        // installed before the repository started emitting the field.
        if (plugin.Manifest.LastUpdate > 0)
            return DateTimeOffset.FromUnixTimeSeconds(plugin.Manifest.LastUpdate).LocalDateTime;

        // The repository listing is refreshed on every startup, so this covers installs
        // that predate the field without waiting for the user to update the plugin.
        if (remoteByName.TryGetValue(plugin.Manifest.InternalName, out var remote) &&
            remote.LastUpdate > 0)
            return DateTimeOffset.FromUnixTimeSeconds(remote.LastUpdate).LocalDateTime;

        // Last resort for dev plugins and third-party repositories that publish no
        // timestamp at all. This is when the file arrived rather than when it was
        // published, so it is an approximation - but an ordering that is roughly right
        // beats every entry claiming 1970.
        try
        {
            return plugin.DllFile.LastWriteTime;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read plugin file time: {PluginName}", plugin.Manifest.Name);
            return DateTimeOffset.FromUnixTimeSeconds(0).LocalDateTime;
        }
    }

    /// <summary>
    /// Build changelog entries for versions that exist on the repository but are not the
    /// ones currently installed.
    /// </summary>
    /// <remarks>
    /// This deliberately consumes <see cref="PluginManager.UpdatablePlugins"/> instead of
    /// re-deriving "is this newer" from the raw repository listing. That list is produced by
    /// <c>DetectAvailablePluginUpdates</c>, which already resolves the four things a naive
    /// name-and-version comparison gets wrong: it picks the testing or the stable track per
    /// the user's own setting, drops candidates built for a different API level (offering a
    /// changelog for something the installer would refuse to install is worse than silence),
    /// matches the repository the plugin was actually installed from rather than an arbitrary
    /// same-name entry, and collapses several newer versions down to the highest one.
    ///
    /// That last point is also why no attempt is made to reconstruct intermediate versions:
    /// the repository listing carries only the newest version's changelog, so the history
    /// between the installed version and the newest simply does not exist locally.
    /// </remarks>
    /// <param name="updates">The plugins with an available update.</param>
    /// <returns>One entry per update that carries changelog text.</returns>
    private static IEnumerable<IChangelogEntry> BuildAvailableUpdateEntries(
        IEnumerable<AvailablePluginUpdate> updates)
    {
        foreach (var update in updates)
        {
            // Dev plugins are built locally and the installer refuses to update them, so
            // such a row would advertise an action the user cannot take.
            if (update.InstalledPlugin.IsDev)
                continue;

            var manifest = update.UpdateManifest;

            // Same rule the installed-plugin view uses: the testing track has its own
            // changelog and does NOT fall back to the stable one, because that text
            // describes a different build.
            var changelog = update.UseTesting ? manifest.TestingChangelog : manifest.Changelog;
            if (changelog.IsNullOrEmpty())
                continue;

            // AvailablePluginUpdate.EffectiveVersion throws when the manifest carries no
            // version. Read the field directly instead: a single malformed repository entry
            // must not take the whole changelog list down with it.
            var version = update.UseTesting ? manifest.TestingAssemblyVersion : manifest.AssemblyVersion;
            if (version == null)
                continue;

            yield return new PluginChangelogEntry(
                update.InstalledPlugin,
                new PluginHistory.PluginVersion
                {
                    Version = version.ToString(),
                    Changelog = changelog,
                    PublishedBy = manifest.Author,
                    PublishedAt = ResolveFeedPublishedAt(manifest),
                },
                true);
        }
    }

    /// <summary>
    /// Work out when a repository version was published.
    /// </summary>
    /// <param name="manifest">The repository manifest for the available update.</param>
    /// <returns>The publication time, or <see cref="DateTime.MinValue"/> if unknown.</returns>
    private static DateTime ResolveFeedPublishedAt(RemotePluginManifest manifest)
    {
        // LOCAL time, for the same reason spelled out in ResolvePublishedAt: the window
        // renders this as `DateTime.Now - when`, so handing back a UTC value inflates the
        // difference by the machine's UTC offset - eight hours on a TC client.
        if (manifest.LastUpdate > 0)
        {
            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(manifest.LastUpdate).LocalDateTime;
            }
            catch (ArgumentOutOfRangeException ex)
            {
                // e.g. a repository that publishes milliseconds instead of seconds.
                Log.Warning(
                    ex, "Repository manifest carried an out-of-range timestamp: {PluginName}", manifest.InternalName);
            }
        }

        // Publication time genuinely unknown. DrawChangelog omits the date for MinValue
        // rather than inventing one; the row still carries its "has update" marker.
        return DateTime.MinValue;
    }

    /// <summary>
    /// API response for a history of plugin versions.
    /// </summary>
    internal class PluginHistory
    {
        /// <summary>
        /// Gets or sets the version history of the plugin.
        /// </summary>
        public List<PluginVersion> Versions { get; set; } = null!;

        /// <summary>
        /// A single plugin version.
        /// </summary>
        internal class PluginVersion
        {
#pragma warning disable SA1600
            public string Version { get; set; } = null!;

            public string Dip17Track { get; set; } = null!;

            public string? Changelog { get; set; }

            public DateTime PublishedAt { get; set; }

            public int? PrNumber { get; set; }

            public string? PublishedBy { get; set; }
#pragma warning restore SA1600
        }
    }
}
