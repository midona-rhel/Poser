extern alias ProductionPoser;

using System.Numerics;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using ProductionPoser::Poser.UI;

namespace Poser.ContractTests;

/// <summary>
/// The arithmetic behind the overlay and gizmo parity sweep, pinned where it is
/// pure: the hover-list wheel order, the tri-state a group of bones resolves
/// to, the snap grid, the connector shortening, and the centroid a group of
/// targets pivots on. Everything here is a rule transcribed from Brio or
/// Ktisis, so a change to any of these numbers is a change to what the
/// reference does.
/// </summary>
public sealed class OverlayGizmoContractTests
{
    // ── the actor-handle name strip ──────────────────────────────────────
    // The overlay names an actor handle on every frame, so the object-index
    // suffix comes off with a scan rather than a regex. The regex is the
    // SPECIFICATION, so it is what the scan is measured against here — a
    // divergence would be the overlay disagreeing with the shell about what
    // an actor is called.

    private const string SuffixPattern = @"\s*\(\d+\)$";

    [Theory]
    [InlineData("Y'shtola Rhul (201)")]
    [InlineData("Y'shtola Rhul(201)")]
    [InlineData("Y'shtola Rhul   (439)")]
    [InlineData("Y'shtola Rhul")]
    [InlineData("")]
    [InlineData("(201)")]
    [InlineData(" (201)")]
    [InlineData("Name (201) trailing")]
    [InlineData("Name ()")]
    [InlineData("Name (abc)")]
    [InlineData("Name (12a)")]
    [InlineData("Name (201")]
    [InlineData("Name 201)")]
    [InlineData("Name ((201))")]
    [InlineData("Name (201)(202)")]
    [InlineData("(0)")]
    [InlineData("Name\t(201)")]
    public void The_index_suffix_scan_answers_exactly_as_the_regex_does(
        string name)
    {
        Assert.Equal(
            System.Text.RegularExpressions.Regex.Replace(
                name, SuffixPattern, ""),
            SkeletonOverlayWindow.StripObjectIndex(name));
    }

    [Fact]
    public void A_name_with_no_suffix_is_handed_back_uncopied()
    {
        // The whole point of the scan: the common frame allocates nothing.
        var name = string.Concat("Y'shtola", " Rhul");
        Assert.Same(name, SkeletonOverlayWindow.StripObjectIndex(name));
    }

    // ── hover-list wheel (Ktisis SelectableGui.DrawSelectList) ───────────

    [Fact]
    public void Wheel_forward_walks_toward_the_front_of_the_hover_list()
    {
        // Ktisis SUBTRACTS the notch, so a positive wheel lowers the index.
        Assert.Equal(1, SkeletonOverlayWindow.CycleHoverIndex(2, 4, 1f));
        Assert.Equal(3, SkeletonOverlayWindow.CycleHoverIndex(2, 4, -1f));
    }

    [Fact]
    public void Wheel_wraps_at_both_ends()
    {
        Assert.Equal(3, SkeletonOverlayWindow.CycleHoverIndex(0, 4, 1f));
        Assert.Equal(0, SkeletonOverlayWindow.CycleHoverIndex(3, 4, -1f));
    }

    [Fact]
    public void A_cluster_of_two_alternates_in_both_directions()
    {
        int index = 0;
        index = SkeletonOverlayWindow.CycleHoverIndex(index, 2, -1f);
        Assert.Equal(1, index);
        index = SkeletonOverlayWindow.CycleHoverIndex(index, 2, -1f);
        Assert.Equal(0, index);
        index = SkeletonOverlayWindow.CycleHoverIndex(index, 2, 1f);
        Assert.Equal(1, index);
    }

    [Fact]
    public void An_overshooting_burst_lands_on_the_far_end_not_modulo()
    {
        // Ktisis' wrap is one test per side, so overshoot clamps to the
        // opposite end rather than walking round. Reproduced deliberately.
        Assert.Equal(3, SkeletonOverlayWindow.CycleHoverIndex(1, 4, 5f));
        Assert.Equal(0, SkeletonOverlayWindow.CycleHoverIndex(1, 4, -5f));
    }

    [Fact]
    public void An_empty_hover_list_has_no_index_to_step()
    {
        Assert.Equal(0, SkeletonOverlayWindow.CycleHoverIndex(3, 0, 1f));
    }

