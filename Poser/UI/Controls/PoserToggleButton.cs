using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Poser.UI.Effects;

namespace Poser.UI.Controls;

/// <summary>
/// A styled toggle button that switches between two FontAwesome icons.
/// Styled like PoserButton with shadow, gradients, and proper border.
/// </summary>
public static class PoserToggleButton
{
    private const float ButtonSize = 24f;
    private const float ButtonRounding = 4f;

    /// <summary>
    /// Draws a toggle button that switches between two icons.
    /// </summary>
    /// <param name="id">Unique ID for the button.</param>
    /// <param name="value">Current toggle state (ref).</param>
    /// <param name="iconOff">Icon to show when value is false.</param>
    /// <param name="iconOn">Icon to show when value is true.</param>
    /// <param name="tooltip">Optional tooltip text.</param>
    /// <returns>True if value changed.</returns>
    public static bool Draw(string id, ref bool value, FontAwesomeIcon iconOff, FontAwesomeIcon iconOn, string? tooltip = null)
    {
        float scale = PoserUI.Scale;
        float size = ButtonSize * scale;
        float rounding = ButtonRounding * scale;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var buttonPos = cursorScreenPos;
        var buttonEnd = buttonPos + new Vector2(size, size);

        // Handle interaction
        ImGui.InvisibleButton(id, new Vector2(size, size));
        bool clicked = ImGui.IsItemClicked();
        bool isActive = ImGui.IsItemActive() || value; // Treat toggled-on as active
        bool isHovered = ImGui.IsItemHovered();

        if (clicked)
            value = !value;

        // Draw drop shadow
        DrawHelpers.DrawControlShadow(drawList, buttonPos, buttonEnd, ButtonRounding);

        // Get button color based on state
        Vector4 buttonColor;
        if (isActive)
            buttonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
        else if (isHovered)
            buttonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];
        else
            buttonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        buttonColor = UIColors.ApplyAlpha(buttonColor with { W = 1f });
        var buttonColorU32 = ImGui.ColorConvertFloat4ToU32(buttonColor);
        drawList.AddRectFilled(buttonPos, buttonEnd, buttonColorU32, rounding);

        // Add highlight/shadow gradients when not active
        if (!isActive)
        {
            DrawHelpers.DrawButtonGradients(drawList, buttonPos, buttonEnd, size, rounding);
        }

        // Draw border
        drawList.AddRect(buttonPos, buttonEnd, UIColors.ApplyAlpha(UIColors.BorderU32), rounding, ImDrawFlags.None, 1f);

        // Draw current icon (scaled down to 70% of button size)
        var icon = value ? iconOn : iconOff;
        var iconFont = UiBuilder.IconFont;
        var iconStr = icon.ToIconString();

        ImGui.PushFont(iconFont);
        var iconSize = ImGui.CalcTextSize(iconStr);
        float iconFontSize = ImGui.GetFontSize();
        ImGui.PopFont();

        // Scale icon down
        float iconScale = 0.7f;
        var scaledIconSize = iconSize * iconScale;

        // Center the scaled icon in the box
        var iconPos = buttonPos + new Vector2(
            (size - scaledIconSize.X) * 0.5f,
            (size - iconFontSize * iconScale) * 0.5f);

        // Draw icon with outline at smaller scale
        float outlineOffset = 1f * scale;
        DrawHelpers.DrawOutlinedIconScaled(drawList, iconFont, iconPos, iconStr, UIColors.ApplyAlpha(UIColors.BlackU32), UIColors.ApplyAlpha(UIColors.WhiteU32), outlineOffset, iconScale);

        // Show tooltip if provided
        if (isHovered && tooltip != null)
            ImGui.SetTooltip(tooltip);

        return clicked;
    }

    /// <summary>
    /// Gets the button size (scaled).
    /// </summary>
    public static float Size => ButtonSize * PoserUI.Scale;
}
