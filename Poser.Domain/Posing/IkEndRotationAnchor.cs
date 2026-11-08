using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

/// <summary>
/// Rotations captured when the Two Joint end-rotation mode changes. Forearm
/// is the preceding bone (for example, a forearm or shin); End is the end
/// bone; Authored is the user's rotation at that time.
/// </summary>
public readonly record struct IkEndRotationAnchor(Quaternion Forearm, Quaternion End, Quaternion Authored)
{
    /// <summary>
    /// End-bone rotation relative to the held target, captured on the first solve
    /// after the mode switch. Null means it has not been captured yet.
    /// </summary>
    public Quaternion? HeldOffset { get; init; }

    public bool IsValid => Valid(Forearm) && Valid(End) && Valid(Authored)
        && (HeldOffset is not { } offset || Valid(offset));

    private static bool Valid(Quaternion value) =>
        TransformMath.IsFinite(value) && value.LengthSquared() > 1e-8f;

    /// <summary>
    /// Applies the user's rotation change since capture. A supplied forearm
    /// rotation also makes the end follow that bone's rotation change.
    /// </summary>
    public Quaternion Resolve(Quaternion authored, Quaternion? forearm)
    {
        var held = End * Quaternion.Inverse(Authored) * authored;
        return Quaternion.Normalize(forearm is { } turned
            ? turned * Quaternion.Inverse(Forearm) * held
            : held);
    }
}
