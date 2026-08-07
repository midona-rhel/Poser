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

    // Internal rather than private only so <see cref="Baked"/> can surface an
    // array of them across the worker/main-thread boundary.
    internal readonly record struct Pixel(short X, short Y, byte Coverage);

    private sealed class Mask
    {
        public required Pixel[] Pixels { get; init; }

        public void Draw(
            ImDrawListPtr draw, Vector2 origin, Vector4 color,
            float groupOpacity, Vector4 background, float styleAlpha)
        {
            foreach (var pixel in Pixels)
            {
                uint packed = Packed(
                    color, pixel.Coverage, background, groupOpacity,
                    styleAlpha);
                var min = origin + new Vector2(pixel.X, pixel.Y);
                draw.AddRectFilled(min, min + Vector2.One, packed);
            }
        }
    }

    /// <summary>The colour one mask pixel is drawn in. The painter and the
    /// baked texture MUST agree byte for byte, so both come from here.
    /// </summary>
    private static uint Packed(
        Vector4 color, byte coverage, Vector4 background, float groupOpacity,
        float styleAlpha)
        => ImGui.ColorConvertFloat4ToU32(
            ApplyStyleAlpha(
                GroupOverlay(color, coverage / 255f, background, groupOpacity),
                styleAlpha));

    /// <summary>
    /// <c>ColorEx.ApplyAlpha</c> with the style alpha PASSED IN rather than
    /// read live. A backgrounded bake packs its pixels a frame or more after
    /// the draw that asked for it, and the icon cache keys on the style alpha
    /// at request time — reading the live value at pack time would file a
    /// bitmap under an alpha it was not painted with.
    /// </summary>
    private static Vector4 ApplyStyleAlpha(Vector4 color, float alpha) =>
        alpha >= 1f ? color : color with { W = color.W * alpha };

    private static readonly Dictionary<ulong, Mask> Cache = new();

    /// <summary>Guards <see cref="Cache"/>: <see cref="Resolve"/> runs on the
    /// icon cache's background rasterizer while the painter may be resolving
    /// the same table on the main thread.</summary>
    private static readonly object CacheGate = new();

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
        // Read once here instead of once per pixel inside ColorEx.ApplyAlpha:
        // same value, same result, and it keeps the pixel loop free of any
        // ImGui call so the packer is shared with the backgrounded bake.
        mask.Draw(
            draw, origin, color,
            Math.Clamp(groupOpacity, 0f, 1f), groupBackground,
            ImGui.GetStyle().Alpha);
    }

    /// <summary>
    /// A rasterized icon that has not been coloured yet: the coverage mask
    /// plus the resolved paint inputs <see cref="Pack"/> needs. This is the
    /// split point between what may run on a worker thread and what may not.
    /// </summary>
    internal sealed class Baked
    {
        public required Pixel[] Pixels { get; init; }
        public required Vector2 Origin { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required Vector4 Color { get; init; }
        public required float Opacity { get; init; }
        public required Vector4 Background { get; init; }

        /// <summary>The style alpha at REQUEST time, not at pack time.</summary>
        public required float StyleAlpha { get; init; }
    }

    /// <summary>
    /// PURE CPU: rasterizes the coverage mask and resolves the paint inputs
    /// with no ImGui, draw-list, or otherwise thread-affine call anywhere in
    /// its reach. Safe to run off the main thread; <see cref="Cache"/> is the
    /// only shared state and it is gated.
    ///
    /// <para>Null means the document contributes no strokes — the draw paints
    /// nothing, which is a legitimate bake and not a painter fallback.</para>
    /// </summary>
    internal static Baked? Resolve(
        IReadOnlyList<SvgPath> paths,
        Func<Vector2, Vector2> svgToScreen,
        float scale,
        Vector4? tint,
        float? strokeWidthOverride,
        float groupOpacity,
        Vector4 groupBackground,
        float styleAlpha)
    {
        if (!TryResolve(
                paths, svgToScreen, scale, strokeWidthOverride,
                out var mask, out var origin, out int width, out int height,
                out var stroke))
            return null;
        return new Baked
        {
            Pixels = mask.Pixels,
            Origin = origin,
            Width = width,
            Height = height,
            Color = tint.HasValue ? Multiply(stroke, tint.Value) : stroke,
            Opacity = Math.Clamp(groupOpacity, 0f, 1f),
            Background = groupBackground,
            StyleAlpha = styleAlpha,
        };
    }

    /// <summary>
    /// MAIN THREAD ONLY. Turns a resolved mask into the straight-alpha RGBA8
    /// bitmap the host uploads. Every pixel goes through <see cref="Packed"/>,
    /// which calls <c>ImGui.ColorConvertFloat4ToU32</c> — a native cimgui
    /// entry point whose thread affinity is not something this code can
    /// verify, so it stays on the main thread. Routing the colour step
    /// through the painter's own function is also what keeps the texture
    /// byte-for-byte identical to the painter's output.
    ///
    /// <para>The cost is trivial next to the rasterization it follows: a few
    /// hundred covered pixels here, against width x height x 16 coverage
    /// samples in <see cref="Build"/> — which is the part that moved off the
    /// main thread.</para>
    /// </summary>
    internal static byte[] Pack(Baked baked)
    {
        var rgba = new byte[baked.Width * baked.Height * 4];
        foreach (var pixel in baked.Pixels)
        {
            uint packed = Packed(
                baked.Color, pixel.Coverage, baked.Background, baked.Opacity,
                baked.StyleAlpha);
            int at = (pixel.Y * baked.Width + pixel.X) * 4;
            rgba[at] = (byte)packed;
            rgba[at + 1] = (byte)(packed >> 8);
            rgba[at + 2] = (byte)(packed >> 16);
            rgba[at + 3] = (byte)(packed >> 24);
        }
        return rgba;
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
        // The BUILD deliberately stays OUTSIDE the gate: it is the expensive
        // part (width x height x 16 coverage samples), and holding the lock
        // across it would let a background resolve stall the frame — the very
        // hitch this machinery exists to remove. Two threads racing the same
        // key just build the same deterministic mask twice and the last
        // writer wins; a Mask is immutable once built, so sharing is safe.
        lock (CacheGate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                mask = cached;
                return true;
            }
        }
        mask = Build(strokes, origin, widthPixels, heightPixels);
        lock (CacheGate)
        {
            if (Cache.Count >= MaxCachedMasks)
                Cache.Clear();
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
