using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI.Effects;

namespace Poser.UI.Controls;

/// <summary>
/// A styled button matching the scrubber thumb appearance.
/// </summary>
public static class PoserButton
{
    private const float ButtonHeight = 24f;
    private const float ButtonRounding = 4f;
    private const float ButtonPaddingX = 12f;

    /// <summary>
    /// Draws a styled button.
    /// </summary>
    /// <param name="id">Unique ID for the button.</param>
    /// <param name="label">Button text.</param>
    /// <returns>True if button was clicked.</returns>
    public static bool Draw(string id, string label)
    {
        float scale = PoserUI.Scale;
        float height = ButtonHeight * scale;
        float rounding = ButtonRounding * scale;
        float paddingX = ButtonPaddingX * scale;

        // Calculate button width based on text
        var textSize = ImGui.CalcTextSize(label);
        float width = textSize.X + paddingX * 2;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        var buttonPos = cursorScreenPos;
        var buttonEnd = buttonPos + new Vector2(width, height);

        // Handle interaction first
        ImGui.InvisibleButton(id, new Vector2(width, height));
        bool isActive = ImGui.IsItemActive();
        bool isHovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked();

        // Draw drop shadow using control shadow helper (20% opacity, 50% shorter)
        DrawHelpers.DrawControlShadow(drawList, buttonPos, buttonEnd, ButtonRounding);

        // Get button color based on state
        Vector4 buttonColor;
        if (isActive)
            buttonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
        else if (isHovered)
            buttonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonHovered];
        else
            buttonColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        buttonColor.W = 1f;
        var buttonColorU32 = ImGui.ColorConvertFloat4ToU32(buttonColor);
        drawList.AddRectFilled(buttonPos, buttonEnd, buttonColorU32, rounding);

        // Add highlight/shadow gradients when not active
        if (!isActive)
        {
            DrawHelpers.DrawButtonGradients(drawList, buttonPos, buttonEnd, height, rounding);
        }

        // Draw border
        drawList.AddRect(buttonPos, buttonEnd, UIColors.BorderU32, rounding, ImDrawFlags.None, 1f);

        // Draw text centered
        var textPos = buttonPos + (new Vector2(width, height) - textSize) * 0.5f;
        drawList.AddText(textPos, UIColors.TextU32, label);

        return clicked;
    }

    /// <summary>
    /// Draws a right-aligned styled button.
    /// </summary>
    /// <param name="id">Unique ID for the button.</param>
    /// <param name="label">Button text.</param>
    /// <returns>True if button was clicked.</returns>
    public static bool DrawRightAligned(string id, string label)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float paddingX = ButtonPaddingX * scale;

        // Calculate button width
        var textSize = ImGui.CalcTextSize(label);
        float width = textSize.X + paddingX * 2;

        // Move cursor to right-align
        float availableWidth = ImGui.GetContentRegionAvail().X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + availableWidth - width);

        return Draw(id, label);
    }
}
