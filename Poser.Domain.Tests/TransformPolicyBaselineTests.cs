using System.Numerics;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests;

public sealed class TransformPolicyBaselineTests
{
    [Fact]
    public void Pose_transform_creation_rejects_non_finite_values_and_normalizes_rotation()
    {
        var accepted = PoseTransform.CreateChecked(
            new Vector3(1, 2, 3),
            new Quaternion(0, 0, 0, 2),
            Vector3.One);

        Assert.Equal(Quaternion.Identity, accepted.Rotation);
        Assert.True(accepted.IsValid);
        Assert.Equal(Quaternion.Identity, accepted.Normalized().Rotation);
        Assert.False(PoseTransform.TryCreate(
            new Vector3(float.NaN, 0, 0),
            Quaternion.Identity,
            Vector3.One,
            out _,
            out _));
        Assert.False(PoseTransform.TryCreate(
            Vector3.Zero,
            Quaternion.Zero,
            Vector3.One,
            out _,
            out _));
    }

    [Fact]
    public void Direct_pose_mirror_preserves_the_established_formula_and_additive_scale()
    {
        var delta = new PoseDelta(
            new Vector3(1, 2, 3),
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.4f),
            new Vector3(4, 5, 6));
        var sourceBaseline =
            Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.7f);
        var destinationBaseline =
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -0.3f);
        var normalizedSource = TransformMath.NormalizeRotation(sourceBaseline);
        var normalizedDestination =
            TransformMath.NormalizeRotation(destinationBaseline);
        var mirroredSource = PoseOperations.MirrorRotation(normalizedSource);
        var expectedRotation = TransformMath.NormalizeRotation(
            Quaternion.Inverse(normalizedDestination) *
            mirroredSource *
            PoseOperations.MirrorRotation(
                TransformMath.NormalizeRotation(delta.Rotation)) *
            Quaternion.Inverse(mirroredSource) *
            normalizedDestination);

        var result = PoseOperations.MirrorRebased(
            delta,
            sourceBaseline,
            destinationBaseline);

        Assert.Equal(new Vector3(-1, 2, 3), result.Position);
        Assert.Equal(expectedRotation.X, result.Rotation.X, 5);
        Assert.Equal(expectedRotation.Y, result.Rotation.Y, 5);
        Assert.Equal(expectedRotation.Z, result.Rotation.Z, 5);
        Assert.Equal(expectedRotation.W, result.Rotation.W, 5);
        Assert.Equal(delta.Scale, result.Scale);
    }

    [Fact]
    public void Bone_pose_stores_normalized_layers_and_invalid_replace_is_atomic()
    {
        var nonNormalized = new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "normalized"),
            TransformComponents.All,
            new PoseDelta(
                Vector3.One,
                new Quaternion(0, 0, 0, 2),
                Vector3.Zero));
        var original = new BonePose([nonNormalized]);
        var invalid = new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "invalid"),
            TransformComponents.All,
            new PoseDelta(
                Vector3.Zero,
                Quaternion.Zero,
                Vector3.Zero));

        Assert.Equal(1f, original.Layers[0].Delta.Rotation.Length(), 5);
        Assert.Throws<ArgumentException>(() => original.Replace(invalid));
        Assert.Single(original.Layers);
        Assert.Equal(0UL, original.Version);
    }
}
