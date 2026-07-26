using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>Every interactive element of the in-world gizmo. One engaged
/// handle owns the complete drag; hover priority between overlapping
/// kinds is deterministic (see <see cref="WorldGizmo.HitTest"/>).</summary>
public enum WorldHandleKind
{
    TranslateAxis,
    TranslatePlane,
    RotateRing,
    Roll,
    ScaleAxis,
    ScaleUniform,
}

/// <summary>One handle identity: kind plus axis index (0..2; ignored for
/// Roll and ScaleUniform).</summary>
public readonly record struct WorldHandle(WorldHandleKind Kind, int Axis);

/// <summary>A hover/press resolution: the handle, its screen distance, and
/// the underlying ring segment when the handle is a ring.</summary>
public readonly record struct WorldHandleHit(WorldHandle Handle, float Distance, RingHit? Ring);

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
    /// <summary>Unit direction from the camera through the pivot. Used for
    /// front/rear classification and drag-plane normals — both genuinely
    /// about this pivot. It is NOT the roll axis; roll uses the shared
    /// camera view axis so the two surfaces agree (see
    /// <see cref="RotationGizmoRings.CameraViewAxis"/>).</summary>
    public Vector3 ViewDirection;
    /// <summary>The camera's rotation, for the shared roll convention.</summary>
    public Quaternion ViewRotation = Quaternion.Identity;

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
        if (!Matrix4x4.Decompose(view, out _, out var viewRotation, out _))
            return null;

        var result = new WorldGizmoProjection
        {
            ViewProj = viewProj,
            InvViewProj = invViewProj,
            CameraPosition = camera.GetCameraPosition(),
            DisplayCenter = displaySize / 2f,
            Pivot = pivotWorld,
            ViewRotation = viewRotation,
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
            // Roll takes the SHARED camera view axis and the shared view
            // rotation, identical to the inspector's, so the same drag
            // rolls the same way on both surfaces. The X/Y/Z ring points
            // below are still perspective-projected; only roll — a screen
            // circle on both surfaces — shares its convention.
            ViewRotation = projection.ViewRotation,
            RollAxisWorld = RotationGizmoRings.CameraViewAxis(
                projection.ViewRotation),
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
        // Roll is a screen-space circle here exactly as in the inspector,
        // so it uses the shared screen-space derivation rather than a
        // second, perspective one that could disagree in sign.
        if (hit.Axis == RotationGizmoRings.RollAxis)
            return RotationGizmoRings.RollTangent(rings, mouse, hit.Tangent);

        var grabWorld = projection.Pivot + Vector3.Transform(
            RotationGizmoRings.LocalRingPoint(hit.Axis, hit.SegmentIndex),
            rings.Frame) * ringWorldRadius;
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

    // Handle geometry in gizmo units (multiples of WorldScale). Universal
    // pushes the scale knobs beyond the translate arrowheads and the rings
    // outside both, so every handle keeps its own uncluttered grab band.
    private const float ShaftInner = 0.2f;
    private const float ShaftOuter = 1.0f;
    private const float PlaneInner = 0.35f;
    private const float PlaneOuter = 0.65f;
    private const float UniversalKnobDistance = 1.18f;
    private const float UniversalRingRadius = 1.35f;

    /// <summary>
    /// The complete per-frame world-gizmo geometry for one tool: which
    /// handle families are active and their projected screen shapes. Frames
    /// are world-space axis bases — translate uses the orientation mode's
    /// axes, scale always the target's own local axes (stock-gizmo parity),
    /// rings the rotate frame (radial under a Parent pivot).
    /// </summary>
    public sealed class Layout
    {
        public required WorldGizmoProjection Projection;
        public Quaternion TranslateFrame;
        public Quaternion ScaleFrame;
        public ProjectedRings? Rings;
        public float RingWorldRadius;
        public float UiScale;

        public bool TranslateActive;
        public Vector2[] ShaftStart = new Vector2[3];
        public Vector2[] ShaftEnd = new Vector2[3];
        public bool[] ShaftVisible = new bool[3];
        public Vector2[][] PlaneQuad = new Vector2[3][];
        public bool[] PlaneVisible = new bool[3];

        public bool ScaleActive;
        public bool ScaleShafts;
        public Vector2[] ScaleShaftStart = new Vector2[3];
        public Vector2[] ScaleKnob = new Vector2[3];
        public bool[] ScaleVisible = new bool[3];
        public bool UniformActive;
    }

    /// <summary>Builds the layout for the given tool. Handles whose
    /// projection degenerates (behind camera, edge-on plane, shaft shorter
    /// than a few pixels) are marked invisible: not drawn, not hittable.</summary>
    public static Layout Build(
        WorldGizmoProjection projection,
        TransformTool tool,
        Quaternion translateFrame,
        Quaternion scaleFrame,
        Quaternion ringFrame,
        float uiScale)
    {
        var layout = new Layout { Projection = projection, UiScale = uiScale };
        layout.TranslateFrame = translateFrame;
        layout.ScaleFrame = scaleFrame;
        bool universal = tool == TransformTool.Universal;
        float s = projection.WorldScale;

        if (tool is TransformTool.Move || universal)
        {
            layout.TranslateActive = true;
            for (int a = 0; a < 3; a++)
            {
                var axis = FrameAxis(translateFrame, a);
                bool ok = projection.Project(
                    projection.Pivot + axis * (ShaftInner * s), out var start);
                ok &= projection.Project(
                    projection.Pivot + axis * (ShaftOuter * s), out var end);
                layout.ShaftStart[a] = start;
                layout.ShaftEnd[a] = end;
                layout.ShaftVisible[a] = ok &&
                    Vector2.Distance(start, end) > 6f * uiScale;

                // Plane handle a lies between the OTHER two axes; its world
                // normal is axis a. Edge-on planes disappear.
                var u = FrameAxis(translateFrame, (a + 1) % 3);
                var v = FrameAxis(translateFrame, (a + 2) % 3);
                bool facing = MathF.Abs(Vector3.Dot(
                    axis, projection.ViewDirection)) > 0.08f;
                var quad = new Vector2[4];
                bool projected = facing &&
                    projection.Project(projection.Pivot + (u * PlaneInner + v * PlaneInner) * s, out quad[0]) &&
                    projection.Project(projection.Pivot + (u * PlaneOuter + v * PlaneInner) * s, out quad[1]) &&
                    projection.Project(projection.Pivot + (u * PlaneOuter + v * PlaneOuter) * s, out quad[2]) &&
                    projection.Project(projection.Pivot + (u * PlaneInner + v * PlaneOuter) * s, out quad[3]);
                layout.PlaneQuad[a] = quad;
                layout.PlaneVisible[a] = projected;
            }
        }

        if (tool is TransformTool.Scale || universal)
        {
            layout.ScaleActive = true;
            layout.ScaleShafts = !universal;
            layout.UniformActive = true;
            float knobDistance = universal ? UniversalKnobDistance : ShaftOuter;
            for (int a = 0; a < 3; a++)
            {
                var axis = FrameAxis(scaleFrame, a);
                bool ok = projection.Project(
                    projection.Pivot + axis * (ShaftInner * s), out var start);
                ok &= projection.Project(
                    projection.Pivot + axis * (knobDistance * s), out var knob);
                layout.ScaleShaftStart[a] = start;
                layout.ScaleKnob[a] = knob;
                layout.ScaleVisible[a] = ok &&
                    Vector2.Distance(projection.Center, knob) > 8f * uiScale;
            }
        }

        if (tool is TransformTool.Rotate || universal)
        {
            layout.RingWorldRadius = (universal ? UniversalRingRadius : 1f) * s;
            var rings = ProjectRings(
                projection, ringFrame, layout.RingWorldRadius, uiScale);
            if (rings.Valid)
                layout.Rings = rings;
        }
        return layout;
    }

    public static Vector3 FrameAxis(Quaternion frame, int axis) =>
        Vector3.Normalize(Vector3.Transform(
            axis switch
            {
                0 => Vector3.UnitX,
                1 => Vector3.UnitY,
                _ => Vector3.UnitZ,
            },
            frame));

    /// <summary>
    /// Resolves the hovered handle. Priority between overlapping kinds is
    /// deterministic and documented: plane quads, then the uniform-scale
    /// centre, then scale knobs, then translate shafts, then ring
    /// segments and the roll circle (which order X → Y → Z → Roll among
    /// themselves). Within a tier the nearest candidate wins.
    /// </summary>
    public static WorldHandleHit? HitTest(Layout layout, Vector2 mouse, float tolerance)
    {
        if (layout.TranslateActive)
            for (int a = 0; a < 3; a++)
                if (layout.PlaneVisible[a] && PointInQuad(mouse, layout.PlaneQuad[a]))
                    return new WorldHandleHit(
                        new WorldHandle(WorldHandleKind.TranslatePlane, a), 0f, null);

        if (layout.UniformActive &&
            Vector2.Distance(mouse, layout.Projection.Center) <= 9f * layout.UiScale)
            return new WorldHandleHit(
                new WorldHandle(WorldHandleKind.ScaleUniform, 0), 0f, null);

        if (layout.ScaleActive)
        {
            int best = -1;
            float bestDistance = tolerance + 4f * layout.UiScale;
            for (int a = 0; a < 3; a++)
            {
                if (!layout.ScaleVisible[a])
                    continue;
                float distance = Vector2.Distance(mouse, layout.ScaleKnob[a]);
                if (layout.ScaleShafts)
                    distance = MathF.Min(distance, DistanceToSegment(
                        mouse, layout.ScaleShaftStart[a], layout.ScaleKnob[a]));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = a;
                }
            }
            if (best >= 0)
                return new WorldHandleHit(
                    new WorldHandle(WorldHandleKind.ScaleAxis, best), bestDistance, null);
        }

        if (layout.TranslateActive)
        {
            int best = -1;
            float bestDistance = tolerance;
            for (int a = 0; a < 3; a++)
            {
                if (!layout.ShaftVisible[a])
                    continue;
                float distance = DistanceToSegment(
                    mouse, layout.ShaftStart[a], layout.ShaftEnd[a]);
                // The arrowhead extends the grab band past the shaft tip.
                distance = MathF.Min(distance, MathF.Max(0f,
                    Vector2.Distance(mouse, layout.ShaftEnd[a]) - 10f * layout.UiScale));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = a;
                }
            }
            if (best >= 0)
                return new WorldHandleHit(
                    new WorldHandle(WorldHandleKind.TranslateAxis, best), bestDistance, null);
        }

        if (layout.Rings is { } rings &&
            RotationGizmoRings.HitTest(rings, mouse, tolerance) is { } ringHit)
            return new WorldHandleHit(
                ringHit.Axis == RotationGizmoRings.RollAxis
                    ? new WorldHandle(WorldHandleKind.Roll, 0)
                    : new WorldHandle(WorldHandleKind.RotateRing, ringHit.Axis),
                ringHit.Distance, ringHit);

        return null;
    }

    /// <summary>
    /// Draws every active handle with the approved pastel grammar: axis
    /// palette, hover/active emphasis, no plate, no rear arcs, no cursor
    /// decoration — the world counterpart of the inspector styling.
    /// </summary>
    public static void Draw(
        ImDrawListPtr dl,
        Layout layout,
        WorldHandle? hover,
        WorldHandle? active)
    {
        float uiScale = layout.UiScale;

        if (layout.Rings is { } rings)
        {
            RotationGizmoRings.Draw(
                dl, rings,
                hover is { } h ? RingEmphasisAxis(h) : -1,
                active is { } a ? RingEmphasisAxis(a) : -1,
                drawRearArcs: false, uiScale);
        }

        if (layout.TranslateActive)
        {
            for (int a = 0; a < 3; a++)
            {
                if (!layout.PlaneVisible[a])
                    continue;
                bool hot = IsHot(hover, active, WorldHandleKind.TranslatePlane, a);
                var color = AxisColor(a);
                uint fill = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(color with { W = hot ? 0.45f : 0.22f }));
                uint border = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(color with { W = hot ? 1f : 0.7f }));
                var quad = layout.PlaneQuad[a];
                dl.AddQuadFilled(quad[0], quad[1], quad[2], quad[3], fill);
                dl.AddQuad(quad[0], quad[1], quad[2], quad[3], border, 1.5f * uiScale);
            }
            for (int a = 0; a < 3; a++)
            {
                if (!layout.ShaftVisible[a])
                    continue;
                bool hot = IsHot(hover, active, WorldHandleKind.TranslateAxis, a);
                var color = AxisColor(a);
                uint stroke = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(color with { W = hot ? 1f : 0.85f }));
                dl.AddLine(layout.ShaftStart[a], layout.ShaftEnd[a],
                    stroke, (hot ? 4.5f : 3f) * uiScale);
                var direction = Vector2.Normalize(
                    layout.ShaftEnd[a] - layout.ShaftStart[a]);
                var perpendicular = new Vector2(-direction.Y, direction.X);
                var tip = layout.ShaftEnd[a] + direction * 12f * uiScale;
                dl.AddTriangleFilled(
                    tip,
                    layout.ShaftEnd[a] + perpendicular * 5f * uiScale,
                    layout.ShaftEnd[a] - perpendicular * 5f * uiScale,
                    stroke);
            }
        }

        if (layout.ScaleActive)
        {
            for (int a = 0; a < 3; a++)
            {
                if (!layout.ScaleVisible[a])
                    continue;
                bool hot = IsHot(hover, active, WorldHandleKind.ScaleAxis, a);
                var color = AxisColor(a);
                uint stroke = ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(color with { W = hot ? 1f : 0.85f }));
                if (layout.ScaleShafts)
                    dl.AddLine(layout.ScaleShaftStart[a], layout.ScaleKnob[a],
                        stroke, (hot ? 4.5f : 3f) * uiScale);
                float half = (hot ? 6f : 5f) * uiScale;
                dl.AddRectFilled(
                    layout.ScaleKnob[a] - new Vector2(half, half),
                    layout.ScaleKnob[a] + new Vector2(half, half),
                    stroke, 1.5f * uiScale);
            }
        }

        if (layout.UniformActive)
        {
            bool hot = IsHot(hover, active, WorldHandleKind.ScaleUniform, 0);
            var color = new Vector4(1f, 1f, 1f, hot ? 0.95f : 0.55f);
            dl.AddCircleFilled(layout.Projection.Center, 4f * uiScale,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(color with { W = hot ? 0.5f : 0.25f })));
            dl.AddCircle(layout.Projection.Center, 7f * uiScale,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color)),
                0, (hot ? 2.5f : 1.5f) * uiScale);
        }
    }

    private static Vector4 AxisColor(int axis) => axis switch
    {
        0 => Theme.Palette.AxisX,
        1 => Theme.Palette.AxisY,
        _ => Theme.Palette.AxisZ,
    };

    private static bool IsHot(
        WorldHandle? hover, WorldHandle? active, WorldHandleKind kind, int axis)
    {
        var handle = new WorldHandle(kind, axis);
        return hover == handle || active == handle;
    }

    private static int RingEmphasisAxis(WorldHandle? handle) => handle switch
    {
        { Kind: WorldHandleKind.RotateRing, Axis: var axis } => axis,
        { Kind: WorldHandleKind.Roll } => RotationGizmoRings.RollAxis,
        _ => -1,
    };

    private static bool PointInQuad(Vector2 point, Vector2[] quad)
    {
        bool sign = false;
        for (int i = 0; i < 4; i++)
        {
            var a = quad[i];
            var b = quad[(i + 1) % 4];
            float cross = (b.X - a.X) * (point.Y - a.Y) -
                (b.Y - a.Y) * (point.X - a.X);
            if (i == 0)
                sign = cross >= 0f;
            else if (cross >= 0f != sign)
                return false;
        }
        return true;
    }

    private static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lengthSquared = ab.LengthSquared();
        if (lengthSquared < 1e-6f)
            return Vector2.Distance(point, a);
        float t = Math.Clamp(
            Vector2.Dot(point - a, ab) / lengthSquared, 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }
}
