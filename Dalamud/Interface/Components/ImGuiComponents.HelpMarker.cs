using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Common.Math;

namespace Dalamud.Interface.Components;

/// <summary>
/// Class containing various methods providing ImGui components.
/// </summary>
public static partial class ImGuiComponents
{
    /// <summary>
    /// HelpMarker component to add a help icon with text on hover.
    /// </summary>
    /// <param name="helpText">The text to display on hover.</param>
    public static void HelpMarker(string helpText) => HelpMarker(helpText, FontAwesomeIcon.InfoCircle);

    /// <summary>
    /// HelpMarker component to add a custom icon with text on hover.
    /// </summary>
    /// <param name="helpText">The text to display on hover.</param>
    /// <param name="icon">The icon to use.</param>
    /// <param name="color">The color of the icon.</param>
    public static void HelpMarker(string helpText, FontAwesomeIcon icon, Vector4? color = null)
    {
        var colorCount = 0;
        try
        {
            if (color.HasValue)
            {
                PushColorTracked(ImGuiCol.TextDisabled, color.Value, ref colorCount);
            }

            ImGui.SameLine();

            var iconChar = icon.ToIconChar();
            PushFontTracked(UiBuilder.IconFont);
            try
            {
                ImGui.TextDisabled(new ReadOnlySpan<char>(in iconChar));
            }
            finally
            {
                PopFontTracked();
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.BeginTooltip();
                try
                {
                    ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35.0f);
                    try
                    {
                        ImGui.Text(helpText);
                    }
                    finally
                    {
                        ImGui.PopTextWrapPos();
                    }
                }
                finally
                {
                    ImGui.EndTooltip();
                }
            }
        }
        finally
        {
            PopColorsTracked(colorCount);
        }
    }
}
