using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Game.Bindings;

namespace Poser.Game.Posing;

internal static class ActorPoseReadiness
{
    // Weapon slots can arrive before the body during a redraw.
    internal static bool HasCharacterSkeleton(IReadOnlyList<ISkeleton> skeletons) =>
        skeletons.Any(s => s.Slot == PoseSlot.Character
            && s.RootBone != null && s.Bones.Count > 0);

    internal static bool IsReady(IReadOnlyList<ISkeleton> skeletons, StableBindingRegistry bindings) =>
        HasCharacterSkeleton(skeletons) && skeletons.All(s =>
            s.RootBone is { } root && bindings.GetBoneId(root) is { } id
            && bindings.Resolve(id) is { Success: true, Value: { } published }
            && ReferenceEquals(root, published));
}
