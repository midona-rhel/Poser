using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// THE color-math home. Compositing, interpolation and alpha helpers used
/// by chrome painters and tags. Independent of <see cref="Theme"/>; works
/// on raw <see cref="Vector4"/> / U32 values.
/// </summary>
public static class ColorEx
{
    /// <summary>Applies the current ImGui style alpha to a Vector4 color.</summary>
    public static Vector4 ApplyAlpha(Vector4 color)
    {
        float alpha = ImGui.GetStyle().Alpha;
        if (alpha >= 1f) return color;
        return color with { W = color.W * alpha };
    }

    /// <summary>Applies the current ImGui style alpha to a U32 color.</summary>
    public static uint ApplyAlpha(uint color)
    {
        float alpha = ImGui.GetStyle().Alpha;
        if (alpha >= 1f) return color;
        var vec = ImGui.ColorConvertU32ToFloat4(color);
        vec.W *= alpha;
        return ImGui.ColorConvertFloat4ToU32(vec);
    }

    /// <summary>
    /// THE opacity multiply: scales a color's alpha by <paramref name="opacity"/>.
    /// This is the canonical spelling for every "fade this color" site —
    /// disabled states, hover dimming, secondary text — so the operation
    /// reads the same everywhere. It does NOT decide WHICH opacity a
    /// control uses; that stays at the call site.
    /// </summary>
    public static Vector4 Fade(this Vector4 color, float opacity)
        => color with { W = color.W * opacity };

    /// <summary>Top layer composited over the bottom layer (source-over),
    /// returned straight-alpha — the flattened color a CSS element shows
    /// where the two overlap before any group opacity applies.</summary>
    internal static Vector4 FlattenOver(Vector4 top, Vector4 bottom)
    {
        float alpha = top.W + bottom.W * (1f - top.W);
        if (alpha <= 0f)
            return default;
        var rgb = (new Vector3(top.X, top.Y, top.Z) * top.W
            + new Vector3(bottom.X, bottom.Y, bottom.Z)
                * bottom.W * (1f - top.W)) / alpha;
        return new Vector4(rgb, alpha);
    }

    /// <summary>
    /// Compensated label color/alpha for the disabled group: drawing
    /// glyphs at coverage c over the ALREADY-faded fill must equal the
    /// CSS flatten-then-fade result. For fill alpha &lt; 1 the solution is
    /// exact for every backdrop: alpha = o(1−af)/(1−o·af) and the color
    /// absorbs the excess fill contribution. An opaque fill admits no
    /// backdrop-independent solution, so it references the theme
    /// surface instead.
    /// </summary>
    internal static Vector4 DisabledLabelCompensation(
        Vector4 text, Vector4 fill, Vector4 surface, float groupOpacity)
    {
        float af = fill.W;
        if (af < 0.999f)
        {
            float alpha = groupOpacity * (1f - af) / (1f - groupOpacity * af);
            var rgb = (new Vector3(text.X, text.Y, text.Z) * groupOpacity
                - new Vector3(fill.X, fill.Y, fill.Z)
                    * (groupOpacity * af * (1f - alpha))) / alpha;
            return new Vector4(
                Math.Clamp(rgb.X, 0f, 1f),
                Math.Clamp(rgb.Y, 0f, 1f),
                Math.Clamp(rgb.Z, 0f, 1f),
                alpha * text.W);
        }
        var opaque = new Vector3(text.X, text.Y, text.Z)
            - (new Vector3(fill.X, fill.Y, fill.Z)
                - new Vector3(surface.X, surface.Y, surface.Z))
                * (1f - groupOpacity);
        return new Vector4(
            Math.Clamp(opaque.X, 0f, 1f),
            Math.Clamp(opaque.Y, 0f, 1f),
            Math.Clamp(opaque.Z, 0f, 1f),
            groupOpacity * text.W);
    }

    /// <summary>Premultiplied-alpha interpolation — how Chromium
    /// transitions between rgba backgrounds of different alpha.</summary>
    internal static Vector4 PremultipliedLerp(Vector4 from, Vector4 to, float t)
    {
        float alpha = from.W + (to.W - from.W) * t;
        if (alpha <= 0f)
            return default;
        var rgb = (new Vector3(from.X, from.Y, from.Z) * from.W * (1f - t)
            + new Vector3(to.X, to.Y, to.Z) * to.W * t) / alpha;
        return new Vector4(rgb, alpha);
    }
}
