using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;

namespace Poser.UI;

/// <summary>
/// Bounded, asynchronous thumbnail store for the pose library grid.
/// <para>
/// A .pose file may carry a top-level <c>Base64Image</c> string (see
/// <c>PosingCore/Files/PoseFile.cs</c>). Decoding one costs a file read, a
/// JSON parse, a base64 decode and a texture upload — Brio does all four on
/// the render thread via <c>CreateFromImageAsync(data).Wait()</c>, which
/// stalls the frame once per newly visible tile. Here the decode runs on a
/// worker and the render thread only ever does a dictionary lookup.
/// </para>
/// <para>
/// Threading contract: every field except <see cref="_completed"/>,
/// <see cref="_decodeGate"/> and <see cref="_disposed"/> is main-thread-only.
/// Finished decodes cross back through the queue and are integrated in
/// <see cref="Tick"/>.
/// </para>
/// </summary>
public sealed class PoseThumbnailCache : IDisposable
{
    /// <summary>Floor for the eviction budget. The real budget tracks how many
    /// tiles the grid asked for last frame, so a view showing more than this
    /// can still keep everything it draws.</summary>
    private const int MinTextureBudget = 128;

    /// <summary>Concurrent decodes. A resize or a scroll can miss dozens of
    /// tiles in one frame; unthrottled that is dozens of simultaneous large
    /// reads, base64 decodes and uploads.</summary>
    private const int MaxConcurrentDecodes = 3;

    /// <summary>Textures selected per eviction pass. Bounds the reusable
    /// selection buffer so eviction stays allocation-free.</summary>
    private const int EvictionBatch = 32;

    /// <summary>Guards the <c>int</c> cast for the pooled rental. A pose file
    /// past this is not a thumbnail source; it is treated as image-less.</summary>
    private const long MaxPoseFileBytes = 32L * 1024 * 1024;

    private const string Base64ImageProperty = "Base64Image";

    // The repo's own pose reader tolerates trailing commas (PoseFile.JsonOptions),
    // so the thumbnail probe must not reject a file the loader would accept.
    private static readonly JsonDocumentOptions JsonReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// One tile's state. A reference type so the hit path can touch
    /// <see cref="LastTouch"/> without re-storing into the dictionary.
    /// </summary>
    private sealed class Slot
    {
        public IDalamudTextureWrap? Wrap;
        public nint Handle;
        /// <summary>The wrap's natural pixel size, read once on integration:
        /// an aspect fit needs it every frame and the wrap must not be
        /// re-queried on the hit path.</summary>
        public Vector2 Size;
        public bool Failed;
        public bool Loading;
        public long LastTouch;
    }

    private readonly ITextureProvider _textureProvider;

    private readonly Dictionary<string, Slot> _slots = new();

    /// <summary>The only cross-thread channel. A null wrap means "no usable image".</summary>
    private readonly ConcurrentQueue<(string Path, int Generation, IDalamudTextureWrap? Wrap)>
        _completed = new();

    /// <summary>Never disposed: workers may be parked on it long after
    /// <see cref="Dispose"/>, and a <see cref="SemaphoreSlim"/> whose
    /// <c>AvailableWaitHandle</c> is never touched owns nothing to release.</summary>
    private readonly SemaphoreSlim _decodeGate = new(MaxConcurrentDecodes, MaxConcurrentDecodes);

    /// <summary>Reused selection buffer for <see cref="Evict"/>, kept ascending
    /// by <see cref="Slot.LastTouch"/> while a pass runs. Slots are cleared out
    /// again at the end of the pass so eviction never pins one alive.</summary>
    private readonly Slot?[] _evictionBuffer = new Slot?[EvictionBatch];

    private long _frame;

    /// <summary>Distinct tiles asked for since the last <see cref="Tick"/>, and
    /// the same count for the frame before it. The budget is derived from the
    /// latter, so eviction can never drop a texture the grid is still drawing.</summary>
    private int _touchedThisFrame;
    private int _touchedLastFrame;

    /// <summary>Live uploaded textures, tracked incrementally so an
    /// under-budget <see cref="Tick"/> never has to count them.</summary>
    private int _liveTextures;

    /// <summary>Bumped by <see cref="Clear"/>; loads carry the value they
    /// started under so late arrivals from a cleared cache are discarded.</summary>
    private int _generation;

    private volatile bool _disposed;

