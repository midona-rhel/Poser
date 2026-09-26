using System.Collections.Generic;

namespace Poser.Domain.Scene;

/// <summary>One engine festival slot: id 0 means the slot is empty.</summary>
public readonly record struct ActiveFestival(uint Id, ushort Phase);

/// <summary>A phase the reference data names for a festival.</summary>
public sealed record FestivalPhaseInfo(int Id, string Name);

/// <summary>
/// A festival row joined with the curated reference data. A row the data does
/// not name is still offered, flagged <see cref="Unknown"/>; a row known to
/// break the game in some places is flagged <see cref="Unsafe"/>.
/// </summary>
public sealed record FestivalEntry(
    uint Id,
    string Name,
    bool Unknown,
    bool Unsafe,
    IReadOnlyList<FestivalPhaseInfo> KnownPhases);
