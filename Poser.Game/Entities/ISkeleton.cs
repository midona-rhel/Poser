using System;
using System.Collections.Generic;
using Poser.Domain.Identity;

namespace Poser.Entities;

/// <summary>
/// Represents one slot skeleton attached to an actor. One actor owns
/// independently replaceable Character, MainHand, OffHand, Prop, and
/// Ornament skeletons; a slot is never another actor.
/// </summary>
public interface ISkeleton : IEntity
{
    /// <summary>
    /// The actor this skeleton belongs to.
    /// </summary>
    IActor Actor { get; }

    /// <summary>The pose slot this skeleton occupies.</summary>
    PoseSlot Slot { get; }

    /// <summary>
    /// The native CharacterBase this skeleton was built from. A different
    /// current pointer for the same slot means the skeleton was replaced.
    /// </summary>
    nint CharacterBaseAddress { get; }

    /// <summary>
    /// The root bone of the skeleton.
    /// </summary>
    IBone? RootBone { get; }

    /// <summary>
    /// All bones in the skeleton.
    /// </summary>
    IReadOnlyList<IBone> Bones { get; }

    /// <summary>
    /// Whether this skeleton is valid and has been initialized.
    /// </summary>
    bool IsValid { get; }

    /// <summary>
    /// Advances every time the skeleton's native view is (re)built — the
    /// skeleton-change key consumers compare instead of any per-instance id
    /// (issue #78). Two reads returning the same value have seen the same
    /// build of the same native skeleton.
    /// </summary>
    long BuildRevision { get; }

    /// <summary>
    /// Gets a bone by name.
    /// </summary>
    IBone? GetBone(string name);

    /// <summary>
    /// Gets a bone by index and partial ID.
    /// </summary>
    IBone? GetBone(int partialId, int boneIndex);

    /// <summary>
    /// Refreshes the skeleton data from game memory.
    /// </summary>
    void Refresh();

    /// <summary>
    /// The skeleton's own rest ("reference") pose in model space, one entry
    /// per mapped bone, computed from the native skeleton's reference locals
    /// without touching the live pose. Attach-driven partial roots (every
    /// partial root except the skeleton root) are skipped. Empty when the
    /// native skeleton is unavailable.
    /// </summary>
    IReadOnlyList<(IBone Bone, Transform Reference)> CaptureReferencePose();
}
