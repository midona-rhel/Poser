using System.Numerics;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.ContractTests;

/// <summary>
/// What "Mirror edits" reaches beyond one skeleton's own left/right pairs: the
/// two weapon hands exchange with each other, and the actor's authored facing
/// mirrors with the body it belongs to.
/// </summary>
public sealed class MirrorTargetContractTests
{
    [Fact]
    public void Mirror_exchanges_an_adjustment_between_the_two_weapon_hands()
    {
        var (main, off) = WeaponBones();
        using var app = WeaponHarness(main, off, mainPose: PoseAt(4));

        var result = app.PoseEdits.Mirror(
            [TransformTargetId.ForBone(main), TransformTargetId.ForBone(off)],
            "Mirror edits");

        Assert.True(result.Success, result.Detail);
        // The main hand's adjustment crossed to the off hand, laterally
        // reflected; the main hand is left with the off hand's nothing.
        Assert.Equal(
            -4,
            app.Runtime.State(TransformTargetId.ForBone(off))
                .Pose.Layers[0].Delta.Position.X);
        Assert.Empty(
            app.Runtime.State(TransformTargetId.ForBone(main)).Pose.Layers);
    }

    [Fact]
    public void A_weapon_bone_with_no_opposite_hand_mirrors_in_place()
    {
        var (main, _) = WeaponBones();
        using var app = WeaponHarness(main, offHand: null, mainPose: PoseAt(4));

        var result = app.PoseEdits.Mirror(
            [TransformTargetId.ForBone(main)],
            "Mirror edits");

        Assert.True(result.Success, result.Detail);
        // Brio's wholesale dictionary move drops this bone into an off hand
        // that is not there; mirroring it where it stands keeps it.
        Assert.Equal(
            -4,
            app.Runtime.State(TransformTargetId.ForBone(main))
                .Pose.Layers[0].Delta.Position.X);
    }

    [Fact]
    public void Mirror_reflects_an_actors_authored_facing_in_the_same_entry()
    {
        var bone = TestIds.BoneTarget();
        using var app = ActorHarness(bone, Yaw(30f), hasOverride: true);
        var actor = TestIds.ActorTarget();

        var result = app.PoseEdits.Mirror([bone, actor], "Mirror edits");

        Assert.True(result.Success, result.Detail);
        AssertRotation(
            Yaw(-30f),
            app.Runtime.State(actor).Transform.Rotation);
        // One entry still covers the whole actor — the model transform did not
        // buy itself a second undo step.
        Assert.True(app.History.CanUndo);
        Assert.True(app.Gestures.Undo().Success);
        Assert.False(app.History.CanUndo);
        AssertRotation(Yaw(30f), app.Runtime.State(actor).Transform.Rotation);
    }

    [Fact]
    public void Mirror_leaves_the_world_position_of_an_actor_alone()
    {
        var bone = TestIds.BoneTarget();
        using var app = ActorHarness(bone, Yaw(30f), hasOverride: true);
        var actor = TestIds.ActorTarget();

        Assert.True(app.PoseEdits.Mirror([bone, actor], "Mirror edits").Success);

        // Reflecting a world X reflects the actor across the world origin,
        // which is an arbitrary point on any real map. Where an actor stands
        // is not a left/right fact.
        Assert.Equal(
            new Vector3(7, 0, 0),
            app.Runtime.State(actor).Transform.Position);
    }

    [Fact]
    public void An_actor_that_was_never_moved_is_not_mirrored()
    {
        var bone = TestIds.BoneTarget();
        using var app = ActorHarness(bone, Yaw(30f), hasOverride: false);
        var actor = TestIds.ActorTarget();

        Assert.True(app.PoseEdits.Mirror([bone, actor], "Mirror edits").Success);

        // Authored edits only, the contract the bones already keep.
        AssertRotation(Yaw(30f), app.Runtime.State(actor).Transform.Rotation);
    }

    [Fact]
    public void Mirror_refuses_a_target_that_is_neither_a_bone_nor_an_actor()
    {
        var bone = TestIds.BoneTarget();
        using var app = ActorHarness(bone, Yaw(30f), hasOverride: true);

        var result = app.PoseEdits.Mirror(
            [bone, TransformTargetId.ForLight(new LightId(TestIds.ActorLineage, 0))],
            "Mirror edits");

        Assert.False(result.Success);
        Assert.Equal("Mirror accepts bone and actor targets only.", result.Detail);
    }

    private static (BoneId Main, BoneId Off) WeaponBones()
    {
        var actor = TestIds.Actor();
        return (
            new BoneId(
                new SkeletonId(actor, PoseSlot.MainHand, 0), 0, 1, "n_buki_a"),
            new BoneId(
                new SkeletonId(actor, PoseSlot.OffHand, 0), 0, 1, "n_buki_a"));
    }

    private static TransformApplicationHarness WeaponHarness(
        BoneId mainHand,
        BoneId? offHand,
        BonePose mainPose)
    {
        var app = new TransformApplicationHarness();
        var bones = offHand is { } off
            ? new[] { mainHand, off }
            : new[] { mainHand };
        app.Scene.Refresh(new SceneSnapshot(
            1,
            [new ActorDescriptor(
                TestIds.Actor(),
                "Test actor",
                bones.Select(bone => new SkeletonDescriptor(
                    bone.Skeleton,
                    [new BoneDescriptor(bone, bone.CanonicalName, null)]))
                    .ToArray())],
            [],
            [],
            []));
        foreach (var bone in bones)
        {
            var target = TransformTargetId.ForBone(bone);
            var pose = bone == mainHand ? mainPose : new BonePose();
            app.Runtime.Seed(new TransformTargetState(
                target,
                PoseTransform.Identity,
                pose,
                HasOverride: pose.Layers.Count > 0));
        }

        return app;
    }

    private static TransformApplicationHarness ActorHarness(
        TransformTargetId bone,
        Quaternion actorRotation,
        bool hasOverride)
    {
        var app = new TransformApplicationHarness();
        app.Scene.Refresh(
            TestScenes.ActorAndBoneScene(TestIds.Actor(), bone.Bone!.Value));
        app.Runtime.Seed(new TransformTargetState(
            bone,
            PoseTransform.Identity,
            PoseAt(2),
            HasOverride: true));
        app.Runtime.Seed(new TransformTargetState(
            TestIds.ActorTarget(),
            PoseTransform.CreateChecked(
                new Vector3(7, 0, 0), actorRotation, Vector3.One),
            new BonePose(),
            hasOverride));
        return app;
    }

    private static Quaternion Yaw(float degrees) =>
        Quaternion.CreateFromAxisAngle(
            Vector3.UnitY, degrees * MathF.PI / 180f);

    private static void AssertRotation(Quaternion expected, Quaternion actual)
    {
        expected = Quaternion.Normalize(expected);
        actual = Quaternion.Normalize(actual);
        // A quaternion and its negation are the same rotation.
        float dot = MathF.Abs(Quaternion.Dot(expected, actual));
        Assert.True(
            dot > 0.9999f,
            $"Expected rotation {expected}, got {actual}.");
    }

    private static BonePose PoseAt(float x) =>
        new([
            new PoseLayer(
                new PoseLayerId(PoseLayerKind.Manual, "mirror"),
                TransformComponents.All,
                new PoseDelta(
                    new Vector3(x, 0, 0),
                    Quaternion.Identity,
                    Vector3.Zero)),
        ]);
}
