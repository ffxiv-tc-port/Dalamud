using System.Numerics;

using Dalamud.Bindings.ImGui;

namespace Dalamud.Interface.Components;

/// <summary>
/// Class containing various methods providing ImGui components.
/// </summary>
public static partial class ImGuiComponents
{
    /// <summary>
    /// Alpha modified IconButton component to use an icon as a button with alpha and color options.
    /// </summary>
    /// <param name="icon">The icon for the button.</param>
    /// <param name="id">The ID of the button.</param>
    /// <param name="defaultColor">The default color of the button.</param>
    /// <param name="activeColor">The color of the button when active.</param>
    /// <param name="hoveredColor">The color of the button when hovered.</param>
    /// <param name="alphaMult">A multiplier for the current alpha levels.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool DisabledButton(FontAwesomeIcon icon, int? id = null, Vector4? defaultColor = null, Vector4? activeColor = null, Vector4? hoveredColor = null, float alphaMult = .5f)
    {
        PushFontTracked(UiBuilder.IconFont);
        try
        {
            Span<char> label = stackalloc char[IntLabelLength];
            if (!id.HasValue)
            {
                label[0] = icon.ToIconChar();
                return DisabledButtonCore(label[..1], defaultColor, activeColor, hoveredColor, alphaMult);
            }

            return TryWriteIconLabel(icon, id.Value, label, out var length)
                       ? DisabledButtonCore(label[..length], defaultColor, activeColor, hoveredColor, alphaMult)
                       : DisabledButtonCore($"{icon.ToIconString()}##{id}", defaultColor, activeColor, hoveredColor, alphaMult);
        }
        finally
        {
            PopFontTracked();
        }
    }

    /// <summary>
    /// Alpha modified Button component to use as a disabled button with alpha and color options.
    /// </summary>
    /// <param name="labelWithId">The button label with ID.</param>
    /// <param name="defaultColor">The default color of the button.</param>
    /// <param name="activeColor">The color of the button when active.</param>
    /// <param name="hoveredColor">The color of the button when hovered.</param>
    /// <param name="alphaMult">A multiplier for the current alpha levels.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool DisabledButton(string labelWithId, Vector4? defaultColor = null, Vector4? activeColor = null, Vector4? hoveredColor = null, float alphaMult = .5f)
        => DisabledButtonCore(labelWithId, defaultColor, activeColor, hoveredColor, alphaMult);

    private static bool DisabledButtonCore(ReadOnlySpan<char> labelWithId, Vector4? defaultColor, Vector4? activeColor, Vector4? hoveredColor, float alphaMult)
    {
        var colorCount = 0;
        try
        {
            PushButtonColorsTracked(defaultColor, activeColor, hoveredColor, ref colorCount);

            var style = ImGui.GetStyle();

            PushStyleTracked(ImGuiStyleVar.Alpha, style.Alpha * alphaMult);
            try
            {
                return ImGui.Button(labelWithId);
            }
            finally
            {
                PopStyleTracked();
            }
        }
        finally
        {
            PopColorsTracked(colorCount);
        }
    }
}
