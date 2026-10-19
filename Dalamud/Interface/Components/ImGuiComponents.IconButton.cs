using System.Numerics;

using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Dalamud.Interface.Components;

/// <summary>
/// Class containing various methods providing ImGui components.
/// </summary>
public static partial class ImGuiComponents
{
    /// <summary>
    /// IconButton component to use an icon as a button.
    /// </summary>
    /// <param name="icon">The icon for the button.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(FontAwesomeIcon icon) => IconButton(icon, null);

    /// <summary>
    /// IconButton component to use an icon as a button.
    /// </summary>
    /// <param name="icon">The icon for the button.</param>
    /// <param name="size">Sets the size of the button. If either dimension is set to 0, that dimension will conform to the size of the icon.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(FontAwesomeIcon icon, Vector2 size) => IconButton(icon, null, null, null, size);

    /// <summary>
    /// IconButton component to use an icon as a button.
    /// </summary>
    /// <param name="icon">The icon for the button.</param>
    /// <param name="defaultColor">The default color of the button.</param>
    /// <param name="activeColor">The color of the button when active.</param>
    /// <param name="hoveredColor">The color of the button when hovered.</param>
    /// <param name="size">Sets the size of the button. If either dimension is set to 0, that dimension will conform to the size of the icon.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(FontAwesomeIcon icon, Vector4? defaultColor, Vector4? activeColor = null, Vector4? hoveredColor = null, Vector2? size = null)
    {
        var iconChar = icon.ToIconChar();
        ReadOnlySpan<char> iconText = new(in iconChar);
        return IconButtonCore(iconText, iconText.IndexOf('#'), defaultColor, activeColor, hoveredColor, size);
    }

    /// <summary>
    /// IconButton component to use an icon as a button.
    /// </summary>
    /// <param name="id">The ID of the button.</param>
    /// <param name="icon">The icon for the button.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(int id, FontAwesomeIcon icon) => IconButton(id, icon, null);

    /// <summary>
    /// IconButton component to use an icon as a button.
    /// </summary>
    /// <param name="id">The ID of the button.</param>
    /// <param name="icon">The icon for the button.</param>
    /// <param name="size">Sets the size of the button. If either dimension is set to 0, that dimension will conform to the size of the icon.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(int id, FontAwesomeIcon icon, Vector2 size) => IconButton(id, icon, null, null, null, size);

    /// <summary>
    /// IconButton component to use an icon as a button with color options.
    /// </summary>
    /// <param name="id">The ID of the button.</param>
    /// <param name="icon">The icon for the button.</param>
    /// <param name="defaultColor">The default color of the button.</param>
    /// <param name="activeColor">The color of the button when active.</param>
    /// <param name="hoveredColor">The color of the button when hovered.</param>
    /// <param name="size">Sets the size of the button. If either dimension is set to 0, that dimension will conform to the size of the icon.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(int id, FontAwesomeIcon icon, Vector4? defaultColor, Vector4? activeColor = null, Vector4? hoveredColor = null, Vector2? size = null)
    {
        Span<char> label = stackalloc char[IntLabelLength];
        if (!TryWriteIconLabel(icon, id, label, out var length))
            return IconButton($"{icon.ToIconString()}##{id}", defaultColor, activeColor, hoveredColor, size);

        ReadOnlySpan<char> iconText = label[..length];
        return IconButtonCore(iconText, iconText.IndexOf('#'), defaultColor, activeColor, hoveredColor, size);
    }

    /// <summary>
    /// IconButton component to use an icon as a button.
    /// </summary>
    /// <param name="id">The ID of the button.</param>
    /// <param name="icon">The icon for the button.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(string id, FontAwesomeIcon icon) => IconButton(id, icon, null);

    /// <summary>
    /// IconButton component to use an icon as a button.
    /// </summary>
    /// <param name="id">The ID of the button.</param>
    /// <param name="icon">The icon for the button.</param>
    /// <param name="size">Sets the size of the button. If either dimension is set to 0, that dimension will conform to the size of the icon.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(string id, FontAwesomeIcon icon, Vector2 size)
        => IconButton(id, icon, null, null, null, size);

