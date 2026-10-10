using Poser.Domain.Companions;
using Poser.Entities;

namespace Poser.Game;

/// <summary>
/// Turns a wrapper into the exact native descriptor every operation runs
/// against, and answers which wrappers the ledger owns. Unresolved identity
/// (null or fault) is always refusal.
/// </summary>
internal sealed class SpawnActorResolver
{
    private readonly IActorSpawnNativeAdapter _native;
    private readonly SpawnOwnershipLedger _ownership;

    public SpawnActorResolver(IActorSpawnNativeAdapter native, SpawnOwnershipLedger ownership)
    {
        _native = native;
        _ownership = ownership;
    }

    /// <summary>
    /// The single fail-closed resolution gate for operations on arbitrary
    /// actors. Unresolved identity (null or fault) refuses the operation —
    /// it is never permission to dereference a raw address. A wrapper bound
    /// to an ownership record additionally requires its exact descriptor.
    /// </summary>
    public bool TryResolveForOperation(
        IActor actor,
        out SpawnNativeDescriptor descriptor,
        out SpawnOwnershipRecord? ownership)
    {
        descriptor = default;
        ownership = null;
        if (actor.Address == nint.Zero)
            return false;

        SpawnNativeDescriptor? current;
        try
        {
            current = _native.ResolveActor(actor.Address);
        }
        catch
        {
            return false;
        }
        if (current is null)
            return false;

        if (_ownership.TryGetBound(actor, out var bound))
        {
            if (!_ownership.TryGetExact(actor, current.Value, out _))
                return false;
            ownership = bound;
        }

        descriptor = current.Value;
        return true;
    }

    public bool IsSpawned(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        try
        {
            var current = _native.ResolveActor(actor.Address);
            return current is { } descriptor
                && _ownership.TryGetExact(actor, descriptor, out _);
        }
        catch
        {
            return false;
        }
    }

    public CompanionKind? SpawnedKind(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return null;
        try
        {
            var descriptor = _native.ResolveActor(actor.Address);
            return descriptor is { } current
                ? _ownership.GetKind(actor, current)
                : null;
        }
        catch
        {
            return null;
        }
    }
}
