using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Reactive;

/// <summary>
/// THE box recipe, implemented once for every element that has one. Nothing
/// here knows what kind of control it is painting: it is handed a flattened
/// sheet and draws the fill, the ramp between the fill's two endpoints, the
/// 1px inside border and the compensated disabled group — the accepted text
/// button's own arithmetic, promoted from a control to the base.
/// </summary>
internal static class BoxPaint
{
    /// <summary>What the box resolved for its own content and its subtree.</summary>
    internal readonly struct Result
    {
        internal Result(Vector4? foreground, float glyphOpacity)
        {
            Foreground = foreground;
            GlyphOpacity = glyphOpacity;
        }

        /// <summary>currentColor AFTER the disabled group's compensation.</summary>
        internal readonly Vector4? Foreground;

        /// <summary>The multiplier every glyph at or below this element folds
        /// into its own opacity.</summary>
        internal readonly float GlyphOpacity;
    }

    /// <summary>
    /// Draws the element's chrome. <paramref name="ownsBox"/> is false when a
    /// painter hook owns the box, in which case the base contributes no
    /// pixels and only resolves what the content must be drawn with — a hook
    /// and a sheet fill are never both drawn.
    /// </summary>
    internal static Result Draw(
        ImDrawListPtr draw,
        Vector2 min,
        Vector2 max,
        in Poser.UI.ResolvedPaint style,
        uint identity,
        bool hovered,
        bool disabled,
        bool ownsBox)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float radius = style.Radius * scale;
        float borderPx = style.BorderWidth * scale;
        // CSS borders paint fully INSIDE the box; ImGui strokes centre on the
        // path, so the path is inset by half a border.
        float inset = borderPx * 0.5f;

        // The ramp advances EVERY frame — a disabled frame drives it toward
        // idle, so disabling while hovered and re-enabling away from the
        // pointer can never replay stale hover fill.
        float eased = hovered ? 1f : 0f;
        if (style.FillTransition is { } transition && identity != 0)
            eased = transition.Evaluate(
                Poser.UI.Motion.Progress(
                    identity, hovered, transition.DurationSeconds));

        bool grouped = disabled && style.GroupOpacity is { } group && group < 1f
            && (style.RestFill is not null || style.Border is not null);
        if (grouped && ownsBox)
        {
            // .btn:disabled is CSS GROUP opacity — the element flattens before
            // the fade applies once. ControlPaint.DisabledGroup owns that
            // recipe, and the label draws with the compensated colour it hands
            // back.
            float fade = style.GroupOpacity!.Value;
            Poser.UI.ControlPaint.DisabledContent content =
                Poser.UI.ControlPaint.DisabledGroup(
                    draw, min, max, radius, borderPx,
                    style.RestFill ?? default, style.Border ?? default, fade);
            return new Result(
                style.Foreground is { } label ? content.Label(label) : null,
                content.Glyph(style.Opacity));
        }

        if (ownsBox)
        {
            if (style.RestFill is not null || style.HoverFill is not null)
            {
                // The background follows its transition with PREMULTIPLIED
                // interpolation, as Chromium interpolates rgba.
                Vector4 background = Poser.UI.ColorEx.PremultipliedLerp(
                    style.RestFill ?? default, style.HoverFill ?? default, eased);
                draw.AddRectFilled(
                    min,
                    max,
                    ImGui.ColorConvertFloat4ToU32(
                        Poser.UI.ColorEx.ApplyAlpha(background)),
                    radius);
            }

            if (style.Border is { } border && borderPx > 0f)
                draw.AddRect(
                    min + new Vector2(inset),
                    max - new Vector2(inset),
                    ImGui.ColorConvertFloat4ToU32(
                        Poser.UI.ColorEx.ApplyAlpha(border)),
                    MathF.Max(0f, radius - inset),
                    ImDrawFlags.None,
                    borderPx);
        }

        // NO focus-visible outline — PRODUCT DECISION (user): this is a
        // native-styled UI, not a web page; Picto's .btn:focus-visible ring is
        // deliberately not reproduced anywhere in Crystarium.
        float opacity = style.Opacity;
        if (grouped)
            opacity *= style.GroupOpacity!.Value;
        return new Result(style.Foreground, opacity);
    }
}
