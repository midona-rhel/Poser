using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>Penumbra collections, temporary resources, and redraws.</summary>
public interface IPenumbraPort
{
    IntegrationAvailability Penumbra { get; }

    IntegrationValue<IReadOnlyList<ExternalItem>> GetCollections();

    IntegrationValue<CollectionAssignment> GetCollectionAssignment(ActorId actor);

    /// <summary>
    /// The local player's effective collection, used as the initial collection for plain spawns.
    /// </summary>
    IntegrationValue<Guid> GetPlayerCollection();

    /// <summary>Captures only a currently effective collection owned by the duplicate lifecycle.</summary>
    IntegrationValue<SpawnCollectionSnapshot?> CaptureInheritedCollection(ActorId actor);
    IntegrationResult RestoreInheritedCollection(ActorId actor, SpawnCollectionSnapshot snapshot);

    /// <summary>Creates or updates only this actor's individual assignment.</summary>
    IntegrationResult SetIndividualCollection(ActorId actor, Guid collection);

    /// <summary>Restores the captured assignment-vs-inheritance state: either
    /// the prior individual assignment, or deletion of Poser's assignment so
    /// inheritance resumes.</summary>
    IntegrationResult RestoreCollection(ActorId actor, CollectionBaseline baseline);

    /// <summary>
    /// Creates a temporary collection. Record its returned ID before assignment
    /// so it can still be cleaned up if assignment fails.
    /// </summary>
    IntegrationValue<Guid> CreateTemporaryCollection(string name);

    /// <summary>
    /// Assigns this temporary collection without replacing an existing temporary assignment.
    /// </summary>
    IntegrationResult AssignTemporaryCollection(Guid collection, ActorId actor);

    /// <summary>Adds Poser's temporary mod (embedded files, swaps, and meta
    /// manipulations) to the temporary collection under the owned tag.</summary>
    IntegrationResult AddTemporaryMods(
        Guid collection,
        IReadOnlyDictionary<string, string> paths,
        string manipulations);

    /// <summary>Deletes the temporary collection (and with it Poser's
    /// temporary mods and its assignment). Works by id after the actor is
    /// gone.</summary>
    IntegrationResult DeleteTemporaryCollection(Guid collection);

    /// <summary>Actor-specific meta manipulations, not the global/current
    /// UI collection's.</summary>
    IntegrationValue<string> GetActorMetaManipulations(ActorId actor);

    /// <summary>Current resource replacements for the actor: resolved actual
    /// path (local file or swap source game path) to the game paths it
    /// serves.</summary>
    IntegrationValue<IReadOnlyDictionary<string, IReadOnlyList<string>>>
        GetActorResourcePaths(ActorId actor);

    IntegrationValue<string> GetModDirectory();

    /// <summary>Fire-and-forget redraw request for teardown paths that must
    /// not wait.</summary>
    IntegrationResult RequestRedraw(ActorId actor);

    /// <summary>
    /// Requests a redraw, waits up to timeout for the same actor to become drawable,
    /// and refreshes scene bindings to the new body.
    /// </summary>
    Task<IntegrationResult> RedrawAndWait(
        ActorId actor, TimeSpan timeout, CancellationToken cancellation);
}
