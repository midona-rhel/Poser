using System.Collections.Immutable;
using System.Numerics;
using Poser.Documents.Animation;
using Poser.Domain.Transforms;

namespace Poser.Game.Animation;

internal static class IdlePoseRetargeter
{
    public static IdleSkeletonPose Retarget(IdleHavokEncoder.SkeletonLayout source, IdleSkeletonPose pose,
        IdleHavokEncoder.SkeletonLayout target, PoseTransform[] idle)
    {
        var names = source.Bones.Select((name, index) => (name, index)).ToDictionary(x => x.name, x => x.index);
        var posed = pose.Tracks.ToDictionary(t => (int)t.BoneIndex, t => t.Posed);
        var tracks = ImmutableArray.CreateBuilder<IdleBoneTrack>(target.Bones.Length);
        string? Parent(IdleHavokEncoder.SkeletonLayout layout, int i) => layout.Parents[i] < 0 ? null : layout.Bones[layout.Parents[i]];
        for (short i = 0; i < target.Bones.Length; i++)
        {
            var value = idle[i];
            if (names.TryGetValue(target.Bones[i], out int original) && posed.TryGetValue(original, out var sample)
                && Parent(source, original) == Parent(target, i))
            {
                var from = source.ReferencePose[original];
                var to = target.ReferencePose[i];
                // Transfer local offsets from each rig's reference, not source bone
                // lengths or indices. This is a basic retarget, not contact/IK fitting.
                float length = from.Position.Length();
                float ratio = length > 0.00001f ? to.Position.Length() / length : 1f;
                value = PoseTransform.CreateChecked(to.Position + (sample.Position - from.Position) * ratio,
                    sample.Rotation * Quaternion.Inverse(from.Rotation) * to.Rotation,
                    to.Scale * (sample.Scale / from.Scale));
            }
            tracks.Add(new(i, idle[i], value));
        }
        return new(target.Name, target.Bones.Length, tracks.MoveToImmutable());
    }
}
