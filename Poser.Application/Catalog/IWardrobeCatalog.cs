using System.Collections.Generic;
using Poser.Domain.Integration;

namespace Poser.Services;

/// <summary>The wardrobe the pickers list: every item per slot, every dye,
/// every facewear, read once from the game sheets.</summary>
public interface IWardrobeCatalog
{
    IReadOnlyList<WardrobeItem> ItemsFor(EquipSlot slot);
    IReadOnlyList<DyeEntry> Dyes { get; }
    IReadOnlyList<FacewearEntry> Facewear { get; }
    WardrobeItem? Item(uint id);
    DyeEntry? Dye(byte id);
}
