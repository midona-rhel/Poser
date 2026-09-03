using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using NSubstitute;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Game.Transforms;

namespace Poser.Tests.Files;

public sealed class GroupTransformRoutingTests
{
    [Theory]
    [InlineData(TransformOperation.Translate)]
    [InlineData(TransformOperation.Rotate)]
    [InlineData(TransformOperation.Scale)]
    public void Legacy_facade_entry_points_always_attach_group_metadata(TransformOperation operation)
    {
        var selection = new SelectionSession();
        var scene = new SceneSession(selection);
        var actor = ActorId.New();
        var world = WorldObjectId.New();
        var targets = new[] { TransformTargetId.ForActor(actor), TransformTargetId.ForWorldObject(world) };
        scene.Refresh(new SceneSnapshot(1, [new(actor, "Actor", [])], [], [], [],
            WorldObjects: [new(world, "World", "path")]));
        var source = Substitute.For<IGroupTransformSource>();
        source.Refusal(Arg.Any<TransformTargetId>()).Returns((string?)null);
        var runtime = Substitute.For<ITransformRuntimePort>();
        var live = new Dictionary<TransformTargetId, PoseTransform> {
            [targets[0]] = PoseTransform.Identity,
            [targets[1]] = new(new(2, 0, 0), Quaternion.Identity, Vector3.One)
        };
        source.Read(Arg.Any<TransformTargetId>()).Returns(call => live[(TransformTargetId)call[0]]);
        source.CurrentTarget(Arg.Any<TransformTargetId>()).Returns(call => (TransformTargetId)call[0]);
        source.TryFrame(Arg.Any<Vector3>(), out Arg.Any<GroupTransformFrame>()).Returns(call => {
            call[1] = new GroupTransformFrame((Vector3)call[0], Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f));
            return true;
        });
        runtime.Capture(Arg.Any<TransformTargetId>()).Returns(call => {
            var target = (TransformTargetId)call[0];
            return TransformPortResult.Ok(new(target, live[target], new BonePose(), false));
        });
        runtime.ApplyAbsolute(Arg.Any<TransformTargetState>(), Arg.Any<PoseTransform>(), Arg.Any<bool>())
            .Returns(call => {
                live[((TransformTargetState)call[0]).Target] = (PoseTransform)call[1];
                return TransformPortResult.Ok();
            });
        var state = new GroupTransformState();
        var history = new TransformHistory();
        using var coordinator = new GroupTransformCoordinator(scene, new SceneGroups(), state, source);
        using var gestures = new TransformGestureService(scene, runtime, history,
            groupTransforms: state, groupSource: source, groupCoordinator: coordinator);
        var facade = new CleanTransformFacade(scene, gestures,
            new TransformCommandService(scene, runtime, history, gestures), null!, coordinator);
        selection.Add(SelectionId.ForActor(actor));
        selection.Add(SelectionId.ForWorldObject(world));
        Assert.False(facade.Begin([targets[0]], operation, TransformSpace.World).Success);
        // Same old signature used by move commands and the world gizmo: no
        // group flag/id, and a local per-target request.
        var begin = facade.Begin(targets, operation, TransformSpace.Local);
        Assert.True(begin.Success, begin.Detail);
        Assert.Equal(Vector3.UnitX, facade.ActivePivot);
        var delta = new TransformDelta(Vector3.UnitY,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .4f), new(1.5f));
        Assert.True(facade.Update(begin.GestureId!.Value, delta).Success);
        Assert.True(facade.Commit(begin.GestureId.Value).Success);
        var patch = Assert.IsType<TransformPatch>(history.PeekUndo());
        Assert.NotNull(patch.GroupState);
        Assert.True(state.TryRead(null, targets, target => live[target],
            GroupScaleMode.SizesAndSpacing, out _, out _));
    }
}
