using System.Numerics;
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
