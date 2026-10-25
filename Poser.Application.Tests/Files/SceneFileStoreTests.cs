using System;
using System.IO;
using System.Numerics;
using System.Text.Json;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Application.Diagnostics;
using Poser.Documents.Files;

namespace Poser.Application.Tests.Files;

public sealed class SceneFileStoreTests
{
    [Fact]
    public void A_complete_scene_round_trips_with_its_entity_relationships()
    {
        using var fixture = new SceneFixture();
        var original = ValidScene();
        original.Actors[0].SpawnedKind = CompanionKind.Mount;
        original.Actors[0].AppearanceNotSaved = true;

        var write = SceneFileStore.Default.Write(original, fixture.Path);
        var read = SceneFileStore.Default.Read(fixture.Path);

        Assert.True(write.Succeeded, write.Failure?.Detail);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Equal(original.SceneId, read.Scene!.SceneId);
        Assert.Equal(1, read.Scene.Actors.Count);
        Assert.Equal(CompanionKind.Mount, read.Scene.Actors[0].SpawnedKind);
        Assert.True(read.Scene.Actors[0].AppearanceNotSaved);
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
    public void A_scene_saved_with_its_animation_round_trips_layers_and_bone_edits()
    {
        using var fixture = new SceneFixture();
        var original = ValidScene();
        var actor = original.Actors[0];
        actor.Animation = new SceneAnimationState
        {
            Base = new SceneAnimationLayer { Timeline = 6310, Playing = true, Speed = 0f, Weight = 1f, HeldSeconds = 4.03f },
            UpperBody = new SceneAnimationLayer { Timeline = 8008, Playing = true, Loop = true, Speed = 0.5f, Weight = 0.6f },
            HeldExpression = 605,
            Stance = Poser.Domain.Animation.AnimationStance.SitGround,
            Advanced = true,
        };
        actor.PoseEdits = new()
        {
            new SceneBoneEdit
            {
                Slot = PoseSlot.Character, Partial = 0, Name = "j_kubi",
                Offsets = { new SceneBoneOffset
                {
                    Propagate = Poser.Domain.Posing.TransformComponents.Rotation,
                    Rotation = Quaternion.CreateFromYawPitchRoll(0.3f, 0f, 0f),
                    Frame = Poser.Domain.Posing.TransformFrame.BoneLocal,
                } },
            },
        };

        Assert.True(SceneFileStore.Default.Write(original, fixture.Path).Succeeded);
        var read = SceneFileStore.Default.Read(fixture.Path);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        var back = read.Scene!.Actors[0];
        Assert.Equal(6310, back.Animation!.Base!.Timeline);
        Assert.Equal(4.03f, back.Animation.Base.HeldSeconds);
        Assert.Equal(0.5f, back.Animation.UpperBody!.Speed);
        Assert.True(back.Animation.UpperBody.Loop);
        Assert.Equal((ushort)605, back.Animation.HeldExpression);
        Assert.Equal(Poser.Domain.Animation.AnimationStance.SitGround, back.Animation.Stance);
        var edit = Assert.Single(back.PoseEdits!);
        Assert.Equal("j_kubi", edit.Name);
        Assert.Equal(original.Actors[0].PoseEdits![0].Offsets[0].Rotation, edit.Offsets[0].Rotation);
        Assert.Equal(SceneFile.CurrentVersion, read.Scene.FileVersion);
    }

    [Fact]
    public void A_saved_animation_out_of_range_is_refused_before_anything_changes()
    {
        var scene = ValidScene();
        scene.Actors[0].Animation = new SceneAnimationState
        {
            UpperBody = new SceneAnimationLayer { Timeline = 8008, Weight = 2f },
        };
        Assert.False(SceneFileValidation.Validate(scene).Succeeded);
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

    /// <summary>Writes a raw document into a scene container. A <c>.xivs</c>
    /// file stores payloads as separate entries, allowing a scene to carry
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
        using var fixture = new SceneFixture();
        WriteContainer(fixture.Path, "{ nope");
        Assert.Equal(SceneStoreFailureKind.Json, SceneFileStore.Default.Read(fixture.Path).Failure!.Kind);

        var json = JsonSerializer.Serialize(ValidScene(), SceneJsonOptionsAccessor.Options);
        json = json.Replace(
            $"\"FileVersion\": {SceneFile.CurrentVersion}",
            $"\"FileVersion\": {SceneFile.CurrentVersion + 1}",
            StringComparison.Ordinal);
        WriteContainer(fixture.Path, json);
        var future = SceneFileStore.Default.Read(fixture.Path);

        Assert.False(future.Succeeded);
        Assert.Equal(SceneStoreFailureKind.FutureVersion, future.Failure!.Kind);

        // A bare JSON document is not a scene container.
        File.WriteAllText(fixture.Path, JsonSerializer.Serialize(ValidScene(), SceneJsonOptionsAccessor.Options));
        var bare = SceneFileStore.Default.Read(fixture.Path);
        Assert.False(bare.Succeeded);
        Assert.Contains("container", bare.Failure!.Detail);
    }

    [Fact]
    public void Older_bom_prefixed_scenes_read_and_resave_at_the_current_version()
    {
        using var fixture = new SceneFixture();
        var document = System.Text.Json.Nodes.JsonNode.Parse(
            JsonSerializer.Serialize(ValidScene(), SceneJsonOptionsAccessor.Options))!;
        document["FileVersion"] = SceneFile.MinimumVersion;
        // A pre-#229 chain: carried opaquely, never validated or restored.
        document["Actors"]![0]!["Fabrik"] = System.Text.Json.Nodes.JsonNode.Parse(
            """[{"Slot":"Character","Endpoint":"j_te_r","Config":{"Solver":"NotASolver"}}]""");
        WriteContainer(fixture.Path, "\uFEFF" + document.ToJsonString());

        var read = SceneFileStore.Default.Read(fixture.Path);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Equal(SceneFile.MinimumVersion, read.Scene!.FileVersion);
        Assert.NotNull(read.Scene.Actors[0].Fabrik);

        Assert.True(SceneFileStore.Default.Write(read.Scene, fixture.Path).Succeeded);
        Assert.Equal(SceneFile.CurrentVersion, SceneFileStore.Default.Read(fixture.Path).Scene!.FileVersion);
    }

    [Fact]
    public void Bare_json_light_and_camera_entries_read_as_one_entity_entries()
    {
        using var fixture = new SceneFixture();
        var lightPath = Path.Combine(fixture.Root, "old" + SceneFile.LightEntryExtension);
        var cameraPath = Path.Combine(fixture.Root, "old" + SceneFile.CameraEntryExtension);
        var light = ValidScene().Lights[0].Light!;
        light.CameraAnchor = new PlacementAnchorData { Position = new Vector3(1, 2, 3) };
        File.WriteAllText(lightPath, JsonSerializer.Serialize(light, SceneJsonOptionsAccessor.Options));
        File.WriteAllText(cameraPath, JsonSerializer.Serialize(
            new CameraFile { Name = "Saved view", Kind = CameraKind.Free },
            SceneJsonOptionsAccessor.Options));

        var lightRead = SceneFileStore.Default.Read(lightPath);
        var cameraRead = SceneFileStore.Default.Read(cameraPath);

        Assert.True(lightRead.Succeeded, lightRead.Failure?.Detail);
        Assert.Equal("Key light", Assert.Single(lightRead.Scene!.Lights).Light!.Name);
        Assert.Equal(new Vector3(1, 2, 3), lightRead.Scene.CameraAnchor!.Position);
        Assert.True(cameraRead.Succeeded, cameraRead.Failure?.Detail);
        var camera = Assert.Single(cameraRead.Scene!.Cameras);
        Assert.True(camera.IsLive);
        Assert.False(camera.IsDefault);
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

    [Fact]
    public void Entity_counts_are_not_capped_by_the_file_format()
    {
        // The game has no light or prop cap; a 53-light capture used to be
        // refused whole by an arbitrary 50.
        using var fixture = new SceneFixture();
        var scene = ValidScene();
        var light = scene.Lights[0].Light!;
        for (int i = 1; i < 53; i++)
            scene.Lights.Add(new SceneLight
            {
                Key = Guid.NewGuid(),
                Light = new LightFile { Name = $"Light {i}", Kind = light.Kind, Transform = light.Transform },
            });
        var prop = scene.Props[0];
        for (int i = 1; i < 120; i++)
            scene.Props.Add(new SceneProp
            {
                Key = Guid.NewGuid(),
                Name = $"Chair {i}",
                Model = prop.Model,
                Submodel = prop.Submodel,
                Variant = prop.Variant,
                Transform = prop.Transform,
            });

        var write = SceneFileStore.Default.Write(scene, fixture.Path);
        var read = SceneFileStore.Default.Read(fixture.Path);

        Assert.True(write.Succeeded, write.Failure?.Detail);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Equal(53, read.Scene!.Lights.Count);
        Assert.Equal(120, read.Scene.Props.Count);
        Assert.True(SceneFileValidation.ValidateForLoad(read.Scene, out var refusals).Succeeded);
        Assert.Empty(refusals);
    }

    [Fact]
    public void An_oversized_scene_document_is_still_refused()
    {
        // The size cap, not entity counts, is what bounds a hostile file.
        using var fixture = new SceneFixture();
        Directory.CreateDirectory(fixture.Root);
        using (var file = File.Create(fixture.Path))
        using (var archive = new System.IO.Compression.ZipArchive(
            file, System.IO.Compression.ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry(SceneFileStore.DocumentEntry).Open())
            entry.Write(new byte[SceneFileLimits.MaxDocumentBytes + 1]);

        var read = SceneFileStore.Default.Read(fixture.Path);

        Assert.False(read.Succeeded);
        Assert.Equal(SceneStoreFailureKind.SizeLimit, read.Failure!.Kind);
    }

    // ── issue #439: one bad entity never refuses the whole scene ─────────

    [Fact]
    public void A_load_names_bad_entities_and_keeps_the_rest_while_writes_stay_strict()
    {
        var scene = ValidScene();
        scene.Description = new string('d', 400);
        scene.Lights.Add(new SceneLight
        {
            Key = Guid.NewGuid(),
            Light = new LightFile { Name = new string('x', 300), Transform = scene.Lights[0].Light!.Transform },
        });
        scene.WorldObjects = [new SceneWorldObject { Key = Guid.NewGuid(), Path = "bg/fast.avfx", VfxSpeed = float.PositiveInfinity }];
        scene.Actors[0].Mcdf = new SceneActorMcdf
        {
            FileName = "lead.mcdf",
            ContentHash = new string('A', 64),
            PackageEntry = SceneFileStore.DocumentEntry,
        };
        Assert.False(SceneFileValidation.Validate(scene).Succeeded);

        var load = SceneFileValidation.ValidateForLoad(scene, out var refusals);

        Assert.True(load.Succeeded);
        Assert.Equal(
            new[] { SceneOutcomeKind.CharacterFile, SceneOutcomeKind.WorldObject, SceneOutcomeKind.Light },
            refusals.Select(refusal => refusal.Kind).ToArray());
        Assert.Equal("Lead", refusals[0].Name);
        Assert.Single(scene.Actors);
        Assert.Null(scene.Actors[0].Mcdf);
        Assert.Single(scene.Lights);
        Assert.Empty(scene.WorldObjects);
        Assert.True(SceneFileValidation.Validate(scene).Succeeded);

        // The structure the load's restore dereferences is checked whether or
        // not a group carries a transform.
        scene.Groups = [new SceneGroupEntry { Key = Guid.NewGuid(), Name = "Rig", Members = null! }];
        Assert.False(SceneFileValidation.ValidateForLoad(scene, out _).Succeeded);
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
