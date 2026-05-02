using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Effects;

namespace Poser.UI.Controls;

/// <summary>
/// A minimal icon toggle - just an icon with outline, no background or border.
/// States:
/// - Inactive: dimmed icon with outline
/// - Inactive + Hover: brighter icon
/// - Active: full white icon with outline
/// </summary>
public static class IconToggle
{
    /// <summary>
    /// Gets the toggle size (scaled). Uses Flex.LargeIconSize.
    /// </summary>
    public static float Size => Flex.LargeIconSize * PoserUI.Scale;

    /// <summary>
    /// Draws an icon toggle.
    /// </summary>
    /// <param name="id">Unique ID for the toggle.</param>
    /// <param name="value">Current toggle state (ref).</param>
    /// <param name="icon">FontAwesome icon to display.</param>
    /// <param name="tooltip">Optional tooltip text.</param>
    /// <returns>True if value changed.</returns>
    public static bool Draw(string id, ref bool value, FontAwesomeIcon icon, string? tooltip = null)
    {
        float scale = PoserUI.Scale;
        float size = Flex.LargeIconSize * scale;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        // Handle interaction
        ImGui.InvisibleButton(id, new Vector2(size, size));
        bool clicked = ImGui.IsItemClicked();
        bool isHovered = ImGui.IsItemHovered();

        if (clicked)
            value = !value;

        // No background, no border - just the icon

        // Get icon font and string
        var iconFont = UiBuilder.IconFont;
        var iconStr = icon.ToIconString();

        ImGui.PushFont(iconFont);
        var iconTextSize = ImGui.CalcTextSize(iconStr);
        ImGui.PopFont();

        // Center icon in the button area
        var iconPos = cursorScreenPos + new Vector2(
            (size - iconTextSize.X) * 0.5f,
            (size - iconTextSize.Y) * 0.5f);

        float outlineOffset = 1f * scale;

        // Draw icon based on state - all states have outline
        if (value)
        {
            // Active: white icon with black outline
            DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, iconStr,
                UIColors.ApplyAlpha(UIColors.BlackU32),
                UIColors.ApplyAlpha(UIColors.WhiteU32),
                outlineOffset);
        }
        else if (isHovered)
        {
            // Inactive + Hover: brighter icon with outline
            var hoverColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.8f, 0.8f, 0.8f, 0.8f));
            DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, iconStr,
                UIColors.ApplyAlpha(UIColors.BlackU32),
                hoverColor,
                outlineOffset);
        }
        else
        {
            // Inactive: dimmed icon with outline
            var dimmedColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.5f, 0.5f, 0.5f, 0.5f));
            DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, iconStr,
                UIColors.ApplyAlpha(UIColors.BlackU32),
                dimmedColor,
                outlineOffset);
        }

        // Show tooltip if provided
        if (isHovered && tooltip != null)
            ImGui.SetTooltip(tooltip);

        return clicked;
    }
}
