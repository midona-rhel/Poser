using System.Numerics;
using NSubstitute;
using Poser.Application.Presentation;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Application.Tests.Scene;

public sealed class RetainedTransformTargetTests
{
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "actor")]
    [InlineData(true, "slot")]
    [InlineData(true, "partial")]
    [InlineData(true, "index")]
    [InlineData(true, "name")]
    public void Active_bone_gesture_recovery_follows_only_the_same_bone_after_redraw(bool publicationGap, string? mismatch)
    {
        var actor = ActorId.New();
        var old = new BoneId(new SkeletonId(actor, PoseSlot.Character, 0), 1, 3, "j_f_ago");
        var current = old with { Skeleton = old.Skeleton.NextGeneration() };
        current = mismatch switch
        {
            "actor" => current with { Skeleton = current.Skeleton with { Actor = ActorId.New() } },
            "slot" => current with { Skeleton = current.Skeleton with { Slot = PoseSlot.MainHand } },
            "partial" => current with { PartialId = 2 },
            "index" => current with { BoneIndex = 4 },
            "name" => current with { CanonicalName = "j_f_dago" },
            _ => current,
        };
        var scene = Scene(old);
        var runtime = Substitute.For<ITransformRuntimePort>();
        var baseline = new TransformTargetState(TransformTargetId.ForBone(old),
            PoseTransform.Identity with { Position = Vector3.One }, new BonePose(), true);
        runtime.Capture(baseline.Target).Returns(TransformPortResult.Ok(baseline));
        runtime.Restore(Arg.Any<TransformTargetState>()).Returns(call =>
        {
            var state = call.Arg<TransformTargetState>();
            return scene.Contains(state.Target) ? TransformPortResult.Ok(state)
                : TransformPortResult.Fail(TransformPortStatus.StaleTarget, "Stale skeleton");
        });
        using var service = new TransformGestureService(scene, runtime, new EditHistory());
        Assert.True(service.Begin(new([baseline.Target], TransformOperation.Rotate,
            TransformSpace.World, PivotMode.Primary)).Success);
        if (publicationGap)
        {
            Assert.True(scene.TryRefresh(new SceneSnapshot(2,
                [new ActorDescriptor(actor, "Actor", [])], [], [], [])).Accepted);
            service.ReconcileScene(scene.Contains);
            Assert.NotNull(service.PendingRecovery);
            Assert.False(service.TryCompleteRecovery());
        }
        Assert.True(scene.TryRefresh(new SceneSnapshot(3,
            [new ActorDescriptor(current.Skeleton.Actor, "Actor", [new SkeletonDescriptor(current.Skeleton,
                [new BoneDescriptor(current, "Jaw", null)])])], [], [], [])).Accepted);
        service.ReconcileScene(scene.Contains);

        if (mismatch != null)
        {
            Assert.False(service.TryCompleteRecovery());
            Assert.NotNull(service.PendingRecovery);
            runtime.DidNotReceive().Restore(Arg.Is<TransformTargetState>(state =>
                state.Target == TransformTargetId.ForBone(current)));
            return;
        }
        Assert.True(service.TryCompleteRecovery());
        Assert.Null(service.ActiveGesture);
        Assert.Null(service.PendingRecovery);
        runtime.Received().Restore(baseline with { Target = TransformTargetId.ForBone(current) });
        Assert.Null(service.History.PeekUndo());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Redraw_gap_retains_whole_patch_then_rekeys_when_slot_returns(bool redo)
    {
        var actor = ActorId.New();
        var bone = new BoneId(new SkeletonId(actor, PoseSlot.Character, 0), 0, 0, "n_root");
        var scene = Scene(bone);
        var history = new EditHistory();
        var state = new TransformTargetState(TransformTargetId.ForBone(bone), PoseTransform.Identity, new BonePose(), true);
        var patch = new TransformPatch("Bone edit", [state], [state]);
        history.Append(patch);
        if (redo) history.CommitUndo(patch);
        Assert.True(scene.TryRefresh(new SceneSnapshot(2,
            [new ActorDescriptor(actor, "Actor", [])], [], [], [])).Accepted);

        history.Reconcile(scene.Contains, scene.CurrentTransformTarget, scene.IsTransformTargetPending);

        var waiting = Assert.IsType<TransformPatch>(redo ? history.PeekRedo() : history.PeekUndo());
        Assert.Equal(patch.Id, waiting.Id);
        Assert.True(waiting.AwaitingTargets);
        var runner = Substitute.For<IUndoRunner>();
        var journal = new UndoJournal(history, runner, _ => true, Substitute.For<IUserNotices>());
        for (int attempt = 0; attempt < 3; attempt++)
        {
            Assert.Equal(UndoJournal.TargetsPending, (redo ? journal.Redo() : journal.Undo()).Detail);
            Assert.Equal(UndoJournal.TargetsPending,
                (redo ? journal.Redo(SelectionId.ForActor(actor)) : journal.Undo(SelectionId.ForActor(actor))).Detail);
        }
        runner.DidNotReceive().Run(Arg.Any<HistoryEntry>(), Arg.Any<bool>(), Arg.Any<SelectionId?>());
        Assert.False(scene.Contains(state.Target));
        Assert.Null(scene.CurrentTransformTarget(state.Target));
        var current = bone with { Skeleton = bone.Skeleton.NextGeneration() };
        Assert.True(scene.TryRefresh(new SceneSnapshot(3,
            [new ActorDescriptor(actor, "Actor", [new SkeletonDescriptor(current.Skeleton,
                [new BoneDescriptor(current, "Root", null)])])], [], [], [])).Accepted);

        history.Reconcile(scene.Contains, scene.CurrentTransformTarget, scene.IsTransformTargetPending);

        var ready = Assert.IsType<TransformPatch>(redo ? history.PeekRedo() : history.PeekUndo());
        Assert.Equal(patch.Id, ready.Id);
        Assert.False(ready.AwaitingTargets);
        Assert.Equal(TransformTargetId.ForBone(current), Assert.Single(ready.Before).Target);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pending_patch_is_dropped_when_owner_disappears_or_returned_slot_lacks_exact_bone(bool ownerGone)
    {
        var actor = ActorId.New();
        var bone = new BoneId(new SkeletonId(actor, PoseSlot.Character, 0), 0, 0, "n_root");
        var scene = new SceneSession(new SelectionSession());
        Assert.True(scene.TryRefresh(new SceneSnapshot(1,
            [new ActorDescriptor(actor, "Actor", [])], [], [], [])).Accepted);
        var history = new EditHistory();
        var state = new TransformTargetState(TransformTargetId.ForBone(bone), PoseTransform.Identity, new BonePose(), true);
        history.Append(new TransformPatch("Bone edit", [state], [state]));
        history.Reconcile(scene.Contains, scene.CurrentTransformTarget, scene.IsTransformTargetPending);
        Assert.True(Assert.IsType<TransformPatch>(history.PeekUndo()).AwaitingTargets);
        Assert.True(scene.TryRefresh(new SceneSnapshot(2, ownerGone ? [] :
            [new ActorDescriptor(actor, "Actor", [new SkeletonDescriptor(bone.Skeleton.NextGeneration(), [])])],
            [], [], [])).Accepted);

        history.Reconcile(scene.Contains, scene.CurrentTransformTarget, scene.IsTransformTargetPending);

        Assert.Null(history.PeekUndo());
    }

    [Fact]
    public void Mixed_actor_patch_waits_for_all_replacements_before_rekeying()
    {
        var actor = ActorId.New();
        var body = new BoneId(new SkeletonId(actor, PoseSlot.Character, 0), 0, 0, "n_root");
        var otherActor = ActorId.New();
        var weapon = new BoneId(new SkeletonId(otherActor, PoseSlot.MainHand, 0), 0, 0, "n_root");
        var bodyNow = body with { Skeleton = body.Skeleton.NextGeneration() };
        var weaponNow = weapon with { Skeleton = weapon.Skeleton.NextGeneration() };
        var scene = new SceneSession(new SelectionSession());
        Assert.True(scene.TryRefresh(new SceneSnapshot(1,
            [new ActorDescriptor(actor, "Ready actor", [new SkeletonDescriptor(bodyNow.Skeleton,
                [new BoneDescriptor(bodyNow, "Body", null)])]),
             new ActorDescriptor(otherActor, "Redrawing actor", [])], [], [], [])).Accepted);
        var history = new EditHistory();
        TransformTargetState State(BoneId bone) => new(TransformTargetId.ForBone(bone), PoseTransform.Identity, new BonePose(), true);
        history.Append(new TransformPatch("Two bones", [State(body), State(weapon)], [State(body), State(weapon)]));

        history.Reconcile(scene.Contains, scene.CurrentTransformTarget, scene.IsTransformTargetPending);

        var waiting = Assert.IsType<TransformPatch>(history.PeekUndo());
        Assert.True(waiting.AwaitingTargets);
        Assert.Equal(TransformTargetId.ForBone(body), waiting.Before[0].Target);
        Assert.True(scene.TryRefresh(new SceneSnapshot(2,
            [new ActorDescriptor(actor, "Ready actor", [
                new SkeletonDescriptor(bodyNow.Skeleton, [new BoneDescriptor(bodyNow, "Body", null)])]),
             new ActorDescriptor(otherActor, "Other actor", [
                new SkeletonDescriptor(new SkeletonId(otherActor, PoseSlot.Character, 1), []),
                new SkeletonDescriptor(weaponNow.Skeleton, [new BoneDescriptor(weaponNow, "Weapon", null)])])], [], [], [])).Accepted);

        history.Reconcile(scene.Contains, scene.CurrentTransformTarget, scene.IsTransformTargetPending);

        var ready = Assert.IsType<TransformPatch>(history.PeekUndo());
        Assert.False(ready.AwaitingTargets);
        Assert.Equal(TransformTargetId.ForBone(bodyNow), ready.Before[0].Target);
        Assert.Equal(TransformTargetId.ForBone(weaponNow), ready.Before[1].Target);
    }

    [Fact]
    public void Unequipped_weapon_on_ready_actor_is_not_a_pending_redraw()
    {
        var actor = ActorId.New();
        var body = new BoneId(new SkeletonId(actor, PoseSlot.Character, 0), 0, 0, "n_root");
        var weapon = new BoneId(new SkeletonId(actor, PoseSlot.MainHand, 0), 0, 0, "n_root");
        var scene = Scene(body);
        var history = new EditHistory();
        var state = new TransformTargetState(TransformTargetId.ForBone(weapon), PoseTransform.Identity, new BonePose(), true);
        history.Append(new TransformPatch("Weapon edit", [state], [state]));

        history.Reconcile(scene.Contains, scene.CurrentTransformTarget, scene.IsTransformTargetPending);

        Assert.False(scene.IsTransformTargetPending(state.Target));
        Assert.Null(history.PeekUndo());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Undo_and_redo_bone_patches_survive_slot_redraw(bool redo)
    {
        var actor = ActorId.New();
        var old = new BoneId(new SkeletonId(actor, PoseSlot.MainHand, 0), 0, 0, "n_root");
        var current = old with { Skeleton = old.Skeleton.NextGeneration() };
        var scene = Scene(current);
        var history = new EditHistory();
        var before = new TransformTargetState(TransformTargetId.ForBone(old), PoseTransform.Identity, new BonePose(), false);
        var after = before with { Transform = PoseTransform.Identity with { Position = Vector3.One }, HasOverride = true };
        var patch = new TransformPatch("Move bone", [before], [after]);
        history.Append(patch);
        if (redo) history.CommitUndo(patch);

        history.Reconcile(scene.Contains, scene.CurrentTransformTarget);

        var retained = Assert.IsType<TransformPatch>(redo ? history.PeekRedo() : history.PeekUndo());
        Assert.Equal(patch.Id, retained.Id);
        Assert.Equal(before with { Target = TransformTargetId.ForBone(current) }, Assert.Single(retained.Before));
        Assert.Equal(after with { Target = TransformTargetId.ForBone(current) }, Assert.Single(retained.After));
        Assert.False(scene.Contains(TransformTargetId.ForBone(old)));
        Assert.Equal(SelectionId.ForActor(actor), scene.Resolve(SelectionId.ForBone(old)));
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("slot")]
    [InlineData("partial")]
    [InlineData("index")]
    [InlineData("name")]
    public void Bone_reconciliation_never_matches_a_different_native_identity(string mismatch)
    {
        var actor = ActorId.New();
        var old = new BoneId(new SkeletonId(actor, PoseSlot.Character, 0), 0, 0, "n_root");
        var current = old with { Skeleton = old.Skeleton.NextGeneration() };
        current = mismatch switch
        {
            "actor" => current with { Skeleton = current.Skeleton with { Actor = ActorId.New() } },
            "slot" => current with { Skeleton = current.Skeleton with { Slot = PoseSlot.MainHand } },
            "partial" => current with { PartialId = 1 },
            "index" => current with { BoneIndex = 1 },
            "name" => current with { CanonicalName = "N_ROOT" },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };
        Assert.Null(Scene(current).CurrentTransformTarget(TransformTargetId.ForBone(old)));
    }

    [Fact]
    public void Every_entity_transform_kind_resolves_only_within_the_committed_scene()
    {
        var id = Guid.NewGuid();
        var scene = new SceneSession(new SelectionSession());
        var actor = new ActorId(id, 1);
        var light = new LightId(id, 1);
        var prop = new PropId(id, 1);
        var world = new WorldObjectId(id, 1);
        var collider = new OverlayId(id, 1);
        // Sharing a GUID across kinds must not make the kinds interchangeable.
        Assert.True(scene.TryRefresh(new SceneSnapshot(1,
            [new ActorDescriptor(actor, "Actor without a ready skeleton", [])],
            [new LightDescriptor(light, "Light", LightKind.Point)], [],
            [new PropDescriptor(prop, "Prop")],
            Overlays: [new OverlayDescriptor(collider, "Collider", OverlayNodeKind.Collider)],
            WorldObjects: [new WorldObjectDescriptor(world, "World", "test/path")])).Accepted);
        var pairs = new (TransformTargetId Old, TransformTargetId Current)[]
        {
            (TransformTargetId.ForActor(new(id, 0)), TransformTargetId.ForActor(actor)),
            (TransformTargetId.ForLight(new(id, 0)), TransformTargetId.ForLight(light)),
            (TransformTargetId.ForProp(new(id, 0)), TransformTargetId.ForProp(prop)),
            (TransformTargetId.ForWorldObject(new(id, 0)), TransformTargetId.ForWorldObject(world)),
            (TransformTargetId.ForCollider(new(id, 0)), TransformTargetId.ForCollider(collider)),
        };
        foreach (var pair in pairs)
        {
            Assert.Equal(pair.Current, scene.CurrentTransformTarget(pair.Old));
            Assert.Equal(pair.Current, scene.CurrentTransformTarget(pair.Current));
            Assert.False(scene.Contains(pair.Old));
        }
        Assert.True(scene.TryRefresh(new SceneSnapshot(2, [], [], [], [])).Accepted);
        foreach (var pair in pairs) Assert.Null(scene.CurrentTransformTarget(pair.Old));
        Assert.Null(scene.CurrentTransformTarget(default));
    }

    [Fact]
    public void Missing_bone_drops_only_its_patch_and_never_falls_back_to_actor()
    {
        var actor = ActorId.New();
        var bone = new BoneId(new SkeletonId(actor, PoseSlot.Character, 0), 0, 0, "n_root");
        var scene = new SceneSession(new SelectionSession());
        Assert.True(scene.TryRefresh(new SceneSnapshot(1,
            [new ActorDescriptor(actor.NextGeneration(), "Actor", [])], [], [], [])).Accepted);
        var history = new EditHistory();
        var unrelated = new SceneLifecyclePatch("Spawn light", () => true, () => true);
        history.Append(unrelated);
        var state = new TransformTargetState(TransformTargetId.ForBone(bone), PoseTransform.Identity, new BonePose(), true);
        history.Append(new TransformPatch("Bone edit", [state], [state]));

        history.Reconcile(scene.Contains, scene.CurrentTransformTarget);

        Assert.Same(unrelated, history.PeekUndo());
        Assert.Null(scene.CurrentTransformTarget(state.Target));
    }

    [Fact]
    public void A_non_collider_overlay_is_not_a_transform_replacement()
    {
        var id = OverlayId.New();
        var scene = new SceneSession(new SelectionSession());
        Assert.True(scene.TryRefresh(new SceneSnapshot(1, [], [], [], [],
            Overlays: [new OverlayDescriptor(id.NextGeneration(), "UI", OverlayNodeKind.Talk)])).Accepted);
        Assert.Null(scene.CurrentTransformTarget(TransformTargetId.ForCollider(id)));
    }

    private static SceneSession Scene(BoneId bone)
    {
        var scene = new SceneSession(new SelectionSession());
        Assert.True(scene.TryRefresh(new SceneSnapshot(1,
            [new ActorDescriptor(bone.Skeleton.Actor, "Actor", [new SkeletonDescriptor(bone.Skeleton,
                [new BoneDescriptor(bone, "Root", null)])])], [], [], [])).Accepted);
        return scene;
    }
}
