using System.Collections.Concurrent;
using Poser.Application.Operations;
using Poser.Domain.Companions;
using Poser.Files;
using Poser.Game.Scene;

namespace Poser.Game.Tests.Scene;

/// <summary>
/// The scene transaction's behavior contract, driven through the
/// <see cref="ISceneRuntime"/> seam so every assertion is about admission,
/// ordering, guards, rollback and terminal truthfulness — never about native
/// state. The fake runs framework actions inline, so an awaited
/// <see cref="SceneWorkflow.Drain"/> is an exact barrier.
/// </summary>
public sealed class SceneWorkflowTests
{
    // ── the seam fake ────────────────────────────────────────────────────

    private sealed class FakeRuntime : ISceneRuntime
    {
        public readonly List<string> Calls = new();
        public readonly ConcurrentQueue<string> Destroyed = new();

        public SessionGeneration? Session = SessionGeneration.New();
        public SceneFile? ReadResult;
        public SceneStoreFailure? ReadFailure;
        public SceneFile? Captured;
        public SceneWriteOutcome WriteResult = SceneWriteOutcome.Success();

        /// <summary>Runs after each named call, so a test can flip the session,
        /// cancel, or release a gate at an exact point in the phase order.</summary>
        public Action<string>? AfterCall;

        public Func<SceneActor, string?>? ActorSpawnFailure;
        public Func<SceneProp, string?>? PropSpawnFailure;
        public Func<SceneLight, string?>? LightSpawnFailure;
        public Func<SceneActor, string?>? PoseFailure;
        public Func<SceneActor, string?>? CompanionFailure;
        public Func<SceneActor, string?>? PlacementFailure;
        public Func<SceneActor, string?>? AnimationFailure;
        public Func<SceneActor, string?>? GazeFailure;

        /// <summary>The token each actor's gaze was handed, by actor name —
        /// the assertion surface for Entity-target resolution.</summary>
        public readonly Dictionary<string, object?> GazeTargets = new();

        private void Record(string call)
        {
            lock (Calls)
                Calls.Add(call);
            AfterCall?.Invoke(call);
        }

        public SessionGeneration? ActiveSession => Session;

        public Task<T> OnFramework<T>(Func<T> func) => Task.FromResult(func());

        public SceneReadOutcome ReadScene(string path)
        {
            Record("ReadScene");
            return ReadFailure is { } failure
                ? SceneReadOutcome.Failed(failure)
                : SceneReadOutcome.Success(ReadResult!);
        }

        public SceneWriteOutcome WriteScene(SceneFile scene, string path)
        {
            Record("WriteScene");
            Captured = scene;
            return WriteResult;
        }

        public SceneCaptureOutcome CaptureScene(Guid sceneId, string? description)
        {
            Record("CaptureScene");
            return SceneCaptureOutcome.Ok(
                new SceneFile { SceneId = sceneId, Description = description },
                new List<string>());
        }

        public object? SpawnActor(SceneActor data, out string? detail)
        {
            Record($"SpawnActor:{data.Name}");
            detail = ActorSpawnFailure?.Invoke(data);
            return detail is null ? new Token($"actor:{data.Name}") : null;
        }

        public bool ActorReady(object actor)
        {
            Record("ActorReady");
            return true;
        }

        public string? AttachCompanion(object actor, SceneActor data)
        {
            Record($"AttachCompanion:{data.Name}");
            return CompanionFailure?.Invoke(data);
        }

        public string? ArmPoseImport(
            object actor,
            SceneActor data,
            string description,
            Action<OperationReceipt> onReceipt)
        {
            Record($"ArmPoseImport:{data.Name}");
            if (PoseFailure?.Invoke(data) is { } refusal)
                return refusal;
            onReceipt(OperationReceipt.Applied(
                Guid.NewGuid(), OperationEpoch.First, Session!.Value,
                new Poser.Domain.Identity.ActorId(Guid.NewGuid(), 1)));
            return null;
        }

        public string? PlaceActor(object actor, SceneActor data)
        {
            Record($"PlaceActor:{data.Name}");
            return PlacementFailure?.Invoke(data);
        }

