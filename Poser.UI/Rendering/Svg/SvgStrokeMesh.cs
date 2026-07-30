using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Builds one non-overlapping stroke mesh. Unlike layering translucent
/// polylines and circles, this applies opacity once and is independent of
/// whatever destination is behind the SVG.
/// </summary>
internal static class SvgStrokeMesh
{
    private readonly record struct Section(
        Vector2 Left, Vector2 Right, Vector2 LeftFringe, Vector2 RightFringe);

    public static void Draw(
        ImDrawListPtr draw, List<Vector2> source, Vector4 color, float width,
        bool closed, bool roundCaps, bool roundJoins)
    {
        var points = Clean(source);
        if (points.Count < 2 || width <= 0f)
            return;
        float radius = width * 0.5f;
        if (Vector2.DistanceSquared(points[0], points[^1])
            <= radius * radius * 0.04f)
        {
            points.RemoveAt(points.Count - 1);
            closed = true;
        }

        // Offset-line intersections form a continuous, non-overlapping
        // join. At icon scale the bounded miter is visually equivalent to
        // the SVG round join while avoiding fan self-intersections on
        // tightly flattened curves.
        var sections = BuildSections(points, radius, closed, 1f);
        if (sections.Count < 2)
            return;

        uint solid = ImGui.ColorConvertFloat4ToU32(
            ColorEx.ApplyAlpha(color));
        uint clear = solid & 0x00FFFFFFu;
        int pairs = closed ? sections.Count : sections.Count - 1;
        for (int i = 0; i < pairs; i++)
        {
            var a = sections[i];
            var b = sections[(i + 1) % sections.Count];
            Quad(draw, a.Left, a.Right, b.Right, b.Left, solid, solid);
            Quad(
                draw, a.LeftFringe, a.Left, b.Left, b.LeftFringe,
                clear, solid);
            Quad(
                draw, a.Right, a.RightFringe, b.RightFringe, b.Right,
                solid, clear);
        }

        if (!closed && roundCaps)
        {
            var firstDirection = Vector2.Normalize(points[1] - points[0]);
            var lastDirection =
                Vector2.Normalize(points[^1] - points[^2]);
            Cap(draw, points[0], radius, firstDirection, false, solid, clear);
            Cap(draw, points[^1], radius, lastDirection, true, solid, clear);
        }
    }

    private static List<Vector2> Clean(List<Vector2> source)
    {
        var result = new List<Vector2>(source.Count);
        foreach (var point in source)
            if (result.Count == 0
                || Vector2.DistanceSquared(result[^1], point) > 0.0001f)
                result.Add(point);
        return result;
    }

    private static List<Section> BuildSections(
        List<Vector2> points, float radius, bool closed, float fringe)
    {
        var result = new List<Section>(points.Count);
        int start = closed ? 0 : 1;
        if (!closed)
        {
            var direction = Vector2.Normalize(points[1] - points[0]);
            var normal = Normal(direction);
            result.Add(Make(points[0], normal, normal, radius, fringe));
        }

        int end = closed ? points.Count : points.Count - 1;
        for (int i = start; i < end; i++)
        {
            int previousIndex = (i - 1 + points.Count) % points.Count;
            int nextIndex = (i + 1) % points.Count;
            var incoming =
                Vector2.Normalize(points[i] - points[previousIndex]);
            var outgoing =
                Vector2.Normalize(points[nextIndex] - points[i]);
            var previousNormal = Normal(incoming);
            var nextNormal = Normal(outgoing);
            var miter = Miter(previousNormal, nextNormal);
            result.Add(Make(points[i], miter, miter, radius, fringe));
        }

        if (!closed)
        {
            var direction =
                Vector2.Normalize(points[^1] - points[^2]);
            var normal = Normal(direction);
            result.Add(Make(points[^1], normal, normal, radius, fringe));
        }
        return result;
    }

    private static Section Make(
        Vector2 center, Vector2 leftNormal, Vector2 rightNormal,
        float radius, float fringe)
        => MakeAsymmetric(
            center, leftNormal, -rightNormal, radius, fringe);

    private static Section MakeAsymmetric(
        Vector2 center, Vector2 leftOut, Vector2 rightOut,
        float radius, float fringe)
    {
        var left = center + leftOut * radius;
        var right = center + rightOut * radius;
        var leftFringe = Vector2.Normalize(leftOut);
        var rightFringe = Vector2.Normalize(rightOut);
        return new(
            left,
            right,
            left + leftFringe * fringe,
            right + rightFringe * fringe);
    }

    private static Vector2 Miter(Vector2 first, Vector2 second)
    {
        var sum = first + second;
        if (sum.LengthSquared() < 0.0001f)
            return second;
        var miter = Vector2.Normalize(sum);
        float denominator = Vector2.Dot(miter, second);
        if (MathF.Abs(denominator) < 0.25f)
            denominator = MathF.CopySign(0.25f, denominator);
        return miter / denominator;
    }

    private static void Cap(
        ImDrawListPtr draw, Vector2 center, float radius,
        Vector2 direction, bool forward, uint solid, uint clear)
    {
        float baseAngle = MathF.Atan2(direction.Y, direction.X);
        float start = baseAngle + (forward ? -MathF.PI / 2f : MathF.PI / 2f);
        float sweep = forward ? MathF.PI : -MathF.PI;
        const int segments = 8;
        for (int i = 0; i < segments; i++)
        {
            float a0 = start + sweep * i / segments;
            float a1 = start + sweep * (i + 1) / segments;
            var n0 = new Vector2(MathF.Cos(a0), MathF.Sin(a0));
            var n1 = new Vector2(MathF.Cos(a1), MathF.Sin(a1));
            Triangle(
                draw, center, center + n0 * radius,
                center + n1 * radius, solid, solid, solid);
            Quad(
                draw,
                center + n0 * radius,
                center + n0 * (radius + 1f),
                center + n1 * (radius + 1f),
                center + n1 * radius,
                solid,
                clear);
        }
    }

    private static Vector2 Normal(Vector2 direction) =>
        new(-direction.Y, direction.X);

    private static void Quad(
        ImDrawListPtr draw, Vector2 a, Vector2 b, Vector2 c, Vector2 d,
        uint inner, uint outer)
    {
        Triangle(draw, a, b, c, inner, outer, outer);
        Triangle(draw, a, c, d, inner, outer, inner);
    }

    private static void Triangle(
        ImDrawListPtr draw, Vector2 a, Vector2 b, Vector2 c,
        uint ca, uint cb, uint cc)
    {
        var uv = ImGui.GetFontTexUvWhitePixel();
        uint baseIndex = (uint)draw.VtxBuffer.Size;
        draw.PrimReserve(3, 3);
        draw.PrimWriteVtx(a, uv, ca);
        draw.PrimWriteVtx(b, uv, cb);
        draw.PrimWriteVtx(c, uv, cc);
        draw.PrimWriteIdx((ushort)baseIndex);
        draw.PrimWriteIdx((ushort)(baseIndex + 1));
        draw.PrimWriteIdx((ushort)(baseIndex + 2));
    }
}
