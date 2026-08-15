extern alias ProductionPoser;

using System.Numerics;
using Poser.Services;
using ProductionPoser::Poser.UI.Controls;

namespace Poser.ContractTests;

/// <summary>
/// Characterization of the world gizmo's camera-facing axis signs — stock
/// ImGuizmo AllowAxisFlip parity (Ktisis ships it default-ON and re-arms it
/// every frame): translate and scale handles extend along whichever of ±axis
/// faces the camera, the plane quads take the same signed pair, the DRAG
/// mapping consumes the very same signed axis the arrow was drawn with, and
/// rotation rings never flip. ImGuizmo's ComputeTripodAxisAndVisibility
/// decides by comparing the ±axis projected lengths with a FLT_EPSILON tie
/// band (ties keep +1) and latches the factors while a manipulation is live;
/// projected length scales by 1/clip-w, so the sign here is the analytic
/// equivalent — flip exactly when the axis's clip-w (camera-forward)
/// component exceeds the epsilon band; NOT the camera→pivot direction, which
/// agrees only at screen centre — with the same latch.
/// </summary>
public sealed class GizmoAxisFlipContractTests
{
    /// <summary>A real perspective camera: LookAt view + FoV projection,
    /// row-vector convention, exactly what WorldGizmoProjection consumes.</summary>
    private sealed class FakeCamera(Vector3 position, Vector3 target) : ICameraService
    {
        public Matrix4x4 GetViewMatrix() =>
            Matrix4x4.CreateLookAt(position, target, Vector3.UnitY);

        public Matrix4x4 GetProjectionMatrix() =>
            Matrix4x4.CreatePerspectiveFieldOfView(
                MathF.PI / 4f, 16f / 9f, 0.1f, 100f);

        public Vector3 GetCameraPosition() => position;

        public bool WorldToScreen(Vector3 worldPos, out Vector2 screenPos)
        {
            screenPos = default;
            return false;
        }

        public Vector3 ScreenToWorld(Vector2 screenPos, float depth) => default;

        public float GetDepthToPosition(Vector3 worldPos) =>
            Vector3.Distance(position, worldPos);

        public Vector3 GetLookDirection() =>
            Vector3.Normalize(target - position);
    }

    private static WorldGizmoProjection Projection(Vector3 cameraPosition)
    {
        var projection = WorldGizmoProjection.Create(
            new FakeCamera(cameraPosition, Vector3.Zero),
            new Vector2(1920f, 1080f),
            Vector3.Zero,
            80f);
        Assert.NotNull(projection);
        return projection!;
    }

    private static Vector3 Axis(int axis) => axis switch
    {
        0 => Vector3.UnitX,
        1 => Vector3.UnitY,
        _ => Vector3.UnitZ,
    };

    /// <summary>The screen-space unit direction of a world direction at the
    /// pivot — the ground truth a drawn arrow must agree with.</summary>
    private static Vector2 ScreenDirection(
        WorldGizmoProjection projection, Vector3 worldDirection)
    {
        Assert.True(projection.Project(
            projection.Pivot + worldDirection * projection.WorldScale,
            out var tip));
        var direction = tip - projection.Center;
        Assert.True(direction.LengthSquared() > 1e-6f);
        return Vector2.Normalize(direction);
    }

    // ---- Sign derivation: all 6 half-spaces --------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Axis_pointing_away_from_the_camera_flips_negative(int axis)
    {
        // viewForward is the camera-forward (clip-w gradient) unit vector; an
        // axis aligned with it points away from the camera and must flip
        // (ImGuizmo: the negated direction projects longer). Both polarities
        // per axis = 6 half-spaces.
        Assert.Equal(-1f, WorldGizmo.AxisFlipSign(Axis(axis), Axis(axis)));
        Assert.Equal(1f, WorldGizmo.AxisFlipSign(-Axis(axis), Axis(axis)));
    }

    // ---- Boundary: no flapping when the camera sits on the axis plane ------

