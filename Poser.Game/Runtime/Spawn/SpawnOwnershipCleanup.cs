using Dalamud.Plugin.Services;
using Poser.Game.Integration;

namespace Poser.Game;

/// <summary>
/// Every terminal outcome of an ownership record: exact delete (with the
/// clone's collection release), promotion of a create whose first resolve
/// failed, and the session-end sweep. Nothing here ever deletes without the
/// exact descriptor proving the object is still ours.
/// </summary>
internal sealed class SpawnOwnershipCleanup
{
    private readonly SpawnOwnershipLedger _ledger;
    private readonly IActorSpawnNativeAdapter _native;
    private readonly ISpawnCollectionPort? _collections;
    private readonly IPluginLog? _log;

    public SpawnOwnershipCleanup(
        SpawnOwnershipLedger ledger,
        IActorSpawnNativeAdapter native,
        ISpawnCollectionPort? collections,
        IPluginLog? log)
    {
        _ledger = ledger;
        _native = native;
        _collections = collections;
        _log = log;
    }

    public bool TryDelete(SpawnOwnershipRecord ownership)
    {
        var result = TryDeleteExact(ownership);
        if (!result)
            _log?.Warning($"ActorSpawnService: Exact delete pending at index {ownership.CreatedIndex}");
        return result;
    }

    private bool TryDeleteExact(SpawnOwnershipRecord ownership)
    {
        try
        {
            if (ownership.State == SpawnOwnershipState.NonRecoverable)
                return false;
            if (ownership.Descriptor is null)
                return false;
            _ledger.MarkPending(ownership.Token);
            if (!_native.IsAvailable)
                return false;

            var current = _native.ResolveByIndex(ownership.Descriptor.Value.Index);
            if (current is null)
            {
                // The assignment died with the object's identifier; a
                // duplicate's temporary collection did not, and goes by GUID.
                if (ownership.CollectionAssigned && _collections is not null
                    && _collections.DiscardCollection(ownership.Descriptor.Value.Address) is { Success: false } discarded)
                    _log?.Warning(
                        $"ActorSpawnService: the clone at index {ownership.CreatedIndex} was already gone and its Penumbra collection could not be deleted: {discarded.Detail}");
                return _ledger.TryRetire(ownership);
            }
            if (current.Value != ownership.Descriptor.Value)
                return false;
            // Released against the PROVEN identity and on the last frame it
            // still exists: Penumbra keys the assignment on the object's own
            // identifier, so after the delete there is nothing left to name.
            ReleaseCollection(ownership);
            Diagnostics.GPoseTransitionLog.Actor(_log, "actor-delete-before", current.Value.Address, $"index={current.Value.Index}");
            if (!_native.DeleteExact(ownership.Descriptor.Value))
                return false;
            _log?.Information($"[GPoseLifetime] actor-delete-complete index={current.Value.Index} actor=0x{current.Value.Address:X}");
            return _ledger.TryRetire(ownership);
        }
        catch
        {
            return false;
        }
    }

    private void ReleaseCollection(SpawnOwnershipRecord ownership)
    {
        if (!ownership.CollectionAssigned || _collections is null)
            return;
        try
        {
            var released = _collections.ReleaseCollection(ownership.Descriptor!.Value.Address);
            if (released.Success)
                ownership.MarkCollectionReleased();
            else
                _log?.Warning(
                    $"ActorSpawnService: the clone's Penumbra collection assignment was not released: {released.Detail}");
        }
        catch (Exception ex)
        {
            // A failing external call never blocks the delete: the object
            // has to go either way, and the leftover assignment is named
            // after the clone, not after anything the user owns.
            _log?.Warning(
                $"ActorSpawnService: releasing the clone's Penumbra collection assignment failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Finishes a create whose first resolve failed. Terminal outcomes:
    /// promotion to exact PendingDelete plus a delete attempt once the
    /// per-index destruction stamp proves the occupant is ours, or retirement
    /// once that stamp shows our created object was destroyed. Returns false
    /// while the outcome is still unknown (caller retries).
    /// </summary>
    public bool TryFinishPendingCreate(SpawnOwnershipRecord ownership)
    {
        if (ownership.State != SpawnOwnershipState.PendingCreate
            || ownership.CreatedIndex == ushort.MaxValue)
            return false;

        if (_native.IndexDestructionStamp(ownership.CreatedIndex) != ownership.CreateIndexStamp)
        {
            // The finalize hook observed a destruction at our created index
            // since create: our object is gone and any occupant is foreign.
            _ledger.RetirePendingCreate(ownership.Token);
            _log?.Debug(
                $"ActorSpawnService: created index {ownership.CreatedIndex} was destroyed externally; record retired");
            return true;
        }

        SpawnNativeDescriptor? current;
        try
        {
            current = _native.ResolveByIndex(ownership.CreatedIndex);
        }
        catch
        {
            // A resolution fault leaves the outcome unknown: retain the
            // record for the next retry; bulk cleanup must not throw.
            return false;
        }
        if (current is null)
            return false;

        // Unchanged destruction stamp: the occupant is the object our create
        // call made, so its identity is now authoritative. The spawn already
        // failed, so the record promotes straight to exact pending deletion.
        if (!ownership.TryResolve(current.Value))
            return false;
        ownership.MarkPending();
        TryDelete(ownership);
        return true;
    }

    /// <summary>
    /// Proof that a readout record is about nothing that still exists: the
    /// finalize hook recorded a destruction at the created slot since create,
    /// or the slot resolves empty while the manager is available. Absence of
    /// proof — no index was ever known, the manager is unavailable, the
    /// resolve faults, or the slot is still occupied — is never vacancy.
    /// </summary>
    private bool IsCreatedIndexProvablyVacated(SpawnOwnershipRecord ownership)
    {
        if (ownership.CreatedIndex == ushort.MaxValue)
            return false;
        try
        {
            if (!_native.IsAvailable)
                return false;
            if (_native.IndexDestructionStamp(ownership.CreatedIndex)
                != ownership.CreateIndexStamp)
                return true;
            return _native.ResolveByIndex(ownership.CreatedIndex) is null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Settles every record at session end. Returns whether any
    /// native object was deleted, so the caller knows the actor list owes a
    /// refresh.</summary>
    public bool DeleteAll()
    {
        var deleted = false;
        foreach (var ownership in _ledger.Snapshot)
        {
            if (ownership.State == SpawnOwnershipState.NonRecoverable)
            {
                if (IsCreatedIndexProvablyVacated(ownership)
                    && _ledger.RetireNonRecoverable(ownership.Token))
                    _log?.Debug(
                        $"ActorSpawnService: non-recoverable readout cleared - created index {ownership.CreatedIndex} is vacated");
                else
                    _log?.Debug(
                        $"ActorSpawnService: non-recoverable record retained for readout (index {ownership.CreatedIndex})");
                continue;
            }
            if (ownership.State == SpawnOwnershipState.PendingCreate)
            {
                if (TryFinishPendingCreate(ownership))
                    deleted = true;
                else
                    _log?.Warning(
                        $"ActorSpawnService: Retaining pending-create record at index {ownership.CreatedIndex}");
                continue;
            }
            if (TryDelete(ownership))
                deleted = true;
            else
                _log?.Warning($"ActorSpawnService: Retaining pending actor at index {ownership.CreatedIndex}");
        }
        return deleted;
    }
}
