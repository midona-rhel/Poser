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
    /// <summary>Why reading an item, dye or facewear list from the game data
    /// failed, shown in the pickers; null otherwise.</summary>
    string? LoadFailure { get; }
    /// <summary>Reads a failed list again; called when a picker opens.</summary>
    void Retry();
}
