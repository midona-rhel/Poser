using System.Numerics;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests;

public sealed class TransformPolicyBaselineTests
{
    public static IEnumerable<object[]> DefinedMasks()
    {
        yield return [TransformComponents.None];
        yield return [TransformComponents.Position];
        yield return [TransformComponents.Rotation];
        yield return [TransformComponents.Position | TransformComponents.Rotation];
        yield return [TransformComponents.Scale];
        yield return [TransformComponents.Position | TransformComponents.Scale];
        yield return [TransformComponents.Rotation | TransformComponents.Scale];
        yield return [TransformComponents.All];
    }

    [Theory]
    [MemberData(nameof(DefinedMasks))]
    public void Defined_masks_are_representable_by_the_current_domain_type(
        TransformComponents mask)
    {
        Assert.True(TransformComponentsPolicy.IsDefined(mask));
        TransformComponentsPolicy.Validate(mask);
        var layer = new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "baseline"),
            mask,
            ValidDelta());

        Assert.Equal(mask, layer.Propagation);
        Assert.True(layer.IsValid);
    }

    [Fact]
    public void Unknown_mask_bits_are_rejected_explicitly()
    {
        var layer = new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "unknown"),
            (TransformComponents)8,
            ValidDelta());

        Assert.False(TransformComponentsPolicy.IsDefined(
            (TransformComponents)8));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransformComponentsPolicy.Validate((TransformComponents)8));
        Assert.False(layer.IsValid);
        Assert.Throws<ArgumentException>(() => new BonePose([layer]));
    }

    [Fact]
    public void None_is_a_valid_no_propagation_layer()
    {
        var layer = new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "none"),
            TransformComponents.None,
            ValidDelta());

        Assert.True(TransformComponentsPolicy.IsDefined(
            TransformComponents.None));
        Assert.True(layer.IsValid);
        Assert.Single(new BonePose([layer]).Layers);
    }

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
    public void Invalid_delta_is_rejected_without_changing_the_frozen_baseline()
    {
        var baseline = PoseTransform.Identity;
        var invalid = new TransformDelta(
            new Vector3(float.PositiveInfinity, 0, 0),
            Quaternion.Identity,
            Vector3.One);

        Assert.False(invalid.IsValid);
        Assert.Throws<ArgumentOutOfRangeException>(() => invalid.Normalized());
        Assert.Throws<ArgumentOutOfRangeException>(() => TransformMath.Apply(
            baseline,
            invalid,
            TransformSpace.Local,
            Vector3.Zero,
            rotatePosition: false));
        Assert.Equal(PoseTransform.Identity, baseline);
    }

    [Fact]
    public void Direct_transform_helpers_reject_malformed_delta_and_baselines()
    {
        var zeroRotation = new TransformDelta(
            Vector3.Zero,
            Quaternion.Zero,
            Vector3.One);
        var nonFiniteDelta = new TransformDelta(
            new Vector3(float.NaN, 0, 0),
            Quaternion.Identity,
            Vector3.One);
        var nonFiniteBaseline = new Quaternion(
            float.PositiveInfinity,
            0,
            0,
            1);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransformMath.Mirror(zeroRotation));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TransformMath.Mirror(nonFiniteDelta));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TransformMath.MirrorRebased(
                TransformDelta.Identity,
                nonFiniteBaseline,
                Quaternion.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TransformMath.LinkRebased(
                TransformDelta.Identity,
                Quaternion.Identity,
                Quaternion.Zero));
    }

    [Fact]
    public void Direct_transform_helpers_robustly_normalize_huge_rotations()
    {
        var hugeDelta = new TransformDelta(
            new Vector3(1, 2, 3),
            new Quaternion(float.MaxValue, 1, -2, 3),
            Vector3.One);
        var hugeBaseline = new Quaternion(float.MaxValue, -4, 5, 6);
        var destinationBaseline = new Quaternion(-7, float.MaxValue, 8, 9);

        var mirrored = TransformMath.Mirror(hugeDelta);
        var rebased = TransformMath.MirrorRebased(
            hugeDelta,
            hugeBaseline,
            destinationBaseline);
        var linked = TransformMath.LinkRebased(
            hugeDelta,
            hugeBaseline,
            destinationBaseline);

        Assert.True(mirrored.IsValid);
        Assert.True(rebased.IsValid);
        Assert.True(linked.IsValid);
        Assert.Equal(hugeDelta.ScaleFactor, mirrored.ScaleFactor);
        Assert.Equal(hugeDelta.ScaleFactor, rebased.ScaleFactor);
        Assert.Equal(hugeDelta.ScaleFactor, linked.ScaleFactor);
    }

    [Fact]
    public void Direct_pose_mirror_rejects_malformed_inputs_and_normalizes_huge_values()
    {
        var nonFiniteDelta = new PoseDelta(
            new Vector3(float.NaN, 0, 0),
            Quaternion.Identity,
            new Vector3(1, 2, 3));
        var hugeDelta = new PoseDelta(
            new Vector3(1, 2, 3),
            new Quaternion(float.MaxValue, 1, -2, 3),
            new Vector3(4, 5, 6));
        var hugeSource = new Quaternion(float.MaxValue, -4, 5, 6);
        var hugeDestination = new Quaternion(-7, float.MaxValue, 8, 9);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PoseOperations.MirrorRebased(
                nonFiniteDelta,
                Quaternion.Identity,
                Quaternion.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PoseOperations.MirrorRebased(
                PoseDelta.Identity,
                new Quaternion(float.PositiveInfinity, 0, 0, 1),
                Quaternion.Identity));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PoseOperations.MirrorRebased(
                PoseDelta.Identity,
                Quaternion.Identity,
                Quaternion.Zero));

        var result = PoseOperations.MirrorRebased(
            hugeDelta,
            hugeSource,
            hugeDestination);

        Assert.True(result.IsValid);
        Assert.Equal(hugeDelta.Scale, result.Scale);
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

    [Fact]
    public void Immutable_bone_pose_replacement_does_not_mutate_the_original()
    {
        var original = new BonePose();
        var replaced = original.Replace(new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "baseline"),
            TransformComponents.All,
            new PoseDelta(
                new Vector3(2, 0, 0),
                Quaternion.Identity,
                Vector3.Zero)));

        Assert.Equal(0UL, original.Version);
        Assert.Empty(original.Layers);
        Assert.Equal(1UL, replaced.Version);
        Assert.Single(replaced.Layers);
    }

    private static PoseDelta ValidDelta() => new(
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.Zero);
}
