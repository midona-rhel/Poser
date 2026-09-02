using Poser.Domain.Actors;

namespace Poser.Application.Actors;

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
