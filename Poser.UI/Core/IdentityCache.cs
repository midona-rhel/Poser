using System;
using System.Collections.Generic;

namespace Poser.UI.Reactive;

/// <summary>
/// The path-identity table. Identity is DERIVED — from the parent path, the
/// element kind, the author's key or the sibling ordinal, and the owning
/// component scope — so a tree that redeclares itself from scratch every frame
/// still addresses the same retained cells. The cache holds one cell per live
/// path and drops the ones a frame did not visit.
/// </summary>
internal sealed class IdentityCache
{
    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>One path's retained cell: the "##rx…" id string, its single
    /// suffixed variant, and the portal body closure. Formatting and closure
    /// construction happen ONCE per path, on first sight; every later frame
    /// reuses the instances, which is what makes a warm frame allocation-free.
    /// Everything a path retains lives here so that pruning the path frees all
    /// of it.</summary>
    internal sealed class IdEntry
    {
        internal IdEntry(string id, int frame)
        {
            Id = id;
            LastSeenFrame = frame;
        }

        internal string Id;

        /// <summary>The ONE suffixed name this path needs: a portal handle
        /// ("_popup") on the anchor, a scroll child ("-scroll") on the portal,
        /// a truncation readout ("-full") on the run.</summary>
        internal string? Alternate;

        /// <summary>The truncation readout's own name. Separate from
        /// <see cref="Alternate"/> because one element can need both: a picker
        /// trigger owns a surface AND cuts its own caption.</summary>
        internal string? Preview;

        internal PortalHost.PortalBody? Body;

        /// <summary>An IN-WINDOW scroll container's retained body. Separate
        /// from <see cref="Body"/> because the two hosts are different
        /// elements' machinery, never the same path's.</summary>
        internal ScrollHost.ScrollBody? Scroll;

        internal int LastSeenFrame;
    }

    private readonly FrameArena _arena;
    private readonly Dictionary<ulong, IdEntry> _interactionIds = [];
    // Retained so pruning costs no allocation on a frame that drops a path.
    private readonly List<ulong> _prunedIds = [];

    internal IdentityCache(FrameArena arena) => _arena = arena;

    /// <summary>Live interaction-id paths; the pruning invariant's probe.</summary>
    internal int Count => _interactionIds.Count;

    // Path identity: parent path, the author's key OR the sibling ordinal, and
    // the owning component scope. A KEYED element drops the ordinal outright —
    // that is what lets a reordered list carry its hover and motion state with
    // it instead of inheriting its neighbour's. There is no kind byte to mix:
    // there is one element.
    internal static ulong Chain(
        ulong parentHash, int ordinal, UiKey key, int scopeId)
    {
        ulong hash = parentHash == 0UL ? FnvOffset : parentHash;
        hash = key.Kind != UiKeyKind.None
            ? key.HashInto(hash)
            : Mix(hash, (ulong)(uint)ordinal);
        return Mix(hash, (ulong)(uint)scopeId);
    }

    internal static ulong Mix(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= FnvPrime;
        }

        return hash;
    }

    internal IdEntry Entry(ulong hash)
    {
        int frame = _arena.FrameId;
        if (_interactionIds.TryGetValue(hash, out IdEntry? entry))
        {
            entry.LastSeenFrame = frame;
            return entry;
        }

        entry = new IdEntry("##rx" + hash.ToString("x16"), frame);
        _interactionIds[hash] = entry;
        return entry;
    }

    /// <summary>The derived names a path is allowed. Suffixing is a retained
    /// string, not a per-frame concatenation.</summary>
    internal string AlternateId(ulong hash, string suffix)
    {
        IdEntry entry = Entry(hash);
        return entry.Alternate ??= entry.Id + suffix;
    }

    /// <inheritdoc cref="AlternateId"/>
    internal string PreviewId(ulong hash)
    {
        IdEntry entry = Entry(hash);
        return entry.Preview ??= entry.Id + "-full";
    }

    internal string InteractionId(ulong hash)
    {
#if DEBUG
        if (_interactionIds.TryGetValue(hash, out IdEntry? existing)
            && existing.LastSeenFrame == _arena.FrameId)
            throw new InvalidOperationException(
                $"Duplicate interaction path {existing.Id}: two siblings of one kind "
                + "resolved to the same identity, so they share a key (or both lack one "
                + "while sharing an ordinal). Give each an explicit stable key.");
#endif
        return Entry(hash).Id;
    }

    // A path the frame did not visit is gone: keeping it would leak one
    // entry per row a long-lived list ever showed.
    internal void Prune(int frame)
    {
        _prunedIds.Clear();
        foreach (KeyValuePair<ulong, IdEntry> entry in _interactionIds)
        {
            if (entry.Value.LastSeenFrame < frame)
                _prunedIds.Add(entry.Key);
        }

        for (int i = 0; i < _prunedIds.Count; i++)
            _interactionIds.Remove(_prunedIds[i]);
    }
}
