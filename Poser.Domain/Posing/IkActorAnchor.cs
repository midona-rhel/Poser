using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

/// <summary>Actor-model target and the handle edits already included at capture.</summary>
public readonly record struct IkActorAnchor(Vector3 Position, Quaternion Rotation,
    Vector3 AuthoredPosition, Quaternion AuthoredRotation)
{
    public bool IsValid => TransformMath.IsFinite(Position) && TransformMath.IsFinite(AuthoredPosition)
        && TransformMath.IsFinite(Rotation) && Rotation.LengthSquared() > 1e-8f
        && TransformMath.IsFinite(AuthoredRotation) && AuthoredRotation.LengthSquared() > 1e-8f;

    public (Vector3 Position, Quaternion Rotation) Resolve(Vector3 authoredPosition, Quaternion authoredRotation) =>
        (Position + authoredPosition - AuthoredPosition,
            Quaternion.Normalize(Rotation * Quaternion.Inverse(AuthoredRotation) * authoredRotation));
}