    // ── tri-state group visibility (Brio ImBrio.TristateCheckbox) ────────

    [Fact]
    public void A_group_resolves_to_none_partial_or_all()
    {
        var presentation = new SkeletonOverlayPresentation();
        var bones = new[] { Bone("a", 1), Bone("b", 2), Bone("c", 3) };

        Assert.Equal(OverlayVisibility.None, presentation.Resolve(bones));

        presentation.SetVisible(new[] { bones[0] }, true);
        Assert.Equal(OverlayVisibility.Partial, presentation.Resolve(bones));
        Assert.False(presentation.AreVisible(bones));

        presentation.SetVisible(bones, true);
        Assert.Equal(OverlayVisibility.All, presentation.Resolve(bones));
        Assert.True(presentation.AreVisible(bones));
    }

    [Fact]
    public void An_empty_group_is_none_and_never_all()
    {
        var presentation = new SkeletonOverlayPresentation();
        Assert.Equal(
            OverlayVisibility.None,
            presentation.Resolve(System.Array.Empty<BoneId>()));
        Assert.False(presentation.AreVisible(System.Array.Empty<BoneId>()));
    }

    private static BoneId Bone(string name, int index) => new(
        new SkeletonId(TestIds.Actor(), PoseSlot.Character, 0),
        PartialId: 0,
        BoneIndex: index,
        CanonicalName: name);

    // ── hold-snap arithmetic (Ktisis Gizmo.Manipulate) ───────────────────

    [Fact]
    public void The_precision_modifier_divides_the_step_by_ten()
    {
        Assert.Equal(5f, GizmoSnap.Increment(5f, precise: false));
        Assert.Equal(0.5f, GizmoSnap.Increment(5f, precise: true), 5);
        Assert.Equal(0.01f, GizmoSnap.Increment(0.1f, precise: true), 5);
    }

    [Fact]
    public void A_non_positive_step_is_no_grid_at_all()
    {
        Assert.Equal(0f, GizmoSnap.Increment(0f, precise: false));
        Assert.Equal(0f, GizmoSnap.Increment(-1f, precise: false));
        Assert.Equal(0f, GizmoSnap.Increment(float.NaN, precise: false));
        // …and every Snap overload therefore passes the value through.
        Assert.Equal(1.234f, GizmoSnap.Snap(1.234f, 0f));
        Assert.Equal(
            new Vector3(1.234f, -5f, 0.001f),
            GizmoSnap.Snap(new Vector3(1.234f, -5f, 0.001f), 0f));
    }

    [Fact]
    public void Snapping_rounds_to_the_nearest_multiple_with_halves_away_from_zero()
    {
        Assert.Equal(0.2f, GizmoSnap.Snap(0.17f, 0.1f), 5);
        Assert.Equal(0.1f, GizmoSnap.Snap(0.13f, 0.1f), 5);
        Assert.Equal(-0.2f, GizmoSnap.Snap(-0.17f, 0.1f), 5);
        Assert.Equal(0.2f, GizmoSnap.Snap(0.15f, 0.1f), 5);
        Assert.Equal(-0.2f, GizmoSnap.Snap(-0.15f, 0.1f), 5);
    }

    [Fact]
    public void A_translate_total_snaps_one_component_at_a_time()
    {
        var snapped = GizmoSnap.Snap(new Vector3(0.17f, -0.44f, 1.02f), 0.1f);
        Assert.Equal(0.2f, snapped.X, 5);
        Assert.Equal(-0.4f, snapped.Y, 5);
        Assert.Equal(1.0f, snapped.Z, 5);
    }

    [Fact]
    public void Rotation_snaps_in_degrees_while_the_gesture_counts_radians()
    {
        // Ktisis' rotate increment is 5°; a hair under 7° lands on 5°, a hair
        // over 7.5° lands on 10°.
        float sevenDegrees = 7f * MathF.PI / 180f;
        Assert.Equal(
            5f * MathF.PI / 180f,
            GizmoSnap.SnapRadiansToDegrees(sevenDegrees, 5f),
            5);
        float eightDegrees = 8f * MathF.PI / 180f;
        Assert.Equal(
            10f * MathF.PI / 180f,
            GizmoSnap.SnapRadiansToDegrees(eightDegrees, 5f),
            5);
        // The precision step keeps the same angle where it is.
        Assert.Equal(
            sevenDegrees,
            GizmoSnap.SnapRadiansToDegrees(sevenDegrees, 0.5f),
            4);
    }

