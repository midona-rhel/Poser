using System.Numerics;

namespace Poser.Domain.Posing;

/// <summary>Deterministic edits over immutable manual pose layers.</summary>
public static class PoseOperations
{
    public static BonePose Reset(BonePose pose) =>
        new(version: checked(pose.Version + 1));

    public static BonePose Mirror(BonePose pose) =>
        new(
            pose.Layers.Select(layer => layer with
            {
                Delta = Mirror(layer.Delta),
            }),
            checked(pose.Version + 1));

    // Skeletons are symmetric across exactly one plane: the sagittal plane,
    // whose normal is the lateral X axis in the applied frame. Mirroring a
    // delta therefore negates ONLY the lateral position component and applies
    // the X-plane mirror conjugation (−x, y, z, −w) to the rotation — it is
    // NOT the full inversion (negate-everything / conjugate) that turns a
    // raised-forward arm into an unrelated pose on the paired bone. Scale is
    // unchanged by a reflection.
    public static PoseDelta Mirror(PoseDelta delta) =>
        new(
            new Vector3(-delta.Position.X, delta.Position.Y, delta.Position.Z),
            Quaternion.Normalize(new Quaternion(
                -delta.Rotation.X,
                delta.Rotation.Y,
                delta.Rotation.Z,
                -delta.Rotation.W)),
            delta.Scale);
}
