using NSubstitute;
using Poser.Application.Posing;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Game.Entities;
using Poser.Game.Posing;
using Poser.Game.Services;

namespace Poser.Game.Tests.Posing;

public sealed class ActorPoseReadinessTests
{
    [Fact]
    public void Copy_waits_for_late_weapon_and_its_binding_publication()
    {
        var bindings = Substitute.For<IEntityBindings>();
        var body = Skeleton(PoseSlot.Character);
        var weapon = Skeleton(PoseSlot.MainHand);
        Publish(bindings, body);
        PoseImportWrite[] copied = [Write(PoseSlot.Character), Write(PoseSlot.MainHand)];

        Assert.True(ActorPoseReadiness.IsReady([body], bindings));
        Assert.False(ActorPoseReadiness.IsReady([body], bindings, copied));
        Assert.False(ActorPoseReadiness.IsReady([body, weapon], bindings, copied));
        Publish(bindings, weapon);
        Assert.True(ActorPoseReadiness.IsReady([body, weapon], bindings, copied));
    }

    [Theory]
    [InlineData(0, "n_root", true)]
    [InlineData(1, "n_root", false)]
    [InlineData(0, "other_root", false)]
    public void Required_bones_use_exact_partial_and_name(int partial, string name, bool expected)
    {
        var bindings = Substitute.For<IEntityBindings>();
        var body = Skeleton(PoseSlot.Character);
        Publish(bindings, body);
        var required = Write(PoseSlot.Character) with { Partial = partial, Bone = name };
        Assert.Equal(expected, ActorPoseReadiness.IsReady([body], bindings, [required]));
    }

    [Fact]
    public void Replacement_root_must_be_the_published_instance()
    {
        var bindings = Substitute.For<IEntityBindings>();
        var body = Skeleton(PoseSlot.Character);
        var id = Publish(bindings, body);
        var replacement = Skeleton(PoseSlot.Character);
        bindings.GetBoneId(replacement.RootBone!).Returns(id);
        Assert.False(ActorPoseReadiness.IsReady([replacement], bindings, [Write(PoseSlot.Character)]));
        var published = new BindingResult<IBone>(BindingStatus.Success, replacement.RootBone);
        bindings.Resolve(id).Returns(published);
        Assert.True(ActorPoseReadiness.IsReady([replacement], bindings, [Write(PoseSlot.Character)]));
    }

    [Fact]
    public void A_weapon_without_a_character_is_not_a_ready_actor()
    {
        var bindings = Substitute.For<IEntityBindings>();
        var weapon = Skeleton(PoseSlot.MainHand);
        Publish(bindings, weapon);
        Assert.False(ActorPoseReadiness.IsReady([weapon], bindings, [Write(PoseSlot.MainHand)]));
    }

    private static PoseImportWrite Write(PoseSlot slot) =>
        new(slot, 0, "n_root", Transform.Identity, TransformComponents.All);

    private static ISkeleton Skeleton(PoseSlot slot)
    {
        var skeleton = Substitute.For<ISkeleton>();
        var root = Substitute.For<IBone>();
        root.BoneName.Returns("n_root");
        root.PartialId.Returns(0);
        skeleton.Slot.Returns(slot);
        skeleton.RootBone.Returns(root);
        skeleton.Bones.Returns(new[] { root });
        return skeleton;
    }

    private static BoneId Publish(IEntityBindings bindings, ISkeleton skeleton)
    {
        var id = new BoneId(new(new(Guid.NewGuid(), 0), skeleton.Slot, 0), 0, 0, "n_root");
        bindings.GetBoneId(skeleton.RootBone!).Returns(id);
        var published = new BindingResult<IBone>(BindingStatus.Success, skeleton.RootBone);
        bindings.Resolve(id).Returns(published);
        return id;
    }
}
