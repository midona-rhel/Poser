using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Range slider. PBI-090 deliberately supersedes the original picto
    /// transcription's coloring: the 14px circular thumb is solid white
    /// and the track is FILLED with <c>--color-primary</c> #3297FF from
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
        float controlHeight = metrics.LogicalHeight;

        // Hit rect = thumb height (14px) across the full width.
        var size = new Vector2(
            MathF.Max(Crystarium.ActiveTheme.Controls.SwitchHeight * scale, widthPx),
            metrics.Height);
        var hit = Interactive.Reserve(id, size, disabled);
        if (hit.DragBegan)
            onBegin?.Invoke();

        float half = controlHeight * 0.5f * scale;
        float x0 = hit.ScreenMin.X + half;
        float x1 = hit.ScreenMax.X - half;

        bool changed = false;
        if (hit.Active && !disabled && x1 > x0 && max > min)
        {
            float t = Math.Clamp((ImGui.GetIO().MousePos.X - x0) / (x1 - x0), 0f, 1f);
            float next = min + t * (max - min);
            if (next != value) { value = next; changed = true; }
        }

        var dl = ImGui.GetWindowDrawList();
        float alpha = disabled ? 0.5f : 1f;
        float cy = (hit.ScreenMin.Y + hit.ScreenMax.Y) * 0.5f;

        // track: height 4, border-radius 2, background --color-border-primary
        float pos = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        float thumbX = x0 + pos * (x1 - x0);

        var track = Crystarium.ActiveTheme.Chrome.ControlBorder;
        track.W *= alpha;
        dl.AddRectFilled(
            new Vector2(hit.ScreenMin.X, cy - Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
            new Vector2(hit.ScreenMax.X, cy + Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(track)),
            Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale);

        // Filled segment: minimum → value in the primary blue; the
        // remainder above stays neutral.
        if (thumbX - hit.ScreenMin.X > 1f * scale)
        {
            var fill = Crystarium.ActiveTheme.Palette.Primary;
            fill.W *= alpha;
            dl.AddRectFilled(
                new Vector2(hit.ScreenMin.X, cy - Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
                new Vector2(thumbX, cy + Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(fill)),
                Crystarium.ActiveTheme.Controls.SliderTrackHeight * 0.5f * scale);
        }

        // Notch marks cross the track at fixed values (no snapping), so the
        // range's reference points are visible before dragging.
        if (marks != null && x1 > x0 && max > min)
        {
            var notchColor = track with { W = MathF.Min(1f, track.W * 2.5f) };
            uint notchU32 = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(notchColor));
            foreach (var notch in marks)
            {
                if (notch < min || notch > max) continue;
                float nx = x0 + (notch - min) / (max - min) * (x1 - x0);
                dl.AddRectFilled(
                    new Vector2(nx - 0.5f * scale, cy - 4f * scale),
                    new Vector2(nx + 0.5f * scale, cy + 4f * scale), notchU32);
            }
        }

        // thumb: 14px circle, solid white over the fill boundary
        var thumb = Crystarium.ActiveTheme.Palette.White;
        thumb.W *= alpha;
        dl.AddCircleFilled(new Vector2(thumbX, cy), half,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(thumb)), 32);

        if (changed) onChange(value);
        if (hit.DragEnded)
            onCommit?.Invoke();
        if (!string.IsNullOrEmpty(help) && hit.Hovered)
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, help!);

        return changed;
    }
}
