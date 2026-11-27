using System.Collections.Generic;
using Poser.Domain.Integration;

namespace Poser.Application.Catalog;

/// <summary>The wardrobe the pickers list: every item per slot, every dye,
/// every facewear, read once from the game sheets.</summary>
public interface IWardrobeCatalog
{
    IReadOnlyList<WardrobeItem> ItemsFor(EquipSlot slot);
    IReadOnlyList<DyeEntry> Dyes { get; }
    IReadOnlyList<FacewearEntry> Facewear { get; }
    WardrobeItem? Item(uint id);
    DyeEntry? Dye(byte id);
    /// <summary>Why reading one list from the game data failed, shown in that
    /// list's picker; null otherwise.</summary>
    string? LoadFailure(WardrobeList list);
    /// <summary>Forgets a failed read of one list, so its next read tries
    /// again; called when that list's picker opens.</summary>
    void Retry(WardrobeList list);
}

/// <summary>The three lists the wardrobe reads from the game data.</summary>
public enum WardrobeList { Items, Dyes, Facewear }
