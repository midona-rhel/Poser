using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Domain.Tests;

public sealed class PortablePoseBaselineTests
{
    [Fact]
    public void Portable_pose_preserves_input_order_and_empty_bones()
    {
        var first = new PortableBonePose(
            new PortableBoneId(PoseSlot.Character, 0, "j_first"),
            new BonePose());
        var second = new PortableBonePose(
            new PortableBoneId(PoseSlot.Character, 1, "j_second"),
            new BonePose());

        var pose = new PortablePose([first, second]);

        Assert.Equal([first, second], pose.Bones);
        Assert.Equal(2, pose.Bones.Count);
        Assert.True(pose.TryGet(first.Bone, out var firstPose));
        Assert.Equal(first.Pose, firstPose);
    }

    [Fact]
    public void Portable_pose_rejects_duplicate_structural_ids_instead_of_overwriting()
    {
        var id = new PortableBoneId(PoseSlot.Character, 0, "j_duplicate");
        var entries = new[]
        {
            new PortableBonePose(id, new BonePose()),
            new PortableBonePose(id, new BonePose()),
        };

        Assert.Throws<ArgumentException>(() => new PortablePose(entries));
    }

    [Fact(Skip = "Slice 1 characterization: current PortablePose has no structural BonePath/ambiguity result API yet.")]
    public void Slice1_structural_portable_pose_characterization()
    {
        Assert.True(false);
    }
}
