using Poser.Domain.Integration;

namespace Poser.Game.Integration;

/// <summary>
/// The Penumbra half of "a clone looks 1:1": the source's effective
/// collection follows the appearance copy onto the clone.
///
/// It is addressed by NATIVE ADDRESS rather than by a stable id because the
/// only caller is the spawn transaction, which holds proven addresses for
/// both objects on the framework tick that copies one into the other — the
/// clone has no stable binding yet, and an overworld clone source never has
/// one at all. Both addresses are resolved to object indices at the call
/// boundary and nothing native is retained, exactly as
/// <see cref="IntegrationRuntimePort"/> does for its stable-id calls.
///
/// Why an explicit assignment is needed at all: Penumbra identifies a GPose
/// actor through the parent index its CopyCharacter hook recorded
/// (Penumbra CutsceneService.cs:123-130), and the spawn's SECOND, self-
/// directed CharacterSetup copy deliberately points that parent at the clone
/// itself. So the clone resolves under its OWN identifier ("Poser One") and
/// inherits none of the source's mods. That self-copy is also what makes the
/// assignment safe — without it the assignment would land on the SOURCE's
/// identifier and rewrite the user's own character collection.
/// </summary>
public interface ISpawnCollectionPort
{
    /// <summary>Assigns the source's EFFECTIVE collection — an individual
    /// assignment when it has one, otherwise whatever Penumbra actually
    /// resolves for it — to the clone as an individual assignment.</summary>
    IntegrationPortResult InheritCollection(nint sourceAddress, nint cloneAddress);

    /// <summary>Removes the assignment <see cref="InheritCollection"/> made.
    /// Must run while the clone still resolves: Penumbra keys the assignment
    /// on the object's identifier, which stops existing with the object.</summary>
    IntegrationPortResult ReleaseCollection(nint cloneAddress);
}
