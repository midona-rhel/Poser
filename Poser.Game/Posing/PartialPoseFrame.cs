using System.Numerics;

namespace Poser.Game.Posing;

/// <summary>The native partial root before and after attachment to its posed parent.</summary>
internal readonly record struct PartialPoseFrame(Transform Before, Transform After)
{
    public bool IsInvertible => Before.Rotation.LengthSquared() > 1e-8f
        && After.Rotation.LengthSquared() > 1e-8f
        && After.Scale.X != 0 && After.Scale.Y != 0 && After.Scale.Z != 0;

    public Transform ToApply(Transform visible)
    {
        // Havok reparents by replacing the root's Qs transform. Undo that
        // replacement before diffing a visible target against the apply-pass
        // basis; otherwise the head's rotation/scale is applied twice.
        var local = Vector3.Transform(visible.Position - After.Position,
            Quaternion.Inverse(After.Rotation)) / After.Scale;
        return new Transform(
            Before.Position + Vector3.Transform(local * Before.Scale, Before.Rotation),
            Quaternion.Normalize(Before.Rotation * Quaternion.Inverse(After.Rotation) * visible.Rotation),
            visible.Scale / After.Scale * Before.Scale);
    }
}
