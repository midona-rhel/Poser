using Poser.Domain.Integration;

namespace Poser.Game.Integration;

/// <summary>
/// Copies Penumbra collection state onto a newly spawned actor. These methods
/// use native addresses because the spawn transaction runs before the clone has
/// a stable binding. Each call resolves the addresses immediately and retains
/// no native pointer.
/// </summary>
public interface ISpawnCollectionPort
{
    IntegrationValue<SpawnCollectionSnapshot?> CaptureInheritedCollection(nint actor) =>
        IntegrationValue<SpawnCollectionSnapshot?>.Ok(null);
    IntegrationResult RestoreInheritedCollection(nint actor, SpawnCollectionSnapshot snapshot) =>
        IntegrationResult.Fail("Collection history is unavailable.");

    /// <summary>Assigns the source's effective collection — an individual
    /// assignment when it has one, otherwise whatever Penumbra actually
    /// resolves for it — to the clone as an individual assignment.</summary>
    IntegrationResult InheritCollection(nint sourceAddress, nint cloneAddress);

    /// <summary>Removes the assignment <see cref="InheritCollection"/> made.
    /// Must run while the clone still resolves: Penumbra keys the assignment
    /// on the object's identifier, which stops existing with the object.</summary>
    IntegrationResult ReleaseCollection(nint cloneAddress);

    /// <summary>For a clone whose slot vanished before it could be released:
    /// deletes its own temporary collection by GUID without touching the
    /// object, so nothing outlives the clone and no later body at the same
    /// address inherits the stale GUID.</summary>
    IntegrationResult DiscardCollection(nint cloneAddress) => IntegrationResult.Ok();

    /// <summary>Assigns the player's effective collection to a fresh actor
    /// that was not copied from anyone in particular — a new actor, a
    /// catalog creature. Brio leaves such a spawn to Penumbra's own
    /// identification; Poser's self-directed copy gives the spawn its own
    /// identifier, so the assignment has to be explicit.</summary>
    IntegrationResult AssignPlayerCollection(nint cloneAddress);

}
