using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>Host hook for uploading a baked panel-shadow RGBA8 asset.</summary>
    public static Func<byte[], int, int, (nint Handle, IDisposable? Keepalive)>?
        PanelShadowTextureUploader
    {
        get => BoxShadowTextureCache.Uploader;
        set => BoxShadowTextureCache.Uploader = value;
    }
}

/// <summary>
/// Bounded cache for soft outset shadow paint. Each entry is a fixed-size
/// rounded shadow ring whose edge strips stretch with the panel; position and
/// panel dimensions are deliberately absent from <see cref="ShadowKey"/>.
/// </summary>
internal static class BoxShadowTextureCache
{
    private const int MaxEntries = 64;
    private const float MaxExtent = 64f;
    private const uint White = 0xFFFFFFFFu;

    private static readonly Dictionary<ShadowKey, Entry> Cache = new();
    private static Func<byte[], int, int, (nint, IDisposable?)>? _uploader;
    private static int _drawTick;

    internal static int EntryCount => Cache.Count;

    internal static Func<byte[], int, int, (nint, IDisposable?)>? Uploader
    {
        get => _uploader;
        set
        {
            // Texture handles belong to the device that made them.
            Clear();
            _uploader = value;
        }
    }

    internal interface IShadowDrawSink
    {
        void AddImage(
            nint handle,
            Vector2 min,
            Vector2 max,
            Vector2 uvMin,
            Vector2 uvMax);
    }

    internal readonly struct ImGuiShadowDrawSink(ImDrawListPtr drawList)
        : IShadowDrawSink
    {
        public void AddImage(
            nint handle,
            Vector2 min,
            Vector2 max,
            Vector2 uvMin,
            Vector2 uvMax)
        {
            drawList.AddImage(
                new ImTextureID(handle), min, max, uvMin, uvMax, White);
        }
    }

