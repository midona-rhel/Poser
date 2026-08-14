using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Poser.Domain.Companions;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Files;
using Poser.Services;

namespace Poser.Tests.Files;

public sealed class SceneFileStoreTests
{
    // ── document building ────────────────────────────────────────────────

    /// <summary>Shared with the library suites, which need a shot the real
    /// codec accepts to prove the library reads one through it.</summary>
    internal static SceneFile ValidScene()
    {
        var actorKey = Guid.NewGuid();
        var pose = new PoseFile();
        pose.Bones["j_kao"] = new PoseFile.BoneData
        {
            Position = new Vector3(0.1f, 0.2f, 0.3f),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };

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
                    ModelCharaId = 0,
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
                    Camera = new CameraFile
                    {
                        Name = "GPose Camera",
                        Kind = CameraKind.Game,
                        Zoom = 2.5f,
                    },
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

    private static SceneFileStore Store() => new();

    // ── round trip ───────────────────────────────────────────────────────

    [Fact]
    public void Complete_scene_round_trips_through_the_atomic_store()
    {
        using var file = new TempSceneFile();
        var scene = ValidScene();

        var write = Store().Write(scene, file.Path);
        Assert.True(write.Succeeded, write.Failure?.Detail);
        Assert.Empty(write.RecoveryEvidencePaths);

        var read = Store().Read(file.Path);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        var loaded = read.Scene!;

        Assert.Equal(scene.SceneId, loaded.SceneId);
        Assert.Equal(SceneFile.CurrentVersion, loaded.FileVersion);

        var actor = Assert.Single(loaded.Actors);
        Assert.Equal("Lead", actor.Name);
        Assert.Equal(CompanionKind.Companion, actor.CompanionKind);
        Assert.Equal(12, actor.CompanionId);
        Assert.True(actor.HasCompanionSlot);
        Assert.Equal(
            new Vector3(0.1f, 0.2f, 0.3f),
            actor.Pose!.Bones["j_kao"].Position);

        var prop = Assert.Single(loaded.Props);
        Assert.Equal((ushort)89, prop.Model);
        Assert.Equal(new Vector3(1, 2, 3), prop.Transform.Position);

        var light = Assert.Single(loaded.Lights);
        Assert.Equal(LightKind.Spot, light.Light!.Kind);
        Assert.Equal("j_te_l", light.Attachment!.BoneName);
        Assert.Equal(scene.Actors[0].Key, light.Attachment.ActorKey);

        var camera = Assert.Single(loaded.Cameras);
        Assert.True(camera.IsLive);
        Assert.True(camera.IsDefault);
        Assert.Equal(scene.Actors[0].Key, camera.TargetActorKey);
        Assert.Equal(new Vector3(0, 0.5f, 0), camera.TargetOffset);

        Assert.Equal(720, loaded.Environment!.MinuteOfDay);
        Assert.Equal(EnvSection.Fog, Assert.Single(loaded.Environment.HeldSections));
        Assert.Equal(100f, loaded.Environment.Fog!.Value.Distance);
        Assert.Null(loaded.Environment.Sky);
    }

    [Fact]
    public void Where_a_scene_was_captured_round_trips()
    {
        using var file = new TempSceneFile();
        var scene = ValidScene();
        scene.TerritoryId = 132;
        scene.PlaceName = "New Gridania";

        Assert.True(Store().Write(scene, file.Path).Succeeded);

        var read = Store().Read(file.Path);
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Equal(132u, read.Scene!.TerritoryId);
        Assert.Equal("New Gridania", read.Scene.PlaceName);

        // The listing reads the place off the metadata probe, not the whole
        // document, so the probe has to carry it too.
        var metadata = Store().ReadMetadata(file.Path);
        Assert.Equal("New Gridania", metadata.PlaceName);
        Assert.Equal(132u, metadata.TerritoryId);
    }

    /// <summary>
    /// The preservation guarantee: a scene written BEFORE scenes recorded
    /// where they were taken carries neither member, and must load exactly as
    /// it always did — absent is a valid document, not a corrupt one, and the
    /// place reads back as nothing rather than as a guess.
    /// </summary>
    [Fact]
    public void A_scene_written_without_a_place_loads_unchanged()
    {
        var scene = ValidScene();
        var json = System.Text.Json.JsonSerializer.Serialize(
            scene, typeof(SceneFile), SceneJsonOptionsAccessor.Options);

        // Strip the two members outright: an old file does not carry them as
        // nulls, it does not carry them at all.
        var lines = json.Split('\n')
            .Where(line =>
                !line.Contains("\"TerritoryId\"")
                && !line.Contains("\"PlaceName\""));
        json = string.Join("\n", lines);
        Assert.DoesNotContain("PlaceName", json);
        Assert.DoesNotContain("TerritoryId", json);

        var read = Store().Parse(json);

        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Null(read.Scene!.PlaceName);
        Assert.Equal(0u, read.Scene.TerritoryId);
        // Everything the file DID carry is untouched.
        Assert.Equal(scene.SceneId, read.Scene.SceneId);
        Assert.Equal("Lead", Assert.Single(read.Scene.Actors).Name);
        Assert.Equal(720, read.Scene.Environment!.MinuteOfDay);
    }

    [Fact]
    public void An_over_long_place_name_is_a_typed_validation_refusal()
    {
        var scene = ValidScene();
        scene.PlaceName = new string('z', 257);

        using var file = new TempSceneFile();
        var write = Store().Write(scene, file.Path);

        Assert.False(write.Succeeded);
        Assert.False(File.Exists(file.Path));
    }

    [Fact]
    public void Unknown_members_are_ignored_on_read()
    {
        var scene = ValidScene();
        var json = System.Text.Json.JsonSerializer.Serialize(
            scene, typeof(SceneFile), SceneJsonOptionsAccessor.Options);
        json = json.Insert(json.IndexOf('{') + 1, "\"FutureUnknownMember\": 42,");

        var read = Store().Parse(json);

        Assert.True(read.Succeeded, read.Failure?.Detail);
    }

    // ── typed refusals ───────────────────────────────────────────────────

    [Fact]
    public void Future_file_version_is_a_typed_future_outcome_not_corruption()
    {
        using var file = new TempSceneFile();
        var scene = ValidScene();
        Assert.True(Store().Write(scene, file.Path).Succeeded);
        var text = File.ReadAllText(file.Path).Replace(
            $"\"FileVersion\": {SceneFile.CurrentVersion}",
            $"\"FileVersion\": {SceneFile.CurrentVersion + 1}");
        File.WriteAllText(file.Path, text);

        var read = Store().Read(file.Path);
        Assert.False(read.Succeeded);
        Assert.Equal(SceneStoreFailureKind.FutureVersion, read.Failure!.Kind);

        var metadata = Store().ReadMetadata(file.Path);
        Assert.Equal(SceneEntryStatus.Future, metadata.Status);
    }

    [Fact]
    public void Corrupt_json_is_a_typed_corrupt_listing_entry()
    {
        using var file = new TempSceneFile();
        File.WriteAllText(file.Path, "{\"SceneId\": ");

        Assert.Equal(
            SceneEntryStatus.Corrupt, Store().ReadMetadata(file.Path).Status);
    }

    [Fact]
    public void Empty_scene_identity_is_refused()
    {
        var scene = ValidScene();
        scene.SceneId = Guid.Empty;

        var write = Store().Write(scene, "unused.poserscene");

        Assert.False(write.Succeeded);
        Assert.Equal(SceneStoreFailureKind.Validation, write.Failure!.Kind);
        Assert.Equal(
            SceneFileValidationFailureKind.Identity,
            write.Failure.ValidationFailure!.Kind);
    }

    [Fact]
    public void Duplicate_actor_keys_are_refused()
    {
        var scene = ValidScene();
        var duplicate = new SceneActor
        {
            Key = scene.Actors[0].Key,
            Name = "Double",
            Pose = new PoseFile(),
        };
        scene.Actors.Add(duplicate);

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Identity);
    }

