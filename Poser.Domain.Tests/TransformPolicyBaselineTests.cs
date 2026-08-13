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
        var layer = new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "baseline"),
            mask,
            ValidDelta());

        Assert.Equal(mask, layer.Propagation);
        Assert.Equal(TransformComponents.All & mask, mask);
    }

    [Fact]
    public void Current_layer_predicate_accepts_unknown_mask_bits()
    {
        var layer = new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "unknown"),
            (TransformComponents)8,
            ValidDelta());

        // Characterization of the accepted baseline: unknown bits are not
        // rejected yet. The target contract is represented by the skipped
        // marker below and will be enabled in the pure-Domain lane.
        Assert.True(layer.IsValid);
        Assert.NotNull(new BonePose([layer]));
    }

    [Fact(Skip = "Slice 1 characterization: current Domain accepts unknown propagation bits; unskip after typed mask validation is added.")]
    public void Slice1_unknown_mask_rejection_characterization()
    {
        var layer = new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "unknown"),
            (TransformComponents)8,
            ValidDelta());

        Assert.False(layer.IsValid);
        Assert.Throws<ArgumentException>(() => new BonePose([layer]));
    }

    [Fact(Skip = "Slice 1 characterization: current Domain rejects None layers; unskip after the pure contract correction.")]
    public void Slice1_None_layer_contract_characterization()
    {
        var layer = new PoseLayer(
            new PoseLayerId(PoseLayerKind.Manual, "none"),
            TransformComponents.None,
            ValidDelta());

        Assert.True(layer.IsValid);
        _ = new BonePose([layer]);
    }

    [Fact]
    public void Pose_transform_creation_rejects_non_finite_values_and_normalizes_rotation()
    {
        var accepted = PoseTransform.CreateChecked(
            new Vector3(1, 2, 3),
            new Quaternion(0, 0, 0, 2),
            Vector3.One);

        Assert.Equal(Quaternion.Identity, accepted.Rotation);
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
