using System.Text.Json;
using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Files;
using Poser.Tests.Files;

namespace Poser.Application.Tests.Scene;

public sealed class SceneSaveNarrowingTests
{
    [Fact]
    public void Light_camera_and_actor_entries_detach_the_actor_they_left_out()
    {
        var scene = SceneFileStoreTests.ValidScene();
        var lead = scene.Actors[0];
        var follower = new SceneActor
        {
            Key = Guid.NewGuid(),
            Name = "Follower",
            Pose = PoseFilePersistenceTests.ValidPose(),
            Gaze = new SceneActorGaze { Mode = GazeTargetMode.Entity, TargetActorKey = lead.Key },
        };
        scene.Actors.Add(follower);
        var followCamera = new SceneCamera
        {
            Key = Guid.NewGuid(),
            Camera = new CameraFile { Name = "Follow cam", Kind = CameraKind.Free },
            TargetActorKey = lead.Key,
            TargetActorName = "Lead",
            IsTargetLocked = true,
        };
        scene.Cameras.Add(followCamera);

        foreach (var (category, key) in new[]
                 {
                     (SceneCategories.Lights, scene.Lights[0].Key),
                     (SceneCategories.Cameras, followCamera.Key),
                     (SceneCategories.Actors, follower.Key),
                 })
        {
            var entry = JsonSerializer.Deserialize<SceneFile>(
                JsonSerializer.Serialize(scene, SceneJsonOptionsAccessor.Options),
                SceneJsonOptionsAccessor.Options)!;
            var options = SceneSaveOptions.Only(category, new[] { key });
            var notes = new List<string>();
            IReadOnlyDictionary<Guid, ActorId> identities = new Dictionary<Guid, ActorId>();

            Assert.Null(SceneSaveNarrowing.Narrow(entry, options.OnlyEntityKeys, ref identities));
            SceneSavePolicy.Apply(entry, options, notes);
            SceneSaveNarrowing.DetachDangling(entry, notes);

            Assert.Single(notes);
            var validation = SceneFileValidation.Validate(entry);
            Assert.True(validation.Succeeded, validation.Failure?.Detail);
        }
    }

    [Fact]
    public void Overlay_entry_carries_only_its_overlay_and_takes_the_entry_name()
    {
        var overlay = new SceneOverlay { Key = Guid.NewGuid(), Node = new OverlayNodeState { Name = "Old" } };
        var scene = new SceneFile
        {
            SceneId = Guid.NewGuid(),
            Overlays = new() { overlay, new SceneOverlay { Key = Guid.NewGuid(), Node = new OverlayNodeState() } },
            WorldObjects = new() { new SceneWorldObject { Key = Guid.NewGuid(), Path = "bg/a.mdl" } },
        };
        var options = SceneSaveOptions.Only(SceneCategories.Overlays, new[] { overlay.Key })
            with { EntryName = "Speech" };
        IReadOnlyDictionary<Guid, ActorId> identities = new Dictionary<Guid, ActorId>();

        Assert.Null(SceneSaveNarrowing.Narrow(scene, options.OnlyEntityKeys, ref identities));
        SceneSaveNarrowing.ApplyEntryName(scene, options.EntryName);
        SceneSavePolicy.Apply(scene, options, new List<string>());

        Assert.Null(scene.WorldObjects);
        Assert.Equal("Speech", Assert.Single(scene.Overlays!).Node!.Name);
    }
}
