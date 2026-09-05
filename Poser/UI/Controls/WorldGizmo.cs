using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>Interactive in-world gizmo handle kinds.</summary>
public enum WorldHandleKind
{
    TranslateAxis,
    TranslatePlane,
    TranslateCenter,
    RotateRing,
    Roll,
    ScaleAxis,
    ScaleUniform,
}

/// <summary>Handle kind and axis index.</summary>
public readonly record struct WorldHandle(WorldHandleKind Kind, int Axis);

/// <summary>Resolved handle and optional ring segment.</summary>
public readonly record struct WorldHandleHit(WorldHandle Handle, float Distance, RingHit? Ring);

/// <summary>Perspective projection and drag-plane helpers for the world gizmo.
/// Geometry uses the active camera matrices and a stable pivot size.</summary>
public sealed class WorldGizmoProjection
{
    public Matrix4x4 ViewProj;
    public Matrix4x4 InvViewProj;
    /// <summary>Camera eye derived from <see cref="ViewProj"/>.</summary>
    public Vector3 CameraPosition;
    public Vector2 DisplayCenter;
    /// <summary>The gizmo pivot in world space.</summary>
    public Vector3 Pivot;
    /// <summary>The projected pivot in screen pixels.</summary>
    public Vector2 Center;
    /// <summary>World length corresponding to the requested handle size.</summary>
    public float WorldScale;
    /// <summary>Requested handle size in screen pixels.</summary>
    public float ScreenScale;
    /// <summary>Unit direction from camera to pivot.</summary>
    public Vector3 ViewDirection;
    /// <summary>Image-plane depth direction for the fixed-size rotation ball.</summary>
    public Vector3 RingViewDirection;
    private float _pivotClipW;
    /// <summary>The camera's rotation, for the shared roll convention.</summary>
    public Quaternion ViewRotation = Quaternion.Identity;

    /// <summary>Builds one projection, or null when it is unusable.</summary>
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
        if (!Matrix4x4.Invert(view, out var invView))
            return null;
        if (!Matrix4x4.Decompose(view, out _, out var viewRotation, out _))
            return null;

        var result = new WorldGizmoProjection
        {
            ViewProj = viewProj,
            InvViewProj = invViewProj,
            CameraPosition = invView.Translation,
            DisplayCenter = displaySize / 2f,
            Pivot = pivotWorld,
            ViewRotation = viewRotation,
            ScreenScale = sizePixels,
        };
        if (!result.Project(pivotWorld, out var center))
            return null;
        result.Center = center;

        var toPivot = pivotWorld - result.CameraPosition;
        if (toPivot.LengthSquared() < 1e-8f)
            return null;
        result.ViewDirection = Vector3.Normalize(toPivot);
        var depthGradient = new Vector3(viewProj.M14, viewProj.M24, viewProj.M34);
        result.RingViewDirection = depthGradient.LengthSquared() > 1e-12f
            ? Vector3.Normalize(depthGradient)
            : result.ViewDirection;
        result._pivotClipW = Vector3.Dot(depthGradient, pivotWorld) + viewProj.M44;

