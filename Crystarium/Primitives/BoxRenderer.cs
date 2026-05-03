using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI.Controls;
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
        float scale = PoserUI.Scale;
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
            var bg = UIColors.ApplyAlpha(style.BackgroundColor.Value);
            drawList.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(bg), radius);
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
        if (style.BorderWidth > 0f && style.BorderColor.HasValue)
        {
            var borderU32 = ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(style.BorderColor.Value));
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
                ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(o.Color)),
                radius + off, ImDrawFlags.None, w);
        }
    }

    private static void DrawShadow(ImDrawListPtr drawList, Vector2 min, Vector2 max, in BoxShadow sh, float baseRadius)
    {
        float scale = PoserUI.Scale;
        var offset = new Vector2(sh.OffsetX, sh.OffsetY) * scale;
        float spread = sh.Spread * scale;
        float radius = (baseRadius + sh.Spread) * scale;

        var sMin = min + offset - new Vector2(spread, spread);
        var sMax = max + offset + new Vector2(spread, spread);

        if (sh.Blur > 0f)
        {
            // Approximate soft shadow with multiple decreasing-alpha rects (poor man's gaussian).
            int steps = System.Math.Max(2, (int)(sh.Blur));
            for (int i = steps; i >= 1; i--)
            {
                float t = i / (float)steps;
                float blurExpand = sh.Blur * scale * t;
                var bMin = sMin - new Vector2(blurExpand, blurExpand);
                var bMax = sMax + new Vector2(blurExpand, blurExpand);
                var col = sh.Color;
                col.W *= (1f - t) * 0.5f;
                drawList.AddRectFilled(bMin, bMax,
                    ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(col)),
                    radius + blurExpand);
            }
        }
        else
        {
            drawList.AddRectFilled(sMin, sMax,
                ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(sh.Color)),
                radius);
        }
    }

    private static void DrawInsetShadow(ImDrawListPtr drawList, Vector2 min, Vector2 max, in BoxShadow sh, float baseRadius)
    {
        // Inset: draw within the box, tinted toward the inset color around the edges.
        // Cheap approximation: draw an inset border tinted with the shadow color.
        float scale = PoserUI.Scale;
        float radius = baseRadius * scale;
        float thickness = System.Math.Max(1f, sh.Blur) * scale;
        var color = ImGui.ColorConvertFloat4ToU32(UIColors.ApplyAlpha(sh.Color));
        // Pull the rect in slightly so the inset stroke sits inside the border.
        drawList.AddRect(
            min + new Vector2(0.5f, 0.5f),
            max - new Vector2(0.5f, 0.5f),
            color, radius, ImDrawFlags.None, thickness);
    }

    private static void DrawGradient(ImDrawListPtr drawList, Vector2 min, Vector2 max, in Gradient g, float radius)
    {
        var start = UIColors.ApplyAlpha(g.Start);
        var end = UIColors.ApplyAlpha(g.End);

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
}
