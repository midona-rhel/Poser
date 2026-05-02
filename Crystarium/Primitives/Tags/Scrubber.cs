using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Track-and-thumb scrubber with drag math.
    /// Returns true if value changed.
    /// </summary>
    public static bool Scrubber(ElementProps props, ref float value, float min, float max,
        float step = 0f,
        float displayMultiplier = 1f, string displayFormat = "F2", string displaySuffix = "",
        bool hideValue = false)
    {
        Stylesheet.EnsureInitialized();

        bool changed = false;
        float scale = PoserUI.Scale;
        float thumbW = 12f * scale;
        float thumbH = Flex.RowHeight * scale;
        float trackH = 2f * scale;
        float rounding = 4f * scale;
        float gap = Flex.ItemGap * scale;

        float controlH = thumbH;

        float valueTextW = 0f;
        if (!hideValue)
        {
            var maxText = (max * displayMultiplier).ToString(displayFormat, CultureInfo.InvariantCulture) + displaySuffix;
            valueTextW = ImGui.CalcTextSize(maxText).X;
        }

        float totalWidth = ResolveAvailableWidth(props.Style.Width);
        float trackWidth = hideValue
            ? totalWidth - thumbW
            : totalWidth - valueTextW - gap - thumbW - gap;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        float trackOffsetY = (controlH - trackH) / 2f;
        var trackStart = new Vector2(cursorScreenPos.X + thumbW / 2f, cursorScreenPos.Y + trackOffsetY);
        var trackEnd = new Vector2(cursorScreenPos.X + thumbW / 2f + trackWidth, cursorScreenPos.Y + trackOffsetY + trackH);
        drawList.AddRectFilled(trackStart, trackEnd, UIColors.ApplyAlpha(UIColors.BorderU32), trackH / 2f);

        var whiteHighlight = UIColors.ApplyAlpha(UIColors.White with { W = 0.4f });
        drawList.AddLine(
            new Vector2(trackStart.X, trackEnd.Y),
            new Vector2(trackEnd.X, trackEnd.Y),
            ImGui.ColorConvertFloat4ToU32(whiteHighlight), 1f);

        // Tick marks above the track when step > 0
        if (step > 0)
        {
            float tickH = 6f * scale;
            float tickW = 1f * scale;
            float tickY = trackStart.Y - tickH - 2f * scale;
            for (float v = min; v <= max + step * 0.5f; v += step)
            {
                float tickT = Math.Clamp((v - min) / (max - min), 0f, 1f);
                float tickX = trackStart.X + tickT * trackWidth;
                drawList.AddRectFilled(
                    new Vector2(tickX - tickW / 2f, tickY),
                    new Vector2(tickX + tickW / 2f, tickY + tickH),
                    UIColors.ApplyAlpha(UIColors.BorderU32));
            }
        }

        float t = Math.Clamp((value - min) / (max - min), 0f, 1f);
        float thumbX = cursorScreenPos.X + t * trackWidth;
        var thumbPos = new Vector2(thumbX, cursorScreenPos.Y);
        var thumbEnd = thumbPos + new Vector2(thumbW, thumbH);

        // Hit area covers the full track + thumb plus a vertical hit margin
        float hitMargin = 4f * scale;
        var trackHitStart = new Vector2(trackStart.X - thumbW / 2f, trackStart.Y - hitMargin);
        var trackHitSize = new Vector2(trackWidth + thumbW, trackH + hitMargin * 2);
        ImGui.SetCursorScreenPos(trackHitStart);
        ImGui.InvisibleButton(props.Id ?? "scrubber", trackHitSize);
        bool isActive = ImGui.IsItemActive() && props.Disabled != true;

        ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(0, controlH));

        DrawHelpers.DrawControlShadow(drawList, thumbPos, thumbEnd, 4f);

        var thumbColor = isActive
            ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]
            : ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
        thumbColor = UIColors.ApplyAlpha(thumbColor with { W = 1f });
        drawList.AddRectFilled(thumbPos, thumbEnd, ImGui.ColorConvertFloat4ToU32(thumbColor), rounding);

        if (!isActive)
            DrawHelpers.DrawButtonGradients(drawList, thumbPos, thumbEnd, thumbH, 4f);

        drawList.AddRect(thumbPos, thumbEnd, UIColors.ApplyAlpha(UIColors.BorderU32), rounding, ImDrawFlags.None, 1f);

        if (!hideValue)
        {
            var valueText = (value * displayMultiplier).ToString(displayFormat, CultureInfo.InvariantCulture) + displaySuffix;
            float textOffsetY = (controlH - ImGui.GetTextLineHeight()) / 2f;
            var textPos = new Vector2(cursorScreenPos.X + trackWidth + thumbW + gap, cursorScreenPos.Y + textOffsetY);
            drawList.AddText(textPos, UIColors.ApplyAlpha(UIColors.TextU32), valueText);
        }

        if (isActive)
        {
            float mouseX = ImGui.GetMousePos().X;
            float newT = Math.Clamp((mouseX - cursorScreenPos.X - thumbW / 2f) / trackWidth, 0f, 1f);
            float newValue = min + newT * (max - min);

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
