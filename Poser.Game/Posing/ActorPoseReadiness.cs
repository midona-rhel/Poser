using Poser.Domain.Identity;
using Poser.Application.Posing;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Posing;

internal static class ActorPoseReadiness
{
    // Weapon slots can arrive before the body during a redraw.
    internal static bool HasCharacterSkeleton(IReadOnlyList<ISkeleton> skeletons) =>
        skeletons.Any(s => s.Slot == PoseSlot.Character
            && s.RootBone != null && s.Bones.Count > 0);

    internal static bool IsReady(IReadOnlyList<ISkeleton> skeletons, IEntityBindings bindings,
        IReadOnlyList<PoseImportWrite>? required = null)
    {
        if (!HasCharacterSkeleton(skeletons) || !skeletons.All(s =>
            s.RootBone is { } root && bindings.GetBoneId(root) is { } id
            && bindings.Resolve(id) is { Success: true, Value: { } published }
            && ReferenceEquals(root, published))) return false;
        if (required is null) return true;
        // A character can publish before its weapon slots. A live copy must
        // wait for the slots/partials it captured, not accept the body alone.
        var available = skeletons.SelectMany(s => s.Bones.Select(b =>
            (s.Slot, b.PartialId, b.BoneName))).ToHashSet();
        return required.All(b => available.Contains((b.Slot, b.Partial, b.Bone)));
    }
}
