using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI;

/// <summary>
/// Internal helper. Renders a BoxStyle's chrome (shadow → fill → border)
/// at an explicit screen-space rectangle. No cursor manipulation.
/// </summary>
internal static class BoxRenderer
{
    public static void Draw(ImDrawListPtr drawList, Vector2 min, Vector2 max, in BoxStyle style)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float radius = style.BorderRadius * scale;

        // ---- Outset shadows (drawn behind chrome) ----
        if (style.BoxShadows != null)
        {
            for (int i = 0; i < style.BoxShadows.Length; i++)
            {
                if (!style.BoxShadows[i].Inset)
                    DrawShadow(drawList, min, max, style.BoxShadows[i], style.BorderRadius);
            }
        }
        if (style.BoxShadow.HasValue && !style.BoxShadow.Value.Inset)
            DrawShadow(drawList, min, max, style.BoxShadow.Value, style.BorderRadius);

        // ---- Background ----
        if (style.BackgroundColor.HasValue)
        {
            var bg = ColorEx.ApplyAlpha(style.BackgroundColor.Value);
            drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bg), radius);
        }

        // ---- Inset shadows (drawn on top of background, inside the box) ----
        if (style.BoxShadows != null)
        {
            for (int i = 0; i < style.BoxShadows.Length; i++)
            {
                if (style.BoxShadows[i].Inset)
                    DrawInsetShadow(drawList, min, max, style.BoxShadows[i], style.BorderRadius);
            }
        }
        if (style.BoxShadow.HasValue && style.BoxShadow.Value.Inset)
            DrawInsetShadow(drawList, min, max, style.BoxShadow.Value, style.BorderRadius);

        // ---- Border ----
        bool hasPerSide = style.BorderTopColor.HasValue || style.BorderRightColor.HasValue
                       || style.BorderBottomColor.HasValue || style.BorderLeftColor.HasValue;
        if (style.BorderWidth > 0f && hasPerSide)
        {
            DrawPerSideBorder(drawList, min, max, style, radius, style.BorderWidth * scale);
        }
    }

    /// <summary>
    /// Draws a border whose four sides can have different colors (CSS border-*-color).
    /// Each side's stroke owns its straight edge plus 45° of each adjacent corner arc,
    /// matching how browsers miter rounded multi-color borders.
    /// </summary>
    private static void DrawPerSideBorder(ImDrawListPtr drawList, Vector2 min, Vector2 max, in BoxStyle style, float radius, float thickness)
    {
        const float PI = System.MathF.PI;
        // CSS borders paint fully INSIDE the box; ImGui strokes center on the path.
        // Inset the path by half the thickness so the outer edge lands on the rect.
        float inset = thickness * 0.5f;
        min += new Vector2(inset, inset);
        max -= new Vector2(inset, inset);
        float r = System.MathF.Min(
            System.MathF.Max(0f, radius - inset),
            System.MathF.Min((max.X - min.X) * 0.5f, (max.Y - min.Y) * 0.5f));
        var tl = new Vector2(min.X + r, min.Y + r);
        var tr = new Vector2(max.X - r, min.Y + r);
        var br = new Vector2(max.X - r, max.Y - r);
        var bl = new Vector2(min.X + r, max.Y - r);

        DrawSide(style.BorderTopColor, tl, 1.25f * PI, 1.5f * PI, tr, 1.5f * PI, 1.75f * PI);
        DrawSide(style.BorderRightColor, tr, 1.75f * PI, 2f * PI, br, 0f, 0.25f * PI);
        DrawSide(style.BorderBottomColor, br, 0.25f * PI, 0.5f * PI, bl, 0.5f * PI, 0.75f * PI);
        DrawSide(style.BorderLeftColor, bl, 0.75f * PI, PI, tl, PI, 1.25f * PI);

        void DrawSide(Vector4? color, Vector2 c1, float a1Start, float a1End, Vector2 c2, float a2Start, float a2End)
        {
            if (!color.HasValue) return;
            // A side whose straight run has vanished — the radius clamped to
            // half the box, i.e. a pill's cap — has both of its corner arcs on
            // the SAME centre. Two PathArcTo calls then repeat the vertex at
            // the seam, and ImGui's AA stroke cannot normalize a zero-length
            // segment: the miter collapses and that one pixel is painted
            // several times over, several times too bright. One arc across
            // the whole span is the identical geometry without the duplicate.
            if (Vector2.DistanceSquared(c1, c2) < 1e-6f)
            {
                // a2End is the continuation of a1Start, so unwrap it past the
                // 2π seam the right-hand side straddles.
                float end = a2End >= a1Start ? a2End : a2End + 2f * PI;
                drawList.PathArcTo(c1, r, a1Start, end);
            }
            else
            {
                drawList.PathArcTo(c1, r, a1Start, a1End);
                drawList.PathArcTo(c2, r, a2Start, a2End);
            }
            drawList.PathStroke(
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color.Value)),
                ImDrawFlags.None, thickness);
        }
    }

    private static void DrawShadow(ImDrawListPtr drawList, Vector2 min, Vector2 max, in BoxShadow sh, float baseRadius)
    {
        float scale = ImGuiHelpers.GlobalScale;
        var offset = new Vector2(sh.OffsetX, sh.OffsetY) * scale;
        float spread = sh.Spread * scale;
        float blur = sh.Blur * scale;
        float radius = (baseRadius + sh.Spread) * scale;

        var sMin = min + offset - new Vector2(spread, spread);
        var sMax = max + offset + new Vector2(spread, spread);

        if (blur <= 0f)
        {
            drawList.AddRectFilled(sMin, sMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(sh.Color)),
                radius);
            return;
        }

        // CSS box-shadow: alpha falls from color.a to 0 across ±blur around the
        // spread edge with a gaussian-like sigmoid. Rendered as a solid core plus
        // NON-overlapping 1px rings whose alpha follows 1−smoothstep — the old
        // additive stacked-fill approximation piled alpha near the box and read
        // as a hard dark ring.
        float core = blur; // core = spread edge − blur
        var cMin = sMin + new Vector2(core, core);
        var cMax = sMax - new Vector2(core, core);
        if (cMax.X > cMin.X && cMax.Y > cMin.Y)
        {
            drawList.AddRectFilled(cMin, cMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(sh.Color)),
                System.MathF.Max(0f, radius - core));
        }

        int rings = System.Math.Max(2, (int)(2f * blur));
        float step = 2f * blur / rings;
        for (int i = 0; i < rings; i++)
        {
            // e: expansion relative to the core edge, from 0 to 2·blur
            float e = (i + 0.5f) * step;
            float t = e / (2f * blur);
            float alpha = sh.Color.W * (1f - t * t * (3f - 2f * t)); // 1 − smoothstep
            if (alpha <= 0.003f) continue;
            var col = sh.Color with { W = alpha };
            drawList.AddRect(
                cMin - new Vector2(e, e),
                cMax + new Vector2(e, e),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(col)),
                System.MathF.Max(0f, radius - core) + e,
                ImDrawFlags.None,
                step + 0.5f); // slight overlap between rings avoids gaps from AA
        }
    }

    private static void DrawInsetShadow(ImDrawListPtr drawList, Vector2 min, Vector2 max, in BoxShadow sh, float baseRadius)
    {
        // Inset: draw within the box, tinted toward the inset color around the edges.
        // Cheap approximation: draw an inset border tinted with the shadow color.
        float scale = ImGuiHelpers.GlobalScale;
        float radius = baseRadius * scale;
        float thickness = System.Math.Max(1f, sh.Blur) * scale;
        var color = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(sh.Color));
        // Pull the rect in slightly so the inset stroke sits inside the border.
        drawList.AddRect(
            min + new Vector2(0.5f, 0.5f),
            max - new Vector2(0.5f, 0.5f),
            color, radius, ImDrawFlags.None, thickness);
    }
}
