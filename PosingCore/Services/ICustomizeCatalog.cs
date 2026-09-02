using System.Collections.Generic;
using Poser.Domain.Integration;

namespace Poser.Services;

/// <summary>The character-making data the Appearance view draws from:
/// the races and clans by name, the menu per clan and gender, the
/// shared colour palettes, read once from the game.</summary>
public interface ICustomizeCatalog
{
    IReadOnlyList<RaceEntry> Races { get; }
    IReadOnlyList<ClanEntry> Clans { get; }
    CustomizeMenu? Menu(byte clan, byte gender);
    CustomizePalettes Palettes { get; }
    /// <summary>The game texture that stands for the legacy tattoo.</summary>
    string LegacyTattooTexture { get; }
}