    [Fact]
    public void Edge_on_axis_keeps_positive_sign()
    {
        // Exactly perpendicular: ±axis project equally; ImGuizmo's strict
        // comparison keeps +1. So does ours.
        Assert.Equal(1f, WorldGizmo.AxisFlipSign(Vector3.UnitX, Vector3.UnitZ));
        Assert.Equal(1f, WorldGizmo.AxisFlipSign(Vector3.UnitY, Vector3.UnitX));
        Assert.Equal(1f, WorldGizmo.AxisFlipSign(Vector3.UnitZ, Vector3.UnitY));
    }

    [Fact]
    public void Sign_is_stable_inside_the_epsilon_tie_band()
    {
        // Float noise on the flip side of exact perpendicular stays +1 until
        // the dot exceeds the tie band — the ImGuizmo FLT_EPSILON equivalent
        // that stops per-frame sign flapping at the boundary. Beyond the band
        // the flip is genuine.
        var barelyAway = Vector3.Normalize(new Vector3(1f, 0f, 5e-7f));
        var clearlyAway = Vector3.Normalize(new Vector3(1f, 0f, 1e-3f));
        Assert.Equal(1f, WorldGizmo.AxisFlipSign(barelyAway, Vector3.UnitZ));
        Assert.Equal(-1f, WorldGizmo.AxisFlipSign(clearlyAway, Vector3.UnitZ));
    }

    [Fact]
    public void Off_center_pivot_flips_by_camera_forward_not_pivot_direction()
    {
        // Camera at (0,0,10) looking at the origin (forward (0,0,-1)) with
        // the pivot far off-centre at (5,0,2): the camera→pivot ray leans
        // hard +X, but the clip-w component of +X is exactly zero — screen
        // scale is 1/w and w grows along camera FORWARD, not along the pivot
        // ray. ImGuizmo therefore does NOT flip X here; a camera→pivot
        // derivation would, and would draw the arrow opposite to Ktisis at
        // the screen edge.
        var projection = WorldGizmoProjection.Create(
            new FakeCamera(new Vector3(0f, 0f, 10f), Vector3.Zero),
            new Vector2(1920f, 1080f),
            new Vector3(5f, 0f, 2f),
            80f);
        Assert.NotNull(projection);
        var layout = WorldGizmo.Build(
            projection!, TransformTool.Move,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, 1f);

        // The discriminator: the pivot ray genuinely leans +X …
        Assert.True(Vector3.Dot(Vector3.UnitX, projection!.ViewDirection) > 0.1f);
        // … yet X does not flip, because camera forward decides.
        Assert.Equal(1f, layout.TranslateSign[0]);

        // Every sign equals the clip-w criterion read straight off the raw
        // ViewProj matrix — the reviewer-stated ImGuizmo reduction, computed
        // here independently of WorldGizmoProjection.ViewForward.
        var m = projection.ViewProj;
        for (int a = 0; a < 3; a++)
        {
            var axis = WorldGizmo.FrameAxis(Quaternion.Identity, a);
            float wComponent =
                m.M14 * axis.X + m.M24 * axis.Y + m.M34 * axis.Z;
            Assert.Equal(
                wComponent > 1e-6f ? -1f : 1f, layout.TranslateSign[a]);
        }
    }

    // ---- Build: drawn arrows and the drag axis are one vector --------------

    [Fact]
    public void Away_facing_translate_axes_flip_and_toward_facing_stay()
    {
        // Camera in the (-X,-Z) quadrant: world +X and +Z point away → flip;
        // +Y stays perpendicular-ish → no flip.
        var projection = Projection(new Vector3(-5f, 0f, -5f));
        var layout = WorldGizmo.Build(
            projection, TransformTool.Move,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, 1f);

        Assert.Equal(-1f, layout.TranslateSign[0]);
        Assert.Equal(1f, layout.TranslateSign[1]);
        Assert.Equal(-1f, layout.TranslateSign[2]);
    }

    [Fact]
    public void Drawn_translate_arrows_agree_with_the_signed_drag_axis()
    {
        // The invariant the drag mapping relies on: the shaft drawn on screen
        // runs along the projection of SignedTranslateAxis — the exact vector
        // BeginGesture freezes — so the arrow and the drag agree by
        // construction, on both flipped and unflipped axes.
        var projection = Projection(new Vector3(-5f, 3f, -5f));
        var layout = WorldGizmo.Build(
            projection, TransformTool.Move,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, 1f);

        for (int a = 0; a < 3; a++)
        {
            if (!layout.ShaftVisible[a])
                continue;
            var drawn = Vector2.Normalize(
                layout.ShaftEnd[a] - layout.ShaftStart[a]);
            var drag = ScreenDirection(
                projection, layout.SignedTranslateAxis(a));
            Assert.True(
                Vector2.Dot(drawn, drag) > 0.99f,
                $"Axis {a}: drawn arrow disagrees with signed drag axis.");
        }
    }

