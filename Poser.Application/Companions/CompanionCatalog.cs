using System;
using System.Collections.Generic;
using Poser.Domain.Companions;

namespace Poser.Application.Companions;

/// <summary>
/// The searchable companion catalog. Holds one flat list of attachable
/// minions, mounts and ornaments and answers filtered queries; it never
/// touches the game, so the loader can populate it off the framework
/// thread and every surface reads the same rows.
///
/// Entries that cannot be attached are never admitted (that is the
/// loader's job), so a search result is always selectable.
/// </summary>
public sealed class CompanionCatalog
{
    private IReadOnlyList<CompanionEntry> _entries = Array.Empty<CompanionEntry>();

    public bool IsLoaded { get; private set; }
    public int Count => _entries.Count;
    public IReadOnlyList<CompanionEntry> Entries => _entries;

    public void Publish(IReadOnlyList<CompanionEntry> entries)
    {
        _entries = entries;
        IsLoaded = true;
    }

    /// <summary>
    /// Case-insensitive name search composed with the kind filter. An empty
    /// query matches everything; a null kind means no kind restriction.
    /// </summary>
    public IReadOnlyList<CompanionEntry> Search(
        string query,
        CompanionKind? kind = null,
        int limit = 512)
    {
        var results = new List<CompanionEntry>();
        bool matchAll = string.IsNullOrWhiteSpace(query);
        foreach (var entry in _entries)
        {
            if (kind != null && entry.Kind != kind)
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

    /// <summary>Typing a bare number finds the row with that id, so direct
    /// id entry and search are the same box.</summary>
    private static bool MatchesId(CompanionEntry entry, string query) =>
        ushort.TryParse(query, out var id) && entry.Id == id;

    /// <summary>Ids are only unique within a sheet, so a lookup needs both.</summary>
    public CompanionEntry? Find(CompanionKind kind, ushort id)
    {
        foreach (var entry in _entries)
            if (entry.Kind == kind && entry.Id == id)
                return entry;
        return null;
    }
}
