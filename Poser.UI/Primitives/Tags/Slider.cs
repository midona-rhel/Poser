using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// How a slider's TRAVEL maps onto its value range. The readout always states
/// the true value; only the thumb's position changes.
/// </summary>
public enum SliderScale
{
    /// <summary>Position IS the value's fraction of the range.</summary>
    Linear,

    /// <summary>
    /// Position is an EXPONENTIAL fraction: the bottom of the range owns most
    /// of the travel, so a control whose perceptual response is front-loaded
    /// (fog distance, fog thickness) stays adjustable where it actually does
    /// something instead of saturating in the first tenth of the track.
    /// </summary>
    Log,
}

public static partial class Crystarium
{
    /// <summary>
    /// The exponential's curvature: the value fraction at half travel is
    /// <c>(sqrt(1+K)-1)/K</c>, so 99 puts the halfway thumb at 9% of the range
    /// — two decades of usable resolution, which is the span the ranges that
    /// ask for this scale actually carry.
    /// </summary>
    private const float SliderLogCurvature = 99f;

    /// <summary>The 0..1 TRAVEL a value sits at under a scale.</summary>
    private static float SliderPositionOf(
        float value, float minimum, float maximum, SliderScale scale)
    {
        if (!(maximum > minimum))
            return 0f;
        float fraction = Math.Clamp(
            (value - minimum) / (maximum - minimum), 0f, 1f);
        return scale == SliderScale.Log
            ? MathF.Log(1f + SliderLogCurvature * fraction)
                / MathF.Log(1f + SliderLogCurvature)
            : fraction;
    }

    /// <summary>The value a 0..1 TRAVEL means under a scale — the exact
    /// inverse of <see cref="SliderPositionOf"/>, so a drag that lands on a
    /// pixel reads back to that same pixel.</summary>
    private static float SliderValueOf(
        float position, float minimum, float maximum, SliderScale scale)
    {
        float travel = Math.Clamp(position, 0f, 1f);
        float fraction = scale == SliderScale.Log
            ? (MathF.Pow(1f + SliderLogCurvature, travel) - 1f)
                / SliderLogCurvature
            : travel;
        return minimum + fraction * (maximum - minimum);
    }

    /// <summary>
    /// Range slider. The coloring deliberately deviates from the picto
    /// transcription: the 14px circular thumb is solid white
    /// and the track is FILLED from
    /// its minimum to the current value, the remainder staying the
    /// neutral white @ .14. Geometry, hit area, drag semantics, notches,
    /// readout, and disabled fade are unchanged. <paramref name=marks/> marks values with a bar crossing the track.
    /// without snapping. Custom-drawn (ImGui's SliderFloat grab is
    /// rectangular). This is the ONE slider — there is no second look.
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
        SliderScale scale = SliderScale.Linear)
    {
        float frameScale = ImGuiHelpers.GlobalScale;
        var metrics = ControlSizing.Resolve(
            style,
            ImGui.GetContentRegionAvail().X / frameScale,
            Crystarium.ActiveTheme.Controls.SliderHeight);
        float widthPx = metrics.Width;

        // Hit rect = thumb height (14px) across the full width.
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
                min, max, scale);
            if (!float.IsNaN(next) && next != value) { value = next; changed = true; }
        }

        PaintSlider(
            ImGui.GetWindowDrawList(), hit.ScreenMin, hit.ScreenMax,
            SliderPositionOf(value, min, max, scale),
            marks, min, max, disabled, scale);

        if (changed) onChange(value);
        if (hit.DragEnded)
            onCommit?.Invoke();
        if (!string.IsNullOrEmpty(help) && hit.Hovered)
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);

        return changed;
    }

    /// <summary>
    /// The value the pointer is over while the track owns the drag: the
    /// x span the thumb's CENTRE can occupy is the rect inset by half a
    /// thumb on each end, and the thumb is as tall as the rect, so the
    /// inset reads off the box rather than off the resolved metrics.
    /// <para>Returns <c>NaN</c> when that span or the value range is
    /// empty — there is no value under the pointer, and the caller must
    /// leave the one it has alone rather than clamp to an end.</para>
    /// </summary>
    private static float SliderValueAt(
        float mouseX, Vector2 min, Vector2 max, float minimum, float maximum,
        SliderScale scale)
    {
        float half = (max.Y - min.Y) * 0.5f;
        float x0 = min.X + half;
        float x1 = max.X - half;
        if (!(x1 > x0) || !(maximum > minimum))
            return float.NaN;
        return SliderValueOf(
            (mouseX - x0) / (x1 - x0), minimum, maximum, scale);
    }

    /// <summary>
    /// The slider's pixels alone — track, white fill, notches, thumb —
    /// owning no drag state. <paramref name="normalized"/> is the value's 0..1
    /// position; <paramref name="marks"/> stay in VALUE space, which is
    /// why the range comes along.
    /// </summary>
    private static void PaintSlider(
        ImDrawListPtr dl, Vector2 min, Vector2 max, float normalized,
        IReadOnlyList<float>? marks, float minimum, float maximum,
        bool disabled, SliderScale valueScale = SliderScale.Linear)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float half = (max.Y - min.Y) * 0.5f;
        float x0 = min.X + half;
        float x1 = max.X - half;
        float alpha = disabled ? 0.5f : 1f;
        float cy = (min.Y + max.Y) * 0.5f;

        // track: height 4, border-radius 2, background --color-border-primary
        float thumbX = x0 + normalized * (x1 - x0);

        var track = Crystarium.ActiveTheme.Chrome.ControlBorder.Fade(alpha);
        dl.AddRectFilled(
            new Vector2(min.X, cy - Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
            new Vector2(max.X, cy + Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(track)),
            Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale);

        // Filled segment: minimum → value in the primary blue; the
        // remainder above stays neutral.
        if (thumbX - min.X > 1f * scale)
        {
            // The filled span is WHITE like the thumb, not the primary blue;
            // the remaining deviation from Picto's .rangeInput is its
            // primary THUMB.
            var fill = Crystarium.ActiveTheme.Palette.White.Fade(alpha);
            dl.AddRectFilled(
                new Vector2(min.X, cy - Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
                new Vector2(thumbX, cy + Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(fill)),
                Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale);
        }

        // Notch marks cross the track at fixed values (no snapping), so the
        // range's reference points are visible before dragging.
        if (marks != null && x1 > x0 && maximum > minimum)
        {
            var notchColor = track with { W = MathF.Min(1f, track.W * 2.5f) };
            uint notchU32 = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(notchColor));
            foreach (var notch in marks)
            {
                if (notch < minimum || notch > maximum) continue;
                // Marks stay in VALUE space, so they travel through the same
                // mapping the thumb does — a log slider's notches bunch up at
                // the top exactly as its values do.
                float nx = x0 + SliderPositionOf(
                    notch, minimum, maximum, valueScale) * (x1 - x0);
                dl.AddRectFilled(
                    new Vector2(nx - 0.5f * scale, cy - 4f * scale),
                    new Vector2(nx + 0.5f * scale, cy + 4f * scale), notchU32);
            }
        }

        // thumb: 14px circle, solid white over the fill boundary. The thumb
        // OCCLUDES: a disabled thumb fades by flattening over the surface
        // rather than by alpha, so the track can never show through it
        // (user: "the scrubber shows the partially filled slider behind it").
        var thumb = ColorEx.FlattenOver(
            Crystarium.ActiveTheme.Palette.White.Fade(alpha),
            Crystarium.ActiveTheme.Surface);
        dl.AddCircleFilled(new Vector2(thumbX, cy), half,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(thumb)), 32);
    }
}
