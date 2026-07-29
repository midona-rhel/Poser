using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.UI.Effects;

namespace Poser.UI;

/// <summary>
/// Internal helper. Renders a BoxStyle's chrome (shadow → fill → gradient → border)
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
        if (style.BackgroundGradient.HasValue)
        {
            DrawGradient(drawList, min, max, style.BackgroundGradient.Value, radius);
        }
        else if (style.BackgroundColor.HasValue)
        {
            var bg = ColorEx.ApplyAlpha(style.BackgroundColor.Value);
            drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bg), radius);
        }

        // ---- Background image (over fill, under border) ----
        if (style.BackgroundImage is { IsLoaded: true } img)
        {
            DrawBackgroundImage(drawList, min, max, img, style.BackgroundImageFit ?? ImageFit.Cover, radius);
        }

        // ---- Background SVG (canonical fit/center/snap geometry) ----
        if (style.BackgroundSvg is { } svg)
        {
            bool clipPushed = false;
            if (radius > 0f)
            {
                drawList.PushClipRect(min, max, true);
                clipPushed = true;
            }
            Crystarium.SvgBox(svg, min, max);
            if (clipPushed) drawList.PopClipRect();
        }

        if (style.RaisedGradient)
        {
            float height = max.Y - min.Y;
            DrawHelpers.DrawButtonGradients(drawList, min, max, height, style.BorderRadius);
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
        else if (style.BorderWidth > 0f && style.BorderColor.HasValue)
        {
            var borderU32 = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(style.BorderColor.Value));
            drawList.AddRect(min, max, borderU32, radius, ImDrawFlags.None, style.BorderWidth * scale);
        }

        // ---- Outline (drawn outside border, doesn't take space) ----
        if (style.Outline.HasValue)
        {
            var o = style.Outline.Value;
            float off = o.Offset * scale;
            float w = o.Width * scale;
            var oMin = min - new Vector2(off, off);
            var oMax = max + new Vector2(off, off);
            drawList.AddRect(oMin, oMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(o.Color)),
                radius + off, ImDrawFlags.None, w);
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

        var fallback = style.BorderColor;
        DrawSide(style.BorderTopColor ?? fallback, tl, 1.25f * PI, 1.5f * PI, tr, 1.5f * PI, 1.75f * PI);
        DrawSide(style.BorderRightColor ?? fallback, tr, 1.75f * PI, 2f * PI, br, 0f, 0.25f * PI);
        DrawSide(style.BorderBottomColor ?? fallback, br, 0.25f * PI, 0.5f * PI, bl, 0.5f * PI, 0.75f * PI);
        DrawSide(style.BorderLeftColor ?? fallback, bl, 0.75f * PI, PI, tl, PI, 1.25f * PI);

        void DrawSide(Vector4? color, Vector2 c1, float a1Start, float a1End, Vector2 c2, float a2Start, float a2End)
        {
            if (!color.HasValue) return;
            drawList.PathArcTo(c1, r, a1Start, a1End);
            drawList.PathArcTo(c2, r, a2Start, a2End);
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

    private static void DrawGradient(ImDrawListPtr drawList, Vector2 min, Vector2 max, in Gradient g, float radius)
    {
        var start = ColorEx.ApplyAlpha(g.Start);
        var end = ColorEx.ApplyAlpha(g.End);

        // Pick four-corner colors based on direction.
        Vector4 tl, tr, br, bl;
        switch (g.Direction)
        {
            case GradientDirection.ToBottom:      tl = start; tr = start; br = end;   bl = end;   break;
            case GradientDirection.ToTop:         tl = end;   tr = end;   br = start; bl = start; break;
            case GradientDirection.ToRight:       tl = start; tr = end;   br = end;   bl = start; break;
            case GradientDirection.ToLeft:        tl = end;   tr = start; br = start; bl = end;   break;
            case GradientDirection.ToBottomRight: tl = start; tr = Mid(start, end); br = end; bl = Mid(start, end); break;
            case GradientDirection.ToTopLeft:     tl = end;   tr = Mid(start, end); br = start; bl = Mid(start, end); break;
            default:                              tl = start; tr = start; br = end;   bl = end;   break;
        }

        if (radius <= 0f)
        {
            drawList.AddRectFilledMultiColor(min, max,
                ImGui.ColorConvertFloat4ToU32(tl),
                ImGui.ColorConvertFloat4ToU32(tr),
                ImGui.ColorConvertFloat4ToU32(br),
                ImGui.ColorConvertFloat4ToU32(bl));
        }
        else
        {
            // Rounded gradient: render rounded base in start color, then multi-color quad on top
            // with the same vertices retinted via the AddRect path. Approximation; for higher fidelity
            // use DrawHelpers.DrawRoundedRectWithHorizontalGradient for horizontal cases.
            if (g.Direction == GradientDirection.ToRight || g.Direction == GradientDirection.ToLeft)
            {
                var size = max - min;
                var left = g.Direction == GradientDirection.ToRight ? start : end;
                var right = g.Direction == GradientDirection.ToRight ? end : start;
                DrawHelpers.DrawRoundedRectWithHorizontalGradient(drawList, min, size, left, right, radius);
            }
            else
            {
                drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(start), radius);
                drawList.AddRectFilledMultiColor(min, max,
                    ImGui.ColorConvertFloat4ToU32(tl),
                    ImGui.ColorConvertFloat4ToU32(tr),
                    ImGui.ColorConvertFloat4ToU32(br),
                    ImGui.ColorConvertFloat4ToU32(bl));
            }
        }
    }

    private static Vector4 Mid(Vector4 a, Vector4 b) => new((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f, (a.W + b.W) * 0.5f);

    private static void DrawBackgroundImage(ImDrawListPtr drawList, Vector2 min, Vector2 max, IImageSource img, ImageFit fit, float radius)
    {
        var boxSize = max - min;
        var imgSize = img.Size;
        if (imgSize.X <= 0f || imgSize.Y <= 0f) return;

        // Default UV covers the full texture; Cover/Contain may shift to crop / letterbox.
        Vector2 uvMin = Vector2.Zero;
        Vector2 uvMax = Vector2.One;
        Vector2 dstMin = min;
        Vector2 dstMax = max;

        switch (fit)
        {
            case ImageFit.Fill:
                // Stretch to box; UV unchanged.
                break;

            case ImageFit.Cover:
            {
                float boxAspect = boxSize.X / boxSize.Y;
                float imgAspect = imgSize.X / imgSize.Y;
                if (imgAspect > boxAspect)
                {
                    // image is wider than box — crop sides
                    float cropU = (1f - boxAspect / imgAspect) * 0.5f;
                    uvMin.X = cropU;
                    uvMax.X = 1f - cropU;
                }
                else
                {
                    // image is taller than box — crop top/bottom
                    float cropV = (1f - imgAspect / boxAspect) * 0.5f;
                    uvMin.Y = cropV;
                    uvMax.Y = 1f - cropV;
                }
                break;
            }

            case ImageFit.Contain:
            {
                float boxAspect = boxSize.X / boxSize.Y;
                float imgAspect = imgSize.X / imgSize.Y;
                if (imgAspect > boxAspect)
                {
                    float fittedHeight = boxSize.X / imgAspect;
                    float padY = (boxSize.Y - fittedHeight) * 0.5f;
                    dstMin.Y = min.Y + padY;
                    dstMax.Y = max.Y - padY;
                }
                else
                {
                    float fittedWidth = boxSize.Y * imgAspect;
                    float padX = (boxSize.X - fittedWidth) * 0.5f;
                    dstMin.X = min.X + padX;
                    dstMax.X = max.X - padX;
                }
                break;
            }

            case ImageFit.None:
                dstMax = min + imgSize;
                break;
        }

        // Honor border radius via clip rect when needed; AddImage doesn't round corners.
        bool clipPushed = false;
        if (radius > 0f)
        {
            drawList.PushClipRect(min, max, true);
            clipPushed = true;
        }
        drawList.AddImage(img.TextureHandle, dstMin, dstMax, uvMin, uvMax);
        if (clipPushed) drawList.PopClipRect();
    }
}
