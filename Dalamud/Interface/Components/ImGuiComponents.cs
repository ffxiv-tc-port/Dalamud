using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace Dalamud.Interface.Components;

/// <summary>
/// Class containing various methods providing ImGui components.
/// </summary>
public static partial class ImGuiComponents
{
    // "<icon>##<int>" always fits; if formatting still fails, the caller falls back to the heap string.
    private const int IntLabelLength = 64;

    // "<icon>##<string id>" is built on the stack up to this many id chars; longer ids fall back to the heap string.
    private const int MaxStackIdLength = 253;

    // The *Tracked helpers do what ImRaii.Color / Font / Style Push and Pop do, including the static bookkeeping
    // read by ImRaii.DefaultColors / DefaultFont / DefaultStyle, without allocating the ImRaii object.
    // Callers pop in a finally, using the count recorded at push time.
    private static void PushColorTracked(ImGuiCol idx, Vector4 color, ref int count)
    {
        ImRaii.Color.Stack.Add((idx, ImGui.GetColorU32(idx)));
        ImGui.PushStyleColor(idx, color);
        ++count;
    }

    private static void PushButtonColorsTracked(Vector4? defaultColor, Vector4? activeColor, Vector4? hoveredColor, ref int count)
    {
        if (defaultColor.HasValue)
            PushColorTracked(ImGuiCol.Button, defaultColor.Value, ref count);

        if (activeColor.HasValue)
            PushColorTracked(ImGuiCol.ButtonActive, activeColor.Value, ref count);

        if (hoveredColor.HasValue)
            PushColorTracked(ImGuiCol.ButtonHovered, hoveredColor.Value, ref count);
    }

    private static void PopColorsTracked(int count)
    {
        ImGui.PopStyleColor(count);
        ImRaii.Color.Stack.RemoveRange(ImRaii.Color.Stack.Count - count, count);
    }

    private static void PushFontTracked(ImFontPtr font)
    {
        if (ImRaii.Font.FontPushCounter++ == 0)
            ImRaii.Font.DefaultPushed = ImGui.GetFont();
        ImGui.PushFont(font);
    }

    private static void PopFontTracked()
    {
        ImRaii.Font.FontPushCounter -= 1;
        ImGui.PopFont();
    }

    private static void PushStyleTracked(ImGuiStyleVar idx, float value)
    {
        ImRaii.Style.Stack.Add((idx, ImRaii.Style.GetStyle(idx)));
        ImGui.PushStyleVar(idx, value);
    }

    private static void PopStyleTracked()
    {
        ImGui.PopStyleVar(1);
        ImRaii.Style.Stack.RemoveRange(ImRaii.Style.Stack.Count - 1, 1);
    }

    /// <summary>Writes the chars of <c>$"{icon.ToIconString()}##{id}"</c> (current culture, as the interpolation uses).</summary>
    private static bool TryWriteIconLabel(FontAwesomeIcon icon, int id, Span<char> destination, out int length)
    {
        destination[0] = icon.ToIconChar();
        destination[1] = '#';
        destination[2] = '#';
        if (id.TryFormat(destination[3..], out var written))
        {
            length = 3 + written;
            return true;
        }

        length = 0;
        return false;
    }

    /// <summary>Writes the chars of <c>$"{icon.ToIconString()}##{id}"</c>; a null id writes nothing after the "##".</summary>
    private static int WriteIconLabel(FontAwesomeIcon icon, string? id, Span<char> destination)
    {
        destination[0] = icon.ToIconChar();
        destination[1] = '#';
        destination[2] = '#';
        id.AsSpan().CopyTo(destination[3..]);
        return 3 + (id?.Length ?? 0);
    }
}
