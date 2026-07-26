using System;
using System.Numerics;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Perspective-correct projection for the IN-WORLD gizmo, distinct from the
/// inspector's direction-only basis by design (Brio splits them the same
/// way: ImBrio.Gizmo for the widget, real view/projection matrices for the
/// overlay). All geometry is built in world space at the pivot, sized so it
/// keeps a stable perceived pixel size at the pivot's depth, and projected
/// through the active camera's real view and projection matrices — so
/// placement and orientation stay perspective-correct anywhere on screen
/// without the fixed-metre-radius deformation this replaces.
/// </summary>
public sealed class WorldGizmoProjection
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 InvViewProj;
    public Vector3 CameraPosition;
    public Vector2 DisplayCenter;
    /// <summary>The gizmo pivot in world space.</summary>
    public Vector3 Pivot;
    /// <summary>The projected pivot in screen pixels.</summary>
    public Vector2 Center;
    /// <summary>World units per gizmo unit: the world length that projects
    /// to the requested pixel size at the pivot's depth. All handle
    /// geometry is expressed as multiples of this, which is exactly how a
    /// stable perceived handle size falls out of a perspective path.</summary>
    public float WorldScale;
    /// <summary>Unit direction from the camera through the pivot; the
    /// camera-roll rotation axis.</summary>
    public Vector3 ViewDirection;

    /// <summary>
    /// Builds the projection for one frame, or null when the camera is
    /// unavailable or the pivot is behind / unprojectable — a
    /// non-projectable target draws nothing and accepts no input.
    /// </summary>
    public static WorldGizmoProjection? Create(
        ICameraService camera,
        Vector2 displaySize,
        Vector3 pivotWorld,
        float sizePixels)
    {
        var view = camera.GetViewMatrix();
        var projection = camera.GetProjectionMatrix();
        var viewProj = view * projection;
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
            return null;

        var result = new WorldGizmoProjection
        {
            ViewProj = viewProj,
            InvViewProj = invViewProj,
            CameraPosition = camera.GetCameraPosition(),
            DisplayCenter = displaySize / 2f,
            Pivot = pivotWorld,
        };
        if (!result.Project(pivotWorld, out var center))
            return null;
        result.Center = center;

        var toPivot = pivotWorld - result.CameraPosition;
        if (toPivot.LengthSquared() < 1e-8f)
            return null;
        result.ViewDirection = Vector3.Normalize(toPivot);

        // WorldScale is measured, not derived from matrix cells: project a
        // unit offset perpendicular to the view direction at the pivot and
        // read off pixels-per-world-unit. This stays convention-free.
        var reference = MathF.Abs(Vector3.Dot(result.ViewDirection, Vector3.UnitY)) > 0.99f
            ? Vector3.UnitX
            : Vector3.UnitY;
        var lateral = Vector3.Normalize(
            Vector3.Cross(result.ViewDirection, reference));
        if (!result.Project(pivotWorld + lateral, out var offsetScreen))
            return null;
        float pixelsPerWorldUnit = Vector2.Distance(offsetScreen, center);
        if (pixelsPerWorldUnit < 1e-3f)
            return null;
        result.WorldScale = sizePixels / pixelsPerWorldUnit;
        return result;
    }

    /// <summary>
    /// World point to screen pixels through the real view/projection — the
    /// same row-vector math as CameraService.WorldToScreen, false when the
    /// point is behind the camera.
    /// </summary>
    public bool Project(Vector3 world, out Vector2 screen)
    {
        var m = ViewProj;
        float x = m.M11 * world.X + m.M21 * world.Y + m.M31 * world.Z + m.M41;
        float y = m.M12 * world.X + m.M22 * world.Y + m.M32 * world.Z + m.M42;
        float w = m.M14 * world.X + m.M24 * world.Y + m.M34 * world.Z + m.M44;
        screen = new Vector2(
            DisplayCenter.X + DisplayCenter.X * x / w,
            DisplayCenter.Y - DisplayCenter.Y * y / w);
        return w > 0.001f;
    }

    /// <summary>The world-space mouse ray direction for a screen point.</summary>
    public Vector3? RayDirection(Vector2 screen)
    {
        float ndcX = screen.X / DisplayCenter.X - 1f;
        float ndcY = 1f - screen.Y / DisplayCenter.Y;
        var near = Vector4.Transform(new Vector4(ndcX, ndcY, 0f, 1f), InvViewProj);
        var far = Vector4.Transform(new Vector4(ndcX, ndcY, 1f, 1f), InvViewProj);
        if (MathF.Abs(near.W) < 1e-6f || MathF.Abs(far.W) < 1e-6f)
            return null;
        var direction = new Vector3(far.X / far.W, far.Y / far.W, far.Z / far.W) -
            new Vector3(near.X / near.W, near.Y / near.W, near.Z / near.W);
        return direction.LengthSquared() < 1e-8f
            ? null
            : Vector3.Normalize(direction);
    }

    /// <summary>
    /// Intersects the mouse ray with a world plane; null when the ray is
    /// parallel to the plane or the intersection lies behind the camera.
    /// </summary>
    public Vector3? RayPlane(Vector2 screen, Vector3 planePoint, Vector3 planeNormal)
    {
        if (RayDirection(screen) is not { } direction)
            return null;
        float denominator = Vector3.Dot(direction, planeNormal);
        if (MathF.Abs(denominator) < 1e-6f)
            return null;
        float t = Vector3.Dot(planePoint - CameraPosition, planeNormal) / denominator;
        return t < 0f ? null : CameraPosition + direction * t;
    }
}

