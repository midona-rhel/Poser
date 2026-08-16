using System;
using System.Numerics;

namespace Poser.Domain.Transforms;

/// <summary>
/// Snap helpers for the total accumulated by a gesture. Quantising the total
/// avoids rounding drift between frames.
/// </summary>
public static class GizmoSnap
{
    /// <summary>Divisor used by the precise snap modifier.</summary>
    public const float PrecisionDivisor = 10f;

    /// <summary>The live increment for a step and its precision state. A
    /// non-positive configured step means "no grid", and quantising against it
    /// would be a division by zero, so it reports zero and every Snap overload
    /// below passes the value through.</summary>
    public static float Increment(float step, bool precise)
    {
        if (!float.IsFinite(step) || step <= 0f)
            return 0f;
        return precise ? step / PrecisionDivisor : step;
    }

    /// <summary>Rounds to the nearest multiple of <paramref name="increment"/>,
    /// halves away from zero. A zero or non-finite increment is no grid at
    /// all.</summary>
    public static float Snap(float value, float increment)
    {
        if (increment <= 0f || !float.IsFinite(increment) || !float.IsFinite(value))
            return value;
        return MathF.Round(value / increment, MidpointRounding.AwayFromZero)
            * increment;
    }

    /// <summary>Per-component quantisation — a translate total lands on the
    /// grid one axis at a time, exactly as ImGuizmo's per-axis snap
    /// vector does.</summary>
    public static Vector3 Snap(Vector3 value, float increment) => new(
        Snap(value.X, increment),
        Snap(value.Y, increment),
        Snap(value.Z, increment));

    /// <summary>An angle in RADIANS quantised to a DEGREE increment — the two
    /// units the rotate path actually holds: the gesture accumulates radians,
    /// the configured step reads 5°.</summary>
    public static float SnapRadiansToDegrees(float radians, float degreeStep)
    {
        if (degreeStep <= 0f || !float.IsFinite(degreeStep))
            return radians;
        float degrees = Snap(
            radians * (180f / MathF.PI),
            degreeStep);
        return degrees * (MathF.PI / 180f);
    }
}
