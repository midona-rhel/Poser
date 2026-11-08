using System.Numerics;

namespace Poser.Domain.Posing;

/// <summary>A held position and rotation, plus the bone edits already included when they were captured.</summary>
public readonly record struct HeldTarget(
    Vector3 Target,
    Quaternion Rotation,
    Vector3 Translation,
    Quaternion RotationDelta)
{
    public bool Unseated { get; init; }
}
