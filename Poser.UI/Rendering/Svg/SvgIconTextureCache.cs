using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace Poser.UI;

public static partial class Crystarium
{
    /// <summary>
    /// Host hook for turning a straight-alpha RGBA8 bitmap into an ImGui
    /// texture. Crystarium cannot reference a graphics device, so the host
    /// supplies the upload and, if its handle needs an owner alive, a
    /// keepalive the cache disposes with the texture.
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

    /// <summary>
    /// Rasterize icon bakes ON the calling thread, the frame they are asked
    /// for, with no per-frame painter or upload budget. Off by default: in
    /// game the budgets and the background rasterizer are what keep a reload,
    /// a first shell draw, or a theme retint — all of which make every
    /// visible icon a cache miss in ONE frame — from costing a ~300ms hitch.
    ///
    /// <para>The capture harness sets this so a golden is a pure function of
    /// its frame: with budgets on, whether an icon has its texture yet
    /// depends on how many frames the harness happened to pump.</para>
    /// </summary>
    public static bool IconBakesSynchronous
    {
        get => SvgIconTextureCache.SynchronousBakes;
        set => SvgIconTextureCache.SynchronousBakes = value;
    }
}

/// <summary>
/// Bake-once cache for icon draws: one uploaded bitmap per distinct
/// (document, size, colour) draw, blitted as a single quad afterwards. The
/// bitmap is the painter's own per-pixel output, so the warm path is a
/// transport change and not a rendering change.
///
/// <para>Cold FRAMES are the hard part. A plugin reload, the first shell
/// draw, and every theme or accent retint turn ~40 visible icons into
/// first-seen keys simultaneously; done inline that is one frame of
/// software painting plus one frame of rasterize-and-upload, a ~300ms wall
/// either way. So the rasterization runs on a background worker and the
/// main thread spends a fixed, small budget per frame draining it.</para>
/// </summary>
internal static class SvgIconTextureCache
{
    private const int MaxEntries = 1024;
    private const uint White = 0xFFFFFFFFu;

    /// <summary>Painter fallbacks allowed per ImGui frame. Steady frames have
    /// no first-seen keys at all, so this is never reached outside a storm;
    /// during one it caps the per-pixel painter at a handful of icons and the
    /// rest are simply absent for a frame or two while their bakes land.
    /// </summary>
    private const int PaintBudget = 6;

    /// <summary>Completed rasterizations packed and uploaded per ImGui frame.
    /// </summary>
    private const int UploadBudget = 8;

    /// <summary>See <see cref="Crystarium.IconBakesSynchronous"/>.</summary>
    internal static bool SynchronousBakes;

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

    // ---------- background rasterization ----------

    /// <summary>
    /// One icon waiting to be rasterized, then the result on the way back.
    /// Allocated per job, which only happens on a miss — storms allocate, warm
    /// frames do not touch any of this.
    /// </summary>
    private sealed class RasterJob
    {
        // Request. Note the box is a SIZE, not a screen rect: the bake is
        // rasterized in local space, so position cannot reach the worker.
        public int Generation;
        public ulong Key;
        public SvgDocument Doc = null!;
        public Vector2 Size;
        public Vector4? Tint;
        public bool FlipX;
        public float? StrokeWidth;
        public float GroupOpacity;
        public Vector4 GroupBackground;

        /// <summary>Captured at ENQUEUE. The pack runs frames later and the
        /// key was taken under this alpha, so the live value is wrong.
        /// </summary>
        public float StyleAlpha;

        // Result.
        public bool Bakeable;
        public SvgStrokeMask.Baked? Baked;
    }

    private static readonly ConcurrentQueue<RasterJob> Inbox = new();
    private static readonly ConcurrentQueue<RasterJob> Completed = new();

    /// <summary>Keys with a job in flight, so a storm enqueues each icon once
    /// however many frames it takes to come back. Main thread only.</summary>
    private static readonly HashSet<ulong> Pending = new();

