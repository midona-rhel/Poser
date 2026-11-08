using System.Numerics;

namespace Poser.Domain.Posing;

/// <summary>
/// Creates edited copies of manual pose layers.
/// </summary>
public static class PoseOperations
{
    public static BonePose Reset(BonePose pose) =>
        new(version: checked(pose.Version + 1));

    /// <summary>
    /// Mirrors layers left-to-right across model X, accounting for the two bones'
    /// different starting rotations. A simple quaternion-component flip would
    /// ignore that difference. B_src and B_dst below are the captured starting
    /// rotations; M is reflection across the YZ plane:
    ///   d' = B_dst⁻¹ · M(B_src) · M(d) · M(B_src)⁻¹ · B_dst
    /// Position changes reflect across X; scale changes are copied unchanged.
    /// </summary>
    public static BonePose MirrorRebased(
        BonePose pose,
        Quaternion sourceBaseline,
        Quaternion destinationBaseline) =>
        new(
            pose.Layers.Select(layer => layer with
            {
                Delta = MirrorRebased(layer.Delta, sourceBaseline, destinationBaseline),
            }),
            checked(pose.Version + 1));

    public static PoseDelta MirrorRebased(
        PoseDelta delta,
        Quaternion sourceBaseline,
        Quaternion destinationBaseline)
    {
        delta = delta.Normalized();
        sourceBaseline = Transforms.TransformMath.NormalizeRotation(sourceBaseline);
        destinationBaseline =
            Transforms.TransformMath.NormalizeRotation(destinationBaseline);
        var mirroredSource = Transforms.TransformMath.NormalizeRotation(
            MirrorRotation(sourceBaseline));
        var rotation = Transforms.TransformMath.NormalizeRotation(
            Quaternion.Inverse(destinationBaseline) *
            mirroredSource *
            MirrorRotation(delta.Rotation) *
            Quaternion.Inverse(mirroredSource) *
            destinationBaseline);
        return new PoseDelta(
            MirrorPosition(delta.Position),
            rotation,
            delta.Scale);
    }

    /// <summary>
    /// Reflects a rotation across the model-space YZ plane by negating its
    /// quaternion Y and Z components. This is the left/right mirror plane.
    /// </summary>
    public static Quaternion MirrorRotation(Quaternion value) =>
        new(value.X, -value.Y, -value.Z, value.W);

    /// <summary>
    /// Reflects a model-space position across X, swapping left and right.
    /// </summary>
    public static Vector3 MirrorPosition(Vector3 value) =>
        new(-value.X, value.Y, value.Z);
}
