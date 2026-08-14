using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Inline (cursor-flow) Tabler icon at a LOGICAL CSS-pixel size —
    /// the same size semantics as <see cref="TextStyle"/>; UI scaling
    /// happens once inside the renderer. Picto renders bare Tabler SVGs
    /// (24-grid, round caps/joins) sized per call site, so size, tint,
    /// opacity, disabled treatment, and the optional stroke-width
    /// override (the Tabler React <c>stroke</c> prop) all live here.
    /// </summary>
    public static void Icon(
        TablerIcon icon, float size, Vector4? color = null, bool flipX = false,
        bool disabled = false, float opacity = 1f, float? strokeWidth = null)
        => IconInline(Tabler.Get(icon), size, color, flipX, disabled, opacity, strokeWidth);

    /// <summary>Inline icon by registered name.</summary>
    public static void Icon(
        string name, float size, Vector4? color = null, bool flipX = false,
        bool disabled = false, float opacity = 1f, float? strokeWidth = null)
        => IconInline(Tabler.Get(name), size, color, flipX, disabled, opacity, strokeWidth);

    /// <summary>
    /// Composed-control icon: fits and centers the glyph inside a
    /// SCREEN-space box (already scaled by the owning control), through
    /// the same canonical geometry path the inline form uses.
    /// <paramref name="contentScale"/> shrinks the glyph square inside
    /// the box (button glyph inset); 1 fills the box.
    /// </summary>
    public static void IconIn(
        Vector2 min, Vector2 max, TablerIcon icon, Vector4? color = null,
        float contentScale = 1f, float opacity = 1f, bool disabled = false,
        bool flipX = false, float? strokeWidth = null)
        => DrawIconBox(
            Tabler.Get(icon), min, max, color, contentScale, opacity,
            disabled, flipX, strokeWidth);

    /// <summary><paramref name="filled"/> asks for the glyph's SOLID twin —
    /// the latched on-state's whole vocabulary. It falls back to the outline
    /// when Tabler ships no filled variant, so a caller never has to know
    /// which glyphs have one.</summary>
    private static void IconInComposited(
        Vector2 min, Vector2 max, TablerIcon icon,
        float opacity = 1f, Vector4 background = default,
        bool flipX = false, float? strokeWidth = null,
        bool filled = false)
        => DrawIconBox(
            filled ? Tabler.GetFilled(icon) : Tabler.Get(icon),
            min, max, null, 1f, 1f,
            false, flipX, strokeWidth, compositeStroke: true,
            groupOpacity: opacity, groupBackground: background);

    /// <inheritdoc cref="IconInComposited(Vector2, Vector2, TablerIcon, float, Vector4, bool, float?, bool)"/>
    private static void IconInComposited(
        Vector2 min, Vector2 max, string name,
        float opacity = 1f, Vector4 background = default,
        bool flipX = false, float? strokeWidth = null,
        bool filled = false)
        => DrawIconBox(
            filled ? Tabler.GetFilled(name) : Tabler.Get(name),
            min, max, null, 1f, 1f,
            false, flipX, strokeWidth, compositeStroke: true,
            groupOpacity: opacity, groupBackground: background);

    /// <summary>Composed-control icon by registered name.</summary>
    public static void IconIn(
        Vector2 min, Vector2 max, string name, Vector4? color = null,
        float contentScale = 1f, float opacity = 1f, bool disabled = false,
        bool flipX = false, float? strokeWidth = null)
        => DrawIconBox(
            Tabler.Get(name), min, max, color, contentScale, opacity,
            disabled, flipX, strokeWidth);

    private static void IconInline(
        SvgDocument? doc, float size, Vector4? color, bool flipX,
        bool disabled, float opacity, float? strokeWidth)
    {
        float side = size * ImGuiHelpers.GlobalScale;
        var min = ImGui.GetCursorScreenPos();
        DrawIconBox(
            doc, min, min + new Vector2(side), color, 1f, opacity,
            disabled, flipX, strokeWidth);
        ImGui.Dummy(new Vector2(side));
    }

    /// <summary>The one icon geometry path: min-side square fit, center,
    /// whole-pixel snap, tint composition (theme text default × opacity ×
    /// disabled opacity), and stroke scaling inside the SVG renderer.</summary>
    private static void DrawIconBox(
        SvgDocument? doc, Vector2 min, Vector2 max, Vector4? color,
        float contentScale, float opacity, bool disabled, bool flipX,
        float? strokeWidth,
        bool compositeStroke = false,
        float groupOpacity = 1f,
        Vector4 groupBackground = default)
    {
        if (doc == null)
            return;
        var tint = (color ?? ActiveTheme.Text).Fade(opacity);
        if (disabled)
            tint = tint.Fade(ActiveTheme.Chrome.DisabledOpacity);
        SvgBoxCore(
            doc, min, max, tint, contentScale, flipX, strokeWidth,
            compositeStroke, groupOpacity, groupBackground);
    }

    private static void SvgBoxCore(
        SvgDocument doc, Vector2 min, Vector2 max, Vector4? tint,
        float contentScale, bool flipX, float? strokeWidth,
        bool compositeStroke = false,
        float groupOpacity = 1f,
        Vector4 groupBackground = default)
    {
        float side = MathF.Min(max.X - min.X, max.Y - min.Y) * contentScale;
        if (side <= 0f)
            return;
        var center = (min + max) * 0.5f;
        var boxMin = ActiveTheme.Optical.Snap(center - new Vector2(side) * 0.5f);
        var boxMax = boxMin + new Vector2(side);
        if (compositeStroke)
            doc.RenderComposited(
                ImGui.GetWindowDrawList(), boxMin, boxMax, tint, flipX,
                strokeWidth, groupOpacity, groupBackground);
        else
            doc.Render(
                ImGui.GetWindowDrawList(), boxMin, boxMax, tint, flipX,
                strokeWidth);
    }
}
