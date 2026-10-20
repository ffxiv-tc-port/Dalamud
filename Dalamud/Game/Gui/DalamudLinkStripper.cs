using Dalamud.Game.Text.SeStringHandling;

using Lumina.Text;
using Lumina.Text.Payloads;

namespace Dalamud.Game.Gui;

/// <summary>
/// Removes Dalamud's chat links from SeString bytes, keeping the text they wrap.
/// </summary>
/// <remarks>
/// <para>For lines printed to the game's chat log while ChatGui's HandleLinkClick hook is missing: a click on such a
/// link would then reach the game's own handler instead of Dalamud's.</para>
/// <para>A link counts as Dalamud's when its type is <see cref="DalamudLinkType"/>, is not a plain number, or does not
/// fit the byte the game keeps it in. Other links stay byte for byte unless one of those starts inside them. Never
/// throws, also on malformed input.</para>
/// </remarks>
internal static class DalamudLinkStripper
{
    /// <summary>The link type of <see cref="Text.SeStringHandling.Payloads.DalamudLinkPayload"/>.</summary>
    internal const uint DalamudLinkType = (uint)Payload.EmbeddedInfoType.DalamudLink - 1;

    private const uint TerminatorType = (uint)LinkMacroPayloadType.Terminator;
    private const byte Stx = 0x02;
    private const byte Etx = 0x03;

    /// <summary>
    /// Removes every Dalamud link: at top level the link and the terminator that closes it, the text between them
    /// stays; a macro with one starting anywhere inside it (header, nested strings) is removed whole.
    /// </summary>
    /// <param name="data">The SeString, without its null terminator.</param>
    /// <returns>Null when nothing was removed; otherwise the remaining bytes and a null terminator.</returns>
    internal static byte[]? Strip(ReadOnlySpan<byte> data)
    {
        var result = StripOnce(data);

        // A start byte that opened no macro may open one once a payload after it is gone; every pass removes at
        // least one payload, so this ends.
        while (result != null && StripOnce(result.AsSpan(0, result.Length - 1)) is { } again)
            result = again;

        return result;
    }

    /// <summary>
    /// Gets a value indicating whether a link <see cref="Strip"/> removes starts at any offset of
    /// <paramref name="data"/>, whatever payload that offset belongs to. Nothing does in what Strip returns.
    /// </summary>
    /// <param name="data">The bytes to look at.</param>
    /// <returns>Whether such a link is there.</returns>
    internal static bool ContainsDalamudLink(ReadOnlySpan<byte> data) => HasLinkStartIn(data, 0, data.Length);

    private static byte[]? StripOnce(ReadOnlySpan<byte> data)
    {
        // Removed payloads are at least four bytes long, so an equal count means nothing was removed.
        var kept = Filter(data, default);
        if (kept == data.Length)
            return null;

        var result = new byte[kept + 1];
        Filter(data, result);
        return result;
    }

    // Counts the kept bytes, and copies them when output is not empty. Both calls decide identically.
    private static int Filter(ReadOnlySpan<byte> data, Span<byte> output)
    {
        var kept = 0;
        var dropNextTerminator = false;
        var pos = 0;
        while (pos < data.Length)
        {
            var keep = true;
            if (TryReadMacro(data[pos..], out var code, out var body, out var length))
            {
                var hidesLink = HasLinkStartIn(data, pos + 1, length - 1);
                if (code != MacroCode.Link)
                {
                    keep = !hidesLink;
                }
                else if (IsTerminator(body))
                {
                    keep = !dropNextTerminator && !hidesLink;
                    dropNextTerminator = false;
                }
                else
                {
                    // Only a terminator that comes before any other link is this link's own.
                    keep = !OpensDalamudLink(body) && !hidesLink;
                    dropNextTerminator = !keep;
                }
            }
            else
            {
                // Text, up to the next start byte; a start byte that opens no macro is text as well.
                var next = data[(pos + 1)..].IndexOf(Stx);
                length = next < 0 ? data.Length - pos : next + 1;
            }

            if (keep)
            {
                if (!output.IsEmpty)
                    data.Slice(pos, length).CopyTo(output[kept..]);
                kept += length;
            }

            pos += length;
        }

        return kept;
    }

    // A link starting at an offset in the range is read up to the end of data, as a parser that lost step would.
    private static bool HasLinkStartIn(ReadOnlySpan<byte> data, int start, int count)
    {
        for (var i = start; i < start + count; i++)
        {
            if (data[i] == Stx &&
                TryReadMacro(data[i..], out var code, out var body, out _) &&
                code == MacroCode.Link &&
                OpensDalamudLink(body))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTerminator(ReadOnlySpan<byte> linkBody)
        => SeExpressionUtilities.TryDecodeUInt(linkBody, out var type, out _) && type == TerminatorType;

    private static bool OpensDalamudLink(ReadOnlySpan<byte> linkBody)
        => !SeExpressionUtilities.TryDecodeUInt(linkBody, out var type, out _) ||
           type == DalamudLinkType ||
           type > byte.MaxValue;

    // Same shape as Lumina's payload enumerator, without its throw on a negative or overflowing body length.
    private static bool TryReadMacro(ReadOnlySpan<byte> data, out MacroCode code, out ReadOnlySpan<byte> body, out int length)
    {
        code = default;
        body = default;
        length = 0;
        if (data.Length < 4 || data[0] != Stx)
            return false;

        if (!SeExpressionUtilities.TryDecodeInt(data[2..], out var bodyLength, out var lengthLength) ||
            bodyLength < 0 ||
            bodyLength > data.Length - 3 - lengthLength)
        {
            return false;
        }

        length = 3 + lengthLength + bodyLength;
        if (data[length - 1] != Etx)
            return false;

        code = (MacroCode)data[1];
        body = data.Slice(2 + lengthLength, bodyLength);
        return true;
    }
}
