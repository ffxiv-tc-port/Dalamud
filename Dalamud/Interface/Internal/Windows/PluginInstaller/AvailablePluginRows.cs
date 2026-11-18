using System.Collections.Generic;
using System.Runtime.InteropServices;

using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;

namespace Dalamud.Interface.Internal.Windows.PluginInstaller;

/// <summary>
/// The rows of the installer's "all plugins" page, and their grouping by repository, in buffers reused every frame.
/// <see cref="Build"/> gives the rows, in the order, that GatherProxies' LINQ gave; <see cref="GroupByRepository"/>
/// the groups and order that <c>GroupBy(repository).OrderBy(key)</c> gave. Draw thread only.
/// </summary>
/// <remarks>Call <see cref="Clear"/> once the frame has drawn them: held rows would keep removed plugins alive.</remarks>
internal sealed class AvailablePluginRows
{
    // The text the repository view shows for a group of rows that has neither an installed nor a remote repository.
    private const string DevPluginGroupKey = "开发版插件";

    private readonly List<RemotePluginManifest> filtered = [];
    private readonly List<PluginManifest> inCategory = [];
    private readonly List<LocalPlugin> unmatched = [];
    private readonly List<Row> rows = [];
    private readonly StableStringKeySorter<Row> rowSorter = new();

    private readonly List<Row> groupedRows = [];
    private readonly List<Group> groups = [];
    private readonly StableStringKeySorter<Group> groupSorter = new();
    private readonly Dictionary<string, int> groupOfKey = new();
    private readonly List<int> groupOfRow = [];
    private readonly List<int> groupStart = [];

    /// <summary>Gets the rows <see cref="Build"/> made, in display order.</summary>
    public List<Row> Rows => this.rows;

    /// <summary>Gets the rows ordered group by group, as <see cref="Groups"/> index them.</summary>
    public List<Row> GroupedRows => this.groupedRows;

    /// <summary>Gets the repository groups, in display order.</summary>
    public List<Group> Groups => this.groups;

    /// <summary>
    /// Fills <see cref="Rows"/>: every available manifest that passes the filter and is in the current category,
    /// paired with its installed non-dev plugin, then every other installed plugin that passes the filter; kept or
    /// dropped by whether its manifest is hidden, then ordered by the repository it came from.
    /// </summary>
    /// <param name="available">The available manifests; not empty.</param>
    /// <param name="installed">The installed plugins.</param>
    /// <param name="isFiltered">Whether the search filters a manifest out.</param>
    /// <param name="categories">The category manager; its content is read for the current category.</param>
    /// <param name="hiddenNames">The internal names the user hid.</param>
    public void Build(
        List<RemotePluginManifest> available,
        List<LocalPlugin> installed,
        Func<IPluginManifest, bool> isFiltered,
        PluginCategoryManager categories,
        List<string> hiddenNames)
    {
        this.Clear();

        // The copy is taken first, as ToList() did; matched plugins are removed from it one by one.
        this.unmatched.AddRange(installed);

        foreach (var manifest in available)
        {
            if (!isFiltered(manifest))
                this.filtered.Add(manifest);
        }

        if (this.filtered.Count == 0)
            return;

        categories.FillCurrentCategoryContent(this.filtered, this.inCategory);
        foreach (var manifest in this.inCategory)
        {
            var availableManifest = (RemotePluginManifest)manifest;
            var plugin = PluginInstallerLookup.FindInstalledNonDev(installed, availableManifest);
            if (plugin != null)
                this.unmatched.Remove(plugin);

            this.rows.Add(new Row(availableManifest, plugin));
        }

        // Installed plugins no manifest claimed, mostly dev or orphaned ones.
        foreach (var installedPlugin in this.unmatched)
        {
            if (isFiltered(installedPlugin.Manifest))
                continue;

            // TODO: We should also check categories here, for good measure
            this.rows.Add(new Row(null, installedPlugin));
        }

        // Where(IsProxyHidden): on the hidden page only hidden rows stay, elsewhere only the others.
        var showHidden = categories.CurrentCategoryKind == PluginCategoryManager.CategoryKind.Hidden;
        var kept = 0;
        var span = CollectionsMarshal.AsSpan(this.rows);
        for (var i = 0; i < span.Length; i++)
        {
            var isHidden = hiddenNames.Contains(span[i].RemoteManifest?.InternalName!);
            if (isHidden == showHidden)
                span[kept++] = span[i];
        }

        this.rows.RemoveRange(kept, this.rows.Count - kept);
        this.rowSorter.Sort(this.rows, static row => row.LocalPlugin != null
                                                         ? row.LocalPlugin.Manifest.InstalledFromUrl
                                                         : row.RemoteManifest != null
                                                             ? row.RemoteManifest.SourceRepo.PluginMasterUrl
                                                             : string.Empty);
    }

