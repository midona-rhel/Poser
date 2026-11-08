using System;
using System.Numerics;

namespace Poser.Domain.Transforms;

/// <summary>
/// Represents a 3D transform with position, rotation, and scale.
/// </summary>
public struct Transform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    /// <summary>
    /// No change when position and scale are added to an existing transform.
    /// </summary>
    public static readonly Transform Zero = new()
    {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.Zero
    };

    /// <summary>
    /// No translation, no rotation, and unchanged size.
    /// </summary>
    public static readonly Transform Identity = new()
    {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One
    };

    /// <summary>Copies the pose's position, rotation, and scale.</summary>
    public static Transform FromPose(Poser.Domain.Transforms.PoseTransform value) =>
        new(value.Position, value.Rotation, value.Scale);

    public Transform()
    {
        Position = Vector3.Zero;
        Rotation = Quaternion.Identity;
        Scale = Vector3.One;
    }

    public Transform(Vector3 position, Quaternion rotation, Vector3 scale)
    {
        Position = position;
        Rotation = rotation;
        Scale = scale;
    }

    public Transform(Vector3 position) : this()
    {
        Position = position;
    }

    /// <summary>
    /// Creates a model matrix from this transform.
    /// </summary>
    public Matrix4x4 ToMatrix()
    {
        return Matrix4x4.CreateScale(Scale) *
               Matrix4x4.CreateFromQuaternion(Rotation) *
               Matrix4x4.CreateTranslation(Position);
    }

    /// <summary>
    /// Estimates rotation and scale from a matrix's three basis rows.
    /// This loses scale signs, replaces zero scale with 0.01, and cannot preserve shear.
    /// </summary>
    public static Transform FromMatrix(Matrix4x4 matrix)
    {
        Vector3 position = matrix.Translation;

        // Row lengths give magnitudes, not the original signed scale.
        Vector3 scale = new(
            new Vector3(matrix.M11, matrix.M12, matrix.M13).Length(),
            new Vector3(matrix.M21, matrix.M22, matrix.M23).Length(),
            new Vector3(matrix.M31, matrix.M32, matrix.M33).Length()
        );

        // This also changes the scale returned to the caller.
        scale.X = MathF.Abs(scale.X) < float.Epsilon ? 0.01f : scale.X;
        scale.Y = MathF.Abs(scale.Y) < float.Epsilon ? 0.01f : scale.Y;
        scale.Z = MathF.Abs(scale.Z) < float.Epsilon ? 0.01f : scale.Z;

        // Create normalized rotation matrix by dividing out scale
        Matrix4x4 rotationMatrix = new(
            matrix.M11 / scale.X, matrix.M12 / scale.X, matrix.M13 / scale.X, 0,
            matrix.M21 / scale.Y, matrix.M22 / scale.Y, matrix.M23 / scale.Y, 0,
            matrix.M31 / scale.Z, matrix.M32 / scale.Z, matrix.M33 / scale.Z, 0,
            0, 0, 0, 1
        );

        Quaternion rotation = Quaternion.CreateFromRotationMatrix(rotationMatrix);

        return new Transform(position, rotation, scale);
    }

    /// <summary>
    /// Subtracts the other position and scale and calculates the relative rotation.
    /// </summary>
    public Transform CalculateDiff(Transform other)
    {
        return new Transform
        {
            Position = Position - other.Position,
            Rotation = Quaternion.Normalize(Quaternion.Conjugate(other.Rotation) * Rotation),
            Scale = Scale - other.Scale
        };
    }

    private const float Epsilon = 0.0001f;

    /// <summary>
    /// Compares position and scale within a tolerance. The rotation comparison assumes
    /// unit quaternions, treating q and -q as the same orientation. Exact component
    /// equality also lets a default, zero quaternion compare equal to itself.
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is Transform other)
        {
            return Vector3.DistanceSquared(Position, other.Position) < Epsilon &&
                   (MathF.Abs(Quaternion.Dot(Rotation, other.Rotation)) > 1f - Epsilon || Rotation == other.Rotation) &&
                   Vector3.DistanceSquared(Scale, other.Scale) < Epsilon;
        }
        return false;
    }

    /// <summary>
    /// Hashes quantized components. This does not guarantee equal hashes for values
    /// that Equals accepts within its tolerance; avoid using Transform as a hash key.
    /// </summary>
    public override int GetHashCode()
    {
        static int RoundComponent(float v) => (int)(v * 1000);
        return HashCode.Combine(
            RoundComponent(Position.X), RoundComponent(Position.Y), RoundComponent(Position.Z),
            RoundComponent(Rotation.W),
            RoundComponent(Scale.X), RoundComponent(Scale.Y), RoundComponent(Scale.Z));
    }

    public static bool operator ==(Transform left, Transform right) => left.Equals(right);
    public static bool operator !=(Transform left, Transform right) => !left.Equals(right);
}
