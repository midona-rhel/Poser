using System.Numerics;
using Poser.Domain.Transforms;
using Poser.Game.Animation;

namespace Poser.Tests;

public sealed class IdlePoseLocalTransformTests
{
    [Fact]
    public void Rotated_nonuniform_scale_exports_without_matrix_decomposition()
    {
        var parent = new Transform(new(2, 3, 4), Quaternion.CreateFromYawPitchRoll(.3f, -.5f, .8f), new(2, .5f, 3));
        var local = new Transform(new(.4f, -.2f, .7f), Quaternion.CreateFromYawPitchRoll(.7f, .4f, -.9f), new(.8f, 1.3f, .6f));
        var model = Compose(parent, local);
        Assert.True(Matrix4x4.Invert(parent.ToMatrix(), out var inverse));
        Assert.False(Matrix4x4.Decompose(model.ToMatrix() * inverse, out _, out _, out _));
        Equivalent(local, IdlePoseLocalTransform.FromModel("j_kao", model, parent));
    }

    [Theory]
    [InlineData(-2f, .5f, 3f)]
    public void Local_components_reconstruct_the_captured_model_pose(float x, float y, float z)
    {
        var parent = new Transform(new(7, -2, 3), Quaternion.CreateFromYawPitchRoll(.2f, .8f, -1f), new(x, y, z));
        var model = new Transform(new(4, 5, -3), Quaternion.CreateFromYawPitchRoll(-.4f, .6f, .9f), new(.4f, -2, 3));
        var local = IdlePoseLocalTransform.FromModel("j_f_ago", model, parent);
        var reconstructed = Compose(parent, Transform.FromPose(local));
        Equivalent(model, PoseTransform.CreateChecked(reconstructed.Position, reconstructed.Rotation, reconstructed.Scale));
    }

    [Fact]
    public void Root_keeps_position_orientation_and_signed_scale()
    {
        var root = new Transform(new(1, 2, 3), Quaternion.CreateFromYawPitchRoll(.3f, .5f, .7f), new(-2, 3, .5f));
        Equivalent(root, IdlePoseLocalTransform.FromModel("n_root", root, null));
    }

    private static Transform Compose(Transform parent, Transform local) => new(
        parent.Position + Vector3.Transform(parent.Scale * local.Position, parent.Rotation),
        Quaternion.Normalize(parent.Rotation * local.Rotation), parent.Scale * local.Scale);

    private static void Equivalent(Transform expected, PoseTransform actual)
    {
        Assert.True(Vector3.Distance(expected.Position, actual.Position) < .0001f);
        Assert.True(MathF.Abs(Quaternion.Dot(expected.Rotation, actual.Rotation)) > .99999f);
        Assert.True(Vector3.Distance(expected.Scale, actual.Scale) < .0001f);
    }
}
