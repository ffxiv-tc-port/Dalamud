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

            this.Changelogs = this.manager.InstalledPlugins
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
        // Written into the manifest at install time - accurate, but absent on anything
        // installed before the repository started emitting the field.
        if (plugin.Manifest.LastUpdate > 0)
            return DateTimeOffset.FromUnixTimeSeconds(plugin.Manifest.LastUpdate).DateTime;

        // The repository listing is refreshed on every startup, so this covers installs
        // that predate the field without waiting for the user to update the plugin.
        if (remoteByName.TryGetValue(plugin.Manifest.InternalName, out var remote) &&
            remote.LastUpdate > 0)
            return DateTimeOffset.FromUnixTimeSeconds(remote.LastUpdate).DateTime;

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
            return DateTimeOffset.FromUnixTimeSeconds(0).DateTime;
        }
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
