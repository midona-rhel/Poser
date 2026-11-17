using Poser.Domain.Identity;

namespace Poser.Domain.Posing;

/// <summary>
/// Identifies a bone by slot, partial skeleton, and name, without tying it to an actor.
/// </summary>
public readonly record struct PortableBoneId(
    PoseSlot Slot,
    int PartialId,
    string CanonicalName)
{
    public bool IsValid =>
        Slot != PoseSlot.Unknown &&
        PartialId >= 0 &&
        !string.IsNullOrWhiteSpace(CanonicalName);

    public static PortableBoneId From(BoneId bone) =>
        new(bone.Slot, bone.PartialId, bone.CanonicalName);
}
