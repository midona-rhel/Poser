using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Poser.UI.Effects;

namespace Poser.UI;

public static partial class Crystarium
{
    // ---- Short overloads ----

    public static bool Checkbox(string id, ref bool value)
        => CheckboxCore(id, ref value, default, null, false, null, null);
    public static bool Checkbox(string id, ref bool value, StyleClassSet classes)
        => CheckboxCore(id, ref value, classes, null, false, null, null);
    public static bool Checkbox(string id, ref bool value, in CheckboxProps props)
        => CheckboxCore(id, ref value, props.Classes, props.Tooltip, props.Disabled, props.OnChange, props.Style);

    public static float CheckboxSize =>
        Theme.Metrics.Control.Checkbox * ImGuiHelpers.GlobalScale;

    private static bool CheckboxCore(string id, ref bool value, StyleClassSet classes,
        string? tooltip, bool disabled, System.Action<bool>? onChange, CheckboxStyle? inline)
    {
        Stylesheet.EnsureInitialized();

        var classSet = Cls.Checkbox + classes;
        var preState = (disabled ? PseudoState.Disabled : PseudoState.None) | (value ? PseudoState.Checked : 0);

        // Resolve once early to read size.
        var pre = Stylesheet.ResolveCheckbox(classSet, preState);
        if (inline.HasValue) pre = pre.MergedWith(inline.Value);

        if (pre.Display == UI.Display.None) return false;

        float scale = ImGuiHelpers.GlobalScale;
        float size = (pre.Size ?? Sizing.Fixed(Theme.Metrics.Control.Checkbox)).Value * scale;
        size = SizeUtil.Clamp(size, pre.MinSize, pre.MaxSize, scale);

        var hit = Interactive.Reserve(id, new Vector2(size, size), disabled, Norvrandt.AvailableHeight);
        if (hit.Clicked) { value = !value; onChange?.Invoke(value); }

        var state = hit.State;
        if (value) state |= PseudoState.Checked;

        var resolved = Stylesheet.ResolveCheckbox(classSet, state);
        if (inline.HasValue) resolved = resolved.MergedWith(inline.Value);

        var pos = hit.ScreenMin;
        var end = hit.ScreenMax;
        bool hovered = hit.Hovered;
        bool clicked = hit.Clicked;

        var drawList = ImGui.GetWindowDrawList();
        float rounding = (resolved.BorderRadius ?? 2f) * scale;

        var bg = resolved.BackgroundColor ?? (hovered ? ImGui.GetStyle().Colors[(int)ImGuiCol.FrameBgHovered] : Norvrandt.Sheet.CurrentTheme.SurfaceSunken);
        bg = ColorEx.ApplyAlpha(bg);
        if (disabled) bg.W *= resolved.Opacity ?? 0.4f;
        drawList.AddRectFilled(pos, end, ImGui.ColorConvertFloat4ToU32(bg), rounding);

        float borderWidth = (resolved.BorderWidth ?? 1f) * scale;
        if (borderWidth > 0f)
        {
            var borderColor = resolved.BorderColor ?? Theme.Palette.Black;
            var borderU = ColorEx.ApplyAlpha(borderColor);
            if (disabled) borderU.W *= 0.4f;
            // Stroke inset by half thickness — the border paints fully inside the box
            // like CSS `outline-offset: -1px` (picto .checkBox).
            float bi = borderWidth * 0.5f;
            drawList.AddRect(pos + new Vector2(bi, bi), end - new Vector2(bi, bi),
                ImGui.ColorConvertFloat4ToU32(borderU),
                System.MathF.Max(0f, rounding - bi), ImDrawFlags.None, borderWidth);
        }

        // Checkmark — Tabler IconCheck ("M5 12l5 5l10 -10", 24-grid, stroke 2, round
        // caps) at 10/14 of the box, matching picto FolderTree's <IconCheck size={10}/>.
        if (value)
        {
            var fillColor = ColorEx.ApplyAlpha(resolved.CheckmarkColor ?? Theme.Palette.White);
            if (disabled) fillColor.W *= 0.4f;
            uint fill = ImGui.ColorConvertFloat4ToU32(fillColor);

            float iconSpan = size * (10f / 14f);
            float unit = iconSpan / 24f;
            var origin = pos + new Vector2((size - iconSpan) * 0.5f, (size - iconSpan) * 0.5f);
            drawList.PathLineTo(origin + new Vector2(5f, 12f) * unit);
            drawList.PathLineTo(origin + new Vector2(10f, 17f) * unit);
            drawList.PathLineTo(origin + new Vector2(20f, 7f) * unit);
            drawList.PathStroke(fill, ImDrawFlags.None, 2f * unit);
        }

        if (hovered && !string.IsNullOrEmpty(tooltip))
            HoverHelp.Explain(id, hit.ScreenMin, hit.ScreenMax, tooltip!);
        return clicked;
    }

    private static uint ApplyAlphaU32(uint c, float mul)
    {
        var v = ImGui.ColorConvertU32ToFloat4(c);
        v.W *= mul;
        return ImGui.ColorConvertFloat4ToU32(v);
    }
}
