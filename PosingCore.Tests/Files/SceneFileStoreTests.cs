using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Files;
using Poser.Services;

namespace Poser.Tests.Files;

public sealed class SceneFileStoreTests
{
    [Fact]
    public void A_complete_scene_round_trips_with_its_entity_relationships()
    {
        using var fixture = new SceneFixture();
        var original = ValidScene();

        var write = SceneFileStore.Default.Write(original, fixture.Path);
        var read = SceneFileStore.Default.Read(fixture.Path);

        Assert.True(write.Succeeded, write.Failure?.Detail);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Equal(original.SceneId, read.Scene!.SceneId);
        Assert.Equal(1, read.Scene.Actors.Count);
        Assert.Equal(1, read.Scene.Props.Count);
        Assert.Equal(1, read.Scene.Lights.Count);
        Assert.Equal(1, read.Scene.Cameras.Count);
        Assert.Equal(original.Lights[0].Attachment!.ActorKey, read.Scene.Lights[0].Attachment!.ActorKey);
        Assert.Equal(original.Cameras[0].TargetActorKey, read.Scene.Cameras[0].TargetActorKey);
        Assert.Equal(720, read.Scene.Environment!.MinuteOfDay);
    }

    [Fact]
    public void Optional_members_are_omitted_and_unknown_members_are_ignored()
    {
        using var fixture = new SceneFixture();
        var scene = ValidScene();
        scene.World = null;
        scene.Overlays = null;
        scene.WorldObjects = null;

        var json = JsonSerializer.Serialize(scene, SceneJsonOptionsAccessor.Options);
        Assert.DoesNotContain("World\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Overlays", json, StringComparison.Ordinal);
        Assert.DoesNotContain("WorldObjects", json, StringComparison.Ordinal);

        var withUnknown = json.TrimEnd();
        withUnknown = withUnknown[..^1] + ",\"FutureMember\":true}";
        File.WriteAllText(fixture.Path, withUnknown);
        var read = SceneFileStore.Default.Read(fixture.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Null(read.Scene!.World);
        Assert.Null(read.Scene.Overlays);
        Assert.Null(read.Scene.WorldObjects);
    }

    [Fact]
    public void Corrupt_and_future_scene_data_have_typed_rejections()
    {
        Assert.Equal(SceneStoreFailureKind.Json, SceneFileStore.Default.Parse("{ nope").Failure!.Kind);

        var json = JsonSerializer.Serialize(ValidScene(), SceneJsonOptionsAccessor.Options);
        json = json.Replace("\"FileVersion\": 1", "\"FileVersion\": 2", StringComparison.Ordinal);
        var future = SceneFileStore.Default.Parse(json);

        Assert.False(future.Succeeded);
        Assert.Equal(SceneStoreFailureKind.FutureVersion, future.Failure!.Kind);
    }

    [Fact]
    public void A_scene_write_failure_preserves_an_existing_destination()
    {
        using var fixture = new SceneFixture();
        File.WriteAllText(fixture.Path, "old scene");
        var store = new SceneFileStore(new FailingSceneFileSystem());

        var result = store.Write(ValidScene(), fixture.Path);

        Assert.False(result.Succeeded);
        Assert.Equal(SceneStoreFailureKind.TemporaryCreate, result.Failure!.Kind);
        Assert.Equal("old scene", File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void Validation_rejects_broken_identity_relationships_without_touching_disk()
    {
        using var fixture = new SceneFixture();
        File.WriteAllText(fixture.Path, "old scene");
        var scene = ValidScene();
        scene.SceneId = Guid.Empty;
        scene.Lights[0].Attachment!.ActorKey = Guid.NewGuid();

        var validation = SceneFileValidation.Validate(scene);
        var write = SceneFileStore.Default.Write(scene, fixture.Path);

        Assert.False(validation.Succeeded);
        Assert.Equal(SceneFileValidationFailureKind.Identity,
            validation.Failure!.Kind);
        Assert.False(write.Succeeded);
        Assert.Equal("old scene", File.ReadAllText(fixture.Path));
    }

    internal static SceneFile ValidScene()
    {
        var actorKey = Guid.NewGuid();
        var pose = PoseFilePersistenceTests.ValidPose();
        return new SceneFile
        {
            SceneId = Guid.NewGuid(),
            Description = "Test scene",
            SavedAt = DateTimeOffset.UtcNow,
            Actors =
            {
                new SceneActor
                {
                    Key = actorKey,
                    Name = "Lead",
                    HasCompanionSlot = true,
                    CompanionKind = CompanionKind.Companion,
                    CompanionId = 12,
                    Pose = pose,
                },
            },
            Props =
            {
                new SceneProp
                {
                    Key = Guid.NewGuid(),
                    Name = "Chair",
                    Model = 89,
                    Submodel = 1,
                    Variant = 2,
                    Transform = new LightFile.TransformData
                    {
                        Position = new Vector3(1, 2, 3),
                        Rotation = Quaternion.Identity,
                        Scale = Vector3.One,
                    },
                },
            },
            Lights =
            {
                new SceneLight
                {
                    Key = Guid.NewGuid(),
                    Light = new LightFile
                    {
                        Name = "Key light",
                        Kind = LightKind.Spot,
                        Transform = new LightFile.TransformData
                        {
                            Position = new Vector3(0, 2, 0),
                            Rotation = Quaternion.Identity,
                            Scale = Vector3.One,
                        },
                        Color = new Vector3(1, 0.9f, 0.8f),
                        Intensity = 1.5f,
                        Range = 10f,
                        SpotAngle = 45f,
                    },
                    Attachment = new SceneBoneAttachment
                    {
                        ActorKey = actorKey,
                        Slot = PoseSlot.Character,
                        PartialId = 0,
                        BoneName = "j_te_l",
                    },
                },
            },
            Cameras =
            {
                new SceneCamera
                {
                    Key = Guid.NewGuid(),
                    IsLive = true,
                    IsDefault = true,
                    Camera = new CameraFile { Name = "GPose Camera", Kind = CameraKind.Game, Zoom = 2.5f },
                    TargetActorKey = actorKey,
                    TargetActorName = "Lead",
                    TargetOffset = new Vector3(0, 0.5f, 0),
                },
            },
            Environment = new SceneEnvironment
            {
                MinuteOfDay = 720,
                DayOfMonth = 12,
                IsTimeFrozen = true,
                WeatherId = 2,
                IsWeatherOverrideEnabled = true,
                HeldSections = { EnvSection.Fog },
                Fog = new EnvFogValues(
                    new Vector4(0.5f, 0.5f, 0.5f, 1f), 100f, 0.4f, 1f, 1f, 0.7f, 1f),
            },
        };
    }
}

internal static class SceneJsonOptionsAccessor
{
    public static System.Text.Json.JsonSerializerOptions Options => SceneFile.JsonOptions;
}

internal sealed class SceneFixture : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "poser-scene-store-tests", Guid.NewGuid().ToString("N"));
    public string Path => System.IO.Path.Combine(Root, "scene.poserscene");

    public SceneFixture() => Directory.CreateDirectory(Root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
        }
    }
}

internal sealed class FailingSceneFileSystem : IPoseFileStoreFileSystem
{
    public Stream OpenRead(string path) => File.OpenRead(path);
    public Stream CreateNew(string path) => throw new IOException("injected scene write failure");
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);
    public bool Exists(string path) => File.Exists(path);
    public void Replace(string source, string destination, string backup) => File.Replace(source, destination, backup);
    public void Move(string source, string destination) => File.Move(source, destination);
    public void Delete(string path) => File.Delete(path);
}
