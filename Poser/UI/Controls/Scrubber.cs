using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI.Effects;

namespace Poser.UI.Controls;

/// <summary>
/// A scrubber control for numerical values with a draggable thumb on a track.
/// </summary>
public static class Scrubber
{
    private const float TrackHeight = 2f;
    private const float ThumbWidth = 12f;
    private const float ThumbHeight = 24f;
    private const float ThumbRounding = 4f;
    private const float ValueTextGap = 12f;
    private const float TickHeight = 6f;
    private const float TickWidth = 1f;

    /// <summary>
    /// Draws a scrubber control.
    /// </summary>
    /// <param name="id">Unique ID for the control.</param>
    /// <param name="value">Current value (modified if user drags).</param>
    /// <param name="min">Minimum value.</param>
    /// <param name="max">Maximum value.</param>
    /// <param name="step">Step increment for snapping. If 0, no snapping.</param>
    /// <param name="width">Total width including value text. If 0, uses available width.</param>
    /// <param name="displayMultiplier">Multiplier for display value (e.g., 100 for percentage).</param>
    /// <param name="displayFormat">Format string for display (e.g., "F0" for no decimals).</param>
    /// <param name="displaySuffix">Suffix for display (e.g., "%" for percentage).</param>
    /// <returns>True if value was changed.</returns>
    public static bool Draw(string id, ref float value, float min, float max, float step = 0f, float width = 0f,
        float displayMultiplier = 1f, string displayFormat = "F2", string displaySuffix = "")
    {
        bool changed = false;

        float scale = PoserUI.Scale;
        float thumbW = ThumbWidth * scale;
        float thumbH = ThumbHeight * scale;
        float trackH = TrackHeight * scale;
        float rounding = ThumbRounding * scale;
        float gap = ValueTextGap * scale;

        // Use thumb height as control height (no extra padding)
        float controlH = thumbH;

        // Calculate text width based on max value format
        var maxText = (max * displayMultiplier).ToString(displayFormat, CultureInfo.InvariantCulture) + displaySuffix;
        float valueTextW = ImGui.CalcTextSize(maxText).X;

        // Calculate dimensions
        float totalWidth = width > 0 ? width : ImGui.GetContentRegionAvail().X;
        float trackWidth = totalWidth - valueTextW - gap - thumbW;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        // Vertical offset to center within control height
        float thumbOffsetY = 0f;
        float trackOffsetY = (controlH - trackH) / 2f;

        // Draw track with white highlight on bottom
        var trackStart = new Vector2(cursorScreenPos.X + thumbW / 2f, cursorScreenPos.Y + trackOffsetY);
        var trackEnd = new Vector2(cursorScreenPos.X + thumbW / 2f + trackWidth, cursorScreenPos.Y + trackOffsetY + trackH);
        drawList.AddRectFilled(trackStart, trackEnd, UIColors.BorderU32, trackH / 2f);
        // White highlight on bottom of track
        var whiteHighlight = UIColors.White with { W = 0.4f };
        var whiteHighlightU32 = ImGui.ColorConvertFloat4ToU32(whiteHighlight);
        drawList.AddLine(
            new Vector2(trackStart.X, trackEnd.Y),
            new Vector2(trackEnd.X, trackEnd.Y),
            whiteHighlightU32, 1f);

        // Draw tick marks above the track if step is specified
        if (step > 0)
        {
            float tickH = TickHeight * scale;
            float tickW = TickWidth * scale;
            float tickY = trackStart.Y - tickH - 2f * scale; // 2px gap above track

            for (float v = min; v <= max + step * 0.5f; v += step)
            {
                float tickT = Math.Clamp((v - min) / (max - min), 0f, 1f);
                float tickX = trackStart.X + tickT * trackWidth;
                drawList.AddRectFilled(
                    new Vector2(tickX - tickW / 2f, tickY),
                    new Vector2(tickX + tickW / 2f, tickY + tickH),
                    UIColors.BorderU32);
            }
        }

        // Calculate thumb position based on value
        float t = Math.Clamp((value - min) / (max - min), 0f, 1f);
        float thumbX = cursorScreenPos.X + t * trackWidth;
        var thumbPos = new Vector2(thumbX, cursorScreenPos.Y + thumbOffsetY);
        var thumbEnd = thumbPos + new Vector2(thumbW, thumbH);

        // Handle interaction - only on track area (with some hit margin)
        float hitMargin = 4f * scale;
        var trackHitStart = new Vector2(trackStart.X - thumbW / 2f, trackStart.Y - hitMargin);
        var trackHitSize = new Vector2(trackWidth + thumbW, trackH + hitMargin * 2);
        ImGui.SetCursorScreenPos(trackHitStart);
        ImGui.InvisibleButton(id, trackHitSize);
        bool isActive = ImGui.IsItemActive();

        // Advance cursor properly for next widget
        ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(0, controlH));

