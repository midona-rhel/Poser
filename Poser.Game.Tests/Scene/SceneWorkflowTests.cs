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

        /// <summary>Messages returned when capture cannot start.</summary>
        public readonly Queue<string> TransientArmRefusals = new();

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
            if (TransientArmRefusals.Count > 0)
                return TransientArmRefusals.Dequeue();
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

        /// <summary>The TERMINAL receipt detail a pose import ends on, when a
        /// test wants an admitted-then-failed import rather than an admitted
        /// one. Distinct from <see cref="PoseFailure"/>, which refuses at the
        /// arm and never publishes a receipt at all.</summary>
        public Func<SceneActor, string?>? PoseTerminalFailure;

        /// <summary>
        /// Publishes the PENDING acknowledgement before the terminal receipt,
        /// exactly as the real engine does (<c>PoseImportCapture.Reserve</c>
        /// publishes a Pending receipt whose Detail is the import DESCRIPTION,
        /// synchronously, from inside the arm). A workflow that latches the
        /// first receipt it is handed therefore reports every pose import
        /// failed with its own label — issue #41's reported defect.
        /// </summary>
        private void PublishPoseReceipts(
            string description, Action<OperationReceipt> onReceipt, string? terminal)
        {
            var id = Guid.NewGuid();
            var target = new Poser.Domain.Identity.ActorId(Guid.NewGuid(), 1);
            onReceipt(OperationReceipt.Pending(
                id, OperationEpoch.First, Session!.Value, target, description));
            onReceipt(terminal is null
                ? OperationReceipt.Applied(
                    id, OperationEpoch.First, Session!.Value, target)
                : OperationReceipt.Failed(
                    id, OperationEpoch.First, Session!.Value, target, terminal));
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
            PublishPoseReceipts(
                description, onReceipt, PoseTerminalFailure?.Invoke(data));
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
            PublishPoseReceipts(description, onReceipt, null);
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
            object? camera, object targetActor, string displayName,
            bool targetLocked)
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
    public async Task Save_and_load_success_preserve_order_phase_and_final_objects()
    {
        var saveRuntime = new FakeRuntime();
        using (var save = new SceneWorkflow(saveRuntime))
        {
            Assert.True(save.BeginSave("shot.poserscene", "A shot").Success);
            await save.Drain;
            Assert.Equal(new[] { "ArmSceneCapture", "CaptureScene", "StampMcdfHashes", "WriteScene" }, saveRuntime.Calls);
            Assert.Equal(OperationReceiptState.Applied, save.Receipt!.State);
        }

        var lead = Actor("Lead", out var leadKey);
        lead.CompanionKind = CompanionKind.Companion;
        lead.CompanionId = 4;
        var scene = SceneWith(lead);
        scene.Props.Add(new SceneProp { Key = Guid.NewGuid(), Name = "Chair" });
        scene.Lights.Add(new SceneLight { Key = Guid.NewGuid(), Light = new LightFile { Name = "Key" } });
        scene.Cameras.Add(new SceneCamera { Key = Guid.NewGuid(), Camera = new CameraFile { Name = "Default" }, IsDefault = true, IsLive = true, TargetActorKey = leadKey, TargetActorName = "Lead" });
        scene.Environment = new SceneEnvironment();

        var runtime = new FakeRuntime { ReadResult = scene };
        using var load = new SceneWorkflow(runtime);
        Assert.True(load.BeginLoad("shot.poserscene").Success);
        await load.Drain;
        Assert.Equal(new[] { "ReadScene", "CaptureEnvironmentState", "CaptureWorldState", "CaptureDefaultCameraState", "SpawnActor:Lead", "SpawnProp:Chair", "ActorReady", "AttachCompanion:Lead", "SetActorVisibility", "ApplyActorAnimation:Lead", "ArmPoseImport:Lead", "PlaceActor:Lead", "ApplyActorGaze:Lead", "ApplyDefaultCamera", "SetCameraTarget", "SetLiveCamera", "SpawnLight", "ApplyEnvironment", "ApplyWorld" }, runtime.Calls);
        Assert.Empty(runtime.Destroyed);
        Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);
    }

    [Fact]
    public async Task Load_failure_rolls_back_reverse_and_session_replacement_cancels_exactly()
    {
        var scene = SceneWith(Actor("Lead", out _), Actor("Second", out _));
        var failedRuntime = new FakeRuntime { ReadResult = scene, ActorSpawnFailure = a => a.Name == "Second" ? "no free slot" : null };
        using (var failed = new SceneWorkflow(failedRuntime))
        {
            Assert.True(failed.BeginLoad("shot.poserscene").Success);
            await failed.Drain;
            Assert.Equal(new[] { "actor:Lead" }, failedRuntime.Destroyed.ToArray());
            Assert.Equal(OperationReceiptState.RolledBack, failed.Receipt!.State);
            Assert.Contains("no free slot", failed.Progress!.Outcome!.Detail);
        }

        var replacedRuntime = new FakeRuntime { ReadResult = SceneWith(Actor("Lead", out _)) };
        replacedRuntime.AfterCall = call => { if (call == "SpawnActor:Lead") replacedRuntime.Session = SessionGeneration.New(); };
        using var replaced = new SceneWorkflow(replacedRuntime);
        Assert.True(replaced.BeginLoad("shot.poserscene").Success);
        await replaced.Drain;
        Assert.Equal(new[] { "actor:Lead" }, replacedRuntime.Destroyed.ToArray());
        Assert.Equal(OperationReceiptState.Cancelled, replaced.Receipt!.State);
        Assert.Contains("session ended", replaced.Progress!.Outcome!.Detail);
    }

    // ── issue #41: the pose import's pending acknowledgement ─────────────

    /// <summary>
    /// The reported defect. The engine publishes a Pending receipt whose
    /// Detail is the import DESCRIPTION before it publishes anything terminal;
    /// a load that answered on the first receipt reported every posed actor
    /// failed, with <c>Scene pose: &lt;actor&gt;</c> as the only stated reason.
    /// </summary>
    [Fact]
    public async Task Pose_import_answers_on_the_terminal_receipt_not_the_pending_label()
    {
        var runtime = new FakeRuntime { ReadResult = SceneWith(Actor("Midona Rhel", out _)) };
        using var load = new SceneWorkflow(runtime);
        Assert.True(load.BeginLoad("shot.poserscene").Success);
        await load.Drain;

        Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);
        var outcome = load.Progress!.Outcome!;
        Assert.DoesNotContain(outcome.Entities, entity => !entity.Restored);
        Assert.DoesNotContain(
            outcome.Entities,
            entity => entity.Detail?.Contains("Scene pose:") == true);
    }

    /// <summary>An import that IS admitted and then fails terminally still
    /// reports the terminal reason, and only that one — the pending label must
    /// not survive as a fallback detail.</summary>
    [Fact]
    public async Task Pose_import_reports_the_terminal_reason_for_an_admitted_failure()
    {
        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(Actor("Midona Rhel", out _)),
            PoseTerminalFailure = _ => "The pose import rolled itself back.",
        };
        using var load = new SceneWorkflow(runtime);
        Assert.True(load.BeginLoad("shot.poserscene").Success);
        await load.Drain;

        var refusal = Assert.Single(
            load.Progress!.Outcome!.Entities, entity => !entity.Restored);
        Assert.Equal("Actor", refusal.Kind);
        Assert.Equal("Midona Rhel", refusal.Name);
        Assert.Equal("The pose import rolled itself back.", refusal.Detail);
    }

    /// <summary>Every refused entity leaves the terminal publication with a
    /// next step, and no restored one carries one. A row that only restates
    /// the entity's own name is the reported defect.</summary>
    [Fact]
    public async Task Every_refused_entity_carries_a_reason_and_a_next_step()
    {
        var scene = SceneWith(Actor("Midona Rhel", out _));
        scene.Props.Add(new SceneProp { Key = Guid.NewGuid(), Name = "Chair" });
        var runtime = new FakeRuntime
        {
            ReadResult = scene,
            PropSpawnFailure = _ => "No free spawn slot.",
            PoseTerminalFailure = _ => "The pose import rolled itself back.",
        };
        using var load = new SceneWorkflow(runtime);
        Assert.True(load.BeginLoad("shot.poserscene").Success);
        await load.Drain;

        var entities = load.Progress!.Outcome!.Entities;
        foreach (var entity in entities.Where(entity => !entity.Restored))
        {
            Assert.False(string.IsNullOrWhiteSpace(entity.Detail));
            Assert.False(string.IsNullOrWhiteSpace(entity.Remedy));
            Assert.NotEqual(entity.Name, entity.Detail);
        }
        Assert.Contains(entities, entity => entity.Kind == "Object" && !entity.Restored);
        Assert.Contains(entities, entity => entity.Kind == "Actor" && !entity.Restored);
        Assert.DoesNotContain(
            entities, entity => entity.Restored && entity.Remedy != null);
    }
}
