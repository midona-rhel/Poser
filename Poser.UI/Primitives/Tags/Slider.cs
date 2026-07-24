using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Range slider — pixel transcription of picto's <c>.slider</c> input
    /// (tokens.css + M5/M7 usage): 4px track, radius 2, <c>--color-border-primary</c>
    /// (white @ .14); 14px circular thumb in <c>--color-primary</c> #3297FF.
    /// No filled-left segment and no inline value text — pair with a separate
    /// mono value label like picto's <c>.sliderVal</c>. Custom-drawn (ImGui's
    /// SliderFloat grab is rectangular).
    /// </summary>
    public static bool Slider(string id, ref float value, float min, float max)
        => SliderCore(id, ref value, min, max, default, null, false, null, null);

    public static bool Slider(string id, ref float value, float min, float max, in SliderProps props)
        => SliderCore(id, ref value, min, max, props.Classes, props.Tooltip, props.Disabled, props.OnChange, props.Style);

    private static bool SliderCore(string id, ref float value, float min, float max,
        StyleClassSet classes, string? tooltip, bool disabled, Action<float>? onChange,
        SliderStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var classSet = Cls.Slider + classes;
        var resolved = Stylesheet.ResolveSlider(classSet, disabled ? PseudoState.Disabled : PseudoState.None);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);
        if (resolved.Display == UI.Display.None) return false;

        float scale = ImGuiHelpers.GlobalScale;
        float widthPx;
        if (resolved.Width.HasValue && resolved.Width.Value.Mode == SizingMode.Fixed)
            widthPx = resolved.Width.Value.Value * scale;
        else
            widthPx = Norvrandt.AvailableWidth;
        widthPx = SizeUtil.Clamp(widthPx, resolved.MinWidth, resolved.MaxWidth, scale);

        // Hit rect = thumb height (14px) across the full width.
        var size = new Vector2(widthPx, 14f * scale);
        var hit = Interactive.Reserve(id, size, disabled, Norvrandt.AvailableHeight);

        float half = 7f * scale;                       // thumb radius
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
        float alpha = (disabled ? 0.5f : 1f) * (resolved.Opacity ?? 1f);
        float cy = (hit.ScreenMin.Y + hit.ScreenMax.Y) * 0.5f;

        // track: height 4, border-radius 2, background --color-border-primary
        var track = resolved.BackgroundColor ?? new Vector4(1f, 1f, 1f, 0.14f);
        track.W *= alpha;
        dl.AddRectFilled(
            new Vector2(hit.ScreenMin.X, cy - 2f * scale),
            new Vector2(hit.ScreenMax.X, cy + 2f * scale),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(track)), 2f * scale);

        // thumb: 14px circle, --color-primary
        float pos = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        var thumb = resolved.GrabColor ?? new Vector4(50 / 255f, 151 / 255f, 255 / 255f, 1f);
        thumb.W *= alpha;
        dl.AddCircleFilled(new Vector2(x0 + pos * (x1 - x0), cy), half,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(thumb)), 32);

        if (changed) onChange?.Invoke(value);
        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

        return changed;
    }
}
