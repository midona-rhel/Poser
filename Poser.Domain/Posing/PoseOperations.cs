using System.Numerics;

namespace Poser.Domain.Posing;

/// <summary>Deterministic edits over immutable manual pose layers.</summary>
public static class PoseOperations
{
    public static BonePose Reset(BonePose pose) =>
        new(version: checked(pose.Version + 1));

    /// <summary>
    /// Counterpart-frame-aware sagittal transfer of authored layers.
    /// Counterpart bones' bind/animated baselines
    /// can differ by ~180°, so a raw component flip turns a forward arm into
    /// a backward one. Each delta is evaluated relative to its source bone's
    /// frozen animated baseline, reflected through the sagittal plane, and
    /// rebased into the destination baseline's frame:
    ///   d' = B_dst⁻¹ · M(B_src) · M(d) · M(B_src)⁻¹ · B_dst
    /// The conversion preserves post-multiply composition, so converting
    /// layer-by-layer equals converting the composed total. Positions
    /// reflect laterally in the model frame; scale transfers unchanged.
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
        var mirroredSource = MirrorRotation(sourceBaseline);
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

    /// <summary>Model-space sagittal mirror of a rotation: reflection across
    /// the YZ plane, `(x, −y, −z, w)` — the Brio MirrorBoneTransform plane.
    /// (Ktisis FlipPose uses the z-plane form but composes it with a 180°
    /// root yaw, which nets out to this same reflection.)</summary>
    public static Quaternion MirrorRotation(Quaternion value) =>
        new(value.X, -value.Y, -value.Z, value.W);

    /// <summary>Model-space sagittal mirror of a translation (lateral X).</summary>
    public static Vector3 MirrorPosition(Vector3 value) =>
        new(-value.X, value.Y, value.Z);
}