    [Fact]
    public void Plane_quads_take_the_signed_axis_pair()
    {
        var projection = Projection(new Vector3(-4f, -5f, -6f));
        var layout = WorldGizmo.Build(
            projection, TransformTool.Move,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, 1f);

        for (int a = 0; a < 3; a++)
        {
            if (!layout.PlaneVisible[a])
                continue;
            var quad = layout.PlaneQuad[a];
            var centroid = (quad[0] + quad[1] + quad[2] + quad[3]) / 4f;
            var actual = Vector2.Normalize(centroid - projection.Center);
            var expected = ScreenDirection(
                projection,
                Vector3.Normalize(
                    layout.SignedTranslateAxis((a + 1) % 3) +
                    layout.SignedTranslateAxis((a + 2) % 3)));
            Assert.True(
                Vector2.Dot(actual, expected) > 0.9f,
                $"Plane {a}: quad does not sit on the signed u/v side.");
        }
    }

    [Fact]
    public void Scale_knobs_flip_with_their_own_frame()
    {
        // Scale always uses the target's local axes; rotate the frame 180°
        // about Y so local +X is world -X. Camera on world +X: world -X points
        // away → local X flips; local Z (world -Z) points toward → stays.
        var projection = Projection(new Vector3(6f, 0f, 0f));
        var scaleFrame = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
        var layout = WorldGizmo.Build(
            projection, TransformTool.Scale,
            Quaternion.Identity, scaleFrame, Quaternion.Identity, 1f);

        Assert.Equal(-1f, layout.ScaleSign[0]);

        for (int a = 0; a < 3; a++)
        {
            if (!layout.ScaleVisible[a])
                continue;
            var drawn = Vector2.Normalize(
                layout.ScaleKnob[a] - projection.Center);
            var drag = ScreenDirection(projection, layout.SignedScaleAxis(a));
            Assert.True(
                Vector2.Dot(drawn, drag) > 0.99f,
                $"Scale axis {a}: knob disagrees with signed drag axis.");
        }
    }

    // ---- Latch: a live gesture pins the engaged signs ----------------------

    [Fact]
    public void Held_signs_override_the_camera_derivation()
    {
        // ImGuizmo reuses tripodState.mAxisFactor while mbUsing so a translate
        // that carries the pivot across the camera plane never flips the drawn
        // handle mid-drag. Build must honour held signs the same way.
        var projection = Projection(new Vector3(-5f, 0f, -5f));
        float[] held = [1f, 1f, 1f];
        var layout = WorldGizmo.Build(
            projection, TransformTool.Universal,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, 1f,
            held, held);

        Assert.Equal([1f, 1f, 1f], layout.TranslateSign);
        Assert.Equal([1f, 1f, 1f], layout.ScaleSign);
    }

    // ---- Rings: never flip, never even see the signs -----------------------

    [Fact]
    public void Rotation_rings_are_independent_of_axis_signs()
    {
        // PBI-006 screen-stable ring semantics stay whole: ring geometry is
        // identical whether the linear signs flipped or were latched straight,
        // exactly as ImGuizmo applies no mulAxis to rotation.
        var projection = Projection(new Vector3(-5f, 2f, -5f));
        var natural = WorldGizmo.Build(
            projection, TransformTool.Universal,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, 1f);
        var latched = WorldGizmo.Build(
            projection, TransformTool.Universal,
            Quaternion.Identity, Quaternion.Identity, Quaternion.Identity, 1f,
            [1f, 1f, 1f], [1f, 1f, 1f]);

        Assert.NotNull(natural.Rings);
        Assert.NotNull(latched.Rings);
        for (int a = 0; a < 3; a++)
            Assert.Equal(natural.Rings!.Points[a], latched.Rings!.Points[a]);
    }
}
