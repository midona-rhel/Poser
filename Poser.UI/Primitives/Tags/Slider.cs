using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// Maps slider travel to its value range.
/// </summary>
public enum SliderScale
{
    /// <summary>Position follows the value fraction.</summary>
    Linear,

    /// <summary>Position follows an exponential value fraction.</summary>
    Log,

    /// <summary>The measured multi-decade mapping: LINEAR from the
    /// minimum to max/10^decades across the FIRST HALF of the travel,
    /// then one decade per equal remaining segment — 0→1 to the middle,
    /// 10 at three-quarters, 100 at the end of a 0–100 range. The
    /// curvature parameter carries the decade count for this scale.</summary>
    Decades,
}

public static partial class Crystarium
{
    /// <summary>
    /// Curvature keeps fine control near the lower end of logarithmic ranges.
    /// </summary>
    private const float SliderLogCurvature = 99f;

    /// <summary>Returns a value's normalized slider position.</summary>
    private static float SliderPositionOf(
        float value, float minimum, float maximum, SliderScale scale,
        float curvature = SliderLogCurvature)
    {
        if (!(maximum > minimum))
            return 0f;
        float fraction = Math.Clamp(
            (value - minimum) / (maximum - minimum), 0f, 1f);
        if (scale == SliderScale.Decades)
        {
            float decades = MathF.Max(1f, MathF.Round(curvature));
            float linearTop = MathF.Pow(10f, -decades);
            if (fraction <= linearTop)
                return fraction / linearTop * 0.5f;
            return 0.5f + MathF.Log10(fraction / linearTop)
                / decades * 0.5f;
        }
        return scale == SliderScale.Log
            ? MathF.Log(1f + curvature * fraction)
                / MathF.Log(1f + curvature)
            : fraction;
    }

    /// <summary>Returns the value at a normalized slider position.</summary>
    private static float SliderValueOf(
        float position, float minimum, float maximum, SliderScale scale,
        float curvature = SliderLogCurvature)
    {
        float travel = Math.Clamp(position, 0f, 1f);
        float fraction;
        if (scale == SliderScale.Decades)
        {
            float decades = MathF.Max(1f, MathF.Round(curvature));
            float linearTop = MathF.Pow(10f, -decades);
            fraction = travel <= 0.5f
                ? travel / 0.5f * linearTop
                : linearTop * MathF.Pow(
                    10f, (travel - 0.5f) / 0.5f * decades);
        }
        else
        {
            fraction = scale == SliderScale.Log
                ? (MathF.Pow(1f + curvature, travel) - 1f)
                    / curvature
                : travel;
        }
        return minimum + fraction * (maximum - minimum);
    }

    /// <summary>
    /// THE standard slider: a value-well with the fill inside and the mono
    /// number at the right — AxisWell wearing the slider's fill. Dragging
    /// sets the value by absolute position along the well (through the
    /// same travel mapping the classic slider uses, log included);
    /// double-click types the exact value through the shared well editor.
    /// </summary>
    public static bool SliderWell(
        string id,
        float value,
        float minimum,
        float maximum,
        Action<float> onChange,
        Action? onBegin = null,
        Action? onCommit = null,
        string? format = null,
        SliderScale scale = SliderScale.Linear,
        float logCurvature = SliderLogCurvature,
        ControlStyle style = default,
        bool disabled = false)
    {
        float uiScale = ImGuiHelpers.GlobalScale;
        var metrics = ControlSizing.Resolve(
            style,
            ActiveTheme.Form.ValueColumnWidth,
            ActiveTheme.Controls.WorkspaceHeight);
        var pos = ImGui.GetCursorScreenPos();
        var size = metrics.Size;

        void Clamped(float next) =>
            onChange(Math.Clamp(next, minimum, maximum));

        if (_axisEditId == id && !disabled)
            return EditAxisWell(
                id, string.Empty, value, Clamped, onCommit,
                ActiveTheme.FormValue, format ?? "0.###", pos, size, uiScale);

        var hit = Interactive.Reserve(id, size, disabled);
        bool changed = false;
        if (hit.DoubleClicked)
        {
            _axisEditId = id;
            _axisEditValue = value;
            _axisEditNeedsFocus = true;
        }
        else if (hit.Active)
        {
            if (hit.Clicked)
                onBegin?.Invoke();
            float fraction = size.X > 0f
                ? (ImGui.GetIO().MousePos.X - pos.X) / size.X
                : 0f;
            float next = SliderValueOf(
                fraction, minimum, maximum, scale, logCurvature);
            next = Math.Clamp(next, minimum, maximum);
            if (next != value)
            {
                onChange(next);
                value = next;
                changed = true;
            }
        }
        if (hit.DragEnded)
            Commit(onCommit);

        DrawSliderWell(
            pos, size, value, minimum, maximum, scale, logCurvature,
            format, hit.Active, disabled, uiScale);
        if (hit.Hovered && _axisEditId == null)
            HoverHelp.Explain(id, pos, pos + size,
                "Drag to set · Double-click to type");
        return changed;
    }