    [Fact]
    public void Light_attachment_to_a_missing_actor_is_refused()
    {
        var scene = ValidScene();
        scene.Lights[0].Attachment!.ActorKey = Guid.NewGuid();

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Relationship);
    }

    [Fact]
    public void Camera_target_to_a_missing_actor_is_refused()
    {
        var scene = ValidScene();
        scene.Cameras[0].TargetActorKey = Guid.NewGuid();

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Relationship);
    }

    [Fact]
    public void Camera_target_state_without_a_target_is_refused()
    {
        var scene = ValidScene();
        scene.Cameras[0].TargetActorKey = null;

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Relationship);
    }

    [Fact]
    public void A_camera_set_requires_exactly_one_live_camera()
    {
        var scene = ValidScene();
        scene.Cameras[0].IsLive = false;

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Relationship);
    }

    [Fact]
    public void The_default_camera_must_be_a_game_camera()
    {
        var scene = ValidScene();
        scene.Cameras[0].Camera!.Kind = CameraKind.Free;

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Relationship);
    }

    [Fact]
    public void Companion_attachment_without_a_slot_is_refused()
    {
        var scene = ValidScene();
        scene.Actors[0].HasCompanionSlot = false;

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Relationship);
    }

    [Fact]
    public void Companion_id_without_a_kind_is_refused()
    {
        var scene = ValidScene();
        scene.Actors[0].CompanionKind = null;

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Relationship);
    }

    [Fact]
    public void An_actor_without_a_pose_document_is_refused()
    {
        var scene = ValidScene();
        scene.Actors[0].Pose = null;

        AssertValidationFailure(scene, SceneFileValidationFailureKind.EmbeddedPose);
    }

    [Fact]
    public void An_invalid_embedded_pose_fails_the_whole_document()
    {
        var scene = ValidScene();
        scene.Actors[0].Pose!.Bones["j_kao"].Rotation =
            new Quaternion(float.NaN, 0, 0, 1);

        AssertValidationFailure(scene, SceneFileValidationFailureKind.EmbeddedPose);
    }

    [Fact]
    public void Nonfinite_light_values_are_refused()
    {
        var scene = ValidScene();
        scene.Lights[0].Light!.Intensity = float.PositiveInfinity;

        AssertValidationFailure(
            scene, SceneFileValidationFailureKind.NonFiniteNumeric);
    }

    [Fact]
    public void Degenerate_prop_rotation_is_refused()
    {
        var scene = ValidScene();
        scene.Props[0].Transform.Rotation = default;

        AssertValidationFailure(
            scene, SceneFileValidationFailureKind.DegenerateQuaternion);
    }

    [Fact]
    public void Environment_minute_out_of_range_is_refused()
    {
        var scene = ValidScene();
        scene.Environment!.MinuteOfDay = 1440;

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Range);
    }

    [Fact]
    public void Held_section_without_values_is_refused()
    {
        var scene = ValidScene();
        scene.Environment!.Fog = null;

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Document);
    }

    [Fact]
    public void Section_values_without_a_hold_are_refused()
    {
        var scene = ValidScene();
        scene.Environment!.Sky = new EnvSkyValues(1, 1f);

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Document);
    }

    [Fact]
    public void Actor_collection_over_the_bound_is_refused()
    {
        var scene = ValidScene();
        for (var index = 0; index <= SceneFileLimits.MaxActors; index++)
        {
            scene.Actors.Add(new SceneActor
            {
                Key = Guid.NewGuid(),
                Name = $"Extra {index}",
                Pose = new PoseFile(),
            });
        }

        AssertValidationFailure(
            scene, SceneFileValidationFailureKind.CollectionSize);
    }

    [Fact]
    public void Overlong_names_are_refused()
    {
        var scene = ValidScene();
        scene.Actors[0].Name =
            new string('x', SceneFileLimits.MaxNameCharacters + 1);

        AssertValidationFailure(scene, SceneFileValidationFailureKind.Name);
    }

    // ── store semantics ──────────────────────────────────────────────────

    [Fact]
    public void An_invalid_scene_never_touches_an_existing_destination()
    {
        using var file = new TempSceneFile();
        File.WriteAllText(file.Path, "existing bytes");
        var scene = ValidScene();
        scene.SceneId = Guid.Empty;

        var write = Store().Write(scene, file.Path);

        Assert.False(write.Succeeded);
        Assert.Equal("existing bytes", File.ReadAllText(file.Path));
    }

    [Fact]
    public void Overwriting_an_existing_scene_replaces_it_and_cleans_recovery_files()
    {
        using var file = new TempSceneFile();
        var first = ValidScene();
        Assert.True(Store().Write(first, file.Path).Succeeded);

        var second = ValidScene();
        second.Description = "Replaced";
        var write = Store().Write(second, file.Path);

        Assert.True(write.Succeeded, write.Failure?.Detail);
        var read = Store().Read(file.Path);
        Assert.Equal("Replaced", read.Scene!.Description);
        Assert.Equal(second.SceneId, read.Scene.SceneId);

        var directory = Path.GetDirectoryName(Path.GetFullPath(file.Path))!;
        var name = Path.GetFileName(file.Path);
        Assert.Empty(Directory.GetFiles(directory, $".{name}.*"));
    }

    [Fact]
    public void An_empty_file_is_a_typed_validation_failure()
    {
        using var file = new TempSceneFile();
        File.WriteAllText(file.Path, string.Empty);

        var read = Store().Read(file.Path);

        Assert.False(read.Succeeded);
        Assert.Equal(SceneStoreFailureKind.Validation, read.Failure!.Kind);
    }

    [Fact]
    public void A_missing_file_is_a_typed_read_failure()
    {
        var read = Store().Read(Path.Combine(
            Path.GetTempPath(), $"poser-missing-{Guid.NewGuid():N}.poserscene"));

        Assert.False(read.Succeeded);
        Assert.Equal(SceneStoreFailureKind.Read, read.Failure!.Kind);
    }

    [Fact]
    public void Metadata_reports_counts_for_a_valid_scene()
    {
        using var file = new TempSceneFile();
        Assert.True(Store().Write(ValidScene(), file.Path).Succeeded);

        var metadata = Store().ReadMetadata(file.Path);

        Assert.Equal(SceneEntryStatus.Valid, metadata.Status);
        Assert.Equal(1, metadata.ActorCount);
        Assert.Equal(1, metadata.PropCount);
        Assert.Equal(1, metadata.LightCount);
        Assert.Equal(1, metadata.CameraCount);
        Assert.Equal("Test scene", metadata.Description);
    }

    private static void AssertValidationFailure(
        SceneFile scene, SceneFileValidationFailureKind kind)
    {
        var outcome = SceneFileValidation.Validate(scene);
        Assert.False(outcome.Succeeded);
        Assert.Equal(kind, outcome.Failure!.Kind);

        var write = Store().Write(scene, Path.Combine(
            Path.GetTempPath(), $"poser-refused-{Guid.NewGuid():N}.poserscene"));
        Assert.False(write.Succeeded);
    }

    private sealed class TempSceneFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"poser-scene-{Guid.NewGuid():N}.poserscene");

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            var name = System.IO.Path.GetFileName(Path);
            foreach (var leftover in Directory.GetFiles(directory, $".{name}.*"))
                File.Delete(leftover);
        }
    }
}

/// <summary>Test-only access to the internal scene JSON options for shaping
/// unknown-member fixtures with the exact wire conventions.</summary>
internal static class SceneJsonOptionsAccessor
{
    public static System.Text.Json.JsonSerializerOptions Options =>
        SceneFile.JsonOptions;
}
