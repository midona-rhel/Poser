using System;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Controls;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    public static bool Scrubber(string id, ref float value, float min, float max)
        => ScrubberCore(id, ref value, min, max, default, null, false, null, 0f, 1f, "F2", "", false, null);
    public static bool Scrubber(string id, ref float value, float min, float max, float step)
        => ScrubberCore(id, ref value, min, max, default, null, false, null, step, 1f, "F2", "", false, null);
    public static bool Scrubber(string id, ref float value, float min, float max, in ScrubberProps props)
        => ScrubberCore(id, ref value, min, max, props.Classes, props.Tooltip, props.Disabled, props.OnChange,
            props.Step, props.DisplayMultiplier == 0 ? 1f : props.DisplayMultiplier,
            string.IsNullOrEmpty(props.DisplayFormat) ? "F2" : props.DisplayFormat,
            props.DisplaySuffix ?? "", props.HideValue, props.Style);

    private static bool ScrubberCore(string id, ref float value, float min, float max,
        StyleClassSet classes, string? tooltip, bool disabled, Action<float>? onChange,
        float step, float displayMultiplier, string displayFormat, string displaySuffix, bool hideValue,
        ScrubberStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var classSet = Cls.Scrubber + classes;
        var preState = disabled ? PseudoState.Disabled : PseudoState.None;
        var resolved = Stylesheet.ResolveScrubber(classSet, preState);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        if (resolved.Display == UI.Display.None) return false;

        bool changed = false;
        float scale = PoserUI.Scale;
        float thumbW = (resolved.ThumbWidth ?? 12f) * scale;
        float thumbH = (resolved.Height ?? Sizing.Fixed(Flex.RowHeight)).Value * scale;
        float trackH = (resolved.TrackHeight ?? 2f) * scale;
        float rounding = (resolved.ThumbBorderRadius ?? 4f) * scale;
        float gap = Flex.ItemGap * scale;

        float controlH = thumbH;

        float valueTextW = 0f;
        if (!hideValue)
        {
            var maxText = (max * displayMultiplier).ToString(displayFormat, CultureInfo.InvariantCulture) + displaySuffix;
            valueTextW = ImGui.CalcTextSize(maxText).X;
        }

        float totalWidth;
        if (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            totalWidth = resolved.Width.Value.Value * scale;
        else
            totalWidth = AvailableWidth;
        totalWidth = SizeUtil.Clamp(totalWidth, resolved.MinWidth, resolved.MaxWidth, scale);

        float trackWidth = hideValue ? totalWidth - thumbW : totalWidth - valueTextW - gap - thumbW - gap;

        var cursorScreenPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        float trackOffsetY = (controlH - trackH) / 2f;
        var trackStart = new Vector2(cursorScreenPos.X + thumbW / 2f, cursorScreenPos.Y + trackOffsetY);
        var trackEnd = new Vector2(cursorScreenPos.X + thumbW / 2f + trackWidth, cursorScreenPos.Y + trackOffsetY + trackH);

        var trackColor = UIColors.ApplyAlpha(resolved.TrackColor ?? UIColors.Border);
        drawList.AddRectFilled(trackStart, trackEnd, ImGui.ColorConvertFloat4ToU32(trackColor), trackH / 2f);

        var whiteHighlight = UIColors.ApplyAlpha(UIColors.White with { W = 0.4f });
        drawList.AddLine(
            new Vector2(trackStart.X, trackEnd.Y), new Vector2(trackEnd.X, trackEnd.Y),
            ImGui.ColorConvertFloat4ToU32(whiteHighlight), 1f);

        if (step > 0)
        {
            float tickH = 6f * scale, tickW = 1f * scale, tickY = trackStart.Y - tickH - 2f * scale;
            var tickColor = UIColors.ApplyAlpha(resolved.TickColor ?? UIColors.Border);
            uint tickU32 = ImGui.ColorConvertFloat4ToU32(tickColor);
            for (float v = min; v <= max + step * 0.5f; v += step)
            {
                float t = Math.Clamp((v - min) / (max - min), 0f, 1f);
                float x = trackStart.X + t * trackWidth;
                drawList.AddRectFilled(new Vector2(x - tickW / 2f, tickY), new Vector2(x + tickW / 2f, tickY + tickH), tickU32);
            }
        }

        float ratio = Math.Clamp((value - min) / (max - min), 0f, 1f);
        float thumbX = cursorScreenPos.X + ratio * trackWidth;
        var thumbPos = new Vector2(thumbX, cursorScreenPos.Y);
        var thumbEnd = thumbPos + new Vector2(thumbW, thumbH);

        float hitMargin = 4f * scale;
        var trackHitStart = new Vector2(trackStart.X - thumbW / 2f, trackStart.Y - hitMargin);
        var trackHitSize = new Vector2(trackWidth + thumbW, trackH + hitMargin * 2);
        ImGui.SetCursorScreenPos(trackHitStart);
        ImGui.InvisibleButton(id, trackHitSize);
        bool isActive = ImGui.IsItemActive() && !disabled;

        ImGui.SetCursorScreenPos(cursorScreenPos + new Vector2(0, controlH));

        DrawHelpers.DrawControlShadow(drawList, thumbPos, thumbEnd, 4f);

        Vector4 thumb;
        if (resolved.ThumbColor.HasValue)
        {
            thumb = UIColors.ApplyAlpha(resolved.ThumbColor.Value);
        }
        else
        {
            var raw = isActive
                ? ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive]
                : ImGui.GetStyle().Colors[(int)ImGuiCol.Button];
            thumb = UIColors.ApplyAlpha(raw with { W = 1f });
        }
        drawList.AddRectFilled(thumbPos, thumbEnd, ImGui.ColorConvertFloat4ToU32(thumb), rounding);

        if (!isActive) DrawHelpers.DrawButtonGradients(drawList, thumbPos, thumbEnd, thumbH, 4f);

        var thumbBorder = UIColors.ApplyAlpha(resolved.ThumbBorderColor ?? UIColors.Border);
        drawList.AddRect(thumbPos, thumbEnd, ImGui.ColorConvertFloat4ToU32(thumbBorder), rounding,
            ImDrawFlags.None, (resolved.ThumbBorderWidth ?? 1f) * scale);

        if (!hideValue)
        {
            var valueText = (value * displayMultiplier).ToString(displayFormat, CultureInfo.InvariantCulture) + displaySuffix;
            float textOffsetY = (controlH - ImGui.GetTextLineHeight()) / 2f;
            var textPos = new Vector2(cursorScreenPos.X + trackWidth + thumbW + gap, cursorScreenPos.Y + textOffsetY);
            var textColor = UIColors.ApplyAlpha(resolved.Color ?? UIColors.Text);
            drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(textColor), valueText);
        }

        if (isActive)
        {
            float mouseX = ImGui.GetMousePos().X;
            float newRatio = Math.Clamp((mouseX - cursorScreenPos.X - thumbW / 2f) / trackWidth, 0f, 1f);
            float newValue = min + newRatio * (max - min);

            if (step > 0)
            {
                newValue = MathF.Round(newValue / step) * step;
                newValue = Math.Clamp(newValue, min, max);
            }

            if (Math.Abs(newValue - value) > 0.0001f)
            {
                value = newValue;
                changed = true;
                onChange?.Invoke(value);
            }
        }

        return changed;
    }
}
