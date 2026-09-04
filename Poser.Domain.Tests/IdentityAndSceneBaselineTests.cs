using System.Numerics;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;

namespace Poser.Domain.Tests;

public sealed class IdentityAndSceneBaselineTests
{
    [Fact]
    public void Identity_is_generation_and_slot_safe_but_name_sensitive()
    {
        var actor = Actor();
        var character = new SkeletonId(actor, PoseSlot.Character, 0);
        var bone = new BoneId(character, 0, 4, "j_same");
        var renamed = new BoneId(character, 0, 4, "j_renamed");

        Assert.Equal(actor.LogicalId, actor.NextGeneration().LogicalId);
        Assert.NotEqual(character, character.NextGeneration());
        Assert.NotEqual(character, new SkeletonId(actor, PoseSlot.MainHand, 0));
        Assert.True(bone.IsValid);
        Assert.NotEqual(bone, renamed);
        Assert.Equal(bone.GetHashCode(), new BoneId(character, 0, 4, "j_same").GetHashCode());
        Assert.Equal(2, new Dictionary<BoneId, string> { [bone] = "a", [renamed] = "b" }.Count);
        Assert.False(new BoneId(new SkeletonId(actor, PoseSlot.Unknown, 0), 0, 0, "j").IsValid);
        Assert.False(new BoneId(character, -1, 0, "j").IsValid);
        Assert.False(new BoneId(character, 0, 0, " ").IsValid);
    }

    [Fact]
    public void Scene_snapshot_is_structural_pointer_free_and_preserves_nested_state()
    {
        var actor = Actor();
        var companion = new ActorId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"), 3);
        var bone = new BoneId(new SkeletonId(actor, PoseSlot.Character, 2), 1, 5, "j_hand_l");
        var light = LightId.New();
        var camera = CameraId.New();
        var prop = PropId.New();
        var snapshot = new SceneSnapshot(
            19,
            [new ActorDescriptor(actor, "Actor", [new SkeletonDescriptor(bone.Skeleton, [new BoneDescriptor(bone, "Hand", null, IsHidden: true)])]), new ActorDescriptor(companion, "Companion", [], IsCompanion: true, OwnerActor: actor, AttachmentKind: CompanionKind.Companion)],
            [new LightDescriptor(light, "Light", LightKind.Point, false, LightOwnership.World, bone)],
            [new CameraDescriptor(camera, "Camera", CameraKind.Free, true, false, true, actor, bone, new Vector3(1, 2, 3))],
            [new PropDescriptor(prop, "Prop", false)],
            new EnvironmentDescriptor(615, 12, 42, true, true, EnvironmentSection.Sky | EnvironmentSection.Fog),
            [new GazeDescriptor(actor, GazeMode.Position, GazeParts.All, GazeParts.Head, null, new Vector3(4, 5, 6), new Vector3(7, 8, 9), new Vector3(10, 11, 12), new Vector3(13, 14, 15))]);

        Assert.Equal(actor, snapshot.Actors[0].Id);
        Assert.Equal(actor, snapshot.Actors[1].OwnerActor);
        Assert.True(snapshot.Actors[0].Skeletons[0].Bones[0].IsHidden);
        Assert.Equal(bone, snapshot.Lights[0].AttachedBone);
        Assert.Equal(actor, snapshot.Cameras[0].TargetActor);
        Assert.Equal(new Vector3(1, 2, 3), snapshot.Cameras[0].TargetOffset);
        Assert.Equal(615, snapshot.Environment!.MinuteOfDay);
        Assert.Equal(GazeParts.Head, snapshot.GazeStates[0].LockedParts);

        var actors = new List<ActorDescriptor>(snapshot.Actors);
        var copy = new SceneSnapshot(19, actors, snapshot.Lights, snapshot.Cameras, snapshot.Props, snapshot.Environment, snapshot.GazeStates);
        actors.Clear();
        Assert.True(snapshot.ContentEquals(copy));
        Assert.False(snapshot.ContentEquals(copy with
        {
            Actors = copy.Actors.Select(item => item.OwnerActor is null
                ? item
                : item with { AttachmentKind = CompanionKind.Mount }).ToArray(),
        }));
        Assert.False(snapshot.ContentEquals(copy with { Revision = 20 }));
        Assert.False(snapshot.ContentEquals(copy with { Environment = new EnvironmentDescriptor(616, 12, 42) }));
        Assert.Equal(actor.LogicalId, TransformTargetId.ForActor(actor).ActorLineage);
        Assert.Equal(SelectionId.ForActor(actor), TransformTargetId.ForActor(actor).ToSelectionId());
    }

    private static ActorId Actor() => new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), 0);
}
