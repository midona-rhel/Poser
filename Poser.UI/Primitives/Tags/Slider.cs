using System;
using System.Collections.Generic;
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
        float fraction = scale == SliderScale.Log
            ? (MathF.Pow(1f + curvature, travel) - 1f)
                / curvature
            : travel;
        return minimum + fraction * (maximum - minimum);
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
        float logCurvature = SliderLogCurvature)
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

        bool changed = false;
        if (hit.Active && !disabled)
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
            onCommit?.Invoke();
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
