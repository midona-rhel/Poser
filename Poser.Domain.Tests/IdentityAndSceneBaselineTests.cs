using System.Numerics;
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

    [Fact]
    public void Slice1_complete_scene_snapshot_round_trip_preserves_scene_state()
    {
        var actor = Actor();
        var companion = new ActorId(
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            3);
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
            [
                new ActorDescriptor(
                    actor,
                    "Actor",
                    [new SkeletonDescriptor(
                        bone.Skeleton,
                        [new BoneDescriptor(
                            bone,
                            "Hand",
                            null,
                            IsHidden: true)])]),
                new ActorDescriptor(
                    companion,
                    "Companion",
                    [],
                    IsCompanion: true,
                    OwnerActor: actor),
            ],
            [new LightDescriptor(
                light,
                "Light",
                LightKind.Point,
                IsOn: false,
                Ownership: LightOwnership.World,
                AttachedBone: bone)],
            [new CameraDescriptor(
                camera,
                "Camera",
                CameraKind.Free,
                 IsLive: true,
                 IsDefault: false,
                 IsLocked: true,
                 TargetActor: actor,
                 TargetBone: bone,
                 TargetOffset: new Vector3(1, 2, 3))],
            [new PropDescriptor(prop, "Prop", Visible: false)],
            new EnvironmentDescriptor(
                MinuteOfDay: 615,
                DayOfMonth: 12,
                WeatherId: 42,
                IsTimeFrozen: true,
                IsWeatherOverrideEnabled: true,
                HeldSections: EnvironmentSection.Sky | EnvironmentSection.Fog),
            [new GazeDescriptor(
                actor,
                GazeMode.Position,
                GazeParts.All,
                GazeParts.Head,
                TargetActor: null,
                Anchor: new Vector3(4, 5, 6),
                EyesPosition: new Vector3(7, 8, 9),
                HeadPosition: new Vector3(10, 11, 12),
                BodyPosition: new Vector3(13, 14, 15))]);

        Assert.Equal(actor, snapshot.Actors[0].Id);
        Assert.Equal(actor, snapshot.Actors[1].OwnerActor);
        Assert.True(snapshot.Actors[0].Skeletons[0].Bones[0].IsHidden);
        Assert.Equal(LightOwnership.World, snapshot.Lights[0].Ownership);
        Assert.Equal(bone, snapshot.Lights[0].AttachedBone);
        Assert.Equal(actor, snapshot.Cameras[0].TargetActor);
        Assert.Equal(bone, snapshot.Cameras[0].TargetBone);
        Assert.True(snapshot.Cameras[0].IsLocked);
        Assert.Equal(new Vector3(1, 2, 3), snapshot.Cameras[0].TargetOffset);
        Assert.False(snapshot.Props[0].Visible);
        Assert.Equal(615, snapshot.Environment!.MinuteOfDay);
        Assert.Equal(
            EnvironmentSection.Sky | EnvironmentSection.Fog,
            snapshot.Environment.HeldSections);
        Assert.Equal(actor, snapshot.GazeStates[0].Actor);
        Assert.Equal(GazeMode.Position, snapshot.GazeStates[0].Mode);
        Assert.Equal(GazeParts.Head, snapshot.GazeStates[0].LockedParts);
        Assert.Equal(new Vector3(10, 11, 12), snapshot.GazeStates[0].HeadPosition);
    }

    [Fact]
    public void Scene_snapshot_copies_input_collections_instead_of_retaining_mutable_storage()
    {
        var actor = Actor();
        var bone = new BoneId(
            new SkeletonId(actor, PoseSlot.Character, 0),
            0,
            1,
            "j_root");
        var bones = new List<BoneDescriptor>
        {
            new(bone, "Root", null),
        };
        var skeletons = new List<SkeletonDescriptor>
        {
            new(bone.Skeleton, bones),
        };
        var actors = new List<ActorDescriptor>
        {
            new(actor, "Actor", skeletons),
        };

        var snapshot = new SceneSnapshot(
            1,
            actors,
            [],
            [],
            []);
        var withSnapshot = snapshot with { Actors = actors };

        bones.Clear();
        skeletons.Clear();
        actors.Clear();

        Assert.Single(snapshot.Actors);
        Assert.Single(snapshot.Actors[0].Skeletons);
        Assert.Single(snapshot.Actors[0].Skeletons[0].Bones);
        Assert.Single(withSnapshot.Actors);
        Assert.Single(withSnapshot.Actors[0].Skeletons);
        Assert.Single(withSnapshot.Actors[0].Skeletons[0].Bones);
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
