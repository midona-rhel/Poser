using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Poser.UI;
using Poser.UI.Effects;

namespace Poser.UI.Controls;

/// <summary>
/// A styled checkbox with custom appearance.
/// Square with black outline, default background, white checkmark with black outline.
/// </summary>
public static class PoserCheckbox
{
    // Use Flex constant for standardized sizing
    private static float CheckboxSize => Flex.ControlSize;
    private const float CheckboxRounding = 2f;

    /// <summary>
    /// Draws a styled checkbox.
    /// </summary>
    /// <param name="id">Unique ID for the checkbox.</param>
    /// <param name="value">Current checked state (ref).</param>
    /// <param name="alpha">Optional alpha multiplier (0-1) for transparency. Default is 1.</param>
    /// <returns>True if value changed.</returns>
    public static bool Draw(string id, ref bool value, float alpha = 1f)
    {
        float scale = PoserUI.Scale;
        float size = CheckboxSize * scale;
        float rounding = CheckboxRounding * scale;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var boxPos = cursorScreenPos;
        var boxEnd = boxPos + new Vector2(size, size);

        // Handle interaction (disabled when alpha < 1 typically means disabled)
        ImGui.InvisibleButton(id, new Vector2(size, size));
        bool clicked = ImGui.IsItemClicked() && alpha >= 1f;
        bool isHovered = ImGui.IsItemHovered() && alpha >= 1f;

        if (clicked)
            value = !value;

        // Helper to apply alpha to a color
        uint ApplyAlphaToColor(uint color, float a)
        {
            var vec = ImGui.ColorConvertU32ToFloat4(color);
            vec.W *= a;
            return ImGui.ColorConvertFloat4ToU32(vec);
        }

        // Draw background
        var bgColor = UIColors.ApplyAlpha(isHovered ? UIColors.ControlBackgroundHovered : UIColors.ControlBackground);
        bgColor.W *= alpha;
        var bgColorU32 = ImGui.ColorConvertFloat4ToU32(bgColor);
        drawList.AddRectFilled(boxPos, boxEnd, bgColorU32, rounding);

        // Draw black outline
        drawList.AddRect(boxPos, boxEnd, ApplyAlphaToColor(UIColors.ApplyAlpha(UIColors.BlackU32), alpha), rounding, ImDrawFlags.None, 1f);

        // Draw checkmark if checked
        if (value)
        {
            // Use Font Awesome check icon
            var iconFont = UiBuilder.IconFont;
            var checkIcon = FontAwesomeIcon.Check.ToIconString();

            ImGui.PushFont(iconFont);
            var iconSize = ImGui.CalcTextSize(checkIcon);
            ImGui.PopFont();

            // Center the icon in the box
            var iconPos = boxPos + (new Vector2(size, size) - iconSize) * 0.5f;

            // Draw checkmark with outline
            float outlineOffset = 1f * scale;
            DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, checkIcon,
                ApplyAlphaToColor(UIColors.ApplyAlpha(UIColors.BlackU32), alpha),
                ApplyAlphaToColor(UIColors.ApplyAlpha(UIColors.WhiteU32), alpha),
                outlineOffset);
        }

        return clicked;
    }

    /// <summary>
    /// Gets the checkbox size (scaled).
    /// </summary>
    public static float Size => CheckboxSize * PoserUI.Scale;
}
