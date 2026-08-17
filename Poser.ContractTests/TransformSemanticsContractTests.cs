using System.Numerics;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.ContractTests;

/// <summary>
/// Pure transform arithmetic and application gesture semantics are
/// contract-tested here.
/// </summary>
public sealed class TransformSemanticsContractTests
{
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
