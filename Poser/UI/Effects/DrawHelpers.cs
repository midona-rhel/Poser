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
