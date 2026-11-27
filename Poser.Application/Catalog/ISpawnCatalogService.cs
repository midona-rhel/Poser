using System.Collections.Generic;
using Poser.Domain.Companions;

namespace Poser.Application.Catalog;

/// <summary>
/// Minions, mounts, and fashion accessories offered by the spawn browser.
/// Implementations should cache the game-data lookup instead of rebuilding it each frame.
/// </summary>
public interface ISpawnCatalogService
{
    IReadOnlyList<SpawnCatalogEntry> Entries { get; }
    /// <summary>Why reading the list from the game data failed, shown in the
    /// picker; null otherwise.</summary>
    string? LoadFailure { get; }
    /// <summary>Reads the list again after a failure; called when the picker opens.</summary>
    void Retry();
}
