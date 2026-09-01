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
/// The single-flight owner of the whole-scene workflow: admission
/// (exact session generation, owner-local operation epoch, operation id),
/// the save capture/write pipeline, the load transaction's ordered phases
/// with reverse-order rollback, and the bounded cancel/drain that runs
/// before disposal. It reuses <see cref="OperationReceipt"/>,
/// <see cref="OperationEpoch"/> and <see cref="SessionGeneration"/> wholesale —
/// there is no scene-specific receipt or epoch type.
///
/// A whole-scene operation has no single target actor, so its receipts target
/// the scene's own logical identity: <c>new ActorId(SceneScopeId, 0)</c>,
/// where the scope id is the document's SceneId for a save and a minted
/// load-scope identity for a load (the file's id is unknown at admission and
/// receipt identity must be stable from Pending to terminal).
///
/// Load semantics: the ENTIRE document is validated before any native
/// mutation; entities spawn additively unless the load was asked to clear the
/// session first (<see cref="SceneLoadOptions.ClearExistingScene"/>, whose
/// sweep is deliberately outside the rollback ledger and says so in the
/// outcome);
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

    /// <summary>Bound for the armed whole-scene capture to answer. The refresh
    /// it waits on has its own tick bound and answers either way, so this only
    /// catches a framework thread that stopped ticking entirely.</summary>
    private static readonly TimeSpan SceneCaptureTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Bound for every attached companion's own body to build. It is
    /// short because it is best-effort: the pose phase reports what did not
    /// make it, rather than the scene waiting on a companion that never
    /// draws.</summary>
    private static readonly TimeSpan CompanionReadyTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Bound for one saved character file to import. Generous because
    /// the transaction behind it extracts a whole package and waits out its
    /// own redraw barrier; cancelling the load cuts it short.</summary>
    /// <summary>
    /// A character-file import's bound. Real packages run to hundreds of
    /// megabytes and the import decompresses, extracts, applies and waits for
    /// a redraw, so this is minutes rather than the one minute it used to be —
    /// a bound that expires mid-import turns a working restore into a named
    /// failure for no reason but impatience.
    /// </summary>
    private static readonly TimeSpan McdfImportTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Building a package from live provider state, per actor. The
    /// exporter walks Penumbra's resource tree and writes the archive.
    /// </summary>
    private static readonly TimeSpan AppearanceSealTimeout =
        TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly ISceneRuntime _runtime;

    /// <summary>Where the operation record goes. Null only under the contract
    /// tests, which assert the published read models rather than the log.
    /// </summary>
    private readonly Dalamud.Plugin.Services.IPluginLog? _log;

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
        Overlays.OverlayNodeService overlays,
        Poser.Services.ILightingService lighting,
        Poser.Services.IVirtualCameraService cameras,
        Poser.Services.IEnvironmentService environment,
        Bindings.StableBindingRegistry bindings,
        Poser.Application.Animation.AnimationSession animation,
        Poser.Services.IGazeService gaze,
        Poser.Application.Integration.ActorIntegrationSession integration,
        Poser.Services.IWorldRenderingService rendering,
        Poser.Services.IActorManager actors,
        Dalamud.Plugin.Services.IObjectTable objects,
        WorldObjects.WorldObjectService worldObjects,
        Poser.Services.IPlaceService place,
        Poser.Library.IMcdfHashIndex mcdfHashes,
        Poser.Application.Selection.SelectionSession selection,
        Poser.Application.Scene.SceneGroups sceneGroups,
        Poser.Library.IPoseLibraryService library,
        Dalamud.Plugin.Services.IPluginLog log)
        : this(new SceneRuntimeAdapter(
            framework, sessions, capture, poses, spawns, skeletons, posing,
            props, overlays, lighting, cameras, environment, bindings,
            animation, gaze, integration, rendering, actors, objects,
            worldObjects, place, mcdfHashes, selection, log), log, sceneGroups,
            library)
    {
    }

    internal SceneWorkflow(
        ISceneRuntime runtime,
        Dalamud.Plugin.Services.IPluginLog? log = null,
        Poser.Application.Scene.SceneGroups? groups = null,
        Poser.Library.IPoseLibraryService? library = null)
    {
        _runtime = runtime;
        _log = log;
        _groups = groups;
        _library = library;
    }

    /// <summary>The sidebar's structure store — null only under the test
    /// runtime, where saves simply carry no structure.</summary>
    private readonly Poser.Application.Scene.SceneGroups? _groups;

    /// <summary>The library index — a completed save tells it, so a fresh
    /// entry lists without anyone rescanning by hand. Null under the test
    /// runtime.</summary>
    private readonly Poser.Library.IPoseLibraryService? _library;

    /// <summary>What including modded appearance would add to a save right
    /// now, in bytes. Read every frame by the save surface, so it stays a
    /// cheap stat over the actors in the session and nothing more.</summary>
    public long EstimatedAppearanceBytes => _runtime.EstimateAppearanceBytes();

    /// <summary>The armed capture's bound. Only the contract tests set it —
    /// waiting the real bound out would make asserting the timeout a
    /// fifteen-second test.</summary>
    internal TimeSpan CaptureBound { get; init; } = SceneCaptureTimeout;

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
        public readonly List<object> StagedOverlays = new();
        public readonly List<object> SpawnedLights = new();
        public readonly List<object> CreatedCameras = new();

        // Borrowed, not created — but rollback still has to undo the claim, and
        // releasing one is the exact inverse of taking it.
        public readonly List<object> BorrowedWorldObjects = new();
        public CameraFile? DefaultCameraBaseline;
        public SceneEnvironment? EnvironmentBaseline;
        public SceneWorld? WorldBaseline;
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

    /// <summary>Starts the whole-scene save: the bone-cache refresh is armed
    /// first, the framework-thread pointer-free capture runs once it lands,
    /// then off-thread validation and the atomic write.</summary>
    public SceneActionResult BeginSave(
        string path,
        string? description = null,
        SceneSaveOptions? options = null)
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
            ScenePhase.RefreshingPoses, 0, 0, true, null);
        RaiseChanged();
        _task = Task.Run(
            () => RunSave(
                operation,
                path,
                description,
                options ?? SceneSaveOptions.Default,
                cancellation),
            CancellationToken.None);
        return SceneActionResult.Ok();
    }

    /// <summary>Starts the whole-scene load transaction. Null options is the
    /// load as it has always been — see <see cref="SceneLoadOptions.Default"/>.
    /// </summary>
    public SceneActionResult BeginLoad(
        string path, SceneLoadOptions? options = null)
    {
        var chosen = options ?? SceneLoadOptions.Default;
        if (AdmissionGate() is { } refused)
            return refused;
        if (_runtime.ActiveSession is not { } session)
            return SceneActionResult.Fail(
                "No GPose session is active; a scene load needs the exact session identity.");
        // A load that includes no category would report success over a session
        // it never touched; refused at admission, where nothing has happened.
        if (!chosen.IncludesAnything)
            return SceneActionResult.Fail(
                "The load has every category switched off, so there is nothing to restore.");

        var operation = Admit(
            Guid.NewGuid(), System.IO.Path.GetFileName(path),
            SceneOperationKind.Load, session);
        var cancellation = _cancellation!.Token;
        _progress = new SceneProgress(
            SceneOperationKind.Load, operation.FileName,
            ScenePhase.Reading, 0, 0, true, null);
        RaiseChanged();
        _task = Task.Run(
            () => RunLoad(operation, path, chosen, cancellation),
            CancellationToken.None);
        return SceneActionResult.Ok();
    }

    // ── Save ─────────────────────────────────────────────────────────────

    private async Task RunSave(
        Operation operation,
        string path,
        string? description,
        SceneSaveOptions options,
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
            // The capture is ARMED, not called: the bone caches it reads are
            // only current for skeletons the per-frame rebuild qualified, so a
            // never-posed actor would serialize its skeleton-build-time values.
            // The arm re-qualifies every actor's skeletons and the capture runs
            // in the update pass that follows — which is why a save that used
            // to be one framework hop is now a bounded await.
            var completion = new TaskCompletionSource<SceneCaptureOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            string? armRefusal;
            try
            {
                armRefusal = await _runtime.OnFramework(() =>
                    Guard(operation, cancellation)
                        ?? _runtime.ArmSceneCapture(
                            operation.SceneScopeId, description,
                            outcome => completion.TrySetResult(outcome)));
            }
            catch (Exception ex)
            {
                Finish(false, $"The capture dispatch failed: {ex.Message}");
                return;
            }
            if (armRefusal != null)
            {
                Finish(false, armRefusal);
                return;
            }

            PublishStep(operation, new SceneProgress(
                SceneOperationKind.Save, operation.FileName,
                ScenePhase.Capturing, 0, 0, true, null));

            var settled = await Task.WhenAny(
                completion.Task,
                Task.Delay(CaptureBound, CancellationToken.None));
            if (settled != completion.Task)
            {
                Finish(false, "The scene capture did not finish within its bound.");
                return;
            }
            var captured = completion.Task.Result;

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

            var notes = captured.Notes.ToList();

            // The actor-entry save narrows to its one actor BEFORE sealing:
            // sealing reads and packages appearance per actor, and an entry
            // save must pay for exactly one.
            var actorIdentities = captured.ActorIdentities;
            if (options.OnlyActorLogicalId is { } only)
            {
                var keep = new HashSet<Guid>(actorIdentities
                    .Where(pair => pair.Value.LogicalId == only)
                    .Select(pair => pair.Key));
                scene.Actors.RemoveAll(entry => !keep.Contains(entry.Key));
                if (scene.Actors.Count != 1)
                {
                    Finish(false,
                        "The actor was not in the capture; it may have just " +
                        "been removed. Nothing was saved.");
                    return;
                }
                actorIdentities = actorIdentities
                    .Where(pair => keep.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            if (options.OnlyOverlayKey is { } onlyOverlay)
            {
                scene.Overlays?.RemoveAll(entry => entry.Key != onlyOverlay);
                if ((scene.Overlays?.Count ?? 0) != 1)
                {
                    Finish(false,
                        "The overlay was not in the capture; it may have " +
                        "just been removed. Nothing was saved.");
                    return;
                }
            }

            // The sidebar's structure rides the document: named groups and
            // the user's root order, referencing the entity lists by the
            // keys they already carry.
            if (options.IncludeStructure && _groups != null)
                WriteStructure(scene, actorIdentities);

            // The group-entry save narrows to the group's members, the
            // actor-entry rule generalized: everything else leaves, and
            // only groups every member of which survived ride along.
            if (options.OnlyEntityKeys is { } onlyKeys)
            {
                var keep = new HashSet<Guid>(onlyKeys);
                // Actor entries key by capture key, not logical id — admit
                // the capture keys of every kept logical id.
                foreach (var pair in actorIdentities)
                    if (keep.Contains(pair.Value.LogicalId))
                        keep.Add(pair.Key);
                scene.Actors.RemoveAll(entry => !keep.Contains(entry.Key));
                scene.Props.RemoveAll(entry => !keep.Contains(entry.Key));
                scene.Lights.RemoveAll(entry => !keep.Contains(entry.Key));
                scene.Cameras.RemoveAll(entry => !keep.Contains(entry.Key));
                scene.Overlays?.RemoveAll(entry => !keep.Contains(entry.Key));
                scene.WorldObjects?.RemoveAll(
                    entry => !keep.Contains(entry.Key));
                scene.Groups?.RemoveAll(group =>
                    group.Members.Count == 0
                    || !group.Members.All(member => keep.Contains(member.Key)));
                // An entry has no sidebar order of its own: its entities
                // seat where the load lands them.
                scene.RootOrder = null;
                if (scene.Actors.Count + scene.Props.Count
                    + scene.Lights.Count + scene.Cameras.Count
                    + (scene.Overlays?.Count ?? 0)
                    + (scene.WorldObjects?.Count ?? 0) == 0)
                {
                    Finish(false,
                        "Nothing the entry names was in the capture; it "
                        + "may have just been removed. Nothing was "
                        + "saved.");
                    return;
                }
                actorIdentities = actorIdentities
                    .Where(pair => keep.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value);
            }

            // The world-object entry saves a SPAWNABLE copy: the load
            // creates its path anew instead of matching the map, so the
            // entry works in any zone.
            if (options.WorldObjectsAsSpawned && scene.WorldObjects != null)
                foreach (var spawnable in scene.WorldObjects)
                    spawnable.Spawned = true;

            // The entry's name IS the thing's name: Stone rail spawns a
            // Stone rail. A group entry names the GROUP and its children
            // keep their own saved names, so the group check leads.
            if (options.EntryName is { Length: > 0 } entryName)
            {
                if (scene.Groups is { Count: 1 } namedGroups)
                    namedGroups[0].Name = entryName;
                else if (scene.WorldObjects is { Count: 1 } namedObjects)
                    namedObjects[0].Name = entryName;
                else if (scene.Props is { Count: 1 } namedProps)
                    namedProps[0].Name = entryName;
                else if (scene.Lights is { Count: 1 } namedLights
                    && namedLights[0].Light is { } lightDocument)
                    lightDocument.Name = entryName;
                else if (scene.Cameras is { Count: 1 } namedCameras
                    && namedCameras[0].Camera is { } cameraDocument)
                    cameraDocument.Name = entryName;
                else if (scene.Overlays is { Count: 1 } namedOverlays
                    && namedOverlays[0].Node is { } nodeDocument)
                    namedOverlays[0].Node =
                        nodeDocument with { Name = entryName };
                else if (scene.Actors is { Count: 1 } namedActors)
                    namedActors[0].Name = entryName;
            }

            // Appearance is sealed BEFORE the policy narrows the document:
            // the policy's job is to drop what could not be sealed, so it has
            // to run second. Only a save that asked for appearance pays for
            // this — it packages mods and reads tens of megabytes.
            IReadOnlyList<string> sealTemporaries = Array.Empty<string>();
            if (options.IncludeModdedAppearance)
            {
                PublishStep(operation, new SceneProgress(
                    SceneOperationKind.Save, operation.FileName,
                    ScenePhase.ApplyingAppearance, 0, 0, false, null));
                var sealed_ = await _runtime.SealAppearance(
                    scene, actorIdentities, AppearanceSealTimeout,
                    cancellation);
                notes.AddRange(sealed_.Notes);
                sealTemporaries = sealed_.TemporaryFiles;
                PublishStep(operation, new SceneProgress(
                    SceneOperationKind.Save, operation.FileName,
                    ScenePhase.Writing, 0, 0, false, null));
            }

            int unsealedAppearance = SceneSavePolicy.Apply(scene, options, notes);

            if (cancellation.IsCancellationRequested)
            {
                Finish(false, "The save was cancelled before writing.", notes);
                return;
            }

            // Character-file references are hashed HERE, off the framework
            // thread, between the capture that produced them and the write:
            // hashing a package is file work the frame the capture ran on may
            // not spend.
            if (_runtime.StampMcdfHashes(scene) is { Count: > 0 } stamped)
                notes.AddRange(stamped);

            // The narrowed, sealed document is what gets written, so its own
            // limits — the per-actor and whole-document appearance caps — are
            // enforced against what is actually going to disk.
            var validated = SceneFileValidation.Validate(scene);
            if (!validated.Succeeded)
            {
                Finish(
                    false,
                    $"The scene did not validate: {validated.Failure!.Detail}",
                    notes);
                return;
            }

            // A .json path exports a Stagehand Stage; what a Stage cannot
            // carry lands in the notes.
            var written = Poser.Files.StageFile.IsStagePath(path)
                ? Poser.Files.StageFile.Write(scene, path, notes)
                : _runtime.WriteScene(scene, path);
            // The writer has streamed every payload into the container, so the
            // packages sealing created are the caller's to drop now — and only
            // now: deleting them earlier would delete the bytes being saved.
            foreach (var temporary in sealTemporaries)
                _runtime.DeleteTemporary(temporary);
            if (!written.Succeeded)
            {
                Finish(
                    false,
                    $"The scene could not be written: {written.Failure!.Detail}",
                    notes,
                    written.RecoveryEvidencePaths);
                return;
            }

            var summary =
                $"Saved {scene.Actors.Count} actors, {scene.Props.Count} objects, " +
                $"{scene.Lights.Count} lights and {scene.Cameras.Count} cameras to " +
                $"{operation.FileName}.";
            if (notes.Count > 0)
                summary += $" {notes.Count} entities carried notes.";

            // A save that dropped appearance the user explicitly asked for is
            // NOT a plain success. It wrote a file, so it is not a failure
            // either — it is the partial state the entity list exists for, and
            // it has to reach the notification rather than only the log.
            if (unsealedAppearance > 0)
            {
                var appearanceOutcomes = new List<SceneEntityOutcome>();
                foreach (var actor in scene.Actors)
                    appearanceOutcomes.Add(
                        new SceneEntityOutcome("Actor", actor.Name, true));
                appearanceOutcomes.Add(new SceneEntityOutcome(
                    "Character file",
                    unsealedAppearance == 1 ? "1 actor" : $"{unsealedAppearance} actors",
                    false,
                    "The appearance package could not be built, so the scene "
                    + "saved without it."));
                FinishTerminal(
                    operation, SceneOperationKind.Save,
                    OperationReceiptState.Failed,
                    summary + " Modded appearance was requested but not saved.",
                    appearanceOutcomes, notes, Array.Empty<string>());
                return;
            }

            Finish(true, summary, notes);
            // The file exists NOW: tell the index, so the entry lists in
            // the library and the portal without a hand-driven refresh.
            _library?.RequestScan();
        }
        catch (Exception ex)
        {
            Finish(false, $"The save failed unexpectedly: {ex.Message}");
        }
    }

    // ── Load ─────────────────────────────────────────────────────────────

    private async Task RunLoad(
        Operation operation,
        string path,
        SceneLoadOptions options,
        CancellationToken cancellation)
    {
        var entities = new List<SceneEntityOutcome>();
        // Facts about the OPERATION rather than about any one entity: what a
        // destroy-first clear cost, and which categories the user left out.
        var notes = new List<string>();
        int total = 0;
        int done = 0;

        void Step(ScenePhase phase, bool cancellable = true) =>
            PublishStep(operation, new SceneProgress(
                SceneOperationKind.Load, operation.FileName,
                phase, done, total, cancellable, null));

        void Finish(OperationReceiptState state, string detail) =>
            FinishTerminal(
                operation, SceneOperationKind.Load, state, detail,
                entities, notes, Array.Empty<string>());

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
            // A .json path is a Stagehand Stage: the read translates it
            // into a scene document and the rest of the load never knows.
            var read = Poser.Files.StageFile.IsStagePath(path)
                ? Poser.Files.StageFile.Read(path, notes)
                : _runtime.ReadScene(path);
            if (!read.Succeeded || read.Scene is not { } scene)
            {
                // Nothing native has run, so there is nothing to roll back:
                // a corrupt, oversized or future file is a plain Failed.
                Finish(OperationReceiptState.Failed, read.Failure!.Detail);
                return;
            }

            // The per-category views. An excluded category is an EMPTY view
            // rather than a flag consulted at each of its phases: every phase
            // then reads one list, and a category can never be half-skipped.
            var actors = options.IncludeActors
                ? (IReadOnlyList<SceneActor>)scene.Actors
                : Array.Empty<SceneActor>();
            var props = options.IncludeProps
                ? (IReadOnlyList<SceneProp>)scene.Props
                : Array.Empty<SceneProp>();
            var overlays = options.IncludeOverlays
                ? (IReadOnlyList<SceneOverlay>)(scene.Overlays ?? [])
                : Array.Empty<SceneOverlay>();
            // A borrowed map object is the one entity whose view is decided by
            // WHERE THE SESSION IS rather than by an option. It is not a thing
            // the load can create: it can only take back an object this map is
            // already standing. The gate runs here, before any native work, so
            // a scene loaded in the wrong zone refuses its borrowed entries by
            // name and lands everything else.
            // SPAWNED objects are the exception to the zone gate: they are
            // created by path, standing anywhere; only BORROWED entries can
            // be refused for being in the wrong zone.
            var worldObjects = (IReadOnlyList<SceneWorldObject>)(
                scene.WorldObjects ?? []);
            int borrowedCount = 0;
            foreach (var entry in worldObjects)
                if (!entry.Spawned)
                    borrowedCount++;
            uint currentTerritory = borrowedCount == 0
                ? 0u
                : await _runtime.OnFramework(_runtime.CurrentTerritoryId);
            if (borrowedCount > 0 &&
                (currentTerritory == 0 || currentTerritory != scene.TerritoryId))
            {
                string where = string.IsNullOrWhiteSpace(scene.PlaceName)
                    ? $"territory {scene.TerritoryId}"
                    : scene.PlaceName;
                notes.Add(
                    $"This scene borrowed {borrowedCount} map " +
                    $"{(borrowedCount == 1 ? "object" : "objects")} in " +
                    $"{where}, which is not where you are. " +
                    $"{(borrowedCount == 1 ? "It was" : "They were")} " +
                    "left alone.");
                var spawnedOnly = new List<SceneWorldObject>();
                foreach (var entry in worldObjects)
                    if (entry.Spawned)
                        spawnedOnly.Add(entry);
                worldObjects = spawnedOnly;
            }
            var lights = options.IncludeLights
                ? (IReadOnlyList<SceneLight>)scene.Lights
                : Array.Empty<SceneLight>();
            var cameras = options.IncludeCameras
                ? (IReadOnlyList<SceneCamera>)scene.Cameras
                : Array.Empty<SceneCamera>();
            var environment = options.IncludeEnvironment
                ? scene.Environment
                : null;

            // What the file HAS that this load was told to leave alone. Stated
            // once, as a note, so a scene that came back with fewer entities
            // than it was saved with says why rather than looking short.
            AppendSkipNote(notes, "actors", options.IncludeActors, scene.Actors.Count);
            AppendSkipNote(notes, "objects", options.IncludeProps, scene.Props.Count);
            AppendSkipNote(notes, "lights", options.IncludeLights, scene.Lights.Count);
            AppendSkipNote(notes, "cameras", options.IncludeCameras, scene.Cameras.Count);
            AppendSkipNote(
                notes, "overlays", options.IncludeOverlays,
                scene.Overlays?.Count ?? 0);
            AppendSkipNote(
                notes, "the environment", options.IncludeEnvironment,
                scene.Environment is null ? 0 : 1);

            // Relative placement rebases the READ document, before one native
            // call: a file with no origin refuses HERE, where nothing has
            // happened and there is nothing to roll back.
            if (options.PlaceRelativeToCurrentOrigin)
            {
                var origin = await _runtime.OnFramework(_runtime.CurrentOrigin);
                if (origin is not { } anchor)
                {
                    Finish(
                        OperationReceiptState.Failed,
                        "There is nobody to place the scene relative to, so the " +
                        "load was not started. Load it as saved instead.");
                    return;
                }
                if (SceneRelativePlacement.Rebase(scene, anchor) is { } refusal)
                {
                    Finish(OperationReceiptState.Failed, refusal);
                    return;
                }
                notes.Add("Placed relative to where you are standing.");

                // The rebase moved everything Poser places. It did NOT move the
                // map's own objects, because it cannot: they are matched by the
                // point the map stands them at.
                if (worldObjects.Count > 0)
                    notes.Add(
                        $"The {worldObjects.Count} borrowed map " +
                        $"{(worldObjects.Count == 1 ? "object stays" : "objects stay")} " +
                        "where the map has them; only what Poser placed moved.");
            }

            // The object-entry placement: the caller resolved the CURRENT
            // anchor; the document carries the SAVED one. A mode whose saved
            // anchor the file does not record refuses before anything is
            // touched.
            if (options.Placement != Poser.Files.ObjectPlacementMode.AsSaved)
            {
                Poser.Files.PlacementAnchorData? savedAnchor;
                if (options.Placement ==
                    Poser.Files.ObjectPlacementMode.InFrontOfCamera)
                {
                    // The anchor is the content ITSELF: its centroid moves
                    // to the point in front of the camera, no turn — the
                    // light spawn's behavior, generalized. An entry that
                    // places nothing simply loads as saved.
                    savedAnchor = SceneContentCentroid(scene) is { } centroid
                        ? new Poser.Files.PlacementAnchorData
                        {
                            Position = centroid,
                            Yaw = options.PlacementYaw,
                        }
                        : null;
                    if (savedAnchor is null)
                        notes.Add(
                            "The entry places nothing, so it loaded as "
                            + "saved.");
                }
                else
                {
                    savedAnchor = options.Placement ==
                        Poser.Files.ObjectPlacementMode.RelativeToCamera
                            ? scene.CameraAnchor
                            : scene.ActorAnchor;
                    // No saved anchor is no longer a refusal (ruled
                    // 2026-08-31): the content's CENTROID stands in, so
                    // the content lands ON the current camera or actor —
                    // no turn — instead of keeping an offset the entry
                    // never recorded.
                    if (savedAnchor is null)
                    {
                        savedAnchor =
                            SceneContentCentroid(scene) is { } centre
                                ? new Poser.Files.PlacementAnchorData
                                {
                                    Position = centre,
                                    Yaw = options.PlacementYaw,
                                }
                                : null;
                        if (savedAnchor is null)
                            notes.Add(
                                "The entry places nothing, so it loaded as "
                                + "saved.");
                        else
                            notes.Add(
                                "No saved anchor: the content's centre "
                                + "lands on the anchor instead.");
                    }
                }
                if (savedAnchor is { } anchor)
                {
                    if (ScenePlacementRebase.Rebase(
                            scene, anchor,
                            options.PlacementPosition, options.PlacementYaw)
                        is { } placementRefusal)
                    {
                        Finish(OperationReceiptState.Failed, placementRefusal);
                        return;
                    }
                    notes.Add(options.Placement switch
                    {
                        Poser.Files.ObjectPlacementMode.RelativeToCamera =>
                            "Placed relative to the camera.",
                        Poser.Files.ObjectPlacementMode.InFrontOfCamera =>
                            "Placed in front of the camera.",
                        _ => "Placed relative to the actor.",
                    });
                }
            }

            total = actors.Count + props.Count +
                lights.Count + cameras.Count +
                overlays.Count + worldObjects.Count +
                (environment is null ? 0 : 1);

            // Phase 2 — baselines, then spawn/admit every entity that other
            // phases depend on. Actor spawn failures are structural: pose
            // and relationships cannot proceed against a hole.
            //
            // The destroy-first clear runs at the head of the same framework
            // action, so nothing this load creates can be caught by the sweep
            // that was meant to precede it. It is deliberately OUTSIDE the
            // rollback ledger: rollback undoes what this operation CREATED, and
            // no ledger can resurrect an actor the user asked to be rid of —
            // which is why the clear reports what it cost.
            var actorTokens = new Dictionary<Guid, object>();
            // Per-kind key→token maps feed the structure restore: groups
            // and the root order reference entities by these keys.
            var propTokens = new Dictionary<Guid, object>();
            var overlayTokens = new Dictionary<Guid, object>();
            var worldObjectTokens = new Dictionary<Guid, object>();
            var lightTokens = new Dictionary<Guid, object>();
            var cameraTokens = new Dictionary<Guid, object>();
            Step(ScenePhase.SpawningEntities);
            var spawnFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;

                if (options.ClearExistingScene &&
                    _runtime.ClearScene().Summary() is { } cleared)
                    notes.Add(cleared);

                // A baseline is captured only for what this load will WRITE:
                // restoring an environment the load never touched would undo
                // edits the user made before it.
                if (environment is not null || options.IncludeEnvironment)
                {
                    operation.EnvironmentBaseline = _runtime.CaptureEnvironmentState();
                    operation.WorldBaseline = _runtime.CaptureWorldState();
                }
                if (cameras.Count > 0)
                    operation.DefaultCameraBaseline =
                        _runtime.CaptureDefaultCameraState();

                foreach (var actor in actors)
                {
                    var token = _runtime.SpawnActor(actor, out var detail);
                    if (token is null)
                        return $"Actor '{actor.Name}' could not be spawned: " +
                            $"{detail ?? "the spawn failed."}";
                    operation.SpawnedActors.Add(token);
                    actorTokens[actor.Key] = token;
                }

                foreach (var prop in props)
                {
                    var token = _runtime.SpawnProp(prop, out var detail);
                    if (token is null)
                    {
                        entities.Add(new SceneEntityOutcome(
                            "Object", prop.Name, false,
                            detail ?? "The object could not be spawned."));
                        continue;
                    }
                    operation.SpawnedProps.Add(token);
                    propTokens[prop.Key] = token;
                    entities.Add(new SceneEntityOutcome("Object", prop.Name, true));
                }

                // An overlay node that will not stage is a NAMED refusal, not
                // a structural one: the scene it decorates is still a scene
                // without it, exactly as a prop's is.
                foreach (var overlay in overlays)
                {
                    string name = overlay.Node?.Name ?? "Overlay";
                    var token = _runtime.SpawnOverlay(overlay, out var detail);
                    if (token is null)
                    {
                        entities.Add(new SceneEntityOutcome(
                            "Overlay", name, false,
                            detail ?? "The overlay could not be staged."));
                        continue;
                    }
                    operation.StagedOverlays.Add(token);
                    overlayTokens[overlay.Key] = token;
                    entities.Add(new SceneEntityOutcome(
                        "Overlay", name, true, detail));
                }

                // Borrowing back the map's own objects. A refusal here is
                // NAMED and never structural: the map may have been rebuilt,
                // the object may already be borrowed, or it may simply not be
                // standing where this scene recorded it — and a scene is still
                // a scene without it.
                foreach (var worldObject in worldObjects)
                {
                    string name = WorldObjects.WorldObjectService.DisplayName(
                        worldObject.Path);
                    var token = _runtime.AdoptWorldObject(
                        worldObject, out var detail);
                    if (token is null)
                    {
                        entities.Add(new SceneEntityOutcome(
                            "World object", name, false,
                            detail ?? "The map object could not be borrowed."));
                        continue;
                    }
                    operation.BorrowedWorldObjects.Add(token);
                    worldObjectTokens[worldObject.Key] = token;
                    entities.Add(
                        new SceneEntityOutcome("World object", name, true));
                }
                return null;
            });
            if (spawnFailure != null)
            {
                await Abort(spawnFailure);
                return;
            }
            done = props.Count;

            // Phase 3 — bounded readiness barrier: pose needs the spawned
            // actors' skeletons, which build with their draw objects.
            Step(ScenePhase.AwaitingActors);
            var ready = await WaitForActors(operation, cancellation);
            if (ready != null)
            {
                await Abort(ready);
                return;
            }

            // Phase 3b — character files, BEFORE anything that hangs off a
            // body. An MCDF import redraws the actor, which destroys its draw
            // object and every skeleton with it: a pose applied first would be
            // thrown away, and a companion attached first would go with the old
            // body. Each import runs through the ORDINARY MCDF transaction, so
            // the ownership it registers — and the by-name unlock-and-restore
            // teardown that ownership buys — is the same one a hand-driven
            // import leaves behind.
            if (actors.Any(entry => entry.Mcdf is not null))
            {
                Step(ScenePhase.ApplyingAppearance);
                foreach (var actor in actors)
                {
                    if (actor.Mcdf is null)
                        continue;
                    if (Guard(operation, cancellation) is { } stop)
                    {
                        await Abort(stop);
                        return;
                    }
                    var appearance = await _runtime.ImportMcdf(
                        path, actorTokens[actor.Key], actor,
                        McdfImportTimeout, cancellation);
                    // A missing package is a refusal by name; a package whose
                    // bytes moved on is restored WITH the divergence named.
                    // Neither is ever a silent skip.
                    if (appearance.Detail is { } detail)
                        entities.Add(new SceneEntityOutcome(
                            "Character file", actor.Name,
                            appearance.Restored, detail));
                }

                // The redraws rebuilt the skeletons every later phase reads.
                Step(ScenePhase.AwaitingActors);
                var rebuilt = await WaitForActors(operation, cancellation);
                if (rebuilt != null)
                {
                    await Abort(rebuilt);
                    return;
                }
            }

            // Phase 4 — explicit relationships.
            Step(ScenePhase.ApplyingRelationships);
            var relationshipFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;
                foreach (var actor in actors)
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

            // Phase 4a — a companion's own BODY builds several frames after
            // its attachment lands, and a companion pose has nothing to land
            // on until its skeleton exists. Bounded, and deliberately NOT
            // structural: a companion that never draws costs one named refusal
            // in the pose phase, never the whole scene.
            if (actors.Any(entry => entry.CompanionPose is not null))
            {
                Step(ScenePhase.AwaitingActors);
                await WaitForCompanions(
                    operation, actors, actorTokens, cancellation);
            }

            // Phase 4b — FREEZE, before the pose. A scene carries pose data
            // and no animation: a timeline id resolves against the loading
            // client's own game and mods, so replaying one would show a
            // different thing on every machine, or nothing. Stopping the actor
            // first is what makes the pose land on a held frame and the load
            // deterministic.
            Step(ScenePhase.FreezingActors);
            var freezeFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;
                foreach (var actor in actors)
                {
                    // VISIBILITY BEFORE THE POSE, and the ordering is the
                    // invariant, not the mechanism. Hiding is a fade today
                    // (ActorSpawnNativeAdapter.SetAlpha) and a fade cannot
                    // cost an actor its skeleton — but this phase used to run
                    // after the pose, so when hiding WAS a draw-state
                    // teardown a scene saved with a hidden actor threw away
                    // the pose it had just applied to it. Stated here so no
                    // later change to how an actor hides can bring that back.
                    _runtime.SetActorVisibility(actorTokens[actor.Key], actor.Visible);
                    var detail = _runtime.FreezeActor(actorTokens[actor.Key]);
                    if (detail != null)
                        entities.Add(new SceneEntityOutcome(
                            "Animation", actor.Name, false, detail));
                }
                return null;
            });
            if (freezeFailure != null)
            {
                await Abort(freezeFailure);
                return;
            }

            // Phase 5 — pose. One atomic pose import per actor, strictly
            // sequential (the import engine is single-flight), each awaited
            // to its own terminal receipt within a bound. A pose failure
            // rolls ITSELF back and becomes a typed entity outcome; the
            // actor stays restored.
            Step(ScenePhase.ApplyingPose);
            foreach (var actor in actors)
            {
                if (Guard(operation, cancellation) is { } stop)
                {
                    await Abort(stop);
                    return;
                }

                var token = actorTokens[actor.Key];
                var poseResult = await ImportPose(
                    operation,
                    receipt => _runtime.ArmPoseImport(
                        token, actor, $"Scene pose: {actor.Name}", receipt),
                    cancellation);
                var placement = poseResult == null
                    ? await _runtime.OnFramework(() =>
                        Guard(operation, cancellation)
                            ?? _runtime.PlaceActor(token, actor))
                    : poseResult;
                entities.Add(placement == null
                    ? new SceneEntityOutcome("Actor", actor.Name, true)
                    : new SceneEntityOutcome("Actor", actor.Name, false, placement));

                // The companion's OWN pose, after its owner's: the same
                // single-flight engine takes one import at a time, and a
                // companion that could not be posed is a named refusal beside
                // a restored actor, never a failed scene.
                if (actor.CompanionPose is not null)
                {
                    var companion = await ImportPose(
                        operation,
                        receipt => _runtime.ArmCompanionPoseImport(
                            token, actor, $"Scene companion pose: {actor.Name}",
                            receipt),
                        cancellation);
                    if (companion != null)
                        entities.Add(new SceneEntityOutcome(
                            "Companion", actor.Name, false, companion));
                }
                done++;
                Step(ScenePhase.ApplyingPose);
            }

            // Phase 6 — presentation. Visibility is NOT here; it rode with the
            // animation, before the pose (see phase 4b).
            Step(ScenePhase.ApplyingPresentation);
            var presentationFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;
                foreach (var actor in actors)
                {
                    // Gaze comes AFTER the pose: the look-at re-drives its
                    // channels every frame, and its Entity target is another
                    // RESTORED actor, so it needs every token to exist. The
                    // document validated the reference, so a stated key is
                    // always present here.
                    var target = actor.Gaze?.TargetActorKey is { } gazeTarget
                        ? actorTokens[gazeTarget]
                        : null;
                    var detail = _runtime.ApplyActorGaze(
                        actorTokens[actor.Key], actor, target);
                    if (detail != null)
                        entities.Add(new SceneEntityOutcome(
                            "Gaze", actor.Name, false, detail));
                }
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
                foreach (var camera in cameras)
                {
                    object? token = null;
                    string? detail;
                    if (camera.IsDefault)
                    {
                        detail = _runtime.ApplyDefaultCamera(camera);
                        // The default camera mints a structure token too:
                        // without one, a saved group that held the Main
                        // Camera silently lost it on every load.
                        if (detail == null
                            && _runtime.DefaultCameraToken() is { } main)
                            cameraTokens[camera.Key] = main;
                    }
                    else
                    {
                        token = _runtime.CreateCamera(camera, out detail);
                        if (token != null)
                        {
                            operation.CreatedCameras.Add(token);
                            cameraTokens[camera.Key] = token;
                        }
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
                        // miss here if the target actor itself failed (which is
                        // structural and already aborted) or if this load was
                        // told to leave the actors out — then the camera is
                        // restored and its target refused BY NAME.
                        if (!actorTokens.ContainsKey(targetKey))
                        {
                            entities.Add(new SceneEntityOutcome(
                                "Camera", camera.Camera!.Name, false,
                                "The camera was restored but it follows an " +
                                "actor this load did not restore."));
                            done++;
                            continue;
                        }
                        var targetDetail = _runtime.SetCameraTarget(
                            token, actorTokens[targetKey],
                            camera.TargetActorName, camera.IsTargetLocked);
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

                if (cameras.Count > 0)
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
                foreach (var light in lights)
                {
                    // An attachment whose owner was not loaded is a NAMED
                    // refusal of that light, exactly as an unresolvable
                    // attachment already is: a light is never silently
                    // detached into world space.
                    if (light.Attachment is { } unresolved &&
                        !actorTokens.ContainsKey(unresolved.ActorKey))
                    {
                        entities.Add(new SceneEntityOutcome(
                            "Light", light.Light!.Name, false,
                            "The light is attached to an actor this load did " +
                            "not restore, so it was not spawned."));
                        done++;
                        continue;
                    }
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
                        lightTokens[light.Key] = token;
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

            // Phase 9 — environment and the session-wide toggles, stamped last
            // exactly as both references order it. The world block runs even
            // when the file states none: "no frozen water, no frozen physics"
            // is what a scene taken with the game running says, so a load into
            // a session that froze either one must RELEASE it, or the scene did
            // not restore what it saved.
            {
                Step(ScenePhase.ApplyingEnvironment);
                var environmentFailure = await _runtime.OnFramework(() =>
                {
                    if (Guard(operation, cancellation) is { } stop)
                        return stop;
                    if (environment is { } stated)
                    {
                        _runtime.ApplyEnvironment(stated);
                        entities.Add(new SceneEntityOutcome(
                            "Environment", "Environment", true));
                        done++;
                    }
                    // Reported only when something DEGRADED: a toggle that
                    // landed is not worth a row beside the entities. The
                    // session-wide toggles belong to the environment category,
                    // so a load that leaves the environment out leaves them
                    // exactly as the user set them.
                    if (options.IncludeEnvironment &&
                        _runtime.ApplyWorld(scene.World ?? new SceneWorld())
                        is { } detail)
                        entities.Add(new SceneEntityOutcome(
                            "World", "World", false, detail));
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
                    ? $"Loaded {operation.FileName}: " +
                      $"{Count(actors.Count, "actor")}, " +
                      $"{Count(props.Count, "object")}, " +
                      $"{Count(lights.Count, "light")}, " +
                      $"{Count(cameras.Count, "camera")}."
                    : $"Loaded {operation.FileName} partially: " +
                      $"{failures.Count} of {total} " +
                      (total == 1 ? "entity" : "entities") + " could not be " +
                      "restored (everything that did restore was kept): " +
                      string.Join("; ", failures.Select(failure =>
                          $"{failure.Kind} '{failure.Name}': {failure.Detail}"));
                // The document's structure is STAGED, not applied: the
                // freshly spawned entities bind on the next snapshot
                // publish, so the sidebar resolves the tokens and rebuilds
                // groups and order then.
                StageStructure(
                    scene, actorTokens, propTokens, overlayTokens,
                    worldObjectTokens, lightTokens, cameraTokens);

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
                    notes, Array.Empty<string>());
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

    // ── sidebar structure: save-side write, load-side staging ───────────

    /// <summary>A completed load's structure — the document's groups and
    /// root order plus the file-key → runtime-token map — waiting for the
    /// sidebar. The spawned entities bind on the next snapshot publish;
    /// the sidebar resolves the tokens then and clears this.</summary>
    public sealed class PendingStructure
    {
        public required IReadOnlyList<SceneGroupEntry> Groups { get; init; }
        public required IReadOnlyList<SceneStructureRef>? RootOrder { get; init; }
        public required IReadOnlyDictionary<Guid, object> Tokens { get; init; }
    }

    public PendingStructure? PendingSceneStructure { get; private set; }

    public void ClearPendingStructure() => PendingSceneStructure = null;

    private void StageStructure(
        SceneFile scene, params Dictionary<Guid, object>[] tokenMaps)
    {
        if ((scene.Groups?.Count ?? 0) == 0
            && (scene.RootOrder?.Count ?? 0) == 0)
            return;
        var tokens = new Dictionary<Guid, object>();
        foreach (var map in tokenMaps)
            foreach (var pair in map)
                tokens.TryAdd(pair.Key, pair.Value);
        PendingSceneStructure = new PendingStructure
        {
            Groups = scene.Groups
                ?? (IReadOnlyList<SceneGroupEntry>)Array.Empty<SceneGroupEntry>(),
            RootOrder = scene.RootOrder,
            Tokens = tokens,
        };
    }

    /// <summary>Writes the sidebar's structure into the document. Actor
    /// members translate LOGICAL id → capture key through the identities
    /// the capture reported; every other kind's key IS its logical id.
    /// The store is read live off the save's worker thread, so a rare
    /// concurrent structural edit skips the structure rather than failing
    /// the save.</summary>
    private void WriteStructure(
        SceneFile scene,
        IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId> actorIdentities)
    {
        try
        {
            var actorKeys = new Dictionary<Guid, Guid>();
            foreach (var pair in actorIdentities)
                actorKeys[pair.Value.LogicalId] = pair.Key;

            SceneStructureRef? RefOf(
                global::Poser.Domain.Identity.SelectionId member)
            {
                string? kind = member.Kind switch
                {
                    global::Poser.Domain.Identity.SceneEntityKind.Actor => "actor",
                    global::Poser.Domain.Identity.SceneEntityKind.Prop => "prop",
                    global::Poser.Domain.Identity.SceneEntityKind.WorldObject =>
                        "worldObject",
                    global::Poser.Domain.Identity.SceneEntityKind.Light => "light",
                    global::Poser.Domain.Identity.SceneEntityKind.Camera => "camera",
                    global::Poser.Domain.Identity.SceneEntityKind.Overlay =>
                        "overlay",
                    _ => null,
                };
                if (kind == null)
                    return null;
                Guid? logical = member switch
                {
                    { Actor: { } actor } => actor.LogicalId,
                    { Prop: { } prop } => prop.LogicalId,
                    { WorldObject: { } worldObject } => worldObject.LogicalId,
                    { Light: { } light } => light.LogicalId,
                    { Camera: { } camera } => camera.LogicalId,
                    { Overlay: { } overlay } => overlay.LogicalId,
                    _ => null,
                };
                if (logical is not { } key)
                    return null;
                if (kind == "actor"
                    && !actorKeys.TryGetValue(key, out key))
                    return null;
                return new SceneStructureRef { Kind = kind, Key = key };
            }

            var groups = new List<SceneGroupEntry>();
            foreach (var group in _groups!.All)
            {
                var entry = new SceneGroupEntry
                {
                    Key = group.Id,
                    Name = group.Name,
                    Locked = group.Locked,
                };
                foreach (var member in group.Members)
                    if (RefOf(member) is { } reference)
                        entry.Members.Add(reference);
                if (entry.Members.Count >= 2)
                    groups.Add(entry);
            }
            if (groups.Count > 0)
                scene.Groups = groups;

            var order = new List<SceneStructureRef>();
            foreach (var slot in _groups.RootOrder)
            {
                if (slot.IsGroup)
                    order.Add(new SceneStructureRef
                    {
                        Kind = "group",
                        Key = slot.GroupId,
                    });
                else if (slot.Entity is { } entity
                    && RefOf(entity) is { } reference)
                    order.Add(reference);
            }
            if (order.Count > 0)
                scene.RootOrder = order;
        }
        catch (InvalidOperationException)
        {
            scene.Groups = null;
            scene.RootOrder = null;
        }
    }

    /// <summary>The average position of everything the document PLACES —
    /// actors, props, unattached lights, spawned world objects, free
    /// cameras. Null when it places nothing.</summary>
    private static System.Numerics.Vector3? SceneContentCentroid(
        SceneFile scene)
    {
        var sum = System.Numerics.Vector3.Zero;
        int counted = 0;
        foreach (var actor in scene.Actors)
            if (actor.ModelTransform is { } placement)
            {
                sum += placement.Position;
                counted++;
            }
        foreach (var prop in scene.Props)
        {
            sum += prop.Transform.Position;
            counted++;
        }
        foreach (var light in scene.Lights)
            if (light.Attachment is null && light.Light is { } document)
            {
                sum += document.Transform.Position;
                counted++;
            }
        foreach (var worldObject in scene.WorldObjects ?? [])
            if (worldObject.Spawned)
            {
                sum += worldObject.Transform.Position;
                counted++;
            }
        foreach (var camera in scene.Cameras)
            if (camera.Camera is { Kind: global::Poser.Domain.Scene.CameraKind.Free } document)
            {
                sum += document.Position;
                counted++;
            }
        return counted == 0 ? null : sum / counted;
    }

    /// <summary>One line stating a category the user left out, and only when
    /// the FILE actually carries something in it: "props were not loaded" over
    /// a scene with no props says nothing true about this load.</summary>
    private static void AppendSkipNote(
        List<string> notes, string category, bool included, int count)
    {
        if (included || count == 0)
            return;
        notes.Add(count == 1 && category.StartsWith("the ", StringComparison.Ordinal)
            ? $"The file's {category[4..]} was not loaded."
            : $"The file's {count} {category} were not loaded.");
    }

    /// <summary>
    /// Arms ONE atomic pose import — an actor's or its companion's, through
    /// <paramref name="arm"/> — and awaits its TERMINAL receipt within a
    /// bound. Returns null on Applied, else the detail.
    ///
    /// <para>Pending receipts are DROPPED rather than latched. The import
    /// engine acknowledges an admitted import by publishing a Pending receipt
    /// synchronously from inside <paramref name="arm"/>
    /// (<c>PoseImportCapture.Reserve</c> → <c>CleanPoseFacade.BeginImport</c>),
    /// and that receipt's Detail is the import's DESCRIPTION. Completing on it
    /// made every scene pose import report itself failed with its own label —
    /// the reported "1 of 4 entities could not be restored" whose only stated
    /// reason was <c>Scene pose: &lt;actor&gt;</c>. Only a terminal state is an
    /// answer; <see cref="OperationReceiptState.Pending"/> is the explicit
    /// non-terminal acknowledgement and says nothing about the outcome.</para>
    /// </summary>
    private async Task<string?> ImportPose(
        Operation operation,
        Func<Action<OperationReceipt>, string?> arm,
        CancellationToken cancellation)
    {
        var completion = new TaskCompletionSource<OperationReceipt>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        string? refusal;
        try
        {
            refusal = await _runtime.OnFramework(() =>
                Guard(operation, cancellation)
                    ?? arm(receipt =>
                    {
                        if (receipt.State != OperationReceiptState.Pending)
                            completion.TrySetResult(receipt);
                    }));
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

    /// <summary>
    /// Best-effort barrier over every attached companion whose pose the scene
    /// carries. It answers when they have all built, when the operation is
    /// invalidated, or when the bound expires — never as a failure, because a
    /// companion that never draws is the pose phase's named refusal to report,
    /// not a reason to tear down a restored scene.
    /// </summary>
    private async Task WaitForCompanions(
        Operation operation,
        IReadOnlyList<SceneActor> actors,
        Dictionary<Guid, object> actorTokens,
        CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + CompanionReadyTimeout;
        while (true)
        {
            bool ready;
            try
            {
                ready = await _runtime.OnFramework(() =>
                {
                    if (operation.Invalidated)
                        return true;
                    foreach (var entry in actors)
                    {
                        if (entry.CompanionPose is null)
                            continue;
                        if (!_runtime.CompanionReady(actorTokens[entry.Key]))
                            return false;
                    }
                    return true;
                });
            }
            catch (Exception)
            {
                // The framework thread is gone; the pose phase reports it.
                return;
            }

            if (ready || operation.Invalidated ||
                cancellation.IsCancellationRequested ||
                DateTime.UtcNow >= deadline)
                return;
            try
            {
                await Task.Delay(50, _disposal.Token);
            }
            catch (OperationCanceledException)
            {
                return;
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

        if (operation.WorldBaseline is { } world)
        {
            try
            {
                _runtime.ApplyWorld(world);
                operation.WorldBaseline = null;
            }
            catch (Exception ex)
            {
                failures.Add($"world toggle restore: {ex.Message}");
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

        // First out, because it is the one rollback step that GIVES SOMETHING
        // BACK rather than destroying it: whatever else fails below, the map
        // must not be left holding this load's displacements.
        RollbackList(operation.BorrowedWorldObjects, _runtime.ReleaseWorldObject,
            "world object release", failures);
        RollbackList(operation.CreatedCameras, _runtime.DestroyCamera,
            "camera", failures);
        RollbackList(operation.SpawnedLights, _runtime.DestroyLight,
            "light", failures);
        RollbackList(operation.StagedOverlays, _runtime.DestroyOverlay,
            "overlay", failures);
        RollbackList(operation.SpawnedProps, _runtime.DestroyProp,
            "object", failures);
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

    /// <summary>A count and its noun, agreeing. Scene outcomes are read by a
    /// user who just watched the thing happen; "1 actors" reads as a bug in
    /// the count, not a bug in the grammar.</summary>
    private static string Count(int value, string noun) =>
        $"{value} {noun}{(value == 1 ? string.Empty : "s")}";

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
        // Every refusal leaves here carrying its next step. Filling it in at
        // the ONE terminal publication rather than at each of the twenty-odd
        // refusal sites is what makes "a refused row explains itself" an
        // invariant rather than a habit a new site can forget.
        entities = entities
            .Select(entity => entity.Restored || entity.Remedy != null
                ? entity
                : entity with { Remedy = SceneEntityRemedy.For(entity.Kind) })
            .ToList();

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
        LogTerminal(operation, kind, state, detail, entities, notes, evidence);
        PublishTerminal(operation, progress, receipt);
    }

    /// <summary>
    /// The scene operation's own record, and the answer to issue #41's
    /// headline diagnostic defect: the log showed the SIDE EFFECTS of a load
    /// (a spawned clone, a built skeleton, a spawned light) and never the
    /// operation that caused them, so a partial restore could not be
    /// attributed to anything at all.
    ///
    /// <para>Every line is prefixed with the same correlated
    /// <c>Scene {kind} {operationId}</c>, so one grep gathers a whole
    /// operation out of an interleaved Dalamud log: one terminal line, then
    /// one line PER ENTITY carrying its kind, its stable scene name, its
    /// outcome, its refusal reason and its next step, then the notes and every
    /// recovery file left on disk.</para>
    /// </summary>
    private void LogTerminal(
        Operation operation,
        SceneOperationKind kind,
        OperationReceiptState state,
        string detail,
        IReadOnlyList<SceneEntityOutcome> entities,
        IReadOnlyList<string> notes,
        IReadOnlyList<string> evidence)
    {
        if (_log is null)
            return;

        string prefix = $"Scene {kind} {operation.OperationId:D}";
        string terminal =
            $"{prefix}: {state}: {operation.FileName}: {detail}";
        if (state == OperationReceiptState.Applied)
            _log.Information(terminal);
        else if (state is OperationReceiptState.Cancelled
                 or OperationReceiptState.RolledBack)
            _log.Warning(terminal);
        else
            _log.Error(terminal);

        foreach (var entity in entities)
        {
            string line = $"{prefix}: {entity.Kind} '{entity.Name}': " +
                (entity.Restored ? "restored" : "refused");
            if (!string.IsNullOrWhiteSpace(entity.Detail))
                line += $": {entity.Detail}";
            if (!entity.Restored && !string.IsNullOrWhiteSpace(entity.Remedy))
                line += $" Next: {entity.Remedy}";
            if (entity.Restored)
                _log.Debug(line);
            else
                _log.Warning(line);
        }

        foreach (var note in notes)
            _log.Information($"{prefix}: {note}");
        foreach (var path in evidence)
            _log.Warning($"{prefix}: recovery file: {path}");
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