    private readonly struct ShadowKey : IEquatable<ShadowKey>
    {
        private readonly uint _offsetX;
        private readonly uint _offsetY;
        private readonly uint _blur;
        private readonly uint _spread;
        private readonly uint _radius;
        private readonly uint _scale;
        private readonly uint _styleAlpha;
        private readonly uint _red;
        private readonly uint _green;
        private readonly uint _blue;
        private readonly uint _alpha;

        public ShadowKey(
            in BoxShadow shadow,
            float baseRadius,
            float scale,
            float styleAlpha)
        {
            _offsetX = Bits(shadow.OffsetX);
            _offsetY = Bits(shadow.OffsetY);
            _blur = Bits(shadow.Blur);
            _spread = Bits(shadow.Spread);
            _radius = Bits(baseRadius);
            _scale = Bits(scale);
            _styleAlpha = Bits(styleAlpha);
            _red = Bits(shadow.Color.X);
            _green = Bits(shadow.Color.Y);
            _blue = Bits(shadow.Color.Z);
            _alpha = Bits(shadow.Color.W);
        }

        public bool Equals(ShadowKey other) =>
            _offsetX == other._offsetX
            && _offsetY == other._offsetY
            && _blur == other._blur
            && _spread == other._spread
            && _radius == other._radius
            && _scale == other._scale
            && _styleAlpha == other._styleAlpha
            && _red == other._red
            && _green == other._green
            && _blue == other._blue
            && _alpha == other._alpha;

        public override bool Equals(object? obj) =>
            obj is ShadowKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_offsetX);
            hash.Add(_offsetY);
            hash.Add(_blur);
            hash.Add(_spread);
            hash.Add(_radius);
            hash.Add(_scale);
            hash.Add(_styleAlpha);
            hash.Add(_red);
            hash.Add(_green);
            hash.Add(_blue);
            hash.Add(_alpha);
            return hash.ToHashCode();
        }
    }

    private struct Entry
    {
        public nint Handle;
        public IDisposable? Keepalive;
        public int Width;
        public int Height;
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int LastDraw;
    }

    internal static bool TryDraw<TSink>(
        TSink sink,
        Vector2 min,
        Vector2 max,
        in BoxShadow shadow,
        float baseRadius,
        float scale,
        float styleAlpha)
        where TSink : IShadowDrawSink
    {
        if (_uploader is null || shadow.Inset || shadow.Blur <= 0f)
            return false;

        var key = new ShadowKey(shadow, baseRadius, scale, styleAlpha);
        _drawTick++;
        if (Cache.TryGetValue(key, out var entry))
        {
            entry.LastDraw = _drawTick;
            Cache[key] = entry;
            DrawSlices(sink, entry, min, max);
            return true;
        }

        if (!TryDescribe(
                min,
                max,
                shadow,
                baseRadius,
                scale,
                out var description))
            return false;

        byte[] pixels;
        try
        {
            pixels = Bake(description, shadow.Color, styleAlpha);
        }
        catch (Exception)
        {
            // A malformed style must fall back to the old CPU painter.
            return false;
        }

        nint handle;
        IDisposable? keepalive;
        try
        {
            (handle, keepalive) = _uploader(pixels, description.Width, description.Height);
        }
        catch (Exception)
        {
            return false;
        }

        if (handle == 0)
        {
            keepalive?.Dispose();
            return false;
        }

        if (Cache.Count >= MaxEntries)
            EvictOldest();

        entry = new Entry
        {
            Handle = handle,
            Keepalive = keepalive,
            Width = description.Width,
            Height = description.Height,
            Left = description.Left,
            Top = description.Top,
            Right = description.Right,
            Bottom = description.Bottom,
            LastDraw = _drawTick,
        };
        Cache.Add(key, entry);
        DrawSlices(sink, entry, min, max);
        return true;
    }

    internal static void Clear()
    {
        foreach (var entry in Cache.Values)
            entry.Keepalive?.Dispose();
        Cache.Clear();
        _drawTick = 0;
    }

    private readonly struct Description
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
        public readonly float CoreMinX;
        public readonly float CoreMinY;
        public readonly float CoreMaxOffsetX;
        public readonly float CoreMaxOffsetY;
        public readonly float CoreRadius;
        public readonly float Blur;

        public Description(
            int left,
            int top,
            int right,
            int bottom,
            float boxWidth,
            float boxHeight,
            float coreMinX,
            float coreMinY,
            float coreMaxX,
            float coreMaxY,
            float coreRadius,
            float blur)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
            Width = left + 1 + right;
            Height = top + 1 + bottom;
            CoreMinX = coreMinX;
            CoreMinY = coreMinY;
            CoreMaxOffsetX = coreMaxX - boxWidth;
            CoreMaxOffsetY = coreMaxY - boxHeight;
            CoreRadius = coreRadius;
            Blur = blur;
        }
    }

    private static bool TryDescribe(
        Vector2 min,
        Vector2 max,
        in BoxShadow shadow,
        float baseRadius,
        float scale,
        out Description description)
    {
        description = default;
        if (!float.IsFinite(scale) || scale <= 0f)
            return false;

        float boxWidth = max.X - min.X;
        float boxHeight = max.Y - min.Y;
        float blur = shadow.Blur * scale;
        float spread = shadow.Spread * scale;
        float offsetX = shadow.OffsetX * scale;
        float offsetY = shadow.OffsetY * scale;
        float radius = (baseRadius + shadow.Spread) * scale;
        float coreRadius = MathF.Max(0f, radius - blur);
        if (!float.IsFinite(boxWidth)
            || !float.IsFinite(boxHeight)
            || !float.IsFinite(blur)
            || !float.IsFinite(spread)
            || !float.IsFinite(offsetX)
            || !float.IsFinite(offsetY)
            || !float.IsFinite(radius)
            || blur <= 0f
            || blur > MaxExtent
            || boxWidth <= 2f * MathF.Max(0f, radius) + 1f
            || boxHeight <= 2f * MathF.Max(0f, radius) + 1f)
            return false;

        float leftExtent = MathF.Max(0f, spread + blur - offsetX);
        float topExtent = MathF.Max(0f, spread + blur - offsetY);
        float rightExtent = MathF.Max(0f, spread + blur + offsetX);
        float bottomExtent = MathF.Max(0f, spread + blur + offsetY);
        if (leftExtent > MaxExtent
            || topExtent > MaxExtent
            || rightExtent > MaxExtent
            || bottomExtent > MaxExtent)
            return false;

        int left = Math.Max(1, (int)MathF.Ceiling(leftExtent));
        int top = Math.Max(1, (int)MathF.Ceiling(topExtent));
        int right = Math.Max(1, (int)MathF.Ceiling(rightExtent));
        int bottom = Math.Max(1, (int)MathF.Ceiling(bottomExtent));

        float coreMinX = offsetX - spread + blur;
        float coreMinY = offsetY - spread + blur;
        float coreMaxX = boxWidth + offsetX + spread - blur;
        float coreMaxY = boxHeight + offsetY + spread - blur;
        if (coreMaxX <= coreMinX || coreMaxY <= coreMinY)
            return false;

        float maxCoreRadius = MathF.Min(
            (coreMaxX - coreMinX) * 0.5f,
            (coreMaxY - coreMinY) * 0.5f);
        coreRadius = MathF.Min(coreRadius, MathF.Max(0f, maxCoreRadius));
        description = new Description(
            left,
            top,
            right,
            bottom,
            boxWidth,
            boxHeight,
            coreMinX,
            coreMinY,
            coreMaxX,
            coreMaxY,
            coreRadius,
            blur);
        return true;
    }

    private static byte[] Bake(
        in Description description,
        Vector4 color,
        float styleAlpha)
    {
        var pixels = new byte[description.Width * description.Height * 4];
        // The representative box is deliberately larger than the one-pixel
        // stretch strips, so each corner is baked away from the opposite
        // corner. The source asset never contains panel width or height.
        float representativeWidth = MathF.Max(
            description.Left + description.Right + 2f,
            2f * (description.CoreRadius + description.Blur) + 2f);
        float representativeHeight = MathF.Max(
            description.Top + description.Bottom + 2f,
            2f * (description.CoreRadius + description.Blur) + 2f);
        float coreMaxX = representativeWidth + description.CoreMaxOffsetX;
        float coreMaxY = representativeHeight + description.CoreMaxOffsetY;
        float appliedStyleAlpha = styleAlpha >= 1f ? 1f : styleAlpha;
        int index = 0;
        for (int y = 0; y < description.Height; y++)
        {
            for (int x = 0; x < description.Width; x++)
            {
                float alpha;
                if (x == description.Left && y == description.Top)
                {
                    // The box background is painted after the shadow. Keep
                    // the center transparent so the asset is safe to reuse.
                    alpha = 0f;
                }
                else if (y < description.Top && x == description.Left)
                {
                    alpha = AlphaAt(
                        representativeWidth * 0.5f,
                        y + 0.5f - description.Top,
                        description.CoreMinX,
                        description.CoreMinY,
                        coreMaxX,
                        coreMaxY,
                        description.CoreRadius,
                        description.Blur);
                }
                else if (y > description.Top && x == description.Left)
                {
                    alpha = AlphaAt(
                        representativeWidth * 0.5f,
                        representativeHeight + y + 0.5f - description.Top - 1f,
                        description.CoreMinX,
                        description.CoreMinY,
                        coreMaxX,
                        coreMaxY,
                        description.CoreRadius,
                        description.Blur);
                }
                else if (x < description.Left && y == description.Top)
                {
                    alpha = AlphaAt(
                        x + 0.5f - description.Left,
                        representativeHeight * 0.5f,
                        description.CoreMinX,
                        description.CoreMinY,
                        coreMaxX,
                        coreMaxY,
                        description.CoreRadius,
                        description.Blur);
                }
                else if (x > description.Left && y == description.Top)
                {
                    alpha = AlphaAt(
                        representativeWidth + x + 0.5f - description.Left - 1f,
                        representativeHeight * 0.5f,
                        description.CoreMinX,
                        description.CoreMinY,
                        coreMaxX,
                        coreMaxY,
                        description.CoreRadius,
                        description.Blur);
                }
                else
                {
                    float localX = x + 0.5f - description.Left;
                    float localY = y + 0.5f - description.Top;
                    if (x > description.Left)
                        localX += representativeWidth - 1f;
                    if (y > description.Top)
                        localY += representativeHeight - 1f;
                    alpha = AlphaAt(
                        localX,
                        localY,
                        description.CoreMinX,
                        description.CoreMinY,
                        coreMaxX,
                        coreMaxY,
                        description.CoreRadius,
                        description.Blur);
                }

                pixels[index++] = ToByte(color.X);
                pixels[index++] = ToByte(color.Y);
                pixels[index++] = ToByte(color.Z);
                pixels[index++] = ToByte(color.W * appliedStyleAlpha * alpha);
            }
        }
        return pixels;
    }

    private static float AlphaAt(
        float x,
        float y,
        float coreMinX,
        float coreMinY,
        float coreMaxX,
        float coreMaxY,
        float radius,
        float blur)
    {
        float halfX = (coreMaxX - coreMinX) * 0.5f;
        float halfY = (coreMaxY - coreMinY) * 0.5f;
        float centerX = (coreMinX + coreMaxX) * 0.5f;
        float centerY = (coreMinY + coreMaxY) * 0.5f;
        float qx = MathF.Abs(x - centerX) - halfX + radius;
        float qy = MathF.Abs(y - centerY) - halfY + radius;
        float outsideX = MathF.Max(qx, 0f);
        float outsideY = MathF.Max(qy, 0f);
        float signedDistance =
            MathF.Sqrt(outsideX * outsideX + outsideY * outsideY)
            + MathF.Min(MathF.Max(qx, qy), 0f)
            - radius;
        float distance = MathF.Max(0f, signedDistance);
        float t = distance / (2f * blur);
        if (t >= 1f)
            return 0f;
        if (t <= 0f)
            return 1f;
        return 1f - t * t * (3f - 2f * t);
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)(MathF.Max(0f, MathF.Min(1f, value)) * 255f + 0.5f), 0, 255);

    private static void DrawSlices<TSink>(
        TSink sink,
        in Entry entry,
        Vector2 min,
        Vector2 max)
        where TSink : IShadowDrawSink
    {
        float width = entry.Width;
        float height = entry.Height;
        var uvLeft = entry.Left / width;
        var uvTop = entry.Top / height;
        var uvRight = (entry.Left + 1) / width;
        var uvBottom = (entry.Top + 1) / height;
        var uvEnd = new Vector2(1f, 1f);

        sink.AddImage(entry.Handle, min - new Vector2(entry.Left, entry.Top), min,
            Vector2.Zero, new Vector2(uvLeft, uvTop));
        sink.AddImage(entry.Handle, new Vector2(min.X, min.Y - entry.Top),
            new Vector2(max.X, min.Y), new Vector2(uvLeft, 0f),
            new Vector2(uvRight, uvTop));
        sink.AddImage(entry.Handle, new Vector2(max.X, min.Y - entry.Top),
            new Vector2(max.X + entry.Right, min.Y), new Vector2(uvRight, 0f),
            new Vector2(1f, uvTop));
        sink.AddImage(entry.Handle, new Vector2(min.X - entry.Left, min.Y),
            new Vector2(min.X, max.Y), new Vector2(0f, uvTop),
            new Vector2(uvLeft, uvBottom));
        sink.AddImage(entry.Handle, new Vector2(max.X, min.Y),
            new Vector2(max.X + entry.Right, max.Y), new Vector2(uvRight, uvTop),
            new Vector2(1f, uvBottom));
        sink.AddImage(entry.Handle, new Vector2(min.X - entry.Left, max.Y),
            new Vector2(min.X, max.Y + entry.Bottom), new Vector2(0f, uvBottom),
            new Vector2(uvLeft, 1f));
        sink.AddImage(entry.Handle, new Vector2(min.X, max.Y),
            new Vector2(max.X, max.Y + entry.Bottom), new Vector2(uvLeft, uvBottom),
            new Vector2(uvRight, 1f));
        sink.AddImage(entry.Handle, max,
            max + new Vector2(entry.Right, entry.Bottom), new Vector2(uvRight, uvBottom),
            uvEnd);
    }

    private static void EvictOldest()
    {
        ShadowKey oldestKey = default;
        int oldestTick = int.MaxValue;
        foreach (var pair in Cache)
        {
            if (pair.Value.LastDraw < oldestTick)
            {
                oldestTick = pair.Value.LastDraw;
                oldestKey = pair.Key;
            }
        }

        if (Cache.Remove(oldestKey, out var entry))
            entry.Keepalive?.Dispose();
    }

    private static uint Bits(float value) =>
        BitConverter.SingleToUInt32Bits(value);
}
