namespace Poser.Application.Actors;

/// <summary>
/// Opaque identity of one discovered overworld actor. It carries no pointer,
/// index, or native id — the Game side keeps the exact observation it stands
/// for, and an id is only ever re-issued for that SAME exact identity
/// (address, object-table index, GameObjectId). A different occupant is a
/// different id, so an id can never resolve to "whatever occupies that slot
/// now"; an id whose object is gone is a typed stale refusal.
///
/// Ids outlive a refresh on purpose: two surfaces list candidates at their own
/// cadences (the overlay's adoption handles),
/// and per-pass minting would have each one's refresh silently invalidate the
/// other's rows.
/// </summary>
public readonly record struct WorldActorCandidateId(Guid Value)
{
    public static WorldActorCandidateId New() => new(Guid.NewGuid());
}

/// <summary>The kinds of overworld character Poser offers for cloning —
/// exactly the set the GPose admission scan accepts, so a clone's source kind
/// never names something the scene itself could not have admitted.</summary>
public enum WorldActorKind
{
    Player,
    BattleNpc,
    EventNpc,
    Companion,
    Retainer,
}

/// <summary>One visible overworld actor, as a pointer-free snapshot for
/// Application/UI. <paramref name="Position"/> is where the actor stood when
/// it was listed — the world point an adoption handle projects from.</summary>
public readonly record struct WorldActorCandidate(
    WorldActorCandidateId Id,
    string Name,
    WorldActorKind Kind,
    float DistanceFromPlayer,
    System.Numerics.Vector3 Position = default);

public enum WorldActorImportStatus
{
    Success,

    /// <summary>The candidate no longer names the exact object that was
    /// listed — despawned, replaced, moved, hidden, or from an older
    /// enumeration pass. Nothing native was touched.</summary>
    StaleCandidate,

    /// <summary>The source was proven current but the spawn transaction
    /// itself failed; the spawn service's own rollback applies.</summary>
    SpawnFailed,

    /// <summary>The operation cannot run at all right now (not in GPose,
    /// off the game's update thread, spawning unavailable).</summary>
    Unavailable,
}

/// <summary>
/// Typed outcome of a world-actor import. The clone is synchronous: on
/// <see cref="WorldActorImportStatus.Success"/> the clone exists, owned by
/// Poser's spawn transaction, and enters the scene through the ordinary
/// registry scan like every other spawn — there is no pending receipt.
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

/// <summary>
/// Discovery and import of visible overworld actors. Listing is read-only:
/// the only operation that ever crosses from a candidate to native effect is
/// <see cref="CloneCandidate"/>, which clones the exact source into a
/// Poser-owned GPose actor — the source is never adopted, mutated, or
/// deleted. A <see cref="RefreshCandidates"/> pass keeps the ids of the
/// objects it still sees and drops the rest.
/// </summary>
public interface IWorldActorReadPort
{
    /// <summary>Enumerates the currently visible overworld actors, nearest
    /// first. Each keeps the id its exact identity was last given; anything
    /// that has gone loses its id. Empty outside GPose.</summary>
    IReadOnlyList<WorldActorCandidate> RefreshCandidates();

    /// <summary>Clones the candidate's exact source into a Poser-owned GPose
    /// actor, revalidating the source's full identity immediately before the
    /// spawn. Any identity drift is a typed stale refusal.</summary>
    WorldActorImportResult CloneCandidate(WorldActorCandidateId id);
}
