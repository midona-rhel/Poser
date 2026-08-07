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
    private struct Entry(
        nint Handle,
        Vector2 Offset,
        Vector2 Size,
        IDisposable? Keepalive,
        bool Painter)
    {
        public readonly nint Handle = Handle;
        public readonly Vector2 Offset = Offset;
        public readonly Vector2 Size = Size;
        public readonly IDisposable? Keepalive = Keepalive;
        public readonly bool Painter = Painter;

        /// <summary>Recency ordinal for the overflow sweep, stamped per hit
        /// through the dictionary ref so a hit stays one lookup.</summary>
        public int LastDraw;
    }

    private static readonly Dictionary<ulong, Entry> Cache = new();

    /// <summary>Monotonic draw ordinal; recency, not time.</summary>
    private static int _drawTick;

    // Overflow sweep scratch (cold path, static so it never allocates).
    private static readonly int[] SweepTicks = new int[MaxEntries];
    private static readonly ulong[] SweepKeys = new ulong[MaxEntries];

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

    internal static void Clear()
    {
        foreach (var entry in Cache.Values)
            entry.Keepalive?.Dispose();
        Cache.Clear();
        Array.Clear(Seen);
        _seenAt = 0;
    }

    /// <summary>
    /// Overflow eviction: drop the least-recently-drawn HALF. The old policy
    /// cleared the whole cache, which re-baked every visible icon on the next
    /// frame — a one-off 100ms-class hitch whenever a long session finally
    /// crossed <see cref="MaxEntries"/>. Cold path by construction; the
    /// scratch arrays are static so it allocates nothing.
    /// </summary>
    private static void EvictStale()
    {
        int count = 0;
        foreach (var pair in Cache)
        {
            SweepTicks[count] = pair.Value.LastDraw;
            SweepKeys[count] = pair.Key;
            count++;
        }
        // Median by sorting a COPY of the ticks; the keys keep dictionary
        // order and are re-tested against the threshold instead.
        Array.Sort(SweepTicks, 0, count);
        int threshold = SweepTicks[count / 2];
        for (int i = 0; i < count; i++)
        {
            ulong key = SweepKeys[i];
            if (Cache.TryGetValue(key, out var entry)
                && entry.LastDraw <= threshold)
            {
                entry.Keepalive?.Dispose();
                Cache.Remove(key);
            }
        }
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
        _drawTick++;
        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrNullRef(Cache, key);
        Entry entry;
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref slot))
        {
            slot.LastDraw = _drawTick;
            entry = slot;
        }
        else
        {
            if (!Repeated(key))
                return false;
            // The bake is rasterized in LOCAL space: RenderCore floors the
            // box to whole pixels, and an integer translation of stroke
            // geometry has identical per-pixel coverage, so where the icon
            // SITS can never matter to the bitmap. Screen coordinates used
            // to leak in through their float exponent — dragging a window
            // across a power-of-two x or y re-baked every visible icon in
            // one frame (~320ms).
            entry = Bake(
                doc, Vector2.Zero, max - min, Vector2.Zero, tint, flipX,
                strokeWidth, groupOpacity, groupBackground);
            entry.LastDraw = _drawTick;
            if (Cache.Count >= MaxEntries)
                EvictStale();
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
    /// Everything the baked pixels depend on — and POSITION IS NOT IN IT.
    /// The bake is rasterized in local space off a whole-pixel box, so the
    /// bitmap depends only on the size and the paint inputs. Keying position
    /// (its float exponent and sub-pixel phase, as this originally did) made
    /// window drags re-bake every visible icon at each power-of-two crossing.
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
