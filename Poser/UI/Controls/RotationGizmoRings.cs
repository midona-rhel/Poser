using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// One projected ring set: the shared rotation-gizmo geometry consumed by
/// BOTH the inspector widget and the in-world overlay (PBI-002 correction
/// 4C). Ring points are true world-space circles around the pivot, projected
/// through the actual game camera (`ICameraService.WorldToScreen`), so the
/// red/green/blue rings describe the same real rotation axes everywhere.
/// </summary>
public sealed class ProjectedRings
{
    public bool Valid;
    public Vector2 Center;
    public Vector2[][] Points = Array.Empty<Vector2[]>();
    public bool[][] Front = Array.Empty<bool[]>();
    public float ScreenRadius;
    public float RollRadius;
    public Quaternion Frame = Quaternion.Identity;
    public Vector3 RollAxisWorld = Vector3.UnitZ;

    /// <summary>The same rings re-centered for a fixed widget location.</summary>
    public ProjectedRings Recentered(Vector2 newCenter)
    {
        var moved = new ProjectedRings
        {
            Valid = Valid,
            Center = newCenter,
            Points = new Vector2[Points.Length][],
            Front = Front,
            ScreenRadius = ScreenRadius,
            RollRadius = RollRadius,
            Frame = Frame,
            RollAxisWorld = RollAxisWorld,
        };
        var offset = newCenter - Center;
        for (int a = 0; a < Points.Length; a++)
        {
            moved.Points[a] = new Vector2[Points[a].Length];
            for (int i = 0; i < Points[a].Length; i++)
                moved.Points[a][i] = Points[a][i] + offset;
        }
        return moved;
    }
}

public readonly record struct RingHit(int Axis, float Distance, Vector2 Tangent);

/// <summary>
/// The one shared rotation-gizmo calculation (PBI-002 correction 4C): frame
/// basis, camera projection, front/rear classification, ring hit testing,
/// drag tangents, the outer camera-roll axis, and the Ctrl/Shift
/// sensitivity policy. Both rotation surfaces dispatch results through the
/// existing clean TransformGestureService lifecycle — this class owns no
/// gesture state.
/// </summary>
public static class RotationGizmoRings
{
    public const int RingPoints = 96;
    public const int RollAxis = 3;

    /// <summary>Shared drag-sensitivity policy: Ctrl fine (0.1×), Shift
    /// coarse (10×), Ctrl+Shift back to 1×.</summary>
    public static float ModifierMultiplier(ImGuiIOPtr io) =>
        io.KeyCtrl && io.KeyShift ? 1f :
        io.KeyCtrl ? 0.1f :
        io.KeyShift ? 10f : 1f;

    /// <summary>Screen pixels of tangent drag per radian of rotation.</summary>
    public const float PixelsPerRadian = 200f;

