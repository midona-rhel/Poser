using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
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
        Action? onCommit = null)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var metrics = ControlSizing.Resolve(
            style,
            ImGui.GetContentRegionAvail().X / scale,
            Crystarium.ActiveTheme.Controls.SliderHeight);
        float widthPx = metrics.Width;

        // Hit rect = thumb height (14px) across the full width.
        var size = new Vector2(
            MathF.Max(Crystarium.ActiveTheme.Controls.SwitchHeight * scale, widthPx),
            metrics.Height);
        var hit = Interactive.Reserve(id, size, disabled);
        if (hit.DragBegan)
            onBegin?.Invoke();

        bool changed = false;
        if (hit.Active && !disabled)
        {
            float next = SliderValueAt(
                ImGui.GetIO().MousePos.X, hit.ScreenMin, hit.ScreenMax, min, max);
            if (!float.IsNaN(next) && next != value) { value = next; changed = true; }
        }

        PaintSlider(
            ImGui.GetWindowDrawList(), hit.ScreenMin, hit.ScreenMax,
            max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f,
            marks, min, max, disabled);

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
    internal static float SliderValueAt(
        float mouseX, Vector2 min, Vector2 max, float minimum, float maximum)
    {
        float half = (max.Y - min.Y) * 0.5f;
        float x0 = min.X + half;
        float x1 = max.X - half;
        if (!(x1 > x0) || !(maximum > minimum))
            return float.NaN;
        float t = Math.Clamp((mouseX - x0) / (x1 - x0), 0f, 1f);
        return minimum + t * (maximum - minimum);
    }

    /// <summary>
    /// The slider's pixels alone — track, white fill, notches, thumb —
    /// owning no drag state. <paramref name="normalized"/> is the value's 0..1
    /// position; <paramref name="marks"/> stay in VALUE space, which is
    /// why the range comes along.
    /// </summary>
    internal static void PaintSlider(
        ImDrawListPtr dl, Vector2 min, Vector2 max, float normalized,
        IReadOnlyList<float>? marks, float minimum, float maximum,
        bool disabled)
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
                float nx = x0 + (notch - minimum) / (maximum - minimum) * (x1 - x0);
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
