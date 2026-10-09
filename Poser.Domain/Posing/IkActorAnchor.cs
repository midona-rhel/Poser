using System.Numerics;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

/// <summary>Target in the captured solve frame, carried by the first ancestor outside the chain.</summary>
public readonly record struct IkActorAnchor(Vector3 Position, Quaternion Rotation,
    Vector3 AuthoredPosition, Quaternion AuthoredRotation)
{
    public string? ParentName { get; init; }
    public int ParentPartial { get; init; }
    public PoseTransform? ParentTransform { get; init; }
    public bool IsValid => TransformMath.IsFinite(Position) && TransformMath.IsFinite(AuthoredPosition)
        && TransformMath.IsFinite(Rotation) && Rotation.LengthSquared() > 1e-8f
        && TransformMath.IsFinite(AuthoredRotation) && AuthoredRotation.LengthSquared() > 1e-8f
        && (ParentName == null ? ParentTransform == null : ParentPartial >= 0 && ParentTransform is { IsValid: true });

    public Matrix4x4 FrameDelta(PoseTransform? currentParent)
    {
        static Matrix4x4 Matrix(PoseTransform value) => Matrix4x4.CreateScale(value.Scale)
            * Matrix4x4.CreateFromQuaternion(value.Rotation) * Matrix4x4.CreateTranslation(value.Position);
        return ParentTransform is { } captured && currentParent is { } current
            && Matrix4x4.Invert(Matrix(captured), out var inverse)
                ? inverse * Matrix(current) : Matrix4x4.Identity;
    }

    public Vector3 ToReferenceDelta(Vector3 step, PoseTransform? currentParent) =>
        Matrix4x4.Invert(FrameDelta(currentParent), out var inverse)
            ? Vector3.TransformNormal(step, inverse) : Vector3.Zero;

    public (Vector3 Position, Quaternion Rotation) Resolve(Vector3 authoredPosition, Quaternion authoredRotation,
        PoseTransform? currentParent = null)
    {
        var turn = ParentTransform is { } captured && currentParent is { } current
            ? Quaternion.Normalize(current.Rotation * Quaternion.Inverse(captured.Rotation)) : Quaternion.Identity;
        return (Vector3.Transform(Position + authoredPosition - AuthoredPosition, FrameDelta(currentParent)),
            Quaternion.Normalize(turn * Rotation * Quaternion.Inverse(AuthoredRotation) * authoredRotation));
    }
}
