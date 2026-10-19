namespace Dalamud.Interface.Internal.Windows.PluginInstaller;

/// <summary>A reusable character buffer, for text built from parts every frame. Not thread-safe.</summary>
internal sealed class TextBuffer
{
    private char[] chars = new char[256];
    private int length;

    /// <summary>Gets the text built since the last <see cref="Clear"/>; valid until the buffer is changed.</summary>
    public ReadOnlySpan<char> Text => this.chars.AsSpan(0, this.length);

    /// <summary>Empties the buffer.</summary>
    /// <returns>This buffer.</returns>
    public TextBuffer Clear()
    {
        this.length = 0;
        return this;
    }

    /// <summary>Appends text; null appends nothing.</summary>
    /// <param name="text">The text.</param>
    /// <returns>This buffer.</returns>
    public TextBuffer Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return this;

        if (this.length + text.Length > this.chars.Length)
            Array.Resize(ref this.chars, Math.Max(this.chars.Length * 2, this.length + text.Length));
        text.CopyTo(0, this.chars, this.length, text.Length);
        this.length += text.Length;
        return this;
    }
}
