using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Host hook for turning a straight-alpha RGBA8 bitmap into an ImGui
    /// texture. Crystarium cannot reference a graphics device, so the host
    /// (Dalamud's texture provider in game, the D3D11 device in the capture
    /// harness) supplies the upload and, if its handle needs an owner alive,
    /// a keepalive the cache disposes with the texture.
    ///
    /// <para>Without it every icon falls back to the per-pixel painter —
    /// correct, but hundreds of 1px rects per glyph per frame.</para>
    /// </summary>
    public static Func<byte[], int, int, (nint Handle, IDisposable? Keepalive)>?
        IconTextureUploader
    {
        get => SvgIconTextureCache.Uploader;
        set => SvgIconTextureCache.Uploader = value;
    }

    /// <summary>Drops every baked icon texture. Setting a new uploader does
    /// this already; call it directly only to reclaim the memory.</summary>
    public static void ReleaseIconTextures() => SvgIconTextureCache.Clear();

    /// <summary>Baked icon textures currently held (diagnostics).</summary>
    public static int IconTextureCount => SvgIconTextureCache.Count;
}

/// <summary>
/// Bake-once cache for icon draws: one uploaded bitmap per distinct
/// (document, box, colour) draw, blitted as a single quad afterwards. The
/// bitmap is the painter's own per-pixel output, so the warm path is a
/// transport change and not a rendering change.
/// </summary>
internal static class SvgIconTextureCache
{
    private const int MaxEntries = 1024;
    private const uint White = 0xFFFFFFFFu;

    /// <summary><see cref="Handle"/> 0 with <see cref="Painter"/> false is a
    /// bakeable draw whose mask is empty — it draws nothing, correctly.
    /// </summary>
    private readonly record struct Entry(
        nint Handle,
        Vector2 Offset,
        Vector2 Size,
        IDisposable? Keepalive,
        bool Painter);

    private static readonly Dictionary<ulong, Entry> Cache = new();

    // A key is drawn by the painter the first time it is seen and only earns
    // a texture once it comes back. Hover/press transitions retint an icon
    // every frame, and baking those would upload a texture per frame for a
    // state that never repeats; a resting icon repeats immediately.
    private static readonly ulong[] Seen = new ulong[64];
    private static int _seenAt;

    private static Func<byte[], int, int, (nint, IDisposable?)>? _uploader;

    internal static Func<byte[], int, int, (nint, IDisposable?)>? Uploader
    {
        get => _uploader;
        set
        {
            // Handles belong to the device that made them.
            Clear();
            _uploader = value;
        }
    }

    internal static int Count => Cache.Count;

    internal static void Clear()
    {
        foreach (var entry in Cache.Values)
            entry.Keepalive?.Dispose();
        Cache.Clear();
        Array.Clear(Seen);
        _seenAt = 0;
    }

    /// <summary>Draws the icon from a baked texture, or reports that this
    /// draw belongs to the painter. Allocation-free on a cache hit.</summary>
    internal static bool TryDraw(
        ImDrawListPtr draw,
        SvgDocument doc,
        Vector2 min,
        Vector2 max,
        Vector4? tint,
        bool flipX,
        float? strokeWidth,
        float groupOpacity,
        Vector4 groupBackground)
    {
        if (_uploader is null)
        {
            WarnMissingUploader();
            return false;
        }

        ulong key = Key(
            doc, min, max, tint, flipX, strokeWidth,
            groupOpacity, groupBackground);
        var floor = new Vector2(MathF.Floor(min.X), MathF.Floor(min.Y));
        if (!Cache.TryGetValue(key, out var entry))
        {
            if (!Repeated(key))
                return false;
            entry = Bake(
                doc, min, max, floor, tint, flipX, strokeWidth,
                groupOpacity, groupBackground);
            if (Cache.Count >= MaxEntries)
                Clear();
            Cache[key] = entry;
        }

        if (entry.Painter)
            return false;
        if (entry.Handle != 0)
        {
            var at = floor + entry.Offset;
            draw.AddImage(
                new ImTextureID(entry.Handle),
                at,
                at + entry.Size,
                Vector2.Zero,
                Vector2.One,
                White);
        }
        return true;
    }

