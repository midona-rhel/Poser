using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Game.Entities;

namespace Poser.Game;

/// <summary>
/// A pose store's address: the ACTOR and the SLOT, by name. Deliberately
/// not the skeleton instance and not the actor's pointer.
///
/// <para>This key used to carry <c>skeleton.Id.Unique</c>, a fresh id per
/// Skeleton object. A redraw builds a new Skeleton, so the authored pose
/// ended up filed under a key nothing would look up again — and every
/// piece of machinery that existed to survive a redraw (the carryover
/// parking lot, the two adoption points, the migration in the
/// skeleton-created handler) existed only to move poses from the dead key
/// to the live one. Keyed by name, the store simply stays where it is and
/// the next apply pass lands it on whatever skeleton is current.</para>
///
/// <para>The bone stacks inside were already name-keyed
/// (<c>SkeletonPoseInfo.GetPoseInfo(name, partial)</c>). Only the outer key
/// was instance-bound.</para>
/// </summary>
internal readonly record struct SkeletonKey(
    string Actor,
    PoseSlot Slot)
{
    public static SkeletonKey Of(ISkeleton skeleton) => new(
        skeleton.Actor.Id.Unique,
        skeleton.Slot);
}
