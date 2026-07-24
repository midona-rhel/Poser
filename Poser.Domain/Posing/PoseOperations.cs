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

    public static PoseDelta Mirror(PoseDelta delta) =>
        new(
            -delta.Position,
            Quaternion.Normalize(Quaternion.Conjugate(delta.Rotation)),
            -delta.Scale);
}
