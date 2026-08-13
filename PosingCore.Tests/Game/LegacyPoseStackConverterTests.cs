using System;
using System.Numerics;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Game.Transforms;
using DomainComponents = Poser.Domain.Posing.TransformComponents;
using LegacyComponents = Poser.Core.TransformComponents;

namespace Poser.Tests.Game;

public sealed class LegacyPoseStackConverterTests
{
    public static TheoryData<int> DefinedMasks => new()
    {
        0, 1, 2, 3, 4, 5, 6, 7,
    };

    [Theory]
    [MemberData(nameof(DefinedMasks))]
    public void Defined_mask_round_trips_without_dropping_the_layer(int mask)
    {
        var target = BoneTarget();
        var stacks = new[]
        {
            Layer((LegacyComponents)mask),
        };

        var result = LegacyPoseStackConverter.Convert(
            target,
            PoseTransform.Identity,
            Quaternion.Identity,
            stacks);

        Assert.True(result.Success, result.Detail);
        var layer = Assert.Single(result.State!.Pose.Layers);
        Assert.Equal((DomainComponents)mask, layer.Propagation);
        Assert.Equal("legacy-0", layer.Id.Name);
    }

    [Fact]
    public void Unknown_mask_returns_rejected_with_exact_target_and_stack_context()
    {
        var target = BoneTarget();
        var stacks = new[]
        {
            Layer(LegacyComponents.All, layer: "expression"),
            Layer((LegacyComponents)8),
        };
        TransformPortResult result = default;

        var exception = Record.Exception(() => result =
            LegacyPoseStackConverter.Convert(
                target,
                PoseTransform.Identity,
                Quaternion.Identity,
                stacks));

        Assert.Null(exception);
        Assert.Equal(TransformPortStatus.Rejected, result.Status);
        Assert.Null(result.State);
        Assert.Contains(target.ToString(), result.Detail!, StringComparison.Ordinal);
        Assert.Contains("stack 1", result.Detail!, StringComparison.Ordinal);
        Assert.Contains("0x00000008", result.Detail!, StringComparison.Ordinal);
        Assert.Equal((LegacyComponents)8, stacks[1].PropagateComponents);
    }

    [Fact]
    public void Nonfinite_delta_returns_invalid_transform_with_rejected_delta_context()
    {
        var target = BoneTarget();
        var stacks = new[]
        {
            Layer(LegacyComponents.All, layer: "gaze"),
            Layer(
                LegacyComponents.None,
                new Transform(
                    new Vector3(float.NaN, 2, 3),
                    Quaternion.Identity,
                    new Vector3(4, 5, 6))),
        };
        TransformPortResult result = default;

        var exception = Record.Exception(() => result =
            LegacyPoseStackConverter.Convert(
                target,
                PoseTransform.Identity,
                Quaternion.Identity,
                stacks));

        Assert.Null(exception);
        Assert.Equal(TransformPortStatus.InvalidTransform, result.Status);
        Assert.Null(result.State);
        Assert.Contains(target.ToString(), result.Detail!, StringComparison.Ordinal);
        Assert.Contains("stack 1", result.Detail!, StringComparison.Ordinal);
        Assert.Contains("Position=(NaN, 2, 3)", result.Detail!, StringComparison.Ordinal);
        Assert.Contains("Rotation=(0, 0, 0, 1)", result.Detail!, StringComparison.Ordinal);
        Assert.Contains("Scale=(4, 5, 6)", result.Detail!, StringComparison.Ordinal);
        Assert.True(float.IsNaN(stacks[1].Transform.Position.X));
    }

    [Fact]
    public void Zero_rotation_delta_returns_invalid_transform_without_throwing_or_state()
    {
        var target = BoneTarget();
        var stacks = new[]
        {
            Layer(
                LegacyComponents.Rotation,
                new Transform(
                    Vector3.Zero,
                    Quaternion.Zero,
                    Vector3.Zero)),
        };
        TransformPortResult result = default;

        var exception = Record.Exception(() => result =
            LegacyPoseStackConverter.Convert(
                target,
                PoseTransform.Identity,
                Quaternion.Identity,
                stacks));

        Assert.Null(exception);
        Assert.Equal(TransformPortStatus.InvalidTransform, result.Status);
        Assert.Null(result.State);
        Assert.Contains(target.ToString(), result.Detail!, StringComparison.Ordinal);
        Assert.Contains("stack 0", result.Detail!, StringComparison.Ordinal);
        Assert.Contains("Rotation=(0, 0, 0, 0)", result.Detail!, StringComparison.Ordinal);
        Assert.Equal(Quaternion.Zero, stacks[0].Transform.Rotation);
    }

    private static BonePoseTransformInfo Layer(
        LegacyComponents components,
        Transform? transform = null,
        string? layer = null) =>
        new(
            components,
            transform ?? new Transform(
                new Vector3(1, 2, 3),
                new Quaternion(0, 0, 0, 2),
                new Vector3(4, 5, 6)),
            layer);

    private static TransformTargetId BoneTarget()
    {
        var actor = new ActorId(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            3);
        return TransformTargetId.ForBone(new BoneId(
            new SkeletonId(actor, PoseSlot.OffHand, 5),
            PartialId: 2,
            BoneIndex: 7,
            CanonicalName: "j_test"));
    }
}
