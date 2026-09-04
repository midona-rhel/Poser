using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests.Transforms;

public sealed class GroupTransformReadModelTests
{
    [Fact]
    public void Local_and_world_rotation_intents_share_orientation_but_not_axes()
    {
        var frame = new GroupTransformFrame(Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f));
        var authored = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f);
        var worldOrientation = frame.ToWorldOrientation(authored);
        var increment = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .3f);
        // Overlay rotates around displayed local X; numeric input requests Q * increment.
        var overlay = Quaternion.CreateFromAxisAngle(Vector3.Transform(Vector3.UnitX, worldOrientation), .3f);
        var numeric = frame.ToWorldDelta(authored * increment * Quaternion.Inverse(authored));
        Assert.True(MathF.Abs(Quaternion.Dot(overlay, numeric)) > .99999f);
        var controls = GroupTransformControls.Identity(Vector3.Zero) with { Rotation = authored };
        Assert.True(controls.TryAdvance(frame, new(Vector3.Zero, overlay, Vector3.One),
            GroupScaleMode.SpacingOnly, Vector3.Zero, out var local));
        Assert.True(MathF.Abs(Quaternion.Dot(frame.ToWorldOrientation(local.Rotation), worldOrientation * increment)) > .99999f);
        Assert.True(controls.TryAdvance(frame, new(Vector3.Zero, increment, Vector3.One),
            GroupScaleMode.SpacingOnly, Vector3.Zero, out var world));
        Assert.True(MathF.Abs(Quaternion.Dot(frame.ToWorldOrientation(world.Rotation), increment * worldOrientation)) > .99999f);
        Assert.True(MathF.Abs(Quaternion.Dot(local.Rotation, world.Rotation)) < .9999f);
    }

    [Fact]
    public void Authored_read_is_independent_of_member_order_and_quaternion_sign()
    {
        var first = Target();
        var second = Target();
        var initial = new Dictionary<TransformTargetId, PoseTransform>
        {
            [first] = Pose(Vector3.Zero),
            [second] = Pose(new(2f, 0f, 0f)),
        };
        Assert.True(GroupTransformBaseline.TryCapture(
            initial,
            GroupTransformFrame.World(new(1f, 2f, 3f)),
            out var baseline,
            out _));
        var controls = new GroupTransformControls(
            new(3f, 4f, 5f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f),
            new(2f, 1f, 1f),
            new(1.5f, 1f, 1f));
        var expected = new Dictionary<TransformTargetId, PoseTransform>
        {
            [first] = Pose(new(1f, 0f, 0f)),
            [second] = Pose(new(3f, 0f, 0f), new(0f, 0f, 0f, -1f)),
        };
        var state = new GroupTransformSnapshot(baseline!, expected, controls);

        Assert.True(GroupTransformReadModel.TryRead(
            state,
            new Dictionary<TransformTargetId, PoseTransform>
            {
                [second] = expected[second] with { Rotation = Quaternion.Identity },
                [first] = expected[first],
            },
            GroupScaleMode.SizesAndSpacing,
            out var read,
            out _));
        Assert.Equal(controls.Position, read.Position);
        Assert.Equal(controls.OwnScale, read.Scale);
        Assert.True(MathF.Abs(Quaternion.Dot(read.Rotation, controls.Rotation)) > .9999f);
    }

    [Fact]
    public void External_member_edit_invalidates_instead_of_fitting_a_new_pose()
    {
        var first = Target();
        var second = Target();
        var initial = new Dictionary<TransformTargetId, PoseTransform>
        {
            [first] = Pose(Vector3.Zero),
            [second] = Pose(Vector3.UnitX),
        };
        Assert.True(GroupTransformBaseline.TryCapture(
            initial,
            GroupTransformFrame.World(Vector3.Zero),
            out var baseline,
            out _));
        var state = new GroupTransformSnapshot(
            baseline!, initial, GroupTransformControls.Identity(baseline!.InitialCentroid));
        var changed = new Dictionary<TransformTargetId, PoseTransform>(initial)
        {
            [second] = Pose(new(3f, 0f, 0f)),
        };

        Assert.False(GroupTransformReadModel.TryRead(
            state, changed, GroupScaleMode.SpacingOnly, out _, out var error));
        Assert.Contains("outside", error, StringComparison.OrdinalIgnoreCase);
    }

    private static TransformTargetId Target() =>
        TransformTargetId.ForActor(ActorId.New());

    [Theory]
    [InlineData(.7f, -.2f, .1f)]
    [InlineData(-1.2f, 1.1f, 2f)]
    [InlineData(2.4f, -.9f, -2.3f)]
    public void Captured_camera_frame_keeps_heading_but_removes_pitch_and_roll(float yaw, float pitch, float roll)
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(yaw, pitch, roll);
        var cameraWorld = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(4, 5, 6);
        Assert.True(Matrix4x4.Invert(cameraWorld, out var view));
        Assert.True(GroupTransformFrame.TryFromView(view, Vector3.One, out var frame));
        Assert.True(MathF.Abs(Quaternion.Dot(frame.Rotation,
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, yaw))) > .99999f);
        Assert.True(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, frame.Rotation)) < .00001f);
        Assert.Equal(Vector3.One, frame.Origin);
        Assert.False(GroupTransformFrame.TryFromView(default, Vector3.Zero, out _));
        Assert.False(GroupTransformFrame.TryFromView(view, new(float.NaN), out _));
        var authored = Quaternion.CreateFromYawPitchRoll(0, .4f, -.3f);
        var controls = GroupTransformControls.Identity(frame.Origin);
        Assert.True(controls.TryAdvance(frame, new(Vector3.Zero, frame.ToWorldDelta(authored), Vector3.One),
            GroupScaleMode.SpacingOnly, frame.Origin, out var next));
        Assert.True(MathF.Abs(Quaternion.Dot(authored, next.Rotation)) > .99999f);
        Assert.True(Vector3.Distance(Vector3.UnitY,
            Vector3.Transform(Vector3.UnitY, frame.ToWorldOrientation(next.Rotation))) > .1f);
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(-1f, 0f)]
    [InlineData(1f, .8f)]
    [InlineData(-1f, -.8f)]
    public void Vertical_camera_uses_finite_deterministic_right_heading(float sign, float roll)
    {
        Quaternion? previous = null;
        foreach (float offset in new[] { -.0001f, 0f, .0001f })
        {
            var rotation = Quaternion.CreateFromYawPitchRoll(.7f, sign * MathF.PI / 2 + offset, roll);
            Assert.True(Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(rotation), out var view));
            Assert.True(GroupTransformFrame.TryFromView(view, new(2, 3, 4), out var frame));
            Assert.True(frame.IsValid);
            Assert.Equal(new Vector3(2, 3, 4), frame.Origin);
            Assert.True(Vector3.Distance(Vector3.UnitY, Vector3.Transform(Vector3.UnitY, frame.Rotation)) < .00001f);
            var right = Vector3.Transform(Vector3.UnitX, rotation);
            var expected = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.Atan2(-right.Z, right.X));
            Assert.True(MathF.Abs(Quaternion.Dot(expected, frame.Rotation)) > .99999f);
            if (previous is { } prior) Assert.True(MathF.Abs(Quaternion.Dot(prior, frame.Rotation)) > .99999f);
            previous = frame.Rotation;
        }
    }

    [Fact]
    public void Coincident_reflected_members_need_no_geometric_fit()
    {
        var initial = new Dictionary<TransformTargetId, PoseTransform> {
            [Target()] = new(Vector3.Zero, Quaternion.Identity, new(-1, 2, 3)),
            [Target()] = new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .6f), new(3, 1, -2))
        };
        Assert.True(GroupTransformBaseline.TryCapture(initial, GroupTransformFrame.World(Vector3.Zero),
            out var baseline, out _));
        var snapshot = new GroupTransformSnapshot(baseline!, initial, GroupTransformControls.Identity(Vector3.Zero));
        Assert.True(GroupTransformReadModel.TryRead(snapshot, initial, GroupScaleMode.SpacingOnly, out var read, out _));
        Assert.Equal(Vector3.One, read.Scale);
    }

    private static PoseTransform Pose(
        Vector3 position,
        Quaternion rotation = default) =>
        PoseTransform.CreateChecked(
            position,
            rotation == default ? Quaternion.Identity : rotation,
            Vector3.One);
}
