using System.Numerics;

using Dalamud.Configuration.Internal;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Internal.Types;
using Dalamud.Utility;

using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Dalamud.Game.Gui.Dtr;

/// <summary>
/// Interface representing a read-only entry in the server info bar.
/// </summary>
public interface IReadOnlyDtrBarEntry
{
    /// <summary>
    /// Gets the title of this entry.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets a value indicating whether this entry has a click action.
    /// </summary>
    public bool HasClickAction { get; }

    /// <summary>
    /// Gets the text of this entry.
    /// </summary>
    public SeString? Text { get; }

    /// <summary>
    /// Gets a tooltip to be shown when the user mouses over the dtr entry.
    /// </summary>
    public SeString? Tooltip { get; }

    /// <summary>
    /// Gets a value indicating whether this entry should be shown.
    /// </summary>
    public bool Shown { get; }

    /// <summary>
    /// Gets a value indicating whether the user has hidden this entry from view through the Dalamud settings.
    /// </summary>
    public bool UserHidden { get; }

    /// <summary>
    /// Gets an action to be invoked when the user clicks on the dtr entry.
    /// </summary>
    public Action<DtrInteractionEvent>? OnClick { get; }

    /// <summary>
    /// Gets the axis-aligned bounding box of this entry, in screen coordinates.
    /// </summary>
    public (Vector2 Min, Vector2 Max) ScreenBounds { get; }
}

/// <summary>
/// Interface representing an entry in the server info bar.
/// </summary>
public interface IDtrBarEntry : IReadOnlyDtrBarEntry
{
    /// <summary>
    /// Gets or sets the text of this entry.
    /// </summary>
    public new SeString? Text { get; set; }

    /// <summary>
    /// Gets or sets a tooltip to be shown when the user mouses over the dtr entry.
    /// </summary>
    public new SeString? Tooltip { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this entry is visible.
    /// </summary>
    public new bool Shown { get; set; }

    /// <summary>
    /// Gets or sets an action to be invoked when the user clicks on the dtr entry.
    /// </summary>
    public new Action<DtrInteractionEvent>? OnClick { get; set; }

    /// <summary>
    /// Remove this entry from the bar.
    /// You will need to re-acquire it from DtrBar to reuse it.
    /// </summary>
    public void Remove();
}

/// <summary>
/// Class representing an entry in the server info bar.
/// </summary>
internal sealed unsafe class DtrBarEntry : IDisposable, IDtrBarEntry
{
    private readonly DalamudConfiguration configuration;

    private bool shownBacking = true;
    private SeString? textBacking;

    /// <summary>
    /// Initializes a new instance of the <see cref="DtrBarEntry"/> class.
    /// </summary>
    /// <param name="configuration">Dalamud configuration, used to check if the entry is hidden by the user.</param>
    /// <param name="title">The title of the bar entry.</param>
    /// <param name="textNode">The corresponding text node.</param>
    internal DtrBarEntry(DalamudConfiguration configuration, string title, AtkTextNode* textNode)
    {
        this.configuration = configuration;
        this.Title = title;
        this.TextNode = textNode;
    }

    /// <inheritdoc/>
    public string Title { get; init; }

    /// <inheritdoc cref="IDtrBarEntry.Text" />
    public SeString? Text
    {
        get => this.textBacking;
        set
        {
            this.textBacking = value;
            this.Dirty = true;
        }
    }

    /// <inheritdoc cref="IDtrBarEntry.Tooltip" />
    public SeString? Tooltip { get; set; }

    /// <inheritdoc/>
    public Action<DtrInteractionEvent>? OnClick { get; set; }

    /// <inheritdoc/>
    public bool HasClickAction => this.OnClick != null;

    /// <inheritdoc cref="IDtrBarEntry.Shown" />
    public bool Shown
    {
        get => this.shownBacking;
        set
        {
            if (value != this.shownBacking)
            {
                this.shownBacking = value;
                this.Dirty = true;
            }
        }
    }

    /// <inheritdoc/>
    [Api13ToDo("Maybe make this config scoped to internal name?")]
    public bool UserHidden => this.configuration.DtrIgnore?.Contains(this.Title) ?? false;

    /// <inheritdoc/>
    public (Vector2 Min, Vector2 Max) ScreenBounds
        => this.TextNode switch
        {
            null => default,
            var node => node->IsVisible()
                            ? (new(node->ScreenX, node->ScreenY),
                                  new(node->ScreenX + node->GetWidth(), node->ScreenY + node->GetHeight()))
                            : default,
        };

    /// <summary>
    /// Gets or sets the internal text node of this entry.
    /// </summary>
    internal AtkTextNode* TextNode { get; set; }

    /// <summary>
    /// Gets or sets the storage for the text of this entry.
    /// </summary>
    internal Utf8String* Storage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this entry should be removed.
    /// </summary>
    internal bool ShouldBeRemoved { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this entry is dirty.
    /// </summary>
    internal bool Dirty { get; set; }

    /// <summary>
    /// Gets or sets the encoded text, null terminator included, that <see cref="TextNode"/> currently shows.
    /// Null when the node has not been given this entry's text, which forces the next write.
    /// </summary>
    internal byte[]? AppliedText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this entry has just been added.
    /// </summary>
    internal bool Added { get; set; }

    /// <summary>
    /// Gets or sets the plugin that owns this entry.
    /// </summary>
    internal LocalPlugin? OwnerPlugin { get; set; }

    /// <summary>
    /// Remove this entry from the bar.
    /// You will need to re-acquire it from DtrBar to reuse it.
    /// </summary>
    public void Remove()
    {
        this.ShouldBeRemoved = true;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Remove();
    }

    /// <summary>
    /// Checks whether <paramref name="text"/> encodes to exactly <paramref name="applied"/>, without building the encoding.
    /// </summary>
    /// <param name="text">The text to check.</param>
    /// <param name="applied">An encoding with its null terminator, as written to the node.</param>
    /// <returns>Whether writing <paramref name="text"/> would write the same bytes again.</returns>
    internal static bool EncodesTo(SeString text, ReadOnlySpan<byte> applied)
    {
        if (applied.IsEmpty || applied[^1] != 0)
            return false;

        var body = applied[..^1];
        var offset = 0;
        foreach (var payload in text.Payloads)
        {
            var bytes = payload.Encode();
            if (bytes.Length > body.Length - offset || !body.Slice(offset, bytes.Length).SequenceEqual(bytes))
                return false;

            offset += bytes.Length;
        }

        return offset == body.Length;
    }

    /// <summary>
    /// Encodes <paramref name="text"/> for the node, unless the node already shows exactly that.
    /// </summary>
    /// <param name="text">The text to show.</param>
    /// <returns>The encoding to write, null terminator included, or null when nothing needs writing.</returns>
    /// <remarks>
    /// Compares bytes, not instances: a plugin may change the payloads of the instance it set and set it again.
    /// </remarks>
    internal byte[]? EncodeIfChanged(SeString text)
    {
        if (this.AppliedText is { } applied && EncodesTo(text, applied))
            return null;

        return text.EncodeWithNullTerminator();
    }
}
