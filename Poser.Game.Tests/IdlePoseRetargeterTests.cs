using System.Numerics;
using Poser.Documents.Animation;
using Poser.Domain.Transforms;
using Poser.Game.Animation;

namespace Poser.Game.Tests;

public sealed class IdlePoseRetargeterTests
{
    [Fact]
    public void Bone_names_not_indices_choose_tracks_and_target_lengths_are_preserved()
    {
        var reference = PoseTransform.Identity with { Position = Vector3.UnitY };
        var targetReference = reference with { Position = Vector3.UnitY * 2 };
        var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .6f);
        var source = new IdleHavokEncoder.SkeletonLayout("root", ["root", "arm", "extra"], [-1, 0, 0], [PoseTransform.Identity, reference, reference]);
        var target = new IdleHavokEncoder.SkeletonLayout("root", ["root", "other", "arm"], [-1, 0, 0], [PoseTransform.Identity, targetReference, targetReference]);
        var pose = new IdleSkeletonPose("root", 3, [new(0, PoseTransform.Identity, PoseTransform.Identity),
            new(1, reference, reference with { Rotation = rotation }), new(2, reference, reference)]);
        var mapped = IdlePoseRetargeter.Retarget(source, pose, target, target.ReferencePose);
        Assert.Equal(targetReference.Position, mapped.Tracks[2].Posed.Position);
        Assert.True(MathF.Abs(Quaternion.Dot(rotation, mapped.Tracks[2].Posed.Rotation)) > .999999f);
        Assert.Equal(targetReference, mapped.Tracks[1].Posed);
        Assert.Equal(new short[] { 0, 1, 2 }, mapped.Tracks.Select(t => t.BoneIndex));
    }

    [Fact]
    public void Facial_offsets_are_relative_to_target_reference_and_scale_by_bone_length()
    {
        var reference = PoseTransform.Identity with { Position = Vector3.UnitX };
        var targetRef = reference with { Position = Vector3.UnitX * 3 };
        var source = new IdleHavokEncoder.SkeletonLayout("face", ["face", "lid"], [-1, 0], [PoseTransform.Identity, reference]);
        var target = source with { ReferencePose = [PoseTransform.Identity, targetRef] };
        var pose = new IdleSkeletonPose("face", 2, [new(0, PoseTransform.Identity, PoseTransform.Identity),
            new(1, reference, reference with { Position = Vector3.UnitX + Vector3.UnitY * .1f })]);
        var output = IdlePoseRetargeter.Retarget(source, pose, target, target.ReferencePose);
        Assert.True(Vector3.Distance(new(3, .3f, 0), output.Tracks[1].Posed.Position) < 1e-6f);
    }
}
