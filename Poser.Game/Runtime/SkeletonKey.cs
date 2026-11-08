using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Game.Entities;

namespace Poser.Game;

/// <summary>
/// Identifies persisted pose state by actor and equipment slot. It excludes
/// native pointers and skeleton instances so a redraw can replace the runtime
/// skeleton without losing the authored pose.
/// </summary>
internal readonly record struct SkeletonKey(
    string Actor,
    PoseSlot Slot)
{
    public static SkeletonKey Of(ISkeleton skeleton) => new(
        skeleton.Actor.Id.Unique,
        skeleton.Slot);
}
