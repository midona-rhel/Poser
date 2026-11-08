using System.Collections.Generic;

namespace Poser.Domain.Integration;

/// <summary>Glamourer's API slot numbers. Six was the belt, which the
/// game no longer has; the left ring is fourteen.</summary>
public enum EquipSlot : byte
{
    MainHand = 1,
    OffHand = 2,
    Head = 3,
    Body = 4,
    Hands = 5,
    Legs = 7,
    Feet = 8,
    Ears = 9,
    Neck = 10,
    Wrists = 11,
    RightFinger = 12,
    LeftFinger = 14,
}

/// <summary>Glamourer's meta flags.</summary>
public enum MetaSwitch : byte
{
    Wetness = 0x01,
    HatVisible = 0x02,
    VisorToggled = 0x04,
    WeaponVisible = 0x08,
}

/// <summary>
/// An item's display information and the model identifiers used to render it.
/// </summary>
public sealed record WardrobeItem(
    uint Id, string Name, uint Icon, byte DyeCount,
    ushort Model, ushort WeaponType, byte Variant);

public sealed record DyeEntry(byte Id, string Name, uint Color);

public sealed record FacewearEntry(uint Id, string Name, uint Icon);

/// <summary>What one slot wears: the item id in Glamourer's numbering
/// (see <see cref="WardrobeIds"/>) and its two dyes.</summary>
public readonly record struct WardrobeSlot(ulong ItemId, byte Dye1, byte Dye2);

/// <summary>An actor's wardrobe as Glamourer reports it.</summary>
public sealed record WardrobeState(
    IReadOnlyDictionary<EquipSlot, WardrobeSlot> Slots,
    ulong Facewear,
    bool HatVisible,
    bool VisorToggled,
    bool WeaponVisible,
    bool VieraEarsVisible)
{
    public WardrobeSlot Slot(EquipSlot slot) =>
        Slots.TryGetValue(slot, out var worn) ? worn : default;
}

/// <summary>
/// Encodes Glamourer item IDs. Ordinary items use game-table row IDs.
/// Special values near uint.MaxValue represent empty slots and smallclothes.
/// Custom IDs pack a model, weapon type, and variant without requiring an item row.
/// </summary>
public static class WardrobeIds
{
    private const ulong CustomFlag = 1ul << 48;
    private const ulong BonusFlag = 1ul << 49;
    private const ulong Sentinels = 1024;

    public static ulong Nothing(EquipSlot slot) => uint.MaxValue - 128 - (ulong)(byte)slot;

    public static ulong Smallclothes(EquipSlot slot) => uint.MaxValue - 256 - (ulong)(byte)slot;

    public static ulong Custom(ushort model, ushort weaponType, byte variant) =>
        model | ((ulong)weaponType << 16) | ((ulong)variant << 32) | CustomFlag;

    /// <summary>A row of the item sheet, as opposed to a sentinel or a
    /// packed model.</summary>
    public static bool IsSheetItem(ulong id) => id != 0 && id < uint.MaxValue - Sentinels;

    public static bool IsSmallclothes(ulong id)
    {
        if (id > uint.MaxValue)
            return false;
        ulong below = uint.MaxValue - id;
        return below >= 256 && below < 384;
    }

    public static bool IsNothing(ulong id) =>
        id == 0 || (id <= uint.MaxValue && id >= uint.MaxValue - Sentinels && !IsSmallclothes(id));

    public static bool IsCustom(ulong id) => (id & CustomFlag) != 0 && (id & BonusFlag) == 0;

    public static (ushort Model, ushort WeaponType, byte Variant) Split(ulong id) =>
        ((ushort)id, (ushort)(id >> 16), (byte)(id >> 32));

    /// <summary>
    /// Treats zero or a custom ID with model zero as empty facewear.
    /// </summary>
    public static bool IsNoFacewear(ulong id) => id == 0 || ((id & CustomFlag) != 0 && (ushort)id == 0);

    // Model IDs for invisible equipment options.
    public static readonly ulong EmperorsBody = Custom(279, 0, 1);
    public static readonly ulong EmperorsAccessory = Custom(53, 0, 1);
    public static readonly ulong Invisible = Custom(6121, 0, 12);
}
