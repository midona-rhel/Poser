using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests.Transforms;

public sealed class GroupTransformReadModelTests
{
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

    [Fact]
    public void Captured_camera_frame_is_inverse_view_rotation()
    {
        var rotation = Quaternion.CreateFromYawPitchRoll(.7f, -.2f, .1f);
        var cameraWorld = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(4, 5, 6);
        Assert.True(Matrix4x4.Invert(cameraWorld, out var view));
        Assert.True(GroupTransformFrame.TryFromView(view, Vector3.One, out var frame));
        Assert.True(MathF.Abs(Quaternion.Dot(frame.Rotation, rotation)) > .99999f);
        Assert.Equal(Vector3.One, frame.Origin);
        Assert.False(GroupTransformFrame.TryFromView(default, Vector3.Zero, out _));
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
