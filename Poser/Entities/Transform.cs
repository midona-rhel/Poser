using System;
using System.Numerics;

namespace Poser;

/// <summary>
/// Represents a 3D transform with position, rotation, and scale.
/// </summary>
public struct Transform
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public static readonly Transform Identity = new()
    {
        Position = Vector3.Zero,
        Rotation = Quaternion.Identity,
        Scale = Vector3.One
    };

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
    /// Decomposes a matrix into a transform (using Brio's robust method).
    /// Extracts scale from column vector lengths, avoiding Matrix4x4.Decompose instability.
    /// </summary>
    public static Transform FromMatrix(Matrix4x4 matrix)
    {
        // Position is directly from translation
        Vector3 position = matrix.Translation;

        // Scale is calculated from the length of each column vector
        Vector3 scale = new(
            new Vector3(matrix.M11, matrix.M12, matrix.M13).Length(),
            new Vector3(matrix.M21, matrix.M22, matrix.M23).Length(),
            new Vector3(matrix.M31, matrix.M32, matrix.M33).Length()
        );

        // Avoid division by zero
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
    /// Calculates the difference between this transform and another (like Brio's CalculateDiff).
    /// Returns a delta transform: this - other.
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

    public static Transform operator +(Transform a, Transform b)
    {
        return new Transform
        {
            Position = a.Position + b.Position,
            Rotation = a.Rotation * b.Rotation,
            Scale = a.Scale * b.Scale
        };
    }

    public static Transform operator -(Transform a, Transform b)
    {
        return new Transform
        {
            Position = a.Position - b.Position,
            Rotation = Quaternion.Inverse(b.Rotation) * a.Rotation,
            Scale = a.Scale / b.Scale
        };
    }

    public override bool Equals(object? obj)
    {
        if (obj is Transform other)
        {
            return Position == other.Position &&
                   Rotation == other.Rotation &&
                   Scale == other.Scale;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Position, Rotation, Scale);
    }

    public static bool operator ==(Transform left, Transform right) => left.Equals(right);
    public static bool operator !=(Transform left, Transform right) => !left.Equals(right);
}