    private static void DrawSliderWell(
        Vector2 pos,
        Vector2 size,
        float value,
        float minimum,
        float maximum,
        SliderScale scale,
        float logCurvature,
        string? format,
        bool active,
        bool disabled,
        float uiScale)
    {
        var draw = ImGui.GetWindowDrawList();
        var max = pos + size;
        float radius = ActiveTheme.Radii.Small * uiScale;
        var well = ActiveTheme.Chrome.InputWell;
        // The fill is an OPAQUE blend of well ground and accent — the
        // approved mockup color — never a translucent wash.
        var accent = ActiveTheme.Chrome.AccentFill;
        var fill = new Vector4(
            well.X + (accent.X - well.X) * 0.45f,
            well.Y + (accent.Y - well.Y) * 0.45f,
            well.Z + (accent.Z - well.Z) * 0.45f,
            1f);
        var border = active
            ? ActiveTheme.FormValue with { W = 0.60f }
            : ActiveTheme.Chrome.ControlBorder;
        if (disabled)
        {
            well = well.Fade(ActiveTheme.Chrome.DisabledOpacity);
            fill = ActiveTheme.Chrome.ControlBorder
                .Fade(ActiveTheme.Chrome.DisabledOpacity);
            border = border.Fade(ActiveTheme.Chrome.DisabledOpacity);
        }
        draw.AddRectFilled(
            pos, max,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(well)),
            radius);
        float fraction = SliderPositionOf(
            value, minimum, maximum, scale, logCurvature);
        if (fraction > 0f)
        {
            // The fill clips against the well's rounded silhouette.
            draw.PushClipRect(
                pos, new Vector2(pos.X + size.X * fraction, max.Y), true);
            draw.AddRectFilled(
                pos, max,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(fill)),
                radius);
            draw.PopClipRect();
        }
        float inset = 0.5f * uiScale;
        draw.AddRect(
            pos + new Vector2(inset),
            max - new Vector2(inset),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(border)),
            MathF.Max(0f, radius - inset),
            ImDrawFlags.None,
            uiScale);

        string text = format is { } fixedFormat
            ? value.ToString(fixedFormat, CultureInfo.InvariantCulture)
            : AdaptiveValueText(value);
        var wellStyle = new TextStyle
        {
            Size = ActiveTheme.Typography.LabelSize,
            Family = FontFamily.Mono,
            Color = ActiveTheme.FormValue,
            Disabled = disabled,
        };
        float pad = ActiveTheme.Form.AxisWellHorizontalPadding * uiScale;
        float textWidth = MeasureText(text, wellStyle).X;
        TextInBand(
            new Vector2(max.X - pad - textWidth, pos.Y),
            new Vector2(textWidth, size.Y),
            text,
            wellStyle);
    }

    /// <summary>
    /// Custom-drawn range slider with a circular thumb and optional value marks.
    /// </summary>
    public static bool Slider(
        string id,
        float value,
        float min,
        float max,
        Action<float> onChange,
        ControlStyle style = default,
        IReadOnlyList<float>? marks = null,
        bool disabled = false,
        string? help = null,
        Action? onBegin = null,
        Action? onCommit = null,
        SliderScale scale = SliderScale.Linear,
        float logCurvature = SliderLogCurvature,
        float? altReset = null)
    {
        float frameScale = ImGuiHelpers.GlobalScale;
        var metrics = ControlSizing.Resolve(
            style,
            ImGui.GetContentRegionAvail().X / frameScale,
            Crystarium.ActiveTheme.Controls.SliderHeight);
        float widthPx = metrics.Width;

        // The hit area spans the full track at the thumb's height.
        var size = new Vector2(
            MathF.Max(
                Crystarium.ActiveTheme.Controls.SwitchHeight * frameScale,
                widthPx),
            metrics.Height);
        var hit = Interactive.Reserve(id, size, disabled);
        if (hit.DragBegan)
            onBegin?.Invoke();

        // Alt-click restores the stated default — one gesture, one undo
        // step, no travel. It owns the click: the drag update stands down
        // so the value cannot jump to the pointer first.
        bool altResetHit = altReset is { } fallback && hit.Clicked
            && ImGui.GetIO().KeyAlt && !disabled;
        bool changed = false;
        if (altResetHit && value != altReset!.Value)
        {
            value = altReset.Value;
            changed = true;
        }
        if (hit.Active && !disabled && !altResetHit)
        {
            float next = SliderValueAt(
                ImGui.GetIO().MousePos.X, hit.ScreenMin, hit.ScreenMax,
                min, max, scale, logCurvature);
            if (!float.IsNaN(next) && next != value) { value = next; changed = true; }
        }

        PaintSlider(
            ImGui.GetWindowDrawList(), hit.ScreenMin, hit.ScreenMax,
            SliderPositionOf(value, min, max, scale, logCurvature),
            marks, min, max, disabled, scale, logCurvature);

        if (changed) onChange(value);
        if (hit.DragEnded)
            Commit(onCommit);
        if (!string.IsNullOrEmpty(help) && hit.Hovered)
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);

        return changed;
    }

    /// <summary>
    /// Returns the value under the pointer while the slider owns the drag.
    /// Returns <c>NaN</c> when the track or range is empty.
    /// </summary>
    private static float SliderValueAt(
        float mouseX, Vector2 min, Vector2 max, float minimum, float maximum,
        SliderScale scale, float curvature = SliderLogCurvature)
    {
        float half = (max.Y - min.Y) * 0.5f;
        float x0 = min.X + half;
        float x1 = max.X - half;
        if (!(x1 > x0) || !(maximum > minimum))
            return float.NaN;
        return SliderValueOf(
            (mouseX - x0) / (x1 - x0), minimum, maximum, scale, curvature);
    }

    /// <summary>
    /// Draws the track, primary fill, marks, and thumb without interaction.
    /// </summary>
    private static void PaintSlider(
        ImDrawListPtr dl, Vector2 min, Vector2 max, float normalized,
        IReadOnlyList<float>? marks, float minimum, float maximum,
        bool disabled, SliderScale valueScale = SliderScale.Linear,
        float curvature = SliderLogCurvature)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float half = (max.Y - min.Y) * 0.5f;
        float x0 = min.X + half;
        float x1 = max.X - half;
        float alpha = disabled ? 0.5f : 1f;
        float cy = (min.Y + max.Y) * 0.5f;

        // The neutral track remains visible beyond the current value.
        float thumbX = x0 + normalized * (x1 - x0);

        var track = Crystarium.ActiveTheme.Chrome.ControlBorder.Fade(alpha);
        dl.AddRectFilled(
            new Vector2(min.X, cy - Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
            new Vector2(max.X, cy + Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(track)),
            Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale);

        // The fill follows the active theme, including live accent changes.
        if (thumbX - min.X > 1f * scale)
        {
            var fill = Crystarium.ActiveTheme.Palette.Primary.Fade(alpha);
            dl.AddRectFilled(
                new Vector2(min.X, cy - Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
                new Vector2(thumbX, cy + Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(fill)),
                Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale);
        }

        // Marks cross the track at fixed values without snapping.
        if (marks != null && x1 > x0 && maximum > minimum)
        {
            var notchColor = track with { W = MathF.Min(1f, track.W * 2.5f) };
            uint notchU32 = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(notchColor));
            foreach (var notch in marks)
            {
                if (notch < minimum || notch > maximum) continue;
                // Marks use the same mapping as the thumb.
                float nx = x0 + SliderPositionOf(
                    notch, minimum, maximum, valueScale, curvature) * (x1 - x0);
                dl.AddRectFilled(
                    new Vector2(nx - 0.5f * scale, cy - 4f * scale),
                    new Vector2(nx + 0.5f * scale, cy + 4f * scale), notchU32);
            }
        }

        // The white thumb stays opaque over the fill boundary when disabled.
        var thumb = ColorEx.FlattenOver(
            Crystarium.ActiveTheme.Palette.White.Fade(alpha),
            Crystarium.ActiveTheme.Surface);
        dl.AddCircleFilled(new Vector2(thumbX, cy), half,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(thumb)), 32);
    }
}
