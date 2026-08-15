using System.Numerics;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.ContractTests;

/// <summary>
/// Ktisis' RelativeBones, which Poser ships OFF because Brio's one-delta-to-all
/// is what every Poser build so far has done. The arithmetic claim is that a
/// secondary keeps the angle it held to the primary; these cases state it
/// against the reference formula rather than against a screenshot.
/// </summary>
public sealed class RelativeSecondaryBoneContractTests
{
    [Fact]
    public void A_secondary_keeps_the_angle_it_held_to_the_primary()
    {
        // The secondary starts a quarter turn about Y away from the primary.
        var offset = Yaw(90f);
        var delta = Yaw(30f);

        var relative = TransformMath.RelativeToPrimary(
            new TransformDelta(Vector3.Zero, delta, Vector3.One),
            primaryBaseline: Quaternion.Identity,
            targetBaseline: offset);

        // Apply post-multiplies onto the target's own baseline, so the result
        // must equal Ktisis' direct write: (q_t·q_p⁻¹)·Δ·q_p.
        var applied = TransformMath.Apply(
            PoseTransform.CreateChecked(Vector3.Zero, offset, Vector3.One),
            relative,
            TransformSpace.World,
            Vector3.Zero,
            rotatePosition: false);
        AssertRotation(offset * delta, applied.Rotation);

        // And the angle to the primary is preserved: the primary turned by
        // delta, the secondary by the same amount about its own frame, so the
        // 90° between them is still 90°.
        var primary = TransformMath.Apply(
            PoseTransform.Identity,
            new TransformDelta(Vector3.Zero, delta, Vector3.One),
            TransformSpace.World,
            Vector3.Zero,
            rotatePosition: false);
        AssertRotation(
            offset,
            Quaternion.Inverse(primary.Rotation) * applied.Rotation);
    }

    [Fact]
    public void The_primarys_own_delta_is_untouched_by_the_rebase()
    {
        var delta = new TransformDelta(
            new Vector3(1, 2, 3), Yaw(30f), new Vector3(2, 2, 2));

        var relative = TransformMath.RelativeToPrimary(
            delta, Quaternion.Identity, Quaternion.Identity);

        AssertRotation(delta.Rotation, relative.Rotation);
        // Translation and scale never rebase: the relative claim is about
        // angle, and per-target translation would scatter a group move.
        Assert.Equal(delta.Translation, relative.Translation);
        Assert.Equal(delta.ScaleFactor, relative.ScaleFactor);
    }

    [Fact]
    public void The_gesture_rebases_secondaries_and_leaves_the_primary_alone()
    {
        var primary = TestIds.BoneTarget(name: "j_sebo_a", boneIndex: 1);
        var secondary = TestIds.BoneTarget(name: "j_sebo_b", boneIndex: 2);
        using var app = Harness(primary, secondary, secondaryRotation: Yaw(90f));

        var begun = app.Gestures.Begin(new BeginTransformGesture(
            [primary, secondary],
            TransformOperation.Rotate,
            TransformSpace.World,
            PivotMode.PerTarget,
            RelativeSecondaryBones: true));
        Assert.True(begun.Success, begun.Detail);
        Assert.True(app.Gestures.Update(
            begun.GestureId!.Value,
            new TransformDelta(Vector3.Zero, Yaw(30f), Vector3.One)).Success);

        AssertRotation(Yaw(30f), app.Runtime.State(primary).Transform.Rotation);
        AssertRotation(
            Yaw(90f) * Yaw(30f),
            app.Runtime.State(secondary).Transform.Rotation);
    }

    [Fact]
    public void With_the_toggle_off_every_target_takes_the_same_delta()
    {
        var primary = TestIds.BoneTarget(name: "j_sebo_a", boneIndex: 1);
        var secondary = TestIds.BoneTarget(name: "j_sebo_b", boneIndex: 2);
        using var app = Harness(primary, secondary, secondaryRotation: Yaw(90f));

        var begun = app.Gestures.Begin(new BeginTransformGesture(
            [primary, secondary],
            TransformOperation.Rotate,
            TransformSpace.World,
            PivotMode.PerTarget));
        Assert.True(begun.Success, begun.Detail);
        Assert.True(app.Gestures.Update(
            begun.GestureId!.Value,
            new TransformDelta(Vector3.Zero, Yaw(30f), Vector3.One)).Success);

        // Brio's behaviour, and Poser's default: the raw delta pre-multiplies
        // onto each baseline, so the secondary swings about the PRIMARY's axes.
        AssertRotation(
            Yaw(30f) * Yaw(90f),
            app.Runtime.State(secondary).Transform.Rotation);
    }

    private static TransformApplicationHarness Harness(
        TransformTargetId primary,
        TransformTargetId secondary,
        Quaternion secondaryRotation)
    {
        var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorAndBonesScene(
            TestIds.Actor(), primary.Bone!.Value, secondary.Bone!.Value));
        app.Runtime.Seed(new TransformTargetState(
            primary,
            PoseTransform.Identity,
            new Domain.Posing.BonePose(),
            HasOverride: false));
        app.Runtime.Seed(new TransformTargetState(
            secondary,
            PoseTransform.CreateChecked(
                Vector3.Zero, secondaryRotation, Vector3.One),
            new Domain.Posing.BonePose(),
            HasOverride: false));
        return app;
    }

    private static Quaternion Yaw(float degrees) =>
        Quaternion.CreateFromAxisAngle(
            Vector3.UnitY, degrees * MathF.PI / 180f);

    private static void AssertRotation(Quaternion expected, Quaternion actual)
    {
        expected = Quaternion.Normalize(expected);
        actual = Quaternion.Normalize(actual);
        float dot = MathF.Abs(Quaternion.Dot(expected, actual));
        Assert.True(
            dot > 0.9999f,
            $"Expected rotation {expected}, got {actual}.");
    }
}