    /// <summary>Bumped by <see cref="Clear"/>; results stamped with an older
    /// value are dropped on drain. This is how pending work is discarded
    /// safely without reaching into a thread that may be mid-rasterize.
    /// </summary>
    private static int _generation;

    /// <summary>0 or 1: whether a drain task is live. A bounded
    /// <see cref="Task.Run"/> pump rather than a dedicated thread, because a
    /// long-lived thread rooted in the plugin's assembly load context would
    /// block Dalamud from unloading it — and reload is one of the exact cases
    /// this code exists to make smooth.</summary>
    private static int _draining;

    private static void Pump()
    {
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
            return;
        Task.Run(Drain);
    }

    private static void Drain()
    {
        do
        {
            while (Inbox.TryDequeue(out var job))
                Rasterize(job);
            Volatile.Write(ref _draining, 0);
            // A job queued between the failed dequeue and the release above
            // found _draining still set and scheduled nothing, so re-check
            // before really giving up.
        }
        while (!Inbox.IsEmpty
            && Interlocked.CompareExchange(ref _draining, 1, 0) == 0);
    }

    /// <summary>
    /// The background half. <c>SvgDocument.TryResolveMask</c> is pure CPU —
    /// geometry projection, stroke extraction and the 4x4-supersampled
    /// coverage build — with no ImGui, draw-list or otherwise thread-affine
    /// call in its reach; the style alpha that used to leak in through
    /// <c>ColorEx.ApplyAlpha</c> is now captured at enqueue and carried on the
    /// job. The colour pack and the upload stay on the main thread.
    /// </summary>
    private static void Rasterize(RasterJob job)
    {
        if (job.Generation != Volatile.Read(ref _generation))
            return;
        try
        {
            job.Bakeable = job.Doc.TryResolveMask(
                Vector2.Zero, job.Size, job.Tint, job.FlipX, job.StrokeWidth,
                job.GroupOpacity, job.GroupBackground, job.StyleAlpha,
                out var baked);
            job.Baked = baked;
        }
        catch (Exception)
        {
            // An escaped rasterizer fault must not take the process down from
            // a pool thread. Hand the draw back to the painter instead.
            job.Bakeable = false;
            job.Baked = null;
        }
        Completed.Enqueue(job);
    }

    /// <summary>
    /// Main-thread half: pack and upload at most <see cref="UploadBudget"/>
    /// finished rasterizations. Uploads stay here because the
    /// <see cref="Uploader"/> delegate's thread-safety is unknown — it is a
    /// host callback into a graphics device (Dalamud's texture provider, the
    /// harness's D3D11 device), and neither documents being callable off the
    /// render thread.
    /// </summary>
    private static void Integrate(ref int uploads)
    {
        while (uploads < UploadBudget && Completed.TryDequeue(out var job))
        {
            if (job.Generation != _generation)
                continue;
            Pending.Remove(job.Key);
            uploads++;
            Entry entry;
            if (!job.Bakeable)
                entry = new Entry(0, default, default, null, true);
            else if (job.Baked is not { } baked)
                entry = new Entry(0, default, default, null, false);
            else
                entry = Upload(baked);
            entry.LastDraw = _drawTick;
            if (Cache.Count >= MaxEntries)
                EvictStale();
            Cache[job.Key] = entry;
        }
    }

    private static Entry Upload(SvgStrokeMask.Baked baked)
    {
        // Bakeable, but the mask came out empty: this draw paints nothing.
        if (baked.Width <= 0 || baked.Height <= 0)
            return new Entry(0, default, default, null, false);
        var (handle, keepalive) = _uploader!(
            SvgStrokeMask.Pack(baked), baked.Width, baked.Height);
        if (handle == 0)
        {
            keepalive?.Dispose();
            return new Entry(0, default, default, null, true);
        }
        return new Entry(
            handle,
            baked.Origin,
            new Vector2(baked.Width, baked.Height),
            keepalive,
            false);
    }

