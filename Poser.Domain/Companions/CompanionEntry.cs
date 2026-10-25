namespace Poser.Domain.Companions;

/// <summary>
/// The game-data category used to look up a companion. Empty attachment
/// slots are represented by null, not by a category in this enum.
/// </summary>
public enum CompanionKind
{
    Companion,
    Mount,
    Ornament,
}

/// <summary>
/// Information for displaying and attaching a companion. Id is its row
/// number within the game-data category given by Kind.
/// </summary>
public sealed record CompanionEntry(
    CompanionKind Kind,
    ushort Id,
    string Name,
    uint Icon = 0,
    uint ModelId = 0);
