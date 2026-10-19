using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Dalamud.Interface.Internal.Windows.PluginInstaller;

/// <summary>
/// Text the plugin installer draws for every row, every frame, kept until what it is made of changes.
/// Draw thread only, except <see cref="InvalidateLocalized"/>.
/// </summary>
/// <remarks>
/// Localized parts are only read again after <see cref="InvalidateLocalized"/>: every read of a localized string
/// walks the stack to find the calling assembly and allocates.
/// </remarks>
internal sealed class PluginRowTextCache
{
    private readonly ConditionalWeakTable<object, Row> rows = new();
    private readonly bool cacheLocalized;
    private int localizedGeneration;

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginRowTextCache"/> class.
    /// </summary>
    /// <param name="cacheLocalized">False when nothing will call <see cref="InvalidateLocalized"/>; then every call builds anew.</param>
    public PluginRowTextCache(bool cacheLocalized)
    {
        this.cacheLocalized = cacheLocalized;
    }

    /// <summary>
    /// What <c>description.Split('\r', '\n')[0]</c> cut to <paramref name="maxLength"/> characters gives, without copying.
    /// </summary>
    /// <param name="description">The description.</param>
    /// <param name="maxLength">The most characters to keep.</param>
    /// <returns>The first line.</returns>
    public static ReadOnlySpan<char> FirstLine(string description, int maxLength)
    {
        var line = description.AsSpan();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0)
            line = line[..end];
        return line.Length < maxLength ? line : line[..maxLength];
    }

    /// <summary>Makes every localized text be built again on its next use. Any thread.</summary>
    public void InvalidateLocalized() => Interlocked.Increment(ref this.localizedGeneration);

    /// <summary>The row's title: <paramref name="build"/>'s result for this name and these modifiers.</summary>
    /// <param name="row">What the row shows (the plugin or the manifest); only held weakly.</param>
    /// <param name="name">The plugin's name.</param>
    /// <param name="modifiers">Which title modifiers apply.</param>
    /// <param name="build">Builds the title; must depend on nothing but its arguments and the language.</param>
    /// <returns>The title.</returns>
    public string GetLabel(object row, string name, int modifiers, Func<string, int, string> build)
    {
        var generation = Volatile.Read(ref this.localizedGeneration);
        var cached = this.rows.GetOrCreateValue(row);
        if (!this.cacheLocalized || cached.Label == null || !ReferenceEquals(cached.LabelName, name) ||
            cached.LabelModifiers != modifiers || cached.LabelGeneration != generation)
        {
            cached.Label = build(name, modifiers);
            cached.LabelName = name;
            cached.LabelModifiers = modifiers;
            cached.LabelGeneration = generation;
        }

        return cached.Label;
    }

    /// <summary>The row's author line: <paramref name="build"/>'s result for this author and download count.</summary>
    /// <param name="row">What the row shows (the plugin or the manifest); only held weakly.</param>
    /// <param name="author">The author.</param>
    /// <param name="downloadCount">The download count.</param>
    /// <param name="build">Builds the line in the current culture; must depend on nothing but its arguments, the language and the culture.</param>
    /// <returns>The author line.</returns>
    public string GetAuthorLine(object row, string author, long downloadCount, Func<string, long, string> build)
    {
        var generation = Volatile.Read(ref this.localizedGeneration);
        var culture = CultureInfo.CurrentCulture;
        var cached = this.rows.GetOrCreateValue(row);
        if (!this.cacheLocalized || cached.Author == null || !ReferenceEquals(cached.AuthorName, author) ||
            cached.AuthorDownloadCount != downloadCount || !ReferenceEquals(cached.AuthorCulture, culture) ||
            cached.AuthorGeneration != generation)
        {
            cached.Author = build(author, downloadCount);
            cached.AuthorName = author;
            cached.AuthorDownloadCount = downloadCount;
            cached.AuthorCulture = culture;
            cached.AuthorGeneration = generation;
        }

        return cached.Author;
    }

    /// <summary>A localized text that is the same for every row.</summary>
    /// <param name="slot">Where it is kept; one slot per text.</param>
    /// <param name="build">Reads the text.</param>
    /// <returns>The text.</returns>
    public string GetShared(ref SharedText slot, Func<string> build)
    {
        var generation = Volatile.Read(ref this.localizedGeneration);
        if (!this.cacheLocalized || slot.Text == null || slot.Generation != generation)
            slot = new SharedText(build(), generation);

        return slot.Text!;
    }

    /// <summary>A cached text and the localization it was read under.</summary>
    /// <param name="Text">The text, or null before the first read.</param>
    /// <param name="Generation">The localization generation it was read under.</param>
    internal readonly record struct SharedText(string? Text, int Generation);

    private sealed class Row
    {
        public string? Label { get; set; }

        public string? LabelName { get; set; }

        public int LabelModifiers { get; set; }

        public int LabelGeneration { get; set; }

        public string? Author { get; set; }

        public string? AuthorName { get; set; }

        public long AuthorDownloadCount { get; set; }

        public CultureInfo? AuthorCulture { get; set; }

        public int AuthorGeneration { get; set; }
    }
}
