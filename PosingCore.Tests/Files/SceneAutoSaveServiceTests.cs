using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using NSubstitute;
using Poser.Config;
using Poser.Files;
using Poser.Game.Scene;
using Poser.Services;

namespace Poser.Tests.Files;

/// <summary>
/// The whole-shot snapshot cadence, layout and retention. Capture and the
/// scene-operation gate are injected, so every assertion is about the
/// auto-save's own decisions rather than native state.
/// </summary>
public sealed class SceneAutoSaveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "poser-scene-autosave-" + Guid.NewGuid().ToString("N"));

    private readonly IPluginLog _log = Substitute.For<IPluginLog>();
    private readonly IGPoseService _gpose = Substitute.For<IGPoseService>();
    private readonly ConfigurationService _configuration =
        new(Substitute.For<IDalamudPluginInterface>());

    private bool _operationRunning;
    private SceneCaptureOutcome? _captureResult;
    private int _captures;
    private Guid _lastCapturedSceneId;

    private static readonly DateTime Noon =
        new(2026, 8, 14, 12, 30, 15, DateTimeKind.Utc);

    private DateTime _now = Noon;

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp directory never fails a test.
        }
    }

    private static SceneFile Shot(int actors = 1)
    {
        var scene = new SceneFile { SceneId = Guid.NewGuid() };
        for (int index = 0; index < actors; index++)
        {
            var pose = new PoseFile();
            pose.Bones["j_kao"] = new PoseFile.BoneData
            {
                Position = Vector3.Zero,
                Rotation = Quaternion.Identity,
                Scale = Vector3.One,
            };
            scene.Actors.Add(new SceneActor
            {
                Key = Guid.NewGuid(),
                Name = $"Actor {index}",
                Pose = pose,
            });
        }
        return scene;
    }

    private SceneAutoSaveService Create()
    {
        _gpose.IsGPosing.Returns(true);
        _configuration.Config.AutoSave.Enabled = true;
        _configuration.Config.AutoSave.SceneSnapshots = true;
        _configuration.Config.AutoSave.IntervalSeconds = 60;
        return new SceneAutoSaveService(
            _log,
            framework: null,
            _gpose,
            _configuration,
            (sceneId, _) =>
            {
                _captures++;
                _lastCapturedSceneId = sceneId;
                return _captureResult ?? SceneCaptureOutcome.Ok(Shot(), new());
            },
            () => _operationRunning,
            _root,
            () => _now,
            // Inline dispatch: the write completes before the tick returns.
            work =>
            {
                work();
                return true;
            });
    }

    private string[] Snapshots() =>
        Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*.poserscene", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();

    // ── cadence ──────────────────────────────────────────────────────────

    [Fact]
    public void The_first_armed_tick_schedules_one_interval_out()
    {
        using var service = Create();

        service.Tick(_now);

        Assert.Equal(0, _captures);
        Assert.Empty(Snapshots());
        Assert.Equal(SceneAutoSaveStatus.Idle, service.LastResult.Status);
    }

    [Fact]
    public void A_due_tick_writes_one_snapshot_into_its_local_day_folder()
    {
        using var service = Create();

        service.Tick(_now);
        _now = Noon.AddSeconds(61);
        service.Tick(_now);

        var written = Assert.Single(Snapshots());
        var local = _now.ToLocalTime();
        Assert.Equal(
            Path.Combine(_root, local.ToString("yyyy-MM-dd")),
            Path.GetDirectoryName(written));
        Assert.Equal(
            $"{local:HH-mm-ss} Scene.poserscene", Path.GetFileName(written));
        Assert.Equal(SceneAutoSaveStatus.Written, service.LastResult.Status);
        Assert.Equal(written, service.LastResult.Path);
    }

    [Fact]
    public void A_snapshot_is_readable_back_through_the_ordinary_scene_codec()
    {
        using var service = Create();

        service.Tick(_now);
        _now = Noon.AddSeconds(61);
        service.Tick(_now);

        var read = SceneFileStore.Default.Read(Assert.Single(Snapshots()));
        Assert.True(read.Succeeded, read.Failure?.Detail);
        Assert.Single(read.Scene!.Actors);
    }

    [Fact]
    public void Every_snapshot_of_one_session_carries_the_same_scene_identity()
    {
        using var service = Create();

        service.Tick(_now);
        _now = Noon.AddSeconds(61);
        service.Tick(_now);
        var first = _lastCapturedSceneId;
        _now = Noon.AddSeconds(122);
        service.Tick(_now);

        Assert.NotEqual(Guid.Empty, first);
        Assert.Equal(first, _lastCapturedSceneId);
    }

    [Fact]
    public void A_new_gpose_session_starts_a_new_scene_identity()
    {
        using var service = Create();

        service.Tick(_now);
        _now = Noon.AddSeconds(61);
        service.Tick(_now);
        var first = _lastCapturedSceneId;

        _gpose.IsGPosing.Returns(false);
        service.Tick(_now);
        _gpose.IsGPosing.Returns(true);
        service.Tick(_now);
        _now = Noon.AddSeconds(200);
        service.Tick(_now);

        Assert.NotEqual(first, _lastCapturedSceneId);
    }

    [Fact]
    public void Scene_snapshots_can_be_turned_off_without_touching_pose_autosave()
    {
        using var service = Create();
        _configuration.Config.AutoSave.SceneSnapshots = false;

        service.Tick(_now);
        _now = Noon.AddSeconds(61);
        service.Tick(_now);

        Assert.Equal(0, _captures);
        Assert.Empty(Snapshots());
        Assert.True(_configuration.Config.AutoSave.Enabled);
    }

    [Fact]
    public void Leaving_gpose_disarms_the_timer()
    {
        using var service = Create();
        service.Tick(_now);
        _gpose.IsGPosing.Returns(false);

        _now = Noon.AddSeconds(61);
        service.Tick(_now);

        Assert.Equal(0, _captures);
        Assert.Empty(Snapshots());
    }

    // ── refusals ─────────────────────────────────────────────────────────

    [Fact]
    public void A_running_scene_operation_skips_the_snapshot_by_name()
    {
        using var service = Create();
        _operationRunning = true;

        service.SnapshotNow();

        Assert.Equal(0, _captures);
        Assert.Empty(Snapshots());
        Assert.Equal(SceneAutoSaveStatus.Skipped, service.LastResult.Status);
        Assert.Contains("half-restored", service.LastResult.Detail);
    }

    [Fact]
    public void An_empty_shot_writes_nothing_and_leaves_no_folder()
    {
        using var service = Create();
        _captureResult = SceneCaptureOutcome.Ok(
            new SceneFile { SceneId = Guid.NewGuid() }, new());

        service.SnapshotNow();

        Assert.Empty(Snapshots());
        Assert.False(Directory.Exists(_root));
        Assert.Equal(SceneAutoSaveStatus.Skipped, service.LastResult.Status);
    }

    [Fact]
    public void A_capture_refusal_is_a_typed_failure_not_a_silent_miss()
    {
        using var service = Create();
        _captureResult = SceneCaptureOutcome.Fail("A pose import is applying.");

        service.SnapshotNow();

        Assert.Empty(Snapshots());
        Assert.Equal(SceneAutoSaveStatus.Failed, service.LastResult.Status);
        Assert.Contains("pose import", service.LastResult.Detail);
    }

    // ── retention ────────────────────────────────────────────────────────

    [Fact]
    public void Retention_keeps_the_newest_snapshots_and_prunes_the_rest()
    {
        using var service = Create();
        _configuration.Config.AutoSave.MaxSceneSnapshots = 2;

        for (int index = 1; index <= 4; index++)
        {
            _now = Noon.AddSeconds(index * 61);
            service.SnapshotNow();
        }

        var kept = Snapshots();
        Assert.Equal(2, kept.Length);
        var local = _now.ToLocalTime();
        Assert.Contains(
            kept, path => Path.GetFileName(path) ==
                $"{local:HH-mm-ss} Scene.poserscene");
    }

    [Fact]
    public void Retention_is_floored_at_one_snapshot()
    {
        using var service = Create();
        _configuration.Config.AutoSave.MaxSceneSnapshots = 0;

        _now = Noon.AddSeconds(61);
        service.SnapshotNow();
        _now = Noon.AddSeconds(122);
        service.SnapshotNow();

        Assert.Single(Snapshots());
    }

    [Fact]
    public void A_day_folder_whose_last_snapshot_is_pruned_goes_with_it()
    {
        using var service = Create();
        _configuration.Config.AutoSave.MaxSceneSnapshots = 1;

        service.SnapshotNow();
        var firstDay = Directory.GetDirectories(_root).Single();
        // A day later: the new snapshot's folder is the only one that may
        // survive the sweep.
        _now = Noon.AddDays(1);
        service.SnapshotNow();

        Assert.Single(Snapshots());
        Assert.False(Directory.Exists(firstDay));
    }

    [Fact]
    public void Two_snapshots_in_the_same_second_suffix_instead_of_overwriting()
    {
        using var service = Create();
        _configuration.Config.AutoSave.MaxSceneSnapshots = 10;

        service.SnapshotNow();
        service.SnapshotNow();

        Assert.Equal(2, Snapshots().Length);
    }
}
