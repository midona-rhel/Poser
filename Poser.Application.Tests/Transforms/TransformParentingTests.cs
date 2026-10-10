using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Transforms;

public sealed class TransformParentingTests
{
    private sealed class Runtime : IParentingRuntime, ITransformRuntimePort
    {
        public Dictionary<SelectionId, PoseTransform> Values = new();
        public bool CanParent(SelectionId child) => Values.ContainsKey(child) && child.Bone == null;
        public PoseTransform? Read(SelectionId id) => Values.TryGetValue(id, out var value) ? value : null;
        public bool Write(SelectionId id, PoseTransform world)
        { if (!Values.ContainsKey(id)) return false; Values[id] = world; return true; }
        public SelectionId? ResolveBone(ActorId actor, PoseSlot slot, string name, int partial) => null;
        public TransformPortResult Capture(TransformTargetId target) => Read(target.ToSelectionId()) is { } value
            ? TransformPortResult.Ok(new(target, value, new(), false))
            : TransformPortResult.Fail(TransformPortStatus.StaleTarget, "Missing");
        public TransformPortResult ApplyAbsolute(TransformTargetState baseline, PoseTransform desired, bool rawBaseline = false) =>
            Write(baseline.Target.ToSelectionId(), desired) ? TransformPortResult.Ok()
                : TransformPortResult.Fail(TransformPortStatus.StaleTarget, "Missing");
        public TransformPortResult Restore(TransformTargetState state) => ApplyAbsolute(state, state.Transform);
    }

    private static SelectionId Actor() => SelectionId.ForActor(ActorId.New());
    private static PoseTransform At(float x) => PoseTransform.Identity with { Position = new(x, 0, 0) };
    private static (Runtime Runtime, TransformHistory History, TransformParenting Parents) Setup()
    {
        var runtime = new Runtime(); var history = new TransformHistory();
        return (runtime, history, new(runtime, history, new(history)));
    }

    [Fact]
    public void Child_first_multi_selection_moves_once_and_offset_history_survives_parent_movement()
    {
        var (r, history, parents) = Setup();
        var a = LightId.New(); var b = LightId.New();
        var parent = SelectionId.ForLight(a); var child = SelectionId.ForLight(b);
        r.Values[parent] = At(0); r.Values[child] = At(2);
        parents.Attach(child, parent);
        var scene = new SceneSession(new SelectionSession());
        Assert.True(scene.TryRefresh(new SceneSnapshot(1, [],
            [new(a, "Parent", LightKind.Point), new(b, "Child", LightKind.Point)], [], [])).Accepted);
        var port = new ParentedTransformPort(r, parents);
        using var gestures = new TransformGestureService(scene, port, history);
        var journal = new UndoJournal(history, gestures, _ => true, new Fixtures.NoticeLog());
        Assert.True(gestures.Begin(new([TransformTargetId.ForLight(b), TransformTargetId.ForLight(a)],
            TransformOperation.Translate, TransformSpace.World, PivotMode.PerTarget)).Success);
        var id = gestures.ActiveGesture!.Value;
        Assert.True(gestures.Update(id, new(new(5, 0, 0), Quaternion.Identity, Vector3.One)).Success);
        Assert.True(gestures.Commit(id).Success);
        parents.Evaluate();
        Assert.Equal(At(5), r.Values[parent]); Assert.Equal(At(7), r.Values[child]);
        Assert.Equal(At(2), parents.Read(child)!.Offset);
        Assert.True(journal.Undo().Success); parents.Evaluate();
        Assert.Equal(At(0), r.Values[parent]); Assert.Equal(At(2), r.Values[child]);
        Assert.True(journal.Redo().Success); parents.Evaluate();
        Assert.Equal(At(5), r.Values[parent]); Assert.Equal(At(7), r.Values[child]);
        var commands = new TransformCommandService(scene, port, history, gestures);
        Assert.True(commands.SetAbsolute(TransformTargetId.ForLight(b), At(9), "Offset").Success);
        r.Values[parent] = At(20); parents.Evaluate();
        Assert.True(journal.Undo().Success); Assert.Equal(At(22), r.Values[child]);
        Assert.True(journal.Redo().Success); Assert.Equal(At(24), r.Values[child]);
    }

    [Fact]
    public void Attach_preserves_world_then_follows_rotation_without_inheriting_scale()
    {
        var (r, _, p) = Setup(); var parent = Actor(); var child = Actor();
        r.Values[parent] = At(4); r.Values[child] = At(6) with { Scale = new(2, 3, 4) };
        Assert.True(p.Attach(child, parent).Success);
        Assert.Equal(new Vector3(6, 0, 0), r.Values[child].Position);
        r.Values[parent] = At(10) with { Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Scale = new(9) };
        p.Evaluate();
        Assert.True(Vector3.Distance(new(10, 2, 0), r.Values[child].Position) < .0001f);
        Assert.Equal(new Vector3(2, 3, 4), r.Values[child].Scale);
        Assert.True(p.Attach(child, null).Success);
        var detached = r.Values[child]; r.Values[parent] = At(100); p.Evaluate();
        Assert.Equal(detached, r.Values[child]);
    }

    [Fact]
    public void Nested_parents_update_in_order_and_reject_cycles_including_own_bones()
    {
        var (r, _, p) = Setup(); var a = Actor(); var b = Actor(); var c = Actor();
        r.Values[a] = At(0); r.Values[b] = At(1); r.Values[c] = At(2);
        Assert.True(p.Attach(c, b).Success); Assert.True(p.Attach(b, a).Success);
        Assert.False(p.Attach(a, c).Success);
        var bone = SelectionId.ForBone(new(new(a.Actor!.Value, PoseSlot.Character, 0), 0, 0, "hand"));
        r.Values[bone] = At(0);
        Assert.False(p.Attach(a, bone).Success);
        r.Values[a] = At(10); p.Evaluate();
        Assert.Equal(At(11), r.Values[b]); Assert.Equal(At(12), r.Values[c]);
        Assert.True(p.Depth(b) < p.Depth(c));
    }

    [Fact]
    public void Missing_parent_holds_child_and_only_history_can_rebind_a_replacement()
    {
        var (r, h, p) = Setup(); var parent = Actor(); var child = Actor();
        r.Values[parent] = At(0); r.Values[child] = At(2); p.Attach(child, parent);
        r.Values.Remove(parent); p.Evaluate(); Assert.Equal(At(2), r.Values[child]);
        Assert.False(p.EditWorld(child, At(9)));
        var restored = SelectionId.ForActor(parent.Actor!.Value.NextGeneration()); r.Values[restored] = At(10);
        p.Evaluate(); Assert.Equal(At(2), r.Values[child]);
        h.RetainLifecycleEntity(parent, () => restored); p.Evaluate();
        Assert.Equal(At(12), r.Values[child]);
    }
}