    private static Entry Bake(
        SvgDocument doc,
        Vector2 min,
        Vector2 max,
        Vector2 floor,
        Vector4? tint,
        bool flipX,
        float? strokeWidth,
        float groupOpacity,
        Vector4 groupBackground)
    {
        // Fills and multicolour documents keep the painter's ordering.
        if (!doc.TryBakeMask(
                min, max, tint, flipX, strokeWidth,
                groupOpacity, groupBackground,
                out var origin, out int width, out int height, out var rgba))
            return new Entry(0, default, default, null, true);
        // Bakeable, but the mask came out empty: this draw paints nothing.
        if (width <= 0 || height <= 0)
            return new Entry(0, default, default, null, false);

        var (handle, keepalive) = _uploader!(rgba, width, height);
        if (handle == 0)
        {
            keepalive?.Dispose();
            return new Entry(0, default, default, null, true);
        }
        return new Entry(
            handle,
            origin - floor,
            new Vector2(width, height),
            keepalive,
            false);
    }

    /// <summary>Records the key and reports whether it had already been
    /// seen. A fixed ring, so the guard itself never allocates.</summary>
    private static bool Repeated(ulong key)
    {
        foreach (ulong seen in Seen)
            if (seen == key)
                return true;
        Seen[_seenAt] = key;
        _seenAt = (_seenAt + 1) % Seen.Length;
        return false;
    }

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// Everything the baked pixels depend on. Position enters as (binade,
    /// fraction) rather than as an absolute coordinate: the transform's
    /// rounding is decided by the exponent of the coordinates it works in and
    /// by the sub-pixel phase, so two boxes sharing both produce bit-identical
    /// geometry — which is also what lets a list scrolling by whole pixels
    /// keep hitting the same bake.
    /// </summary>
    private static ulong Key(
        SvgDocument doc,
        Vector2 min,
        Vector2 max,
        Vector4? tint,
        bool flipX,
        float? strokeWidth,
        float groupOpacity,
        Vector4 background)
    {
        ulong hash = Mix(FnvOffset, (uint)doc.CacheId);
        hash = Mix(hash, Exponent(min.X));
        hash = Mix(hash, Exponent(min.Y));
        hash = Mix(hash, Exponent(max.X));
        hash = Mix(hash, Exponent(max.Y));
        hash = Mix(hash, Bits(min.X - MathF.Floor(min.X)));
        hash = Mix(hash, Bits(min.Y - MathF.Floor(min.Y)));
        hash = Mix(hash, Bits(max.X - min.X));
        hash = Mix(hash, Bits(max.Y - min.Y));
        hash = Mix(hash, flipX ? 1u : 0u);
        hash = Mix(
            hash,
            strokeWidth.HasValue ? Bits(strokeWidth.Value) : 0xFFFFFFFFu);
        hash = Mix(hash, tint.HasValue ? 1u : 0u);
        if (tint is { } color)
            hash = Mix(hash, color);
        hash = Mix(hash, Bits(groupOpacity));
        hash = Mix(hash, background);
        // The style alpha the painter would fold in at draw time.
        return Mix(hash, Bits(ImGui.GetStyle().Alpha));
    }

    private static ulong Mix(ulong hash, uint value) =>
        (hash ^ value) * FnvPrime;

    private static ulong Mix(ulong hash, Vector4 value)
    {
        hash = Mix(hash, Bits(value.X));
        hash = Mix(hash, Bits(value.Y));
        hash = Mix(hash, Bits(value.Z));
        return Mix(hash, Bits(value.W));
    }

    private static uint Bits(float value) =>
        BitConverter.SingleToUInt32Bits(value);

    private static uint Exponent(float value) =>
        BitConverter.SingleToUInt32Bits(value) >> 23;

#if DEBUG
    private static bool _warned;
#endif

    private static void WarnMissingUploader()
    {
#if DEBUG
        if (_warned)
            return;
        _warned = true;
        System.Diagnostics.Debug.WriteLine(
            "Crystarium: no IconTextureUploader is registered — every icon "
            + "falls back to the per-pixel painter. Register one at host "
            + "startup (Crystarium.IconTextureUploader).");
#endif
    }
}
