using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Transforms;

public sealed class GroupTransformStateTests
{
    [Fact]
    public void Captured_structure_survives_live_edits_and_restores_its_nested_baseline()
    {
        using var f = new Fixture(count: 3);
        var steps = new GroupSteps(f.Groups, f.History, new ValueJournal(f.History), f.State, f.Coordinator);
        var child = steps.Create("Child", f.Selected.Take(2).ToArray())!;
        var parent = f.Groups.Create("Parent", [f.Selected[2]], allowThin: true)!;
        f.Groups.Nest(child.Id, parent.Id);
        f.Groups.RestoreOrder([RootSlot.ForGroup(parent.Id)]);
        ISceneStructure structure = new SceneStructure(f.Groups, f.Coordinator, f.State);
        var baseline = f.State.NamedSnapshot(child.Id)!;
        var snapshot = structure.Capture();

        child.Name = "Changed after capture";
        child.Members.Clear();
        f.Groups.Clear();
        f.State.Clear();
        structure.Import(snapshot.Groups, snapshot.RootOrder);

        var restoredChild = Assert.Single(f.Groups.All, group => group.Name == "Child");
        var restoredParent = Assert.Single(f.Groups.All, group => group.Name == "Parent");
        Assert.Equal(restoredParent.Id, restoredChild.ParentId);
        Assert.Equal(f.Selected.Take(2), restoredChild.Members);
        Assert.Same(baseline, f.State.NamedSnapshot(restoredChild.Id));
        Assert.Equal(RootSlot.ForGroup(restoredParent.Id), Assert.Single(f.Groups.RootOrder));
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public void Rotated_group_mirror_scales_on_frozen_display_axes_and_replays_exactly()
    {
        const GroupScaleMode mode = GroupScaleMode.SizesAndSpacing;
        const float x = -2f;
        using var f = new Fixture(3, noncollinear: true);
        var frame = f.Snapshot.Baseline.Frame;
        var authored = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .8f);
        f.Perform(new(Vector3.Zero, frame.ToWorldDelta(authored), Vector3.One));
        var before = f.Snapshot;
        var native = f.Live.ToDictionary();
        Assert.True(f.Coordinator.TryReadWorldSelection(mode, out var display, out _));
        Assert.True(MathF.Abs(Quaternion.Dot(frame.Rotation * authored, display.Rotation)) > .99999f);
        var axis = Vector3.Transform(Vector3.UnitX, display.Rotation);
        var factors = new Vector3(x, 1, 1);
        var delta = new TransformDelta(Vector3.Zero, Quaternion.Identity, factors);
        var id = f.Begin(mode);
        f.Camera = Quaternion.CreateFromAxisAngle(Vector3.UnitX, 1.2f);
        Assert.True(f.Service.Update(id, delta).Success);
        foreach (var target in f.Targets)
        {
            var offset = native[target].Position - display.Position;
            // Independent projection oracle: only the displayed X component changes.
            var expected = native[target].Position + axis * (Vector3.Dot(offset, axis) * (x - 1));
            Assert.True(Vector3.Distance(expected, f.Live[target].Position) < .00001f);
            Assert.Equal(mode == GroupScaleMode.SpacingOnly ? Vector3.One : factors, f.Live[target].Scale);
        }
        Assert.True(Vector3.Distance(display.Position, GroupTransformBaseline.Centroid(f.Live.Values)) < .00001f);
        var once = f.Live.ToDictionary();
        Assert.True(f.Service.Update(id, delta).Success);
        Assert.Equal(once, f.Live);
        Assert.True(f.Service.Cancel(id).Success);
        Assert.Equal(native, f.Live);
        Assert.True(before.ContentEquals(f.Snapshot));
        f.Perform(delta, mode);
        Assert.Equal(once, f.Live);
        var after = f.Snapshot;
        Assert.Equal(before.Controls.Rotation, after.Controls.Rotation);
        Assert.True(f.Journal.Undo().Success);
        Assert.Equal(native, f.Live);
        Assert.True(before.ContentEquals(f.Snapshot));
        Assert.True(f.Journal.Redo().Success);
        Assert.Equal(once, f.Live);
        Assert.True(after.ContentEquals(f.Snapshot));
        Assert.True(f.Coordinator.TryReadWorldSelection(mode, out var final, out _));
        Assert.Equal(display.Rotation, final.Rotation);
    }