/// <summary>
/// The in-world gizmo geometry built on <see cref="WorldGizmoProjection"/>.
/// Ring drawing and segment hit-testing are shared with the inspector
/// through <see cref="ProjectedRings"/>/<see cref="RotationGizmoRings"/> —
/// only the projection that fills the geometry differs between the two
/// surfaces, which is the deliberate Brio split.
/// </summary>
public static class WorldGizmo
{
    /// <summary>
    /// Perspective-projected rotation rings: points on a world circle of
    /// <paramref name="ringWorldRadius"/> about the pivot, one per axis of
    /// <paramref name="frame"/>, projected through the real matrices. Front
    /// segments are on the camera side of the pivot. The roll ring stays a
    /// true screen circle just outside the projected ring extent.
    /// </summary>
    public static ProjectedRings ProjectRings(
        WorldGizmoProjection projection,
        Quaternion frame,
        float ringWorldRadius,
        float scale)
    {
        var rings = new ProjectedRings
        {
            Frame = frame,
            Center = projection.Center,
            // The perspective surface derives tangents itself
            // (PositiveTangentPerspective); the direction-only ViewRotation
            // stays identity here on purpose.
            RollAxisWorld = projection.ViewDirection,
        };
        rings.Points = new Vector2[3][];
        rings.Front = new bool[3][];
        float maxRadius = 0f;
        for (int a = 0; a < 3; a++)
        {
            rings.Points[a] = new Vector2[RotationGizmoRings.RingPoints];
            rings.Front[a] = new bool[RotationGizmoRings.RingPoints];
            for (int i = 0; i < RotationGizmoRings.RingPoints; i++)
            {
                var world = projection.Pivot + Vector3.Transform(
                    RotationGizmoRings.LocalRingPoint(a, i), frame) * ringWorldRadius;
                if (!projection.Project(world, out var screen))
                    return rings; // behind camera — invalid, draw nothing
                rings.Points[a][i] = screen;
                // Front = nearer the camera than the pivot's view plane.
                rings.Front[a][i] = Vector3.Dot(
                    world - projection.Pivot, projection.ViewDirection) < 0f;
                maxRadius = MathF.Max(
                    maxRadius, Vector2.Distance(screen, projection.Center));
            }
        }
        rings.ScreenRadius = maxRadius;
        rings.RollRadius = maxRadius + 8f * scale;
        rings.Valid = true;
        return rings;
    }

    /// <summary>
    /// The screen direction of POSITIVE rotation at the grab point for the
    /// perspective rings: epsilon-rotate the world grab point about the
    /// ring's world axis and project both through the same real matrices,
    /// so drag direction matches the applied rotation by construction.
    /// </summary>
    public static Vector2 PositiveTangentPerspective(
        WorldGizmoProjection projection,
        ProjectedRings rings,
        RingHit hit,
        Vector2 mouse,
        float ringWorldRadius)
    {
        Vector3 grabWorld;
        if (hit.Axis == RotationGizmoRings.RollAxis)
        {
            // The roll grab point lives on the view plane through the pivot.
            grabWorld = projection.RayPlane(
                mouse, projection.Pivot, projection.ViewDirection)
                ?? projection.Pivot;
            if (Vector3.DistanceSquared(grabWorld, projection.Pivot) < 1e-10f)
                return hit.Tangent;
        }
        else
        {
            grabWorld = projection.Pivot + Vector3.Transform(
                RotationGizmoRings.LocalRingPoint(hit.Axis, hit.SegmentIndex),
                rings.Frame) * ringWorldRadius;
        }
        var axisWorld = RotationGizmoRings.AxisWorld(rings, hit.Axis);
        var rotated = projection.Pivot + Vector3.Transform(
            grabWorld - projection.Pivot,
            Quaternion.CreateFromAxisAngle(axisWorld, 0.05f));
        if (!projection.Project(grabWorld, out var a) ||
            !projection.Project(rotated, out var b))
            return hit.Tangent;
        var tangent = b - a;
        return tangent.LengthSquared() < 1e-8f
            ? hit.Tangent
            : Vector2.Normalize(tangent);
    }
}
