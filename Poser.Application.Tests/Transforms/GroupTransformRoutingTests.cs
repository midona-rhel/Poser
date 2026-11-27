using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NSubstitute;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Application.Viewport;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Transforms;

public sealed class GroupTransformRoutingTests
{
    [Fact]
    public void Moving_several_things_moves_them_as_a_group_and_records_the_group_values()
    {
        const TransformOperation operation = TransformOperation.Translate;
        var selection = new CurrentSelection();
        var scene = new SceneSession(selection);
        var actor = ActorId.New();
        var other = ActorId.New();
        var world = WorldObjectId.New();
        var targets = new[] { TransformTargetId.ForActor(actor), TransformTargetId.ForWorldObject(world), TransformTargetId.ForActor(other) };
        scene.Refresh(new SceneSnapshot(1, [new(actor, "Actor", []), new(other, "Other", [])], [], [], [],
            WorldObjects: [new(world, "World", "path")]));
        var runtime = Substitute.For<ITransformRuntimePort>();
        var live = new Dictionary<TransformTargetId, PoseTransform> {
            [targets[0]] = PoseTransform.Identity,
            [targets[1]] = new(new(2, 0, 0), Quaternion.Identity, Vector3.One),
            [targets[2]] = new(new(1, 3, 2), Quaternion.Identity, Vector3.One)
        };
        var viewport = Substitute.For<IViewportReads>();
        viewport.GetModelTransform(Arg.Any<TransformTargetId>()).Returns(call => live[(TransformTargetId)call[0]]);
        var camera = Substitute.For<ICameraProjection>();
        Matrix4x4.Invert(Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f)), out var view);
        camera.GetViewMatrix().Returns(view);
        runtime.Capture(Arg.Any<TransformTargetId>()).Returns(call => {
            var target = (TransformTargetId)call[0];
            return TransformPortResult.Ok(new(target, live[target], new BonePose(), false));
        });
        runtime.ApplyAbsolute(Arg.Any<TransformTargetState>(), Arg.Any<PoseTransform>(), Arg.Any<bool>())
            .Returns(call => {
                live[((TransformTargetState)call[0]).Target] = (PoseTransform)call[1];
                return TransformPortResult.Ok();
            });
        runtime.Restore(Arg.Any<TransformTargetState>()).Returns(call => {
            var saved = (TransformTargetState)call[0];
            live[saved.Target] = saved.Transform;
            return TransformPortResult.Ok(saved);
        });
        var state = new GroupTransformState();
        var history = new EditHistory();
        using var coordinator = new GroupTransformCoordinator(scene, new SceneGroups(), state, viewport, camera, history);
        using var gestures = new TransformGestureService(scene, runtime, history,
            groupCoordinator: coordinator);
        var editing = new TransformEditing(scene, gestures,
            new TransformCommands(runtime, history, gestures), null!, coordinator, new ValueJournal(history), null!, null!, null!, null!);
        selection.Add(SelectionId.ForActor(actor));
        selection.Add(SelectionId.ForWorldObject(world));
        selection.Add(SelectionId.ForActor(other));
        Assert.False(editing.Begin([targets[0]], operation, TransformSpace.World).Success);
        // Asked for a move in each target's own space, several things still
        // move as a group: around their shared centre, in world space.
        var begin = editing.Begin(targets, operation, TransformSpace.Local);
        Assert.True(begin.Success, begin.Detail);
        Assert.Equal(new Vector3(1, 1, 2f / 3), editing.ActivePivot);
        var delta = new TransformDelta(Vector3.UnitY,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f), new(1.5f));
        Assert.True(editing.Update(begin.GestureId!.Value, delta).Success);
        Assert.True(editing.Commit(begin.GestureId.Value).Success);
        var patch = Assert.IsType<TransformPatch>(history.PeekUndo());
        Assert.NotNull(patch.GroupState);
        Assert.True(state.TryRead(null, targets, target => live[target],
            GroupScaleMode.SizesAndSpacing, out _, out _));

        // After the group is turned, scaling along X stretches the group along
        // its own turned X axis, from its centre.
        var rotate = editing.Begin(targets, TransformOperation.Rotate, TransformSpace.World);
        Assert.True(rotate.Success);
        Assert.True(editing.Update(rotate.GestureId!.Value, new(Vector3.Zero,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f), Vector3.One)).Success);
        Assert.True(editing.Commit(rotate.GestureId.Value).Success);
        foreach (var mode in new[] { GroupScaleMode.SpacingOnly, GroupScaleMode.SizesAndSpacing })
        {
            var before = live.ToDictionary();
            Assert.True(coordinator.TryReadWorldSelection(mode, out var display, out _));
            var xAxis = Vector3.Transform(Vector3.UnitX, display.Rotation);
            var scale = editing.Begin(targets, TransformOperation.Scale, TransformSpace.World,
                PivotMode.Centroid, groupScale: mode);
            Assert.True(scale.Success, scale.Detail);
            Assert.True(editing.Update(scale.GestureId!.Value,
                new(Vector3.Zero, Quaternion.Identity, new(2, 1, 1))).Success);
            foreach (var target in targets)
            {
                var expected = before[target].Position + xAxis * Vector3.Dot(
                    before[target].Position - display.Position, xAxis);
                Assert.True(Vector3.Distance(expected, live[target].Position) < .00001f);
            }
            Assert.True(editing.Cancel(scale.GestureId.Value).Success);
            Assert.Equal(before, live);
        }
        // A pinned single entity remains editable while the workspace holds
        // a group; a pinned group remains editable with a different selection.
        var single = editing.Begin([targets[0]], operation, TransformSpace.World,
            selection: [SelectionId.ForActor(actor)]);
        Assert.True(single.Success, single.Detail);
        Assert.True(editing.Cancel(single.GestureId!.Value).Success);
        var pinned = selection.Selected.ToArray();
        selection.Select(SelectionId.ForActor(other));
        var group = editing.Begin(targets, operation, TransformSpace.World, selection: pinned);
        Assert.True(group.Success, group.Detail);
        Assert.True(editing.Cancel(group.GestureId!.Value).Success);
        Assert.Equal(SelectionId.ForActor(other), selection.Primary);
    }
}
