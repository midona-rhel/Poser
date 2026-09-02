namespace Poser.Domain.Integration;

/// <summary>An equipment slot as Glamourer names them; the values are the
/// Glamourer API's own, so a slot crosses the IPC as its number.</summary>
public enum EquipSlot : byte
{
    MainHand = 1,
    OffHand = 2,
    Head = 3,
    Body = 4,
    Hands = 5,
    Legs = 6,
    Feet = 7,
    Ears = 8,
    Neck = 9,
    Wrists = 10,
    RightFinger = 11,
    LeftFinger = 12,
}

/// <summary>The meta switches Glamourer holds per actor; the values are
/// its flag bits.</summary>
public enum MetaSwitch : byte
{
    Wetness = 0x01,
    HatVisible = 0x02,
    VisorToggled = 0x04,
    WeaponVisible = 0x08,
}

/// <summary>One item a slot can wear: its game id, name, icon and how many
/// dyes it takes.</summary>
public sealed record WardrobeItem(uint Id, string Name, uint Icon, byte DyeCount);

/// <summary>One dye: its id, name and colour as packed 0xRRGGBB.</summary>
public sealed record DyeEntry(byte Id, string Name, uint Color);

/// <summary>One facewear: its bonus item id, name and icon.</summary>
public sealed record FacewearEntry(uint Id, string Name, uint Icon);
