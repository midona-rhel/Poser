using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

/// <summary>
/// Rasterizes all strokes in an icon into one cached coverage mask. A mask
/// pixel is emitted once, so group opacity cannot compound where SVG paths,
/// caps, or joins overlap.
/// </summary>
internal static class SvgStrokeMask
{
    private const int MaxCachedMasks = 512;

    private readonly record struct Stroke(
        List<Vector2> Points,
        float Radius,
        bool Closed,
        bool RoundCaps,
        bool RoundJoins);

    private readonly record struct Pixel(short X, short Y, byte Coverage);

    private sealed class Mask
    {
        public required Pixel[] Pixels { get; init; }

        public void Draw(
            ImDrawListPtr draw, Vector2 origin, Vector4 color,
            float groupOpacity, Vector4 background)
        {
            foreach (var pixel in Pixels)
            {
                uint packed = Packed(
                    color, pixel.Coverage, background, groupOpacity);
                var min = origin + new Vector2(pixel.X, pixel.Y);
                draw.AddRectFilled(min, min + Vector2.One, packed);
            }
        }
    }

    /// <summary>The colour one mask pixel is drawn in. The painter and the
    /// baked texture MUST agree byte for byte, so both come from here.
    /// </summary>
    private static uint Packed(
        Vector4 color, byte coverage, Vector4 background, float groupOpacity)
        => ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(
            GroupOverlay(
                color, coverage / 255f, background, groupOpacity)));

    private static readonly Dictionary<ulong, Mask> Cache = new();

    public static void Draw(
        ImDrawListPtr draw,
        IReadOnlyList<SvgPath> paths,
        Func<Vector2, Vector2> svgToScreen,
        float scale,
        Vector4? tint,
        float? strokeWidthOverride,
        float groupOpacity,
        Vector4 groupBackground)
    {
        if (!TryResolve(
                paths, svgToScreen, scale, strokeWidthOverride,
                out var mask, out var origin, out _, out _, out var stroke))
            return;
        var color = tint.HasValue
            ? Multiply(stroke, tint.Value)
            : stroke;
        mask.Draw(
            draw, origin, color,
            Math.Clamp(groupOpacity, 0f, 1f), groupBackground);
    }

    /// <summary>
    /// The same coverage the painter draws, as a straight-alpha RGBA8 bitmap
    /// the host can upload once and blit as a single quad. Every pixel goes
    /// through <see cref="Packed"/>, so the texture is the painter's own
    /// output — the quad is a transport change, not a rendering change.
    /// </summary>
    internal static void Bake(
        IReadOnlyList<SvgPath> paths,
        Func<Vector2, Vector2> svgToScreen,
        float scale,
        Vector4? tint,
        float? strokeWidthOverride,
        float groupOpacity,
        Vector4 groupBackground,
        out Vector2 origin,
        out int width,
        out int height,
        out byte[] rgba)
    {
        if (!TryResolve(
                paths, svgToScreen, scale, strokeWidthOverride,
                out var mask, out origin, out width, out height,
                out var stroke))
        {
            origin = default;
            width = 0;
            height = 0;
            rgba = [];
            return;
        }

        var color = tint.HasValue ? Multiply(stroke, tint.Value) : stroke;
        float opacity = Math.Clamp(groupOpacity, 0f, 1f);
        rgba = new byte[width * height * 4];
        foreach (var pixel in mask.Pixels)
        {
            uint packed = Packed(
                color, pixel.Coverage, groupBackground, opacity);
            int at = (pixel.Y * width + pixel.X) * 4;
            rgba[at] = (byte)packed;
            rgba[at + 1] = (byte)(packed >> 8);
            rgba[at + 2] = (byte)(packed >> 16);
            rgba[at + 3] = (byte)(packed >> 24);
        }
    }

    private static bool TryResolve(
        IReadOnlyList<SvgPath> paths,
        Func<Vector2, Vector2> svgToScreen,
        float scale,
        float? strokeWidthOverride,
        out Mask mask,
        out Vector2 origin,
        out int widthPixels,
        out int heightPixels,
        out Vector4 baseStroke)
    {
        mask = null!;
        origin = default;
        widthPixels = 0;
        heightPixels = 0;
        baseStroke = default;

        var strokes = new List<Stroke>();
        Vector4? baseColor = null;
        float minX = float.MaxValue;
        float minY = float.MaxValue;
        float maxX = float.MinValue;
        float maxY = float.MinValue;

        foreach (var path in paths)
        {
            float width = (strokeWidthOverride ?? path.StrokeWidth) * scale;
            if (path.Stroke is not { } strokeColor || width <= 0f)
                continue;
            baseColor ??= strokeColor;
            foreach (var subPath in path.SubPaths)
            {
                if (subPath.Points.Count < 2)
                    continue;
                var points = new List<Vector2>(subPath.Points.Count);
                foreach (var source in subPath.Points)
                {
                    var point = svgToScreen(source);
                    if (points.Count > 0
                        && Vector2.DistanceSquared(points[^1], point)
                            <= 0.0001f)
                        continue;
                    points.Add(point);
                    float extent = width * 0.5f + 0.5f;
                    minX = MathF.Min(minX, point.X - extent);
                    minY = MathF.Min(minY, point.Y - extent);
                    maxX = MathF.Max(maxX, point.X + extent);
                    maxY = MathF.Max(maxY, point.Y + extent);
                }
                // Tabler authors DOTS as near-zero segments ("h.01"): the
                // dedupe above collapses them to one point, and the caps
                // ARE the dot — a single-point round-capped subpath must
                // survive as a cap disc, or every i-dot and keyboard dot
                // in the set vanishes.
                if (points.Count >= 2
                    || (points.Count == 1 && path.RoundCaps))
                    strokes.Add(new(
                        points, width * 0.5f, subPath.Closed,
                        path.RoundCaps, path.RoundJoins));
            }
        }

        if (strokes.Count == 0 || baseColor is not { } stroke)
            return false;

        baseStroke = stroke;
        origin = new Vector2(MathF.Floor(minX), MathF.Floor(minY));
        widthPixels = Math.Max(1, (int)MathF.Ceiling(maxX) - (int)origin.X);
        heightPixels = Math.Max(1, (int)MathF.Ceiling(maxY) - (int)origin.Y);
        ulong key = Hash(strokes, origin, widthPixels, heightPixels);
        if (!Cache.TryGetValue(key, out mask!))
        {
            if (Cache.Count >= MaxCachedMasks)
                Cache.Clear();
            mask = Build(strokes, origin, widthPixels, heightPixels);
            Cache[key] = mask;
        }
        return true;
    }

    // The button background has already been drawn at background alpha ×
    // group opacity. Solve the one source-over draw needed at each icon
    // pixel so the final result equals: flatten background + icon first,
    // then apply CSS element opacity once to that complete group.
    private static Vector4 GroupOverlay(
        Vector4 foreground, float coverage,
        Vector4 background, float groupOpacity)
    {
        float foregroundAlpha = Math.Clamp(
            foreground.W * coverage, 0f, 1f);
        float backgroundAlpha = Math.Clamp(background.W, 0f, 1f);
        float layerAlpha = foregroundAlpha
            + backgroundAlpha * (1f - foregroundAlpha);
        float groupedAlpha = layerAlpha * groupOpacity;
        float drawnBackgroundAlpha = backgroundAlpha * groupOpacity;
        float remaining = 1f - drawnBackgroundAlpha;
        float overlayAlpha = remaining > 0.0001f
            ? 1f - (1f - groupedAlpha) / remaining
            : 1f;
        overlayAlpha = Math.Clamp(overlayAlpha, 0f, 1f);
        if (overlayAlpha <= 0.0001f)
            return Vector4.Zero;

        var foregroundRgb = new Vector3(
            foreground.X, foreground.Y, foreground.Z);
        var backgroundRgb = new Vector3(
            background.X, background.Y, background.Z);
        var groupedPremultiplied = groupOpacity * (
            foregroundRgb * foregroundAlpha
            + backgroundRgb * backgroundAlpha * (1f - foregroundAlpha));
        var drawnBackgroundPremultiplied =
            backgroundRgb * drawnBackgroundAlpha;
        var overlayRgb = (
            groupedPremultiplied
            - drawnBackgroundPremultiplied * (1f - overlayAlpha))
            / overlayAlpha;
        overlayRgb = Vector3.Clamp(overlayRgb, Vector3.Zero, Vector3.One);
        return new Vector4(overlayRgb, overlayAlpha);
    }

    // Coverage is AREA-estimated over a 4x4 subsample grid per pixel. A
    // single center sample makes brightness a function of distance LUCK:
    // a round cap or join disc landing near a pixel center produces a
    // full-brightness dot (the seam where Tabler's two circle arcs meet
    // read ~35% heavier than the ring), while mid-chord pixels ramp low.
    // Averaging subsamples approximates the box convolution the browser's
    // analytic rasterizer computes, so rings read evenly. The mask is
    // cached, so the 16x build cost is paid once per icon identity.
    private const int Subsamples = 4;

    private static Mask Build(
        List<Stroke> strokes,
        Vector2 origin,
        int width,
        int height)
    {
        var pixels = new List<Pixel>(width * height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float coverage = 0f;
                for (int sy = 0; sy < Subsamples; sy++)
                {
                    for (int sx = 0; sx < Subsamples; sx++)
                    {
                        var point = origin + new Vector2(
                            x + (sx + 0.5f) / Subsamples,
                            y + (sy + 0.5f) / Subsamples);
                        coverage += Coverage(strokes, point);
                    }
                }
                coverage /= Subsamples * Subsamples;
                if (coverage > 0f)
                    pixels.Add(new(
                        checked((short)x),
                        checked((short)y),
                        checked((byte)MathF.Round(coverage * 255f))));
            }
        }
        return new Mask { Pixels = pixels.ToArray() };
    }

    private static float Coverage(List<Stroke> strokes, Vector2 point)
    {
        float coverage = 0f;
        foreach (var stroke in strokes)
        {
            int segmentCount = stroke.Closed
                ? stroke.Points.Count
                : stroke.Points.Count - 1;
            for (int i = 0; i < segmentCount; i++)
            {
                var a = stroke.Points[i];
                var b = stroke.Points[(i + 1) % stroke.Points.Count];
                coverage = MathF.Max(
                    coverage,
                    EdgeCoverage(
                        stroke.Radius,
                        DistanceToStrip(point, a, b)));
            }

            if (stroke.RoundCaps && !stroke.Closed)
            {
                coverage = MathF.Max(
                    coverage,
                    EdgeCoverage(
                        stroke.Radius,
                        Vector2.Distance(point, stroke.Points[0])));
                coverage = MathF.Max(
                    coverage,
                    EdgeCoverage(
                        stroke.Radius,
                        Vector2.Distance(point, stroke.Points[^1])));
            }

            if (stroke.RoundJoins)
            {
                int first = stroke.Closed ? 0 : 1;
                int last = stroke.Closed
                    ? stroke.Points.Count
                    : stroke.Points.Count - 1;
                for (int i = first; i < last; i++)
                    coverage = MathF.Max(
                        coverage,
                        EdgeCoverage(
                            stroke.Radius,
                            Vector2.Distance(point, stroke.Points[i])));
            }
        }
        return coverage;
    }

    private static float DistanceToStrip(
        Vector2 point, Vector2 a, Vector2 b)
    {
        var direction = b - a;
        float lengthSquared = direction.LengthSquared();
        if (lengthSquared <= 0.0001f)
            return float.MaxValue;
        float t = Vector2.Dot(point - a, direction) / lengthSquared;
        if (t < 0f || t > 1f)
            return float.MaxValue;
        var nearest = a + direction * t;
        return Vector2.Distance(point, nearest);
    }

    private static float EdgeCoverage(float radius, float distance) =>
        Math.Clamp(radius + 0.5f - distance, 0f, 1f);

    private static ulong Hash(
        List<Stroke> strokes,
        Vector2 origin,
        int width,
        int height)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;

        void Add(uint value)
        {
            hash ^= value;
            hash *= prime;
        }

        Add((uint)width);
        Add((uint)height);
        foreach (var stroke in strokes)
        {
            Add(BitConverter.SingleToUInt32Bits(stroke.Radius));
            Add(stroke.Closed ? 1u : 0u);
            Add(stroke.RoundCaps ? 1u : 0u);
            Add(stroke.RoundJoins ? 1u : 0u);
            Add((uint)stroke.Points.Count);
            foreach (var point in stroke.Points)
            {
                Add(BitConverter.SingleToUInt32Bits(point.X - origin.X));
                Add(BitConverter.SingleToUInt32Bits(point.Y - origin.Y));
            }
        }
        return hash;
    }

    private static Vector4 Multiply(Vector4 left, Vector4 right) =>
        new(
            left.X * right.X,
            left.Y * right.Y,
            left.Z * right.Z,
            left.W * right.W);
}
