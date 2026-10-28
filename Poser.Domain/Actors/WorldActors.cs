using System;
using System.Collections.Generic;

namespace Poser.Domain.Actors;

/// <summary>
/// Identifies a world actor listed for cloning. The runtime must check that
/// the same actor still exists before using this ID. Refreshing one list
/// should not invalidate the IDs displayed by another list.
/// </summary>
public readonly record struct WorldActorCandidateId(Guid Value)
{
    public static WorldActorCandidateId New() => new(Guid.NewGuid());
}

/// <summary>World-actor categories offered for cloning into GPose.</summary>
public enum WorldActorKind
{
    Player,
    BattleNpc,
    EventNpc,
    Companion,
    Retainer,
}

/// <summary>Information displayed for a world actor. Position is captured
/// when the list is refreshed; it is not a live position or a memory address.</summary>
public readonly record struct WorldActorCandidate(
    WorldActorCandidateId Id,
    string Name,
    WorldActorKind Kind,
    float DistanceFromPlayer,
    System.Numerics.Vector3 Position = default);

public enum WorldActorImportStatus
{
    Success,

    /// <summary>The listed actor could not be revalidated for cloning.</summary>
    StaleCandidate,

    /// <summary>The source was found, but creating the clone failed.</summary>
    SpawnFailed,

    /// <summary>Cloning is unavailable in the current game or thread state.</summary>
    Unavailable,
}

/// <summary>
/// Result of requesting a world-actor clone. Success reports creation, not
/// completion of later appearance loading or skeleton setup.
/// </summary>
public readonly record struct WorldActorImportResult(
    WorldActorImportStatus Status,
    string? Detail = null)
{
    public bool Success => Status == WorldActorImportStatus.Success;

    public static WorldActorImportResult Ok() =>
        new(WorldActorImportStatus.Success);

    public static WorldActorImportResult Stale(string detail) =>
        new(WorldActorImportStatus.StaleCandidate, detail);

    public static WorldActorImportResult Failed(string detail) =>
        new(WorldActorImportStatus.SpawnFailed, detail);

    public static WorldActorImportResult NotAvailable(string detail) =>
        new(WorldActorImportStatus.Unavailable, detail);
}
