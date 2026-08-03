using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
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
/// Threading contract: every field except <see cref="_completed"/> and
/// <see cref="_disposed"/> is main-thread-only. Finished decodes cross back
/// through the queue and are integrated in <see cref="Tick"/>.
/// </para>
/// </summary>
public sealed class PoseThumbnailCache : IDisposable
{
    // Bounded GPU memory: a large pose folder would otherwise hold one uploaded texture per file.
    private const int MaxTextures = 128;

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

    private long _frame;

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
            if (slot.Wrap is not null)
            {
                slot.LastTouch = _frame;
                size = slot.Size;
                return slot.Handle;
            }

            // Failed is memoized so a broken or image-less file is probed
            // once, not once per frame. Loading simply has nothing yet.
            size = Vector2.Zero;
            return 0;
        }

        _slots[filePath] = new Slot { Loading = true, LastTouch = _frame };

        var generation = _generation;
        _ = Task.Run(() => LoadAsync(filePath, generation));
        size = Vector2.Zero;
        return 0;
    }

    /// <summary>
    /// Advances the frame counter, integrates finished decodes and evicts
    /// down to the texture budget. Call once per frame from the window's Draw.
    /// </summary>
    public void Tick()
    {
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
    /// budget. Failed and still-loading slots hold no texture, so they neither
    /// count against the budget nor get evicted.
    /// </summary>
    private void Evict()
    {
        while (_liveTextures > MaxTextures)
        {
            string? oldestKey = null;
            Slot? oldestSlot = null;
            var oldestTouch = long.MaxValue;

            // Struct enumerator over the concrete dictionary: no allocation.
            foreach (var entry in _slots)
            {
                var slot = entry.Value;
                if (slot.Wrap is null || slot.LastTouch >= oldestTouch)
                    continue;

                oldestTouch = slot.LastTouch;
                oldestKey = entry.Key;
                oldestSlot = slot;
            }

            if (oldestKey is null || oldestSlot is null)
            {
                // Budget and reality disagree; trust reality rather than spin.
                _liveTextures = 0;
                return;
            }

            oldestSlot.Wrap?.Dispose();
            oldestSlot.Wrap = null;
            oldestSlot.Handle = 0;
            _slots.Remove(oldestKey);
            _liveTextures--;
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

        try
        {
            var fileBytes = File.ReadAllBytes(filePath);

            string? encoded = null;
            using (var document = JsonDocument.Parse(fileBytes, JsonReadOptions))
            {
                var root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty(Base64ImageProperty, out var property)
                    && property.ValueKind == JsonValueKind.String)
                {
                    encoded = property.GetString();
                }
            }

            // No image on this pose is an ordinary outcome, not an error.
            if (!string.IsNullOrWhiteSpace(encoded))
            {
                var imageBytes = Convert.FromBase64String(encoded);
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
