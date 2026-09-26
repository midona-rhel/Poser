namespace Poser.Services;

/// <summary>
/// The two outline bytes this feature writes.
///
/// <para>Poser writes the game's outline byte and restores the value it read;
/// it does not assume that <see cref="None"/> is the object's resting state.
/// </para>
/// </summary>
public static class WorldObjectOutline
{
    /// <summary>No outline. Kept for the restore path's fallback only — the
    /// hover puts back the byte it captured.</summary>
    public const byte None = 0x03;

    /// <summary>What a hovered adoption handle paints its object.</summary>
    public const byte Hover = 0x43;
}