    internal static void Clear()
    {
        foreach (var entry in Cache.Values)
            entry.Keepalive?.Dispose();
        Cache.Clear();
        Array.Clear(Seen);
        _seenAt = 0;

        // Discard pending work. The queues are emptied for the jobs that have
        // not been picked up, and the generation bump covers the one a worker
        // may be inside RIGHT NOW: its result is enqueued as usual and dropped
        // on the next drain. Nothing here waits on or interrupts a worker.
        _generation++;
        while (Inbox.TryDequeue(out _)) { }
        while (Completed.TryDequeue(out _)) { }
        Pending.Clear();
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

    // Per-frame budget state. The frame is read from ImGui rather than taken
    // from a new public seam, so nothing outside has to remember to tick this.
    private static int _frame = -1;
    private static int _paints;
    private static int _uploads;

    /// <summary>Draws the icon from a baked texture, reports that this draw
    /// belongs to the painter (false), or absorbs it for this frame while its
    /// bake is in flight (true, drawing nothing). Allocation-free on a cache
    /// hit.</summary>
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

        float styleAlpha = ImGui.GetStyle().Alpha;
        ulong key = Key(
            doc, min, max, tint, flipX, strokeWidth,
            groupOpacity, groupBackground, styleAlpha);
        var floor = new Vector2(MathF.Floor(min.X), MathF.Floor(min.Y));
        _drawTick++;

        int frame = ImGui.GetFrameCount();
        if (frame != _frame)
        {
            _frame = frame;
            _paints = 0;
            _uploads = 0;
        }
        // Unconditional: in synchronous mode nothing ever enqueues, so this is
        // a single IsEmpty check and the goldens see no difference — but if
        // the flag is flipped mid-session, work already in flight still lands
        // instead of stranding its keys in Pending forever.
        if (!Completed.IsEmpty)
            Integrate(ref _uploads);

        ref var slot = ref System.Runtime.InteropServices.CollectionsMarshal
            .GetValueRefOrNullRef(Cache, key);
        Entry entry;
        if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref slot))
        {
            slot.LastDraw = _drawTick;
            entry = slot;
        }
        else if (SynchronousBakes)
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
                doc, max - min, tint, flipX, strokeWidth,
                groupOpacity, groupBackground, styleAlpha);
            entry.LastDraw = _drawTick;
            if (Cache.Count >= MaxEntries)
                EvictStale();
            Cache[key] = entry;
        }
        else
        {
            // Repeated keys are the ones worth a texture, and one job covers
            // however many frames the bake takes to come back. A transition
            // tint that never repeats never enqueues — it stays on the
            // painter path exactly as before, and spends painter budget.
            if (Repeated(key) && Pending.Add(key))
            {
                Inbox.Enqueue(new RasterJob
                {
                    Generation = _generation,
                    Key = key,
                    Doc = doc,
                    Size = max - min,
                    Tint = tint,
                    FlipX = flipX,
                    StrokeWidth = strokeWidth,
                    GroupOpacity = groupOpacity,
                    GroupBackground = groupBackground,
                    StyleAlpha = styleAlpha,
                });
                Pump();
            }
            if (_paints < PaintBudget)
            {
                _paints++;
                return false;
            }
            // Over budget: claim the draw and emit nothing. Losing an icon
            // for a frame or two during a storm is the price of never
            // software-painting forty of them at once.
            return true;
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
        Vector2 size,
        Vector4? tint,
        bool flipX,
        float? strokeWidth,
        float groupOpacity,
        Vector4 groupBackground,
        float styleAlpha)
    {
        // Fills and multicolour documents keep the painter's ordering.
        if (!doc.TryResolveMask(
                Vector2.Zero, size, tint, flipX, strokeWidth,
                groupOpacity, groupBackground, styleAlpha, out var baked))
            return new Entry(0, default, default, null, true);
        if (baked is null)
            return new Entry(0, default, default, null, false);
        return Upload(baked);
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
        Vector4 background,
        float styleAlpha)
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
        return Mix(hash, Bits(styleAlpha));
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
