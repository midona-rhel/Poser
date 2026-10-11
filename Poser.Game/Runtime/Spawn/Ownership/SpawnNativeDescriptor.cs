namespace Poser.Game;

/// <summary>
/// Exact native identity of one client-object slot occupant.
/// <see cref="Index"/> is the ClientObjectManager slot, never
/// <c>GameObject.ObjectIndex</c> — see
/// <c>docs/architecture/posing-runtime.md</c> for the index-space rule.
/// <see cref="LifetimeStamp"/> is the destruction-sequence stamp for
/// <see cref="Address"/> at resolve time. It advances inside the native
/// Character finalize hook — the game's own lifetime transition — never at
/// adapter observation, so an external delete-and-reuse with an identical
/// index/address/EntityId still compares unequal and fails closed.
/// </summary>
internal readonly record struct SpawnNativeDescriptor(
    ushort Index,
    nint Address,
    ulong EntityId,
    ulong LifetimeStamp = 0);

internal enum SpawnOwnershipState
{
    PendingCreate,
    Live,
    PendingDelete,

    /// <summary>
    /// Create faulted without yielding a usable identity. The record is
    /// retained as an explicit readout (snapshot + one Error log) and is
    /// never allowed to touch native state.
    /// </summary>
    NonRecoverable,
}
