using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Poser.Entities;

namespace Poser.Core;

/// <summary>
/// Pure pose math shared by posing services. No game or Dalamud dependencies —
/// live scenarios exercise these rules through the production pose pipeline.
/// </summary>
public static class PoseMath
{
    private const float RadiansToDegrees = 180f / MathF.PI;
    private const float DegreesToRadians = MathF.PI / 180f;

    /// <summary>
    /// Removes selected descendants whose ancestor is already selected. Applying
    /// a propagated transform to both would compound the descendant's delta.
    /// Input order is preserved so the caller's primary root remains primary.
    /// </summary>
    public static IReadOnlyList<IBone> FilterSelectionRoots(IEnumerable<IBone> bones)
    {
        var ordered = bones.Distinct().ToList();
        var selected = ordered.ToHashSet();
        return ordered.Where(bone =>
        {
            for (var parent = bone.ParentBone; parent != null; parent = parent.ParentBone)
            {
                if (selected.Contains(parent))
                    return false;
            }
            return true;
        }).ToList();
    }

    /// <summary>model = parent COMPOSED WITH local (scale-then-rotate-then-translate).</summary>
    public static Transform Compose(in Transform parent, in Transform local) => new()
    {
        Position = parent.Position + Vector3.Transform(local.Position * parent.Scale, parent.Rotation),
        Rotation = Quaternion.Normalize(parent.Rotation * local.Rotation),
        Scale = parent.Scale * local.Scale,
    };

    /// <summary>local = inverse(parent) COMPOSED WITH model — the parent-relative
    /// transform Ktisis/Anamnesis display. Inverse of <see cref="Compose"/>.</summary>
    public static Transform ToLocal(in Transform parent, in Transform model)
    {
        var invRot = Quaternion.Inverse(parent.Rotation);
        return new Transform
        {
            Position = SafeDivide(Vector3.Transform(model.Position - parent.Position, invRot), parent.Scale),
            Rotation = Quaternion.Normalize(invRot * model.Rotation),
            Scale = SafeDivide(model.Scale, parent.Scale),
        };
    }

    private static Vector3 SafeDivide(Vector3 a, Vector3 b) => new(
        b.X != 0f ? a.X / b.X : a.X,
        b.Y != 0f ? a.Y / b.Y : a.Y,
        b.Z != 0f ? a.Z / b.Z : a.Z);

    /// <summary>
    /// Applies the relative change made to a primary transform to a secondary
    /// transform. Position and scale are additive. Rotation is transferred
    /// through world orientation so each secondary rotates in its own local
    /// frame, matching the editor gizmo's multi-selection convention.
    /// </summary>
    public static Transform ApplyRelativeDelta(
        in Transform primaryBefore,
        in Transform primaryAfter,
        in Transform secondaryBefore)
    {
        var positionDelta = primaryAfter.Position - primaryBefore.Position;
        var scaleDelta = primaryAfter.Scale - primaryBefore.Scale;

        var rotationDelta = Quaternion.Normalize(
            Quaternion.Conjugate(primaryBefore.Rotation) * primaryAfter.Rotation);
        var deltaInWorld = primaryBefore.Rotation
            * rotationDelta
            * Quaternion.Conjugate(primaryBefore.Rotation);
        var deltaInSecondaryLocal = Quaternion.Conjugate(secondaryBefore.Rotation)
            * deltaInWorld
            * secondaryBefore.Rotation;

        return new Transform
        {
            Position = secondaryBefore.Position + positionDelta,
            Rotation = Quaternion.Normalize(secondaryBefore.Rotation * deltaInSecondaryLocal),
            Scale = secondaryBefore.Scale + scaleDelta,
        };
    }

    /// <summary>
    /// Keeps only the requested components from a manipulated transform and restores
    /// every other component from the stable drag baseline. Gizmo matrix
    /// decomposition can introduce tiny (or, under a transformed skeleton, visible)
    /// changes in components the active tool did not edit.
    /// </summary>
    public static Transform ConstrainToComponents(
        in Transform baseline,
        in Transform manipulated,
        TransformComponents components)
    {
        return new Transform
        {
            Position = components.HasFlag(TransformComponents.Position)
                ? manipulated.Position
                : baseline.Position,
            Rotation = components.HasFlag(TransformComponents.Rotation)
                ? Quaternion.Normalize(manipulated.Rotation)
                : baseline.Rotation,
            Scale = components.HasFlag(TransformComponents.Scale)
                ? manipulated.Scale
                : baseline.Scale,
        };
    }