    /// <summary>
    /// IconButton component to use an icon as a button with color options.
    /// </summary>
    /// <param name="id">The ID of the button.</param>
    /// <param name="icon">The icon for the button.</param>
    /// <param name="defaultColor">The default color of the button.</param>
    /// <param name="activeColor">The color of the button when active.</param>
    /// <param name="hoveredColor">The color of the button when hovered.</param>
    /// <param name="size">Sets the size of the button. If either dimension is set to 0, that dimension will conform to the size of the icon.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(string id, FontAwesomeIcon icon, Vector4? defaultColor, Vector4? activeColor = null, Vector4? hoveredColor = null, Vector2? size = null)
    {
        if ((id?.Length ?? 0) > MaxStackIdLength)
            return IconButton($"{icon.ToIconString()}##{id}", defaultColor, activeColor, hoveredColor, size);

        Span<char> label = stackalloc char[3 + MaxStackIdLength];
        ReadOnlySpan<char> iconText = label[..WriteIconLabel(icon, id, label)];
        return IconButtonCore(iconText, iconText.IndexOf('#'), defaultColor, activeColor, hoveredColor, size);
    }

    /// <summary>
    /// IconButton component to use an icon as a button.
    /// </summary>
    /// <param name="iconText">Text already containing the icon string.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(string iconText) => IconButton(iconText, null);

    /// <summary>
    /// IconButton component to use an icon as a button.
    /// </summary>
    /// <param name="iconText">Text already containing the icon string.</param>
    /// <param name="size">Sets the size of the button. If either dimension is set to 0, that dimension will conform to the size of the icon.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(string iconText, Vector2 size) => IconButton(iconText, null, null, null, size);

    /// <summary>
    /// IconButton component to use an icon as a button with color options.
    /// </summary>
    /// <param name="iconText">Text already containing the icon string.</param>
    /// <param name="defaultColor">The default color of the button.</param>
    /// <param name="activeColor">The color of the button when active.</param>
    /// <param name="hoveredColor">The color of the button when hovered.</param>
    /// <param name="size">Sets the size of the button. If either dimension is set to 0, that dimension will conform to the size of the icon.</param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButton(string iconText, Vector4? defaultColor, Vector4? activeColor = null, Vector4? hoveredColor = null, Vector2? size = null)
        => IconButtonCore(iconText, iconText.IndexOf('#'), defaultColor, activeColor, hoveredColor, size);

    /// <summary>
    /// IconButton component to use an icon as a button with color options.
    /// </summary>
    /// <param name="icon">Icon to show.</param>
    /// <param name="text">Text to show.</param>
    /// <param name="size">
    /// Sets the size of the button. If either dimension is set to 0,
    /// that dimension will conform to the size of the icon and text.
    /// </param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButtonWithText(FontAwesomeIcon icon, string text, Vector2 size) => IconButtonWithText(icon, text, null, null, null, size);

