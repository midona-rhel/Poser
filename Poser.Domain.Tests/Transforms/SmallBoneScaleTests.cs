using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests.Transforms;

public class SmallBoneScaleTests
{
    [Theory]
    [InlineData(9.999992e-7f)]
    [InlineData(-9.999992e-7f)]
    public void Appearance_hidden_bone_scale_is_valid_without_enlarging_it(float scale)
    {
        Assert.True(PoseTransform.TryCreate(Vector3.Zero, Quaternion.Identity, new(scale), out var pose, out var error), error);
        Assert.Equal(new Vector3(scale), pose.Scale);
        Assert.True(Matrix4x4.Invert(Matrix4x4.CreateScale(pose.Scale), out var inverse));
        Assert.True(float.IsFinite(inverse.M11));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1e-12f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void Unsafe_scales_remain_rejected(float scale)
        => Assert.False(PoseTransform.TryCreate(Vector3.Zero, Quaternion.Identity, new(scale), out _, out _));
}
