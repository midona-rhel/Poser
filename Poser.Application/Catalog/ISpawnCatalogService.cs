using System.Collections.Generic;
using Poser.Domain.Companions;

namespace Poser.Services;

/// <summary>
/// Every minion, mount and fashion accessory as ONE flat immutable list.
/// The list is built on first access and never again: plugin load pays
/// nothing, and no frame may enumerate the sheets.
/// </summary>
public interface ISpawnCatalogService
{
    IReadOnlyList<SpawnCatalogEntry> Entries { get; }
}