    /// <summary>
    /// IconButton component to use an icon as a button with color options.
    /// </summary>
    /// <param name="icon">Icon to show.</param>
    /// <param name="text">Text to show.</param>
    /// <param name="defaultColor">The default color of the button.</param>
    /// <param name="activeColor">The color of the button when active.</param>
    /// <param name="hoveredColor">The color of the button when hovered.</param>
    /// <param name="size">
    /// Sets the size of the button. If either dimension is set to 0,
    /// that dimension will conform to the size of the icon and text.
    /// </param>
    /// <returns>Indicator if button is clicked.</returns>
    public static bool IconButtonWithText(FontAwesomeIcon icon, string text, Vector4? defaultColor = null, Vector4? activeColor = null, Vector4? hoveredColor = null, Vector2? size = null)
    {
        var colorCount = 0;
        try
        {
            PushButtonColorsTracked(defaultColor, activeColor, hoveredColor, ref colorCount);

            if (size.HasValue)
            {
                size *= ImGuiHelpers.GlobalScale;
            }

            bool button;

            var iconChar = icon.ToIconChar();
            ReadOnlySpan<char> iconText = new(in iconChar);

            Vector2 iconSize;
            PushFontTracked(UiBuilder.IconFont);
            try
            {
                iconSize = ImGui.CalcTextSize(iconText);
            }
            finally
            {
                PopFontTracked();
            }

            var hashIndex = text.IndexOf('#');
            var textStr = hashIndex >= 0 ? text.AsSpan(0, hashIndex) : text.AsSpan();

            var framePadding = ImGui.GetStyle().FramePadding;
            var iconPadding = 3 * ImGuiHelpers.GlobalScale;

            var cursor = ImGui.GetCursorScreenPos();

            ImGui.PushID(text);
            try
            {
                var textSize = ImGui.CalcTextSize(textStr);

                var width = size is { X: not 0 } ? size.Value.X : iconSize.X + textSize.X + (framePadding.X * 2) + iconPadding;
                var height = size is { Y: not 0 } ? size.Value.Y : ImGui.GetFrameHeight();

                button = ImGui.Button(string.Empty, new Vector2(width, height));
            }
            finally
            {
                ImGui.PopID();
            }

            var iconPos = cursor + framePadding;
            var textPos = new Vector2(iconPos.X + iconSize.X + iconPadding, cursor.Y + framePadding.Y);

            var dl = ImGui.GetWindowDrawList();

            PushFontTracked(UiBuilder.IconFont);
            try
            {
                dl.AddText(iconPos, ImGui.GetColorU32(ImGuiCol.Text), iconText);
            }
            finally
            {
                PopFontTracked();
            }

            dl.AddText(textPos, ImGui.GetColorU32(ImGuiCol.Text), textStr);

            return button;
        }
        finally
        {
            PopColorsTracked(colorCount);
        }
    }

    /// <summary>
    /// Get width of IconButtonWithText component.
    /// </summary>
    /// <param name="icon">Icon to use.</param>
    /// <param name="text">Text to use.</param>
    /// <returns>Width.</returns>
    public static float GetIconButtonWithTextWidth(FontAwesomeIcon icon, string text)
    {
        var iconChar = icon.ToIconChar();
        Vector2 iconSize;
        PushFontTracked(UiBuilder.IconFont);
        try
        {
            iconSize = ImGui.CalcTextSize(new ReadOnlySpan<char>(in iconChar));
        }
        finally
        {
            PopFontTracked();
        }

        var textSize = ImGui.CalcTextSize(text);
        var iconPadding = 3 * ImGuiHelpers.GlobalScale;
        return iconSize.X + textSize.X + (ImGui.GetStyle().FramePadding.X * 2) + iconPadding;
    }

    /// <summary>Draws an icon button; <paramref name="iconText"/> is the ID and its text before <paramref name="hashIndex"/> is the icon.</summary>
    private static bool IconButtonCore(ReadOnlySpan<char> iconText, int hashIndex, Vector4? defaultColor, Vector4? activeColor, Vector4? hoveredColor, Vector2? size)
    {
        var colorCount = 0;
        try
        {
            PushButtonColorsTracked(defaultColor, activeColor, hoveredColor, ref colorCount);

            if (size.HasValue)
            {
                size *= ImGuiHelpers.GlobalScale;
            }

            var icon = hashIndex >= 0 ? iconText[..hashIndex] : iconText;

            bool button;

            PushFontTracked(UiBuilder.IconFont);
            try
            {
                var iconSize = ImGui.CalcTextSize(icon);
                var cursor = ImGui.GetCursorScreenPos();

                var width = size is { X: not 0 } ? size.Value.X : iconSize.X + (ImGui.GetStyle().FramePadding.X * 2);
                var height = size is { Y: not 0 } ? size.Value.Y : ImGui.GetFrameHeight();

                var buttonSize = new Vector2(width, height);

                ImGui.PushID(iconText);
                try
                {
                    button = ImGui.Button(string.Empty, buttonSize);
                }
                finally
                {
                    ImGui.PopID();
                }

                var iconPos = cursor + ((buttonSize - iconSize) / 2f);

                ImGui.GetWindowDrawList().AddText(iconPos, ImGui.GetColorU32(ImGuiCol.Text), icon);
            }
            finally
            {
                PopFontTracked();
            }

            return button;
        }
        finally
        {
            PopColorsTracked(colorCount);
        }
    }
}
