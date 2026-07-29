using System;
using System.Collections.Generic;

namespace Poser.UI;

/// <summary>
/// Host-provided texture factory for group-composited surfaces
/// (straight-alpha RGBA8). The game plugin backs this with Dalamud's
/// texture provider; the conformance capture host backs it with its own
/// D3D11 renderer, so both render the identical composed pixels.
/// </summary>
public interface IGroupSurfaceBackend
{
    nint CreateTexture(byte[] rgba, int width, int height);
    void DestroyTexture(nint texture);
}

/// <summary>
/// CSS group-opacity surfaces: an element that flattens (fill, border,
/// glyph coverage, antialiasing) BEFORE one opacity application cannot
/// be reproduced by sequentially blended primitives, so it is composed
/// on the CPU into a texture and drawn as one quad. Cached by content
/// key; entries unused for a while are destroyed.
/// </summary>
public static class GroupSurface
{
    private sealed class Entry
    {
        public nint Texture;
        public int LastFrame;
    }

    private static IGroupSurfaceBackend? _backend;
    private static readonly Dictionary<long, Entry> Cache = new();

    public static bool Available => _backend != null;

    public static void Register(IGroupSurfaceBackend backend)
    {
        Clear();
        _backend = backend;
    }

    public static void Clear()
    {
        if (_backend != null)
            foreach (var entry in Cache.Values)
                _backend.DestroyTexture(entry.Texture);
        Cache.Clear();
    }

    /// <summary>Returns the cached texture for the content key, composing
    /// it once via <paramref name="compose"/> (straight-alpha RGBA8 of
    /// exactly width×height) when absent. Null when no backend exists —
    /// callers fall back to their nearest sequential approximation.</summary>
    internal static nint? Acquire(
        long key, int width, int height, int frame, Func<byte[]> compose)
    {
        if (_backend == null || width <= 0 || height <= 0)
            return null;
        if (Cache.Count > 128)
            Prune(frame);
        if (Cache.TryGetValue(key, out var entry))
        {
            entry.LastFrame = frame;
            return entry.Texture;
        }
        var pixels = compose();
        if (pixels.Length != width * height * 4)
            throw new InvalidOperationException(
                "Group surface composition returned the wrong pixel count.");
        var texture = _backend.CreateTexture(pixels, width, height);
        Cache[key] = new Entry { Texture = texture, LastFrame = frame };
        return texture;
    }

    private static void Prune(int frame)
    {
        var stale = new List<long>();
        foreach (var (key, entry) in Cache)
            if (frame - entry.LastFrame > 120)
                stale.Add(key);
        foreach (var key in stale)
        {
            _backend!.DestroyTexture(Cache[key].Texture);
            Cache.Remove(key);
        }
    }
}
