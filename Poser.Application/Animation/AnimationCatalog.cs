using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Domain.Animation;

namespace Poser.Application.Animation;

/// <summary>Searchable catalog of sheet-backed animation choices.</summary>
public sealed class AnimationCatalog
{
    private IReadOnlyList<TimelineEntry> _entries = Array.Empty<TimelineEntry>();

    public bool IsLoaded { get; private set; }
    public int Count => _entries.Count;
    public IReadOnlyList<TimelineEntry> Entries => _entries;

    public void Publish(IReadOnlyList<TimelineEntry> entries)
    {
        // Named sheet rows lead; raw keys remain available as the fallback.
        _entries = entries
            .OrderBy(static entry => entry.Kind switch
            {
                AnimationKind.Emote or AnimationKind.Expression => 0,
                AnimationKind.Action => 1,
                _ => 2,
            })
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.EmoteIndex)
            .ThenBy(static entry => entry.TimelineId)
            .ToArray();
        IsLoaded = true;
    }

    /// <summary>
    /// Case-insensitive name/key search composed with kind and slot filters.
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
                (entry.Key == null || entry.Key.IndexOf(
                    query, StringComparison.OrdinalIgnoreCase) < 0) &&
                !MatchesId(entry, query))
                continue;
            results.Add(entry);
            if (results.Count >= limit)
                break;
        }
        return results;
    }

    /// <summary>A bare number matches the native timeline id.</summary>
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
    /// Kinds that cannot occur in a selected native slot.
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

}
