using System.Collections.Generic;
using Poser.Game.Types;

namespace Poser.Services;

/// <summary>
/// One spawnable the game sheets declare: the kind it attaches as, its sheet
/// row id, the display name, that name PRE-LOWERCASED, and the sheet's icon.
/// The lowercase copy is minted at build time because a search scans every
/// entry on each keystroke and may allocate nothing while doing it.
/// </summary>
public readonly record struct SpawnCatalogEntry(
    CompanionKind Kind,
    ushort Id,
    string Name,
    string NameLower,
    uint IconId);

/// <summary>
/// Every minion, mount and fashion accessory as ONE flat immutable list.
/// The list is built on first access and never again: plugin load pays
/// nothing, and no frame may enumerate the sheets.
/// </summary>
public interface ISpawnCatalogService
{
    IReadOnlyList<SpawnCatalogEntry> Entries { get; }
}
