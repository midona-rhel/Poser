using System.Collections.Concurrent;
using System.Numerics;
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
        public Func<SceneOverlay, string?>? OverlayStageFailure;
        public Func<SceneWorldObject, string?>? WorldObjectAdoptFailure;
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

        /// <summary>Refuses the ARM — the save never reaches a capture.</summary>
        public string? CaptureArmRefusal;

        /// <summary>Holds the armed capture open, the way a real refresh does
        /// while it waits for the update pass. <see cref="ReleaseCapture"/>
        /// lands it.</summary>
        public bool DeferCapture;

        private Action<SceneCaptureOutcome>? _pendingCapture;
        private SceneCaptureOutcome? _pendingOutcome;

        public string? ArmSceneCapture(
            Guid sceneId,
            string? description,
            Action<SceneCaptureOutcome> onCaptured)
        {
            Record("ArmSceneCapture");
            if (CaptureArmRefusal is { } refusal)
                return refusal;
            var outcome = SceneCaptureOutcome.Ok(
                new SceneFile { SceneId = sceneId, Description = description },
                new List<string>());
            if (DeferCapture)
            {
                _pendingCapture = onCaptured;
                _pendingOutcome = outcome;
                return null;
            }
            Record("CaptureScene");
            onCaptured(outcome);
            return null;
        }

        public void ReleaseCapture()
        {
            var callback = _pendingCapture;
            var outcome = _pendingOutcome;
            _pendingCapture = null;
            _pendingOutcome = null;
            if (callback is null || outcome is null)
                return;
            Record("CaptureScene");
            callback(outcome);
        }

        /// <summary>Notes the hash pass hands back, and the record of whether
        /// it ran before the write at all.</summary>
        public List<string> McdfStampNotes = new();

        public IReadOnlyList<string> StampMcdfHashes(SceneFile scene)
        {
            Record("StampMcdfHashes");
            return McdfStampNotes;
        }

        public Func<SceneActor, SceneMcdfOutcome>? McdfImport;

        public Task<SceneMcdfOutcome> ImportMcdf(
            object actor,
            SceneActor data,
            TimeSpan bound,
            CancellationToken cancellation)
        {
            Record($"ImportMcdf:{data.Name}");
            return Task.FromResult(
                McdfImport?.Invoke(data) ?? SceneMcdfOutcome.Ok());
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

        /// <summary>Ticks a companion body needs before it reads ready, so a
        /// test can assert the load WAITS rather than posing a body that has
        /// not built.</summary>
        public int CompanionReadyAfterPolls;

        public int CompanionReadyPolls;

        public Func<SceneActor, string?>? CompanionPoseFailure;

        public bool CompanionReady(object actor)
        {
            Record("CompanionReady");
            return ++CompanionReadyPolls > CompanionReadyAfterPolls;
        }

        public string? ArmCompanionPoseImport(
            object actor,
            SceneActor data,
            string description,
            Action<OperationReceipt> onReceipt)
        {
            Record($"ArmCompanionPoseImport:{data.Name}");
            if (CompanionPoseFailure?.Invoke(data) is { } refusal)
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

        /// <summary>What each actor's visibility was last written to, by
        /// actor name — the load's ordering is only half the fact, the value
        /// is the other half.</summary>
        public readonly Dictionary<string, bool> VisibleSet = new();

        public void SetActorVisibility(object actor, bool visible)
        {
            VisibleSet[((Token)actor).Name["actor:".Length..]] = visible;
            Record("SetActorVisibility");
        }

        public object? SpawnProp(SceneProp data, out string? detail)
        {
            Record($"SpawnProp:{data.Name}");
            detail = PropSpawnFailure?.Invoke(data);
            return detail is null ? new Token($"prop:{data.Name}") : null;
        }

        public object? SpawnOverlay(SceneOverlay data, out string? detail)
        {
            string name = data.Node?.Name ?? "Overlay";
            Record($"SpawnOverlay:{name}");
            detail = OverlayStageFailure?.Invoke(data);
            return detail is null ? new Token($"overlay:{name}") : null;
        }

        /// <summary>Which zone the fake session is standing in. It matches the
        /// territory <see cref="SceneWith"/> stamps, so a document's borrowed
        /// entries are attempted unless a test moves one of the two apart.
        /// </summary>
        public uint Territory = HomeTerritory;

        public uint CurrentTerritoryId()
        {
            Record("CurrentTerritoryId");
            return Territory;
        }

        public object? AdoptWorldObject(SceneWorldObject data, out string? detail)
        {
            Record($"AdoptWorldObject:{data.Path}");
            detail = WorldObjectAdoptFailure?.Invoke(data);
            return detail is null ? new Token($"world:{data.Path}") : null;
        }

        /// <summary>Recorded as a RELEASE and queued apart from
        /// <see cref="Destroyed"/>: the fake keeps the same distinction the
        /// restore contract makes, so a test cannot pass by destroying
        /// something the map owns.</summary>
        public void ReleaseWorldObject(object token)
        {
            Record($"ReleaseWorldObject:{((Token)token).Name}");
            Released.Enqueue(((Token)token).Name);
        }

        public readonly ConcurrentQueue<string> Released = new();

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

        /// <summary>The last world block the load stamped, so a test can
        /// assert what a scene actually asked the session for.</summary>
        public SceneWorld? AppliedWorld;

        public string? WorldFailure;

        public SceneWorld CaptureWorldState()
        {
            Record("CaptureWorldState");
            return new SceneWorld { IsWaterFrozen = true };
        }

        public string? ApplyWorld(SceneWorld world)
        {
            Record("ApplyWorld");
            AppliedWorld = world;
            return WorldFailure;
        }

        /// <summary>What the session is standing at, for a relative load. Null
        /// is a session with nobody to anchor on.</summary>
        public System.Numerics.Vector3? Origin = new(10f, 0f, 20f);

        public System.Numerics.Vector3? CurrentOrigin()
        {
            Record("CurrentOrigin");
            return Origin;
        }

        /// <summary>What a destroy-first sweep finds to remove.</summary>
        public SceneClearOutcome ClearResult = new(2, 1, 0, 3, 1);

        public SceneClearOutcome ClearScene()
        {
            Record("ClearScene");
            return ClearResult;
        }

        public void DestroyActor(object actor) => Destroy(actor);
        public void DestroyProp(object prop) => Destroy(prop);
        public void DestroyOverlay(object overlay) => Destroy(overlay);
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

    /// <summary>The zone every built document says it was captured in, and the
    /// one the fake session stands in by default. A borrowed map object is the
    /// only entity whose restore depends on the two agreeing, so both sides
    /// name this constant rather than a literal.</summary>
    private const uint HomeTerritory = 132u;

    private static SceneFile SceneWith(
        params SceneActor[] actors)
    {
        var scene = new SceneFile
        {
            SceneId = Guid.NewGuid(),
            TerritoryId = HomeTerritory,
            PlaceName = "Old Gridania",
        };
        scene.Actors.AddRange(actors);
        return scene;
    }

    private static SceneWorldObject WorldObject(
        string path, Vector3 mapPosition = default) =>
        new()
        {
            Key = Guid.NewGuid(),
            Path = path,
            MapPosition = mapPosition,
        };

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

        Assert.Equal(
            new[]
            {
                "ArmSceneCapture", "CaptureScene", "StampMcdfHashes", "WriteScene",
            },
            runtime.Calls);
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

    [Fact]
    public async Task A_save_waits_for_the_bone_refresh_before_it_writes()
    {
        // The whole point of the armed capture: a save that wrote while the
        // refresh was still outstanding would serialize a never-posed actor's
        // skeleton-build-time bones instead of the pose on screen.
        var runtime = new FakeRuntime { DeferCapture = true };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginSave("shot.poserscene").Success);
        await WaitFor(
            () => workflow.Progress!.Phase == ScenePhase.Capturing,
            "the save to reach its capture phase");

        Assert.DoesNotContain("CaptureScene", runtime.Calls);
        Assert.DoesNotContain("WriteScene", runtime.Calls);
        Assert.True(workflow.Busy);

        runtime.ReleaseCapture();
        await workflow.Drain;

        Assert.Equal(
            new[]
            {
                "ArmSceneCapture", "CaptureScene", "StampMcdfHashes", "WriteScene",
            },
            runtime.Calls);
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
    }

    [Fact]
    public async Task A_refusal_to_arm_the_refresh_fails_the_save_without_writing()
    {
        var runtime = new FakeRuntime
        {
            CaptureArmRefusal = "A pose import is applying.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginSave("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(new[] { "ArmSceneCapture" }, runtime.Calls);
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        Assert.Contains("A pose import is applying.", workflow.Receipt.Detail);
    }

    [Fact]
    public async Task A_refresh_that_never_lands_fails_the_save_within_its_bound()
    {
        // A framework thread that stopped ticking must not leave the save
        // parked forever — and must not fall back to writing stale bones.
        var runtime = new FakeRuntime { DeferCapture = true };
        using var workflow = new SceneWorkflow(runtime)
        {
            CaptureBound = TimeSpan.FromMilliseconds(100),
        };

        Assert.True(workflow.BeginSave("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(new[] { "ArmSceneCapture" }, runtime.Calls);
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        Assert.Contains("within its bound", workflow.Receipt.Detail);
    }

    private static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return;
            await Task.Delay(5);
        }
        Assert.Fail($"Timed out waiting for {what}.");
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
                "CaptureWorldState",
                "CaptureDefaultCameraState",
                "SpawnActor:Lead",
                "SpawnProp:Chair",
                // readiness barrier
                "ActorReady",
                // relationships
                "AttachCompanion:Lead",
                // visibility BEFORE the pose: an actor saved hidden must not
                // be hidden on top of the pose the loader just applied to it
                "SetActorVisibility",
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
                "ApplyActorGaze:Lead",
                // cameras, lights, environment last
                "ApplyDefaultCamera",
                "SetCameraTarget",
                "SetLiveCamera",
                "SpawnLight",
                "ApplyEnvironment",
                // The session-wide toggles ride with the environment, and run
                // even for a file that states none — the game's own behaviour
                // is what such a file asks for.
                "ApplyWorld",
            },
            runtime.Calls);
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
        Assert.Empty(runtime.Destroyed);
    }

    /// <summary>
    /// The regression this ordering exists for: hiding used to run in the
    /// presentation phase, AFTER the pose, and hiding used to tear the draw
    /// object down — so an actor saved hidden lost the pose the loader had
    /// just applied to it. Hiding is a fade now, but the load states the
    /// ordering itself so no later change to how an actor hides can undo it.
    /// </summary>
    [Fact]
    public async Task An_actor_saved_hidden_is_hidden_before_its_pose_is_applied()
    {
        var hidden = Actor("Lead", out _);
        hidden.Visible = false;
        var runtime = new FakeRuntime { ReadResult = SceneWith(hidden) };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        int hide = runtime.Calls.IndexOf("SetActorVisibility");
        int pose = runtime.Calls.IndexOf("ArmPoseImport:Lead");
        Assert.True(hide >= 0 && pose >= 0);
        Assert.True(hide < pose, "visibility must be written before the pose");
        Assert.False(runtime.VisibleSet["Lead"]);
    }

    // ── session-wide toggles ─────────────────────────────────────────────

    [Fact]
    public async Task A_scene_stating_no_world_toggles_releases_the_ones_the_session_holds()
    {
        // The absence of a world block IS a statement: the scene was taken
        // with the water running and the physics live, so a session holding
        // either must hand it back.
        var runtime = new FakeRuntime { ReadResult = SceneWith(Actor("Lead", out _)) };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.False(runtime.AppliedWorld!.IsWaterFrozen);
        Assert.False(runtime.AppliedWorld.IsPhysicsFrozen);
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
    }

    [Fact]
    public async Task A_saved_world_toggle_is_stamped_and_a_refused_one_is_named()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.World = new SceneWorld { IsWaterFrozen = true, IsPhysicsFrozen = true };

        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            WorldFailure =
                "The scene was restored except that the water freeze could not be hooked.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.True(runtime.AppliedWorld!.IsWaterFrozen);
        Assert.True(runtime.AppliedWorld.IsPhysicsFrozen);
        var refusal = Assert.Single(
            workflow.Progress!.Outcome!.Entities, entity => entity.Kind == "World");
        Assert.False(refusal.Restored);
        Assert.Contains("could not be hooked", refusal.Detail);
    }

    [Fact]
    public async Task A_rolled_back_load_hands_the_world_toggles_back()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.World = new SceneWorld { IsWaterFrozen = false };

        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            // A structural refusal: the actor cannot be spawned at all.
            ActorSpawnFailure = _ => "No object slot is free.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(OperationReceiptState.RolledBack, workflow.Receipt!.State);
        // The baseline the fake reported at admission, put back.
        Assert.True(runtime.AppliedWorld!.IsWaterFrozen);
    }

    // ── character files ──────────────────────────────────────────────────

    /// <summary>A character file is re-imported BEFORE anything that hangs off
    /// the actor's body, because the import redraws it and takes every
    /// skeleton with it.</summary>
    [Fact]
    public async Task A_saved_character_file_is_imported_before_the_body_is_used()
    {
        var lead = Actor("Lead", out _);
        lead.HasCompanionSlot = true;
        lead.CompanionKind = CompanionKind.Companion;
        lead.CompanionId = 4;
        lead.Mcdf = new SceneActorMcdf
        {
            Path = @"C:\files\friend.mcdf",
            FileName = "friend.mcdf",
        };

        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(lead),
            McdfImport = _ => SceneMcdfOutcome.Ok(),
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        var calls = runtime.Calls;
        int import = calls.IndexOf("ImportMcdf:Lead");
        Assert.True(import > calls.IndexOf("SpawnActor:Lead"));
        Assert.True(import < calls.IndexOf("AttachCompanion:Lead"));
        Assert.True(import < calls.IndexOf("ApplyActorAnimation:Lead"));
        Assert.True(import < calls.IndexOf("ArmPoseImport:Lead"));
        // The redraw rebuilt the skeletons, so readiness is re-established.
        Assert.True(
            calls.LastIndexOf("ActorReady") > import,
            "The load must wait for the redrawn skeletons before posing.");
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
        Assert.DoesNotContain(
            workflow.Progress!.Outcome!.Entities,
            entity => entity.Kind == "Character file");
    }

    [Fact]
    public async Task A_missing_character_file_is_a_named_refusal_not_a_silent_skip()
    {
        var lead = Actor("Lead", out _);
        lead.Mcdf = new SceneActorMcdf
        {
            Path = @"C:\files\gone.mcdf",
            FileName = "gone.mcdf",
        };

        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(lead),
            McdfImport = _ => SceneMcdfOutcome.Refused(
                "The character file 'gone.mcdf' is no longer at C:\\files\\gone.mcdf."),
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        // The actor itself still restores; the missing file is one named
        // refusal, and nothing rolls back.
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        Assert.Empty(runtime.Destroyed);
        var entities = workflow.Progress!.Outcome!.Entities;
        Assert.Contains(entities, entity =>
            entity is { Kind: "Actor", Name: "Lead", Restored: true });
        var refusal = Assert.Single(
            entities, entity => entity.Kind == "Character file");
        Assert.False(refusal.Restored);
        Assert.Contains("no longer at", refusal.Detail);
    }

    [Fact]
    public async Task A_changed_character_file_is_restored_with_the_divergence_named()
    {
        var lead = Actor("Lead", out _);
        lead.Mcdf = new SceneActorMcdf
        {
            Path = @"C:\files\friend.mcdf",
            FileName = "friend.mcdf",
            ContentHash = new string('A', 64),
        };

        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(lead),
            McdfImport = _ => SceneMcdfOutcome.Ok(
                "The character file 'friend.mcdf' has changed since this scene was saved."),
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        // Restored WITH a detail: the whole load still reads as applied.
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
        var note = Assert.Single(
            workflow.Progress!.Outcome!.Entities,
            entity => entity.Kind == "Character file");
        Assert.True(note.Restored);
        Assert.Contains("has changed", note.Detail);
    }

    [Fact]
    public async Task An_actor_with_no_saved_character_file_never_touches_the_importer()
    {
        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(Actor("Lead", out _)),
            McdfImport = _ => SceneMcdfOutcome.Silent,
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.DoesNotContain("ImportMcdf:Lead", runtime.Calls);
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
    }

    [Fact]
    public async Task An_unhashable_character_file_carries_its_note_into_the_save_outcome()
    {
        var runtime = new FakeRuntime
        {
            McdfStampNotes =
            {
                "Actor 'Lead''s character file 'friend.mcdf' could not be read while saving.",
            },
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginSave("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
        var note = Assert.Single(workflow.Progress!.Outcome!.Notes);
        Assert.Contains("could not be read while saving", note);
    }

    /// <summary>A companion is a posable body, not just an attachment: its own
    /// pose lands after its owner's, and only once its body has built.</summary>
    [Fact]
    public async Task A_companion_pose_waits_for_the_body_and_lands_after_its_owner()
    {
        var lead = Actor("Lead", out _);
        lead.HasCompanionSlot = true;
        lead.CompanionKind = CompanionKind.Companion;
        lead.CompanionId = 4;
        lead.CompanionPose = new PoseFile();

        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(lead),
            // The body is not ready on the first two polls; the barrier waits.
            CompanionReadyAfterPolls = 2,
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        var calls = runtime.Calls;
        Assert.Equal(
            calls.IndexOf("AttachCompanion:Lead") + 1,
            calls.IndexOf("CompanionReady"));
        Assert.True(
            calls.IndexOf("ArmCompanionPoseImport:Lead") >
            calls.IndexOf("PlaceActor:Lead"),
            "The companion pose must land after its owner's pose and placement.");
        // The barrier polled until the body answered, plus the arm's own check.
        Assert.True(runtime.CompanionReadyPolls >= 3);
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
    }

    [Fact]
    public async Task A_companion_pose_refusal_is_named_beside_a_restored_actor()
    {
        var lead = Actor("Lead", out _);
        lead.HasCompanionSlot = true;
        lead.CompanionKind = CompanionKind.Companion;
        lead.CompanionId = 4;
        lead.CompanionPose = new PoseFile();

        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(lead),
            CompanionPoseFailure = _ =>
                "The companion's skeleton had not built, so its pose was not restored.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        // Typed partial recovery: the actor itself stays restored and nothing
        // rolls back.
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        Assert.Empty(runtime.Destroyed);
        var entities = workflow.Progress!.Outcome!.Entities;
        Assert.Contains(entities, entity =>
            entity is { Kind: "Actor", Name: "Lead", Restored: true });
        var refusal = Assert.Single(
            entities, entity => entity.Kind == "Companion");
        Assert.False(refusal.Restored);
        Assert.Contains("skeleton had not built", refusal.Detail);
    }

    [Fact]
    public async Task An_actor_with_no_saved_companion_pose_never_waits_on_a_body()
    {
        var lead = Actor("Lead", out _);
        lead.HasCompanionSlot = true;
        lead.CompanionKind = CompanionKind.Companion;
        lead.CompanionId = 4;

        var runtime = new FakeRuntime { ReadResult = SceneWith(lead) };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.DoesNotContain("CompanionReady", runtime.Calls);
        Assert.DoesNotContain("ArmCompanionPoseImport:Lead", runtime.Calls);
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
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

    // ── overlays ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Staged_overlays_are_restored_with_the_rest_of_the_scene()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.Overlays =
        [
            new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new Poser.Domain.Presentation.OverlayNodeState
                {
                    Name = "Opening line",
                },
            },
        ];
        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Contains("SpawnOverlay:Opening line", runtime.Calls);
        Assert.Contains(
            workflow.Progress!.Outcome!.Entities,
            entity => entity is
                { Kind: "Overlay", Name: "Opening line", Restored: true });
    }

    /// <summary>A scene the codec wrote before overlay nodes existed carries
    /// no list at all, and the load must not go looking for one.</summary>
    [Fact]
    public async Task A_scene_with_no_overlay_list_stages_none()
    {
        var runtime = new FakeRuntime { ReadResult = SceneWith(Actor("Lead", out _)) };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.DoesNotContain(
            runtime.Calls, call => call.StartsWith("SpawnOverlay"));
    }

    [Fact]
    public async Task A_refused_overlay_leaves_the_rest_of_the_shot_standing()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.Overlays =
        [
            new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new Poser.Domain.Presentation.OverlayNodeState
                {
                    Name = "Opening line",
                },
            },
        ];
        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            OverlayStageFailure = _ => "The game's UI would not take it.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Empty(runtime.Destroyed);
        Assert.Contains("ArmPoseImport:Lead", runtime.Calls);
        Assert.Contains(
            workflow.Progress!.Outcome!.Entities,
            entity => entity is { Kind: "Overlay", Restored: false });
    }

    /// <summary>A structural refusal must take the staged nodes down with
    /// everything else: a rolled-back load may not leave a dialogue box
    /// hanging over an empty scene.</summary>
    [Fact]
    public async Task A_rolled_back_load_takes_its_staged_overlays_down()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.Overlays =
        [
            new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new Poser.Domain.Presentation.OverlayNodeState
                {
                    Name = "Opening line",
                },
            },
        ];
        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);
        // Cancel once the node is staged: the commit re-guard must roll back
        // rather than commit a half-applied shot.
        runtime.AfterCall = call =>
        {
            if (call == "SpawnOverlay:Opening line")
                workflow.Cancel();
        };

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(OperationReceiptState.Cancelled, workflow.Receipt!.State);
        // Overlays go down before the props and actors they were staged over.
        Assert.Equal(
            new[] { "overlay:Opening line", "actor:Lead" },
            runtime.Destroyed.ToArray());
    }

    // -- borrowed map objects ---------------------------------------------

    /// <summary>Loading in the SAME zone takes the map's objects back and puts
    /// them where the file left them.</summary>
    [Fact]
    public async Task Borrowed_map_objects_are_taken_back_in_the_same_territory()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.WorldObjects = [WorldObject("bg/a.mdl", new Vector3(1f, 2f, 3f))];
        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Contains("AdoptWorldObject:bg/a.mdl", runtime.Calls);
        Assert.Contains(
            workflow.Progress!.Outcome!.Entities,
            entity => entity is { Kind: "World object", Restored: true });
    }

    /// <summary>THE zone rule. A borrowed object's identity is only meaningful
    /// where it was taken, so a load anywhere else refuses every entry BY NAME
    /// and never reaches for whatever shares its path in this zone.</summary>
    [Fact]
    public async Task Borrowed_map_objects_are_refused_by_name_in_another_territory()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.WorldObjects = [WorldObject("bg/a.mdl", new Vector3(1f, 2f, 3f))];
        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            Territory = HomeTerritory + 1,
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.DoesNotContain(
            runtime.Calls, call => call.StartsWith("AdoptWorldObject"));
        Assert.Contains(
            workflow.Progress!.Outcome!.Notes,
            note => note.Contains("Old Gridania") && note.Contains("left alone"));
        // The rest of the scene still lands: a borrowed entry is never
        // structural.
        Assert.Contains("ArmPoseImport:Lead", runtime.Calls);
    }

    /// <summary>No territory to read is not "close enough": it refuses for the
    /// same reason a mismatch does.</summary>
    [Fact]
    public async Task Borrowed_map_objects_are_refused_when_there_is_no_territory()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.WorldObjects = [WorldObject("bg/a.mdl")];
        var runtime = new FakeRuntime { ReadResult = scene, Territory = 0u };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.DoesNotContain(
            runtime.Calls, call => call.StartsWith("AdoptWorldObject"));
    }

    /// <summary>A scene written before map objects could be borrowed carries no
    /// list, and the load must not even ask where it is.</summary>
    [Fact]
    public async Task A_scene_with_no_borrowed_list_never_reads_the_territory()
    {
        var runtime = new FakeRuntime { ReadResult = SceneWith(Actor("Lead", out _)) };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.DoesNotContain("CurrentTerritoryId", runtime.Calls);
        Assert.DoesNotContain(
            runtime.Calls, call => call.StartsWith("AdoptWorldObject"));
    }

    [Fact]
    public async Task An_object_that_is_no_longer_there_leaves_the_rest_standing()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.WorldObjects = [WorldObject("bg/a.mdl")];
        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            WorldObjectAdoptFailure = _ => "It is not standing there any more.",
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Empty(runtime.Destroyed);
        Assert.Empty(runtime.Released);
        Assert.Contains("ArmPoseImport:Lead", runtime.Calls);
        Assert.Contains(
            workflow.Progress!.Outcome!.Entities,
            entity => entity is { Kind: "World object", Restored: false });
    }

    /// <summary>THE ROLLBACK EDGE, and the one that matters most: a load that
    /// is undone must give the map its objects back — RELEASED, never
    /// destroyed — and it must do so before it tears anything else down.
    /// </summary>
    [Fact]
    public async Task A_rolled_back_load_gives_the_map_its_objects_back()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.WorldObjects = [WorldObject("bg/a.mdl")];
        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);
        runtime.AfterCall = call =>
        {
            if (call == "AdoptWorldObject:bg/a.mdl")
                workflow.Cancel();
        };

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.Equal(OperationReceiptState.Cancelled, workflow.Receipt!.State);
        Assert.Equal(new[] { "world:bg/a.mdl" }, runtime.Released.ToArray());
        // Never destroyed: the map owns it.
        Assert.DoesNotContain("world:bg/a.mdl", runtime.Destroyed);
    }

    // -- load options: the import matrix --------------------------------

    /// <summary>Every category populated, so a skip is visible as an absence.
    /// </summary>
    private static SceneFile FullScene(out Guid leadKey)
    {
        var lead = Actor("Lead", out leadKey);
        var scene = SceneWith(lead);
        scene.Origin = new System.Numerics.Vector3(1f, 2f, 3f);
        scene.Props.Add(new SceneProp
        {
            Key = Guid.NewGuid(),
            Name = "Chair",
            Transform = new LightFile.TransformData
            {
                Position = new System.Numerics.Vector3(5f, 0f, 5f),
                Rotation = System.Numerics.Quaternion.Identity,
                Scale = System.Numerics.Vector3.One,
            },
        });
        scene.Lights.Add(new SceneLight
        {
            Key = Guid.NewGuid(),
            Light = new LightFile
            {
                Name = "Key",
                Transform = new LightFile.TransformData
                {
                    Position = new System.Numerics.Vector3(7f, 1f, 7f),
                    Rotation = System.Numerics.Quaternion.Identity,
                    Scale = System.Numerics.Vector3.One,
                },
            },
        });
        scene.Cameras.Add(new SceneCamera
        {
            Key = Guid.NewGuid(),
            Camera = new CameraFile
            {
                Name = "Free",
                Kind = Poser.Domain.Scene.CameraKind.Free,
                Position = new System.Numerics.Vector3(9f, 2f, 9f),
            },
            IsLive = true,
        });
        scene.Overlays =
        [
            new SceneOverlay
            {
                Key = Guid.NewGuid(),
                Node = new Poser.Domain.Presentation.OverlayNodeState
                {
                    Name = "Line",
                },
            },
        ];
        scene.Environment = new SceneEnvironment();
        return scene;
    }

    [Fact]
    public void A_load_that_includes_nothing_is_refused_at_admission()
    {
        var runtime = new FakeRuntime { ReadResult = FullScene(out _) };
        using var workflow = new SceneWorkflow(runtime);

        var result = workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions
            {
                IncludeActors = false,
                IncludeProps = false,
                IncludeLights = false,
                IncludeCameras = false,
                IncludeEnvironment = false,
                IncludeOverlays = false,
            });

        Assert.False(result.Success);
        // Nothing was read, so nothing native could have run either.
        Assert.Empty(runtime.Calls);
    }

    [Fact]
    public async Task Default_options_load_exactly_what_no_options_load()
    {
        var withDefaults = new FakeRuntime { ReadResult = FullScene(out _) };
        using (var workflow = new SceneWorkflow(withDefaults))
        {
            Assert.True(workflow
                .BeginLoad("shot.poserscene", SceneLoadOptions.Default).Success);
            await workflow.Drain;
        }

        var without = new FakeRuntime { ReadResult = FullScene(out _) };
        using (var workflow = new SceneWorkflow(without))
        {
            Assert.True(workflow.BeginLoad("shot.poserscene").Success);
            await workflow.Drain;
        }

        Assert.Equal(without.Calls, withDefaults.Calls);
    }

    [Fact]
    public async Task Clearing_first_sweeps_the_session_before_anything_spawns()
    {
        var runtime = new FakeRuntime { ReadResult = FullScene(out _) };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions { ClearExistingScene = true }).Success);
        await workflow.Drain;

        var clear = runtime.Calls.IndexOf("ClearScene");
        Assert.True(clear >= 0);
        Assert.True(clear < runtime.Calls.IndexOf("SpawnActor:Lead"));
        Assert.True(clear < runtime.Calls.IndexOf("CaptureEnvironmentState"));
        // The sweep is outside the rollback ledger, so the outcome has to say
        // what it cost.
        Assert.Contains(
            workflow.Progress!.Outcome!.Notes,
            note => note.Contains("Cleared the session first") &&
                note.Contains("does not bring them back"));
    }

    [Fact]
    public async Task A_load_that_does_not_clear_never_sweeps()
    {
        var runtime = new FakeRuntime { ReadResult = FullScene(out _) };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.DoesNotContain("ClearScene", runtime.Calls);
    }

    [Fact]
    public async Task An_empty_sweep_says_nothing_about_being_emptied()
    {
        var runtime = new FakeRuntime
        {
            ReadResult = FullScene(out _),
            ClearResult = default,
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions { ClearExistingScene = true }).Success);
        await workflow.Drain;

        Assert.Contains("ClearScene", runtime.Calls);
        Assert.DoesNotContain(
            workflow.Progress!.Outcome!.Notes,
            note => note.Contains("Cleared the session first"));
    }

    [Fact]
    public async Task Excluding_a_category_skips_every_phase_that_category_owns()
    {
        var runtime = new FakeRuntime { ReadResult = FullScene(out _) };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions
            {
                IncludeProps = false,
                IncludeLights = false,
                IncludeOverlays = false,
                IncludeEnvironment = false,
            }).Success);
        await workflow.Drain;

        Assert.Contains("SpawnActor:Lead", runtime.Calls);
        Assert.DoesNotContain("SpawnProp:Chair", runtime.Calls);
        Assert.DoesNotContain("SpawnLight", runtime.Calls);
        Assert.DoesNotContain("SpawnOverlay:Line", runtime.Calls);
        Assert.DoesNotContain("ApplyEnvironment", runtime.Calls);
        // The session-wide toggles belong to the environment category, so a
        // load that leaves it out leaves them as the user set them.
        Assert.DoesNotContain("ApplyWorld", runtime.Calls);
        // What the file HAS and this load left alone is said once, per
        // category, rather than left to look like a short scene.
        var notes = workflow.Progress!.Outcome!.Notes;
        Assert.Contains(notes, note => note.Contains("1 props were not loaded"));
        Assert.Contains(notes, note => note.Contains("1 lights were not loaded"));
        Assert.Contains(notes, note => note.Contains("1 overlays were not loaded"));
        Assert.Contains(
            notes, note => note.Contains("environment was not loaded"));
    }

    [Fact]
    public async Task Excluding_actors_skips_every_phase_that_hangs_off_a_body()
    {
        var scene = FullScene(out _);
        scene.Actors[0].CompanionKind = CompanionKind.Companion;
        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions { IncludeActors = false }).Success);
        await workflow.Drain;

        Assert.DoesNotContain("SpawnActor:Lead", runtime.Calls);
        Assert.DoesNotContain("ArmPoseImport:Lead", runtime.Calls);
        Assert.DoesNotContain("AttachCompanion:Lead", runtime.Calls);
        // The rest of the scene still lands.
        Assert.Contains("SpawnProp:Chair", runtime.Calls);
        Assert.Contains("SpawnLight", runtime.Calls);
    }

    [Fact]
    public async Task A_light_attached_to_an_excluded_actor_is_refused_by_name()
    {
        var scene = FullScene(out var leadKey);
        scene.Lights[0].Attachment = new SceneBoneAttachment
        {
            ActorKey = leadKey,
            BoneName = "j_kubi",
        };
        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions { IncludeActors = false }).Success);
        await workflow.Drain;

        // Never a silent world-space spawn: the light does not appear, and the
        // outcome says which actor it was hanging on.
        Assert.DoesNotContain("SpawnLight", runtime.Calls);
        Assert.Contains(
            workflow.Progress!.Outcome!.Entities,
            entity => entity is
            {
                Kind: "Light", Name: "Key", Restored: false,
            } &&
            entity.Detail!.Contains("this load did not restore"));
    }

    [Fact]
    public async Task A_camera_following_an_excluded_actor_keeps_the_camera()
    {
        var scene = FullScene(out var leadKey);
        scene.Cameras[0].TargetActorKey = leadKey;
        scene.Cameras[0].TargetActorName = "Lead";
        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions { IncludeActors = false }).Success);
        await workflow.Drain;

        Assert.DoesNotContain(
            runtime.Calls, call => call.StartsWith("SetCameraTarget"));
        Assert.Contains(
            workflow.Progress!.Outcome!.Entities,
            entity => entity is { Kind: "Camera", Restored: false } &&
                entity.Detail!.Contains("did not restore"));
    }

    // -- load options: relative placement --------------------------------

    [Fact]
    public async Task A_relative_load_moves_every_world_position_by_one_offset()
    {
        var scene = FullScene(out _);
        scene.Actors[0].ModelTransform = new LightFile.TransformData
        {
            Position = new System.Numerics.Vector3(4f, 0f, 4f),
            Rotation = System.Numerics.Quaternion.Identity,
            Scale = System.Numerics.Vector3.One,
        };
        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            // Origin (1,2,3) -> here (11,2,23): an offset of (10,0,20).
            Origin = new System.Numerics.Vector3(11f, 2f, 23f),
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions { PlaceRelativeToCurrentOrigin = true }).Success);
        await workflow.Drain;

        var offset = new System.Numerics.Vector3(10f, 0f, 20f);
        Assert.Equal(
            new System.Numerics.Vector3(4f, 0f, 4f) + offset,
            scene.Actors[0].ModelTransform!.Position);
        Assert.Equal(
            new System.Numerics.Vector3(5f, 0f, 5f) + offset,
            scene.Props[0].Transform.Position);
        Assert.Equal(
            new System.Numerics.Vector3(7f, 1f, 7f) + offset,
            scene.Lights[0].Light!.Transform.Position);
        Assert.Equal(
            new System.Numerics.Vector3(9f, 2f, 9f) + offset,
            scene.Cameras[0].Camera!.Position);
        Assert.Contains(
            workflow.Progress!.Outcome!.Notes,
            note => note.Contains("relative to where you are standing"));
    }

    [Fact]
    public async Task An_absolute_load_moves_nothing()
    {
        var scene = FullScene(out _);
        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            Origin = new System.Numerics.Vector3(500f, 0f, 500f),
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad("shot.poserscene").Success);
        await workflow.Drain;

        Assert.DoesNotContain("CurrentOrigin", runtime.Calls);
        Assert.Equal(
            new System.Numerics.Vector3(5f, 0f, 5f),
            scene.Props[0].Transform.Position);
    }

    [Fact]
    public async Task A_relative_load_of_an_anchorless_file_refuses_before_it_starts()
    {
        var scene = FullScene(out _);
        scene.Origin = null;
        var runtime = new FakeRuntime { ReadResult = scene };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions { PlaceRelativeToCurrentOrigin = true }).Success);
        await workflow.Drain;

        // Read and asked where we are, then stopped: nothing native ran, so
        // there is nothing to roll back and the state is a plain Failed.
        Assert.Equal(new[] { "ReadScene", "CurrentOrigin" }, runtime.Calls);
        Assert.Equal(OperationReceiptState.Failed, workflow.Receipt!.State);
        Assert.Contains("records no origin", workflow.Progress!.Outcome!.Detail);
    }

    [Fact]
    public async Task A_relative_load_with_nobody_to_anchor_on_refuses()
    {
        var runtime = new FakeRuntime
        {
            ReadResult = FullScene(out _),
            Origin = null,
        };
        using var workflow = new SceneWorkflow(runtime);

        Assert.True(workflow.BeginLoad(
            "shot.poserscene",
            new SceneLoadOptions { PlaceRelativeToCurrentOrigin = true }).Success);
        await workflow.Drain;

        Assert.Equal(new[] { "ReadScene", "CurrentOrigin" }, runtime.Calls);
        Assert.Contains(
            "nobody to place the scene relative to",
            workflow.Progress!.Outcome!.Detail);
    }

    [Fact]
    public void A_bone_attached_light_and_an_orbit_camera_stay_where_they_are()
    {
        var scene = FullScene(out var leadKey);
        scene.Lights[0].Attachment = new SceneBoneAttachment
        {
            ActorKey = leadKey,
            BoneName = "j_kubi",
        };
        scene.Cameras[0].Camera!.Kind = Poser.Domain.Scene.CameraKind.Game;

        Assert.Null(SceneRelativePlacement.Rebase(
            scene, new System.Numerics.Vector3(11f, 2f, 23f)));

        // An attached light's position is its bone's, and an orbit camera
        // orbits a target that moved with the scene; moving either again would
        // move it twice.
        Assert.Equal(
            new System.Numerics.Vector3(7f, 1f, 7f),
            scene.Lights[0].Light!.Transform.Position);
        Assert.Equal(
            new System.Numerics.Vector3(9f, 2f, 9f),
            scene.Cameras[0].Camera!.Position);
    }

    [Fact]
    public void A_relative_load_moves_the_points_an_actor_is_looking_at()
    {
        var scene = FullScene(out _);
        scene.Actors[0].Gaze = new SceneActorGaze
        {
            Position = new System.Numerics.Vector3(1f, 1f, 1f),
            EyesPosition = new System.Numerics.Vector3(2f, 2f, 2f),
            HeadPosition = new System.Numerics.Vector3(3f, 3f, 3f),
            BodyPosition = new System.Numerics.Vector3(4f, 4f, 4f),
        };

        Assert.Null(SceneRelativePlacement.Rebase(
            scene, new System.Numerics.Vector3(11f, 2f, 23f)));

        var offset = new System.Numerics.Vector3(10f, 0f, 20f);
        var gaze = scene.Actors[0].Gaze!;
        Assert.Equal(new System.Numerics.Vector3(1f, 1f, 1f) + offset, gaze.Position);
        Assert.Equal(new System.Numerics.Vector3(2f, 2f, 2f) + offset, gaze.EyesPosition);
        Assert.Equal(new System.Numerics.Vector3(3f, 3f, 3f) + offset, gaze.HeadPosition);
        Assert.Equal(new System.Numerics.Vector3(4f, 4f, 4f) + offset, gaze.BodyPosition);
    }

    // -- disposal --------------------------------------------------------

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
