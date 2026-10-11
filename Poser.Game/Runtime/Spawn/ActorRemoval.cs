using Dalamud.Plugin.Services;
using Poser.Domain.Actors;
using Poser.Domain.Identity;
using Poser.Game.Core;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game;

/// <summary>
/// Taking one actor out of the scene: a Poser-owned actor through its exact
/// ownership record, an adopted world body back to the world, anything else
/// in the GPose table through the scene-table delete. Callers have already
/// proven the framework thread.
/// </summary>
internal sealed class ActorRemoval
{
    private readonly IActorManager _actorManager;
    private readonly IActorSpawnNativeAdapter _native;
    private readonly SpawnOwnershipLedger _ownership;
    private readonly SpawnOwnershipCleanup _cleanup;
    private readonly Func<nint, EntityId?> _expectedWrapperIdentity;

    /// <summary>Address of one object-table slot, re-read on demand. Held as a
    /// function because the service is constructed in tests without a live
    /// table.</summary>
    private readonly Func<int, nint> _objectAddressAt;
    private readonly IPluginLog? _log;

    public ActorRemoval(
        IActorManager actorManager,
        IActorSpawnNativeAdapter native,
        SpawnOwnershipLedger ownership,
        SpawnOwnershipCleanup cleanup,
        Func<nint, EntityId?> expectedWrapperIdentity,
        Func<int, nint> objectAddressAt,
        IPluginLog? log)
    {
        _actorManager = actorManager;
        _native = native;
        _ownership = ownership;
        _cleanup = cleanup;
        _expectedWrapperIdentity = expectedWrapperIdentity;
        _objectAddressAt = objectAddressAt;
        _log = log;
    }