        public string? ApplyActorAnimation(object actor, SceneActor data)
        {
            Record($"ApplyActorAnimation:{data.Name}");
            return AnimationFailure?.Invoke(data);
        }

        public string? ApplyActorGaze(object actor, SceneActor data, object? target)
        {
            Record($"ApplyActorGaze:{data.Name}");
            GazeTargets[data.Name] = target;
            return GazeFailure?.Invoke(data);
        }

        public void SetActorVisibility(object actor, bool visible) =>
            Record("SetActorVisibility");

        public object? SpawnProp(SceneProp data, out string? detail)
        {
            Record($"SpawnProp:{data.Name}");
            detail = PropSpawnFailure?.Invoke(data);
            return detail is null ? new Token($"prop:{data.Name}") : null;
        }

        public object? SpawnLight(
            SceneLight data, object? attachmentOwner, out string? detail)
        {
            Record("SpawnLight");
            detail = LightSpawnFailure?.Invoke(data);
            return detail is null ? new Token("light") : null;
        }

        public CameraFile CaptureDefaultCameraState()
        {
            Record("CaptureDefaultCameraState");
            return new CameraFile();
        }

        public string? ApplyDefaultCamera(SceneCamera data)
        {
            Record("ApplyDefaultCamera");
            return null;
        }

        public object? CreateCamera(SceneCamera data, out string? detail)
        {
            Record("CreateCamera");
            detail = null;
            return new Token("camera");
        }

        public string? SetCameraTarget(
            object? camera, object targetActor, string displayName)
        {
            Record("SetCameraTarget");
            return null;
        }

        public string? SetLiveCamera(object? camera)
        {
            Record("SetLiveCamera");
            return null;
        }

        public SceneEnvironment CaptureEnvironmentState()
        {
            Record("CaptureEnvironmentState");
            return new SceneEnvironment();
        }

        public void ApplyEnvironment(SceneEnvironment target) =>
            Record("ApplyEnvironment");

        public void DestroyActor(object actor) => Destroy(actor);
        public void DestroyProp(object prop) => Destroy(prop);
        public void DestroyLight(object light) => Destroy(light);
        public void DestroyCamera(object camera) => Destroy(camera);

        private void Destroy(object token)
        {
            Record($"Destroy:{((Token)token).Name}");
            Destroyed.Enqueue(((Token)token).Name);
        }

        public void RestoreDefaultCamera(CameraFile baseline) =>
            Record("RestoreDefaultCamera");
    }

    private sealed record Token(string Name);

    // ── document building ────────────────────────────────────────────────

    private static SceneActor Actor(string name, out Guid key)
    {
        key = Guid.NewGuid();
        return new SceneActor { Key = key, Name = name, Pose = new PoseFile() };
    }

    private static SceneFile SceneWith(
        params SceneActor[] actors)
    {
        var scene = new SceneFile { SceneId = Guid.NewGuid() };
        scene.Actors.AddRange(actors);
        return scene;
    }

    private static SceneStoreFailure Corrupt(string detail) =>
        SceneStoreFailure.Create(SceneStoreFailureKind.Json, detail);

    // ── save ─────────────────────────────────────────────────────────────

    [Fact]
    public void Save_outside_a_session_refuses_without_capturing()
    {
        var runtime = new FakeRuntime { Session = null };
        using var workflow = new SceneWorkflow(runtime);

        var result = workflow.BeginSave("shot.poserscene");

        Assert.False(result.Success);
        Assert.Empty(runtime.Calls);
        Assert.Null(workflow.Receipt);
    }

    [Fact]
    public async Task Save_captures_before_it_writes_and_applies()
    {
        var runtime = new FakeRuntime();
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginSave("shot.poserscene", "A shot").Success);
        await workflow.Drain;

