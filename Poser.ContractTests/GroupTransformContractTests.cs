using System.Numerics;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.ContractTests;

/// <summary>
/// What a gesture over MORE THAN ONE entity does, per pivot mode. The pivot is
/// frozen at Begin from the captured baselines and is published while the
/// gesture runs, because a surface drawing a handle on a target the pivot makes
/// orbit has to draw it where that target has got to.
///
/// <para>Brio decides the same way and is the reference for the centroid:
/// <c>Capabilities/Core/EntitManagerCapability.cs DrawMultiTransform</c> takes
/// the mean of the selected transforms and rotates every one of them about it,
/// and <c>UI/Windows/Specialized/PosingOverlayWindow.cs</c> applies the very
/// same centroid from its gizmo for a multi-entity, non-bone selection. Poser
/// differs on ONE point, deliberately: Brio re-derives the centroid every frame
/// from the live transforms, and Poser freezes it, so no applied result becomes
/// the next frame's input.</para>
/// </summary>
public sealed class GroupTransformContractTests
{
    /// <summary>Half a turn about Y: the sharpest possible statement of where
    /// the pivot was, since every point lands diametrically opposite it.</summary>
    private static TransformDelta HalfTurn =>
        TransformDelta.Identity with
        {
            Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI),
        };

    [Fact]
    public void A_centroid_gesture_turns_every_target_about_the_middle_of_them_all()
    {
        var first = TestIds.ActorTarget();
        var second = TestIds.SecondActorTarget();
        using var app = GroupHarness((first, 0f), (second, 4f));

        var begun = app.Gestures.Begin(
            Rotate(PivotMode.Centroid, first, second));

        Assert.True(begun.Success);
        Assert.Equal(new Vector3(2f, 0f, 0f), app.Gestures.ActivePivot);
        Assert.True(app.Gestures.Update(begun.GestureId!.Value, HalfTurn).Success);
        // Both swapped ends around the midpoint at x=2.
        AssertPosition(app, first, 4f);
        AssertPosition(app, second, 0f);
    }

    [Fact]
    public void A_primary_gesture_swings_the_others_about_the_first_and_leaves_it()
    {
        var first = TestIds.ActorTarget();
        var second = TestIds.SecondActorTarget();
        using var app = GroupHarness((first, 0f), (second, 4f));

        var begun = app.Gestures.Begin(
            Rotate(PivotMode.Primary, first, second));

        Assert.True(begun.Success);
        Assert.Equal(new Vector3(0f, 0f, 0f), app.Gestures.ActivePivot);
        Assert.True(app.Gestures.Update(begun.GestureId!.Value, HalfTurn).Success);
        AssertPosition(app, first, 0f);
        AssertPosition(app, second, -4f);
    }

    [Fact]
    public void A_per_target_gesture_turns_everything_on_the_spot()
    {
        var first = TestIds.ActorTarget();
        var second = TestIds.SecondActorTarget();
        using var app = GroupHarness((first, 0f), (second, 4f));

        var begun = app.Gestures.Begin(
            Rotate(PivotMode.PerTarget, first, second));

        Assert.True(begun.Success);
        Assert.True(app.Gestures.Update(begun.GestureId!.Value, HalfTurn).Success);
        AssertPosition(app, first, 0f);
        AssertPosition(app, second, 4f);
    }

    /// <summary>The property that makes the centroid safe as the STANDING rule
    /// for an entity selection rather than a second set of drags: with one
    /// target it is that target's own position, so a single entity turns
    /// exactly as it always has.</summary>
    [Fact]
    public void One_target_under_the_centroid_is_the_per_target_pivot()
    {
        var only = TestIds.ActorTarget();
        using var app = GroupHarness((only, 3f));

        var begun = app.Gestures.Begin(Rotate(PivotMode.Centroid, only));

        Assert.True(begun.Success);
        Assert.Equal(new Vector3(3f, 0f, 0f), app.Gestures.ActivePivot);
        Assert.True(app.Gestures.Update(begun.GestureId!.Value, HalfTurn).Success);
        AssertPosition(app, only, 3f);
    }

    /// <summary>A pivot only governs what ROTATES and SCALES: a group
    /// translation moves every target by the one offset, so the selection keeps
    /// its shape.</summary>
    [Fact]
    public void A_centroid_translation_moves_every_target_by_the_same_offset()
    {
        var first = TestIds.ActorTarget();
        var second = TestIds.SecondActorTarget();
        using var app = GroupHarness((first, 0f), (second, 4f));

        var begun = app.Gestures.Begin(new BeginTransformGesture(
            new[] { first, second },
            TransformOperation.Translate,
            TransformSpace.World,
            PivotMode.Centroid,
            Description: "group translate"));

        Assert.True(begun.Success);
        Assert.True(app.Gestures.Update(
            begun.GestureId!.Value,
            TransformDelta.Identity with { Translation = new Vector3(5f, 0f, 0f) })
            .Success);
        AssertPosition(app, first, 5f);
        AssertPosition(app, second, 9f);
    }

    /// <summary>The pivot is published only while a gesture owns it: there is
    /// no last-gesture pivot for a surface to draw against.</summary>
    [Fact]
    public void No_gesture_publishes_no_pivot()
    {
        var first = TestIds.ActorTarget();
        var second = TestIds.SecondActorTarget();
        using var app = GroupHarness((first, 0f), (second, 4f));
        Assert.Null(app.Gestures.ActivePivot);

        var begun = app.Gestures.Begin(
            Rotate(PivotMode.Centroid, first, second));
        Assert.True(begun.Success);
        Assert.True(app.Gestures.Commit(begun.GestureId!.Value).Success);

        Assert.Null(app.Gestures.ActivePivot);
    }

    private static BeginTransformGesture Rotate(
        PivotMode pivot,
        params TransformTargetId[] targets) =>
        new(
            targets,
            TransformOperation.Rotate,
            TransformSpace.World,
            pivot,
            Description: "group rotate");

    /// <summary>A scene of one actor per target, each seeded at its own place
    /// along X.</summary>
    private static TransformApplicationHarness GroupHarness(
        params (TransformTargetId Target, float X)[] seats)
    {
        var app = new TransformApplicationHarness();
        app.Scene.Refresh(TestScenes.ActorsScene(
            seats.Select(seat => seat.Target.Actor!.Value).ToArray()));
        foreach (var seat in seats)
            app.Runtime.Seed(TestStates.At(seat.Target, seat.X));
        return app;
    }

    private static void AssertPosition(
        TransformApplicationHarness app,
        TransformTargetId target,
        float expectedX)
    {
        var position = app.Runtime.State(target).Transform.Position;
        Assert.True(
            Vector3.Distance(position, new Vector3(expectedX, 0f, 0f)) < 1e-4f,
            $"{target} stands at {position}, expected x={expectedX}.");
    }
}