    public bool DestroyActor(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;
        // An adopted body is the world's: Destroy seats it back where it
        // was taken and lets it go.
        if (_actorManager.IsAdopted(actor))
        {
            _actorManager.ReleaseWorldActor(actor.Address);
            return true;
        }

        try
        {
            if (!_ownership.TryGetBound(actor, out var ownership))
                return false;

            var current = _native.ResolveActor(actor.Address);
            if (current is not null
                && (ownership.Descriptor is not { } expected
                    || expected != current.Value
                    || expected.Address != actor.Address))
            {
                _log?.Warning("ActorSpawnService: Cannot destroy actor - identity mismatch");
                return false;
            }

            if (!_cleanup.TryDelete(ownership))
                return false;

            _log?.Debug($"ActorSpawnService: Destroyed actor at index {ownership.CreatedIndex}");
            _actorManager.RefreshActors();
            return true;
        }
        catch (Exception ex)
        {
            _log?.Error($"ActorSpawnService: Failed to destroy actor: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Removes exactly one actor from the temporary GPose object table.
    ///
    /// <para>An actor Poser owns keeps the stronger create-time identity and
    /// collection-release contract — it routes to <see cref="DestroyActor"/>
    /// rather than being downgraded to a scene scan because the caller used
    /// the general verb.</para>
    ///
    /// <para>THE GATE, and where it lives. Brio range-checks the OBJECT TABLE
    /// index at the enumeration site and never range-checks the
    /// ClientObjectManager slot it ultimately deletes by
    /// (<c>com-&gt;GetIndexByObject</c> → <c>DeleteObjectByIndex</c>, guarded
    /// only against the 0xFFFFFFFF sentinel). Those are two DIFFERENT index
    /// spaces, so a 201-439 test on the manager slot would be checking the
    /// wrong number and refusing valid deletes. This states the real gate in
    /// the real space: the actor must still be standing in the GPose table
    /// range right now. It does not lean on the fact that
    /// <c>ActorManager</c> happens to scan the same range — a gate inherited
    /// by assumption widens silently the day that scan changes.</para>
    ///
    /// <para>Refuses the local/GPose primary, companion bodies, stale or
    /// non-root wrappers, and anything whose typed descriptor no longer
    /// resolves.</para>
    /// </summary>
    public bool RemoveActorFromScene(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            // An adopted body is released: seated back where it was taken
            // and forgotten by the scene; the world keeps it.
            if (_actorManager.IsAdopted(actor))
            {
                _actorManager.ReleaseWorldActor(actor.Address);
                return true;
            }
            if (_ownership.TryGetBound(actor, out _))
                return DestroyActor(actor);

            if (RemovalRefusal(actor) is { } refusal)
            {
                _log?.Warning($"ActorSpawnService: {refusal}");
                return false;
            }

            var descriptor = _native.ResolveActor(actor.Address);
            if (descriptor is not { } current)
            {
                _log?.Warning(
                    "ActorSpawnService: Refused to remove actor without a current typed scene descriptor");
                return false;
            }

            // DeleteExact re-reads the typed descriptor immediately before
            // invoking ClientObjectManager.DeleteObjectByIndex(index, 0).
            if (!_native.DeleteExact(current))
                return false;

            _log?.Debug(
                $"ActorSpawnService: Removed GPose actor at index {current.Index}");
            _actorManager.RefreshActors();
            return true;
        }
        catch (Exception ex)
        {
            _log?.Error(
                $"ActorSpawnService: Failed to remove actor from scene: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The removal gates, read-only, each with the reason a user can act on.
    /// Null means removal would be admitted right now. The UI uses this to
    /// decide whether to offer the verb at all; the mutation re-runs it, so
    /// the answer can never be stale by more than the click.
    /// </summary>
    public string? RemovalRefusal(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return "The actor is no longer in the scene.";
        // An adopted body is released, not destroyed: Destroy is offered.
        if (_actorManager.IsAdopted(actor))
            return null;

        // A Poser-owned actor is always removable: it routes to the
        // stronger owned-teardown path, not the scene-table delete.
        if (_ownership.TryGetBound(actor, out _))
            return null;

        // No local-player exception, deliberately: Brio's CanDestroy admits
        // every actor in its container including your own GPose clone
        // (Brio/Capabilities/Actor/ActorLifetimeCapability.cs:88), and its
        // ClearAll deletes the whole GPose table. The clone is a temporary
        // copy; the overworld character is untouched by deleting it.
        if (actor.ActorKind is ActorKind.Companion or ActorKind.Mount
            or ActorKind.Ornament)
            return "Companions are removed by detaching them from their " +
                "owner, not from the scene.";

        // The wrapper check is the root/ownership boundary: it excludes
        // auxiliary registrations and an old wrapper after a refresh even
        // when a native address happens to be reused. A caller cannot
        // manufacture a wrapper with a copied address and turn that stale
        // view into permission to delete a scene slot.
        EntityId? expectedIdentity;
        try
        {
            expectedIdentity = _expectedWrapperIdentity(actor.Address);
        }
        catch
        {
            expectedIdentity = null;
        }
        if (expectedIdentity is not { } expected
            || expected != actor.Id
            || !_actorManager.Actors.Any(candidate =>
                ReferenceEquals(candidate, actor)
                && candidate.Id == expected
                && candidate.Address == actor.Address))
            return "The actor's identity is stale; it may have just been " +
                "replaced. Try again.";

        // Brio's gate, in Brio's index space, re-read now.
        if (!InGPoseTable(actor.Address))
            return "The actor is not part of the GPose scene.";

        return null;
    }

    /// <summary>Whether this address is currently a GPose-table object.
    /// Re-read at the write, never cached. Brio gates its own scene
    /// destruction on exactly this range and nothing else — <c>DestroyAll</c>
    /// walks <c>_objectTable[GPoseStart..GPoseEnd]</c>
    /// (<c>Brio/Game/Actor/ActorSpawnService.cs:175-183</c>,
    /// <c>ActorTableHelpers.cs:5-8</c>). Slot 200, the game's UI copy, is
    /// deliberately outside it, which is what makes "clear the scene" safe
    /// to mean everything in it.</summary>
    private bool InGPoseTable(nint address)
    {
        for (int index = GPoseObjectTable.FirstActorIndex; index <= GPoseObjectTable.LastActorIndex; index++)
        {
            if (_objectAddressAt(index) == address)
                return true;
        }
        return false;
    }
}