    /// <summary>Reflects an absolute transform across the sagittal (YZ) plane.</summary>
    /// <remarks>Whole-pose mirroring uses <see cref="MirrorPoseDelta"/> instead.</remarks>
    public static Transform MirrorTransform(Transform t)
    {
        return new Transform
        {
            Position = new Vector3(-t.Position.X, t.Position.Y, t.Position.Z),
            Rotation = new Quaternion(-t.Rotation.X, t.Rotation.Y, t.Rotation.Z, -t.Rotation.W),
            Scale = t.Scale
        };
    }


    /// <summary>
    /// Mirrors an additive pose delta using the convention shared by Brio's
    /// Transform.Inverted and Ktisis' inverse mirror mode. This operates on
    /// deltas, not absolute model-space bone transforms: translation and additive
    /// scale are negated, and rotation is inverted with quaternion conjugation.
    /// </summary>
    public static Transform MirrorPoseDelta(Transform delta)
    {
        return new Transform
        {
            Position = -delta.Position,
            Rotation = Quaternion.Normalize(Quaternion.Conjugate(delta.Rotation)),
            Scale = -delta.Scale
        };
    }

    /// <summary>
    /// Scales an expression/action-unit delta without extrapolating a quaternion
    /// slerp through negative time. Negative weights interpolate toward the inverse
    /// rotation, matching the bidirectional action-unit convention.
    /// </summary>
    public static Transform WeightPoseDelta(Transform delta, float weight, bool includePosition)
    {
        var magnitude = Math.Clamp(MathF.Abs(weight), 0f, 1f);
        var rotation = Quaternion.Normalize(delta.Rotation);
        if (weight < 0f)
            rotation = Quaternion.Conjugate(rotation);

        return new Transform
        {
            Position = includePosition ? delta.Position * Math.Clamp(weight, -1f, 1f) : Vector3.Zero,
            Rotation = Quaternion.Normalize(Quaternion.Slerp(Quaternion.Identity, rotation, magnitude)),
            Scale = (delta.Scale - Vector3.One) * magnitude
        };
    }
    /// <summary>
    /// Returns the opposite-side bone name for left/right suffixed bones
    /// (e.g. "j_te_l" → "j_te_r"), or null when the bone has no mirror partner.
    /// </summary>
    public static string? GetMirrorBoneName(string boneName)
    {
        if (boneName.EndsWith("_r"))
        {
            return string.Concat(boneName.AsSpan(0, boneName.Length - 2), "_l");
        }

        if (boneName.EndsWith("_l"))
        {
            return string.Concat(boneName.AsSpan(0, boneName.Length - 2), "_r");
        }

        return null;
    }

    /// <summary>
    /// Converts a rotation to euler angles in degrees around the labeled X/Y/Z axes.
    /// Lossy at gimbal poles — do not round-trip through this for accumulation;
    /// use quaternion composition instead.
    /// </summary>
    public static Vector3 QuaternionToEuler(Quaternion r)
    {
        float yaw = MathF.Atan2(2.0f * (r.Y * r.W + r.X * r.Z), 1.0f - 2.0f * (r.X * r.X + r.Y * r.Y));
        float pitch = MathF.Asin(Math.Clamp(2.0f * (r.X * r.W - r.Y * r.Z), -1f, 1f));
        float roll = MathF.Atan2(2.0f * (r.X * r.Y + r.Z * r.W), 1.0f - 2.0f * (r.X * r.X + r.Z * r.Z));

        // CreateFromYawPitchRoll names its arguments by operation, not coordinate
        // order: yaw is Y, pitch is X, and roll is Z.
        return new Vector3(pitch, yaw, roll) * RadiansToDegrees;
    }

    /// <summary>
    /// Converts labeled X/Y/Z euler angles in degrees to a normalized quaternion.
    /// </summary>
    public static Quaternion EulerToQuaternion(Vector3 euler)
    {
        euler *= DegreesToRadians;
        var quaternion = Quaternion.CreateFromYawPitchRoll(euler.Y, euler.X, euler.Z);
        return Quaternion.Normalize(quaternion);
    }
}
