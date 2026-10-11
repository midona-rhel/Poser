using System.Globalization;
using System.Numerics;
using Poser.Application.AutoSave;
using Poser.Application.Scene;
using Poser.Documents.AutoSave;
using Poser.Documents.Config;
using Poser.Documents.Files;
using Poser.Application.Settings;

namespace Poser.Application.Tests.AutoSave;

public sealed class SceneAutoSaveTests
{
    [Fact]
    public void Deferred_capture_progresses_without_a_panel_and_deduplicates_unchanged_files()
    {
        using var test = new Harness();
        test.Tick(0);
        test.Tick(10);
        Assert.NotNull(test.Pending);
        test.Complete();
        Assert.Equal(SceneAutoSaveStatus.Written, test.Service.LastResult.Status);
        var path = test.Service.LastResult.Path;
        Assert.True(File.Exists(path));
        test.Tick(20);
        test.Complete();
        Assert.Equal(SceneAutoSaveStatus.Skipped, test.Service.LastResult.Status);
        Assert.Single(Directory.GetFiles(test.Root, "*.xivs", SearchOption.AllDirectories));
    }

    [Fact]
    public void Bone_noise_is_compared_to_the_last_written_baseline_so_drift_accumulates()
    {
        using var test = new Harness();
        var bone = new PoseFile.BoneData
        {
            Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One,
        };
        var pose = new PoseFile();
        pose.Bones["j_kosi"] = bone;
        test.Scene.Actors.Add(new SceneActor { Key = Guid.NewGuid(), Name = "Actor", Pose = pose });

        test.Tick(0);
        test.Tick(10);
        test.Complete();
        Assert.True(test.Service.LastResult.Status == SceneAutoSaveStatus.Written,
            test.Service.LastResult.Detail);

        bone.Scale = bone.Scale with { X = bone.Scale.X + 0.0000011f };
        test.Tick(20);
        test.Complete();
        Assert.Equal(SceneAutoSaveStatus.Skipped, test.Service.LastResult.Status);

        bone.Scale = bone.Scale with { X = bone.Scale.X + 0.0000011f };
        test.Tick(30);
        test.Complete();
        Assert.Equal(SceneAutoSaveStatus.Written, test.Service.LastResult.Status);
        Assert.Equal(2, Directory.GetFiles(test.Root, "*.xivs", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Stale_session_capture_is_ignored_and_capture_failure_creates_no_file()
    {
        using var test = new Harness();
        test.Tick(0);
        test.Tick(10);
        var stale = test.Pending!;
        test.Service.Tick(test.Now, false);
        test.Tick(20);
        stale(SceneCaptureOutcome.Ok(test.Scene, []));
        Assert.Equal(SceneAutoSaveStatus.Idle, test.Service.LastResult.Status);
        test.Tick(30);
        test.Pending!(SceneCaptureOutcome.Fail("Actor disappeared during capture."));
        Assert.Equal(SceneAutoSaveStatus.Failed, test.Service.LastResult.Status);
        Assert.Contains("disappeared", test.Service.LastResult.Detail);
        Assert.Contains(test.Warnings, warning => warning.Contains("disappeared"));
        Assert.Empty(Directory.GetFiles(test.Root, "*.xivs", SearchOption.AllDirectories));
    }

    [Fact]
    public void Signature_changes_when_only_a_bone_or_prop_transform_moves()
    {
        var bone = new PoseFile.BoneData
        {
            Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.One,
        };
        var prop = new SceneProp { Key = Guid.NewGuid(), Name = "Prop" };
        var actorPose = new PoseFile();
        actorPose.Bones["j_kosi"] = bone;
        var scene = new SceneFile
        {
            SceneId = Guid.NewGuid(),
            Actors = [new SceneActor { Key = Guid.NewGuid(), Name = "Actor", Pose = actorPose }],
            Props = [prop],
        };

        var before = SceneAutoSaveStore.Signature(scene);
        bone.Position = new Vector3(0, 0.25f, 0);
        var posed = SceneAutoSaveStore.Signature(scene);
        prop.Transform.Position = new Vector3(1, 2, 3);
        var moved = SceneAutoSaveStore.Signature(scene);

        Assert.NotNull(before);
        Assert.NotEqual(before, posed);
        Assert.NotEqual(posed, moved);
    }

    [Fact]
    public void Signature_ignores_bone_capture_noise_but_keeps_small_authored_and_light_edits()
    {
        var bone = new PoseFile.BoneData
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        var light = new LightFile { Intensity = 1f };
        var pose = new PoseFile();
        pose.Bones["j_kami_a"] = bone;
        var scene = new SceneFile
        {
            SceneId = Guid.NewGuid(),
            Actors = [new SceneActor { Key = Guid.NewGuid(), Name = "Actor", Pose = pose }],
            Lights = [new SceneLight { Key = Guid.NewGuid(), Light = light }],
        };

        var baseline = SceneAutoSaveStore.CaptureIdentity(scene)!;
        bone.Scale = new Vector3(1.0000005f, 1f, 0.99999994f);
        Assert.True(SceneAutoSaveStore.SemanticallyEquals(
            baseline, SceneAutoSaveStore.CaptureIdentity(scene)!));

        bone.Scale = new Vector3(1.000004f, 1f, 1f);
        var authored = SceneAutoSaveStore.CaptureIdentity(scene)!;
        Assert.False(SceneAutoSaveStore.SemanticallyEquals(baseline, authored));

        light.Intensity += 0.0000001f;
        Assert.False(SceneAutoSaveStore.SemanticallyEquals(
            authored, SceneAutoSaveStore.CaptureIdentity(scene)!));
    }

    [Fact]
    public void Semantic_identity_has_no_quantization_boundary_and_accumulates_from_written_baseline()
    {
        var bone = new PoseFile.BoneData
        {
            Position = new Vector3(0.12345099f, 0, 0),
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
        };
        var pose = new PoseFile();
        pose.Bones["j_kosi"] = bone;
        var scene = new SceneFile
        {
            SceneId = Guid.NewGuid(),
            Actors = [new SceneActor { Key = Guid.NewGuid(), Pose = pose }],
        };
        var written = SceneAutoSaveStore.CaptureIdentity(scene)!;

        bone.Position = new Vector3(0.12345101f, 0, 0);
        Assert.True(SceneAutoSaveStore.SemanticallyEquals(
            written, SceneAutoSaveStore.CaptureIdentity(scene)!));

        bone.Position = new Vector3(0.1234521f, 0, 0);
        Assert.False(SceneAutoSaveStore.SemanticallyEquals(
            written, SceneAutoSaveStore.CaptureIdentity(scene)!));

        bone.Rotation = new Quaternion(0, 0, 0, -1);
        bone.Position = new Vector3(0.12345099f, 0, 0);
        Assert.True(SceneAutoSaveStore.SemanticallyEquals(
            written, SceneAutoSaveStore.CaptureIdentity(scene)!));
    }

    [Fact]
    public void Semantic_identity_treats_recorded_evaluated_scale_error_as_relative_float_noise()
    {
        var bone = new PoseFile.BoneData
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = new Vector3(1.0000014f, 1.1648104f, 1.0000083f),
        };
        var pose = new PoseFile();
        pose.Bones["n_sippo_b"] = bone;
        var scene = new SceneFile
        {
            SceneId = Guid.NewGuid(),
            Actors = [new SceneActor { Key = Guid.NewGuid(), Pose = pose }],
        };
        var written = SceneAutoSaveStore.CaptureIdentity(scene)!;

        // Values observed in consecutive idle captures of the same frozen actor.
        bone.Scale = new Vector3(1.0000015f, 1.1648097f, 1.0000072f);
        Assert.True(SceneAutoSaveStore.SemanticallyEquals(
            written, SceneAutoSaveStore.CaptureIdentity(scene)!));

        bone.Scale = new Vector3(1.000005f, 1.1648104f, 1.0000083f);
        Assert.False(SceneAutoSaveStore.SemanticallyEquals(
            written, SceneAutoSaveStore.CaptureIdentity(scene)!));
    }

    [Fact]
    public void Day_folder_name_is_gregorian_under_a_non_gregorian_culture()
    {
        using var test = new Harness();
        var store = new SceneAutoSaveStore(test.Root, _ => { });
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("th-TH");
        try
        {
            var (path, _) = store.Write(test.Scene, new DateTime(2026, 9, 1, 10, 0, 0));
            Assert.Equal("2026-09-01", Path.GetFileName(Path.GetDirectoryName(path)));
            Assert.Equal("10-00-00 Scene.xivs", Path.GetFileName(path));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Dispatch_failure_releases_admission_and_retention_uses_the_captured_settings()
    {
        using var test = new Harness();
        test.Dispatch = _ => throw new IOException("Unavailable worker");
        test.Tick(0);
        test.Tick(10);
        test.Complete();
        Assert.Equal(SceneAutoSaveStatus.Failed, test.Service.LastResult.Status);
        test.Dispatch = work => { work(); return true; };
        test.Tick(20);
        test.Complete();
        Assert.Equal(SceneAutoSaveStatus.Written, test.Service.LastResult.Status);
        test.Scene.WorldObjects![0].Name = "Changed scenery";
        Action? writer = null;
        test.Dispatch = work => { writer = work; return true; };
        test.Tick(30);
        test.Complete();
        Assert.NotNull(writer);
        test.Configuration.Config.AutoSave.MaxSceneSnapshots = 1;
        writer!();
        Assert.Equal(SceneAutoSaveStatus.Written, test.Service.LastResult.Status);
        Assert.Equal(2, Directory.GetFiles(test.Root, "*.xivs", SearchOption.AllDirectories).Length);
    }

    private sealed class Harness : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("PoserSceneAutosave-").FullName;
        public DateTime Now { get; private set; } = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        public ConfigurationService Configuration { get; } = new(new MemorySettings());
        public SceneAutoSaveService Service { get; }
        public Action<SceneCaptureOutcome>? Pending;
        public List<string> Warnings { get; } = [];
        public Func<Action, bool> Dispatch = work => { work(); return true; };
        // A scene containing only scenery still has authored content to preserve.
        public SceneFile Scene { get; } = new()
        {
            SceneId = Guid.NewGuid(),
            WorldObjects = [new SceneWorldObject
            {
                Key = Guid.NewGuid(), Name = "Scenery", Path = "bg/test.mdl", Spawned = true,
            }],
        };

        public Harness()
        {
            Configuration.Config.AutoSave.IntervalSeconds = 10;
            Configuration.Config.AutoSave.MaxSceneSnapshots = 2;
            Service = new SceneAutoSaveService(Configuration,
                (id, _, complete) => { Scene.SceneId = id; Pending = complete; return null; },
                () => false, new SceneAutoSaveStore(Root, _ => { }), Warnings.Add,
                () => Now, work => Dispatch(work));
        }

        public void Tick(int seconds)
        {
            Now = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
            Service.Tick(Now, true);
        }

        public void Complete() => Pending!(SceneCaptureOutcome.Ok(Scene, []));
        public void Dispose()
        {
            Service.Dispose();
            Directory.Delete(Root, true);
        }
    }

    private sealed class MemorySettings : IConfigurationPersistence
    {
        public ConfigurationLoadResult Load() => new(new PoserConfiguration());
        public void Save(PoserConfiguration configuration) { }
    }
}