    public PoseThumbnailCache(ITextureProvider textureProvider)
    {
        _textureProvider = textureProvider;
    }

    /// <summary>
    /// The ImGui texture handle for a pose file's embedded thumbnail, or 0
    /// while it is loading and for files with no usable image. The first miss
    /// starts an asynchronous load; a hit marks the entry as recently used.
    /// </summary>
    /// <remarks>
    /// Call this only for tiles the clipper actually draws — visibility is
    /// what drives both loading and eviction.
    /// </remarks>
    public nint Get(string filePath) => Get(filePath, out _);

    /// <summary>
    /// As <see cref="Get(string)"/>, and also answers the image's natural pixel
    /// size — <see cref="Vector2.Zero"/> whenever the handle is 0. A handle
    /// alone cannot be aspect-fitted, so the drawing caller asks for both.
    /// </summary>
    public nint Get(string filePath, out Vector2 size)
    {
        if (_slots.TryGetValue(filePath, out var slot))
        {
            if (slot.LastTouch != _frame)
            {
                slot.LastTouch = _frame;
                _touchedThisFrame++;
            }

            if (slot.Wrap is not null)
            {
                size = slot.Size;
                return slot.Handle;
            }

            // Wrap-less and neither loading nor failed means the slot was
            // evicted: the entry survives to keep its touch history, but the
            // image has to be decoded again exactly like a fresh miss.
            if (!slot.Loading && !slot.Failed)
            {
                slot.Loading = true;
                StartLoad(filePath);
            }

            // Failed is memoized so a broken or image-less file is probed
            // once, not once per frame. Loading simply has nothing yet.
            size = Vector2.Zero;
            return 0;
        }

        _slots[filePath] = new Slot { Loading = true, LastTouch = _frame };
        _touchedThisFrame++;

        StartLoad(filePath);
        size = Vector2.Zero;
        return 0;
    }

    /// <summary>
    /// Advances the frame counter, integrates finished decodes and evicts
    /// down to the texture budget. Call once per frame from the window's Draw.
    /// </summary>
    public void Tick()
    {
        // Tick leads the frame's Get calls, so the running counter still holds
        // the previous frame's demand at this point.
        _touchedLastFrame = _touchedThisFrame;
        _touchedThisFrame = 0;

        _frame++;

        while (_completed.TryDequeue(out var done))
            Integrate(done.Path, done.Generation, done.Wrap);

        Evict();
    }

    /// <summary>
    /// Disposes every texture and resets the cache, including memoized
    /// failures. In-flight loads are invalidated rather than awaited.
    /// </summary>
    public void Clear()
    {
        // Bump first: anything a worker enqueues from here on is already stale.
        _generation++;

        foreach (var slot in _slots.Values)
            slot.Wrap?.Dispose();
        _slots.Clear();
        _liveTextures = 0;
        _touchedThisFrame = 0;
        _touchedLastFrame = 0;

        DrainAndDiscard();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        // Set before clearing so workers stop enqueueing as early as possible;
        // Clear's drain then collects whatever was already in flight.
        _disposed = true;
        Clear();
    }

    private void StartLoad(string filePath)
    {
        var generation = _generation;
        _ = Task.Run(() => LoadAsync(filePath, generation));
    }

    private void Integrate(string filePath, int generation, IDalamudTextureWrap? wrap)
    {
        if (generation != _generation || !_slots.TryGetValue(filePath, out var slot))
        {
            wrap?.Dispose();
            return;
        }

        slot.Loading = false;

        if (wrap is null)
        {
            slot.Failed = true;
            return;
        }

        slot.Wrap = wrap;
        slot.Handle = (nint)wrap.Handle.Handle;
        slot.Size = new Vector2(wrap.Width, wrap.Height);
        slot.LastTouch = _frame;
        _liveTextures++;
    }

