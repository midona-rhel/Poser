using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// Narrow runtime boundary for the external appearance integrations —
/// Penumbra collections/temporary resources, Glamourer designs/state, and
/// Customize+ profiles — plus the outbound Open-in-Glamourer navigation.
///
/// The implementation owns every IPC subscriber and resolves the actor's
/// object index only at the call boundary; no index, address, or subscriber
/// ever crosses this interface. Synchronous members must run on the
/// framework thread and fail truthfully off it; the session marshals its
/// background transaction phases through <see cref="OnFrameworkThread"/>.
/// </summary>
public interface IIntegrationRuntimePort
{
    IntegrationAvailability Penumbra { get; }
    IntegrationAvailability Glamourer { get; }
    IntegrationAvailability CustomizePlus { get; }

    /// <summary>Runs one transaction phase on the framework thread. Executes
    /// inline when already there.</summary>
    Task<T> OnFrameworkThread<T>(Func<T> action);

    /// <summary>Whether the exact actor generation still resolves to a live
    /// native object. Distinguishes "restore natively" from "clean up
    /// Poser-created resources by their own ids only".</summary>
    bool IsResolvable(ActorId actor);

    /// <summary>The exact actor's character name, read while it still
    /// resolves. Captured by an import so a teardown that runs after the
    /// object is gone still has a Glamourer identity to address; see
    /// <see cref="ReleaseGlamourerStateByName"/>.</summary>
    IntegrationValue<string> GetActorName(ActorId actor);

    // ── Penumbra ─────────────────────────────────────────────────────────

    IntegrationValue<IReadOnlyList<ExternalItem>> GetCollections();

    IntegrationValue<CollectionAssignment> GetCollectionAssignment(ActorId actor);

    /// <summary>Creates or updates only this actor's individual assignment.</summary>
    IntegrationPortResult SetIndividualCollection(ActorId actor, Guid collection);

    /// <summary>Restores the captured assignment-vs-inheritance state: either
    /// the prior individual assignment, or deletion of Poser's assignment so
    /// inheritance resumes.</summary>
    IntegrationPortResult RestoreCollection(ActorId actor, CollectionBaseline baseline);

    /// <summary>Creates one temporary collection. The caller registers the
    /// returned id BEFORE assigning, so a failed assignment leaves a
    /// tracked, retryable collection rather than an anonymous leak.</summary>
    IntegrationValue<Guid> CreateTemporaryCollection(string name);

    /// <summary>Assigns the temporary collection to the exact actor WITHOUT
    /// force: an existing temporary assignment (another plugin's) makes
    /// this fail instead of being deleted.</summary>
    IntegrationPortResult AssignTemporaryCollection(Guid collection, ActorId actor);

    /// <summary>Adds Poser's temporary mod (embedded files, swaps, and meta
    /// manipulations) to the temporary collection under the owned tag.</summary>
    IntegrationPortResult AddTemporaryMods(
        Guid collection,
        IReadOnlyDictionary<string, string> paths,
        string manipulations);

    /// <summary>Deletes the temporary collection (and with it Poser's
    /// temporary mods and its assignment). Works by id after the actor is
    /// gone.</summary>
    IntegrationPortResult DeleteTemporaryCollection(Guid collection);

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
    IntegrationPortResult RequestRedraw(ActorId actor);

    /// <summary>Requests a redraw and waits, bounded, for the exact actor to
    /// be drawable again, then refreshes scene bindings so downstream state
    /// reconciles against the redrawn body.</summary>
    Task<IntegrationPortResult> RedrawAndWait(
        ActorId actor, TimeSpan timeout, CancellationToken cancellation);

    // ── Glamourer ────────────────────────────────────────────────────────

    IntegrationValue<IReadOnlyList<ExternalItem>> GetDesigns();

    /// <summary>Complete serialized actor state with the caller's normal
    /// key. A state locked by another plugin fails here, before any
    /// mutation.</summary>
    IntegrationValue<string> CaptureGlamourerState(ActorId actor);

    /// <summary>Applies a design with the API's documented default design
    /// flags and no persistent lock.</summary>
    IntegrationPortResult ApplyDesign(ActorId actor, Guid design);

    /// <summary>MCDF application mode: applies a serialized state as a
    /// FIXED state locked with Poser's own key, so the imported look
    /// survives automation until <see cref="UnlockGlamourerState"/>.</summary>
    IntegrationPortResult HoldGlamourerState(ActorId actor, string state);

    /// <summary>Baseline/design restoration mode: applies a serialized
    /// state with the API's one-shot manual flags and NO persistent lock,
    /// leaving no Poser fixed state behind.</summary>
    IntegrationPortResult RestoreGlamourerState(ActorId actor, string state);

    /// <summary>Releases Poser's own lock only. Never touches another
    /// plugin's lock.</summary>
    IntegrationPortResult UnlockGlamourerState(ActorId actor);

    /// <summary>
    /// Releases a locked MCDF state from a character that no longer
    /// resolves — the GPose clone is destroyed on the exit edge, but
    /// Glamourer's state belongs to the character's identity and outlives
    /// it. Addresses Glamourer BY NAME with Poser's own key: releases the
    /// lock and reverts the imported equipment/customization, so nothing
    /// Poser applied survives the exit. Never touches another plugin's
    /// lock — a foreign key refuses. A character that is not present at
    /// all is a success: there is no longer anything to release.
    /// </summary>
    IntegrationPortResult ReleaseGlamourerStateByName(string name);

    /// <summary>Outbound navigation: opens Glamourer's window on the actor.</summary>
    IntegrationPortResult OpenGlamourer(ActorId actor);

    // ── Customize+ ───────────────────────────────────────────────────────

    /// <summary>Saved (normal) profiles only.</summary>
    IntegrationValue<IReadOnlyList<ExternalItem>> GetBodyProfiles();

    /// <summary>The actor's active profile id and whether it is a readable
    /// saved profile. An active id absent from the saved list is a
    /// temporary profile the API cannot read back.</summary>
    IntegrationValue<BodyProfileProbe> ProbeBodyProfile(ActorId actor);

    IntegrationValue<string> GetBodyProfileJson(Guid profile);

    IntegrationValue<Guid> ApplyTemporaryBodyProfile(ActorId actor, string profileJson);

    /// <summary>Deletes Poser's temporary profile by its OWN id — the one
    /// ownership-safe cleanup primitive. There is deliberately no
    /// delete-by-actor: that would remove whichever temporary profile is
    /// active, including another plugin's.</summary>
    IntegrationPortResult DeleteTemporaryBodyProfileById(Guid profile);
}
