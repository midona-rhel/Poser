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
    /// Decomposes a matrix into a transform.
    /// </summary>
    public static Transform FromMatrix(Matrix4x4 matrix)
    {
        Matrix4x4.Decompose(matrix, out var scale, out var rotation, out var translation);
        return new Transform(translation, rotation, scale);
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
