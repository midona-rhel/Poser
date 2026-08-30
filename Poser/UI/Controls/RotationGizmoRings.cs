using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// One projected ring set: screen points, front/rear flags, and the frame
/// they came from. This is a RESULT container, filled by two different
/// projections — the inspector's direction-only basis (<see cref="
/// RotationGizmoRings.Project"/>) and the world overlay's perspective path
/// (<see cref="WorldGizmo.ProjectRings"/>). Consumers that only read
/// screen geometry — drawing, segment hit-testing — work on either; the
/// projections themselves are deliberately not interchangeable.
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
/// The INSPECTOR's rotation-ring projection, plus the ring calculations
/// both surfaces share: frame basis, segment hit testing, ring drawing,
/// the camera view axis and roll tangent conventions, and the Ctrl/Shift
/// sensitivity policy. The world overlay projects its own ring points
/// through real view/projection matrices (<see cref="WorldGizmo"/>) and
/// only borrows what is genuinely common. Both surfaces dispatch results
/// through the existing clean TransformGestureService lifecycle — this
/// class owns no gesture state.
/// </summary>
public static class RotationGizmoRings
{
    public const int RingPoints = 96;
    public const int RollAxis = 3;

    // Ring topology never depends on the camera, target, or gesture. Keep
    // the unit directions once; Project and WorldGizmo.ProjectRings still
    // transform every point from the current frame and view each draw.
    private static readonly Vector3[][] UnitRingPoints = BuildUnitRingPoints();

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
    /// THE INSPECTOR PROJECTION ONLY — the world overlay must not call
    /// this. Projects the three axis rings around the given SCREEN centre
    /// using only the camera's rotation (Brio ImBrio.Gizmo parity): each
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
        var view = camera.GetViewMatrix();
        view.M44 = 1f;
        if (!Matrix4x4.Decompose(view, out _, out var viewRotation, out _))
            return new ProjectedRings { Frame = frame, Center = center };
        return Project(viewRotation, center, frame, radiusPixels);
    }

    /// <summary>Projection from an EXPLICIT view — the camera ball uses a
    /// fixed vantage (45° yaw, looking down the isometric diagonal) so its
    /// axes rest as an equilateral triangle, top up, whatever the live
    /// camera does.</summary>
    public static ProjectedRings Project(
        Quaternion viewRotation,
        Vector2 center,
        Quaternion frame,
        float radiusPixels)
    {
        var rings = new ProjectedRings { Frame = frame, Center = center };
        rings.Valid = true;
        rings.ViewRotation = viewRotation;
        rings.ScreenRadius = radiusPixels;
        rings.RollRadius = radiusPixels + 8f;
        rings.RollAxisWorld = CameraViewAxis(viewRotation);
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
        // Preserve the old fallback semantics for callers outside the normal
        // range: any non-X/Y axis means Z, and an invalid index still follows
        // the mathematical formula instead of changing its exception shape.
        if ((uint)index >= RingPoints)
            return ComputeLocalRingPoint(axis, index);

        return UnitRingPoints[axis is 0 or 1 ? axis : 2][index];
    }

    private static Vector3[][] BuildUnitRingPoints()
    {
        var points = new Vector3[3][];
        for (int axis = 0; axis < points.Length; axis++)
        {
            points[axis] = new Vector3[RingPoints];
            for (int index = 0; index < RingPoints; index++)
                points[axis][index] = ComputeLocalRingPoint(axis, index);
        }
        return points;
    }

    private static Vector3 ComputeLocalRingPoint(int axis, int index)
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
    /// THE camera view axis, and the only definition of it: the world
    /// direction that maps to camera-space +Z, which is away from the
    /// viewer under the same convention that classifies Z &lt; 0 as front.
    /// Roll rotates about this on BOTH surfaces. Deriving it once is the
    /// point — the inspector and the world overlay project through
    /// different matrices, and two independent derivations could disagree
    /// in sign and roll opposite ways for the same drag.
    /// </summary>
    public static Vector3 CameraViewAxis(Quaternion viewRotation) =>
        Vector3.Normalize(FromCamera(Vector3.UnitZ, viewRotation));

    /// <summary>
    /// The positive-roll screen direction under the pointer. Roll is a
    /// screen-space circle on both surfaces, so both use this one
    /// screen-space derivation: the perpendicular of the pointer's radial
    /// offset, signed by rotating that radial through the shared camera
    /// basis. Identical drag ⇒ identical roll, inspector or world.
    /// </summary>
    public static Vector2 RollTangent(
        ProjectedRings rings,
        Vector2 mouse,
        Vector2 fallback)
    {
        var radial = mouse - rings.Center;
        if (radial.LengthSquared() < 1e-6f)
            return fallback;
        radial = Vector2.Normalize(radial);
        var grabDirection = FromCamera(
            new Vector3(radial.X, radial.Y, 0f), rings.ViewRotation);
        var rotated = Vector3.Transform(
            grabDirection,
            Quaternion.CreateFromAxisAngle(rings.RollAxisWorld, 0.05f));
        var a = ToCamera(grabDirection, rings.ViewRotation);
        var b = ToCamera(rotated, rings.ViewRotation);
        var tangent = new Vector2(b.X - a.X, b.Y - a.Y);
        return tangent.LengthSquared() < 1e-8f
            ? fallback
            : Vector2.Normalize(tangent);
    }

    /// <summary>No axis is locked. The value a lock is cleared to.</summary>
    public const int NoLock = -1;

    /// <summary>
    /// Nearest visible projected ring segment within tolerance; the outer
    /// roll circle competes last. Exact ties resolve X → Y → Z → Roll.
    ///
    /// <para><paramref name="lockedAxis"/> is Brio's ring lock
    /// (<c>ImBrioGizmo</c>: <c>lockedAxis == null || lockedAxis == axis</c>):
    /// while one axis is locked, no other ring hit-tests at all, so a drag
    /// started anywhere near the gizmo turns that one axis and the rings that
    /// happen to cross under the pointer cannot steal it.</para>
    /// </summary>
    public static RingHit? HitTest(
        ProjectedRings rings,
        Vector2 mouse,
        float tolerance,
        int lockedAxis = NoLock)
    {
        if (!rings.Valid)
            return null;
        int axis = -1;
        int segment = 0;
        var tangent = Vector2.Zero;
        float best = tolerance;
        for (int a = 0; a < 3; a++)
        {
            if (lockedAxis != NoLock && lockedAxis != a)
                continue;
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
        if ((lockedAxis == NoLock || lockedAxis == RollAxis) &&
            radialLength > 1e-3f &&
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
    /// Draws the ring set with the approved pastel grammar: axis palette,
    /// hover/active emphasis, wide outer roll ring. Purely screen-space, so
    /// it serves either projection's output; the world overlay passes
    /// <paramref name="drawRearArcs"/> = false so only meaningful front
    /// arcs appear over the game.
    /// </summary>
    public static void Draw(
        ImDrawListPtr dl,
        ProjectedRings rings,
        int hoverAxis,
        int dragAxis,
        bool drawRearArcs,
        float scale,
        int lockedAxis = NoLock)
    {
        if (!rings.Valid)
            return;

        // Brio recolours the locked ring and leaves the rest their own colour;
        // in this palette the equivalent statement is that the OTHERS recede,
        // because a lock is about what the pointer can still reach.
        static float LockFade(int axis, int lockedAxis) =>
            lockedAxis == NoLock || lockedAxis == axis ? 1f : 0.25f;

        // Wide outer camera-roll ring first, then rear arcs, then front arcs.
        {
            bool hot = hoverAxis == RollAxis || dragAxis == RollAxis;
            var rollColor = new Vector4(
                1f, 1f, 1f,
                (hot ? 0.95f : 0.55f) * LockFade(RollAxis, lockedAxis));
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
                    0 => Crystarium.ActiveTheme.Palette.AxisX,
                    1 => Crystarium.ActiveTheme.Palette.AxisY,
                    _ => Crystarium.ActiveTheme.Palette.AxisZ,
                };
                bool hot = hoverAxis == a || dragAxis == a;
                float alpha = (frontPass ? (hot ? 1f : 0.85f) : 0.12f)
                    * LockFade(a, lockedAxis);
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
    /// DIRECTION and re-projecting through the inspector's own
    /// direction-only basis, so drag direction matches the applied
    /// rotation on every ring. The world overlay has its own perspective
    /// equivalent; only the roll branch below is common to both.
    /// </summary>
    public static Vector2 PositiveTangent(
        ProjectedRings rings,
        RingHit hit,
        Vector2 mouse)
    {
        if (!rings.Valid)
            return hit.Tangent;
        if (hit.Axis == RollAxis)
            return RollTangent(rings, mouse, hit.Tangent);
        var grabDirection = Vector3.Transform(
            LocalRingPoint(hit.Axis, hit.SegmentIndex), rings.Frame);
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
/// <summary>Whether the shell windows hide because a world manipulation
/// is HELD (#77): the setting AND a live drag — hovering a handle never
/// hides. Written once per frame by the UI root; the shell fades over
/// 250 ms rather than popping, and windows skip their draw only when
/// fully faded. Reference images and the overlays deliberately stay
/// visible.</summary>
public static class ManipulationHide
{
    public static bool Active;

    /// <summary>The dependent option: the world gizmo's CHROME rides the
    /// same fade — the drag's own sweep and readout never do.</summary>
    public static bool HideGizmo;

    /// <summary>The shell's eased opacity: 1 shown, 0 hidden.</summary>
    public static float Opacity { get; private set; } = 1f;

    private const float FadeSeconds = 0.10f;

    /// <summary>Advanced once per frame by the UI root, after Active is
    /// written.</summary>
    public static void Advance()
    {
        float step = ImGui.GetIO().DeltaTime / FadeSeconds;
        Opacity = Active
            ? MathF.Max(0f, Opacity - step)
            : MathF.Min(1f, Opacity + step);
    }

    /// <summary>Fully faded: the shell windows skip their draw.</summary>
    public static bool Hidden => Opacity <= 0f;

    /// <summary>Scopes the fade over one window's draw: pushes the global
    /// alpha (which every Crystarium color multiplies through) while the
    /// shell is mid-fade, and pops it on ANY exit path.</summary>
    public static FadeHandle FadeScope()
    {
        bool pushed = Opacity < 1f;
        if (pushed)
            ImGui.PushStyleVar(
                ImGuiStyleVar.Alpha, ImGui.GetStyle().Alpha * Opacity);
        return new FadeHandle(pushed);
    }

    public readonly struct FadeHandle : IDisposable
    {
        private readonly bool _pushed;
        internal FadeHandle(bool pushed) => _pushed = pushed;
        public void Dispose()
        {
            if (_pushed)
                ImGui.PopStyleVar();
        }
    }
}

/// <summary>Frame-stamped hold for a LIVE world drag — the hide signal.
/// Distinct from <see cref="GizmoPointerOwnership"/>, which hover also
/// holds so a click on a handle is never a pick: only a held gesture
/// holds this.</summary>
public static class ManipulationDrag
{
    private static int _heldUntilFrame = -1;

    public static void Hold() =>
        _heldUntilFrame = ImGui.GetFrameCount() + 1;

    public static bool Held =>
        ImGui.GetFrameCount() <= _heldUntilFrame;
}

public static class GizmoPointerOwnership
{
    private static int _ownedUntilFrame = -1;

    /// <summary>Call every frame the pointer engages a custom gizmo.</summary>
    public static void Hold() =>
        _ownedUntilFrame = ImGui.GetFrameCount() + 1;

    public static bool Owned =>
        ImGui.GetFrameCount() <= _ownedUntilFrame;
}
