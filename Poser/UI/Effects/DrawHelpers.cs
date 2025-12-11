using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Effects;

/// <summary>
/// Static helper methods for custom ImGui drawing operations.
/// </summary>
public static class DrawHelpers
{
    /// <summary>
    /// Draws a drop shadow around a rectangle on all four sides with smooth corner gradients.
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="rectMin">Top-left corner of the rectangle.</param>
    /// <param name="rectMax">Bottom-right corner of the rectangle.</param>
    /// <param name="shadowSize">Size of the shadow in pixels (before GlobalScale).</param>
    /// <param name="shadowAlpha">Alpha value for the shadow (0-1).</param>
    public static void DrawDropShadow(ImDrawListPtr drawList, Vector2 rectMin, Vector2 rectMax,
        float shadowSize = 8f, float shadowAlpha = 0.4f)
    {
        float size = shadowSize * ImGuiHelpers.GlobalScale;
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, shadowAlpha));
        var transparent = ImGui.ColorConvertFloat4ToU32(Vector4.Zero);

        // Edge shadows
        DrawEdgeShadow(drawList, rectMin, rectMax, size, shadowColor, transparent, Edge.Left);
        DrawEdgeShadow(drawList, rectMin, rectMax, size, shadowColor, transparent, Edge.Top);
        DrawEdgeShadow(drawList, rectMin, rectMax, size, shadowColor, transparent, Edge.Right);
        DrawEdgeShadow(drawList, rectMin, rectMax, size, shadowColor, transparent, Edge.Bottom);

        // Corner shadows
        DrawRadialGradient(drawList, rectMax, size, shadowColor, transparent, Quadrant.BottomRight);
        DrawRadialGradient(drawList, new Vector2(rectMin.X, rectMax.Y), size, shadowColor, transparent, Quadrant.BottomLeft);
        DrawRadialGradient(drawList, rectMin, size, shadowColor, transparent, Quadrant.TopLeft);
        DrawRadialGradient(drawList, new Vector2(rectMax.X, rectMin.Y), size, shadowColor, transparent, Quadrant.TopRight);
    }

    /// <summary>
    /// Draws a drop shadow with a gap on one edge (e.g., for connected tabs).
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="rectMin">Top-left corner of the rectangle.</param>
    /// <param name="rectMax">Bottom-right corner of the rectangle.</param>
    /// <param name="gapEdge">Which edge has the gap.</param>
    /// <param name="gapStart">Start position of the gap (in screen coordinates along the edge).</param>
    /// <param name="gapEnd">End position of the gap (in screen coordinates along the edge).</param>
    /// <param name="shadowSize">Size of the shadow in pixels (before GlobalScale).</param>
    /// <param name="shadowAlpha">Alpha value for the shadow (0-1).</param>
    public static void DrawDropShadowWithGap(ImDrawListPtr drawList, Vector2 rectMin, Vector2 rectMax,
        Edge gapEdge, float gapStart, float gapEnd, float shadowSize = 8f, float shadowAlpha = 0.4f)
    {
        float size = shadowSize * ImGuiHelpers.GlobalScale;
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, shadowAlpha));
        var transparent = ImGui.ColorConvertFloat4ToU32(Vector4.Zero);

        // Draw edges, splitting the gap edge into two parts
        foreach (Edge edge in Enum.GetValues(typeof(Edge)))
        {
            if (edge == gapEdge)
            {
                DrawEdgeShadowWithGap(drawList, rectMin, rectMax, size, shadowColor, transparent, edge, gapStart, gapEnd);
            }
            else
            {
                DrawEdgeShadow(drawList, rectMin, rectMax, size, shadowColor, transparent, edge);
            }
        }

        // Corner shadows
        DrawRadialGradient(drawList, rectMax, size, shadowColor, transparent, Quadrant.BottomRight);
        DrawRadialGradient(drawList, new Vector2(rectMin.X, rectMax.Y), size, shadowColor, transparent, Quadrant.BottomLeft);
        DrawRadialGradient(drawList, rectMin, size, shadowColor, transparent, Quadrant.TopLeft);
        DrawRadialGradient(drawList, new Vector2(rectMax.X, rectMin.Y), size, shadowColor, transparent, Quadrant.TopRight);
    }

    /// <summary>
    /// Draws a single edge shadow.
    /// </summary>
    public static void DrawEdgeShadow(ImDrawListPtr drawList, Vector2 rectMin, Vector2 rectMax,
        float size, uint shadowColor, uint transparent, Edge edge)
    {
        switch (edge)
        {
            case Edge.Left:
                drawList.AddRectFilledMultiColor(
                    new Vector2(rectMin.X - size, rectMin.Y),
                    new Vector2(rectMin.X, rectMax.Y),
                    transparent, shadowColor, shadowColor, transparent);
                break;
            case Edge.Top:
                drawList.AddRectFilledMultiColor(
                    new Vector2(rectMin.X, rectMin.Y - size),
                    new Vector2(rectMax.X, rectMin.Y),
                    transparent, transparent, shadowColor, shadowColor);
                break;
            case Edge.Right:
                drawList.AddRectFilledMultiColor(
                    new Vector2(rectMax.X, rectMin.Y),
                    new Vector2(rectMax.X + size, rectMax.Y),
                    shadowColor, transparent, transparent, shadowColor);
                break;
            case Edge.Bottom:
                drawList.AddRectFilledMultiColor(
                    new Vector2(rectMin.X, rectMax.Y),
                    new Vector2(rectMax.X, rectMax.Y + size),
                    shadowColor, shadowColor, transparent, transparent);
                break;
        }
    }

    /// <summary>
    /// Draws an edge shadow with a gap (for connected elements like tabs).
    /// </summary>
    public static void DrawEdgeShadowWithGap(ImDrawListPtr drawList, Vector2 rectMin, Vector2 rectMax,
        float size, uint shadowColor, uint transparent, Edge edge, float gapStart, float gapEnd)
    {
        switch (edge)
        {
            case Edge.Left:
                if (gapStart > rectMin.Y)
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(rectMin.X - size, rectMin.Y),
                        new Vector2(rectMin.X, gapStart),
                        transparent, shadowColor, shadowColor, transparent);
                }
                if (gapEnd < rectMax.Y)
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(rectMin.X - size, gapEnd),
                        new Vector2(rectMin.X, rectMax.Y),
                        transparent, shadowColor, shadowColor, transparent);
                }
                break;
            case Edge.Top:
                if (gapStart > rectMin.X)
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(rectMin.X, rectMin.Y - size),
                        new Vector2(gapStart, rectMin.Y),
                        transparent, transparent, shadowColor, shadowColor);
                }
                if (gapEnd < rectMax.X)
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(gapEnd, rectMin.Y - size),
                        new Vector2(rectMax.X, rectMin.Y),
                        transparent, transparent, shadowColor, shadowColor);
                }
                break;
            case Edge.Right:
                if (gapStart > rectMin.Y)
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(rectMax.X, rectMin.Y),
                        new Vector2(rectMax.X + size, gapStart),
                        shadowColor, transparent, transparent, shadowColor);
                }
                if (gapEnd < rectMax.Y)
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(rectMax.X, gapEnd),
                        new Vector2(rectMax.X + size, rectMax.Y),
                        shadowColor, transparent, transparent, shadowColor);
                }
                break;
            case Edge.Bottom:
                if (gapStart > rectMin.X)
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(rectMin.X, rectMax.Y),
                        new Vector2(gapStart, rectMax.Y + size),
                        shadowColor, shadowColor, transparent, transparent);
                }
                if (gapEnd < rectMax.X)
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(gapEnd, rectMax.Y),
                        new Vector2(rectMax.X, rectMax.Y + size),
                        shadowColor, shadowColor, transparent, transparent);
                }
                break;
        }
    }

    /// <summary>
    /// Draws a radial gradient quarter-circle using a triangle fan.
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="center">The center point (corner of the rect).</param>
    /// <param name="radius">The radius of the gradient.</param>
    /// <param name="innerColor">Color at the center.</param>
    /// <param name="outerColor">Color at the edge.</param>
    /// <param name="quadrant">Which quadrant to draw.</param>
    /// <param name="segments">Number of segments for smoothness.</param>
    public static void DrawRadialGradient(ImDrawListPtr drawList, Vector2 center, float radius,
        uint innerColor, uint outerColor, Quadrant quadrant, int segments = 8)
    {
        float startAngle, endAngle;
        switch (quadrant)
        {
            case Quadrant.BottomRight:
                startAngle = 0f;
                endAngle = MathF.PI * 0.5f;
                break;
            case Quadrant.BottomLeft:
                startAngle = MathF.PI * 0.5f;
                endAngle = MathF.PI;
                break;
            case Quadrant.TopLeft:
                startAngle = MathF.PI;
                endAngle = MathF.PI * 1.5f;
                break;
            case Quadrant.TopRight:
                startAngle = MathF.PI * 1.5f;
                endAngle = MathF.PI * 2f;
                break;
            default:
                return;
        }

        DrawRadialGradientArc(drawList, center, radius, innerColor, outerColor, startAngle, endAngle, segments);
    }

    /// <summary>
    /// Draws a radial gradient arc using a triangle fan.
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="center">The center point.</param>
    /// <param name="radius">The radius of the gradient.</param>
    /// <param name="innerColor">Color at the center.</param>
    /// <param name="outerColor">Color at the edge.</param>
    /// <param name="startAngle">Start angle in radians.</param>
    /// <param name="endAngle">End angle in radians.</param>
    /// <param name="segments">Number of segments for smoothness.</param>
    public static void DrawRadialGradientArc(ImDrawListPtr drawList, Vector2 center, float radius,
        uint innerColor, uint outerColor, float startAngle, float endAngle, int segments = 8)
    {
        var uv = ImGui.GetFontTexUvWhitePixel();

        int vtxCount = segments + 2;
        int idxCount = segments * 3;

        uint vtxBase = (uint)drawList.VtxBuffer.Size;
        drawList.PrimReserve(idxCount, vtxCount);

        // Center vertex
        drawList.PrimWriteVtx(center, uv, innerColor);

        // Arc vertices
        float angleStep = (endAngle - startAngle) / segments;
        for (int i = 0; i <= segments; i++)
        {
            float angle = startAngle + i * angleStep;
            var pos = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;
            drawList.PrimWriteVtx(pos, uv, outerColor);
        }

        // Triangle indices
        for (int i = 0; i < segments; i++)
        {
            drawList.PrimWriteIdx((ushort)vtxBase);
            drawList.PrimWriteIdx((ushort)(vtxBase + 1 + i));
            drawList.PrimWriteIdx((ushort)(vtxBase + 2 + i));
        }
    }

    /// <summary>
    /// Draws a full radial gradient circle.
    /// </summary>
    public static void DrawRadialGradientCircle(ImDrawListPtr drawList, Vector2 center, float radius,
        uint innerColor, uint outerColor, int segments = 32)
    {
        DrawRadialGradientArc(drawList, center, radius, innerColor, outerColor, 0f, MathF.PI * 2f, segments);
    }

    /// <summary>
    /// Draws a drop shadow excluding specified edges.
    /// </summary>
    public static void DrawDropShadow(ImDrawListPtr drawList, Vector2 rectMin, Vector2 rectMax,
        Edge excludeEdge, float shadowSize = 8f, float shadowAlpha = 0.4f)
    {
        float size = shadowSize * ImGuiHelpers.GlobalScale;
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, shadowAlpha));
        var transparent = ImGui.ColorConvertFloat4ToU32(Vector4.Zero);

        // Edge shadows (skip excluded)
        if (excludeEdge != Edge.Left)
            DrawEdgeShadow(drawList, rectMin, rectMax, size, shadowColor, transparent, Edge.Left);
        if (excludeEdge != Edge.Top)
            DrawEdgeShadow(drawList, rectMin, rectMax, size, shadowColor, transparent, Edge.Top);
        if (excludeEdge != Edge.Right)
            DrawEdgeShadow(drawList, rectMin, rectMax, size, shadowColor, transparent, Edge.Right);
        if (excludeEdge != Edge.Bottom)
            DrawEdgeShadow(drawList, rectMin, rectMax, size, shadowColor, transparent, Edge.Bottom);

        // Corner shadows (all corners still drawn)
        DrawRadialGradient(drawList, rectMax, size, shadowColor, transparent, Quadrant.BottomRight);
        DrawRadialGradient(drawList, new Vector2(rectMin.X, rectMax.Y), size, shadowColor, transparent, Quadrant.BottomLeft);
        DrawRadialGradient(drawList, rectMin, size, shadowColor, transparent, Quadrant.TopLeft);
        DrawRadialGradient(drawList, new Vector2(rectMax.X, rectMin.Y), size, shadowColor, transparent, Quadrant.TopRight);
    }

    /// <summary>
    /// Draws a left-rounded rectangle border (top, left with curves, bottom, optionally right).
    /// Uses AddRect for consistent line thickness.
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="pos">Top-left position.</param>
    /// <param name="end">Bottom-right position.</param>
    /// <param name="rounding">Corner rounding radius.</param>
    /// <param name="color">Border color.</param>
    /// <param name="includeRight">If true, draws all 4 sides. If false, hides right edge.</param>
    /// <param name="bgColor">Background color to use when hiding the right edge (only used when includeRight=false).</param>
    public static void DrawRoundedLeftBorder(ImDrawListPtr drawList, Vector2 pos, Vector2 end,
        float rounding, uint color, bool includeRight = false, uint bgColor = 0)
    {
        // Use AddRect for consistent line thickness on all edges
        drawList.AddRect(pos, end, color, rounding, ImDrawFlags.RoundCornersLeft, 1f);

        if (!includeRight)
        {
            // Hide the right edge by drawing over it with background color
            drawList.AddRectFilled(
                new Vector2(end.X - 1, pos.Y + 1),
                new Vector2(end.X + 1, end.Y - 1),
                bgColor);
        }
    }

    /// <summary>
    /// Draws a horizontal gradient fading from color at left to transparent.
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="pos">Top-left position.</param>
    /// <param name="size">Size of the gradient area.</param>
    /// <param name="color">Color at the left edge.</param>
    /// <param name="fadeRatio">How far across the gradient extends (0-1).</param>
    /// <param name="useClipRect">Whether to push a clip rect for the area.</param>
    public static void DrawHorizontalGradientFade(ImDrawListPtr drawList, Vector2 pos, Vector2 size,
        Vector4 color, float fadeRatio = 0.5f, bool useClipRect = true)
    {
        var end = pos + size;

        if (useClipRect)
            drawList.PushClipRect(pos, end, true);

        var gradientEnd = new Vector2(pos.X + size.X * fadeRatio, end.Y);
        var colorStart = ImGui.ColorConvertFloat4ToU32(color);
        var colorEnd = ImGui.ColorConvertFloat4ToU32(color with { W = 0f });

        drawList.AddRectFilledMultiColor(
            pos, gradientEnd,
            colorStart, colorEnd, colorEnd, colorStart);

        if (useClipRect)
            drawList.PopClipRect();
    }

    /// <summary>
    /// Draws a rounded rectangle with a horizontal gradient by manipulating vertices.
    /// This properly respects rounded corners unlike AddRectFilledMultiColor.
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="pos">Top-left position.</param>
    /// <param name="size">Size of the rectangle.</param>
    /// <param name="colorLeft">Color at the left edge.</param>
    /// <param name="colorRight">Color at the right edge.</param>
    /// <param name="rounding">Corner rounding radius.</param>
    /// <param name="roundingFlags">Which corners to round.</param>
    public static void DrawRoundedRectWithHorizontalGradient(ImDrawListPtr drawList, Vector2 pos, Vector2 size,
        Vector4 colorLeft, Vector4 colorRight, float rounding, ImDrawFlags roundingFlags = ImDrawFlags.RoundCornersAll)
    {
        var end = pos + size;

        // Clip to prevent AA fringe from spilling outside bounds
        drawList.PushClipRect(pos, end, true);

        // Get vertex count before drawing
        int vtxStart = drawList.VtxBuffer.Size;

        // Draw rounded rect with white placeholder color (vertices will be recolored)
        drawList.AddRectFilled(pos, end, 0xFFFFFFFF, rounding, roundingFlags);

        // Get vertex count after drawing
        int vtxEnd = drawList.VtxBuffer.Size;

        // Shade each vertex based on its X position (horizontal gradient)
        float minX = pos.X;
        float width = size.X;

        unsafe
        {
            var vtxPtr = (ImDrawVert*)drawList.VtxBuffer.Data;
            for (int i = vtxStart; i < vtxEnd; i++)
            {
                float t = (vtxPtr[i].Pos.X - minX) / width; // 0 at left, 1 at right
                t = Math.Clamp(t, 0f, 1f);
                var color = Vector4.Lerp(colorLeft, colorRight, t);
                vtxPtr[i].Col = ImGui.ColorConvertFloat4ToU32(color);
            }
        }

        drawList.PopClipRect();
    }

    /// <summary>
    /// Draws an inner shadow along one edge of a rectangle, with optional gap.
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="edge">Which edge to draw the shadow on.</param>
    /// <param name="areaMin">Top-left of the area.</param>
    /// <param name="areaMax">Bottom-right of the area.</param>
    /// <param name="shadowSize">Size of the shadow (before GlobalScale).</param>
    /// <param name="gapStart">Start of gap in screen coords (-1 for no gap).</param>
    /// <param name="gapEnd">End of gap in screen coords (-1 for no gap).</param>
    /// <param name="shadowAlpha">Alpha value for shadow.</param>
    public static void DrawInnerEdgeShadow(ImDrawListPtr drawList, Edge edge, Vector2 areaMin, Vector2 areaMax,
        float shadowSize, float gapStart = -1, float gapEnd = -1, float shadowAlpha = 0.4f)
    {
        float size = shadowSize * ImGuiHelpers.GlobalScale;
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, shadowAlpha));
        var transparent = ImGui.ColorConvertFloat4ToU32(Vector4.Zero);

        bool hasGap = gapStart >= 0 && gapEnd >= 0;

        switch (edge)
        {
            case Edge.Right:
                // Shadow fades from right edge inward (for tab bar right shadow)
                if (hasGap)
                {
                    // Above gap
                    if (gapStart > areaMin.Y)
                    {
                        drawList.AddRectFilledMultiColor(
                            new Vector2(areaMax.X - size, areaMin.Y),
                            new Vector2(areaMax.X, gapStart),
                            transparent, shadowColor, shadowColor, transparent);
                    }
                    // Below gap
                    if (gapEnd < areaMax.Y)
                    {
                        drawList.AddRectFilledMultiColor(
                            new Vector2(areaMax.X - size, gapEnd),
                            new Vector2(areaMax.X, areaMax.Y),
                            transparent, shadowColor, shadowColor, transparent);
                    }
                }
                else
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(areaMax.X - size, areaMin.Y),
                        areaMax,
                        transparent, shadowColor, shadowColor, transparent);
                }
                break;

            case Edge.Left:
                if (hasGap)
                {
                    if (gapStart > areaMin.Y)
                    {
                        drawList.AddRectFilledMultiColor(
                            areaMin,
                            new Vector2(areaMin.X + size, gapStart),
                            shadowColor, transparent, transparent, shadowColor);
                    }
                    if (gapEnd < areaMax.Y)
                    {
                        drawList.AddRectFilledMultiColor(
                            new Vector2(areaMin.X, gapEnd),
                            new Vector2(areaMin.X + size, areaMax.Y),
                            shadowColor, transparent, transparent, shadowColor);
                    }
                }
                else
                {
                    drawList.AddRectFilledMultiColor(
                        areaMin,
                        new Vector2(areaMin.X + size, areaMax.Y),
                        shadowColor, transparent, transparent, shadowColor);
                }
                break;

            case Edge.Top:
                if (hasGap)
                {
                    if (gapStart > areaMin.X)
                    {
                        drawList.AddRectFilledMultiColor(
                            areaMin,
                            new Vector2(gapStart, areaMin.Y + size),
                            shadowColor, shadowColor, transparent, transparent);
                    }
                    if (gapEnd < areaMax.X)
                    {
                        drawList.AddRectFilledMultiColor(
                            new Vector2(gapEnd, areaMin.Y),
                            new Vector2(areaMax.X, areaMin.Y + size),
                            shadowColor, shadowColor, transparent, transparent);
                    }
                }
                else
                {
                    drawList.AddRectFilledMultiColor(
                        areaMin,
                        new Vector2(areaMax.X, areaMin.Y + size),
                        shadowColor, shadowColor, transparent, transparent);
                }
                break;

            case Edge.Bottom:
                if (hasGap)
                {
                    if (gapStart > areaMin.X)
                    {
                        drawList.AddRectFilledMultiColor(
                            new Vector2(areaMin.X, areaMax.Y - size),
                            new Vector2(gapStart, areaMax.Y),
                            transparent, transparent, shadowColor, shadowColor);
                    }
                    if (gapEnd < areaMax.X)
                    {
                        drawList.AddRectFilledMultiColor(
                            new Vector2(gapEnd, areaMax.Y - size),
                            areaMax,
                            transparent, transparent, shadowColor, shadowColor);
                    }
                }
                else
                {
                    drawList.AddRectFilledMultiColor(
                        new Vector2(areaMin.X, areaMax.Y - size),
                        areaMax,
                        transparent, transparent, shadowColor, shadowColor);
                }
                break;
        }
    }

    /// <summary>
    /// Draws a simple drop shadow for controls (buttons, dropdowns, etc).
    /// Uses 20% opacity and 50% shorter shadow than window shadows.
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="rectMin">Top-left corner of the control.</param>
    /// <param name="rectMax">Bottom-right corner of the control.</param>
    /// <param name="rounding">Corner rounding.</param>
    /// <param name="opacityModifier">Optional modifier to the default 20% opacity (1.0 = 20%).</param>
    public static void DrawControlShadow(ImDrawListPtr drawList, Vector2 rectMin, Vector2 rectMax,
        float rounding = 4f, float opacityModifier = 1f)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float shadowOffset = 1f * scale; // 50% shorter than button's 2f
        float opacity = 0.20f * opacityModifier;
        var shadowColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, opacity));

        drawList.AddRectFilled(
            rectMin + new Vector2(shadowOffset, shadowOffset),
            rectMax + new Vector2(shadowOffset, shadowOffset),
            shadowColor, rounding * scale);
    }

    /// <summary>
    /// Draws a drop shadow for window/panel elements.
    /// Uses 50% opacity for more prominent shadows.
    /// </summary>
    /// <param name="drawList">The draw list to render to.</param>
    /// <param name="rectMin">Top-left corner.</param>
    /// <param name="rectMax">Bottom-right corner.</param>
    /// <param name="shadowSize">Size of the shadow in pixels (before GlobalScale).</param>
    /// <param name="opacityModifier">Optional modifier to the default 50% opacity (1.0 = 50%).</param>
    public static void DrawWindowShadow(ImDrawListPtr drawList, Vector2 rectMin, Vector2 rectMax,
        float shadowSize = 8f, float opacityModifier = 1f)
    {
        DrawDropShadow(drawList, rectMin, rectMax, shadowSize, 0.5f * opacityModifier);
    }

    /// <summary>
    /// Edge direction for shadows.
    /// </summary>
    public enum Edge
    {
        Left,
        Top,
        Right,
        Bottom
    }

    /// <summary>
    /// Quadrant for corner gradients.
    /// </summary>
    public enum Quadrant
    {
        BottomRight = 0,
        BottomLeft = 1,
        TopLeft = 2,
        TopRight = 3
    }
}
