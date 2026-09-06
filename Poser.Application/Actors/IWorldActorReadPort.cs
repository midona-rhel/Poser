using Poser.Domain.Actors;

namespace Poser.Application.Actors;

/// <summary>
/// Discovery and import of visible overworld actors. Listing is read-only:
/// <see cref="CloneCandidate"/> retains its legacy name but adopts the exact
/// world body into GPose by reference. Release returns it to the world.
/// A <see cref="RefreshCandidates"/> pass keeps the ids of the objects it
/// still offers and drops the rest; history retains its own observation.
/// </summary>
public interface IWorldActorReadPort
{
    /// <summary>Enumerates the currently visible overworld actors, nearest
    /// first. Each keeps the id its exact identity was last given; anything
    /// that has gone loses its id. Empty outside GPose.</summary>
    IReadOnlyList<WorldActorCandidate> RefreshCandidates();

    /// <summary>Adopts the exact candidate after revalidating its native
    /// identity. Any identity drift is a typed stale refusal.</summary>
    WorldActorImportResult CloneCandidate(WorldActorCandidateId id);
}