        Assert.Equal(new[] { "CaptureScene", "WriteScene" }, runtime.Calls);
        Assert.Equal("A shot", runtime.Captured!.Description);
        Assert.Equal(
            OperationReceiptState.Applied, workflow.Receipt!.State);
        Assert.Equal(ScenePhase.Completed, workflow.Progress!.Phase);
        Assert.True(workflow.Progress.Outcome!.Success);
    }

    [Fact]
    public async Task Save_receipt_targets_the_scene_document_identity()
    {
        var runtime = new FakeRuntime();
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginSave("shot.poserscene").Success);
        var pending = workflow.Receipt!;
        await workflow.Drain;

        // Identity must be stable from Pending to terminal: a whole-shot
        // operation has no single target actor, so the scope id is the target.
        Assert.NotEqual(Guid.Empty, pending.TargetActorId.LogicalId);
        Assert.Equal(pending.TargetActorId, workflow.Receipt!.TargetActorId);
        Assert.Equal(pending.OperationId, workflow.Receipt.OperationId);
        Assert.Equal(runtime.Session!.Value, workflow.Receipt.SessionGeneration);
    }

    [Fact]
    public async Task Save_write_failure_reports_the_surviving_evidence()
    {
        var runtime = new FakeRuntime
        {
            WriteResult = SceneWriteOutcome.Failed(
                SceneStoreFailure.Create(
                    SceneStoreFailureKind.Replace, "The replace failed."),
                new[] { @"C:\scenes\shot.poserscene.tmp" }),
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginSave("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        var evidence = Assert.Single(
            workflow.Progress!.Outcome!.RecoveryEvidencePaths);
        Assert.Equal(@"C:\scenes\shot.poserscene.tmp", evidence);
    }

    [Fact]
    public async Task Only_one_scene_operation_runs_at_a_time()
    {
        using var gate = new ManualResetEventSlim(false);
        var runtime = new FakeRuntime();
        runtime.AfterCall = call =>
        {
            if (call == "CaptureScene")
                gate.Wait(TimeSpan.FromSeconds(10));
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginSave("first.poserscene").Success);
        var second = workflow.BeginLoad("second.poserscene");
        gate.Set();
        await workflow.Drain;

        Assert.False(second.Success);
        Assert.Contains("already running", second.Detail);
        Assert.DoesNotContain("ReadScene", runtime.Calls);
    }

    // ── load: whole-document validation first ────────────────────────────

    [Fact]
    public async Task An_unreadable_document_never_touches_the_session()
    {
        var runtime = new FakeRuntime
        {
            ReadFailure = Corrupt("The document is not valid JSON."),
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(new[] { "ReadScene" }, runtime.Calls);
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        Assert.Contains("not valid JSON", workflow.Progress!.Outcome!.Detail);
    }

    // ── load: documented order ───────────────────────────────────────────

    [Fact]
    public async Task Load_runs_the_documented_phase_order()
    {
        var lead = Actor("Lead", out var leadKey);
        lead.CompanionKind = CompanionKind.Companion;
        lead.CompanionId = 4;
        var scene = SceneWith(lead);
        scene.Props.Add(new SceneProp { Key = Guid.NewGuid(), Name = "Chair" });
        scene.Lights.Add(new SceneLight
        {
            Key = Guid.NewGuid(),
            Light = new LightFile { Name = "Key" },
        });
        scene.Cameras.Add(new SceneCamera
        {
            Key = Guid.NewGuid(),
            Camera = new CameraFile { Name = "Default" },
            IsDefault = true,
            IsLive = true,
            TargetActorKey = leadKey,
            TargetActorName = "Lead",
        });
        scene.Environment = new SceneEnvironment();

        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(
            new[]
            {
                "ReadScene",
                // baselines, then spawn/admit
                "CaptureEnvironmentState",
                "CaptureDefaultCameraState",
                "SpawnActor:Lead",
                "SpawnProp:Chair",
                // readiness barrier
                "ActorReady",
                // relationships
                "AttachCompanion:Lead",
                // animation BEFORE the pose: the pose is authored on top of
                // whatever was playing, so a replayed timeline must not land
                // after it and animate over it
                "ApplyActorAnimation:Lead",
                // transforms/pose
                "ArmPoseImport:Lead",
                "PlaceActor:Lead",
                // presentation — gaze rides here, after the pose, because the
                // look-at re-drives its channels every frame and its target is
                // another restored actor
                "SetActorVisibility",
                "ApplyActorGaze:Lead",
                // cameras, lights, environment last
                "ApplyDefaultCamera",
                "SetCameraTarget",
                "SetLiveCamera",
                "SpawnLight",
                "ApplyEnvironment",
            },
            runtime.Calls);
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
        Assert.Empty(runtime.Destroyed);
    }

    /// <summary>A saved Entity gaze names another actor by in-document key;
    /// the load hands the runtime the RESTORED actor's token, never the key
    /// and never the saved object id.</summary>
    [Fact]
    public async Task An_entity_gaze_is_handed_the_restored_target_token()
    {
        var lead = Actor("Lead", out _);
        var second = Actor("Second", out var secondKey);
        lead.Gaze = new SceneActorGaze
        {
            Mode = Poser.Services.GazeTargetMode.Entity,
            TargetActorKey = secondKey,
        };
        var scene = SceneWith(lead, second);

        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
        Assert.Equal(
            new Token("actor:Second"), runtime.GazeTargets["Lead"]);
        // An actor with no saved gaze is still offered, with no target.
        Assert.Null(runtime.GazeTargets["Second"]);
    }

    /// <summary>
    /// A placement, animation or gaze that cannot land is an ENTITY-level
    /// refusal: the actor stays restored, the operation lands Failed, and
    /// every refusal is named. Silently reporting success is the exact
    /// failure that made a restored scene look like it had forgotten where
    /// the actor stood.
    /// </summary>
    [Fact]
    public async Task Placement_animation_and_gaze_refusals_are_named_not_swallowed()
    {
        var scene = SceneWith(Actor("Lead", out _));
        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            PlacementFailure = _ => "The placement was refused.",
            AnimationFailure = _ => "The timeline was refused.",
            GazeFailure = _ => "The gaze was refused.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        // Nothing is rolled back: the actor itself restored.
        Assert.Empty(runtime.Destroyed);
        var outcome = workflow.Progress!.Outcome!;
        Assert.Contains(outcome.Entities, entity =>
            entity.Kind == "Actor" && !entity.Restored &&
            entity.Detail == "The placement was refused.");
        Assert.Contains(outcome.Entities, entity =>
            entity.Kind == "Animation" && !entity.Restored &&
            entity.Detail == "The timeline was refused.");
        Assert.Contains(outcome.Entities, entity =>
            entity.Kind == "Gaze" && !entity.Restored &&
            entity.Detail == "The gaze was refused.");
    }

    // ── load: structural refusal rolls the whole operation back ──────────

    [Fact]
    public async Task A_failed_actor_spawn_rolls_back_in_reverse_creation_order()
    {
        var scene = SceneWith(
            Actor("Lead", out _), Actor("Second", out _));
        scene.Props.Add(new SceneProp { Key = Guid.NewGuid(), Name = "Chair" });

        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            ActorSpawnFailure = actor =>
                actor.Name == "Second" ? "no free slot" : null,
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        // The prop never spawned — the actor phase aborted first — so
        // rollback destroys exactly the one actor that did.
        Assert.Equal(new[] { "actor:Lead" }, runtime.Destroyed.ToArray());
        Assert.Equal(OperationReceiptState.RolledBack, workflow.Receipt!.State);
        Assert.Equal(ScenePhase.RolledBack, workflow.Progress!.Phase);
        Assert.False(workflow.Progress.Outcome!.LeftEntitiesBehind);
        Assert.Contains("no free slot", workflow.Progress.Outcome.Detail);
    }

    [Fact]
    public async Task Rollback_destroys_lights_props_and_actors_in_reverse()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.Props.Add(new SceneProp { Key = Guid.NewGuid(), Name = "Chair" });
        scene.Lights.Add(new SceneLight
        {
            Key = Guid.NewGuid(),
            Light = new LightFile { Name = "Key" },
        });
        scene.Cameras.Add(new SceneCamera
        {
            Key = Guid.NewGuid(),
            Camera = new CameraFile { Name = "Wide" },
        });
        scene.Environment = new SceneEnvironment();

        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);
        // Cancel after the lights land: the commit re-guard must roll back
        // instead of committing a half-applied shot.
        runtime.AfterCall = call =>
        {
            if (call == "SpawnLight")
                workflow.Cancel();
        };

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(
            new[] { "camera", "light", "prop:Chair", "actor:Lead" },
            runtime.Destroyed.ToArray());
        Assert.Equal(OperationReceiptState.Cancelled, workflow.Receipt!.State);
        Assert.Contains("RestoreDefaultCamera", runtime.Calls);
    }

    [Fact]
    public async Task A_replaced_session_invalidates_the_load_and_rolls_back()
    {
        var scene = SceneWith(Actor("Lead", out _));
        var runtime = new FakeRuntime { ReadResult = scene };
        runtime.AfterCall = call =>
        {
            if (call == "SpawnActor:Lead")
                runtime.Session = SessionGeneration.New();
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(new[] { "actor:Lead" }, runtime.Destroyed.ToArray());
        Assert.Equal(OperationReceiptState.Cancelled, workflow.Receipt!.State);
        Assert.Contains("session ended", workflow.Progress!.Outcome!.Detail);
    }

    // ── load: typed partial recovery keeps what restored ─────────────────

    [Fact]
    public async Task An_unresolvable_light_attachment_is_a_named_refusal()
    {
        var lead = Actor("Lead", out var leadKey);
        var scene = SceneWith(lead);
        scene.Lights.Add(new SceneLight
        {
            Key = Guid.NewGuid(),
            Light = new LightFile { Name = "Rim" },
            Attachment = new SceneBoneAttachment
            {
                ActorKey = leadKey,
                BoneName = "j_gone",
            },
        });

        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            LightSpawnFailure = _ =>
                "The attachment bone 'j_gone' does not exist on the restored actor.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        // Partial recovery: the actor stays, the light is refused by name,
        // and nothing is silently detached into world space.
        Assert.Empty(runtime.Destroyed);
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        var outcome = workflow.Progress!.Outcome!;
        Assert.True(outcome.LeftEntitiesBehind);
        var refusal = Assert.Single(
            outcome.Entities, entity => !entity.Restored);
        Assert.Equal("Light", refusal.Kind);
        Assert.Equal("Rim", refusal.Name);
        Assert.Contains("j_gone", refusal.Detail);
        Assert.Contains(
            outcome.Entities,
            entity => entity is { Kind: "Actor", Name: "Lead", Restored: true });
    }

    [Fact]
    public async Task A_refused_pose_keeps_its_actor_and_names_the_refusal()
    {
        var scene = SceneWith(Actor("Lead", out _));
        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            PoseFailure = _ => "The pose engine is busy.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Empty(runtime.Destroyed);
        Assert.DoesNotContain("PlaceActor:Lead", runtime.Calls);
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        var refusal = Assert.Single(
            workflow.Progress!.Outcome!.Entities, entity => !entity.Restored);
        Assert.Equal("Actor", refusal.Kind);
        Assert.Contains("pose engine is busy", refusal.Detail);
    }

    [Fact]
    public async Task A_refused_companion_is_named_without_losing_the_actor()
    {
        var lead = Actor("Lead", out _);
        lead.CompanionKind = CompanionKind.Mount;
        lead.CompanionId = 9;
        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(lead),
            CompanionFailure = _ => "The companion could not be attached.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Empty(runtime.Destroyed);
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        Assert.Contains(
            workflow.Progress!.Outcome!.Entities,
            entity => entity is { Kind: "Companion", Restored: false });
    }

    [Fact]
    public async Task A_refused_prop_leaves_the_rest_of_the_shot_standing()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.Props.Add(new SceneProp { Key = Guid.NewGuid(), Name = "Chair" });
        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            PropSpawnFailure = _ => "The prop model is not in the catalog.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Empty(runtime.Destroyed);
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        Assert.Contains("ArmPoseImport:Lead", runtime.Calls);
    }

    // ── disposal ─────────────────────────────────────────────────────────

    [Fact]
    public void Disposal_closes_admission_permanently()
    {
        var runtime = new FakeRuntime();
        var workflow = new SceneWorkflow(runtime);
        workflow.Dispose();

        var save = workflow.BeginSave("shot.poserscene");
        var load = workflow.BeginLoad("shot.poserscene");

        Assert.False(save.Success);
        Assert.False(load.Success);
        Assert.Empty(runtime.Calls);
    }
}
