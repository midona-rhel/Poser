using System.Numerics;

namespace Poser.Domain.Transforms;

public enum TransformSpace
{
    Local,
    World,
}

public enum TransformOperation
{
    Translate,
    Rotate,
    Scale,
    Universal,
}

public enum PivotMode
{
    PerTarget,
    Primary,
    SelectionCenter,
    Custom,
}

public enum TransformDeltaMode
{
    Direct,
    Mirrored,
}

/// <summary>Validated absolute transform.</summary>
public readonly record struct PoseTransform(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale)
{
    public const float MaxAbsoluteScale = 1000f;
    public const float MinAbsoluteScale = 0.00001f;

    public static PoseTransform Identity { get; } =
        new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public static bool TryCreate(
        Vector3 position,
        Quaternion rotation,
        Vector3 scale,
        out PoseTransform transform,
        out string? error)
    {
        transform = default;
        if (!TransformMath.IsFinite(position) ||
            !TransformMath.IsFinite(rotation) ||
            !TransformMath.IsFinite(scale))
        {
            error = "Transform contains NaN or infinity.";
            return false;
        }

        var lengthSquared = rotation.LengthSquared();
        if (lengthSquared < 0.000001f)
        {
            error = "Transform rotation is zero.";
            return false;
        }

        if (!IsSafeScale(scale))
        {
            error = "Transform scale is outside the safe domain bound.";
            return false;
        }

        transform = new PoseTransform(
            position,
            Quaternion.Normalize(rotation),
            scale);
        error = null;
        return true;
    }

    public static PoseTransform CreateChecked(
        Vector3 position,
        Quaternion rotation,
        Vector3 scale)
    {
        if (!TryCreate(position, rotation, scale, out var value, out var error))
            throw new ArgumentOutOfRangeException(nameof(scale), error);
        return value;
    }

    private static bool IsSafeScale(Vector3 scale)
    {
        static bool Component(float value) =>
            MathF.Abs(value) >= MinAbsoluteScale &&
            MathF.Abs(value) <= MaxAbsoluteScale;
        return Component(scale.X) && Component(scale.Y) && Component(scale.Z);
    }
}

/// <summary>Gesture-space delta. Scale is multiplicative.</summary>
public readonly record struct TransformDelta(
    Vector3 Translation,
    Quaternion Rotation,
    Vector3 ScaleFactor)
{
    public static TransformDelta Identity { get; } =
        new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public bool IsValid =>
        TransformMath.IsFinite(Translation) &&
        TransformMath.IsFinite(Rotation) &&
        Rotation.LengthSquared() >= 0.000001f &&
        TransformMath.IsFinite(ScaleFactor) &&
        MathF.Abs(ScaleFactor.X) >= PoseTransform.MinAbsoluteScale &&
        MathF.Abs(ScaleFactor.Y) >= PoseTransform.MinAbsoluteScale &&
        MathF.Abs(ScaleFactor.Z) >= PoseTransform.MinAbsoluteScale;

    public TransformDelta Normalized() =>
        this with { Rotation = Quaternion.Normalize(Rotation) };
}

public static class TransformMath
{
    // Sagittal mirror for symmetry gestures: lateral X negated, rotation via
    // the X-plane mirror conjugation (−x, y, z, −w) — not the full conjugate,
    // which inverts the rotation instead of reflecting it.
    public static TransformDelta Mirror(TransformDelta delta) =>
        new(
            new Vector3(
                -delta.Translation.X,
                delta.Translation.Y,
                delta.Translation.Z),
            Quaternion.Normalize(new Quaternion(
                -delta.Rotation.X,
                delta.Rotation.Y,
                delta.Rotation.Z,
                -delta.Rotation.W)),
            delta.ScaleFactor);

    public static PoseTransform Apply(
        PoseTransform baseline,
        TransformDelta delta,
        TransformSpace space,
        Vector3 pivot,
        bool rotatePosition)
    {
        if (!delta.IsValid)
            throw new ArgumentOutOfRangeException(nameof(delta));

        delta = delta.Normalized();
        var rotation = space == TransformSpace.Local
            ? Quaternion.Normalize(baseline.Rotation * delta.Rotation)
            : Quaternion.Normalize(delta.Rotation * baseline.Rotation);

        var position = baseline.Position + delta.Translation;
        if (rotatePosition)
        {
            position = pivot +
                Vector3.Transform(baseline.Position - pivot, delta.Rotation) +
                delta.Translation;
        }

        var scale = baseline.Scale * delta.ScaleFactor;
        return PoseTransform.CreateChecked(position, rotation, scale);
    }

    public static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    public static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);
}
