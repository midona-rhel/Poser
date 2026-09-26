using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Files;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Transforms;

public sealed class TransformParentingTests
{
    private sealed class Runtime : IParentingRuntime, ITransformRuntimePort
    {
        public Dictionary<SelectionId, PoseTransform> Values = new();
        public Dictionary<(ActorId, PoseSlot, string, int), SelectionId> Bones = new();
        public bool CanParent(SelectionId child) => Values.ContainsKey(child) && child.Bone == null;
        public PoseTransform? Read(SelectionId id) => Values.TryGetValue(id, out var value) ? value : null;
        public bool Write(SelectionId id, PoseTransform world)
        { if (!Values.ContainsKey(id)) return false; Values[id] = world; return true; }
        public SelectionId? ResolveBone(ActorId actor, PoseSlot slot, string name, int partial) =>
            Bones.TryGetValue((actor, slot, name, partial), out var bone) ? bone : null;
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
    public void Offset_history_is_relative_to_the_current_parent()
    {
        var (r, h, p) = Setup(); var parent = Actor(); var child = Actor();
        r.Values[parent] = At(1); r.Values[child] = At(3);
        p.Attach(child, parent);
        var step = Assert.IsType<JournalStep>(h.PeekUndo());
        Assert.True(step.Undo()); Assert.Null(p.Read(child));
        r.Values[parent] = At(10); Assert.True(step.Redo());
        Assert.Equal(At(12), r.Values[child]);
        var saved = p.Read(child)!;
        Assert.True(p.EditWorld(child, At(15)));
        r.Values[parent] = At(20);
        Assert.True(p.RestoreOffset(child, saved));
        Assert.Equal(At(22), r.Values[child]);
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

    [Fact]
    public void Bone_rebuild_resolves_only_within_the_same_actor_and_slot()
    {
        var (r, _, p) = Setup(); var actor = ActorId.New(); var child = Actor();
        var bone = new BoneId(new(actor, PoseSlot.Character, 1), 0, 4, "hand");
        var first = SelectionId.ForBone(bone); var second = SelectionId.ForBone(bone with { Skeleton = bone.Skeleton.NextGeneration() });
        r.Values[first] = At(1); r.Values[child] = At(3); p.Attach(child, first);
        r.Values.Remove(first); r.Values[second] = At(10);
        r.Bones[(actor, PoseSlot.Character, "hand", 0)] = second;
        p.Evaluate(); Assert.Equal(At(12), r.Values[child]);
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
        Assert.True(gestures.Begin(new([TransformTargetId.ForLight(b), TransformTargetId.ForLight(a)],
            TransformOperation.Translate, TransformSpace.World, PivotMode.PerTarget)).Success);
        var id = gestures.ActiveGesture!.Value;
        Assert.True(gestures.Update(id, new(new(5, 0, 0), Quaternion.Identity, Vector3.One)).Success);
        Assert.True(gestures.Commit(id).Success);
        parents.Evaluate();
        Assert.Equal(At(5), r.Values[parent]); Assert.Equal(At(7), r.Values[child]);
        Assert.Equal(At(2), parents.Read(child)!.Offset);
        Assert.True(gestures.Undo().Success); parents.Evaluate();
        Assert.Equal(At(0), r.Values[parent]); Assert.Equal(At(2), r.Values[child]);
        Assert.True(gestures.Redo().Success); parents.Evaluate();
        Assert.Equal(At(5), r.Values[parent]); Assert.Equal(At(7), r.Values[child]);
        var commands = new TransformCommandService(scene, port, history, gestures);
        Assert.True(commands.SetAbsolute(TransformTargetId.ForLight(b), At(9), "Offset").Success);
        r.Values[parent] = At(20); parents.Evaluate();
        Assert.True(gestures.Undo().Success); Assert.Equal(At(22), r.Values[child]);
        Assert.True(gestures.Redo().Success); Assert.Equal(At(24), r.Values[child]);
    }

    [Fact]
    public void Saved_parent_relationships_validate_cycles_and_freeze_excluded_targets()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        var scene = new SceneFile { Actors = [new() { Key = a }, new() { Key = b }], Parents =
            [new() { Child = new() { Kind = "actor", Key = a }, Target = new() { Kind = "actor", Key = b }, Offset = At(2) }] };
        Assert.Null(SceneParenting.Validate(scene));
        scene.Parents.Add(new() { Child = new() { Kind = "actor", Key = b }, Target = new() { Kind = "actor", Key = a } });
        Assert.NotNull(SceneParenting.Validate(scene));
        scene.Actors.RemoveAt(1); var notes = new List<string>();
        SceneParenting.Prune(scene, notes); Assert.Empty(scene.Parents); Assert.Single(notes);
    }
}
