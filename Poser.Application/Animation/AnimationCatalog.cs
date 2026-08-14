using System;
using System.Collections.Generic;
using Poser.Domain.Animation;

namespace Poser.Application.Animation;

/// <summary>
/// The searchable animation catalog. Holds one flat list of playable
/// timelines and answers filtered queries; it never touches the game, so
/// the loader can populate it off the framework thread and every surface
/// reads the same rows.
///
/// Entries that cannot be played are never admitted (that is the loader's
/// job), so a search result is always selectable — the PBI's "unsupported
/// entries remain absent rather than failing after selection".
/// </summary>
public sealed class AnimationCatalog
{
    private IReadOnlyList<TimelineEntry> _entries = Array.Empty<TimelineEntry>();

    public bool IsLoaded { get; private set; }
    public int Count => _entries.Count;
    public IReadOnlyList<TimelineEntry> Entries => _entries;

    public void Publish(IReadOnlyList<TimelineEntry> entries)
    {
        _entries = entries;
        IsLoaded = true;
    }

    /// <summary>
    /// Case-insensitive name search composed with the kind and slot
    /// filters. An empty query matches everything; a null slot means no
    /// slot restriction. Kind and slot compose as AND, matching Ktisis.
    /// </summary>
    public IReadOnlyList<TimelineEntry> Search(
        string query,
        AnimationKind? kind = null,
        AnimationSlot? slot = null,
        int limit = 512)
    {
        var results = new List<TimelineEntry>();
        bool matchAll = string.IsNullOrWhiteSpace(query);
        foreach (var entry in _entries)
        {
            if (kind != null && entry.Kind != kind)
                continue;
            if (slot != null && entry.Slot != slot)
                continue;
            if (!matchAll &&
                entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                !MatchesId(entry, query))
                continue;
            results.Add(entry);
            if (results.Count >= limit)
                break;
        }
        return results;
    }

    /// <summary>Typing a bare number finds the timeline with that id, so
    /// direct id entry and search are the same box.</summary>
    private static bool MatchesId(TimelineEntry entry, string query) =>
        uint.TryParse(query, out var id) && entry.TimelineId == id;

    public TimelineEntry? Find(uint timelineId)
    {
        foreach (var entry in _entries)
            if (entry.TimelineId == timelineId)
                return entry;
        return null;
    }

    /// <summary>
    /// Kinds that cannot occur in a slot, so the kind filter can drop
    /// choices that would always return nothing (Ktisis' cross-filter
    /// invalidation). Returns an empty set when no slot is selected.
    /// </summary>
    public static IReadOnlyList<AnimationKind> ExcludedKinds(AnimationSlot? slot) => slot switch
    {
        null => Array.Empty<AnimationKind>(),
        AnimationSlot.Base or AnimationSlot.UpperBody =>
            new[] { AnimationKind.Expression },
        AnimationSlot.Facial =>
            new[] { AnimationKind.Action, AnimationKind.Emote },
        AnimationSlot.Additive =>
            new[] { AnimationKind.Action, AnimationKind.Expression },
        AnimationSlot.Lips =>
            new[] { AnimationKind.Action, AnimationKind.Emote, AnimationKind.Expression },
        _ => Array.Empty<AnimationKind>(),
    };

    /// <summary>The most useful kind still available for a slot, used when
    /// the current kind becomes impossible after a slot change.</summary>
    public static AnimationKind BestKind(AnimationSlot? slot)
    {
        var excluded = ExcludedKinds(slot);
        foreach (var candidate in new[]
                 {
                     AnimationKind.Emote, AnimationKind.Action, AnimationKind.Expression,
                 })
        {
            bool blocked = false;
            foreach (var kind in excluded)
                if (kind == candidate)
                    blocked = true;
            if (!blocked)
                return candidate;
        }
        return AnimationKind.RawTimeline;
    }
}
