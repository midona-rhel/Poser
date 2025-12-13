using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Poser.UI.Effects;

namespace Poser.UI.Controls;

/// <summary>
/// A styled text input control matching the Poser UI style.
/// </summary>
public static class PoserTextInput
{
    private const float InputHeight = 24f;
    private const float Rounding = 4f;
    private const float IconWidth = 24f;

    /// <summary>
    /// Draws a styled text input with optional search icon.
    /// </summary>
    /// <param name="id">Unique ID for the input.</param>
    /// <param name="text">Current text value (ref).</param>
    /// <param name="placeholder">Placeholder text when empty.</param>
    /// <param name="width">Width of the input. If 0, uses available width.</param>
    /// <param name="showSearchIcon">Whether to show a search icon on the left.</param>
    /// <param name="maxLength">Maximum text length.</param>
    /// <returns>True if text changed.</returns>
    public static bool Draw(string id, ref string text, string placeholder = "", float width = 0f, bool showSearchIcon = false, uint maxLength = 256)
    {
        bool changed = false;
        float scale = PoserUI.Scale;
        float height = InputHeight * scale;
        float rounding = Rounding * scale;
        float iconW = showSearchIcon ? IconWidth * scale : 0f;

        float totalWidth = width > 0 ? width : ImGui.GetContentRegionAvail().X;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var inputPos = cursorScreenPos;
        var inputEnd = inputPos + new Vector2(totalWidth, height);

        // Draw control shadow
        DrawHelpers.DrawControlShadow(drawList, inputPos, inputEnd, Rounding);

        // Draw background
        var bgColor = UIColors.ApplyAlpha(UIColors.ControlBackground);
        var bgColorU32 = ImGui.ColorConvertFloat4ToU32(bgColor);
        drawList.AddRectFilled(inputPos, inputEnd, bgColorU32, rounding);

        // Draw border
        var borderColor = UIColors.ApplyAlpha(UIColors.BorderU32);
        drawList.AddRect(inputPos, inputEnd, borderColor, rounding, ImDrawFlags.None, 1f);

        // Draw search icon if enabled
        if (showSearchIcon)
        {
            var iconFont = UiBuilder.IconFont;
            var searchIcon = FontAwesomeIcon.Search.ToIconString();

            ImGui.PushFont(iconFont);
            var iconSize = ImGui.CalcTextSize(searchIcon);
            ImGui.PopFont();

            var iconPos = new Vector2(
                inputPos.X + (iconW - iconSize.X) / 2f,
                inputPos.Y + (height - iconSize.Y) / 2f);

            drawList.AddText(iconFont, ImGui.GetFontSize(), iconPos, UIColors.ApplyAlpha(UIColors.TextDisabledU32), searchIcon);
        }

        // Calculate text input area
        float textPadding = 8f * scale;
        float textX = inputPos.X + iconW + textPadding;
        float textWidth = totalWidth - iconW - textPadding * 2;

        // Position cursor for ImGui input
        ImGui.SetCursorScreenPos(new Vector2(textX, inputPos.Y));

        // Style the input to be transparent (we draw our own background)
        using (ImRaii.PushStyle(ImGuiStyleVar.FramePadding, new Vector2(0, (height - ImGui.GetTextLineHeight()) / 2f)))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 0f))
        using (ImRaii.PushStyle(ImGuiStyleVar.FrameBorderSize, 0f))
        using (ImRaii.PushColor(ImGuiCol.FrameBg, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.Text, UIColors.ApplyAlpha(UIColors.Text)))
        using (ImRaii.PushColor(ImGuiCol.TextDisabled, UIColors.ApplyAlpha(UIColors.TextDisabled)))
        {
            ImGui.SetNextItemWidth(textWidth);

            // Show placeholder as disabled text if empty
            bool showPlaceholder = string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(placeholder);

            if (ImGui.InputText($"##{id}", ref text, (int)maxLength, ImGuiInputTextFlags.None))
            {
                changed = true;
            }

            // Draw placeholder text manually if input is empty and not focused
            if (showPlaceholder && !ImGui.IsItemActive())
            {
                var placeholderPos = new Vector2(textX, inputPos.Y + (height - ImGui.GetTextLineHeight()) / 2f);
                drawList.AddText(placeholderPos, UIColors.ApplyAlpha(UIColors.TextDisabledU32), placeholder);
            }
        }

        // Advance cursor
        ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(0, height));

        return changed;
    }

    /// <summary>
    /// Gets the input height (scaled).
    /// </summary>
    public static float Height => InputHeight * PoserUI.Scale;
}