    /// <summary>
    /// The Parent pivot's radial frame (correction 4E): red (X) points along
    /// normalized child − parent; the remaining axes form a stable
    /// orthonormal basis with a deterministic fallback when the radial
    /// direction is near the reference axis. The parent bone's own
    /// orientation is deliberately not the source of this frame.
    /// </summary>
    public static Quaternion RadialFrame(Vector3 parentPosition, Vector3 childPosition)
    {
        var radial = childPosition - parentPosition;
        if (radial.LengthSquared() < 1e-8f)
            return Quaternion.Identity;
        radial = Vector3.Normalize(radial);
        var reference = MathF.Abs(Vector3.Dot(radial, Vector3.UnitY)) > 0.9f
            ? Vector3.UnitZ
            : Vector3.UnitY;
        var axisB = Vector3.Normalize(Vector3.Cross(reference, radial));
        var axisC = Vector3.Cross(radial, axisB);
        var basis = new Matrix4x4(
            radial.X, radial.Y, radial.Z, 0f,
            axisB.X, axisB.Y, axisB.Z, 0f,
            axisC.X, axisC.Y, axisC.Z, 0f,
            0f, 0f, 0f, 1f);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(basis));
    }

    /// <summary>
    /// Projects the three axis rings around the world-space pivot using the
    /// actual game camera. The world radius is derived so the projected ring
    /// is approximately <paramref name="screenRadius"/> pixels; the roll
    /// ring sits slightly outside. Front segments are those closer to the
    /// camera than the pivot.
    /// </summary>
    public static ProjectedRings Project(
        ICameraService camera,
        Vector3 pivotWorld,
        Quaternion frame,
        float screenRadius)
    {
        var rings = new ProjectedRings { Frame = frame };
        if (!camera.WorldToScreen(pivotWorld, out var center))
            return rings;

        // pixels-per-meter at the pivot, from a camera-right offset.
        var view = camera.GetViewMatrix();
        view.M44 = 1f;
        if (!Matrix4x4.Invert(view, out var viewInverse))
            return rings;
        var cameraRight = Vector3.Normalize(
            Vector3.TransformNormal(Vector3.UnitX, viewInverse));
        if (!camera.WorldToScreen(pivotWorld + cameraRight * 0.5f, out var offsetScreen))
            return rings;
        float pixelsPerMeter = Vector2.Distance(center, offsetScreen) * 2f;
        if (pixelsPerMeter < 1e-3f)
            return rings;
        float worldRadius = screenRadius / pixelsPerMeter;

        var cameraPosition = camera.GetCameraPosition();
        float pivotDepth = Vector3.DistanceSquared(cameraPosition, pivotWorld);

        rings.Valid = true;
        rings.Center = center;
        rings.ScreenRadius = screenRadius;
        rings.RollRadius = screenRadius + 8f;
        rings.RollAxisWorld = Vector3.Normalize(pivotWorld - cameraPosition);
        rings.Points = new Vector2[3][];
        rings.Front = new bool[3][];

        for (int a = 0; a < 3; a++)
        {
            rings.Points[a] = new Vector2[RingPoints];
            rings.Front[a] = new bool[RingPoints];
            for (int i = 0; i < RingPoints; i++)
            {
                float t = i / (float)(RingPoints - 1) * MathF.Tau;
                var local = a switch
                {
                    0 => new Vector3(0f, MathF.Cos(t), MathF.Sin(t)),
                    1 => new Vector3(MathF.Cos(t), 0f, MathF.Sin(t)),
                    _ => new Vector3(MathF.Cos(t), MathF.Sin(t), 0f),
                };
                var world = pivotWorld +
                    Vector3.Transform(local, frame) * worldRadius;
                if (!camera.WorldToScreen(world, out var screen))
                {
                    // Behind the camera: reuse the previous point so the
                    // polyline stays finite; mark it rear-facing.
                    screen = i > 0 ? rings.Points[a][i - 1] : center;
                    rings.Points[a][i] = screen;
                    rings.Front[a][i] = false;
                    continue;
                }
                rings.Points[a][i] = screen;
                rings.Front[a][i] =
                    Vector3.DistanceSquared(cameraPosition, world) < pivotDepth;
            }
        }
        return rings;
    }

    /// <summary>
    /// Nearest visible projected ring segment within tolerance; the outer
    /// roll circle competes last. Exact ties resolve X → Y → Z → Roll.
    /// </summary>
    public static RingHit? HitTest(ProjectedRings rings, Vector2 mouse, float tolerance)
    {
        if (!rings.Valid)
            return null;
        int axis = -1;
        var tangent = Vector2.Zero;
        float best = tolerance;
        for (int a = 0; a < 3; a++)
        {
            for (int i = 1; i < RingPoints; i++)
            {
                if (!rings.Front[a][i])
                    continue;
                float dist = DistanceToSegment(
                    mouse, rings.Points[a][i - 1], rings.Points[a][i]);
                if (dist < best)
                {
                    best = dist;
                    axis = a;
                    tangent = Vector2.Normalize(
                        rings.Points[a][i] - rings.Points[a][i - 1]);
                }
            }
        }
        var radial = mouse - rings.Center;
        float radialLength = radial.Length();
        if (radialLength > 1e-3f &&
            MathF.Abs(radialLength - rings.RollRadius) < best)
        {
            axis = RollAxis;
            tangent = Vector2.Normalize(new Vector2(-radial.Y, radial.X));
        }
        return axis < 0 ? null : new RingHit(axis, best, tangent);
    }

    /// <summary>The world-space rotation axis of a ring in the given frame.</summary>
    public static Vector3 AxisWorld(ProjectedRings rings, int axis) =>
        axis == RollAxis
            ? rings.RollAxisWorld
            : Vector3.Normalize(Vector3.Transform(
                axis switch
                {
                    0 => Vector3.UnitX,
                    1 => Vector3.UnitY,
                    _ => Vector3.UnitZ,
                },
                rings.Frame));

    /// <summary>
    /// Draws the ring set with the inspector's approved grammar: pastel axis
    /// palette, hover/active emphasis, wide outer roll ring. The world
    /// overlay passes <paramref name="drawRearArcs"/> = false so only
    /// meaningful front arcs appear over the game.
    /// </summary>
    public static void Draw(
        ImDrawListPtr dl,
        ProjectedRings rings,
        int hoverAxis,
        int dragAxis,
        bool drawRearArcs,
        float scale)
    {
        if (!rings.Valid)
            return;

        // Wide outer camera-roll ring first, then rear arcs, then front arcs.
        {
            bool hot = hoverAxis == RollAxis || dragAxis == RollAxis;
            var rollColor = new Vector4(1f, 1f, 1f, hot ? 0.95f : 0.55f);
            dl.AddCircle(rings.Center, rings.RollRadius,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(rollColor)),
                0, (hot ? 4.5f : 3.5f) * scale);
        }
        for (int pass = drawRearArcs ? 0 : 1; pass < 2; pass++)
        {
            bool frontPass = pass == 1;
            for (int a = 0; a < 3; a++)
            {
                var axisColor = a switch
                {
                    0 => Theme.Palette.AxisX,
                    1 => Theme.Palette.AxisY,
                    _ => Theme.Palette.AxisZ,
                };
                bool hot = hoverAxis == a || dragAxis == a;
                float alpha = frontPass ? (hot ? 1f : 0.85f) : 0.12f;
                float thickness = (frontPass && hot ? 3f : 2f) * scale;
                uint color = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(axisColor with { W = alpha }));
                for (int i = 1; i < RingPoints; i++)
                {
                    if (rings.Front[a][i] != frontPass)
                        continue;
                    dl.AddLine(
                        rings.Points[a][i - 1], rings.Points[a][i],
                        color, thickness);
                }
            }
        }
    }

    public static string AxisName(int axis) => axis switch
    {
        0 => "X axis",
        1 => "Y axis",
        2 => "Z axis",
        _ => "Roll",
    };

    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-6f)
            return Vector2.Distance(point, a);
        float t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSq, 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }
}
