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
    private const float CheckboxSize = 18f;
    private const float CheckboxRounding = 2f;

    /// <summary>
    /// Draws a styled checkbox.
    /// </summary>
    /// <param name="id">Unique ID for the checkbox.</param>
    /// <param name="value">Current checked state (ref).</param>
    /// <returns>True if value changed.</returns>
    public static bool Draw(string id, ref bool value)
    {
        float scale = PoserUI.Scale;
        float size = CheckboxSize * scale;
        float rounding = CheckboxRounding * scale;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var boxPos = cursorScreenPos;
        var boxEnd = boxPos + new Vector2(size, size);

        // Handle interaction
        ImGui.InvisibleButton(id, new Vector2(size, size));
        bool clicked = ImGui.IsItemClicked();
        bool isHovered = ImGui.IsItemHovered();

        if (clicked)
            value = !value;

        // Draw background
        var bgColor = UIColors.ApplyAlpha(isHovered ? UIColors.ControlBackgroundHovered : UIColors.ControlBackground);
        var bgColorU32 = ImGui.ColorConvertFloat4ToU32(bgColor);
        drawList.AddRectFilled(boxPos, boxEnd, bgColorU32, rounding);

        // Draw black outline
        drawList.AddRect(boxPos, boxEnd, UIColors.ApplyAlpha(UIColors.BlackU32), rounding, ImDrawFlags.None, 1f);

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
            DrawHelpers.DrawOutlinedIcon(drawList, iconFont, iconPos, checkIcon, UIColors.ApplyAlpha(UIColors.BlackU32), UIColors.ApplyAlpha(UIColors.WhiteU32), outlineOffset);
        }

        return clicked;
    }

    /// <summary>
    /// Gets the checkbox size (scaled).
    /// </summary>
    public static float Size => CheckboxSize * PoserUI.Scale;
}
