using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Poser.Application.Operations;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Game.Scene;

/// <summary>
/// The single-flight owner of the whole-shot scene workflow: admission
/// (exact session generation, owner-local operation epoch, operation id),
/// the save capture/write pipeline, the load transaction's ordered phases
/// with reverse-order rollback, and the bounded cancel/drain that runs
/// before disposal. It reuses <see cref="OperationReceipt"/>,
/// <see cref="OperationEpoch"/> and <see cref="SessionGeneration"/> wholesale —
/// there is no scene-specific receipt or epoch type.
///
/// A whole-shot operation has no single target actor, so its receipts target
/// the scene's own logical identity: <c>new ActorId(SceneScopeId, 0)</c>,
/// where the scope id is the document's SceneId for a save and a minted
/// load-scope identity for a load (the file's id is unknown at admission and
/// receipt identity must be stable from Pending to terminal).
///
/// Load semantics: the ENTIRE document is validated before any native
/// mutation; entities spawn additively (nothing pre-existing is destroyed);
/// structural failures (a failed actor spawn, readiness timeout, session
/// replacement, cancellation) roll back everything THIS operation created in
/// reverse order; entity-level failures (a companion, pose, prop, light or
/// camera-target refusal) keep the successfully restored entities and
/// publish a Failed receipt whose outcome names every refusal — typed
/// partial recovery, never a silent detach and never a silent skip.
/// </summary>
public sealed class SceneWorkflow : IDisposable
{
    /// <summary>Bound for the spawned actors' skeleton readiness barrier —
    /// same bound the MCDF redraw barrier uses.</summary>
    private static readonly TimeSpan ActorReadyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Bound for one armed pose import to reach its terminal
    /// receipt.</summary>
    private static readonly TimeSpan PoseImportTimeout = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly ISceneRuntime _runtime;
    private readonly object _publishGate = new();
    private readonly CancellationTokenSource _disposal = new();

    private SceneProgress? _progress;
    private OperationReceipt? _receipt;
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private Operation? _current;
    private OperationEpoch _epoch;
    private bool _disposed;

    /// <summary>
    /// Composition entry point. The native/persistence seam is an
    /// implementation detail of this assembly, so the host wires the OWNERS
    /// and the workflow binds them — nothing outside Poser.Game ever names
    /// <see cref="ISceneRuntime"/>.
    /// </summary>
    public SceneWorkflow(
        Dalamud.Plugin.Services.IFramework framework,
        Poser.Application.Lifecycle.ISessionGenerationSource sessions,
        SceneCaptureService capture,
        Posing.CleanPoseFacade poses,
        Poser.Services.IActorSpawnService spawns,
        Poser.Services.ISkeletonService skeletons,
        Poser.Services.IPosingService posing,
        PropSpawnService props,
        Poser.Services.ILightingService lighting,
        Poser.Services.IVirtualCameraService cameras,
        Poser.Services.IEnvironmentService environment)
        : this(new SceneRuntimeAdapter(
            framework, sessions, capture, poses, spawns, skeletons, posing,
            props, lighting, cameras, environment))
    {
    }

    internal SceneWorkflow(ISceneRuntime runtime)
    {
        _runtime = runtime;
    }

    /// <summary>Raised after any progress/receipt publication; UI reads the
    /// immutable snapshots, never workflow internals.</summary>
    public event Action? Changed;

    public SceneProgress? Progress => _progress;

    public OperationReceipt? Receipt => _receipt;

    /// <summary>Only one scene save/load runs at a time.</summary>
    public bool Busy => _task is { IsCompleted: false };

    /// <summary>The running operation's join handle. The terminal receipt is
    /// always published before it completes, so awaiting it is the exact
    /// "the operation is finished" barrier.</summary>
    internal Task Drain => _task ?? Task.CompletedTask;

    /// <summary>Cooperative cancellation of the running operation.</summary>
    public void Cancel() => _cancellation?.Cancel();

    private sealed class Operation
    {
        public required Guid SceneScopeId { get; init; }
        public required string FileName { get; init; }
        public required Guid OperationId { get; init; }
        public required OperationEpoch Epoch { get; init; }
        public required SessionGeneration Session { get; init; }
        public required SceneOperationKind Kind { get; init; }
        public bool Invalidated;
        public bool TerminalPublished;

