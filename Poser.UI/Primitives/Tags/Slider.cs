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
    /// No filled-left segment. <c>Format</c> opts into an inline mono value
    /// readout right of the track (picto's <c>.sliderVal</c> as part of the
    /// control); without it, pair a separate mono label as before.
    /// <c>Style.Notches</c> marks values with a bar crossing the track,
    /// without snapping. Custom-drawn (ImGui's SliderFloat grab is
    /// rectangular). This is the ONE slider — there is no second look.
    /// </summary>
    public static bool Slider(string id, ref float value, float min, float max)
        => SliderCore(id, ref value, min, max, default, null, false, null, null, null, null);

    public static bool Slider(string id, ref float value, float min, float max, in SliderProps props)
        => SliderCore(id, ref value, min, max, props.Classes, props.Tooltip, props.Disabled, props.OnChange, props.Style,
            props.Format, props.Suffix);

    private static bool SliderCore(string id, ref float value, float min, float max,
        StyleClassSet classes, string? tooltip, bool disabled, Action<float>? onChange,
        SliderStyle? inline, string? format, string? suffix)
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

        // The readout reserves the width of the widest end of the range, so
        // the track does not breathe as digits change; it sits OUTSIDE the
        // hit rect so clicking the number cannot jump the thumb.
        float reserve = 0f;
        string? readout = null;
        var monoFont = FontRegistry.Resolve(FontFamily.Mono, 11f);
        bool monoAvailable = monoFont is { Available: true };
        if (!string.IsNullOrEmpty(format))
        {
            if (monoAvailable) monoFont!.Push();
            string low = min.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + suffix;
            string high = max.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + suffix;
            reserve = MathF.Max(ImGui.CalcTextSize(low).X, ImGui.CalcTextSize(high).X) + 8f * scale;
            readout = value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + suffix;
            if (monoAvailable) monoFont!.Pop();
        }

        // Hit rect = thumb height (14px) across the full width.
        var size = new Vector2(MathF.Max(20f * scale, widthPx - reserve), 14f * scale);
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

        // Notch marks cross the track at fixed values (no snapping), so the
        // range's reference points are visible before dragging.
        if (resolved.Notches is { } notches && x1 > x0 && max > min)
        {
            var notchColor = track with { W = MathF.Min(1f, track.W * 2.5f) };
            uint notchU32 = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(notchColor));
            foreach (var notch in notches)
            {
                if (notch < min || notch > max) continue;
                float nx = x0 + (notch - min) / (max - min) * (x1 - x0);
                dl.AddRectFilled(
                    new Vector2(nx - 0.5f * scale, cy - 4f * scale),
                    new Vector2(nx + 0.5f * scale, cy + 4f * scale), notchU32);
            }
        }

        // thumb: 14px circle, --color-primary
        float pos = max > min ? Math.Clamp((value - min) / (max - min), 0f, 1f) : 0f;
        var thumb = resolved.GrabColor ?? new Vector4(50 / 255f, 151 / 255f, 255 / 255f, 1f);
        thumb.W *= alpha;
        dl.AddCircleFilled(new Vector2(x0 + pos * (x1 - x0), cy), half,
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(thumb)), 32);

        if (readout != null)
        {
            if (monoAvailable) monoFont!.Push();
            var textSize = ImGui.CalcTextSize(readout);
            var textColor = resolved.Color ?? Norvrandt.Sheet.CurrentTheme.Text;
            textColor.W *= alpha;
            dl.AddText(
                new Vector2(hit.ScreenMax.X + 8f * scale, cy - textSize.Y * 0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(textColor)), readout);
            if (monoAvailable) monoFont!.Pop();
        }

        if (changed) onChange?.Invoke(value);
        if (!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);

        return changed;
    }
}
