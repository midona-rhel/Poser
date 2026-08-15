using System;
using System.Collections.Generic;
using Poser.Domain.Appearance;

namespace Poser.Application.Appearance;

/// <summary>
/// The searchable name→ModelChara catalog behind the Model ID editor's
/// search (Brio's NpcSelector data, minus the whole-appearance apply its
/// call site performs — customize/equipment belong to Glamourer). Holds one
/// flat list and answers filtered queries; it never touches the game, so
/// the loader can populate it off the framework thread and every surface
/// reads the same rows.
/// </summary>
public sealed class ModelCatalog
{
    private IReadOnlyList<ModelCatalogEntry> _entries = Array.Empty<ModelCatalogEntry>();

    public bool IsLoaded { get; private set; }
    public int Count => _entries.Count;
    public IReadOnlyList<ModelCatalogEntry> Entries => _entries;

    public void Publish(IReadOnlyList<ModelCatalogEntry> entries)
    {
        _entries = entries;
        IsLoaded = true;
    }

    /// <summary>
    /// Case-insensitive name search composed with the kind filter. An empty
    /// query matches everything; a null kind means no kind restriction. A
    /// bare number is the same box finding the rows that DRAW as that
    /// ModelChara id (Brio folds the id into its search text,
    /// NpcSelector.cs:117-123).
    /// </summary>
    public IReadOnlyList<ModelCatalogEntry> Search(
        string query,
        ModelCatalogKind? kind = null,
        int limit = 512)
    {
        var results = new List<ModelCatalogEntry>();
        bool matchAll = string.IsNullOrWhiteSpace(query);
        bool numeric = int.TryParse(query, out var queriedId);
        foreach (var entry in _entries)
        {
            if (kind != null && entry.Kind != kind)
                continue;
            if (!matchAll &&
                entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                !(numeric && entry.ModelCharaId == queriedId))
                continue;
            results.Add(entry);
            if (results.Count >= limit)
                break;
        }
        return results;
    }

    /// <summary>The first row drawing as the id, for naming a current model
    /// id in a readout; many rows can share one model, any name serves.</summary>
    public ModelCatalogEntry? FindByModelCharaId(int modelCharaId)
    {
        foreach (var entry in _entries)
            if (entry.ModelCharaId == modelCharaId)
                return entry;
        return null;
    }
}
