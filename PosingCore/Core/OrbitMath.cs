using System;
using System.Numerics;

namespace Poser.Core;

/// <summary>Where the orbit pivot comes from.</summary>
public enum OrbitPivotMode
{
    /// <summary>The dragged bone's parent bone position (the headline feature).</summary>
    Parent,
    /// <summary>Centroid of the selected bones.</summary>
    SelectionCenter,
    /// <summary>A user-placed point.</summary>
    Custom,
}

/// <summary>
/// How orbit targets are computed during a drag. Exists so the strategies can
/// be compared IN GAME — the failure mode this feature exists to avoid
/// ("rotate a few times and the bone flies off to infinity") is a property of
/// the computation structure, not of the values.
/// </summary>
public enum OrbitStrategy
{
    /// <summary>
    /// DEFAULT. Targets are a pure function of the drag-start snapshot and the
    /// total rotation: pos = pivot + R·(base − pivot). Nothing is ever read
    /// back from live memory mid-drag and the stack contribution is REPLACED,
    /// not accumulated — error physically cannot compound.
    /// </summary>
    SnapshotAbsolute,

    /// <summary>
    /// Increments accumulate in the delta stack, but each increment is
    /// computed between two exact evaluations of the snapshot math (never
    /// against live memory). Compounds float error additively, not
    /// multiplicatively. Middle ground for comparison.
    /// </summary>
    PureIncrementalRebase,

    /// <summary>
    /// CONTROL / bug reproduction: each frame's target derives from the LIVE
    /// bone transform (which already contains the previous write plus whatever
    /// Havok did in between). This is the Brio/Ktisis-style structure that
    /// produces the fly-to-infinity behavior. Kept selectable on purpose so
    /// the difference can be demonstrated in game.
    /// </summary>
    LiveIncremental,
}

/// <summary>
/// Pure orbit math. The core invariant: an orbit target is a function of the
/// immutable drag-start base and the TOTAL rotation — evaluating it twice with
/// the same inputs gives the same output, and the orbit radius |base − pivot|
/// is preserved exactly (up to normalized-quaternion rounding).
/// </summary>
public static class OrbitMath
{
    /// <summary>Positions/scales beyond this are considered runaway and rejected.</summary>
    public const float MaxSanePosition = 10_000f;

    /// <summary>
    /// Rotate <paramref name="baseTransform"/> around <paramref name="pivot"/>
    /// by <paramref name="totalRotation"/> (model space). The rotation is
    /// normalized before use so a denormalized input cannot scale the radius.
    /// </summary>
    public static Transform EvaluateOrbit(Transform baseTransform, Vector3 pivot, Quaternion totalRotation)
    {
        var rotation = Quaternion.Normalize(totalRotation);
        return new Transform
        {
            Position = pivot + Vector3.Transform(baseTransform.Position - pivot, rotation),
            Rotation = Quaternion.Normalize(rotation * baseTransform.Rotation),
            Scale = baseTransform.Scale,
        };
    }

    /// <summary>NaN/Infinity/magnitude guard — a failed frame must be dropped, never written.</summary>
    public static bool IsSane(Transform t)
    {
        return IsFinite(t.Position) && IsFinite(t.Scale)
            && float.IsFinite(t.Rotation.X) && float.IsFinite(t.Rotation.Y)
            && float.IsFinite(t.Rotation.Z) && float.IsFinite(t.Rotation.W)
            && MathF.Abs(t.Position.X) < MaxSanePosition
            && MathF.Abs(t.Position.Y) < MaxSanePosition
            && MathF.Abs(t.Position.Z) < MaxSanePosition;

        static bool IsFinite(Vector3 v)
            => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    }
}
