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

    private static void IconInComposited(
        Vector2 min, Vector2 max, TablerIcon icon,
        float opacity = 1f, bool flipX = false, float? strokeWidth = null)
        => DrawIconBox(
            Tabler.Get(icon), min, max, null, 1f, opacity,
            false, flipX, strokeWidth, compositeStroke: true);

    private static void IconInComposited(
        Vector2 min, Vector2 max, string name,
        float opacity = 1f, bool flipX = false, float? strokeWidth = null)
        => DrawIconBox(
            Tabler.Get(name), min, max, null, 1f, opacity,
            false, flipX, strokeWidth, compositeStroke: true);

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
        bool compositeStroke = false)
    {
        if (doc == null)
            return;
        var tint = color ?? ActiveTheme.Text;
        tint.W *= opacity;
        if (disabled)
            tint.W *= ActiveTheme.Chrome.DisabledOpacity;
        SvgBoxCore(
            doc, min, max, tint, contentScale, flipX, strokeWidth,
            compositeStroke);
    }

    /// <summary>Background-SVG entry for box styling: uniform-fits the
    /// document's OWN aspect ratio into the FULL bounds (no icon
    /// squaring), snapped to whole pixels, with the tint passed through
    /// verbatim (null = the document's own colors, unlike icons where
    /// null means theme text).</summary>
    internal static void SvgBox(
        SvgDocument doc, Vector2 min, Vector2 max, Vector4? tint = null)
    {
        var boxMin = ActiveTheme.Optical.Snap(min);
        doc.Render(ImGui.GetWindowDrawList(), boxMin, boxMin + (max - min), tint);
    }

    private static void SvgBoxCore(
        SvgDocument doc, Vector2 min, Vector2 max, Vector4? tint,
        float contentScale, bool flipX, float? strokeWidth,
        bool compositeStroke = false)
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
                strokeWidth);
        else
            doc.Render(
                ImGui.GetWindowDrawList(), boxMin, boxMax, tint, flipX,
                strokeWidth);
    }
}
