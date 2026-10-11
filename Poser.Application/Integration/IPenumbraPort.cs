using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>Penumbra collections, temporary resources, and redraws.</summary>
public interface IPenumbraPort
{
    IntegrationAvailability Penumbra { get; }

    IntegrationValue<IReadOnlyList<ExternalItem>> GetCollections();

    IntegrationValue<CollectionAssignment> GetCollectionAssignment(ActorId actor);

    /// <summary>The local player's effective collection: what every plain
    /// spawn is assigned, so wearing it is inheriting, not a choice.</summary>
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

    /// <summary>Creates one temporary collection. The caller registers the
    /// returned id BEFORE assigning, so a failed assignment leaves a
    /// tracked, retryable collection rather than an anonymous leak.</summary>
    IntegrationValue<Guid> CreateTemporaryCollection(string name);

    /// <summary>Assigns the temporary collection to the exact actor WITHOUT
    /// force: an existing temporary assignment (another plugin's) makes
    /// this fail instead of being deleted.</summary>
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

    /// <summary>Requests a redraw and waits, bounded, for the exact actor to
    /// be drawable again, then refreshes scene bindings so downstream state
    /// reconciles against the redrawn body.</summary>
    Task<IntegrationResult> RedrawAndWait(
        ActorId actor, TimeSpan timeout, CancellationToken cancellation);
}
