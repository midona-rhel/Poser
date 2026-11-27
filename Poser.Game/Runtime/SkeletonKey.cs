using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Game.Entities;

namespace Poser.Game;

/// <summary>
/// Finds an actor's pose by actor and equipment slot, such as the body or a
/// weapon. It holds no game pointer and no skeleton object, so a redraw that
/// builds a new skeleton for the slot keeps the pose a person made.
/// </summary>
internal readonly record struct SkeletonKey(
    string Actor,
    PoseSlot Slot)
{
    public static SkeletonKey Of(ISkeleton skeleton) => new(
        skeleton.Actor.Id.Unique,
        skeleton.Slot);
}

/// <summary>
/// Finds one bone by actor, slot, partial skeleton and the bone's game name,
/// the way poses are found. Bone numbers can change when the skeleton is built
/// again; the name and partial stay, so this still finds the bone after a redraw.
/// </summary>
internal readonly record struct BoneKey(
    SkeletonKey Skeleton,
    int Partial,
    string Name)
{
    public static BoneKey Of(IBone bone) => new(
        SkeletonKey.Of(bone.Skeleton),
        bone.PartialId,
        bone.BoneName);
}
