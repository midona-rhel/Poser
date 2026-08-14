using System.Globalization;
using System.Numerics;
using Poser.Application.Transforms;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using LegacyLayer = Poser.Core.BonePoseTransformInfo;

namespace Poser.Game.Transforms;

/// <summary>
/// Serialized conversion seam from the transitional runtime stack shape to
/// the pointer-free transform-port state.
/// </summary>
internal static class LegacyPoseStackConverter
{
    public static TransformPortResult Convert(
        TransformTargetId target,
        PoseTransform transform,
        Quaternion animatedBaselineRotation,
        IReadOnlyList<LegacyLayer> stacks)
    {
        if (target.Kind != TransformTargetKind.Bone || target.Bone is null)
            return TransformPortResult.Fail(
                TransformPortStatus.IdentityMismatch,
                "Malformed bone transform target.");

        var layers = new List<PoseLayer>(stacks.Count);
        for (var index = 0; index < stacks.Count; index++)
        {
            var stack = stacks[index];
            // Named producer layers remain runtime-owned and are not gesture history.
            if (stack.Layer != null)
                continue;

            var propagation = stack.PropagateComponents;
            if (!TransformComponentsPolicy.IsDefined(propagation))
                return TransformPortResult.Fail(
                    TransformPortStatus.Rejected,
                    $"Transform target {target} stack {index} rejected propagation mask " +
                    $"0x{unchecked((uint)(int)stack.PropagateComponents):X8}.");

            var delta = new PoseDelta(
                stack.Transform.Position,
                stack.Transform.Rotation,
                stack.Transform.Scale);
            if (!delta.IsValid)
                return InvalidDelta(target, index, delta);

            PoseDelta normalized;
            try
            {
                normalized = delta.Normalized();
            }
            catch (ArgumentException)
            {
                return InvalidDelta(target, index, delta);
            }

            layers.Add(new PoseLayer(
                new PoseLayerId(
                    PoseLayerKind.Manual,
                    $"legacy-{index}"),
                propagation,
                normalized));
        }

        BonePose pose;
        try
        {
            pose = new BonePose(layers);
        }
        catch (ArgumentException exception)
        {
            return TransformPortResult.Fail(
                TransformPortStatus.Rejected,
                $"Transform target {target} rejected its converted pose stack: " +
                exception.Message);
        }

        return TransformPortResult.Ok(new TransformTargetState(
            target,
            transform,
            pose,
            layers.Count > 0)
        {
            AnimatedBaselineRotation = animatedBaselineRotation,
        });
    }

    private static TransformPortResult InvalidDelta(
        TransformTargetId target,
        int index,
        PoseDelta delta) =>
        TransformPortResult.Fail(
            TransformPortStatus.InvalidTransform,
            $"Transform target {target} stack {index} rejected pose delta " +
            $"Position={Format(delta.Position)}, " +
            $"Rotation={Format(delta.Rotation)}, " +
            $"Scale={Format(delta.Scale)}.");

    private static string Format(Vector3 value) =>
        $"({Format(value.X)}, {Format(value.Y)}, {Format(value.Z)})";

    private static string Format(Quaternion value) =>
        $"({Format(value.X)}, {Format(value.Y)}, {Format(value.Z)}, {Format(value.W)})";

    private static string Format(float value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}
