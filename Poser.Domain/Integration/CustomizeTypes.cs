using System.Collections.Generic;

namespace Poser.Domain.Integration;

/// <summary>Glamourer's customize keys, named as its state JSON names
/// them. The game packs several of these into one byte (a flag in the
/// high bit); Glamourer unpacks them, so every key is a plain value.</summary>
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

/// <summary>An actor's customization as Glamourer reports it.</summary>
public sealed record CustomizeState(IReadOnlyDictionary<CustomizeKey, int> Values, int ModelId)
{
    public int Value(CustomizeKey key) => Values.TryGetValue(key, out var value) ? value : 0;
}

/// <summary>One choice a feature offers: the value the game stores and
/// the icon that shows it, zero when the choice has none.</summary>
public sealed record CustomizeOption(byte Value, uint Icon);

/// <summary>A feature the character-making sheet offers a clan and
/// gender: its game name and the choices it takes.</summary>
public sealed record CustomizeFeature(
    CustomizeKey Key, string Name, IReadOnlyList<CustomizeOption> Options, bool Icons);

/// <summary>Everything the character-making sheet says for one clan and
/// gender: the features, the seven facial-feature icons per face, and
/// the skin and hair palettes that are theirs alone.</summary>
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
