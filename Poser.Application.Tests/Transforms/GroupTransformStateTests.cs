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
    [Theory]
    [InlineData(GroupScaleMode.SpacingOnly, false)]
    [InlineData(GroupScaleMode.SpacingOnly, true)]
    [InlineData(GroupScaleMode.SizesAndSpacing, false)]
    [InlineData(GroupScaleMode.SizesAndSpacing, true)]
    public void Both_surfaces_read_active_preview_without_publishing_it(GroupScaleMode mode, bool commit)
    {
        using var f = new Fixture(3, noncollinear: true);
        var before = f.Snapshot;
        var id = f.Begin(mode);
        AssertPresentation(f, before.Controls, before.WorldRotation, mode);
        GroupTransformControls expected = default;
        foreach (float angle in new[] { .4f, .8f, .4f })
        {
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
            var delta = new TransformDelta(new(2, 3, 1), rotation, new(2, 1, 1));
            Assert.True(f.Service.Update(id, delta).Success);
            Assert.True(before.Controls.TryAdvance(before.Baseline.Frame, delta, mode,
                GroupTransformBaseline.Centroid(f.Live.Values), out expected));
            var writes = f.Writes;
            AssertPresentation(f, expected, before.Baseline.Frame.ToWorldOrientation(expected.Rotation), mode);
            Assert.Equal(writes, f.Writes);
            Assert.Same(before, f.Snapshot);
            Assert.False(f.History.CanUndo);
            Assert.False(f.Service.Begin(new(f.Targets, TransformOperation.Scale, TransformSpace.World,
                PivotMode.Centroid, IsGroupTransform: true)).Success);
        }
        if (commit)
        {
            Assert.True(f.Coordinator.TryReadSelection(mode, out var preview, out _));
            Assert.True(f.Service.Commit(id).Success);
            AssertPresentation(f, expected, f.Snapshot.WorldRotation, mode);
            Assert.Equal(preview, f.Snapshot.Controls.Display(mode));
            Assert.True(f.History.CanUndo);
        }
        else
        {
            Assert.True(f.Service.Cancel(id).Success);
            AssertPresentation(f, before.Controls, before.WorldRotation, mode);
            Assert.True(before.ContentEquals(f.Snapshot));
            Assert.False(f.History.CanUndo);
        }
    }

    [Fact]
    public void Presentation_refuses_mid_write_and_pending_recovery_even_if_live_matches_committed()
    {
        using var f = new Fixture(3, noncollinear: true);
        var before = f.Snapshot;
        f.AfterApply = () => AssertPresentationRefused(f);
        var id = f.Begin();
        Assert.True(f.Service.Update(id, new(Vector3.One, Quaternion.Identity, Vector3.One)).Success);
        AssertPresentation(f, before.Controls with { Position = before.Controls.Position + Vector3.One },
            before.WorldRotation, GroupScaleMode.SizesAndSpacing);
        f.FailApply = true;
        f.FailRestore = true;
        Assert.False(f.Service.Update(id, new(new(2), Quaternion.Identity, Vector3.One)).Success);
        Assert.NotNull(f.Service.PendingRecovery);
        AssertPresentationRefused(f);
        foreach (var (target, pose) in before.Expected) f.Live[target] = pose;
        AssertPresentationRefused(f); // barrier, not merely an expected-pose mismatch
        Assert.Same(before, f.Snapshot);
        Assert.False(f.History.CanUndo);
        f.FailRestore = false;
        Assert.True(f.Service.RetryRecovery(f.Service.PendingRecovery!).Success);
        AssertPresentation(f, before.Controls, before.WorldRotation, GroupScaleMode.SizesAndSpacing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Active_presentation_rejects_stale_generation_or_external_deformation(bool stale)
    {
        using var f = new Fixture();
        var id = f.Begin();
        Assert.True(f.Service.Update(id, new(Vector3.One, Quaternion.Identity, Vector3.One)).Success);
        if (stale) f.Stale = f.Targets[0];
        else f.Live[f.Targets[0]] = Pose(new(9, 8, 7));
        AssertPresentationRefused(f);
        Assert.False(f.History.CanUndo);
    }

    [Fact]
    public void Replacing_frozen_record_does_not_publish_an_unrelated_active_preview()
    {
        using var f = new Fixture();
        var id = f.Begin();
        Assert.True(f.Service.Update(id, new(Vector3.One, Quaternion.Identity, Vector3.One)).Success);
        Assert.True(f.State.Initialize(null, f.Live, GroupTransformFrame.World(Vector3.Zero), out _));
        AssertPresentationRefused(f);
    }

    [Fact]
    public void Unrelated_transaction_blocks_group_presentation_until_it_ends()
    {
        using var f = new Fixture();
        var before = f.Snapshot;
        var begin = f.Service.Begin(new([f.Targets[0]], TransformOperation.Translate,
            TransformSpace.World, PivotMode.PerTarget));
        Assert.True(begin.Success);
        AssertPresentationRefused(f);
        Assert.True(f.Service.Cancel(begin.GestureId!.Value).Success);
        AssertPresentation(f, before.Controls, before.WorldRotation, GroupScaleMode.SizesAndSpacing);
    }

    [Fact]
    public void Scene_revision_change_refuses_preview_before_any_further_transition()
    {
        using var f = new Fixture();
        var id = f.Begin();
        Assert.True(f.Service.Update(id, new(Vector3.One, Quaternion.Identity, Vector3.One)).Success);
        f.Publish();
        AssertPresentationRefused(f);
        Assert.False(f.History.CanUndo);
    }

    private static void AssertPresentation(Fixture f, GroupTransformControls expected, Quaternion world, GroupScaleMode mode)
    {
        Assert.True(f.Coordinator.TryReadSelection(mode, out var authored, out var error), error);
        Assert.True(f.Coordinator.TryReadWorldSelection(mode, out var overlay, out error), error);
        Assert.True(Vector3.Distance(expected.Position, authored.Position) < .00001f);
        Assert.True(MathF.Abs(Quaternion.Dot(expected.Rotation, authored.Rotation)) > .99999f);
        Assert.Equal(expected.DisplayScale(mode), authored.Scale);
        Assert.Equal(authored.Position, overlay.Position);
        Assert.Equal(authored.Scale, overlay.Scale);
        Assert.True(MathF.Abs(Quaternion.Dot(world, overlay.Rotation)) > .99999f);
    }

    private static void AssertPresentationRefused(Fixture f)
    {
        Assert.False(f.Coordinator.TryReadSelection(GroupScaleMode.SizesAndSpacing, out _, out _));
        Assert.False(f.Coordinator.TryReadWorldSelection(GroupScaleMode.SizesAndSpacing, out _, out _));
    }

    [Fact]
    public void Combined_preview_keeps_scale_axes_at_begin_not_at_previous_preview()
    {
        using var f = new Fixture(3, noncollinear: true);
        var before = f.Snapshot;
        var axis = Vector3.Transform(Vector3.UnitX, before.WorldRotation);
        var pivot = before.Controls.Position;
        var id = f.Begin(GroupScaleMode.SpacingOnly);
        foreach (float angle in new[] { .4f, .8f, .4f })
        {
            var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, angle);
            Assert.True(f.Service.Update(id, new(Vector3.Zero, rotation, new(2, 1, 1))).Success);
            foreach (var target in f.Targets)
            {
                var offset = before.Expected[target].Position - pivot;
                var scaled = offset + axis * Vector3.Dot(offset, axis);
                var expected = pivot + Vector3.Transform(scaled, rotation);
                Assert.True(Vector3.Distance(expected, f.Live[target].Position) < .00001f);
            }
        }
        Assert.True(f.Service.Cancel(id).Success);
        Assert.True(before.ContentEquals(f.Snapshot));
    }

    [Theory]
    [InlineData(GroupScaleMode.SpacingOnly, 2f)]
    [InlineData(GroupScaleMode.SizesAndSpacing, 2f)]
    [InlineData(GroupScaleMode.SpacingOnly, -2f)]
    [InlineData(GroupScaleMode.SizesAndSpacing, -2f)]
    public void Rotated_group_scales_on_frozen_display_axes_and_replays_exactly(GroupScaleMode mode, float x)
    {
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
        Assert.True(f.Service.Undo().Success);
        Assert.Equal(native, f.Live);
        Assert.True(before.ContentEquals(f.Snapshot));
        Assert.True(f.Service.Redo().Success);
        Assert.Equal(once, f.Live);
        Assert.True(after.ContentEquals(f.Snapshot));
        Assert.True(f.Coordinator.TryReadWorldSelection(mode, out var final, out _));
        Assert.Equal(display.Rotation, final.Rotation);
    }

    [Fact]
    public void Selection_captures_frame_once_before_read_and_reads_are_pure()
    {
        using var f = new Fixture();
        Assert.Equal(1, f.FrameReads);
        var frozen = f.Snapshot;
        f.Camera = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .9f);
        for (int i = 0; i < 4; i++)
            Assert.True(f.Coordinator.TryReadSelection(GroupScaleMode.SpacingOnly, out _, out _));
        Assert.Equal(1, f.FrameReads);
        Assert.Same(frozen, f.Snapshot);
        f.Live[f.Targets[0]] = Pose(Vector3.One);
        Assert.False(f.Coordinator.TryReadSelection(GroupScaleMode.SpacingOnly, out _, out _));
        Assert.Same(frozen, f.Snapshot);
    }

    [Fact]
    public void Rotation_commit_cancel_undo_redo_share_exact_native_and_authored_state()
    {
        using var f = new Fixture();
        var before = f.Snapshot;
        var local = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f);
        var world = before.Baseline.Frame.ToWorldDelta(local);
        f.Perform(new(Vector3.Zero, world, Vector3.One));
        var after = f.Snapshot;
        Assert.True(MathF.Abs(Quaternion.Dot(local, after.Controls.Rotation)) > .99999f);
        var committed = f.Live.ToDictionary();
        Assert.True(f.Service.Undo().Success);
        Assert.True(before.ContentEquals(f.Snapshot));
        Assert.True(f.Service.Redo().Success);
        Assert.True(after.ContentEquals(f.Snapshot));
        Assert.Equal(committed, f.Live);
        var id = f.Begin();
        Assert.True(f.Service.Update(id, new(Vector3.One, Quaternion.Identity, Vector3.One)).Success);
        Assert.True(f.Service.Cancel(id).Success);
        Assert.True(after.ContentEquals(f.Snapshot));
        Assert.Equal(committed, f.Live);
    }

    [Fact]
    public void Repeated_updates_use_frozen_before_and_commit_once()
    {
        using var f = new Fixture();
        int appended = 0;
        f.History.Appended += _ => appended++;
        var id = f.Begin();
        var delta = new TransformDelta(new(2, 3, 4), Quaternion.Identity, Vector3.One);
        Assert.True(f.Service.Update(id, delta).Success);
        var once = f.Live.ToDictionary();
        Assert.True(f.Service.Update(id, delta).Success);
        Assert.Equal(once, f.Live);
        Assert.True(f.Service.Commit(id).Success);
        Assert.Equal(1, appended);
    }

    [Fact]
    public void Cumulative_spacing_can_exceed_member_scale_limits()
    {
        using var f = new Fixture();
        f.Perform(new(Vector3.Zero, Quaternion.Identity, new(100)), GroupScaleMode.SpacingOnly);
        f.Perform(new(Vector3.Zero, Quaternion.Identity, new(100)), GroupScaleMode.SizesAndSpacing);
        Assert.Equal(new Vector3(10000), f.Snapshot.Controls.SpacingScale);
        Assert.Equal(new Vector3(100), f.Snapshot.Controls.OwnScale);
        Assert.True(f.Coordinator.TryReadSelection(GroupScaleMode.SpacingOnly, out var display, out _));
        Assert.Equal(new Vector3(10000), display.Scale);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.MaxValue)]
    [InlineData(float.Epsilon)]
    public void Invalid_or_unrepresentable_member_scale_is_refused_before_any_write(float factor)
    {
        using var f = new Fixture();
        var before = f.Snapshot;
        var id = f.Begin();
        Assert.False(f.Service.Update(id, new(Vector3.Zero, Quaternion.Identity, new(factor))).Success);
        Assert.Equal(0, f.Writes);
        Assert.Same(before, f.Snapshot);
    }

    [Fact]
    public void Failed_apply_and_delayed_recovery_do_not_publish_controls_or_history()
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
    }

    [Theory]
    [InlineData(1e30f, 1e20f)]
    [InlineData(1e-30f, 1e-20f)]
    public void Cumulative_control_overflow_or_underflow_is_refused_before_writes(float prior, float factor)
    {
        using var f = new Fixture();
        var initial = f.Snapshot;
        f.State.Put(GroupTransformKey.For(null, f.Targets), new(initial.Baseline, initial.Expected,
            initial.Controls with { SpacingScale = new(prior) }));
        var id = f.Begin(GroupScaleMode.SpacingOnly);
        Assert.False(f.Service.Update(id, new(Vector3.Zero, Quaternion.Identity, new(factor))).Success);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public void Small_finite_spacing_factors_do_not_inherit_native_size_bounds()
    {
        using var f = new Fixture();
        f.Perform(new(Vector3.Zero, Quaternion.Identity, new(1e-8f)), GroupScaleMode.SpacingOnly);
        Assert.Equal(new Vector3(1e-8f), f.Snapshot.Controls.SpacingScale);
        Assert.All(f.Live.Values, pose => Assert.Equal(Vector3.One, pose.Scale));
    }

    [Fact]
    public void Delayed_undo_recovery_commits_metadata_and_history_once()
    {
        using var f = new Fixture();
        var before = f.Snapshot;
        f.Perform(new(Vector3.One, Quaternion.Identity, Vector3.One));
        var after = f.Snapshot;
        f.FailRestore = true;
        Assert.False(f.Service.Undo().Success);
        Assert.Same(after, f.Snapshot);
        Assert.True(f.History.CanUndo);
        f.FailRestore = false;
        Assert.True(f.Service.Undo().Success);
        Assert.True(before.ContentEquals(f.Snapshot));
        Assert.False(f.History.CanUndo);
        Assert.True(f.History.CanRedo);
    }

    [Fact]
    public void Frozen_group_record_is_checked_at_commit()
    {
        using var f = new Fixture();
        var before = f.Snapshot;
        var id = f.Begin();
        Assert.True(f.Service.Update(id, new(Vector3.One, Quaternion.Identity, Vector3.One)).Success);
        f.State.Initialize(null, f.Live, new(Vector3.Zero, Quaternion.Identity), out _);
        Assert.False(f.Service.Commit(id).Success);
        Assert.False(f.History.CanUndo);
        Assert.True(before.ContentEquals(f.Snapshot));
        Assert.Equal(before.Expected, f.Live);
    }

    [Fact]
    public void Rebind_preserves_authored_state_and_rekeys_history()
    {
        using var f = new Fixture();
        f.Perform(new(Vector3.One, Quaternion.Identity, Vector3.One));
        var controls = f.Snapshot.Controls;
        f.Rebind();
        Assert.Equal(controls, f.Snapshot.Controls);
        Assert.True(f.Service.Undo().Success);
        Assert.Equal(Vector3.Zero, f.Live[f.Targets[0]].Position);
        Assert.True(f.Service.Redo().Success);
        Assert.Equal(controls, f.Snapshot.Controls);
    }

    [Fact]
    public void Nested_assembly_and_membership_history_capture_final_effective_members()
    {
        using var f = new Fixture(4);
        var steps = new GroupSteps(f.Groups, f.History, new ValueJournal(f.History), f.State, f.Coordinator);
        SceneGroup? parent = null;
        steps.Run("Duplicate tree", () => {
            parent = steps.Create("Parent", [], allowThin: true)!;
            var child = steps.Create("Child", f.Selected.Take(2).ToArray())!;
            steps.Nest(child.Id, parent.Id);
            steps.AddMember(parent.Id, f.Selected[2]);
        });
        var original = f.State.NamedSnapshot(parent!.Id)!;
        Assert.Equal(3, original.Expected.Count);
        Assert.True(original.Baseline.Frame.Rotation != Quaternion.Identity);
        steps.AddMember(parent.Id, f.Selected[3]);
        Assert.Equal(4, f.State.NamedSnapshot(parent.Id)!.Expected.Count);
        Assert.True(f.Service.Undo().Success);
        Assert.True(original.ContentEquals(f.State.NamedSnapshot(parent.Id)!));
        Assert.True(f.Service.Redo().Success);
        Assert.Equal(4, f.State.NamedSnapshot(parent.Id)!.Expected.Count);
        Assert.True(f.Service.Undo().Success);
        Assert.True(f.Service.Undo().Success);
        Assert.Empty(f.Groups.All);
        Assert.Empty(f.State.CaptureNamed());
    }

    [Fact]
    public void Admission_refuses_partial_or_unsupported_selection()
    {
        using var f = new Fixture();
        Assert.False(f.Coordinator.Admit([f.Targets[0]], GroupScaleMode.SpacingOnly, out _, out _));
        f.Refused = f.Targets[1];
        Assert.False(f.Coordinator.Admit(f.Targets, GroupScaleMode.SpacingOnly, out _, out _));
        var result = f.Service.Begin(new(f.Targets, TransformOperation.Universal, TransformSpace.World,
            PivotMode.Centroid, IsGroupTransform: true));
        Assert.False(result.Success);
        Assert.Equal(0, f.Writes);
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
        Assert.True(f.Service.Undo().Success); // unlock
        Assert.True(f.Groups.Find(parent.Id)!.Locked);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Service.Undo().Success); // lock
        Assert.False(f.Groups.Find(parent.Id)!.Locked);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Service.Undo().Success); // transform
        Assert.True(initial.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Service.Redo().Success);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Service.Redo().Success);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
        Assert.True(f.Service.Redo().Success);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(child.Id)!));
    }

    [Fact]
    public void Temporary_capability_refusal_preserves_named_state_and_membership_updates_preserve_controls()
    {
        using var f = new Fixture(3);
        var steps = new GroupSteps(f.Groups, f.History, new ValueJournal(f.History), f.State, f.Coordinator);
        var group = steps.Create("Pair", f.Selected.Take(2).ToArray())!;
        var original = f.State.NamedSnapshot(group.Id)!;
        f.Refused = f.Targets[0];
        f.Camera = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .8f);
        steps.Rename(group.Id, "Renamed");
        f.Coordinator.BindingsPublished();
        Assert.Same(original, f.State.NamedSnapshot(group.Id));
        Assert.Same(original, f.State.CaptureNamed().Single().Value);
        f.Refused = null;
        steps.AddMember(group.Id, f.Selected[2]);
        var expanded = f.State.NamedSnapshot(group.Id)!;
        Assert.Equal(3, expanded.Expected.Count);
        Assert.Equal(original.Baseline.Frame, expanded.Baseline.Frame);
        Assert.Equal(original.Controls with { Position = expanded.Baseline.InitialCentroid }, expanded.Controls);
        Assert.True(f.Service.Undo().Success);
        Assert.True(original.ContentEquals(f.State.NamedSnapshot(group.Id)!));
        Assert.True(f.Service.Redo().Success);
        Assert.True(expanded.ContentEquals(f.State.NamedSnapshot(group.Id)!));
    }

    [Fact]
    public void Anonymous_store_is_bounded_and_missing_read_does_not_initialize()
    {
        var state = new GroupTransformState();
        TransformTargetId[]? first = null;
        for (int i = 0; i <= GroupTransformState.AnonymousCapacity; i++)
        {
            var pair = new[] { TransformTargetId.ForActor(ActorId.New()), TransformTargetId.ForActor(ActorId.New()) };
            first ??= pair;
            Assert.False(state.TryRead(null, pair, _ => PoseTransform.Identity,
                GroupScaleMode.SpacingOnly, out _, out _));
            Assert.Null(state.Snapshot(null, pair));
            Assert.True(state.Initialize(null, pair.ToDictionary(target => target, _ => PoseTransform.Identity),
                GroupTransformFrame.World(Vector3.Zero), out _));
        }
        Assert.Null(state.Snapshot(null, first!));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Deferred_membership_capture_is_completed_and_frozen_by_history_restore(
        bool unavailableRead, bool refusedOnRedo)
    {
        using var f = new Fixture(3);
        var steps = new GroupSteps(f.Groups, f.History, new ValueJournal(f.History), f.State, f.Coordinator);
        int reappliedGates = 0;
        steps.ReapplyGates = () => reappliedGates++;
        var group = steps.Create("Pair", f.Selected.Take(2).ToArray())!;
        f.SelectNamed(group);
        var frame = f.State.NamedSnapshot(group.Id)!.Baseline.Frame;
        f.PerformNamed(group, new(Vector3.Zero,
            frame.ToWorldDelta(Quaternion.CreateFromAxisAngle(Vector3.UnitX, .5f)), new(1.5f)));
        f.PerformNamed(group, new(Vector3.Zero, Quaternion.Identity, new(2f)),
            GroupScaleMode.SpacingOnly);
        var authored = f.State.NamedSnapshot(group.Id)!;
        var snapshotPort = new SnapshotPort(f);
        var journal = new UndoJournal(f.History, f.Service, snapshotPort,
            new Lazy<IPoseSnapshotPort>(() => snapshotPort), _ => true, _ => {});
        var untouched = f.Live.ToDictionary();
        int writes = f.Writes, frameReads = f.FrameReads;
        void Refuse(bool refused)
        {
            if (unavailableRead) f.Unreadable = refused ? f.Targets[0] : null;
            else f.Refused = refused ? f.Targets[0] : null;
        }

        Refuse(true);
        steps.AddMember(group.Id, f.Selected[2]);
        var membershipEntry = f.History.PeekUndo();
        f.SelectNamed(group);
        f.Coordinator.BindingsPublished();
        Assert.Same(authored, f.State.NamedSnapshot(group.Id));
        Assert.False(f.Coordinator.TryReadSelection(GroupScaleMode.SpacingOnly, out _, out _));
        Assert.Same(authored, f.State.NamedSnapshot(group.Id)); // read is pure
        Refuse(false);
        f.Coordinator.BindingsPublished();
        var complete = f.State.NamedSnapshot(group.Id)!;
        Assert.Equal(3, complete.Expected.Count);
        Assert.Equal(authored.Controls with {
            Position = GroupTransformBaseline.Centroid(untouched.Values) }, complete.Controls);
        Assert.Equal(frame, complete.Baseline.Frame);
        Assert.True(journal.Undo().Success);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(group.Id)!));

        Refuse(refusedOnRedo);
        if (refusedOnRedo)
        {
            int gatesBeforeRefusal = reappliedGates;
            Assert.False(journal.Redo().Success);
            Assert.Equal(2, f.Groups.Descendants(f.Groups.Find(group.Id)!).Count());
            Assert.True(authored.ContentEquals(f.State.NamedSnapshot(group.Id)!));
            Assert.Same(membershipEntry, f.History.PeekRedo());
            Assert.False(journal.Redo().Success); // still retryable, no partial structure
            Assert.Same(membershipEntry, f.History.PeekRedo());
            Assert.Equal(gatesBeforeRefusal, reappliedGates);
            Refuse(false);
        }
        Assert.True(journal.Redo().Success);
        f.SelectNamed(f.Groups.Find(group.Id)!);
        Assert.True(f.Coordinator.TryReadSelection(GroupScaleMode.SpacingOnly, out var read, out _));
        Assert.True(complete.ContentEquals(f.State.NamedSnapshot(group.Id)!));
        Assert.Equal(complete.Controls.Position, read.Position);
        Assert.Equal(authored.Controls.Rotation, read.Rotation);
        Assert.Equal(authored.Controls.SpacingScale, read.Scale);

        // Once resolved at the history boundary, redo owns a complete frozen
        // record and no longer depends on capturing temporarily unreadable poses.
        Assert.True(journal.Undo().Success);
        Refuse(true);
        Assert.True(journal.Redo().Success);
        Assert.True(complete.ContentEquals(f.State.NamedSnapshot(group.Id)!));
        Refuse(false);
        Assert.Equal(writes, f.Writes);
        Assert.Equal(frameReads, f.FrameReads);
        Assert.Equal(untouched, f.Live);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("add")]
    [InlineData("nest")]
    public void Membership_changes_preserve_owned_controls_and_members_then_use_new_centroid(string change)
    {
        using var f = new Fixture(5);
        var steps = new GroupSteps(f.Groups, f.History, new ValueJournal(f.History), f.State, f.Coordinator);
        var group = steps.Create("Group", f.Selected.Take(change == "remove" ? 3 : 2).ToArray())!;
        var child = change == "nest" ? steps.Create("Child", f.Selected.Skip(3).ToArray()) : null;
        f.SelectNamed(group);
        var originalFrame = f.State.NamedSnapshot(group.Id)!.Baseline.Frame;
        f.PerformNamed(group, new(Vector3.Zero,
            originalFrame.ToWorldDelta(Quaternion.CreateFromAxisAngle(Vector3.UnitX, .6f)),
            new(1.5f, 2f, .75f)));
        f.PerformNamed(group, new(Vector3.Zero, Quaternion.Identity, new(2)),
            GroupScaleMode.SpacingOnly);
        var authored = f.State.NamedSnapshot(group.Id)!;
        var childBefore = child == null ? null : f.State.NamedSnapshot(child.Id);
        var liveBeforeMembership = f.Live.ToDictionary();
        int writes = f.Writes, frameReads = f.FrameReads;
        f.Camera = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 1.1f);
        if (change == "remove") steps.RemoveMember(f.Selected[2]);
        else if (change == "add") steps.AddMember(group.Id, f.Selected[2]);
        else Assert.True(steps.Nest(child!.Id, group.Id));
        var updated = f.State.NamedSnapshot(group.Id)!;
        var members = f.Groups.Descendants(f.Groups.Find(group.Id)!).Select(GroupTransformCoordinator.Target)
            .Select(target => target!.Value).ToArray();
        var centroid = GroupTransformBaseline.Centroid(members.Select(target => liveBeforeMembership[target]));
        Assert.NotEqual(authored.Controls.Position, centroid);
        Assert.Equal(authored.Controls with { Position = centroid }, updated.Controls);
        Assert.Equal(authored.Baseline.Frame, updated.Baseline.Frame);
        Assert.Equal(writes, f.Writes);
        Assert.Equal(frameReads, f.FrameReads);
        Assert.Equal(liveBeforeMembership, f.Live);
        Assert.Equal(members.ToHashSet(), updated.Expected.Keys.ToHashSet());
        Assert.All(updated.Expected, pair => Assert.Equal(liveBeforeMembership[pair.Key], pair.Value));
        if (child != null) Assert.Same(childBefore, f.State.NamedSnapshot(child.Id));
        Assert.Same(updated, f.State.CaptureNamed()[GroupTransformKey.For(group.Id, members)]);

        Assert.True(f.Service.Undo().Success);
        Assert.True(authored.ContentEquals(f.State.NamedSnapshot(group.Id)!));
        Assert.Equal(liveBeforeMembership, f.Live);
        Assert.True(f.Service.Redo().Success);
        Assert.True(updated.ContentEquals(f.State.NamedSnapshot(group.Id)!));
        Assert.Equal(liveBeforeMembership, f.Live);

        f.SelectNamed(f.Groups.Find(group.Id)!);
        var begin = f.Service.Begin(new(members, TransformOperation.Rotate, TransformSpace.World,
            PivotMode.Centroid, GroupId: group.Id, IsGroupTransform: true));
        Assert.True(begin.Success, begin.Detail);
        Assert.Equal(centroid, f.Service.ActivePivot);
        var delta = new TransformDelta(Vector3.Zero,
            originalFrame.ToWorldDelta(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .35f)), Vector3.One);
        Assert.True(f.Service.Update(begin.GestureId!.Value, delta).Success);
        Assert.True(f.Service.Commit(begin.GestureId.Value).Success);
        foreach (var target in members)
            Assert.True(GroupTransformReadModel.Equivalent(
                TransformMath.Apply(liveBeforeMembership[target], delta, TransformSpace.World, centroid,
                    rotatePosition: true, scalePosition: true, scaleOwn: true),
                f.Live[target]));
        Assert.Equal(authored.Controls.SpacingScale, f.State.NamedSnapshot(group.Id)!.Controls.SpacingScale);
        Assert.Equal(authored.Controls.OwnScale, f.State.NamedSnapshot(group.Id)!.Controls.OwnScale);
        Assert.True(f.Service.Undo().Success);
        Assert.True(updated.ContentEquals(f.State.NamedSnapshot(group.Id)!));
        Assert.Equal(liveBeforeMembership, f.Live);
    }

    [Fact]
    public void New_selection_captures_only_after_active_gesture_is_cancelled()
    {
        using var f = new Fixture(3);
        f.Selection.Clear();
        f.Selection.Add(f.Selected[0]); f.Selection.Add(f.Selected[1]);
        var pair = f.Targets.Take(2).ToArray();
        var begin = f.Service.Begin(new(pair, TransformOperation.Translate, TransformSpace.World,
            PivotMode.Centroid, IsGroupTransform: true));
        Assert.True(begin.Success);
        Assert.True(f.Service.Update(begin.GestureId!.Value,
            new(Vector3.One, Quaternion.Identity, Vector3.One)).Success);
        f.State.Clear();
        f.Selection.Add(f.Selected[2]);
        Assert.Null(f.Service.ActiveGesture);
        Assert.Equal(Vector3.Zero, f.Live[f.Targets[0]].Position);
        Assert.True(f.Coordinator.TryReadSelection(GroupScaleMode.SpacingOnly, out _, out _));
        Assert.Equal(Vector3.Zero, f.Snapshot.Expected[f.Targets[0]].Position);
    }

    [Fact]
    public void Snapshot_fallback_finishes_group_restore_through_recovery_before_committing()
    {
        using var f = new Fixture();
        var before = f.Snapshot;
        f.Perform(new(Vector3.One, Quaternion.Identity, Vector3.One));
        var patch = (TransformPatch)f.History.PeekUndo()!;
        f.History.Clear();
        var snapshots = new SnapshotPort(f);
        patch = patch with { Context = new(
            f.Targets.Select(target => new ActorStateKey(target.Actor!.Value.LogicalId,
                target.Actor.Value, [], "old", 0)).ToArray(),
            f.Targets.Select(target => new ActorSnapshot(target.Actor!.Value.LogicalId,
                before.Expected[target], [])).ToArray(),
            f.Targets.Select(target => new ActorSnapshot(target.Actor!.Value.LogicalId,
                f.Live[target], [])).ToArray()) };
        f.History.Append(patch);
        var journal = new UndoJournal(f.History, f.Service, snapshots,
            new Lazy<IPoseSnapshotPort>(() => snapshots), _ => true, _ => {}) { StateKeys = true };
        f.FailRestore = true;
        Assert.True(journal.Undo().Success); // snapshot import starts asynchronously
        Assert.True(f.History.CanUndo);
        snapshots.Complete();
        snapshots.Complete();
        Assert.NotNull(f.Service.PendingRecovery);
        Assert.True(f.History.CanUndo);
        Assert.NotEqual(before.Controls, f.Snapshot.Controls);
        f.Rebind();
        f.FailRestore = false;
        Assert.True(journal.Undo().Success);
        Assert.Equal(before.Controls, f.Snapshot.Controls);
        Assert.False(f.History.CanUndo);
        Assert.True(f.History.CanRedo);
    }

    private sealed class SnapshotPort(Fixture fixture) : IActorStateKeySource, IPoseSnapshotPort
    {
        private Action<bool>? _done;
        public ActorStateKey? Current(Guid lineage) => new(lineage,
            fixture.Targets.First(target => target.Actor!.Value.LogicalId == lineage).Actor!.Value,
            [], "changed", 1);
        public ActorSnapshot? Capture(Guid lineage) => null;
        public bool Restore(ActorSnapshot snapshot, Action<bool> finished)
        { _done = finished; return true; }
        public void Complete() { var done = _done; _done = null; done!(true); }
    }

    private static PoseTransform Pose(Vector3 position) =>
        PoseTransform.CreateChecked(position, Quaternion.Identity, Vector3.One);

    [Fact]
    public void Group_snapshot_equality_includes_metadata_even_when_structure_is_unchanged()
    {
        using var f = new Fixture();
        var key = GroupTransformKey.For(Guid.NewGuid(), f.Targets);
        var before = f.Groups.Capture().WithTransforms(new Dictionary<GroupTransformKey, GroupTransformSnapshot> {
            [key] = f.Snapshot });
        var changed = new GroupTransformSnapshot(f.Snapshot.Baseline, f.Snapshot.Expected,
            f.Snapshot.Controls with { SpacingScale = new(2) });
        var after = f.Groups.Capture().WithTransforms(new Dictionary<GroupTransformKey, GroupTransformSnapshot> {
            [key] = changed });
        Assert.False(before.Equals(after));
    }

    private sealed class Fixture : IGroupTransformSource, ITransformRuntimePort, IDisposable
    {
        public readonly SelectionSession Selection = new();
        public readonly SceneSession Scene;
        public readonly SceneGroups Groups = new();
        public readonly GroupTransformState State = new();
        public readonly TransformHistory History = new();
        public readonly GroupTransformCoordinator Coordinator;
        public readonly TransformGestureService Service;
        public Dictionary<TransformTargetId, PoseTransform> Live = new();
        public TransformTargetId[] Targets;
        public SelectionId[] Selected => Targets.Select(target => SelectionId.ForActor(target.Actor!.Value)).ToArray();
        public Quaternion Camera = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f);
        public int FrameReads, Writes;
        public bool FailApply, FailRestore;
        public TransformTargetId? Refused, Unreadable, Stale;
        public Action? AfterApply;
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
            History.Reconcile(Scene.Contains, _ => true, CurrentTarget);
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
        public void SelectNamed(SceneGroup group)
        {
            Selection.Clear();
            Groups.ActiveGroupId = group.Id;
            foreach (var member in Groups.Descendants(group)) Selection.Add(member);
        }
        public void PerformNamed(SceneGroup group, TransformDelta delta,
            GroupScaleMode mode = GroupScaleMode.SizesAndSpacing)
        {
            var targets = Groups.Descendants(group).Select(GroupTransformCoordinator.Target)
                .Select(target => target!.Value).ToArray();
            var begin = Service.Begin(new(targets, TransformOperation.Universal, TransformSpace.World,
                PivotMode.Centroid, GroupScale: mode, GroupId: group.Id, IsGroupTransform: true));
            Assert.True(begin.Success, begin.Detail);
            var update = Service.Update(begin.GestureId!.Value, delta);
            Assert.True(update.Success, update.Detail);
            var commit = Service.Commit(begin.GestureId.Value);
            Assert.True(commit.Success, commit.Detail);
        }
        public PoseTransform? Read(TransformTargetId target) =>
            target != Unreadable && Live.TryGetValue(target, out var pose) ? pose : null;
        public string? Refusal(TransformTargetId target) => target == Refused ? "Attached light" : null;
        public bool TryFrame(Vector3 origin, out GroupTransformFrame frame)
        { FrameReads++; frame = new(origin, Camera); return true; }
        public TransformTargetId? CurrentTarget(TransformTargetId target) =>
            target == Stale ? null : Live.Keys.Cast<TransformTargetId?>().FirstOrDefault(current =>
                current!.Value.Kind == target.Kind
                && GroupTransformIdentity.LogicalId(current.Value) == GroupTransformIdentity.LogicalId(target));
        public TransformPortResult Capture(TransformTargetId target) => Read(target) is { } pose
            ? TransformPortResult.Ok(new(target, pose, new BonePose(), false))
            : TransformPortResult.Fail(TransformPortStatus.StaleTarget, "Missing");
        public TransformPortResult ApplyAbsolute(TransformTargetState baseline, PoseTransform desired, bool rawBaseline = false)
        {
            Writes++; Live[baseline.Target] = desired;
            AfterApply?.Invoke();
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
