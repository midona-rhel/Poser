using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Draws the solid fill and stroke subset used by Poser's embedded icons.
/// </summary>
internal static class SvgRenderer
{
    public static void Render(
        ImDrawListPtr drawList,
        IReadOnlyList<SvgPath> paths,
        Func<Vector2, Vector2> svgToScreen,
        float scale,
        Vector4? tint)
    {
        foreach (var path in paths)
        {
            foreach (var subPath in path.SubPaths)
            {
                if (subPath.Points.Count < 2)
                    continue;

                var screenPoints = new List<Vector2>(subPath.Points.Count);
                foreach (var point in subPath.Points)
                    screenPoints.Add(svgToScreen(point));

                if (subPath.Closed &&
                    screenPoints.Count >= 3 &&
                    path.Fill is { } fill)
                {
                    DrawFill(
                        drawList,
                        screenPoints,
                        tint.HasValue ? Multiply(fill, tint.Value) : fill);
                }

                if (path.Stroke is { } stroke && path.StrokeWidth > 0f)
                {
                    DrawStroke(
                        drawList,
                        screenPoints,
                        tint.HasValue ? Multiply(stroke, tint.Value) : stroke,
                        path.StrokeWidth * scale,
                        subPath.Closed);
                }
            }
        }
    }

    private static void DrawFill(
        ImDrawListPtr drawList,
        List<Vector2> points,
        Vector4 color)
    {
        var packed = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color));

        if (SvgTessellator.IsConvex(points))
        {
            unsafe
            {
                fixed (Vector2* pointer =
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points))
                {
                    drawList.AddConvexPolyFilled(pointer, points.Count, packed);
                }
            }
            return;
        }

        var indices = SvgTessellator.Triangulate(points);
        for (var index = 0; index + 2 < indices.Count; index += 3)
        {
            drawList.AddTriangleFilled(
                points[indices[index]],
                points[indices[index + 1]],
                points[indices[index + 2]],
                packed);
        }
    }

    private static void DrawStroke(
        ImDrawListPtr drawList,
        List<Vector2> points,
        Vector4 color,
        float width,
        bool closed)
    {
        var packed = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color));
        unsafe
        {
            fixed (Vector2* pointer =
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(points))
            {
                drawList.AddPolyline(
                    pointer,
                    points.Count,
                    packed,
                    closed ? ImDrawFlags.Closed : ImDrawFlags.None,
                    width);
            }
        }
    }

    private static Vector4 Multiply(Vector4 left, Vector4 right) =>
        new(
            left.X * right.X,
            left.Y * right.Y,
            left.Z * right.Z,
            left.W * right.W);
}
