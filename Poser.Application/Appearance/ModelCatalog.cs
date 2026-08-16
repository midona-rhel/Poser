using System;
using System.Collections.Generic;
using System.Threading;
using Poser.Domain.Appearance;

namespace Poser.Application.Appearance;

/// <summary>
/// The searchable ModelChara rows used by the model editor. The catalog is
/// populated off the framework thread and then read by every UI surface.
/// </summary>
public sealed class ModelCatalog
{
    private IReadOnlyList<ModelCatalogEntry> _entries = Array.Empty<ModelCatalogEntry>();
    private int _publicationVersion;
    private int _loaded;

    public bool IsLoaded => Volatile.Read(ref _loaded) != 0;
    public int PublicationVersion => Volatile.Read(ref _publicationVersion);
    public int Count => Volatile.Read(ref _entries).Count;
    public IReadOnlyList<ModelCatalogEntry> Entries => Volatile.Read(ref _entries);

    public void Publish(IReadOnlyList<ModelCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copy = new ModelCatalogEntry[entries.Count];
        for (int i = 0; i < entries.Count; i++)
            copy[i] = entries[i];
        var snapshot = Array.AsReadOnly(copy);
        Volatile.Write(ref _entries, snapshot);
        Volatile.Write(ref _loaded, 1);
        Interlocked.Increment(ref _publicationVersion);
    }

    /// <summary>
    /// Searches names and model ids with an optional kind filter.
    /// </summary>
    public IReadOnlyList<ModelCatalogEntry> Search(
        string query,
        ModelCatalogKind? kind = null,
        int limit = 512)
    {
        var entries = Volatile.Read(ref _entries);
        var results = new List<ModelCatalogEntry>();
        bool matchAll = string.IsNullOrWhiteSpace(query);
        bool numeric = int.TryParse(query, out var queriedId);
        foreach (var entry in entries)
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

    /// <summary>Returns the first row drawing as the supplied model id.</summary>
    public ModelCatalogEntry? FindByModelCharaId(int modelCharaId)
    {
        var entries = Volatile.Read(ref _entries);
        foreach (var entry in entries)
            if (entry.ModelCharaId == modelCharaId)
                return entry;
        return null;
    }
}
