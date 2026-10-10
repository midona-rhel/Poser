using Poser.Core;
using Poser.Domain.Companions;
using Poser.Entities;
using Poser.Domain.Identity;

namespace Poser.Game;

internal sealed class SpawnOwnershipRecord
{
    public SpawnOwnershipRecord(
        Guid token,
        ushort createdIndex,
        SpawnNativeDescriptor? descriptor,
        CompanionKind? kind,
        bool hasCompanionSlot,
        ulong createIndexStamp = 0)
    {
        Token = token;
        CreatedIndex = createdIndex;
        Descriptor = descriptor;
        Kind = kind;
        HasCompanionSlot = hasCompanionSlot;
        CreateIndexStamp = createIndexStamp;
    }

    public Guid Token { get; }
    public ushort CreatedIndex { get; }
    public SpawnNativeDescriptor? Descriptor { get; private set; }
    public IActor? Actor { get; private set; }

    /// <summary>Wrapper logical identity captured at bind; later exact
    /// lookups require both the same instance and the same id.</summary>
    public EntityId? BoundId { get; private set; }
    /// <summary>The catalog kind this record was spawned as, or null for a
    /// plain spawn or clone — those are actors, not catalog entries.</summary>
    public CompanionKind? Kind { get; }
    public bool HasCompanionSlot { get; }

    /// <summary>Per-index destruction stamp at create time. Unchanged means
    /// no object at the created index has been destroyed since our create,
    /// i.e. the current occupant is the object our create call made.</summary>
    public ulong CreateIndexStamp { get; }
    public bool Visible { get; private set; } = true;
    public SpawnOwnershipState State { get; private set; } = SpawnOwnershipState.PendingCreate;

    /// <summary>Whether the spawn assigned this clone a Penumbra collection.
    /// It is ownership, not appearance state: only a record that took the
    /// assignment is allowed to release one, so a foreign assignment on a
    /// reused identifier is never deleted on our behalf.</summary>
    public bool CollectionAssigned { get; private set; }

    /// <summary>Adopts an identity resolved for <see cref="CreatedIndex"/>.
    /// The slot check is an invariant assertion, not a policy: the adapter
    /// resolves BY that slot, so a differing slot means the descriptor was
    /// built in the wrong index space and nothing about it can be trusted.
    /// Returns false instead of throwing so the per-frame recovery tick can
    /// treat it as "outcome still unknown" without faulting every frame.</summary>
    public bool TryResolve(SpawnNativeDescriptor descriptor)
    {
        if (descriptor.Index != CreatedIndex)
            return false;
        Descriptor = descriptor;
        State = SpawnOwnershipState.Live;
        return true;
    }

    public void Resolve(SpawnNativeDescriptor descriptor)
    {
        if (!TryResolve(descriptor))
            throw new InvalidOperationException("Spawned object index changed");
    }

    public void Bind(IActor actor)
    {
        Actor = actor;
        BoundId = actor.Id;
    }

    public void MarkCollectionAssigned() => CollectionAssigned = true;
    public void MarkCollectionReleased() => CollectionAssigned = false;

    public void MarkPending() => State = SpawnOwnershipState.PendingDelete;
    public void MarkNonRecoverable() => State = SpawnOwnershipState.NonRecoverable;
    public void SetVisibility(bool visible) => Visible = visible;
}