        public ActorId Target => new(SceneScopeId, 0);

        // What THIS operation created, in creation order; rollback walks
        // these in reverse. Tokens are opaque runtime handles.
        public readonly List<object> SpawnedActors = new();
        public readonly List<object> SpawnedProps = new();
        public readonly List<object> SpawnedLights = new();
        public readonly List<object> CreatedCameras = new();
        public CameraFile? DefaultCameraBaseline;
        public SceneEnvironment? EnvironmentBaseline;
    }

    // ── Publication (late-completion armor) ──────────────────────────────

    private void PublishStep(Operation operation, SceneProgress progress)
    {
        lock (_publishGate)
        {
            if (!ReferenceEquals(_current, operation) || operation.TerminalPublished)
                return;
            _progress = progress;
        }
        RaiseChanged();
    }

    private void PublishTerminal(
        Operation operation, SceneProgress progress, OperationReceipt receipt)
    {
        lock (_publishGate)
        {
            if (!ReferenceEquals(_current, operation) || operation.TerminalPublished)
                return;
            operation.TerminalPublished = true;
            _progress = progress;
            _receipt = receipt;
        }
        RaiseChanged();
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // Observer failures never poison the transaction.
        }
    }

    // ── Admission ────────────────────────────────────────────────────────

    private SceneActionResult? AdmissionGate()
    {
        if (_disposed)
            return SceneActionResult.Fail(
                "Poser is shutting down; no new scene operation can start.");
        if (Busy)
            return SceneActionResult.Fail(
                "Another scene operation is already running.");
        return null;
    }

    private Operation Admit(
        Guid sceneScopeId,
        string fileName,
        SceneOperationKind kind,
        SessionGeneration session)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _epoch = _epoch.IsValid ? _epoch.Next() : OperationEpoch.First;
        var operation = new Operation
        {
            SceneScopeId = sceneScopeId,
            FileName = fileName,
            OperationId = Guid.NewGuid(),
            Epoch = _epoch,
            Session = session,
            Kind = kind,
        };
        lock (_publishGate)
        {
            _current = operation;
            _receipt = OperationReceipt.Pending(
                operation.OperationId, operation.Epoch, session, operation.Target);
        }
        return operation;
    }

    /// <summary>Starts the whole-shot save: framework-thread pointer-free
    /// capture first, then off-thread validation and the atomic write.</summary>
    public SceneActionResult BeginSave(string path, string? description = null)
    {
        if (AdmissionGate() is { } refused)
            return refused;
        if (_runtime.ActiveSession is not { } session)
            return SceneActionResult.Fail(
                "No GPose session is active; a scene save needs the exact session identity.");

        var sceneId = Guid.NewGuid();
        var operation = Admit(
            sceneId, System.IO.Path.GetFileName(path), SceneOperationKind.Save, session);
        var cancellation = _cancellation!.Token;
        _progress = new SceneProgress(
            SceneOperationKind.Save, operation.FileName,
            ScenePhase.Capturing, 0, 0, true, null);
        RaiseChanged();
        _task = Task.Run(
            () => RunSave(operation, path, description, cancellation),
            CancellationToken.None);
        return SceneActionResult.Ok();
    }

    /// <summary>Starts the whole-shot load transaction.</summary>
    public SceneActionResult BeginLoad(string path)
    {
        if (AdmissionGate() is { } refused)
            return refused;
        if (_runtime.ActiveSession is not { } session)
            return SceneActionResult.Fail(
                "No GPose session is active; a scene load needs the exact session identity.");

        var operation = Admit(
            Guid.NewGuid(), System.IO.Path.GetFileName(path),
            SceneOperationKind.Load, session);
        var cancellation = _cancellation!.Token;
        _progress = new SceneProgress(
            SceneOperationKind.Load, operation.FileName,
            ScenePhase.Reading, 0, 0, true, null);
        RaiseChanged();
        _task = Task.Run(
            () => RunLoad(operation, path, cancellation), CancellationToken.None);
        return SceneActionResult.Ok();
    }

    // ── Save ─────────────────────────────────────────────────────────────

    private async Task RunSave(
        Operation operation,
        string path,
        string? description,
        CancellationToken cancellation)
    {
        // A save never mutates the session, so its only terminal states are
        // Applied, Cancelled, and Failed — there is nothing to roll back.
        void Finish(
            bool success,
            string detail,
            IReadOnlyList<string>? notes = null,
            IReadOnlyList<string>? evidence = null)
        {
            var state = success
                ? OperationReceiptState.Applied
                : cancellation.IsCancellationRequested || operation.Invalidated
                    ? OperationReceiptState.Cancelled
                    : OperationReceiptState.Failed;
            FinishTerminal(
                operation, SceneOperationKind.Save, state, detail,
                Array.Empty<SceneEntityOutcome>(),
                notes ?? Array.Empty<string>(),
                evidence ?? Array.Empty<string>());
        }

        try
        {
            SceneCaptureOutcome captured;
            try
            {
                captured = await _runtime.OnFramework(() =>
                {
                    if (Guard(operation, cancellation) is { } stop)
                        return SceneCaptureOutcome.Fail(stop);
                    return _runtime.CaptureScene(operation.SceneScopeId, description);
                });
            }
            catch (Exception ex)
            {
                Finish(false, $"The capture dispatch failed: {ex.Message}");
                return;
            }

            if (!captured.Success || captured.Scene is not { } scene)
            {
                Finish(false, captured.Detail ?? "The scene could not be captured.");
                return;
            }

            if (cancellation.IsCancellationRequested)
            {
                Finish(false, "The save was cancelled before writing.");
                return;
            }

            PublishStep(operation, new SceneProgress(
                SceneOperationKind.Save, operation.FileName,
                ScenePhase.Writing, 0, 0, false, null));

            var written = _runtime.WriteScene(scene, path);
            if (!written.Succeeded)
            {
                Finish(
                    false,
                    $"The scene could not be written: {written.Failure!.Detail}",
                    captured.Notes,
                    written.RecoveryEvidencePaths);
                return;
            }

            var summary =
                $"Saved {scene.Actors.Count} actors, {scene.Props.Count} props, " +
                $"{scene.Lights.Count} lights and {scene.Cameras.Count} cameras to " +
                $"{operation.FileName}.";
            if (captured.Notes.Count > 0)
                summary += $" {captured.Notes.Count} entities carried notes.";
            Finish(true, summary, captured.Notes);
        }
        catch (Exception ex)
        {
            Finish(false, $"The save failed unexpectedly: {ex.Message}");
        }
    }

    // ── Load ─────────────────────────────────────────────────────────────

    private async Task RunLoad(
        Operation operation, string path, CancellationToken cancellation)
    {
        var entities = new List<SceneEntityOutcome>();
        int total = 0;
        int done = 0;

        void Step(ScenePhase phase, bool cancellable = true) =>
            PublishStep(operation, new SceneProgress(
                SceneOperationKind.Load, operation.FileName,
                phase, done, total, cancellable, null));

        void Finish(OperationReceiptState state, string detail) =>
            FinishTerminal(
                operation, SceneOperationKind.Load, state, detail,
                entities, Array.Empty<string>(), Array.Empty<string>());

        async Task<string?> RollbackCreated()
        {
            Step(ScenePhase.RollingBack, cancellable: false);
            try
            {
                return await _runtime.OnFramework(() => Rollback(operation));
            }
            catch (Exception ex)
            {
                // The framework thread is gone (shutdown teardown); nothing
                // is left to restore into.
                return ex.Message;
            }
        }

        // A structural refusal undoes the whole operation. The terminal state
        // states exactly what the session is left holding: RolledBack/Cancelled
        // mean nothing survived, Failed means the rollback itself left named
        // leftovers the user must clean up by hand.
        async Task Abort(string failure)
        {
            bool cancelled =
                cancellation.IsCancellationRequested || operation.Invalidated;
            var leftover = await RollbackCreated();
            string detail = failure;
            if (leftover != null)
                detail += $" Rollback also failed, so these are still in the " +
                    $"session and must be removed by hand: {leftover}";
            Finish(
                leftover != null
                    ? OperationReceiptState.Failed
                    : cancelled
                        ? OperationReceiptState.Cancelled
                        : OperationReceiptState.RolledBack,
                detail);
        }

        try
        {
            // Phase 1 — read and validate the WHOLE document off-thread.
            // Nothing native has happened yet; a corrupt, oversized, or
            // future file is a pure typed refusal.
            var read = _runtime.ReadScene(path);
            if (!read.Succeeded || read.Scene is not { } scene)
            {
                // Nothing native has run, so there is nothing to roll back:
                // a corrupt, oversized or future file is a plain Failed.
                Finish(OperationReceiptState.Failed, read.Failure!.Detail);
                return;
            }

            total = scene.Actors.Count + scene.Props.Count +
                scene.Lights.Count + scene.Cameras.Count +
                (scene.Environment is null ? 0 : 1);

            // Phase 2 — baselines, then spawn/admit every entity that other
            // phases depend on. Actor spawn failures are structural: pose
            // and relationships cannot proceed against a hole.
            var actorTokens = new Dictionary<Guid, object>();
            Step(ScenePhase.SpawningEntities);
            var spawnFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;

                operation.EnvironmentBaseline = _runtime.CaptureEnvironmentState();
                if (scene.Cameras.Count > 0)
                    operation.DefaultCameraBaseline =
                        _runtime.CaptureDefaultCameraState();

                foreach (var actor in scene.Actors)
                {
                    var token = _runtime.SpawnActor(actor, out var detail);
                    if (token is null)
                        return $"Actor '{actor.Name}' could not be spawned: " +
                            $"{detail ?? "the spawn failed."}";
                    operation.SpawnedActors.Add(token);
                    actorTokens[actor.Key] = token;
                }

                foreach (var prop in scene.Props)
                {
                    var token = _runtime.SpawnProp(prop, out var detail);
                    if (token is null)
                    {
                        entities.Add(new SceneEntityOutcome(
                            "Prop", prop.Name, false,
                            detail ?? "The prop could not be spawned."));
                        continue;
                    }
                    operation.SpawnedProps.Add(token);
                    entities.Add(new SceneEntityOutcome("Prop", prop.Name, true));
                }
                return null;
            });
            if (spawnFailure != null)
            {
                await Abort(spawnFailure);
                return;
            }
            done = scene.Props.Count;

            // Phase 3 — bounded readiness barrier: pose needs the spawned
            // actors' skeletons, which build with their draw objects.
            Step(ScenePhase.AwaitingActors);
            var ready = await WaitForActors(operation, cancellation);
            if (ready != null)
            {
                await Abort(ready);
                return;
            }

            // Phase 4 — explicit relationships.
            Step(ScenePhase.ApplyingRelationships);
            var relationshipFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;
                foreach (var actor in scene.Actors)
                {
                    if (actor.CompanionKind is null)
                        continue;
                    var detail = _runtime.AttachCompanion(
                        actorTokens[actor.Key], actor);
                    if (detail != null)
                        entities.Add(new SceneEntityOutcome(
                            "Companion", actor.Name, false, detail));
                }
                return null;
            });
            if (relationshipFailure != null)
            {
                await Abort(relationshipFailure);
                return;
            }

            // Phase 5 — pose. One atomic pose import per actor, strictly
            // sequential (the import engine is single-flight), each awaited
            // to its own terminal receipt within a bound. A pose failure
            // rolls ITSELF back and becomes a typed entity outcome; the
            // actor stays restored.
            Step(ScenePhase.ApplyingPose);
            foreach (var actor in scene.Actors)
            {
                if (Guard(operation, cancellation) is { } stop)
                {
                    await Abort(stop);
                    return;
                }

                var poseResult = await ImportPose(
                    operation, actorTokens[actor.Key], actor, cancellation);
                var placement = poseResult == null
                    ? await _runtime.OnFramework(() =>
                        Guard(operation, cancellation)
                            ?? _runtime.PlaceActor(actorTokens[actor.Key], actor))
                    : poseResult;
                entities.Add(placement == null
                    ? new SceneEntityOutcome("Actor", actor.Name, true)
                    : new SceneEntityOutcome("Actor", actor.Name, false, placement));
                done++;
                Step(ScenePhase.ApplyingPose);
            }

            // Phase 6 — presentation.
            Step(ScenePhase.ApplyingPresentation);
            var presentationFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;
                foreach (var actor in scene.Actors)
                    _runtime.SetActorVisibility(actorTokens[actor.Key], actor.Visible);
                return null;
            });
            if (presentationFailure != null)
            {
                await Abort(presentationFailure);
                return;
            }

            // Phase 7 — cameras: the default camera takes the saved default
            // document, additional cameras are created, targets re-resolve
            // against the RESTORED actors, and exactly one camera goes live.
            Step(ScenePhase.ApplyingCameras);
            var cameraFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;

                object? liveCamera = null;
                bool liveIsDefault = false;
                foreach (var camera in scene.Cameras)
                {
                    object? token = null;
                    string? detail;
                    if (camera.IsDefault)
                    {
                        detail = _runtime.ApplyDefaultCamera(camera);
                    }
                    else
                    {
                        token = _runtime.CreateCamera(camera, out detail);
                        if (token != null)
                            operation.CreatedCameras.Add(token);
                    }

                    if (detail != null)
                    {
                        entities.Add(new SceneEntityOutcome(
                            "Camera", camera.Camera!.Name, false, detail));
                        done++;
                        continue;
                    }

                    if (camera.TargetActorKey is { } targetKey)
                    {
                        // The document validated this reference; it can only
                        // miss here if the target actor itself failed, which
                        // is structural and already aborted.
                        var targetDetail = _runtime.SetCameraTarget(
                            token, actorTokens[targetKey], camera.TargetActorName);
                        if (targetDetail != null)
                        {
                            entities.Add(new SceneEntityOutcome(
                                "Camera", camera.Camera!.Name, false,
                                $"The camera was restored but its target was not: {targetDetail}"));
                            done++;
                            continue;
                        }
                    }

                    if (camera.IsLive)
                    {
                        liveCamera = token;
                        liveIsDefault = camera.IsDefault;
                    }
                    entities.Add(new SceneEntityOutcome(
                        "Camera", camera.Camera!.Name, true));
                    done++;
                }

                if (scene.Cameras.Count > 0)
                {
                    var liveDetail = _runtime.SetLiveCamera(
                        liveIsDefault ? null : liveCamera);
                    if (liveDetail != null)
                        entities.Add(new SceneEntityOutcome(
                            "Camera", "Live camera", false, liveDetail));
                }
                return null;
            });
            if (cameraFailure != null)
            {
                await Abort(cameraFailure);
                return;
            }
            Step(ScenePhase.ApplyingCameras);

            // Phase 8 — lights. An unresolvable attachment is a typed
            // refusal of that light, never a world-space spawn.
            Step(ScenePhase.ApplyingLights);
            var lightFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;
                foreach (var light in scene.Lights)
                {
                    object? owner = light.Attachment is { } attachment
                        ? actorTokens[attachment.ActorKey]
                        : null;
                    var token = _runtime.SpawnLight(light, owner, out var detail);
                    if (token is null)
                    {
                        entities.Add(new SceneEntityOutcome(
                            "Light", light.Light!.Name, false,
                            detail ?? "The light could not be spawned."));
                    }
                    else
                    {
                        operation.SpawnedLights.Add(token);
                        // A non-null detail beside a token is a named
                        // degradation (a gobo the client no longer ships),
                        // reported without refusing the light.
                        entities.Add(new SceneEntityOutcome(
                            "Light", light.Light!.Name, true, detail));
                    }
                    done++;
                }
                return null;
            });
            if (lightFailure != null)
            {
                await Abort(lightFailure);
                return;
            }
            Step(ScenePhase.ApplyingLights);

            // Phase 9 — environment, stamped last exactly as both references
            // order it.
            if (scene.Environment is { } environment)
            {
                Step(ScenePhase.ApplyingEnvironment);
                var environmentFailure = await _runtime.OnFramework(() =>
                {
                    if (Guard(operation, cancellation) is { } stop)
                        return stop;
                    _runtime.ApplyEnvironment(environment);
                    entities.Add(new SceneEntityOutcome(
                        "Environment", "Environment", true));
                    done++;
                    return null;
                });
                if (environmentFailure != null)
                {
                    await Abort(environmentFailure);
                    return;
                }
            }

            // Commit — re-guarded: a cancellation or session replacement
            // landing after the last phase rolls back instead of committing.
            Step(ScenePhase.Committing, cancellable: false);
            var committed = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;
                var failures = entities.Where(entity => !entity.Restored).ToList();
                string detail = failures.Count == 0
                    ? $"Loaded {operation.FileName}: {scene.Actors.Count} actors, " +
                      $"{scene.Props.Count} props, {scene.Lights.Count} lights, " +
                      $"{scene.Cameras.Count} cameras."
                    : $"Loaded {operation.FileName} partially: " +
                      $"{failures.Count} of {total} entities could not be restored " +
                      "(the restored entities were kept): " +
                      string.Join("; ", failures.Select(failure =>
                          $"{failure.Kind} '{failure.Name}': {failure.Detail}"));
                // Publishing inside the framework action orders the terminal
                // before any subsequent framework-thread invalidation. Named
                // refusals beside restored entities are typed partial
                // recovery: Failed, with everything that DID restore kept.
                FinishTerminal(
                    operation, SceneOperationKind.Load,
                    failures.Count == 0
                        ? OperationReceiptState.Applied
                        : OperationReceiptState.Failed,
                    detail, entities,
                    Array.Empty<string>(), Array.Empty<string>());
                return null;
            });
            if (committed != null)
            {
                await Abort(committed);
            }
        }
        catch (Exception ex)
        {
            var leftover = await RollbackCreated();
            string detail = $"The load failed unexpectedly: {ex.Message}";
            if (leftover != null)
                detail += $" Rollback also failed, so these are still in the " +
                    $"session and must be removed by hand: {leftover}";
            Finish(
                leftover != null
                    ? OperationReceiptState.Failed
                    : OperationReceiptState.RolledBack,
                detail);
        }
    }

    /// <summary>Arms one actor's atomic pose import and awaits its terminal
    /// receipt within a bound. Returns null on Applied, else the detail.</summary>
    private async Task<string?> ImportPose(
        Operation operation,
        object actor,
        SceneActor data,
        CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<OperationReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? refusal;
        try
        {
            refusal = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;
                return _runtime.ArmPoseImport(
                    actor, data, $"Scene pose: {data.Name}",
                    receipt => completion.TrySetResult(receipt));
            });
        }
        catch (Exception ex)
        {
            return $"The pose import dispatch failed: {ex.Message}";
        }
        if (refusal != null)
            return refusal;

        var finished = await Task.WhenAny(
            completion.Task, Task.Delay(PoseImportTimeout, CancellationToken.None));
        if (finished != completion.Task)
            return "The pose import did not finish within its bound.";

        var receipt = completion.Task.Result;
        return receipt.State == OperationReceiptState.Applied
            ? null
            : receipt.Detail ?? $"The pose import ended {receipt.State}.";
    }

    /// <summary>Bounded readiness barrier over every spawned actor.</summary>
    private async Task<string?> WaitForActors(
        Operation operation, CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + ActorReadyTimeout;
        while (true)
        {
            bool ready;
            try
            {
                ready = await _runtime.OnFramework(() =>
                {
                    if (operation.Invalidated)
                        return true; // The guard below reports the refusal.
                    foreach (var actor in operation.SpawnedActors)
                    {
                        if (!_runtime.ActorReady(actor))
                            return false;
                    }
                    return true;
                });
            }
            catch (Exception ex)
            {
                return $"The readiness barrier failed: {ex.Message}";
            }

            if (operation.Invalidated || cancellation.IsCancellationRequested)
                return "The load was cancelled.";
            if (ready)
                return null;
            if (DateTime.UtcNow >= deadline)
                return "The spawned actors' skeletons did not build within the readiness bound.";
            try
            {
                await Task.Delay(50, _disposal.Token);
            }
            catch (OperationCanceledException)
            {
                return "Poser is shutting down.";
            }
        }
    }

    /// <summary>Checked at the top of every framework-thread action before
    /// its mutations. A replaced session generation is an invalidation: the
    /// token that admitted this operation no longer exists.</summary>
    private string? Guard(Operation operation, CancellationToken cancellation)
    {
        if (operation.Invalidated || cancellation.IsCancellationRequested)
            return operation.Kind == SceneOperationKind.Save
                ? "The save was cancelled."
                : "The load was cancelled.";
        if (_runtime.ActiveSession is not { } live || live != operation.Session)
        {
            operation.Invalidated = true;
            return "The GPose session ended before the operation completed.";
        }
        return null;
    }

    /// <summary>
    /// Reverse-order destruction of everything THIS operation created, plus
    /// the environment and default-camera baseline restores. Framework
    /// thread only and idempotent — each token clears as it is released.
    /// Returns the joined failure detail, or null.
    /// </summary>
    private string? Rollback(Operation operation)
    {
        var failures = new List<string>();

        if (operation.EnvironmentBaseline is { } environment)
        {
            try
            {
                _runtime.ApplyEnvironment(environment);
                operation.EnvironmentBaseline = null;
            }
            catch (Exception ex)
            {
                failures.Add($"environment restore: {ex.Message}");
            }
        }

        if (operation.DefaultCameraBaseline is { } camera)
        {
            try
            {
                _runtime.RestoreDefaultCamera(camera);
                operation.DefaultCameraBaseline = null;
            }
            catch (Exception ex)
            {
                failures.Add($"default camera restore: {ex.Message}");
            }
        }

        RollbackList(operation.CreatedCameras, _runtime.DestroyCamera,
            "camera", failures);
        RollbackList(operation.SpawnedLights, _runtime.DestroyLight,
            "light", failures);
        RollbackList(operation.SpawnedProps, _runtime.DestroyProp,
            "prop", failures);
        RollbackList(operation.SpawnedActors, _runtime.DestroyActor,
            "actor", failures);

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    private static void RollbackList(
        List<object> tokens,
        Action<object> destroy,
        string kind,
        List<string> failures)
    {
        for (int index = tokens.Count - 1; index >= 0; index--)
        {
            try
            {
                destroy(tokens[index]);
                tokens.RemoveAt(index);
            }
            catch (Exception ex)
            {
                failures.Add($"{kind} destruction: {ex.Message}");
            }
        }
    }

    /// <summary>The ONE terminal publication: the receipt state, the progress
    /// phase and the outcome state are derived from a single decision so a UI
    /// can never read a phase that disagrees with its receipt.</summary>
    private void FinishTerminal(
        Operation operation,
        SceneOperationKind kind,
        OperationReceiptState state,
        string detail,
        IReadOnlyList<SceneEntityOutcome> entities,
        IReadOnlyList<string> notes,
        IReadOnlyList<string> evidence)
    {
        var phase = state switch
        {
            OperationReceiptState.Applied => ScenePhase.Completed,
            OperationReceiptState.RolledBack => ScenePhase.RolledBack,
            OperationReceiptState.Cancelled => ScenePhase.Cancelled,
            _ => ScenePhase.Failed,
        };
        var progress = new SceneProgress(
            kind, operation.FileName, phase, 0, 0, false,
            new SceneOutcome(state, detail, entities, notes, evidence));
        var receipt = state switch
        {
            OperationReceiptState.Applied => OperationReceipt.Applied(
                operation.OperationId, operation.Epoch, operation.Session,
                operation.Target, detail),
            OperationReceiptState.RolledBack => OperationReceipt.RolledBack(
                operation.OperationId, operation.Epoch, operation.Session,
                operation.Target, detail),
            OperationReceiptState.Cancelled => OperationReceipt.Cancelled(
                operation.OperationId, operation.Epoch, operation.Session,
                operation.Target, detail),
            _ => OperationReceipt.Failed(
                operation.OperationId, operation.Epoch, operation.Session,
                operation.Target, detail),
        };
        PublishTerminal(operation, progress, receipt);
    }

    /// <summary>Bounded cancel/drain before disposal: admission closes
    /// permanently, tokens cancel, and the active task is joined inside the
    /// bound. An abandoned task cannot mutate anything — every phase
    /// re-guards on the cancelled token.</summary>
    public void Dispose()
    {
        _disposed = true;
        _cancellation?.Cancel();
        _disposal.Cancel();
        try
        {
            _task?.Wait(DisposeDrainTimeout);
        }
        catch (AggregateException)
        {
            // A cancelled or faulted task is a completed drain.
        }
        _cancellation?.Dispose();
        _disposal.Dispose();
    }
}
