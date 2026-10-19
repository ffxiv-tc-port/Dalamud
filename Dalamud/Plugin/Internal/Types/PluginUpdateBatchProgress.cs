using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Dalamud.Plugin.Internal.Types;

/// <summary>
/// Tracks which plugins of one update batch are done, for progress displays.
/// </summary>
/// <remarks>
/// <see cref="Report"/> runs synchronously on whichever thread finished a plugin, possibly several at once;
/// any thread may read. The member set is fixed at construction.
/// </remarks>
internal sealed class PluginUpdateBatchProgress : IProgress<PluginManager.PluginUpdateProgress>
{
    private readonly HashSet<string> members;
    private readonly ConcurrentDictionary<string, byte> processed = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginUpdateBatchProgress"/> class.
    /// </summary>
    /// <param name="plugins">The updates that make up the batch.</param>
    public PluginUpdateBatchProgress(ICollection<AvailablePluginUpdate> plugins)
    {
        this.members = plugins.Select(x => x.UpdateManifest.InternalName).ToHashSet();
        this.Total = plugins.Count;
    }

    /// <summary>
    /// Gets the number of plugins in the batch, counted the same way as <see cref="PluginManager.PluginUpdateProgress.TotalPlugins"/>.
    /// </summary>
    public int Total { get; }

    /// <summary>
    /// Gets a value indicating whether a plugin belongs to this batch.
    /// </summary>
    /// <param name="internalName">The internal name of the plugin.</param>
    /// <returns>Whether the plugin is part of the batch.</returns>
    public bool Contains(string internalName) => this.members.Contains(internalName);

    /// <summary>
    /// Takes a snapshot of the plugins that have finished, whether they succeeded or not.
    /// </summary>
    /// <returns>The internal names of the finished plugins.</returns>
    public HashSet<string> SnapshotProcessed() => this.processed.Keys.ToHashSet();

    /// <inheritdoc/>
    public void Report(PluginManager.PluginUpdateProgress value)
        => this.processed.TryAdd(value.CurrentPluginManifest.InternalName, 0);
}
