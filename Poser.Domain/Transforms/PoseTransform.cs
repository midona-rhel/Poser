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

/// <summary>
/// Chooses the point around which selected targets rotate and scale.
/// </summary>
public enum PivotMode
{
    /// <summary>
    /// Rotate or scale each target around its own origin without moving that origin.
    /// </summary>
        PerTarget,

    /// <summary>Every target about the first selected one.</summary>
    Primary,

    /// <summary>Every target rotates and scales about the mean of the captured
    /// target positions. With one target this is the same as
    /// <see cref="PerTarget"/>.</summary>
    Centroid,

    /// <summary>
    /// Use a point captured when the drag begins, such as a parent bone position.
    /// </summary>
        Custom,
}

/// <summary>
/// Chooses whether scaling a group changes both object sizes and spacing, or only spacing.
/// </summary>
public enum GroupScaleMode
{
    /// <summary>Sizes and spacing: every member grows and moves out.</summary>
    SizesAndSpacing,

    /// <summary>Spacing only: members keep their size and move out.</summary>
    SpacingOnly,
}

public enum TransformDeltaMode
{
    Direct,
    Mirrored,
}

/// <summary>
/// Position, rotation, and scale with validation used by pose editing.
/// The scale limits below are application policy, not proof of the game's supported range.
/// </summary>
public readonly record struct PoseTransform(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale)
{
    public const float MaxAbsoluteScale = 1000f;
    // Appearance profiles can hide bones with scales around 1e-6.
    // This lower bound permits those values but still rejects zero and smaller scales.
    public const float MinAbsoluteScale = 0.00000001f;

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

/// <summary>
/// Change applied during a drag. Position is added, rotation is composed, and scale is multiplied.
/// </summary>
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
    /// <summary>
    /// Per-component tolerance for detecting an unchanged additive pose delta.
    /// </summary>
    public const float ApproximateSameTolerance = 0.000001f;

    /// <summary>Squared-length bound on position and scale for
    /// <see cref="IsIdentityDeltaByLength"/>.</summary>
    public const float IdentityDeltaLengthSquaredTolerance = 1e-12f;

    /// <summary>Minimum |dot(rotation, identity)| for
    /// <see cref="IsIdentityDeltaByLength"/>.</summary>
    public const float IdentityDeltaMinRotationDot = 0.999999f;

    /// <summary>
    /// Checks whether additive position and scale changes are near zero and rotation W is near 1 or -1.
    /// This assumes a normalized rotation; it does not validate that assumption.
    /// </summary>
    public static bool IsApproximatelyIdentityDelta(
        Transform delta,
        float tolerance = ApproximateSameTolerance) =>
        MathF.Abs(delta.Position.X) < tolerance &&
        MathF.Abs(delta.Position.Y) < tolerance &&
        MathF.Abs(delta.Position.Z) < tolerance &&
        MathF.Abs(delta.Scale.X) < tolerance &&
        MathF.Abs(delta.Scale.Y) < tolerance &&
        MathF.Abs(delta.Scale.Z) < tolerance &&
        MathF.Abs(MathF.Abs(delta.Rotation.W) - 1f) < tolerance;

    /// <summary>
    /// Checks the total position and scale changes against squared-length tolerances.
    /// The rotation test assumes a unit quaternion and also accepts W magnitudes above 1.
    /// </summary>
    public static bool IsIdentityDeltaByLength(Transform delta) =>
        delta.Position.LengthSquared() < IdentityDeltaLengthSquaredTolerance &&
        delta.Scale.LengthSquared() < IdentityDeltaLengthSquaredTolerance &&
        MathF.Abs(Quaternion.Dot(delta.Rotation, Quaternion.Identity)) > IdentityDeltaMinRotationDot;

    // Reflect a model-frame delta across the YZ plane: negate X and reflect
    // the quaternion as (x, -y, -z, w).
    public static TransformDelta Mirror(TransformDelta delta)
    {
        delta = delta.Normalized();
        return new TransformDelta(
            new Vector3(
                -delta.Translation.X,
                delta.Translation.Y,
                delta.Translation.Z),
            NormalizeRotation(MirrorRotation(delta.Rotation)),
            delta.ScaleFactor);
    }

    /// <summary>
    /// Mirrors a rotation while accounting for the two bones' different starting orientations.
    /// Translation flips across X; scale factors remain unchanged.
    /// </summary>
    public static TransformDelta MirrorRebased(
        TransformDelta delta,
        Quaternion sourceBaseline,
        Quaternion destinationBaseline)
    {
        delta = delta.Normalized();
        sourceBaseline = NormalizeRotation(sourceBaseline);
        destinationBaseline = NormalizeRotation(destinationBaseline);
        var mirroredSource = NormalizeRotation(MirrorRotation(sourceBaseline));
        var rotation = NormalizeRotation(
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
    /// Copies a rotation onto the partner bone's own axes using both starting orientations.
    /// Translation and scale copy unchanged.
    /// </summary>
    public static TransformDelta LinkRebased(
        TransformDelta delta,
        Quaternion sourceBaseline,
        Quaternion destinationBaseline)
    {
        delta = delta.Normalized();
        sourceBaseline = NormalizeRotation(sourceBaseline);
        destinationBaseline = NormalizeRotation(destinationBaseline);
        var rotation = NormalizeRotation(
            destinationBaseline *
            Quaternion.Inverse(sourceBaseline) *
            delta.Rotation *
            sourceBaseline *
            Quaternion.Inverse(destinationBaseline));
        return delta with { Rotation = rotation };
    }

    /// <summary>
    /// Adjusts the rotation delta for the target's starting orientation relative to the primary target.
    /// Translation and scale copy unchanged.
    /// </summary>
    public static TransformDelta RelativeToPrimary(
        TransformDelta delta,
        Quaternion primaryBaseline,
        Quaternion targetBaseline)
    {
        delta = delta.Normalized();
        var offset = NormalizeRotation(
            NormalizeRotation(targetBaseline) *
            Quaternion.Inverse(NormalizeRotation(primaryBaseline)));
        return delta with
        {
            Rotation = NormalizeRotation(
                offset * delta.Rotation * Quaternion.Inverse(offset)),
        };
    }

    private static Quaternion MirrorRotation(Quaternion value) =>
        new(value.X, -value.Y, -value.Z, value.W);

    public static PoseTransform Apply(
        PoseTransform baseline,
        TransformDelta delta,
        TransformSpace space,
        Vector3 pivot,
        bool rotatePosition) =>
        Apply(baseline, delta, space, pivot, rotatePosition, scalePosition: false, scaleOwn: true);

    /// <summary>
    /// Applies a drag to the starting transform. rotatePosition moves the origin around the pivot;
    /// scalePosition also scales that offset, but only when rotatePosition is enabled.
    /// scaleOwn separately controls whether the object itself changes size.
    /// </summary>
    public static PoseTransform Apply(
        PoseTransform baseline,
        TransformDelta delta,
        TransformSpace space,
        Vector3 pivot,
        bool rotatePosition,
        bool scalePosition,
        bool scaleOwn,
        bool groupFactors = false,
        Quaternion? scaleFrame = null)
    {
        baseline = baseline.Normalized();
        if (groupFactors)
        {
            if (!GroupTransformControls.ValidDelta(delta))
                throw new ArgumentOutOfRangeException(nameof(delta));
            delta = delta with { Rotation = NormalizeRotation(delta.Rotation) };
        }
        else
            delta = delta.Normalized();
        var rotation = space == TransformSpace.Local
            ? NormalizeRotation(baseline.Rotation * delta.Rotation)
            : NormalizeRotation(delta.Rotation * baseline.Rotation);

        var position = baseline.Position + delta.Translation;
        if (rotatePosition)
        {
            var offset = baseline.Position - pivot;
            if (scalePosition && delta.ScaleFactor != Vector3.One)
            {
                // Group spacing follows the displayed axes frozen at Begin,
                // independently of each member's native own-size axes.
                offset = scaleFrame is { } frame
                    ? Vector3.Transform(Vector3.Transform(offset, Quaternion.Inverse(frame))
                        * delta.ScaleFactor, frame)
                    : offset * delta.ScaleFactor;
            }
            position = pivot +
                Vector3.Transform(offset, delta.Rotation) +
                delta.Translation;
        }

        var scale = scaleOwn ? baseline.Scale * delta.ScaleFactor : baseline.Scale;
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

        // Reject quaternion lengths below 0.001. Scaling before the length
        // calculation avoids overflow for large finite components.
        if (largest < 0.001f / scaledLength)
            return false;

        normalized = new Quaternion(
            scaled.X / scaledLength,
            scaled.Y / scaledLength,
            scaled.Z / scaledLength,
            scaled.W / scaledLength);
        return IsFinite(normalized);
    }

    public static bool IsFinite(Vector2 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y);

    public static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    public static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    public static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) &&
        float.IsFinite(value.W);

    public static bool IsFinite(Transform value) =>
        IsFinite(value.Position) && IsFinite(value.Rotation) && IsFinite(value.Scale);
}
