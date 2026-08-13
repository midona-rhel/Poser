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
    Custom,
}

public enum TransformDeltaMode
{
    Direct,
    Mirrored,
}

/// <summary>Absolute transform with finite-domain validation helpers.</summary>
public readonly record struct PoseTransform(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale)
{
    public const float MaxAbsoluteScale = 1000f;
    public const float MinAbsoluteScale = 0.00001f;

    public static PoseTransform Identity { get; } =
        new(Vector3.Zero, Quaternion.Identity, Vector3.One);

    public bool IsValid =>
        TransformMath.IsFinite(Position) &&
        TransformMath.IsValidRotation(Rotation) &&
        IsSafeScale(Scale);

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

        if (!TransformMath.IsValidRotation(rotation))
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
            TransformMath.NormalizeRotation(rotation),
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

    public PoseTransform Normalized()
    {
        if (!IsValid)
            throw new ArgumentOutOfRangeException(
                nameof(Rotation),
                "Transform is outside the finite, normalized domain.");
        return this with
        {
            Rotation = TransformMath.NormalizeRotation(Rotation),
        };
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
        TransformMath.IsValidRotation(Rotation) &&
        TransformMath.IsFinite(ScaleFactor) &&
        MathF.Abs(ScaleFactor.X) >= PoseTransform.MinAbsoluteScale &&
        MathF.Abs(ScaleFactor.Y) >= PoseTransform.MinAbsoluteScale &&
        MathF.Abs(ScaleFactor.Z) >= PoseTransform.MinAbsoluteScale;

    public TransformDelta Normalized()
    {
        if (!IsValid)
            throw new ArgumentOutOfRangeException(
                nameof(Rotation),
                "Transform delta is outside the finite domain.");
        return this with
        {
            Rotation = TransformMath.NormalizeRotation(Rotation),
        };
    }
}

public static class TransformMath
{
    // Sagittal mirror for model-frame symmetry deltas: lateral X negated and
    // rotation reflected across the YZ plane, (x, −y, −z, w) — the Brio
    // MirrorBoneTransform plane. (Ktisis FlipPose negates x/y instead, but
    // composes that with a 180° root yaw; the net reflection is the same
    // YZ plane. Poser mirrors per-pair without turning the root, so the
    // plane must be applied directly.)
    public static TransformDelta Mirror(TransformDelta delta) =>
        new(
            new Vector3(
                -delta.Translation.X,
                delta.Translation.Y,
                delta.Translation.Z),
            Quaternion.Normalize(MirrorRotation(delta.Rotation)),
            delta.ScaleFactor);

    /// <summary>
    /// Counterpart-frame-aware mirror for LOCAL-space symmetry deltas:
    /// the delta is reflected relative to the
    /// frozen primary baseline and rebased onto the partner's frozen
    /// baseline, so a partner whose bind orientation differs by ~180° still
    /// receives the anatomical mirror instead of a backward rotation.
    /// </summary>
    public static TransformDelta MirrorRebased(
        TransformDelta delta,
        Quaternion sourceBaseline,
        Quaternion destinationBaseline)
    {
        var mirroredSource = MirrorRotation(sourceBaseline);
        var rotation = Quaternion.Normalize(
            Quaternion.Inverse(destinationBaseline) *
            mirroredSource *
            MirrorRotation(delta.Rotation) *
            Quaternion.Inverse(mirroredSource) *
            destinationBaseline);
        return new TransformDelta(
            new Vector3(
                -delta.Translation.X,
                delta.Translation.Y,
                delta.Translation.Z),
            rotation,
            delta.ScaleFactor);
    }

    /// <summary>
    /// Same-local-motion transfer for Link symmetry partners: a world-frame
    /// rotation delta is carried into the source bone's local frame and
    /// re-expressed in the partner's, so the partner repeats the motion
    /// about its OWN axes instead of the primary's world axes. Translation
    /// and scale copy directly.
    /// </summary>
    public static TransformDelta LinkRebased(
        TransformDelta delta,
        Quaternion sourceBaseline,
        Quaternion destinationBaseline)
    {
        var rotation = Quaternion.Normalize(
            destinationBaseline *
            Quaternion.Inverse(sourceBaseline) *
            delta.Rotation *
            sourceBaseline *
            Quaternion.Inverse(destinationBaseline));
        return delta with { Rotation = rotation };
    }

    private static Quaternion MirrorRotation(Quaternion value) =>
        new(value.X, -value.Y, -value.Z, value.W);

    public static PoseTransform Apply(
        PoseTransform baseline,
        TransformDelta delta,
        TransformSpace space,
        Vector3 pivot,
        bool rotatePosition)
    {
        if (!baseline.IsValid)
            throw new ArgumentOutOfRangeException(nameof(baseline));
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

    public static bool IsValidRotation(Quaternion value)
    {
        if (!TryNormalizeRotation(value, out _))
            return false;
        return true;
    }

    public static Quaternion NormalizeRotation(Quaternion value)
    {
        if (!TryNormalizeRotation(value, out var normalized))
            throw new ArgumentOutOfRangeException(
                nameof(value),
                "Rotation must be finite and non-zero.");
        return normalized;
    }

    private static bool TryNormalizeRotation(
        Quaternion value,
        out Quaternion normalized)
    {
        normalized = default;
        if (!IsFinite(value))
            return false;

        var largest = MathF.Max(
            MathF.Abs(value.X),
            MathF.Max(
                MathF.Abs(value.Y),
                MathF.Max(MathF.Abs(value.Z), MathF.Abs(value.W))));
        if (largest <= 0f)
            return false;

        var scaled = new Quaternion(
            value.X / largest,
            value.Y / largest,
            value.Z / largest,
            value.W / largest);
        var scaledLength = MathF.Sqrt(scaled.LengthSquared());
        if (!float.IsFinite(scaledLength) || scaledLength <= 0f)
            return false;

        // Keep the existing minimum-length safety bound without allowing a
        // large finite quaternion to overflow while it is normalized.
        if (largest < 0.001f / scaledLength)
            return false;

        normalized = new Quaternion(
            scaled.X / scaledLength,
            scaled.Y / scaledLength,
            scaled.Z / scaledLength,
            scaled.W / scaledLength);
        return IsFinite(normalized);
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
