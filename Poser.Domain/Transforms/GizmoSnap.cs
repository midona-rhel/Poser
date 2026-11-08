using System;
using System.Numerics;

namespace Poser.Domain.Transforms;

/// <summary>
/// Rounds the total change since a drag began, rather than rounding each frame's
/// change separately. This avoids accumulating rounding error during the drag.
/// </summary>
public static class GizmoSnap
{
    /// <summary>Divisor used by the precise snap modifier.</summary>
    public const float PrecisionDivisor = 10f;

    /// <summary>
    /// Returns the configured step, divided by ten in precise mode.
    /// Nonpositive or nonfinite steps return zero, which disables snapping.
    /// </summary>
    public static float Increment(float step, bool precise)
    {
        if (!float.IsFinite(step) || step <= 0f)
            return 0f;
        return precise ? step / PrecisionDivisor : step;
    }

    /// <summary>
    /// Rounds to the nearest step, with halfway values rounded away from zero.
    /// Invalid steps or nonfinite input are returned unchanged.
    /// </summary>
    public static float Snap(float value, float increment)
    {
        if (increment <= 0f || !float.IsFinite(increment) || !float.IsFinite(value))
            return value;
        return MathF.Round(value / increment, MidpointRounding.AwayFromZero)
            * increment;
    }

    /// <summary>
    /// Snaps each vector component independently.
    /// </summary>
    public static Vector3 Snap(Vector3 value, float increment) => new(
        Snap(value.X, increment),
        Snap(value.Y, increment),
        Snap(value.Z, increment));

    /// <summary>
    /// Snaps an angle expressed in radians to a step expressed in degrees,
    /// then returns the result in radians.
    /// </summary>
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
