using Poser.Scene;
using System.Collections.Concurrent;
using System.Numerics;
using Poser.Domain.Operations;
using Poser.Domain.Companions;
using Poser.Files;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Domain.Scene;

namespace Poser.Application.Tests.Scene;

/// <summary>
/// The scene transaction's behavior contract, driven through the
/// <see cref="ISceneRuntime"/> seam so every assertion is about admission,
/// ordering, guards, rollback and terminal truthfulness — never about native
/// state. The fake runs framework actions inline, so an awaited
/// <see cref="SceneWorkflow.Drain"/> is an exact barrier.
/// </summary>
public sealed class SceneWorkflowTests
{
    private sealed class ParentRuntime : IParentingRuntime
    {
        public Dictionary<SelectionId, PoseTransform> Values = new();
        public bool CanParent(SelectionId child) => true;
        public PoseTransform? Read(SelectionId id) => Values.GetValueOrDefault(id, PoseTransform.Identity);
        public bool Write(SelectionId id, PoseTransform world) { Values[id] = world; return true; }
        public SelectionId? ResolveBone(ActorId actor, PoseSlot slot, string name, int partial) => null;
    }

    [Fact]
    public async Task Parenting_load_save_and_redo_resolve_new_entities_without_live_ids_in_the_file()
    {
        var parentKey = Guid.NewGuid(); var childKey = Guid.NewGuid();
        var document = SceneWith();
        document.Lights.Add(new() { Key = parentKey, Light = new() { Name = "Parent" } });
        document.Lights.Add(new() { Key = childKey, Light = new() { Name = "Child" } });
        var offset = new PoseTransform(new(2, 3, 4), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, .3f), new(2));
        document.Parents = [new() { Child = new() { Kind = "light", Key = childKey },
            Target = new() { Kind = "light", Key = parentKey }, Offset = offset }];
        var runtime = new FakeRuntime { ReadResult = document };
        var groups = new SceneGroups(); var state = new GroupTransformState();
        using var coordinator = new GroupTransformCoordinator(new(new SelectionSession()), groups, state, new EmptyGroupSource());
        var history = new TransformHistory(); var native = new ParentRuntime();
        var parenting = new TransformParenting(native, history, new(history));
        using var workflow = new SceneWorkflow(runtime, new FakeDocuments(runtime), history: history,
            structure: new SceneStructure(groups, coordinator, state), parenting: parenting);
        Assert.True(workflow.BeginLoad("parents.xivs").Success); await workflow.Drain;
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
        var first = Assert.Single(parenting.Capture());
        Assert.Equal(offset, first.Value.Offset);
        Assert.NotEqual(first.Key, first.Value.Target);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo()); history.CommitUndo(step);
        Assert.Empty(parenting.Capture()); // #433: rollback takes its links with it.
        Assert.True(step.Redo()); history.CommitRedo(step); await workflow.Drain;
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
        parenting.Evaluate();
        var currentIds = runtime.SpawnedLightTokens.TakeLast(2).Select(t => runtime.ResolveSceneEntity(t)!.Value).ToArray();
        Assert.Equal(currentIds[0], parenting.Read(currentIds[1])!.Target);
        runtime.CapturedScene = () =>
        {
            var saved = SceneWith();
            foreach (var id in currentIds) saved.Lights.Add(new() { Key = id.Light!.Value.LogicalId, Light = new() { Name = "Saved" } });
            return saved;
        };
        Assert.True(workflow.BeginSave("saved.xivs").Success); await workflow.Drain;
        Assert.Equal(OperationReceiptState.Applied, workflow.Receipt!.State);
        var link = Assert.Single(runtime.Captured!.Parents!);
        Assert.Equal(currentIds[0].Light!.Value.LogicalId, link.Target.Key);
        Assert.Equal(currentIds[1].Light!.Value.LogicalId, link.Child.Key);
        Assert.Equal(offset, link.Offset);
    }

    [Fact]
    public async Task Headless_load_restores_nested_groups_before_completion_and_undo_removes_only_its_groups()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var third = Guid.NewGuid();
        var parent = Guid.NewGuid();
        var child = Guid.NewGuid();
        var document = SceneWith();
        document.Lights.Add(new() { Key = first, Light = new() { Name = "A" } });
        document.Lights.Add(new() { Key = second, Light = new() { Name = "B" } });
        document.Lights.Add(new() { Key = third, Light = new() { Name = "C" } });
        var pose = new PoseTransform(Vector3.One, Quaternion.Identity, Vector3.One);
        document.Groups = [new() { Key = parent, Name = "Parent",
                Members = [new() { Kind = "light", Key = third }] },
            new() { Key = child, Name = "Child", Parent = parent,
                Members = [new() { Kind = "light", Key = first }, new() { Kind = "light", Key = second }],
                Transform = new() { Members = [
                    new() { Member = new() { Kind = "light", Key = first }, Initial = pose, Expected = pose },
                    new() { Member = new() { Kind = "light", Key = second }, Initial = pose, Expected = pose }] } }];
        document.RootOrder = [new() { Kind = "group", Key = parent }];
        var runtime = new FakeRuntime { ReadResult = document };
        var groups = new SceneGroups();
        var existing = groups.Create("Existing", [], allowThin: true)!;
        var state = new GroupTransformState();
        using var coordinator = new GroupTransformCoordinator(new(new SelectionSession()), groups, state, new EmptyGroupSource());
        var structure = new SceneStructure(groups, coordinator, state);
        var history = new TransformHistory();
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime), history: history, structure: structure);
        for (int cycle = 0; cycle < 2; cycle++)
        {
            if (cycle == 0) Assert.True(load.BeginLoad("groups.xivs").Success);
            else
            {
                var redo = Assert.IsType<JournalStep>(history.PeekRedo());
                Assert.True(redo.Redo());
                history.CommitRedo(redo);
            }
            await load.Drain;
            Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);
            var importedParent = Assert.Single(groups.All, group => group.Name == "Parent");
            var importedChild = Assert.Single(groups.All, group => group.Name == "Child");
            Assert.Equal(importedParent.Id, importedChild.ParentId);
            Assert.Equal(2, importedChild.Members.Count);
            var baseline = Assert.IsType<GroupTransformSnapshot>(state.NamedSnapshot(importedChild.Id));
            Assert.All(baseline.Expected.Values, value => Assert.Equal(pose, value));
            Assert.Equal(importedParent.Id, groups.RootOrder[^1].GroupId);
            runtime.CapturedScene = () =>
            {
                var captured = SceneWith();
                foreach (var token in runtime.SpawnedLightTokens.TakeLast(3))
                    captured.Lights.Add(new() { Key = runtime.ResolveSceneEntity(token)!.Value.Light!.Value.LogicalId,
                        Light = new() { Name = "Captured light" } });
                return captured;
            };
            Assert.True(load.BeginSave("captured.xivs").Success);
            await load.Drain;
            Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);
            var savedChild = Assert.Single(runtime.Captured!.Groups!, group => group.Name == "Child");
            var savedParent = Assert.Single(runtime.Captured.Groups!, group => group.Name == "Parent");
            Assert.Equal(savedParent.Key, savedChild.Parent);
            Assert.Equal(2, savedChild.Transform!.Members.Count);
            Assert.All(savedChild.Transform.Members, member => Assert.Equal(pose, member.Expected));
            Assert.Null(SceneGroupTransformCodec.Validate(runtime.Captured));
            var step = Assert.IsType<JournalStep>(history.PeekUndo());
            Assert.True(step.Undo());
            Assert.Same(existing, Assert.Single(groups.All));
            Assert.Null(state.NamedSnapshot(importedChild.Id));
            history.CommitUndo(step);
        }
    }

    private sealed class EmptyGroupSource : IGroupTransformSource
    {
        public PoseTransform? Read(TransformTargetId target) => null;
        public string? Refusal(TransformTargetId target) => null;
        public bool TryFrame(Vector3 origin, out GroupTransformFrame frame) { frame = default; return false; }
        public TransformTargetId? CurrentTarget(TransformTargetId target) => target;
    }

    [Fact]
    public async Task Load_history_tracks_each_redo_incarnation_without_appending_again()
    {
        var scene = SceneWith();
        scene.Lights.Add(new SceneLight { Key = Guid.NewGuid(), Light = new LightFile { Name = "Imported" } });
        var runtime = new FakeRuntime { ReadResult = scene };
        var history = new TransformHistory();
        var appends = 0;
        history.Appended += _ => appends++;
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime), history: history);
        Assert.True(load.BeginLoad("light.xivs").Success);
        await load.Drain;
        Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);
        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        for (var cycle = 0; cycle < 3; cycle++)
        {
            var current = runtime.SpawnedLightTokens[^1];
            Assert.True(step.Undo());
            history.CommitUndo(step);
            Assert.Same(current, runtime.DestroyedLightTokens[^1]);
            Assert.Equal(cycle + 1, runtime.DestroyedLightTokens.Count);
            Assert.True(step.Redo());
            history.CommitRedo(step);
            await load.Drain;
            Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);
            Assert.NotSame(current, runtime.SpawnedLightTokens[^1]);
            Assert.Equal((current, runtime.SpawnedLightTokens[^1]), runtime.HistoryReplacements[^1]);
            Assert.Same(step, history.PeekUndo());
        }
        Assert.Equal(1, appends);
        Assert.DoesNotContain("ClearScene", runtime.Calls);
    }

    // ── the seam fake ────────────────────────────────────────────────────

    private sealed class FakeDocuments(FakeRuntime fixture) : ISceneDocumentStore
    {
        public List<string> ReadPaths { get; } = [];
        public List<string> WritePaths { get; } = [];
        public IReadOnlyList<string> Notes { get; init; } = [];
        public System.IO.Stream? OpenAppearance(string path, string entry) => throw new NotSupportedException();

        public SceneDocumentRead Read(string path)
        {
            ReadPaths.Add(path);
            fixture.Record("ReadScene");
            return new(fixture.ReadFailure is { } failure
                ? SceneReadOutcome.Failed(failure)
                : SceneReadOutcome.Success(fixture.ReadResult!), Notes);
        }

        public SceneDocumentWrite Write(SceneFile scene, string path)
        {
            WritePaths.Add(path);
            fixture.Record("WriteScene");
            fixture.Captured = scene;
            return new(SceneWriteOutcome.Success(), Notes);
        }
    }

    private sealed class FakeRuntime : ISceneRuntime, IDisposable
    {
        public string WorldObjectName(string path) => Path.GetFileNameWithoutExtension(path);

        public readonly List<string> Calls = new();
        public readonly ConcurrentQueue<string> Destroyed = new();

        public SessionGeneration? Session = SessionGeneration.New();
        public SceneFile? ReadResult;
        public SceneStoreFailure? ReadFailure;
        public SceneFile? Captured;

        /// <summary>Runs after each named call, so a test can flip the session,
        /// cancel, or release a gate at an exact point in the phase order.</summary>
        public Action<string>? AfterCall;

        public Func<SceneActor, string?>? ActorSpawnFailure;
        public Func<SceneProp, string?>? PropSpawnFailure;
        public Func<SceneActor, string?>? GazeFailure;

        public void Dispose() { }

        public void Record(string call)
        {
            lock (Calls)
                Calls.Add(call);
            AfterCall?.Invoke(call);
        }

        public SessionGeneration? ActiveSession => Session;

        public Task<T> OnFramework<T>(Func<T> func) => Task.FromResult(func());

        private readonly Dictionary<SceneEntityHandle, string> _names = new();
        private SceneEntityHandle Token(string name)
        {
            var kind = name.Split(':')[0] switch
            {
                "actor" => SceneEntityKind.Actor, "prop" => SceneEntityKind.Prop,
                "overlay" => SceneEntityKind.Overlay, "world" => SceneEntityKind.WorldObject,
                "light" => SceneEntityKind.Light, _ => SceneEntityKind.Camera,
            };
            var token = new SceneEntityHandle(Session!.Value, kind);
            _names.Add(token, name);
            return token;
        }
        private string TokenName(SceneEntityHandle token) => _names[token];

        private readonly Dictionary<SceneEntityHandle, SelectionId> _structureIds = new();
        /// <summary>A light whose binding never publishes.</summary>
        public Func<SceneEntityHandle, bool>? IsUnbound;
        public SelectionId? ResolveSceneEntity(SceneEntityHandle token)
        {
            if (IsUnbound?.Invoke(token) == true)
                return null;
            if (!_structureIds.TryGetValue(token, out var id))
                _structureIds[token] = id = SelectionId.ForLight(new(Guid.NewGuid(), 1));
            return id;
        }

        public string? ArmSceneCapture(
            Guid sceneId,
            string? description,
            Action<SceneCaptureOutcome> onCaptured)
        {
            Record("ArmSceneCapture");
            var captured = CapturedScene?.Invoke()
                ?? new SceneFile { SceneId = sceneId, Description = description };
            captured.SceneId = sceneId;
            captured.Description = description;
            Record("CaptureScene");
            onCaptured(SceneCaptureOutcome.Ok(captured, new List<string>()));
            return null;
        }

        /// <summary>The document the capture hands back, for a test that needs
        /// a save to have something in it.</summary>
        public Func<SceneFile>? CapturedScene;

        public IReadOnlyList<string> StampMcdfHashes(SceneFile scene)
        {
            Record("StampMcdfHashes");
            return [];
        }

        /// <summary>What the seal does to the captured document, so a test can
        /// model an actor whose appearance sealed and one whose did not.
        /// </summary>
        public Func<SceneFile, IReadOnlyList<string>>? SealAppearanceResult;

        public Task<SceneSealOutcome> SealAppearance(
            SceneFile scene,
            IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId> identities,
            TimeSpan bound,
            CancellationToken cancellation)
        {
            Record("SealAppearance");
            return Task.FromResult(new SceneSealOutcome(
                SealAppearanceResult?.Invoke(scene) ?? Array.Empty<string>(),
                new List<string>()));
        }

        public long EstimateAppearanceBytes() => 0;

        public void DeleteTemporary(string path) => Record($"DeleteTemporary:{path}");

        public Task<SceneMcdfOutcome> ImportMcdf(
            string scenePath,
            SceneEntityHandle actor,
            SceneActor data,
            TimeSpan bound,
            CancellationToken cancellation)
        {
            Record($"ImportMcdf:{data.Name}");
            return Task.FromResult(SceneMcdfOutcome.Ok());
        }

        public SceneEntityHandle? SpawnActor(SceneActor data, out string? detail)
        {
            Record($"SpawnActor:{data.Name}");
            detail = ActorSpawnFailure?.Invoke(data);
            return detail is null ? Token($"actor:{data.Name}") : null;
        }

        /// <summary>Polls the barrier must absorb before the actor reports
        /// pose-ready. Models the real gap: after an appearance redraw the
        /// skeleton exists but its BONE bindings have not been republished, so
        /// readiness is false for a few frames.</summary>
        public int ActorReadyAfterPolls;

        public int ActorReadyPolls;

        /// <summary>An actor whose body never draws.</summary>
        public string? NeverReadyActor;

        public bool ActorReady(SceneEntityHandle actor)
        {
            Record("ActorReady");
            if (TokenName(actor) == $"actor:{NeverReadyActor}")
                return false;
            return ++ActorReadyPolls > ActorReadyAfterPolls;
        }

        public string? AttachCompanion(SceneEntityHandle actor, SceneActor data)
        {
            Record($"AttachCompanion:{data.Name}");
            return null;
        }

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
            SceneEntityHandle actor,
            SceneActor data,
            string description,
            Action<OperationReceipt> onReceipt)
        {
            Record($"ArmPoseImport:{data.Name}");
            if (PoseNeverFinishes)
                onReceipt(OperationReceipt.Pending(Guid.NewGuid(), OperationEpoch.First, Session!.Value,
                    new Poser.Domain.Identity.ActorId(Guid.NewGuid(), 1), description));
            else
                PublishPoseReceipts(description, onReceipt, null);
            return null;
        }

        /// <summary>Polls the pose slot answers busy for (an IK bake, another
        /// feature's import) before it frees.</summary>
        public int PoseBusyPolls;
        public bool PoseImportBusy => Interlocked.Decrement(ref PoseBusyPolls) >= 0;
        /// <summary>An armed import that only ever acknowledges.</summary>
        public bool PoseNeverFinishes;
        public bool HeldPoseImports;
        public bool EverHeldPoseImports;
        public void HoldPoseImports(bool held)
        {
            HeldPoseImports = held;
            EverHeldPoseImports |= held;
        }
        public void CancelPoseImport(Guid operationId) => Record("CancelPoseImport");

        public bool CompanionReady(SceneEntityHandle actor)
        {
            Record("CompanionReady");
            return true;
        }

        public string? ArmCompanionPoseImport(
            SceneEntityHandle actor,
            SceneActor data,
            string description,
            Action<OperationReceipt> onReceipt)
        {
            Record($"ArmCompanionPoseImport:{data.Name}");
            PublishPoseReceipts(description, onReceipt, null);
            return null;
        }

        public string? RestoreActorName(SceneEntityHandle actor, SceneActor data) => null;

        public string? PlaceActor(SceneEntityHandle actor, SceneActor data)
        {
            Record($"PlaceActor:{data.Name}");
            return null;
        }

        public string? PlaceCompanion(SceneEntityHandle actor, SceneActor data)
        {
            Record($"PlaceCompanion:{data.Name}");
            return null;
        }

        public string? FreezeActor(SceneEntityHandle actor)
        {
            Record($"FreezeActor:{TokenName(actor)["actor:".Length..]}");
            return null;
        }

        public string? ApplyActorGaze(SceneEntityHandle actor, SceneActor data, SceneEntityHandle? target)
        {
            Record($"ApplyActorGaze:{data.Name}");
            return GazeFailure?.Invoke(data);
        }

        public void SetActorVisibility(SceneEntityHandle actor, bool visible) =>
            Record("SetActorVisibility");

        public SceneEntityHandle? SpawnProp(SceneProp data, out string? detail)
        {
            Record($"SpawnProp:{data.Name}");
            detail = PropSpawnFailure?.Invoke(data);
            return detail is null ? Token($"prop:{data.Name}") : null;
        }

        public SceneEntityHandle? SpawnOverlay(SceneOverlay data, out string? detail)
        {
            string name = data.Node?.Name ?? "Overlay";
            Record($"SpawnOverlay:{name}");
            detail = null;
            return Token($"overlay:{name}");
        }

        public SceneEntityHandle? AdoptWorldObject(SceneWorldObject data, out string? detail)
        {
            Record($"AdoptWorldObject:{data.Path}");
            detail = null;
            return Token($"world:{data.Path}");
        }

        public void ReleaseWorldObject(SceneEntityHandle token) =>
            Record($"ReleaseWorldObject:{TokenName(token)}");

        public readonly List<SceneEntityHandle> SpawnedLightTokens = new();
        public readonly List<(SceneEntityHandle Previous, SceneEntityHandle Replacement)> HistoryReplacements = new();
        public void BindHistoryReplacement(SceneEntityHandle previous, SceneEntityHandle replacement) =>
            HistoryReplacements.Add((previous, replacement));
        public readonly List<SceneEntityHandle> DestroyedLightTokens = new();

        public SceneEntityHandle? SpawnLight(
            SceneLight data, SceneEntityHandle? attachmentOwner, out string? detail)
        {
            Record("SpawnLight");
            detail = null;
            var token = Token("light");
            SpawnedLightTokens.Add(token);
            return token;
        }

        public SceneCameraBaseline CaptureDefaultCameraState()
        {
            Record("CaptureDefaultCameraState");
            return new(new CameraFile(), null, string.Empty, false, null);
        }

        public string? ApplyDefaultCamera(SceneCamera data)
        {
            Record("ApplyDefaultCamera");
            return null;
        }

        public SceneEntityHandle? DefaultCameraToken() => null;

        public SceneEntityHandle? CreateCamera(SceneCamera data, out string? detail)
        {
            Record("CreateCamera");
            detail = null;
            return Token("camera");
        }

        public string? SetCameraTarget(
            SceneEntityHandle? camera, SceneEntityHandle targetActor, string displayName,
            bool targetLocked)
        {
            Record("SetCameraTarget");
            return null;
        }

        public string? SetLiveCamera(SceneEntityHandle? camera)
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

        public SceneWorld CaptureWorldState()
        {
            Record("CaptureWorldState");
            return new SceneWorld { IsWaterFrozen = true };
        }

        public string? ApplyWorld(SceneWorld world)
        {
            Record("ApplyWorld");
            return null;
        }

        public System.Numerics.Vector3? CurrentOrigin()
        {
            Record("CurrentOrigin");
            return new(10f, 0f, 20f);
        }

        public string? Preflight;
        public string? LoadPreflight(int actors) => Preflight;

        public SceneClearOutcome ClearScene()
        {
            Record("ClearScene");
            return new(2, 1, 0, 3, 1);
        }

        public void DestroyActor(SceneEntityHandle actor) => Destroy(actor);
        public void DestroyProp(SceneEntityHandle prop) => Destroy(prop);
        public void DestroyOverlay(SceneEntityHandle overlay) => Destroy(overlay);
        public void DestroyLight(SceneEntityHandle light)
        {
            DestroyedLightTokens.Add(light);
            Destroy(light);
        }
        public void DestroyCamera(SceneEntityHandle camera) => Destroy(camera);

        private void Destroy(SceneEntityHandle token)
        {
            Record($"Destroy:{TokenName(token)}");
            Destroyed.Enqueue(TokenName(token));
        }

        public void RestoreDefaultCamera(SceneCameraBaseline baseline) =>
            Record("RestoreDefaultCamera");
    }

    // ── document building ────────────────────────────────────────────────

    private static SceneActor Actor(string name, out Guid key)
    {
        key = Guid.NewGuid();
        return new SceneActor { Key = key, Name = name, Pose = new PoseFile() };
    }

    private static SceneFile SceneWith(
        params SceneActor[] actors)
    {
        var scene = new SceneFile
        {
            SceneId = Guid.NewGuid(),
            TerritoryId = 132u,
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

    [Fact]
    public async Task Load_failure_rolls_back_reverse_and_session_replacement_cancels_exactly()
    {
        var scene = SceneWith(Actor("Lead", out _), Actor("Second", out _));
        var failedRuntime = new FakeRuntime { ReadResult = scene, ActorSpawnFailure = a => a.Name == "Second" ? "no free slot" : null };
        using (var failed = new SceneWorkflow(failedRuntime, new FakeDocuments(failedRuntime)))
        {
            Assert.True(failed.BeginLoad("shot.xivs").Success);
            await failed.Drain;
            Assert.Equal(new[] { "actor:Lead" }, failedRuntime.Destroyed.ToArray());
            Assert.Equal(OperationReceiptState.RolledBack, failed.Receipt!.State);
            Assert.False(failed.Progress!.Outcome!.LeftEntitiesBehind);
            Assert.Contains("no free slot", failed.Progress!.Outcome!.Detail);
        }

        var replacedRuntime = new FakeRuntime { ReadResult = SceneWith(Actor("Lead", out _)) };
        replacedRuntime.AfterCall = call => { if (call == "SpawnActor:Lead") replacedRuntime.Session = SessionGeneration.New(); };
        using var replaced = new SceneWorkflow(replacedRuntime, new FakeDocuments(replacedRuntime));
        Assert.True(replaced.BeginLoad("shot.xivs").Success);
        await replaced.Drain;
        Assert.Equal(new[] { "actor:Lead" }, replacedRuntime.Destroyed.ToArray());
        Assert.Equal(OperationReceiptState.Cancelled, replaced.Receipt!.State);
        Assert.Contains("session ended", replaced.Progress!.Outcome!.Detail);

        // A read failure touches no native state and appends no history.
        var readRuntime = new FakeRuntime { ReadFailure = Corrupt("The document is not a scene.") };
        var history = new TransformHistory();
        using var read = new SceneWorkflow(readRuntime, new FakeDocuments(readRuntime), history: history);
        Assert.True(read.BeginLoad("shot.xivs").Success);
        await read.Drain;
        Assert.Equal(OperationReceiptState.Failed, read.Receipt!.State);
        Assert.Equal(new[] { "ReadScene" }, readRuntime.Calls);
        Assert.False(history.CanUndo);
        Assert.Contains("The document is not a scene.", read.Progress!.Outcome!.Detail);
    }

    // ── issue #432: optional steps never roll the scene back ─────────────

    [Fact]
    public async Task One_actor_that_never_draws_is_named_and_the_rest_of_the_scene_stays()
    {
        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(Actor("Lead", out _), Actor("Slow", out _)),
            NeverReadyActor = "Slow",
        };
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime))
        {
            ActorReadyBound = TimeSpan.FromMilliseconds(100),
        };
        Assert.True(load.BeginLoad("shot.xivs").Success);
        await load.Drain;

        Assert.Equal(OperationReceiptState.Failed, load.Receipt!.State);
        Assert.Empty(runtime.Destroyed);
        var refusal = Assert.Single(load.Progress!.Outcome!.Entities, entity => !entity.Restored);
        Assert.Equal(("Actor", "Slow"), (refusal.Kind, refusal.Name));
        Assert.Contains("ArmPoseImport:Lead", runtime.Calls);
        Assert.DoesNotContain("ArmPoseImport:Slow", runtime.Calls);
    }

    [Fact]
    public async Task Unbound_members_and_refused_parent_links_are_named_not_rolled_back()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var document = SceneWith();
        document.Lights.Add(new() { Key = a, Light = new() { Name = "A" } });
        document.Lights.Add(new() { Key = b, Light = new() { Name = "B" } });
        document.Lights.Add(new() { Key = c, Light = new() { Name = "C" } });
        document.Groups = [new() { Key = Guid.NewGuid(), Name = "Rig",
            Members = [new() { Kind = "light", Key = a }, new() { Kind = "light", Key = c }] }];
        document.RootOrder = [new() { Kind = "light", Key = a }];
        // A bone parent on a light cannot land.
        document.Parents = [new() { Child = new() { Kind = "light", Key = b },
            Target = new() { Kind = "light", Key = a }, BoneName = "j_kao" }];
        var runtime = new FakeRuntime { ReadResult = document };
        runtime.IsUnbound = token =>
            runtime.SpawnedLightTokens.Count == 3 && token == runtime.SpawnedLightTokens[2];
        var groups = new SceneGroups(); var state = new GroupTransformState();
        using var coordinator = new GroupTransformCoordinator(new(new SelectionSession()), groups, state, new EmptyGroupSource());
        var history = new TransformHistory();
        var parenting = new TransformParenting(new ParentRuntime(), history, new(history));
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime), history: history,
            structure: new SceneStructure(groups, coordinator, state), parenting: parenting)
        {
            StructureBindingBound = TimeSpan.FromMilliseconds(100),
        };
        Assert.True(load.BeginLoad("rig.xivs").Success);
        await load.Drain;

        Assert.Equal(OperationReceiptState.Failed, load.Receipt!.State);
        Assert.Empty(runtime.Destroyed);
        var refusals = load.Progress!.Outcome!.Entities.Where(entity => !entity.Restored).ToList();
        Assert.Equal(2, refusals.Count);
        Assert.Contains(refusals, refusal => (refusal.Kind, refusal.Name) == ("Group", "Rig"));
        Assert.Contains(refusals, refusal => (refusal.Kind, refusal.Name) == ("Parent", "B"));
        Assert.All(refusals, refusal => Assert.False(string.IsNullOrWhiteSpace(refusal.Remedy)));
        Assert.Single(Assert.Single(groups.All, group => group.Name == "Rig").Members);
        Assert.Empty(parenting.Capture());
    }

    [Fact]
    public async Task Clear_first_refuses_before_clearing_and_labels_a_rollback_after_it()
    {
        var clearFirst = new SceneLoadOptions { ClearExistingScene = true };
        var refusedRuntime = new FakeRuntime
        {
            ReadResult = SceneWith(Actor("Lead", out _)),
            Preflight = "There is no local player to spawn the scene's actors from.",
        };
        using (var refused = new SceneWorkflow(refusedRuntime, new FakeDocuments(refusedRuntime)))
        {
            Assert.True(refused.BeginLoad("shot.xivs", clearFirst).Success);
            await refused.Drain;
            Assert.Equal(OperationReceiptState.Failed, refused.Receipt!.State);
            Assert.DoesNotContain("ClearScene", refusedRuntime.Calls);
            Assert.DoesNotContain(refusedRuntime.Calls, call => call.StartsWith("SpawnActor"));
            Assert.False(refused.Progress!.Outcome!.SessionCleared);
        }

        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(Actor("Lead", out _)),
            ActorSpawnFailure = _ => "The game has no free GPose actor slot (the actor table is full).",
        };
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime));
        Assert.True(load.BeginLoad("shot.xivs", clearFirst).Success);
        await load.Drain;
        Assert.Equal(OperationReceiptState.RolledBack, load.Receipt!.State);
        Assert.Contains("ClearScene", runtime.Calls);
        Assert.True(load.Progress!.Outcome!.SessionCleared);
        Assert.Contains("actor table is full", load.Progress.Outcome.Detail);
    }

    // ── issue #433: a committed load is one consistent history step ──────

    private sealed class ReplayRunner : IUndoRunner
    {
        public GestureResult Undo() => GestureResult.Fail("Not a deferred step.");
        public GestureResult Redo() => GestureResult.Fail("Not a deferred step.");
        public GestureResult Replay(JournalStep step, bool before) =>
            (before ? step.Undo() : step.Redo()) ? GestureResult.Ok() : GestureResult.Fail("Refused.");
    }

    [Fact]
    public async Task A_partial_load_is_one_undo_step_that_removes_everything_it_made()
    {
        var runtime = new FakeRuntime
        {
            ReadResult = WholeScene(),
            GazeFailure = _ => "The look-at target is gone.",
        };
        var history = new TransformHistory();
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime), history: history);
        Assert.True(load.BeginLoad("shot.xivs").Success);
        await load.Drain;
        Assert.Equal(OperationReceiptState.Failed, load.Receipt!.State);

        var step = Assert.IsType<JournalStep>(history.PeekUndo());
        Assert.True(step.Undo());
        Assert.Equal(new[] { "light", "overlay:Line", "prop:Chair", "actor:Lead" }, runtime.Destroyed.ToArray());
        Assert.Contains("ReleaseWorldObject:world:bg/example.mdl", runtime.Calls);
        Assert.Contains("RestoreDefaultCamera", runtime.Calls);
    }

    [Fact]
    public async Task Redo_never_clears_again_and_a_failed_replay_stays_redoable()
    {
        var runtime = new FakeRuntime { ReadResult = SceneWith(Actor("Lead", out _)) };
        var history = new TransformHistory();
        var notices = new List<string>();
        var journal = new UndoJournal(history, new ReplayRunner(), _ => true, notices.Add);
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime), history: history);
        Assert.True(load.BeginLoad("shot.xivs", new SceneLoadOptions { ClearExistingScene = true }).Success);
        await load.Drain;
        Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);

        Assert.True(journal.Undo().Success);
        Assert.True(history.CanRedo);
        runtime.ActorSpawnFailure = _ => "The game has no free GPose actor slot (the actor table is full).";
        _ = journal.Redo(); // Admitted; the outcome follows the replay.
        await load.Drain;
        for (int wait = 0; journal.IsRestoring && wait < 200; wait++)
            await Task.Delay(10);

        Assert.False(journal.IsRestoring);
        Assert.Equal(OperationReceiptState.RolledBack, load.Receipt!.State);
        Assert.Single(runtime.Calls, call => call == "ClearScene");
        Assert.True(history.CanRedo);
        Assert.False(history.CanUndo);
        Assert.Contains(notices, notice => notice.Contains("actor table is full"));
    }

    [Fact]
    public async Task Undo_while_another_scene_operation_runs_keeps_the_load_step()
    {
        var runtime = new FakeRuntime { ReadResult = SceneWith(Actor("Lead", out _)) };
        var history = new TransformHistory();
        var journal = new UndoJournal(history, new ReplayRunner(), _ => true, _ => { });
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime), history: history);
        Assert.True(load.BeginLoad("shot.xivs").Success);
        await load.Drain;
        var step = history.PeekUndo();

        using var gate = new ManualResetEventSlim();
        runtime.AfterCall = call => { if (call == "ArmSceneCapture") gate.Wait(); };
        Assert.True(load.BeginSave("busy.xivs").Success);
        Assert.False(journal.Undo().Success);
        Assert.False(journal.Undo().Success);
        Assert.Same(step, history.PeekUndo());

        gate.Set();
        await load.Drain;
        Assert.True(journal.Undo().Success);
        Assert.Contains("actor:Lead", runtime.Destroyed);
    }

    // ── issue #41: the pose import's pending acknowledgement ─────────────

    /// <summary>
    /// A skeleton that is not pose-ready yet must be WAITED for, not refused.
    ///
    /// <para>This is the shape of the reported failure: a clear-first load
    /// respawns the actor, the appearance import redraws it, and the bone
    /// bindings are republished a frame or two later. The barrier polls, so
    /// the load has to absorb that gap and still pose the actor — the import
    /// resolves its targets up front and fails on the first one, which is what
    /// produced "Import target n_root could not be resolved".</para>
    /// </summary>
    [Fact]
    public async Task A_skeleton_that_is_not_ready_yet_is_waited_for_not_refused()
    {
        var runtime = new FakeRuntime
        {
            ReadResult = SceneWith(Actor("Midona Rhel", out _)),
            ActorReadyAfterPolls = 3,
        };
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime));
        Assert.True(load.BeginLoad("shot.xivs").Success);
        await load.Drain;

        Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);
        // It polled rather than giving up on the first look.
        Assert.True(runtime.ActorReadyPolls > 3);
        // And it still posed the actor once readiness landed.
        Assert.Contains("ArmPoseImport:Midona Rhel", runtime.Calls);
        Assert.DoesNotContain(
            load.Progress!.Outcome!.Entities, entity => !entity.Restored);
        // Issue #41: the load answers on the import's terminal receipt, never its pending label.
        Assert.DoesNotContain(load.Progress.Outcome.Entities,
            entity => entity.Detail?.Contains("Scene pose:") == true);
    }

    /// <summary>
    /// THE RESTORE CONTRACT. A scene is a picture, not a performance: it
    /// carries pose data and no animation, because a timeline id resolves
    /// against the loading client's own game and mods and would show something
    /// different — or nothing — on someone else's machine. So every restored
    /// actor is stopped first and the pose lands on a held frame.
    /// </summary>
    [Fact]
    public async Task Every_restored_actor_is_frozen_before_its_pose()
    {
        var actor = Actor("Lead", out _);
        actor.Gaze = new SceneActorGaze { Mode = GazeTargetMode.Detached, Parts = GazeTargetType.All };
        var runtime = new FakeRuntime { ReadResult = SceneWith(actor) };
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime));
        Assert.True(load.BeginLoad("shot.xivs").Success);
        await load.Drain;

        Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);
        int frozen = runtime.Calls.IndexOf("FreezeActor:Lead");
        int posed = runtime.Calls.IndexOf("ArmPoseImport:Lead");
        Assert.True(frozen >= 0, "the actor was never stopped");
        Assert.True(
            frozen < posed,
            "the pose must land on an actor that is already stopped");
        // A detached gaze is established before the import samples its basis.
        int gaze = runtime.Calls.IndexOf("ApplyActorGaze:Lead");
        Assert.True(gaze >= 0 && gaze < posed);
        Assert.Single(runtime.Calls, call => call == "ApplyActorGaze:Lead");
    }

    /// <summary>
    /// A save may not call itself a success while dropping something it was
    /// asked to include. The appearance case is how this was found — an
    /// oversized package was refused, the reference was dropped with it, and
    /// the save reported "Saved shot.xivs" — but the rule is general.
    /// </summary>
    [Fact]
    public async Task A_save_that_omits_requested_content_is_not_a_plain_success()
    {
        var runtime = new FakeRuntime();
        // The seal could not build the package, so the policy drops the
        // reference and the document goes out without appearance.
        runtime.CapturedScene = () =>
        {
            var scene = SceneWith(Actor("Lead", out _));
            scene.Actors[0].Mcdf = new SceneActorMcdf
            {
                Path = @"C:\packages\lead.mcdf",
                FileName = "lead.mcdf",
            };
            return scene;
        };
        runtime.SealAppearanceResult = _ => new[]
        {
            "Actor 'Lead''s appearance is 900 MB, over what Poser can import " +
            "back; the scene saved without it.",
        };

        using var save = new SceneWorkflow(runtime, new FakeDocuments(runtime));
        Assert.True(save.BeginSave(
            "shot.xivs",
            null,
            new SceneSaveOptions { IncludeModdedAppearance = true }).Success);
        await save.Drain;

        // The file was written, so this is not a failure — it is the partial
        // state, and it has to be visible as one.
        Assert.NotEqual(OperationReceiptState.Applied, save.Receipt!.State);
        var outcome = save.Progress!.Outcome!;
        var refusal = Assert.Single(
            outcome.Entities, entity => !entity.Restored);
        Assert.Equal("Character file", refusal.Kind);
        Assert.Contains("appearance", outcome.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ── issue #41: one failure mode per load phase ───────────────────────

    // ── issue #437: per-actor steps wait, and never leave work armed ─────

    [Fact]
    public async Task A_busy_pose_slot_is_waited_for_and_progress_reaches_its_total()
    {
        var scene = SceneWith(Actor("Lead", out _));
        scene.Props.Add(new SceneProp { Key = Guid.NewGuid(), Name = "Chair" });
        scene.WorldObjects = [WorldObject("bg/example.mdl")];
        var runtime = new FakeRuntime { ReadResult = scene, PoseBusyPolls = 3 };
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime));
        var last = (Done: 0, Total: 0);
        load.Changed += () =>
        {
            if (load.Progress is { Outcome: null, EntitiesTotal: > 0 } progress)
                last = (progress.EntitiesDone, progress.EntitiesTotal);
        };
        Assert.True(load.BeginLoad("shot.xivs").Success);
        await load.Drain;

        Assert.Equal(OperationReceiptState.Applied, load.Receipt!.State);
        Assert.Contains("ArmPoseImport:Lead", runtime.Calls);
        Assert.Equal(3, last.Total);
        Assert.Equal(last.Total, last.Done);
        Assert.True(runtime.EverHeldPoseImports);
        Assert.False(runtime.HeldPoseImports);
    }

    [Fact]
    public async Task A_pose_import_that_never_finishes_is_cancelled_not_left_armed()
    {
        var runtime = new FakeRuntime { ReadResult = SceneWith(Actor("Lead", out _)), PoseNeverFinishes = true };
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime))
        {
            PoseImportBound = TimeSpan.FromMilliseconds(100),
        };
        Assert.True(load.BeginLoad("shot.xivs").Success);
        await load.Drain;

        Assert.Equal(OperationReceiptState.Failed, load.Receipt!.State);
        Assert.Contains("CancelPoseImport", runtime.Calls);
        var refusal = Assert.Single(load.Progress!.Outcome!.Entities, entity => !entity.Restored);
        Assert.Equal(("Actor", "Lead"), (refusal.Kind, refusal.Name));
        Assert.Contains("cancelled", refusal.Detail);
    }

    [Fact]
    public async Task Cancelling_during_a_pose_import_ends_cancelled_and_cancels_the_import()
    {
        var runtime = new FakeRuntime { ReadResult = SceneWith(Actor("Lead", out _)), PoseNeverFinishes = true };
        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime));
        runtime.AfterCall = call => { if (call == "ArmPoseImport:Lead") load.Cancel(); };
        Assert.True(load.BeginLoad("shot.xivs").Success);
        await load.Drain;

        Assert.Equal(OperationReceiptState.Cancelled, load.Receipt!.State);
        Assert.Contains("CancelPoseImport", runtime.Calls);
        Assert.Equal(new[] { "actor:Lead" }, runtime.Destroyed.ToArray());
        Assert.False(runtime.HeldPoseImports);
    }

    /// <summary>A whole scene: one of every entity kind the load restores, so
    /// a per-phase failure can be asserted to keep everything the OTHER phases
    /// produced.</summary>
    private static SceneFile WholeScene()
    {
        var lead = Actor("Lead", out var leadKey);
        lead.CompanionKind = CompanionKind.Companion;
        lead.CompanionId = 4;
        lead.CompanionPose = new PoseFile();
        lead.Mcdf = new SceneActorMcdf
        {
            Path = @"C:\packages\lead.mcdf",
            FileName = "lead.mcdf",
        };
        var scene = SceneWith(lead);
        scene.Props.Add(new SceneProp { Key = Guid.NewGuid(), Name = "Chair" });
        scene.Overlays = new List<SceneOverlay>
        {
            new()
            {
                Key = Guid.NewGuid(),
                Node = new Poser.Domain.Presentation.OverlayNodeState
                {
                    Name = "Line",
                },
            },
        };
        scene.WorldObjects = new List<SceneWorldObject>
        {
            WorldObject("bg/example.mdl"),
        };
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
        return scene;
    }

    /// <summary>One named entity-level failure mode per load phase, driven
    /// through the seam the way the real refusals arrive. Every one of them
    /// must land the SAME shape — the operation ends Failed rather than rolled
    /// back, the refused entity is named with its kind, a reason and a next
    /// step, nothing this load created is destroyed, and the outcome says the
    /// successes were kept — because that shape is what the result list and
    /// the operation log both read.</summary>
    public static TheoryData<string, string, Action<object>> PhaseFailures()
    {
        var data = new TheoryData<string, string, Action<object>>();
        void Add(string phase, string kind, Action<FakeRuntime> arrange) =>
            data.Add(phase, kind, runtime => arrange((FakeRuntime)runtime));

        Add("objects", "Object",
            runtime => runtime.PropSpawnFailure = _ => "No free spawn slot.");
        Add("gaze", "Gaze",
            runtime => runtime.GazeFailure = _ => "The look-at target is gone.");
        return data;
    }

    [Theory]
    [MemberData(nameof(PhaseFailures))]
    public async Task Each_phase_failure_keeps_the_scene_and_names_the_entity(
        string phase, string kind, Action<object> arrange)
    {
        var runtime = new FakeRuntime { ReadResult = WholeScene() };
        arrange(runtime);

        using var load = new SceneWorkflow(runtime, new FakeDocuments(runtime));
        Assert.True(load.BeginLoad("shot.xivs").Success);
        await load.Drain;

        var outcome = load.Progress!.Outcome!;
        Assert.Equal(OperationReceiptState.Failed, load.Receipt!.State);
        Assert.Equal(OperationReceiptState.Failed, outcome.State);
        Assert.True(outcome.LeftEntitiesBehind, $"{phase} tore the scene down");
        Assert.Empty(runtime.Destroyed);

        var refusal = Assert.Single(
            outcome.Entities, entity => !entity.Restored);
        Assert.Equal(kind, refusal.Kind);
        Assert.False(string.IsNullOrWhiteSpace(refusal.Detail));
        Assert.False(string.IsNullOrWhiteSpace(refusal.Remedy));

        // The successes stand beside the one refusal, and the terminal states
        // a reason of its own rather than leaving the entity list to speak.
        Assert.Contains(outcome.Entities, entity => entity.Restored);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Detail));
    }
}
