namespace Poser.Game;

/// <summary>
/// Native identity of one client-object slot occupant.
/// <see cref="Index"/> is the ClientObjectManager slot, never
/// <c>GameObject.ObjectIndex</c> — see
/// <c>docs/architecture/posing-runtime.md</c> for the index-space rule.
/// <see cref="LifetimeStamp"/> is the destruction-sequence stamp for
/// <see cref="Address"/> at resolve time. It advances inside the native
/// Character finalize hook — the game's own lifetime transition — never at
/// adapter observation. Deleting and reusing the same index, address, and
/// entity id therefore still produces a different descriptor.
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
    /// Creation failed before producing a usable identity. The record remains
    /// available for diagnostics but cannot access native state.
    /// </summary>
    NonRecoverable,
}