    [Fact]
    public void Rotation_commit_cancel_undo_redo_share_exact_native_and_authored_state()
    {
        using var f = new Fixture();
        int appended = 0;
        f.History.Appended += _ => appended++;
        var before = f.Snapshot;
        var local = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f);
        var world = before.Baseline.Frame.ToWorldDelta(local);
        var gesture = f.Begin();
        // Repeated updates apply against the frozen before-state, and commit appends once.
        Assert.True(f.Service.Update(gesture, new(Vector3.Zero, world, Vector3.One)).Success);
        var once = f.Live.ToDictionary();
        Assert.True(f.Service.Update(gesture, new(Vector3.Zero, world, Vector3.One)).Success);
        Assert.Equal(once, f.Live);
        Assert.True(f.Service.Commit(gesture).Success);
        Assert.Equal(1, appended);
        var after = f.Snapshot;
        Assert.True(MathF.Abs(Quaternion.Dot(local, after.Controls.Rotation)) > .99999f);
        var committed = f.Live.ToDictionary();
        Assert.True(f.Journal.Undo().Success);
        Assert.True(before.ContentEquals(f.Snapshot));
        Assert.True(f.Journal.Redo().Success);
        Assert.True(after.ContentEquals(f.Snapshot));
        Assert.Equal(committed, f.Live);
        var id = f.Begin();
        Assert.True(f.Service.Update(id, new(Vector3.One, Quaternion.Identity, Vector3.One)).Success);
        Assert.True(f.Service.Cancel(id).Success);
        Assert.True(after.ContentEquals(f.Snapshot));
        Assert.Equal(committed, f.Live);
    }

    [Fact]
    public void Failed_apply_undo_and_delayed_recovery_publish_controls_and_history_once()
    {
        using var f = new Fixture();
        var before = f.Snapshot;
        f.FailApply = true; f.FailRestore = true;
        var id = f.Begin();
        Assert.False(f.Service.Update(id, new(Vector3.One, Quaternion.Identity, Vector3.One)).Success);
        Assert.NotNull(f.Service.PendingRecovery);
        Assert.Same(before, f.Snapshot);
        Assert.False(f.History.CanUndo);
        f.FailRestore = false;
        Assert.True(f.Service.RetryRecovery(f.Service.PendingRecovery!).Success);
        Assert.True(before.ContentEquals(f.Snapshot));
        Assert.Null(f.Service.PendingRecovery);
        Assert.Equal(before.Expected, f.Live);

        // A refused undo keeps the entry and commits metadata and history once on retry.
        f.FailApply = false;
        f.Perform(new(Vector3.One, Quaternion.Identity, Vector3.One));
        var after = f.Snapshot;
        f.FailRestore = true;
        Assert.False(f.Journal.Undo().Success);
        Assert.Same(after, f.Snapshot);
        Assert.True(f.History.CanUndo);
        f.FailRestore = false;
        Assert.True(f.Journal.Undo().Success);
        Assert.True(before.ContentEquals(f.Snapshot));
        Assert.False(f.History.CanUndo);
        Assert.True(f.History.CanRedo);
    }

    [Fact]
    public void Rebind_preserves_authored_state_and_rekeys_history()
    {
        using var f = new Fixture();
        f.Perform(new(Vector3.One, Quaternion.Identity, Vector3.One));
        var controls = f.Snapshot.Controls;
        f.Rebind();
        Assert.Equal(controls, f.Snapshot.Controls);
        Assert.True(f.Journal.Undo().Success);
        Assert.Equal(Vector3.Zero, f.Live[f.Targets[0]].Position);
        Assert.True(f.Journal.Redo().Success);
        Assert.Equal(controls, f.Snapshot.Controls);
    }

    [Fact]
    public void Nested_lock_unlock_preserves_authored_child_state_and_save_capture_through_history()
    {
        using var f = new Fixture(3);
        var steps = new GroupSteps(f.Groups, f.History, new ValueJournal(f.History), f.State, f.Coordinator);
        var child = steps.Create("Child", f.Selected.Take(2).ToArray())!;
        var parent = steps.Create("Parent", [f.Selected[2]], allowThin: true)!;
        Assert.True(steps.Nest(child.Id, parent.Id));
        f.Selection.Clear();
        f.Groups.ActiveGroupId = child.Id;
        foreach (var member in child.Members) f.Selection.Add(member);
        var initial = f.State.NamedSnapshot(child.Id)!;
        var targets = child.Members.Select(GroupTransformCoordinator.Target).Select(target => target!.Value).ToArray();
        var begin = f.Service.Begin(new(targets, TransformOperation.Universal, TransformSpace.World,
            PivotMode.Centroid, GroupId: child.Id, IsGroupTransform: true));
        Assert.True(begin.Success, begin.Detail);
        Assert.True(f.Service.Update(begin.GestureId!.Value, new(Vector3.Zero,
            initial.Baseline.Frame.ToWorldDelta(Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f)),
            new(1.5f))).Success);
        Assert.True(f.Service.Commit(begin.GestureId.Value).Success);
        var authored = f.State.NamedSnapshot(child.Id)!;
        f.Camera = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.2f);
        int frameReads = f.FrameReads;
        steps.SetLocked(parent.Id, true);
        Assert.False(f.Coordinator.TryReadSelection(GroupScaleMode.SizesAndSpacing, out _, out _));
        Assert.True(f.Coordinator.TryReadSelection(GroupScaleMode.SizesAndSpacing,
            out var lockedDisplay, out _, requireEditable: false));
        Assert.Equal(authored.Controls.Display(GroupScaleMode.SizesAndSpacing), lockedDisplay);
        Assert.Equal(authored.Baseline.Frame, f.Coordinator.SelectionFrame(requireEditable: false));
        Assert.False(f.Coordinator.Admit(targets, GroupScaleMode.SizesAndSpacing, out _, out _));
        // Save reads the same retained named snapshot even while editing is refused.
        Assert.Same(authored, f.State.NamedSnapshot(child.Id));
        Assert.Same(authored, f.State.CaptureNamed()[GroupTransformKey.For(child.Id, targets)]);
        f.Coordinator.BindingsPublished();
        Assert.Same(authored, f.State.NamedSnapshot(child.Id));
        steps.SetLocked(parent.Id, false);
        Assert.Same(authored, f.State.NamedSnapshot(child.Id));
        Assert.Equal(frameReads, f.FrameReads);
        Assert.True(f.Coordinator.TryReadSelection(GroupScaleMode.SizesAndSpacing, out var display, out _));
        Assert.Equal(authored.Controls.Rotation, display.Rotation);
        Assert.Equal(authored.Controls.OwnScale, display.Scale);
        Assert.True(f.Journal.Undo().Success); // unlock
        Assert.True(f.Groups.Find(parent.Id)!.Locked);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Journal.Undo().Success); // lock
        Assert.False(f.Groups.Find(parent.Id)!.Locked);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Journal.Undo().Success); // transform
        Assert.True(initial.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Journal.Redo().Success);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Journal.Redo().Success);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Journal.Redo().Success);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
    }

    private static PoseTransform Pose(Vector3 position) =>
        PoseTransform.CreateChecked(position, Quaternion.Identity, Vector3.One);

    private sealed class Fixture : IGroupTransformSource, ITransformRuntimePort, IDisposable
    {
        public readonly SelectionSession Selection = new();
        public readonly SceneSession Scene;
        public readonly SceneGroups Groups = new();
        public readonly GroupTransformState State = new();
        public readonly EditHistory History = new();
        public readonly GroupTransformCoordinator Coordinator;
        public readonly TransformGestureService Service;
        public readonly UndoJournal Journal;
        public Dictionary<TransformTargetId, PoseTransform> Live = new();
        public TransformTargetId[] Targets;
        public SelectionId[] Selected => Targets.Select(target => SelectionId.ForActor(target.Actor!.Value)).ToArray();
        public Quaternion Camera = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
        public int FrameReads, Writes;
        public bool FailApply, FailRestore;
        public TransformTargetId? Refused;
        public GroupTransformSnapshot Snapshot => State.Snapshot(null, Targets)!;
        private ulong _revision;
        public Fixture(int count = 2, bool noncollinear = false)
        {
            Scene = new(Selection);
            Targets = Enumerable.Range(0, count).Select(_ => TransformTargetId.ForActor(ActorId.New())).ToArray();
            for (int i = 0; i < count; i++) Live[Targets[i]] = Pose(new(i * 2, 0, 0));
            if (noncollinear) Live[Targets[2]] = Pose(new(1, 3, 2));
            Publish();
            Coordinator = new(Scene, Groups, State, this);
            Service = new(Scene, this, History, groupTransforms: State, groupSource: this,
                groupCoordinator: Coordinator);
            Journal = new(History, Service, _ => true, new Fixtures.NoticeLog());
            foreach (var member in Selected) Selection.Add(member);
        }
        public void Publish() => Assert.True(Scene.TryRefresh(new SceneSnapshot(++_revision,
            Targets.Select(target => new ActorDescriptor(target.Actor!.Value, "Actor", [])).ToArray(), [], [], [])).Accepted);
        public void Rebind()
        {
            var mapped = Targets.Select(target => TransformTargetId.ForActor(
                target.Actor!.Value with { Generation = target.Actor.Value.Generation + 1 })).ToArray();
            Live = Targets.Select((old, i) => (Target: mapped[i], Pose: Live[old]))
                .ToDictionary(pair => pair.Target, pair => pair.Pose);
            Targets = mapped;
            Publish();
            Coordinator.BindingsPublished();
            History.Reconcile(Scene.Contains, CurrentTarget);
            foreach (var member in Selected) Selection.Add(member);
        }
        public TransformGestureId Begin(GroupScaleMode mode = GroupScaleMode.SizesAndSpacing)
        {
            var result = Service.Begin(new(Targets, TransformOperation.Universal, TransformSpace.World,
                PivotMode.Centroid, GroupScale: mode, IsGroupTransform: true));
            Assert.True(result.Success, result.Detail);
            return result.GestureId!.Value;
        }
        public void Perform(TransformDelta delta, GroupScaleMode mode = GroupScaleMode.SizesAndSpacing)
        {
            var id = Begin(mode);
            var update = Service.Update(id, delta);
            Assert.True(update.Success, update.Detail);
            var commit = Service.Commit(id);
            Assert.True(commit.Success, commit.Detail);
        }
        public PoseTransform? Read(TransformTargetId target) =>
            Live.TryGetValue(target, out var pose) ? pose : null;
        public string? Refusal(TransformTargetId target) => target == Refused ? "Attached light" : null;
        public bool TryFrame(Vector3 origin, out GroupTransformFrame frame)
        {
            FrameReads++;
            frame = new(origin, Camera); return true;
        }
        public TransformTargetId? CurrentTarget(TransformTargetId target) =>
            Live.Keys.Cast<TransformTargetId?>().FirstOrDefault(current =>
                current!.Value.Kind == target.Kind
                && GroupTransformIdentity.LogicalId(current.Value) == GroupTransformIdentity.LogicalId(target));
        public TransformPortResult Capture(TransformTargetId target) => Read(target) is { } pose
            ? TransformPortResult.Ok(new(target, pose, new BonePose(), false))
            : TransformPortResult.Fail(TransformPortStatus.StaleTarget, "Missing");
        public TransformPortResult ApplyAbsolute(TransformTargetState baseline, PoseTransform desired, bool rawBaseline = false)
        {
            Writes++; Live[baseline.Target] = desired;
            return FailApply ? TransformPortResult.Fail(TransformPortStatus.Rejected, "Injected apply") : TransformPortResult.Ok();
        }
        public TransformPortResult Restore(TransformTargetState state)
        {
            if (FailRestore || !Live.ContainsKey(state.Target))
                return TransformPortResult.Fail(TransformPortStatus.Rejected, "Injected restore");
            Live[state.Target] = state.Transform;
            return TransformPortResult.Ok(state);
        }
        public void Dispose() { Service.Dispose(); Coordinator.Dispose(); }
    }
}
