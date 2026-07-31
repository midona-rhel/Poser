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
        Vector4? tint,
        float? strokeWidthOverride = null,
        bool compositeStroke = false,
        float groupOpacity = 1f,
        Vector4 groupBackground = default)
    {
        bool useStrokeMask = compositeStroke;
        Vector4? compositeColor = null;
        if (useStrokeMask)
        {
            foreach (var path in paths)
            {
                if (path.Fill.HasValue)
                {
                    // Preserve established paint ordering for custom
                    // filled/multicolor SVGs. IconButton's Tabler outlines
                    // are stroke-only and take the composited mask path.
                    useStrokeMask = false;
                    break;
                }
                if (path.Stroke is { } stroke)
                {
                    if (compositeColor.HasValue
                        && compositeColor.Value != stroke)
                    {
                        useStrokeMask = false;
                        break;
                    }
                    compositeColor = stroke;
                }
            }
            useStrokeMask &= compositeColor.HasValue;
        }

        if (useStrokeMask)
        {
            SvgStrokeMask.Draw(
                drawList, paths, svgToScreen, scale, tint,
                strokeWidthOverride, groupOpacity, groupBackground);
        }

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

                // strokeWidthOverride mirrors the Tabler React `stroke`
                // prop: one value replacing every path's own width, in
                // viewBox units.
                float strokeWidth = strokeWidthOverride ?? path.StrokeWidth;
                if (path.Stroke is { } stroke && strokeWidth > 0f)
                {
                    if (!useStrokeMask)
                        DrawStroke(
                        drawList,
                        screenPoints,
                        tint.HasValue ? Multiply(stroke, tint.Value) : stroke,
                        strokeWidth * scale,
                        subPath.Closed,
                        path.RoundCaps,
                        path.RoundJoins);
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
        bool closed,
        bool roundCaps,
        bool roundJoins)
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

        // ImGui polylines have butt ends and miter-ish joins; SVG round
        // caps/joins add a half-disc of ink at every open end and every
        // significant corner. A filled circle of the stroke radius is
        // exactly that ink. Shallow joins keep their circles inside the
        // stroke, so only real corners get one.
        if (!roundCaps && !roundJoins)
            return;
        float radius = width * 0.5f;
        if (radius <= 0f)
            return;
        if (roundCaps && !closed)
        {
            drawList.AddCircleFilled(points[0], radius, packed);
            drawList.AddCircleFilled(points[^1], radius, packed);
        }
        if (roundJoins && points.Count >= 3)
        {
            // A closed path's vertex 0 is a corner too — the closing
            // segment meets the first there, so it rounds like the rest.
            int first = closed ? 0 : 1;
            int last = closed ? points.Count : points.Count - 1;
            for (int i = first; i < last; i++)
            {
                var previous = points[(i - 1 + points.Count) % points.Count];
                var current = points[i % points.Count];
                var next = points[(i + 1) % points.Count];
                var incoming = Vector2.Normalize(current - previous);
                var outgoing = Vector2.Normalize(next - current);
                if (Vector2.Dot(incoming, outgoing) < 0.94f)
                    drawList.AddCircleFilled(current, radius, packed);
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