    /// <summary>
    /// Drops the least recently touched textures until the cache is inside
    /// budget. Failed, evicted and still-loading slots hold no texture, so they
    /// neither count against the budget nor get evicted.
    /// </summary>
    /// <remarks>
    /// The budget is twice the previous frame's distinct requests, floored at
    /// <see cref="MinTextureBudget"/>: a fixed cap smaller than the visible
    /// tile count would evict textures the grid re-requests on the very next
    /// frame, paying the full decode again every frame.
    /// </remarks>
    private void Evict()
    {
        var budget = Math.Max(MinTextureBudget, _touchedLastFrame * 2);

        while (_liveTextures > budget)
        {
            var overflow = _liveTextures - budget;
            var wanted = overflow < EvictionBatch ? overflow : EvictionBatch;

            var count = 0;
            var newest = long.MaxValue;

            // One pass picks the whole batch: the buffer is held ascending by
            // LastTouch, and once it is full anything not older than its last
            // entry can be skipped outright.
            // Struct enumerator over the concrete dictionary: no allocation.
            foreach (var entry in _slots)
            {
                var slot = entry.Value;
                if (slot.Wrap is null)
                    continue;
                if (count == wanted && slot.LastTouch >= newest)
                    continue;

                var i = count < wanted ? count : wanted - 1;
                while (i > 0 && _evictionBuffer[i - 1]!.LastTouch > slot.LastTouch)
                {
                    _evictionBuffer[i] = _evictionBuffer[i - 1];
                    i--;
                }

                _evictionBuffer[i] = slot;
                if (count < wanted)
                    count++;
                newest = _evictionBuffer[count - 1]!.LastTouch;
            }

            if (count == 0)
            {
                // Budget and reality disagree; trust reality rather than spin.
                _liveTextures = 0;
                return;
            }

            for (var i = 0; i < count; i++)
            {
                var slot = _evictionBuffer[i]!;
                _evictionBuffer[i] = null;

                slot.Wrap?.Dispose();
                slot.Wrap = null;
                slot.Handle = 0;
                slot.Size = Vector2.Zero;
                slot.Loading = false;
                slot.Failed = false;
                _liveTextures--;
            }
        }
    }

    /// <summary>Disposes anything sitting in the completion queue. Safe to
    /// call from either thread: each queued item is handed to exactly one
    /// dequeuer.</summary>
    private void DrainAndDiscard()
    {
        while (_completed.TryDequeue(out var done))
            done.Wrap?.Dispose();
    }

    /// <summary>
    /// Worker body. Reads the pose file, pulls out the embedded image and
    /// uploads it. Never throws: every failure path enqueues a null wrap,
    /// which the main thread memoizes as a failure.
    /// </summary>
    private async Task LoadAsync(string filePath, int generation)
    {
        IDalamudTextureWrap? wrap = null;

        await _decodeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[]? imageBytes = null;
            byte[]? rented = null;

            try
            {
                using var handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var length = RandomAccess.GetLength(handle);
                if (length is > 0 and <= MaxPoseFileBytes)
                {
                    var size = (int)length;
                    rented = ArrayPool<byte>.Shared.Rent(size);

                    var read = 0;
                    while (read < size)
                    {
                        var got = RandomAccess.Read(handle, rented.AsSpan(read, size - read), read);
                        if (got <= 0)
                            break;
                        read += got;
                    }

                    // JsonDocument.Parse over ReadOnlyMemory does not copy: the
                    // document reads straight through to this buffer for its
                    // whole lifetime, so the rental must outlive it. The inner
                    // scope forces the document's dispose to run before the
                    // finally below hands the array back to the pool.
                    using var document = JsonDocument.Parse(
                        new ReadOnlyMemory<byte>(rented, 0, read), JsonReadOptions);

                    var root = document.RootElement;
                    if (root.ValueKind == JsonValueKind.Object
                        && root.TryGetProperty(Base64ImageProperty, out var property)
                        && property.ValueKind == JsonValueKind.String
                        // Decodes from the element's UTF-8 span: no intermediate
                        // string, so only the image itself is ever allocated.
                        && !property.TryGetBytesFromBase64(out imageBytes))
                    {
                        imageBytes = null;
                    }
                }
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented);
            }

            // No image on this pose is an ordinary outcome, not an error.
            if (imageBytes is { Length: > 0 })
            {
                wrap = await _textureProvider
                    .CreateFromImageAsync(imageBytes, "Poser pose thumbnail")
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            wrap?.Dispose();
            wrap = null;
        }
        finally
        {
            _decodeGate.Release();
        }

        if (_disposed)
        {
            wrap?.Dispose();
            return;
        }

        _completed.Enqueue((filePath, generation, wrap));

        // Closes the other ordering of the same race: disposal may have run
        // between the check above and the enqueue, leaving nobody to drain.
        if (_disposed)
            DrainAndDiscard();
    }
}
