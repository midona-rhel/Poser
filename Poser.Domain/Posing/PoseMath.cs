using System;
using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

/// <summary>
/// Shared pose and orientation calculations without game-memory access.
/// </summary>
public static class PoseMath
{
    private const float RadiansToDegrees = 180f / MathF.PI;
    private const float DegreesToRadians = MathF.PI / 180f;

    /// <summary>
    /// Creates a rotation that points local +Z along the supplied direction.
    /// Returns identity when the direction is nonfinite or shorter than 0.0001.
    /// </summary>
    public static Quaternion AlignZTo(Vector3 direction)
    {
        float length = direction.Length();
        if (length < 0.0001f || !float.IsFinite(length))
            return Quaternion.Identity;
        var z = direction / length;
        var reference = MathF.Abs(z.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
        var x = Vector3.Normalize(Vector3.Cross(reference, z));
        var y = Vector3.Cross(z, x);
        var m = new Matrix4x4(
            x.X, x.Y, x.Z, 0f,
            y.X, y.Y, y.Z, 0f,
            z.X, z.Y, z.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(m));
    }

    /// <summary>
    /// Combines parent and local position, rotation and scale. This component
    /// representation does not preserve shear from arbitrary matrix composition.
    /// </summary>
    public static Transform Compose(in Transform parent, in Transform local) => new()
    {
        Position = parent.Position + Vector3.Transform(local.Position * parent.Scale, parent.Rotation),
        Rotation = Quaternion.Normalize(parent.Rotation * local.Rotation),
        Scale = parent.Scale * local.Scale,
    };

    /// <summary>
    /// Weights position and rotation changes. Scale is returned as an additive
    /// change from one. Weights outside 0–1 extrapolate rather than clamp.
    /// </summary>
    public static Transform WeightExpressionDelta(Transform delta, float weight) => new()
    {
        Position = delta.Position * weight,
        Rotation = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, delta.Rotation, weight)),
        Scale = (delta.Scale - Vector3.One) * weight,
    };

    // Ktisis ExpressionController.ApplyBlend divides out (parent * previous)
    // before multiplying (parent * next). The parents cancel for rotation:
    // a fresh delta post-multiplies the bone, while position uses the parent.
    public static Transform ProjectExpressionDelta(Transform delta, Quaternion parent) => delta with
    {
        Position = Vector3.Transform(delta.Position, parent),
    };
    /// <summary>
    /// Swaps an _l or _r suffix, or returns null for other names.
    /// This computes a name; it does not check whether that bone exists.
    /// </summary>
    public static string? GetMirrorBoneName(string boneName)
    {
        if (boneName.EndsWith("_r"))
        {
            return string.Concat(boneName.AsSpan(0, boneName.Length - 2), "_l");
        }

        if (boneName.EndsWith("_l"))
        {
            return string.Concat(boneName.AsSpan(0, boneName.Length - 2), "_r");
        }

        return null;
    }

    /// <summary>
    /// Converts a rotation to euler angles in degrees around the labeled X/Y/Z axes.
    /// Lossy at gimbal poles — do not round-trip through this for accumulation;
    /// use quaternion composition instead.
    /// </summary>
    public static Vector3 QuaternionToEuler(Quaternion r)
    {
        float yaw = MathF.Atan2(2.0f * (r.Y * r.W + r.X * r.Z), 1.0f - 2.0f * (r.X * r.X + r.Y * r.Y));
        float pitch = MathF.Asin(Math.Clamp(2.0f * (r.X * r.W - r.Y * r.Z), -1f, 1f));
        float roll = MathF.Atan2(2.0f * (r.X * r.Y + r.Z * r.W), 1.0f - 2.0f * (r.X * r.X + r.Z * r.Z));

        // CreateFromYawPitchRoll names its arguments by operation, not coordinate
        // order: yaw is Y, pitch is X, and roll is Z.
        return new Vector3(pitch, yaw, roll) * RadiansToDegrees;
    }

    /// <summary>
    /// Converts labeled X/Y/Z euler angles in degrees to a normalized quaternion.
    /// </summary>
    public static Quaternion EulerToQuaternion(Vector3 euler)
    {
        euler *= DegreesToRadians;
        var quaternion = Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z);
        return Quaternion.Normalize(quaternion);
    }
}