        // Measure along camera-right: both points have the same view depth,
        // so pixels per world unit does not change across the viewport.
        // Perpendicular to the eye-to-pivot ray is NOT the image plane off-centre.
        var lateral = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitX, invView));
        if (!result.Project(pivotWorld + lateral, out var offsetScreen))
            return null;
        float pixelsPerWorldUnit = Vector2.Distance(offsetScreen, center);
        if (pixelsPerWorldUnit < 1e-3f)
            return null;
        result.WorldScale = sizePixels / pixelsPerWorldUnit;
        return result;
    }

    /// <summary>Projects a world point to screen pixels.</summary>
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

    /// <summary>Projects a rotation-ball offset at the pivot's fixed depth.
    /// The pivot is perspective-placed, but the control itself cannot warp.</summary>
    public Vector2 ProjectRingOffset(Vector3 worldOffset)
    {
        var clip = Vector4.Transform(new Vector4(worldOffset, 0f), ViewProj);
        // Do not divide each sample by its own depth: that stretches the ball
        // off-centre and can send its near side across the camera plane.
        return Center + new Vector2(DisplayCenter.X * clip.X, -DisplayCenter.Y * clip.Y) / _pivotClipW;
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

/// <summary>World-space gizmo layout, hit testing, and drawing.</summary>
public static class WorldGizmo
{
    /// <summary>Projects the three world rotation rings and roll ring.</summary>
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
            // Roll keeps the shared camera-axis convention.
            ViewRotation = projection.ViewRotation,
            RollAxisWorld = RotationGizmoRings.CameraViewAxis(
                projection.ViewRotation),
        };
        rings.Points = new Vector2[3][];
        rings.Depth = new float[3][];
        for (int a = 0; a < 3; a++)
        {
            rings.Points[a] = new Vector2[RotationGizmoRings.RingPoints];
            rings.Depth[a] = new float[RotationGizmoRings.RingPoints];
            rings.FrontCutoff[a] = RotationGizmoRings.GrowingArcCutoff(
                RotationGizmoRings.AxisWorld(rings, a), projection.RingViewDirection);
            for (int i = 0; i < RotationGizmoRings.RingPoints; i++)
            {
                var direction = Vector3.Transform(RotationGizmoRings.LocalRingPoint(a, i), frame);
                rings.Points[a][i] = projection.ProjectRingOffset(direction * ringWorldRadius);
                // Unit-ring depth keeps the arc cut independent of gizmo size.
                rings.Depth[a][i] = Vector3.Dot(direction, projection.RingViewDirection);
            }
        }
        // All rings use the requested size, not a sampled bounding circle.
        // Roll's draw, pick and sweep share this screen-space radius.
        rings.ScreenRadius = projection.ScreenScale * (ringWorldRadius / projection.WorldScale);
        rings.RollRadius = rings.ScreenRadius + 8f * scale;
        rings.Valid = true;
        return rings;
    }

    /// <summary>Returns the projected positive rotation tangent.</summary>
    public static Vector2 PositiveRingTangent(
        WorldGizmoProjection projection,
        ProjectedRings rings,
        RingHit hit,
        Vector2 mouse,
        float ringWorldRadius)
    {
        // Roll uses the shared screen-space tangent.
        if (hit.Axis == RotationGizmoRings.RollAxis)
            return RotationGizmoRings.RollTangent(rings, mouse, hit.Tangent);

        var grabOffset = Vector3.Transform(
            RotationGizmoRings.LocalRingPoint(hit.Axis, hit.SegmentIndex),
            rings.Frame) * ringWorldRadius;
        var axisWorld = RotationGizmoRings.AxisWorld(rings, hit.Axis);
        var rotated = Vector3.Transform(
            grabOffset,
            Quaternion.CreateFromAxisAngle(axisWorld, 0.05f));
        var a = projection.ProjectRingOffset(grabOffset);
        var b = projection.ProjectRingOffset(rotated);
        var tangent = b - a;
        return tangent.LengthSquared() < 1e-8f
            ? hit.Tangent
            : Vector2.Normalize(tangent);
    }

    // Handle geometry uses multiples of WorldScale.
    private const float ShaftInner = 0.2f;
    private const float ShaftOuter = 1.0f;
    private const float PlaneInner = 0.35f;
    private const float PlaneOuter = 0.65f;
    private const float UniversalKnobDistance = 1.18f;
    private const float UniversalRingRadius = 1.35f;

    /// <summary>Projected handle geometry for one tool frame.</summary>
    public sealed class Layout
    {
        public required WorldGizmoProjection Projection;
        public Quaternion TranslateFrame;
        public Quaternion ScaleFrame;
        public ProjectedRings? Rings;
        public float RingWorldRadius;
        public float UiScale;

        /// <summary>Latched camera-facing signs for linear handles.</summary>
        public float[] TranslateSign = [1f, 1f, 1f];
        public float[] ScaleSign = [1f, 1f, 1f];

        public Vector3 SignedTranslateAxis(int axis) =>
            FrameAxis(TranslateFrame, axis) * TranslateSign[axis];

        public Vector3 SignedScaleAxis(int axis) =>
            FrameAxis(ScaleFrame, axis) * ScaleSign[axis];

        public bool TranslateActive;
        public bool TranslateCenterActive;
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

    /// <summary>Builds visible and hittable geometry for the selected tool.
    /// Degenerate projections are marked invisible.</summary>
    public static Layout Build(
        WorldGizmoProjection projection,
        TransformTool tool,
        Quaternion translateFrame,
        Quaternion scaleFrame,
        Quaternion ringFrame,
        float uiScale,
        float[]? heldTranslateSigns = null,
        float[]? heldScaleSigns = null,
        bool universalCenterTranslates = false)
    {
        var layout = new Layout { Projection = projection, UiScale = uiScale };
        layout.TranslateFrame = translateFrame;
        layout.ScaleFrame = scaleFrame;
        bool universal = tool == TransformTool.Universal;
        float s = projection.WorldScale;

        if (tool is TransformTool.Move || universal)
        {
            layout.TranslateActive = true;
            // Move exposes the centre as a camera-plane handle; Universal
            // keeps its centre for uniform scale unless the user gave it
            // to translation.
            layout.TranslateCenterActive =
                tool == TransformTool.Move || (universal && universalCenterTranslates);
            for (int a = 0; a < 3; a++)
                layout.TranslateSign[a] = heldTranslateSigns?[a] ?? AxisFlipSign(
                    FrameAxis(translateFrame, a), projection.ViewDirection);
            for (int a = 0; a < 3; a++)
            {
                var axis = layout.SignedTranslateAxis(a);
                bool ok = projection.Project(
                    projection.Pivot + axis * (ShaftInner * s), out var start);
                ok &= projection.Project(
                    projection.Pivot + axis * (ShaftOuter * s), out var end);
                layout.ShaftStart[a] = start;
                layout.ShaftEnd[a] = end;
                layout.ShaftVisible[a] = ok &&
                    Vector2.Distance(start, end) > 6f * uiScale;

                // Plane a uses the other two signed axes and hides edge-on.
                var u = layout.SignedTranslateAxis((a + 1) % 3);
                var v = layout.SignedTranslateAxis((a + 2) % 3);
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
            layout.UniformActive = !(universal && universalCenterTranslates);
            float knobDistance = universal ? UniversalKnobDistance : ShaftOuter;
            for (int a = 0; a < 3; a++)
                layout.ScaleSign[a] = heldScaleSigns?[a] ?? AxisFlipSign(
                    FrameAxis(scaleFrame, a), projection.ViewDirection);
            for (int a = 0; a < 3; a++)
            {
                var axis = layout.SignedScaleAxis(a);
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

    /// <summary>Inside this dot-product band, edge-on axes keep sign +1.</summary>
    private const float AxisFlipEpsilon = 1e-6f;

    /// <summary>Face the camera's side of the pivot plane, not its look
    /// direction. Turning the camera in place must not flip the arrows.</summary>
    public static float AxisFlipSign(Vector3 axisWorld, Vector3 cameraToPivot) =>
        Vector3.Dot(axisWorld, cameraToPivot) > AxisFlipEpsilon ? -1f : 1f;

    public static Vector3 FrameAxis(Quaternion frame, int axis) =>
        Vector3.Normalize(Vector3.Transform(
            axis switch
            {
                0 => Vector3.UnitX,
                1 => Vector3.UnitY,
                _ => Vector3.UnitZ,
            },
            frame));

    /// <summary>Applies one scale factor to every starting component. Keeping
    /// this operation component-wise preserves the starting ratios.</summary>
    internal static Vector3 ApplyUniformScale(Vector3 start, float factor) =>
        start * factor;

    /// <summary>Returns one translated world step for a frozen handle hit.
    /// Centre and plane handles use the full plane delta; axis handles keep
    /// only their signed axis component.</summary>
    internal static Vector3 TranslationStep(
        WorldHandleKind kind, Vector3 hit, Vector3 previousHit,
        Vector3 signedAxis) =>
        kind == WorldHandleKind.TranslateAxis
            ? signedAxis * Vector3.Dot(hit - previousHit, signedAxis)
            : hit - previousHit;

    /// <summary>Converts a frozen camera-plane hit to local translation for
    /// the centre's ray-snap path.</summary>
    internal static Vector3 TranslationFromFrozenPlane(
        Vector3 startPosition, Vector3 hit, Vector3 pivotWorld,
        Matrix4x4 inverseModel) =>
        startPosition + Vector3.TransformNormal(
            hit - pivotWorld, inverseModel);

    /// <summary>Resolves drawn handle shapes plus a small pixel tolerance,
    /// not broad circular hitboxes that cover neighbouring scene markers.</summary>
    public static WorldHandleHit? HitTest(Layout layout, Vector2 mouse, float tolerance)
    {
        if (layout.TranslateActive)
            for (int a = 0; a < 3; a++)
                if (layout.PlaneVisible[a] && PointInQuad(mouse, layout.PlaneQuad[a]))
                    return new WorldHandleHit(
                        new WorldHandle(WorldHandleKind.TranslatePlane, a), 0f, null);

        if (layout.TranslateCenterActive &&
            Vector2.Distance(mouse, layout.Projection.Center) <= 7f * layout.UiScale + tolerance)
            return new WorldHandleHit(
                new WorldHandle(WorldHandleKind.TranslateCenter, 0), 0f, null);

        if (layout.UniformActive &&
            Vector2.Distance(mouse, layout.Projection.Center) <= 7f * layout.UiScale + tolerance)
            return new WorldHandleHit(
                new WorldHandle(WorldHandleKind.ScaleUniform, 0), 0f, null);

        if (layout.ScaleActive)
        {
            int best = -1;
            float bestDistance = tolerance;
            for (int a = 0; a < 3; a++)
            {
                if (!layout.ScaleVisible[a])
                    continue;
                var offset = Vector2.Abs(mouse - layout.ScaleKnob[a]);
                float distance = MathF.Max(0f, MathF.Max(offset.X, offset.Y) - 5f * layout.UiScale);
                if (layout.ScaleShafts)
                    distance = MathF.Min(distance, MathF.Max(0f, DistanceToSegment(
                        mouse, layout.ScaleShaftStart[a], layout.ScaleKnob[a]) - 1.5f * layout.UiScale));
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
                float distance = MathF.Max(0f, DistanceToSegment(
                    mouse, layout.ShaftStart[a], layout.ShaftEnd[a]) - 1.5f * layout.UiScale);
                ArrowHead(layout, a, out var tip, out var left, out var right);
                distance = MathF.Min(distance, DistanceToTriangle(mouse, tip, left, right));
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
            RotationGizmoRings.HitTest(rings, mouse, tolerance + 1.75f * layout.UiScale) is { } ringHit)
            return new WorldHandleHit(
                ringHit.Axis == RotationGizmoRings.RollAxis
                    ? new WorldHandle(WorldHandleKind.Roll, 0)
                    : new WorldHandle(WorldHandleKind.RotateRing, ringHit.Axis),
                ringHit.Distance, ringHit);

        return null;
    }

    /// <summary>Draws active handles with axis and hover emphasis.</summary>
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
            bool centerHot = IsHot(
                hover, active, WorldHandleKind.TranslateCenter, 0);
            if (layout.TranslateCenterActive)
            {
                var centerColor = new Vector4(1f, 1f, 1f,
                    centerHot ? 0.95f : 0.55f);
                dl.AddCircleFilled(
                    layout.Projection.Center, 4f * uiScale,
                    ImGui.ColorConvertFloat4ToU32(
                        ColorEx.ApplyAlpha(centerColor with
                        { W = centerHot ? 0.5f : 0.25f })));
                dl.AddCircle(
                    layout.Projection.Center, 7f * uiScale,
                    ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(centerColor)),
                    0, (centerHot ? 2.5f : 1.5f) * uiScale);
            }
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
                ArrowHead(layout, a, out var tip, out var left, out var right);
                dl.AddTriangleFilled(tip, left, right, stroke);
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
        0 => Crystarium.ActiveTheme.Palette.AxisX,
        1 => Crystarium.ActiveTheme.Palette.AxisY,
        _ => Crystarium.ActiveTheme.Palette.AxisZ,
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

    private static void ArrowHead(Layout layout, int axis, out Vector2 tip, out Vector2 left, out Vector2 right)
    {
        var direction = Vector2.Normalize(layout.ShaftEnd[axis] - layout.ShaftStart[axis]);
        var perpendicular = new Vector2(-direction.Y, direction.X);
        tip = layout.ShaftEnd[axis] + direction * 12f * layout.UiScale;
        left = layout.ShaftEnd[axis] + perpendicular * 5f * layout.UiScale;
        right = layout.ShaftEnd[axis] - perpendicular * 5f * layout.UiScale;
    }

    private static float DistanceToTriangle(Vector2 point, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Side(Vector2 p, Vector2 from, Vector2 to) =>
            (to.X - from.X) * (p.Y - from.Y) - (to.Y - from.Y) * (p.X - from.X);
        float ab = Side(point, a, b), bc = Side(point, b, c), ca = Side(point, c, a);
        if (!(ab < 0f || bc < 0f || ca < 0f) || !(ab > 0f || bc > 0f || ca > 0f))
            return 0f;
        return MathF.Min(DistanceToSegment(point, a, b),
            MathF.Min(DistanceToSegment(point, b, c), DistanceToSegment(point, c, a)));
    }

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