    // ── connector shortening (Brio SkeletonLineToCircle) ─────────────────

    [Fact]
    public void A_connector_pulls_back_by_the_inset_at_both_ends()
    {
        var (from, to) = SkeletonOverlayWindow.ShrinkToCircles(
            new Vector2(0f, 0f), new Vector2(10f, 0f), 2f);
        Assert.Equal(2f, from.X, 4);
        Assert.Equal(8f, to.X, 4);
        Assert.Equal(0f, from.Y, 4);
        Assert.Equal(0f, to.Y, 4);
    }

    [Fact]
    public void A_degenerate_or_insetless_connector_is_left_alone()
    {
        var point = new Vector2(4f, 4f);
        var (from, to) = SkeletonOverlayWindow.ShrinkToCircles(point, point, 2f);
        Assert.Equal(point, from);
        Assert.Equal(point, to);

        var (a, b) = SkeletonOverlayWindow.ShrinkToCircles(
            Vector2.Zero, new Vector2(10f, 0f), 0f);
        Assert.Equal(Vector2.Zero, a);
        Assert.Equal(new Vector2(10f, 0f), b);
    }

    // ── centroid pivot (Brio's multi-entity group pivot) ─────────────────

    [Fact]
    public void A_group_rotates_about_the_mean_of_its_members()
    {
        var first = TestIds.ActorTarget();
        var second = TransformTargetId.ForActor(
            new ActorId(Guid.Parse("22222222-2222-2222-2222-222222222222"), 0));
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TwoActorScene(first, second));
        app.Runtime.Seed(StateAt(first, 0f));
        app.Runtime.Seed(StateAt(second, 4f));

        var begin = app.Gestures.Begin(new BeginTransformGesture(
            new[] { first, second },
            TransformOperation.Rotate,
            TransformSpace.World,
            PivotMode.Centroid));
        Assert.True(begin.Success);

        // A half turn about the world Y axis. The centroid is x = 2, so the
        // two positions swap across it — which is exactly what pivoting on the
        // primary would NOT do (it would leave the first where it is).
        var update = app.Gestures.Update(
            begin.GestureId!.Value,
            new TransformDelta(
                Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
                Vector3.One));
        Assert.True(update.Success);

        Assert.Equal(4f, app.Runtime.State(first).Transform.Position.X, 3);
        Assert.Equal(0f, app.Runtime.State(second).Transform.Position.X, 3);
    }

    [Fact]
    public void A_single_target_centroid_is_that_target_and_nothing_moves()
    {
        var only = TestIds.ActorTarget();
        using var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorScene(TestIds.Actor()));
        app.Runtime.Seed(StateAt(only, 7f));

        var begin = app.Gestures.Begin(new BeginTransformGesture(
            new[] { only },
            TransformOperation.Rotate,
            TransformSpace.World,
            PivotMode.Centroid));
        Assert.True(begin.Success);
        Assert.True(app.Gestures.Update(
            begin.GestureId!.Value,
            new TransformDelta(
                Vector3.Zero,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
                Vector3.One)).Success);

        Assert.Equal(7f, app.Runtime.State(only).Transform.Position.X, 3);
    }

    private static TransformTargetState StateAt(
        TransformTargetId target, float x) => TestStates.At(target, x);

    private static Domain.Scene.SceneSnapshot TwoActorScene(
        TransformTargetId first, TransformTargetId second) =>
        new(
            Revision: 1,
            Actors: new[]
            {
                new Domain.Scene.ActorDescriptor(
                    first.Actor!.Value,
                    "First",
                    System.Array.Empty<Domain.Scene.SkeletonDescriptor>()),
                new Domain.Scene.ActorDescriptor(
                    second.Actor!.Value,
                    "Second",
                    System.Array.Empty<Domain.Scene.SkeletonDescriptor>()),
            },
            Lights: System.Array.Empty<Domain.Scene.LightDescriptor>(),
            Cameras: System.Array.Empty<Domain.Scene.CameraDescriptor>(),
            Props: System.Array.Empty<Domain.Scene.PropDescriptor>());
}
