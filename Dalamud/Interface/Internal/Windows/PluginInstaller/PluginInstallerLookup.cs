using System.Collections.Generic;
using System.Runtime.InteropServices;

using Dalamud.Configuration.Internal;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Plugin.Internal.Types.Manifest;

namespace Dalamud.Interface.Internal.Windows.PluginInstaller;

/// <summary>
/// The lookups the plugin installer does for every row it draws, as plain loops so a row allocates nothing.
/// Each returns what the LINQ call it replaces returned: the first match in list order, with the same predicate.
/// </summary>
internal static class PluginInstallerLookup
{
    /// <summary>
    /// The update offered for <paramref name="plugin"/>; plugins are compared by reference.
    /// </summary>
    /// <param name="updates">The updatable plugins.</param>
    /// <param name="plugin">The installed plugin.</param>
    /// <returns>The first matching update, or null.</returns>
    public static AvailablePluginUpdate? FindUpdate(List<AvailablePluginUpdate> updates, LocalPlugin plugin)
    {
        foreach (var update in CollectionsMarshal.AsSpan(updates))
        {
            if (update.InstalledPlugin == plugin)
                return update;
        }

        return null;
    }

    /// <summary>
    /// The repository manifest with this internal name and repository URL.
    /// </summary>
    /// <param name="manifests">The available manifests.</param>
    /// <param name="internalName">The internal name.</param>
    /// <param name="repoUrl">The repository URL.</param>
    /// <returns>The first matching manifest, or null.</returns>
    public static RemotePluginManifest? FindManifest(List<RemotePluginManifest> manifests, string internalName, string? repoUrl)
    {
        foreach (var manifest in CollectionsMarshal.AsSpan(manifests))
        {
            if (manifest.InternalName == internalName && manifest.RepoUrl == repoUrl)
                return manifest;
        }

        return null;
    }

    /// <summary>
    /// Whether a manifest with this internal name offers a testing version.
    /// </summary>
    /// <param name="manifests">The available manifests.</param>
    /// <param name="internalName">The internal name.</param>
    /// <returns>Whether any manifest matches.</returns>
    public static bool HasTestingAvailable(List<RemotePluginManifest> manifests, string internalName)
    {
        foreach (var manifest in CollectionsMarshal.AsSpan(manifests))
        {
            if (manifest.InternalName == internalName && manifest.IsAvailableForTesting)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The testing opt-in for this internal name.
    /// </summary>
    /// <param name="optIns">The opt-ins; null finds nothing.</param>
    /// <param name="internalName">The internal name.</param>
    /// <returns>The first matching opt-in, or null.</returns>
    public static PluginTestingOptIn? FindTestingOptIn(List<PluginTestingOptIn>? optIns, string internalName)
    {
        if (optIns == null)
            return null;

        foreach (var optIn in CollectionsMarshal.AsSpan(optIns))
        {
            if (optIn.InternalName == internalName)
                return optIn;
        }

        return null;
    }

    /// <summary>
    /// The result of the last update for this internal name.
    /// </summary>
    /// <param name="statuses">The update results.</param>
    /// <param name="internalName">The internal name.</param>
    /// <returns>The first matching result, or null.</returns>
    public static PluginUpdateStatus? FindUpdateStatus(List<PluginUpdateStatus> statuses, string internalName)
    {
        foreach (var status in CollectionsMarshal.AsSpan(statuses))
        {
            if (status.InternalName == internalName)
                return status;
        }

        return null;
    }

    /// <summary>
    /// The installed non-dev plugin that came from this manifest.
    /// </summary>
    /// <param name="plugins">The installed plugins.</param>
    /// <param name="manifest">The available manifest.</param>
    /// <returns>The first matching plugin, or null.</returns>
    public static LocalPlugin? FindInstalledNonDev(List<LocalPlugin> plugins, RemotePluginManifest manifest)
    {
        foreach (var plugin in CollectionsMarshal.AsSpan(plugins))
        {
            if (plugin.Manifest.InternalName == manifest.InternalName &&
                plugin.Manifest.RepoUrl == manifest.RepoUrl &&
                !plugin.IsDev)
            {
                return plugin;
            }
        }

        return null;
    }

    /// <summary>
    /// The category info of this kind.
    /// </summary>
    /// <param name="categories">The category list.</param>
    /// <param name="kind">The category kind.</param>
    /// <returns>The first matching category.</returns>
    /// <exception cref="InvalidOperationException">No category is of this kind, as with <c>Enumerable.First</c>.</exception>
    public static PluginCategoryManager.CategoryInfo FindCategory(PluginCategoryManager.CategoryInfo[] categories, PluginCategoryManager.CategoryKind kind)
    {
        foreach (var category in categories)
        {
            if (category.CategoryKind == kind)
                return category;
        }

        throw new InvalidOperationException("Sequence contains no matching element");
    }
}
