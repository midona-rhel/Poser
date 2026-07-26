using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// One projected ring set: the shared rotation-gizmo geometry consumed by
/// BOTH the inspector widget and the in-world overlay. Ring points are
/// DIRECTION-ONLY projections (camera rotation, no translation,
/// perspective, FOV, or depth), so shape and pixel radius are identical
/// anywhere on screen; only the centre moves with the pivot.
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
    public Quaternion ViewRotation = Quaternion.Identity;
    public Vector3 RollAxisWorld = Vector3.UnitZ;
}

public readonly record struct RingHit(int Axis, float Distance, Vector2 Tangent, int SegmentIndex);

/// <summary>
/// The one shared rotation-gizmo calculation: frame
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
    /// The Parent pivot's radial frame: red (X) points along
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
    /// Projects the three axis rings around the given SCREEN centre using
    /// only the camera's rotation (Brio ImBrio.Gizmo compatibility): each
    /// unit ring direction is rotated by the gizmo frame and the view
    /// rotation, then its camera-space X/Y maps straight to the requested
    /// pixel radius. No translation, perspective, FOV, pivot depth, or
    /// actor scale participates — the rings keep one stable shape and size
    /// anywhere on screen. Front segments face the camera (Z &lt; 0).
    /// </summary>
    public static ProjectedRings Project(
        ICameraService camera,
        Vector2 center,
        Quaternion frame,
        float radiusPixels)
    {
        var rings = new ProjectedRings { Frame = frame, Center = center };
        var view = camera.GetViewMatrix();
        view.M44 = 1f;
        if (!Matrix4x4.Decompose(view, out _, out var viewRotation, out _))
            return rings;

        rings.Valid = true;
        rings.ViewRotation = viewRotation;
        rings.ScreenRadius = radiusPixels;
        rings.RollRadius = radiusPixels + 8f;
        // The roll ring rotates about the camera view axis: the world
        // direction mapping to camera-space +Z under the same basis.
        rings.RollAxisWorld = Vector3.Normalize(
            FromCamera(Vector3.UnitZ, viewRotation));
        rings.Points = new Vector2[3][];
        rings.Front = new bool[3][];

        for (int a = 0; a < 3; a++)
        {
            rings.Points[a] = new Vector2[RingPoints];
            rings.Front[a] = new bool[RingPoints];
            for (int i = 0; i < RingPoints; i++)
            {
                var cam = ToCamera(
                    Vector3.Transform(LocalRingPoint(a, i), frame),
                    viewRotation);
                rings.Points[a][i] =
                    center + new Vector2(cam.X, cam.Y) * radiusPixels;
                rings.Front[a][i] = cam.Z < 0f;
            }
        }
        return rings;
    }

    /// <summary>Unit direction of ring point <paramref name="index"/> on
    /// axis <paramref name="axis"/>'s circle in the gizmo frame.</summary>
    public static Vector3 LocalRingPoint(int axis, int index)
    {
        float t = index / (float)(RingPoints - 1) * MathF.Tau;
        return axis switch
        {
            0 => new Vector3(0f, MathF.Cos(t), MathF.Sin(t)),
            1 => new Vector3(MathF.Cos(t), 0f, MathF.Sin(t)),
            _ => new Vector3(MathF.Cos(t), MathF.Sin(t), 0f),
        };
    }

    // THE handedness decision, made once from Brio (ImBrio.Gizmo): camera
    // space is the view matrix's rotation followed by an X mirror; screen
    // offset is camera X/Y and front is Z < 0. Individual axes are never
    // repaired with extra sign flips — tangents derive from this same
    // mapping, so drag direction stays sign-correct by construction.
    private static Vector3 ToCamera(Vector3 worldDirection, Quaternion viewRotation)
    {
        var v = Vector3.Transform(worldDirection, viewRotation);
        return new Vector3(-v.X, v.Y, v.Z);
    }

    private static Vector3 FromCamera(Vector3 cameraDirection, Quaternion viewRotation) =>
        Vector3.Transform(
            new Vector3(-cameraDirection.X, cameraDirection.Y, cameraDirection.Z),
            Quaternion.Inverse(viewRotation));

    /// <summary>
    /// Nearest visible projected ring segment within tolerance; the outer
    /// roll circle competes last. Exact ties resolve X → Y → Z → Roll.
    /// </summary>
    public static RingHit? HitTest(ProjectedRings rings, Vector2 mouse, float tolerance)
    {
        if (!rings.Valid)
            return null;
        int axis = -1;
        int segment = 0;
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
                    segment = i;
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
            segment = 0;
            tangent = Vector2.Normalize(new Vector2(-radial.Y, radial.X));
        }
        return axis < 0 ? null : new RingHit(axis, best, tangent, segment);
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

    /// <summary>
    /// The screen-space direction of POSITIVE rotation about the ring's
    /// axis at the grab point, derived by epsilon-rotating the grab
    /// DIRECTION and re-projecting through the same direction-only basis —
    /// drag direction always matches the applied rotation on every ring
    /// with no perspective projection involved.
    /// </summary>
    public static Vector2 PositiveTangent(
        ProjectedRings rings,
        RingHit hit,
        Vector2 mouse)
    {
        if (!rings.Valid)
            return hit.Tangent;
        Vector3 grabDirection;
        if (hit.Axis == RollAxis)
        {
            var radial = mouse - rings.Center;
            if (radial.LengthSquared() < 1e-6f)
                return hit.Tangent;
            radial = Vector2.Normalize(radial);
            grabDirection = FromCamera(
                new Vector3(radial.X, radial.Y, 0f), rings.ViewRotation);
        }
        else
        {
            grabDirection = Vector3.Transform(
                LocalRingPoint(hit.Axis, hit.SegmentIndex), rings.Frame);
        }
        var axisWorld = AxisWorld(rings, hit.Axis);
        var rotated = Vector3.Transform(
            grabDirection,
            Quaternion.CreateFromAxisAngle(axisWorld, 0.05f));
        var a = ToCamera(grabDirection, rings.ViewRotation);
        var b = ToCamera(rotated, rings.ViewRotation);
        var tangent = new Vector2(b.X - a.X, b.Y - a.Y);
        return tangent.LengthSquared() < 1e-8f
            ? hit.Tangent
            : Vector2.Normalize(tangent);
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

/// <summary>
/// Shared pointer ownership for custom gizmo gestures: while a ring drag —
/// or its release frame — owns the pointer, selection surfaces (skeleton
/// overlay, 3D view) must not treat the click as a bone/actor pick.
/// </summary>
public static class GizmoPointerOwnership
{
    private static int _ownedUntilFrame = -1;

    /// <summary>Call every frame the pointer engages a custom gizmo.</summary>
    public static void Hold() =>
        _ownedUntilFrame = ImGui.GetFrameCount() + 1;

    public static bool Owned =>
        ImGui.GetFrameCount() <= _ownedUntilFrame;
}
