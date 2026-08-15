using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.ContractTests.Fixtures;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.ContractTests;

/// <summary>
/// A borrowed map object MOVES. Every other class the scene holds is answered
/// for by <see cref="SceneSession"/> — its id indexes, its selection repair and
/// its <see cref="SceneSession.Contains(TransformTargetId)"/> — and the world
/// object was the one class that was not, which made every gesture against one
/// refuse as stale before it ever reached a port. These facts pin the whole
/// chain from "the scene holds it" to "the runtime was asked to write it".
/// </summary>
public sealed class WorldObjectTransformContractTests
{
    private static readonly Guid Lineage =
        Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    private static WorldObjectId Id(uint generation = 0) =>
        new(Lineage, generation);

    [Fact]
    public void A_borrowed_object_the_scene_holds_is_a_live_transform_target()
    {
        var session = new SceneSession(new SelectionSession());

        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(1, Borrowed(Id()))).Outcome);

        Assert.True(session.Contains(TransformTargetId.ForWorldObject(Id())));
    }

    [Fact]
    public void A_borrowed_object_the_scene_does_not_hold_is_never_a_target()
    {
        var session = new SceneSession(new SelectionSession());
        Assert.Equal(SceneRefreshOutcome.Applied, session.TryRefresh(Scene(1)).Outcome);

        // Nothing borrowed at all, and a generation the scene never carried:
        // both are stale, and staleness is what refuses a write.
        Assert.False(session.Contains(TransformTargetId.ForWorldObject(Id())));

        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(2, Borrowed(Id(generation: 1)))).Outcome);
        Assert.False(session.Contains(TransformTargetId.ForWorldObject(Id())));
        Assert.True(
            session.Contains(TransformTargetId.ForWorldObject(Id(generation: 1))));
    }

    [Fact]
    public void Selecting_a_borrowed_object_survives_the_next_scene_refresh()
    {
        var selection = new SelectionSession();
        var session = new SceneSession(selection);
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(1, Borrowed(Id()))).Outcome);

        selection.Select(SelectionId.ForWorldObject(Id()));

        // Adopting a second object republishes the scene; the first selection
        // must not be reconciled away by that republish.
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(
                2,
                Borrowed(Id()),
                Borrowed(new WorldObjectId(Guid.NewGuid(), 0)))).Outcome);
        Assert.Equal(SelectionId.ForWorldObject(Id()), selection.Primary);

        // Releasing it is the one thing that ends the selection.
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            session.TryRefresh(Scene(3)).Outcome);
        Assert.Null(selection.Primary);
    }

    [Fact]
    public void A_gesture_over_a_borrowed_object_reaches_the_runtime_write()
    {
        using var app = new TransformApplicationHarness();
        var target = TransformTargetId.ForWorldObject(Id());
        Assert.Equal(
            SceneRefreshOutcome.Applied,
            app.Scene.TryRefresh(Scene(1, Borrowed(Id()))).Outcome);
        app.Runtime.Seed(TestStates.At(target, 0, hasOverride: true));

        var begun = app.Gestures.Begin(new BeginTransformGesture(
            new[] { target },
            TransformOperation.Translate,
            TransformSpace.World,
            PivotMode.PerTarget,
            Description: "Transform 1 world object"));

        Assert.True(begun.Success, begun.Detail);
        Assert.Equal(new[] { target }, app.Runtime.CaptureCalls);

        Assert.True(app.Gestures.Update(
            begun.GestureId!.Value,
            TransformDelta.Identity with { Translation = new Vector3(3, 0, 0) })
            .Success);

        // The write is the whole point of the fix: the borrowed object's
        // placement left the gesture and arrived at the port.
        Assert.Equal(new[] { target }, app.Runtime.ApplyCalls);
        Assert.Equal(
            new Vector3(3, 0, 0),
            app.Runtime.State(target).Transform.Position);

        Assert.True(app.Gestures.Commit(begun.GestureId.Value).Success);
    }

    [Fact]
    public void A_gesture_over_a_released_object_refuses_rather_than_writing()
    {
        using var app = new TransformApplicationHarness();
        var target = TransformTargetId.ForWorldObject(Id());
        Assert.Equal(SceneRefreshOutcome.Applied, app.Scene.TryRefresh(Scene(1)).Outcome);
        app.Runtime.Seed(TestStates.At(target, 0, hasOverride: true));

        var begun = app.Gestures.Begin(new BeginTransformGesture(
            new[] { target },
            TransformOperation.Translate,
            TransformSpace.World,
            PivotMode.PerTarget,
            Description: "Transform 1 world object"));

        Assert.False(begun.Success);
        Assert.Empty(app.Runtime.ApplyCalls);
    }

    private static WorldObjectDescriptor Borrowed(WorldObjectId id) =>
        new(id, $"world-object-{id.LogicalId:N}", "bg/ffxiv/test.mdl");

    private static SceneSnapshot Scene(
        ulong revision,
        params WorldObjectDescriptor[] worldObjects) =>
        new(
            revision,
            Array.Empty<ActorDescriptor>(),
            Array.Empty<LightDescriptor>(),
            Array.Empty<CameraDescriptor>(),
            Array.Empty<PropDescriptor>(),
            WorldObjects: worldObjects);
}
