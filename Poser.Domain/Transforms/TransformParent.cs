using System.Numerics;
using Poser.Domain.Identity;

namespace Poser.Domain.Transforms;

/// <summary>Rigid following; size is authored on the child, never inherited.</summary>
public sealed record TransformParent(SelectionId Target, PoseTransform Offset)
{
    public static PoseTransform Local(PoseTransform world, PoseTransform parent) => new(
        Vector3.Transform(world.Position - parent.Position, Quaternion.Inverse(parent.Rotation)),
        Quaternion.Normalize(Quaternion.Inverse(parent.Rotation) * world.Rotation), world.Scale);

    public static PoseTransform World(PoseTransform local, PoseTransform parent) => new(
        parent.Position + Vector3.Transform(local.Position, parent.Rotation),
        Quaternion.Normalize(parent.Rotation * local.Rotation), local.Scale);
}
