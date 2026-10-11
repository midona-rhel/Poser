using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Game.Transforms;

namespace Poser.Game.Tests.Transforms;

public sealed class RetainedTransformTargetTests
{
    [Theory]
    [InlineData(PoseSlot.Character)]
    [InlineData(PoseSlot.MainHand)]
    [InlineData(PoseSlot.OffHand)]
    public void Bone_history_resolves_after_redraw_without_using_group_identity(PoseSlot slot)
    {
        var oldActor = ActorId.New();
        var actor = oldActor.NextGeneration();
        var oldBone = new BoneId(new SkeletonId(oldActor, slot, 1), 0, 0, "n_root");
        var bone = oldBone with { Skeleton = new SkeletonId(actor, slot, 2) };
        var scene = new SceneSession(new SelectionSession());
        Assert.True(scene.TryRefresh(new SceneSnapshot(1,
            [new ActorDescriptor(actor, "Actor", [new SkeletonDescriptor(bone.Skeleton,
                [new BoneDescriptor(bone, "Root", null)])])], [], [], [])).Accepted);
        var source = new GroupTransformSource(scene, null!, null!, null!);

        Assert.Equal(TransformTargetId.ForBone(bone), source.CurrentTarget(TransformTargetId.ForBone(oldBone)));
        Assert.False(scene.Contains(TransformTargetId.ForBone(oldBone)));
        Assert.Equal("Bones cannot join an entity group transform.", source.Refusal(TransformTargetId.ForBone(bone)));
    }
}
