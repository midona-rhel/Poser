using System.Collections.Generic;

namespace Poser.Domain.Integration;

/// <summary>
/// Appearance field names used in Glamourer state JSON.
/// Flags are separate fields, but their values retain the game's bit masks.
/// </summary>
public enum CustomizeKey
{
    Race,
    Gender,
    BodyType,
    Height,
    Clan,
    Face,
    Hairstyle,
    Highlights,
    SkinColor,
    EyeColorRight,
    HairColor,
    HighlightsColor,
    FacialFeature1,
    FacialFeature2,
    FacialFeature3,
    FacialFeature4,
    FacialFeature5,
    FacialFeature6,
    FacialFeature7,
    LegacyTattoo,
    TattooColor,
    Eyebrows,
    EyeColorLeft,
    EyeShape,
    SmallIris,
    Nose,
    Jaw,
    Mouth,
    Lipstick,
    LipColor,
    MuscleMass,
    TailShape,
    BustSize,
    FacePaint,
    FacePaintReversed,
    FacePaintColor,
    Wetness,
}

public static class CustomizeEncoding
{
    // Glamourer's split fields retain their native bit, not a boolean 1.
    public static int FlagValue(CustomizeKey key) => key switch
    {
        >= CustomizeKey.FacialFeature1 and <= CustomizeKey.LegacyTattoo
            => 1 << (key - CustomizeKey.FacialFeature1),
        CustomizeKey.Highlights or CustomizeKey.SmallIris or CustomizeKey.Lipstick
            or CustomizeKey.FacePaintReversed => 0x80,
        _ => throw new System.ArgumentOutOfRangeException(nameof(key)),
    };
}

/// <summary>An actor's customization as Glamourer reports it.</summary>
public sealed record CustomizeState(IReadOnlyDictionary<CustomizeKey, int> Values, int ModelId)
{
    public int Value(CustomizeKey key) => Values.TryGetValue(key, out var value) ? value : 0;
}

/// <summary>
/// A stored appearance value and its icon ID; zero means no icon.
/// </summary>
public sealed record CustomizeOption(byte Value, uint Icon);

/// <summary>
/// An appearance option offered for a clan and gender, with its allowed choices.
/// </summary>
public sealed record CustomizeFeature(
    CustomizeKey Key, string Name, IReadOnlyList<CustomizeOption> Options, bool Icons);

/// <summary>
/// Character-creation options and color palettes for one clan and gender.
/// </summary>
public sealed record CustomizeMenu(
    byte Clan,
    byte Gender,
    IReadOnlyDictionary<CustomizeKey, CustomizeFeature> Features,
    IReadOnlyDictionary<byte, uint[]> FaceFeatureIcons,
    uint[] SkinColors,
    uint[] HairColors)
{
    public CustomizeFeature? Feature(CustomizeKey key) =>
        Features.TryGetValue(key, out var feature) ? feature : null;
}

/// <summary>The palettes every clan shares, as the game's UI shows them.
/// Colours are packed ABGR, as ImGui takes them.</summary>
public sealed record CustomizePalettes(
    uint[] Eyes, uint[] Highlights, uint[] Lips, uint[] Tattoo, uint[] FacePaint);

public sealed record RaceEntry(byte Race, string Name);

public sealed record ClanEntry(byte Clan, byte Race, string Name);
