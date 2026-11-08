using System.Collections.Generic;

namespace Poser.Domain.Scene;

/// <summary>One engine festival slot: id 0 means the slot is empty.</summary>
public readonly record struct ActiveFestival(uint Id, ushort Phase);

/// <summary>A phase the reference data names for a festival.</summary>
public sealed record FestivalPhaseInfo(int Id, string Name);

/// <summary>
/// Festival metadata for the picker. Unknown means no curated name is
/// available; Unsafe marks entries flagged by the source data. This record
/// does not establish that an entry is safe on the current map.
/// </summary>
public sealed record FestivalEntry(
    uint Id,
    string Name,
    bool Unknown,
    bool Unsafe,
    IReadOnlyList<FestivalPhaseInfo> KnownPhases);