        // Draw drop shadow behind thumb using control shadow helper
        DrawHelpers.DrawControlShadow(drawList, thumbPos, thumbEnd, ThumbRounding);

        // Draw thumb with button styling (force full opacity)
        var buttonColor = isActive ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive] : ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        buttonColor.W = 1f;
        var buttonColorU32 = ImGui.ColorConvertFloat4ToU32(buttonColor);
        drawList.AddRectFilled(thumbPos, thumbEnd, buttonColorU32, rounding);

        // Add highlight/shadow gradients when not active
        if (!isActive)
        {
            float gradientHeight = thumbH * 0.28f;
            float inset = rounding * 0.75f;

            // Top highlight: white 12.5% opacity fading to transparent
            var whiteTop = UIColors.White with { W = 0.125f };
            var whiteTopU32 = ImGui.ColorConvertFloat4ToU32(whiteTop);
            var transparentWhite = UIColors.White with { W = 0f };
            var transparentWhiteU32 = ImGui.ColorConvertFloat4ToU32(transparentWhite);
            drawList.AddRectFilledMultiColor(
                thumbPos + new Vector2(inset, 0),
                new Vector2(thumbEnd.X - inset, thumbPos.Y + gradientHeight),
                whiteTopU32, whiteTopU32, transparentWhiteU32, transparentWhiteU32);

            // Bottom shadow: black 12.5% opacity fading to transparent
            var blackBottom = UIColors.Black with { W = 0.125f };
            var blackBottomU32 = ImGui.ColorConvertFloat4ToU32(blackBottom);
            var transparentBlack = UIColors.Black with { W = 0f };
            var transparentBlackU32 = ImGui.ColorConvertFloat4ToU32(transparentBlack);
            drawList.AddRectFilledMultiColor(
                new Vector2(thumbPos.X + inset, thumbEnd.Y - gradientHeight),
                thumbEnd - new Vector2(inset, 0),
                transparentBlackU32, transparentBlackU32, blackBottomU32, blackBottomU32);
        }

        // Draw border
        drawList.AddRect(thumbPos, thumbEnd, UIColors.BorderU32, rounding, ImDrawFlags.None, 1f);

        // Draw value text to the right, vertically centered
        var valueText = (value * displayMultiplier).ToString(displayFormat, CultureInfo.InvariantCulture) + displaySuffix;
        float textOffsetY = (controlH - ImGui.GetTextLineHeight()) / 2f;
        var textPos = new Vector2(cursorScreenPos.X + trackWidth + thumbW + gap, cursorScreenPos.Y + textOffsetY);
        drawList.AddText(textPos, UIColors.TextU32, valueText);

        if (isActive)
        {
            float mouseX = ImGui.GetMousePos().X;
            float newT = Math.Clamp((mouseX - cursorScreenPos.X - thumbW / 2f) / trackWidth, 0f, 1f);
            float newValue = min + newT * (max - min);

            // Snap to step if specified
            if (step > 0)
            {
                newValue = MathF.Round(newValue / step) * step;
                newValue = Math.Clamp(newValue, min, max);
            }

            if (Math.Abs(newValue - value) > 0.0001f)
            {
                value = newValue;
                changed = true;
            }
        }

        return changed;
    }
}