    /// <summary>Fills <see cref="Groups"/> and <see cref="GroupedRows"/> from <see cref="Rows"/>.</summary>
    public void GroupByRepository()
    {
        this.ClearGroups();

        // Groups numbered in order of first appearance, as GroupBy makes them; a null key is a group of its own.
        var nullGroup = -1;
        foreach (var row in this.rows)
        {
            var key = GroupKey(row);
            int group;
            if (key == null)
            {
                if (nullGroup < 0)
                    nullGroup = this.AddGroup(null);
                group = nullGroup;
            }
            else if (!this.groupOfKey.TryGetValue(key, out group))
            {
                group = this.AddGroup(key);
                this.groupOfKey.Add(key, group);
            }

            this.groupOfRow.Add(group);
            this.groups[group] = this.groups[group] with { Count = this.groups[group].Count + 1 };
        }

        // OrderBy(g => g.Key) is stable: equal keys keep their first-appearance order.
        this.groupSorter.Sort(this.groups, static group => group.Key);

        CollectionsMarshal.SetCount(this.groupStart, this.groups.Count);
        var start = 0;
        for (var i = 0; i < this.groups.Count; i++)
        {
            var group = this.groups[i];
            this.groups[i] = group with { Start = start };
            this.groupStart[group.Id] = start;
            start += group.Count;
        }

        CollectionsMarshal.SetCount(this.groupedRows, this.rows.Count);
        var grouped = CollectionsMarshal.AsSpan(this.groupedRows);
        var next = CollectionsMarshal.AsSpan(this.groupStart);
        var rowSpan = CollectionsMarshal.AsSpan(this.rows);
        for (var i = 0; i < rowSpan.Length; i++)
            grouped[next[this.groupOfRow[i]]++] = rowSpan[i];
    }

    /// <summary>Empties every buffer, letting go of the plugins and manifests it held.</summary>
    public void Clear()
    {
        this.filtered.Clear();
        this.inCategory.Clear();
        this.unmatched.Clear();
        this.rows.Clear();
        this.ClearGroups();
    }

    private static string? GroupKey(Row row)
    {
        if (row.LocalPlugin is { IsDev: false })
            return row.LocalPlugin.Manifest.InstalledFromUrl;
        if (row.RemoteManifest != null)
            return row.RemoteManifest.SourceRepo.PluginMasterUrl;
        return DevPluginGroupKey;
    }

    private int AddGroup(string? key)
    {
        this.groups.Add(new Group(key, this.groups.Count, 0, 0));
        return this.groups.Count - 1;
    }

    private void ClearGroups()
    {
        this.groupedRows.Clear();
        this.groups.Clear();
        this.groupOfKey.Clear();
        this.groupOfRow.Clear();
        this.groupStart.Clear();
    }

    /// <summary>One row: an available manifest, an installed plugin, or both.</summary>
    /// <param name="RemoteManifest">The available manifest, if any.</param>
    /// <param name="LocalPlugin">The installed plugin, if any.</param>
    internal readonly record struct Row(RemotePluginManifest? RemoteManifest, LocalPlugin? LocalPlugin);

    /// <summary>The rows of one repository.</summary>
    /// <param name="Key">The repository the rows are grouped by; may be null or empty.</param>
    /// <param name="Id">Its number in order of first appearance.</param>
    /// <param name="Start">Its first row in <see cref="GroupedRows"/>.</param>
    /// <param name="Count">How many rows it has.</param>
    internal readonly record struct Group(string? Key, int Id, int Start, int Count);
}
