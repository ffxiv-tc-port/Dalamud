using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Json;
using System.Threading.Tasks;

using Dalamud.Networking.Http;
using Dalamud.Plugin.Internal;
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
            this.Changelogs = this.manager.InstalledPlugins
                                  .Where(plugin => !plugin.Manifest.Changelog.IsNullOrEmpty())
                                  .Select(plugin => new PluginChangelogEntry(plugin))
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
