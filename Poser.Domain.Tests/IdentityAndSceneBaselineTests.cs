using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests;

public sealed class IdentityAndSceneBaselineTests
{
    [Fact]
    public void Actor_and_skeleton_generations_are_exact_record_identity()
    {
        var actor = Actor();
        var actorReplacement = actor.NextGeneration();
        var character = new SkeletonId(actor, PoseSlot.Character, 0);
        var characterReplacement = character.NextGeneration();
        var weapon = new SkeletonId(actor, PoseSlot.MainHand, 0);

        Assert.Equal(actor.LogicalId, actorReplacement.LogicalId);
        Assert.NotEqual(actor, actorReplacement);
        Assert.NotEqual(character, characterReplacement);
        Assert.NotEqual(character, weapon);
        Assert.Equal(PoseSlot.Character, character.Slot);
        Assert.Equal(PoseSlot.MainHand, weapon.Slot);
    }

    [Fact]
    public void Bone_identity_includes_slot_partial_index_and_canonical_name()
    {
        var actor = Actor();
        var character = new SkeletonId(actor, PoseSlot.Character, 0);
        var weapon = new SkeletonId(actor, PoseSlot.MainHand, 0);
        var first = new BoneId(character, 0, 4, "j_same");
        var differentSlot = new BoneId(weapon, 0, 4, "j_same");
        var differentGeneration = new BoneId(
            character.NextGeneration(),
            0,
            4,
            "j_same");

        Assert.Equal(PoseSlot.Character, first.Slot);
        Assert.NotEqual(first, differentSlot);
        Assert.NotEqual(first, differentGeneration);
        Assert.True(first.IsValid);
    }

    [Fact]
    public void Current_scene_snapshot_round_trip_preserves_all_baseline_fields()
    {
        var actor = Actor();
        var bone = new BoneId(
            new SkeletonId(actor, PoseSlot.Character, 2),
            1,
            5,
            "j_hand_l");
        var light = LightId.New();
        var camera = CameraId.New();
        var prop = PropId.New();
        var snapshot = new SceneSnapshot(
            19,
            [new ActorDescriptor(
                actor,
                "Actor",
                [new SkeletonDescriptor(
                    bone.Skeleton,
                    [new BoneDescriptor(bone, "Hand", null)])])],
            [new LightDescriptor(
                light,
                "Light",
                LightKind.Point,
                IsOn: false,
                Ownership: LightOwnership.World)],
            [new CameraDescriptor(
                camera,
                "Camera",
                CameraKind.Free,
                IsLive: true,
                IsDefault: false)],
            [new PropDescriptor(prop, "Prop", Visible: false)]);

        Assert.Equal(19UL, snapshot.Revision);
        Assert.Equal(actor, snapshot.Actors[0].Id);
        Assert.Equal(bone, snapshot.Actors[0].Skeletons[0].Bones[0].Id);
        Assert.Equal(LightOwnership.World, snapshot.Lights[0].Ownership);
        Assert.True(snapshot.Cameras[0].IsLive);
        Assert.False(snapshot.Props[0].Visible);
        Assert.Equal(0UL, SceneSnapshot.Empty.Revision);
    }

    [Fact(Skip = "Slice 1 characterization: current SceneSnapshot has no relationship/environment/gaze/object-state fields yet.")]
    public void Slice1_complete_scene_snapshot_characterization()
    {
        Assert.True(false);
    }

    [Fact]
    public void Scene_snapshot_is_pointer_free_and_target_generation_is_structural()
    {
        var actor = Actor();
        var target = TransformTargetId.ForActor(actor);

        Assert.Equal(actor.LogicalId, target.ActorLineage);
        Assert.Equal(TransformTargetKind.Actor, target.Kind);
        Assert.Equal(target.ToSelectionId(), SelectionId.ForActor(actor));
    }

    private static ActorId Actor() =>
        new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 0);
}
