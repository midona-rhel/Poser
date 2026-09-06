using System.Numerics;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Posing;

/// <summary>History stores owned deltas, never the animation frame underneath them.</summary>
internal sealed record AuthoredPoseState(IReadOnlyList<AuthoredBoneState> Bones)
{
    public static AuthoredPoseState Capture(IEnumerable<ISkeleton> skeletons, IBonePosingService posing)
    {
        var bones = new List<AuthoredBoneState>();
        foreach (var skeleton in skeletons)
            foreach (var bone in skeleton.Bones)
            {
                var stacks = posing.CapturePoseStacks(bone).Where(x => x.Layer is null).ToArray();
                if (stacks.Length != 0 || bone.PartialRootScale is not null)
                    bones.Add(new(skeleton.Slot, bone.PartialId, bone.BoneName, stacks, bone.PartialRootScale));
            }
        return new(bones);
    }

    public void Restore(IEnumerable<ISkeleton> skeletons, IBonePosingService posing)
    {
        var saved = Bones.ToDictionary(x => (x.Slot, x.Partial, x.Name));
        foreach (var skeleton in skeletons)
            foreach (var bone in skeleton.Bones)
            {
                saved.TryGetValue((skeleton.Slot, bone.PartialId, bone.BoneName), out var state);
                posing.RestorePoseStacks(bone, state?.Stacks ?? []);
                bone.PartialRootScale = state?.PartialRootScale;
            }
    }
}

internal sealed record AuthoredBoneState(PoseSlot Slot, int Partial, string Name,
    IReadOnlyList<BonePoseTransformInfo> Stacks, Vector3? PartialRootScale);
