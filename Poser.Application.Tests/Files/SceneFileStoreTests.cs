using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Application.Diagnostics;
using Poser.Files;
using Poser.Services;

namespace Poser.Tests.Files;

public sealed class SceneFileStoreTests
{
    [Fact]
    public void Collider_group_saves_and_restores_members_transforms_and_flags()
    {
        using var fixture = new SceneFixture();
        var scene = ValidScene();
        scene.Actors.Clear();
        scene.Props.Clear();
        scene.Lights.Clear();
        scene.Cameras.Clear();
        scene.Overlays = [];
        var group = new SceneGroupEntry { Key = Guid.NewGuid(), Name = "Collision test", Transform = new() };
        foreach (var shape in new[] { Poser.Domain.Posing.IkColliderShape.Box, Poser.Domain.Posing.IkColliderShape.Cone })
        {
            var key = Guid.NewGuid();
            var pose = new Poser.Domain.Transforms.PoseTransform(new(2 + (int)shape, 3, 4),
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, .7f), new(2, 1, 3));
            scene.Overlays.Add(new() { Key = key, Node = new()
            {
                Kind = Poser.Domain.Presentation.OverlayNodeKind.Collider, Name = shape.ToString(),
                Collider = new() { Shape = shape, Transform = pose, Enabled = false, Locked = true },
                Visible = false, Alpha = .3f,
            } });
            var reference = new SceneStructureRef { Kind = "overlay", Key = key };
            group.Members.Add(reference);
            group.Transform.Members.Add(new() { Member = reference, Initial = pose, Expected = pose });
        }
        scene.Groups = [group];
        scene.Parents = [new() { Child = group.Members[1], Target = group.Members[0],
            Offset = new(new(1, 2, 3), Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f), new(2, 3, 4)) }];
        var write = SceneFileStore.Default.Write(scene, fixture.Path);
        Assert.True(write.Succeeded, write.Failure?.Detail);
        var read = SceneFileStore.Default.Read(fixture.Path);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        var restored = Assert.Single(read.Scene!.Groups!);
        Assert.Equal(group.Name, restored.Name);
        var savedParent = Assert.Single(read.Scene.Parents!);
        Assert.Equal(scene.Parents[0].Target.Key, savedParent.Target.Key);
        Assert.Equal(scene.Parents[0].Offset, savedParent.Offset);
        for (int i = 0; i < 2; i++)
        {
            Assert.Equal(scene.Overlays[i].Node, read.Scene.Overlays![i].Node);
            Assert.Equal(read.Scene.Overlays[i].Key, restored.Members[i].Key);
            Assert.Equal(group.Transform.Members[i].Expected, restored.Transform!.Members[i].Expected);
        }
        var config = Poser.Domain.Posing.IkChainConfig.DefaultsForChain() with { Collisions = true, CollisionRadius = .07f };
        var json = JsonSerializer.Serialize(config, SceneJsonOptionsAccessor.Options);
        Assert.Equal(config, JsonSerializer.Deserialize<Poser.Domain.Posing.IkChainConfig>(json, SceneJsonOptionsAccessor.Options));
    }

    [Fact]
    public void Placement_rebases_companions_and_colliders_but_not_screen_overlays()
    {
        var scene = ValidScene();
        scene.Origin = Vector3.Zero;
        var owner = scene.Actors[0];
        owner.ModelTransform = new() { Position = new(2, 3, 4), Rotation = Quaternion.Identity, Scale = Vector3.One };
        var savedRotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, .4f);
        owner.CompanionPose = new PoseFile
        {
            ModelAbsoluteValues = new() { Position = new(3, 2, 5), Rotation = savedRotation, Scale = new(.77f) },
            Bones = new() { ["n_root"] = new() { Position = new(1, 2, 3), Rotation = Quaternion.Identity, Scale = Vector3.One } },
        };
        scene.Overlays = [new SceneOverlay { Node = new() { Kind = Poser.Domain.Presentation.OverlayNodeKind.Collider,
            Collider = new() { Transform = Poser.Domain.Transforms.PoseTransform.Identity with { Position = Vector3.UnitX } } } },
            new SceneOverlay { Node = new() { Position = new Vector2(20, 30) } }];
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, .9f);

        Assert.Null(Poser.Scene.ScenePlacementRebase.Rebase(
            scene, new() { Position = Vector3.Zero, Yaw = 0 }, new Vector3(10, 20, 30), .9f));

        var restored = owner.CompanionPose.ModelAbsoluteValues;
        Assert.True(Vector3.Distance(restored.Position - owner.ModelTransform.Position,
            Vector3.Transform(new Vector3(1, -1, 1), turn)) < .00001f);
        Assert.True(MathF.Abs(Quaternion.Dot(restored.Rotation, Quaternion.Normalize(turn * savedRotation))) > .99999f);
        Assert.Equal(new Vector3(.77f), restored.Scale);
        Assert.Equal(new Vector3(1, 2, 3), owner.CompanionPose.Bones["n_root"].Position);
        Assert.NotEqual(Vector3.UnitX, scene.Overlays[0].Node!.Collider!.Transform.Position);
        Assert.Equal(new Vector2(20, 30), scene.Overlays[1].Node!.Position);
    }

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
        Assert.Equal(original.Cameras[0].IsTargetLocked,
            read.Scene.Cameras[0].IsTargetLocked);
        Assert.Equal(720, read.Scene.Environment!.MinuteOfDay);
    }

    [Fact]
    public void Optional_members_are_omitted_and_unknown_members_are_ignored()
    {
        using var fixture = new SceneFixture();
        var scene = ValidScene();
        scene.World = null;
        scene.WorldObjects = null;

        var json = JsonSerializer.Serialize(scene, SceneJsonOptionsAccessor.Options);
        Assert.DoesNotContain("World\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("WorldObjects", json, StringComparison.Ordinal);

        var withUnknown = json.TrimEnd();
        withUnknown = withUnknown[..^1] + ",\"FutureMember\":true}";
        WriteContainer(fixture.Path, withUnknown);
        var read = SceneFileStore.Default.Read(fixture.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Null(read.Scene!.World);
        Assert.Null(read.Scene.WorldObjects);
    }

    /// <summary>Writes a raw document into a scene CONTAINER, which is what a
    /// .xivs is: the payload entries are what let a scene carry hundreds of
    /// megabytes of appearance without the document growing at all.</summary>
    internal static void WriteContainer(string path, string documentJson)
    {
        using var stream = File.Create(path);
        using var archive = new System.IO.Compression.ZipArchive(
            stream, System.IO.Compression.ZipArchiveMode.Create);
        var entry = archive.CreateEntry(SceneFileStore.DocumentEntry);
        using var writing = new StreamWriter(entry.Open());
        writing.Write(documentJson);
    }

    [Fact]
    public void Corrupt_and_future_scene_data_have_typed_rejections()
    {
        Assert.Equal(SceneStoreFailureKind.Json, SceneFileStore.Default.Parse("{ nope").Failure!.Kind);

        var json = JsonSerializer.Serialize(ValidScene(), SceneJsonOptionsAccessor.Options);
        json = json.Replace(
            $"\"FileVersion\": {SceneFile.CurrentVersion}",
            $"\"FileVersion\": {SceneFile.CurrentVersion + 1}",
            StringComparison.Ordinal);
        var future = SceneFileStore.Default.Parse(json);

        Assert.False(future.Succeeded);
        Assert.Equal(SceneStoreFailureKind.FutureVersion, future.Failure!.Kind);

        // A bare JSON document is not a scene container.
        using var fixture = new SceneFixture();
        File.WriteAllText(fixture.Path, JsonSerializer.Serialize(ValidScene(), SceneJsonOptionsAccessor.Options));
        var bare = SceneFileStore.Default.Read(fixture.Path);
        Assert.False(bare.Succeeded);
        Assert.Contains("container", bare.Failure!.Detail);
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
                    IsTargetLocked = true,
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
    public string Path => System.IO.Path.Combine(Root, "scene" + SceneFile.Extension);

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

internal sealed class FailingSceneFileSystem : IAtomicFileSystem
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
