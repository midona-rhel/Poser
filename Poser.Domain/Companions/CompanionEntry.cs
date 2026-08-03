namespace Poser.Domain.Companions;

/// <summary>
/// Which sheet a companion row came from; also the catalog's kind filter.
/// Mirrors the native companion container kinds, minus None: a catalog row
/// is always something attachable, so "nothing attached" has no row.
/// </summary>
public enum CompanionKind
{
    Companion,
    Mount,
    Ornament,
}

/// <summary>
/// One catalog row: an attachable companion with the identity needed to
/// find it, display it, and spawn it. <see cref="Id"/> is the sheet row id
/// the native container takes, so a selected entry is directly attachable.
/// </summary>
public sealed record CompanionEntry(
    CompanionKind Kind,
    ushort Id,
    string Name,
    uint Icon = 0,
    uint ModelId = 0);
