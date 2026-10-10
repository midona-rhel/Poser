using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Application.Scene;
using Poser.Scene;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Application.Scene;

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
/// mutation — a document-level failure refuses the file, an entity whose own
/// data is invalid is left out by name; entities spawn additively unless the load was asked to clear the
/// session first (<see cref="SceneLoadOptions.ClearExistingScene"/>, whose
/// sweep is deliberately outside the rollback ledger and says so in the
/// outcome, and which is preflighted so it never runs for a load that cannot
/// start).
///
/// <para>THE LOAD POLICY — the one place that decides what a failure costs.
/// REQUIRED steps roll back everything THIS operation created, in reverse
/// order: reading the document, admission and the session staying the same,
/// cancellation, and CREATING each actor (a scene with a hole where an actor
/// should be is not the scene). Everything else is OPTIONAL and becomes a
/// named refusal beside the restored entities — a Failed receipt that keeps
/// what did restore and is still one undoable step: an entity whose saved
/// data does not validate, an actor whose body does
/// not draw within the readiness bound (kept, not posed), appearance
/// (character file, collection), companions, names, animation stop, gaze,
/// pose and placement, props, overlays, map objects, cameras and their
/// targets, lights, environment and world toggles, FABRIK, sidebar groups and
/// order, and transform parent links. Never a silent detach, never a silent
/// skip, and never a whole-scene rollback for one of these.</para>
/// </summary>
public sealed partial class SceneWorkflow : IDisposable, ISceneWorkflow
{
    /// <summary>Bound for the spawned actors' skeleton readiness barrier —
    /// same bound the MCDF redraw barrier uses. Per actor: one that misses it
    /// is a named refusal, never the scene's rollback.</summary>
    private static readonly TimeSpan ActorReadyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Bound for the shared pose slot to come free, and then for one
    /// armed pose import to reach its terminal receipt.</summary>
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

    /// <summary>
    /// A character-file import's bound. Real packages run to hundreds of
    /// megabytes and the import decompresses, extracts, applies and waits for
    /// a redraw, so this is minutes rather than the one minute it used to be —
    /// a bound that expires mid-import turns a working restore into a named
    /// failure for no reason but impatience. Cancelling the load cuts it short.
    /// </summary>
    private static readonly TimeSpan McdfImportTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Building a package from live provider state, per actor. The
    /// exporter walks Penumbra's resource tree and writes the archive.
    /// </summary>
    private static readonly TimeSpan AppearanceSealTimeout =
        TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly ISceneRuntime _runtime;
    private readonly ISceneDocumentStore _documents;

    private readonly ISceneWorkflowObserver? _observer;

    private readonly object _publishGate = new();
    /// <summary>Cancelled at unload and deliberately never disposed: a task
    /// Dispose abandoned after its bounded join still reads its token, and a
    /// timer-less source holds nothing worth releasing.</summary>
    private readonly CancellationTokenSource _disposal = new();

    private SceneProgress? _progress;
    private OperationReceipt? _receipt;
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private Operation? _current;
    private OperationEpoch _epoch;
    private bool _disposed;

    private readonly TransformHistory? _history;
    private readonly Poser.Application.Scene.ISceneStructure? _structure;
    private readonly TransformParenting? _parenting;

    public SceneWorkflow(
        ISceneRuntime runtime,
        ISceneDocumentStore documents,
        ISceneWorkflowObserver? observer = null,
        TransformHistory? history = null,
        Poser.Application.Scene.ISceneStructure? structure = null,
        TransformParenting? parenting = null)
    {
        _runtime = runtime;
        _documents = documents;
        _observer = observer;
        _history = history;
        _structure = structure;
        _parenting = parenting;
    }


    /// <summary>What including modded appearance would add to a save right
    /// now, in bytes. Read every frame by the save surface, so it stays a
    /// cheap stat over the actors in the session and nothing more.</summary>
    public long EstimatedAppearanceBytes => _runtime.EstimateAppearanceBytes();

    /// <summary>The armed capture's bound. Only the contract tests set it —
    /// waiting the real bound out would make asserting the timeout a
    /// fifteen-second test.</summary>
    internal TimeSpan CaptureBound { get; init; } = SceneCaptureTimeout;

    /// <summary>The actor readiness bound, settable for the same reason.</summary>
    internal TimeSpan ActorReadyBound { get; init; } = ActorReadyTimeout;

    /// <summary>The pose slot and pose import bound, settable for the same
    /// reason.</summary>
    internal TimeSpan PoseImportBound { get; init; } = PoseImportTimeout;

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

    /// <summary>Cooperative cancellation of the running operation. A
    /// cancellable step reads "Cancelling" at once: the terminal Cancelled
    /// can trail it by a child's bounded drain.</summary>
    public void Cancel()
    {
        bool published = false;
        lock (_publishGate)
        {
            if (_current is { TerminalPublished: false, CancelRequested: false } operation
                && _progress is { Cancellable: true } progress)
            {
                operation.CancelRequested = true;
                _progress = progress with { Phase = ScenePhase.Cancelling, Cancellable = false };
                published = true;
            }
        }
        _cancellation?.Cancel();
        if (published)
            RaiseChanged();
    }

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
        /// <summary>The user asked to cancel; later cancellable steps keep
        /// reading "Cancelling" until the terminal state lands.</summary>
        public bool CancelRequested;
        /// <summary>Whether a landed load appends its step. A load the
        /// journal itself started as a redo does not: its step is the one
        /// being redone.</summary>
        public LoadHistory? Replay;
        /// <summary>The destroy-first clear ran: a rollback cannot give the
        /// session back what the clear took, and the outcome says so.</summary>
        public bool SessionCleared;

        public ActorId Target => new(SceneScopeId, 0);

        // What THIS operation created, in creation order; rollback walks
        // these in reverse. Receipts contain no native references.
        public readonly List<SceneEntityHandle> SpawnedActors = new();
        public readonly List<SceneEntityHandle> SpawnedProps = new();
        public readonly List<SceneEntityHandle> StagedOverlays = new();
        public readonly List<SceneEntityHandle> SpawnedLights = new();
        public readonly List<SceneEntityHandle> CreatedCameras = new();

        // Borrowed, not created — but rollback still has to undo the claim, and
        // releasing one is the exact inverse of taking it.
        public readonly List<SceneEntityHandle> BorrowedWorldObjects = new();
        public readonly List<Guid> ImportedGroups = new();
        public IReadOnlyDictionary<Guid, Guid> HistoryGroups = new Dictionary<Guid, Guid>();
        public IReadOnlyDictionary<(string Kind, Guid Key), SceneEntityHandle> HistoryEntities =
            new Dictionary<(string Kind, Guid Key), SceneEntityHandle>();
        public SceneCameraBaseline? DefaultCameraBaseline;
        /// <summary>Children whose parent link this load imported; rollback
        /// removes them before the entities go.</summary>
        public readonly List<SelectionId> ImportedLinks = new();
        /// <summary>The load reached its commit (Applied, or Failed with
        /// named refusals): it is in the session and is one history step.
        /// </summary>
        public bool Committed;
        public string? TerminalDetail;
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
            // Non-cancellable steps (rollback, commit) still show as
            // themselves; anything else is the cancel winding down.
            if (operation.CancelRequested && progress.Cancellable)
                progress = progress with { Phase = ScenePhase.Cancelling, Cancellable = false };
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

    private Outcome? AdmissionGate()
    {
        if (_disposed)
            return Outcome.Fail(
                "Poser is shutting down; no new scene operation can start.");
        if (Busy)
            return Outcome.Fail(
                "Another scene operation is already running.");
        return null;
    }

    /// <summary>Makes the operation current and publishes its first phase
    /// under the publication gate, like every later step.</summary>
    private Operation Admit(
        Guid sceneScopeId,
        string fileName,
        SceneOperationKind kind,
        SessionGeneration session,
        ScenePhase firstPhase)
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
            _progress = new SceneProgress(kind, fileName, firstPhase, 0, 0, true, null);
        }
        return operation;
    }

    /// <summary>Starts the whole-scene save: the bone-cache refresh is armed
    /// first, the framework-thread pointer-free capture runs once it lands,
    /// then off-thread validation and the atomic write.</summary>
    public Outcome BeginSave(
        string path,
        string? description = null,
        SceneSaveOptions? options = null)
    {
        if (AdmissionGate() is { } refused)
            return refused;
        if (_runtime.ActiveSession is not { } session)
            return Outcome.Fail(
                "No GPose session is active; a scene save needs the exact session identity.");

        var sceneId = Guid.NewGuid();
        var operation = Admit(
            sceneId, System.IO.Path.GetFileName(path), SceneOperationKind.Save, session,
            ScenePhase.RefreshingPoses);
        var cancellation = _cancellation!.Token;
        RaiseChanged();
        _task = Task.Run(
            () => RunSave(
                operation,
                path,
                description,
                options ?? SceneSaveOptions.Default,
                cancellation),
            CancellationToken.None);
        return Outcome.Ok();
    }

    /// <summary>Starts the whole-scene load transaction. Null options is the
    /// load as it has always been — see <see cref="SceneLoadOptions.Default"/>.
    /// </summary>
    public Outcome BeginLoad(
        string path, SceneLoadOptions? options = null) => BeginLoad(path, options, null);

    private Outcome BeginLoad(
        string path, SceneLoadOptions? options, LoadHistory? replay)
    {
        var chosen = options ?? SceneLoadOptions.Default;
        // An anchored placement and the origin rebase both move the content;
        // run together they land it twice. The placement the caller resolved
        // is the more specific answer, so it wins.
        if (chosen.Placement != Poser.Domain.Scene.ObjectPlacementMode.AsSaved)
            chosen = chosen with { PlaceRelativeToCurrentOrigin = false };
        if (AdmissionGate() is { } refused)
            return refused;
        if (_runtime.ActiveSession is not { } session)
            return Outcome.Fail(
                "No GPose session is active; a scene load needs the exact session identity.");
        // A load that includes no category would report success over a session
        // it never touched; refused at admission, where nothing has happened.
        if (!chosen.IncludesAnything)
            return Outcome.Fail(
                "The load has every category switched off, so there is nothing to restore.");

        var operation = Admit(
            Guid.NewGuid(), System.IO.Path.GetFileName(path),
            SceneOperationKind.Load, session, ScenePhase.Reading);
        operation.Replay = replay;
        if (replay is not null)
            replay.Current = operation;
        var cancellation = _cancellation!.Token;
        RaiseChanged();
        _task = Task.Run(
            () => RunLoad(operation, path, chosen, cancellation),
            CancellationToken.None);
        return Outcome.Ok();
    }

    private sealed class LoadHistory(Operation current)
    {
        public Operation Current = current;
        public IReadOnlyDictionary<(string Kind, Guid Key), SceneEntityHandle> Entities = current.HistoryEntities;
        public IReadOnlyDictionary<Guid, Guid> Groups = current.HistoryGroups;
    }

    /// <summary>
    /// A committed load — Applied, or Failed with named refusals — is one
    /// step. Its undo is the load's own rollback: every entity the load
    /// spawned goes, every link and group it imported goes, every baseline it
    /// overwrote comes back; what was there before the load is untouched. Its
    /// redo loads the file again ADDITIVELY — a redo never clears the session
    /// a second time — and completes on the replay's terminal: a replay that
    /// commits lands the step, one that rolls back leaves it to redo again.
    /// A load that cleared the scene first cannot bring the cleared entities
    /// back: the clear is not a step.
    /// </summary>
    private void AppendLoadStep(Operation operation, string path, SceneLoadOptions options)
    {
        if (operation.Replay is not null)
            return;
        // Redo creates new native entities. Keep the inverse attached to that
        // new operation rather than the first load's emptied rollback lists.
        var load = new LoadHistory(operation);
        var replay = options with { ClearExistingScene = false };
        _history?.Append(new JournalStep(
            $"Load {operation.FileName}",
            () => UndoLoad(load),
            () => BeginLoad(path, replay, load).Success)
        {
            RequiredAsset = path,
            // Busy is a scene operation still running, not a dead step:
            // pressing undo twice during a save must not discard the load.
            OnRefusal = () => Busy ? RefusalAction.Keep : RefusalAction.DropOnRepeat,
            CompleteReplay = (undo, _, _, completed) =>
            {
                if (undo)
                    completed(Poser.Domain.Transforms.GestureResult.Ok());
                else
                    _ = CompleteRedo(load.Current, Drain, completed);
            },
        });
    }

    /// <summary>Reports a redo's REAL outcome once the replayed load is
    /// terminal, on the framework thread history is confined to.</summary>
    private async Task CompleteRedo(
        Operation operation, Task running,
        Action<Poser.Domain.Transforms.GestureResult> completed)
    {
        // Never complete inside the redo call itself: history reads a
        // synchronous completion as the redo's own answer.
        await Task.Yield();
        try
        {
            await running;
        }
        catch (Exception)
        {
            // The terminal is published before the task ends either way.
        }
        var result = operation.Committed
            ? Poser.Domain.Transforms.GestureResult.Ok()
            : Poser.Domain.Transforms.GestureResult.Fail(
                operation.TerminalDetail ?? $"Loading {operation.FileName} again did not complete.");
        try
        {
            await _runtime.OnFramework(() =>
            {
                completed(result);
                return true;
            });
        }
        catch (Exception)
        {
            // The framework is gone (unload); history goes with it.
        }
    }

    private bool UndoLoad(LoadHistory load)
    {
        if (_disposed || Busy) return false;
        // Register before removal publishes missing bindings. Both transform
        // patches and group snapshots follow the same replacement on redo.
        foreach (var (key, token) in load.Entities)
            if (_runtime.ResolveHistoryEntity(token) is { } entity)
                _history?.RetainLifecycleEntity(entity, () =>
                    load.Entities.TryGetValue(key, out var current) ? _runtime.ResolveHistoryEntity(current) : null);
        return Rollback(load.Current) is null;
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
                            outcome =>
                            {
                                try
                                {
                                    if (outcome.Success && outcome.Scene is { } document
                                        && options.IncludeStructure && _structure != null)
                                        SceneStructureCodec.Write(document, _structure.Capture(), outcome.ActorIdentities);
                                    if (outcome.Success && outcome.Scene is { } parentDocument && _parenting != null)
                                        SceneParentingCodec.Write(parentDocument, _parenting.Capture(), outcome.ActorIdentities, _parenting.CompanionOwner);
                                }
                                catch (Exception exception)
                                {
                                    completion.TrySetException(exception);
                                    return;
                                }
                                completion.TrySetResult(outcome);
                            }));
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
                Task.Delay(CaptureBound, cancellation));
            if (settled != completion.Task)
            {
                Finish(false, cancellation.IsCancellationRequested
                    ? "The save was cancelled."
                    : "The scene capture did not finish within its bound.");
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

            // An entry save narrows to its keys BEFORE sealing: sealing reads
            // and packages appearance per actor, and an entry save must pay
            // for exactly the actors it keeps.
            var actorIdentities = captured.ActorIdentities;
            if (SceneSaveNarrowing.Narrow(scene, options.OnlyEntityKeys, ref actorIdentities)
                is { } narrowRefusal)
            {
                Finish(false, narrowRefusal);
                return;
            }

            // A document NEVER carries a borrow (ruled 2026-09-01): every
            // world object saves as a SPAWNABLE copy, so anything saved
            // loads in any map at any position. Borrowing stays a
            // live-session act only.
            if (scene.WorldObjects != null)
                foreach (var spawnable in scene.WorldObjects)
                    spawnable.Spawned = true;

            SceneSaveNarrowing.ApplyEntryName(scene, options.EntryName);

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
            SceneParenting.Prune(scene, notes);
            SceneSaveNarrowing.DetachDangling(scene, notes);

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

            // Format conversion losses belong to the operation's result.
            var stored = _documents.Write(scene, path);
            notes.AddRange(stored.Notes);
            var written = stored.Outcome;
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
                        new SceneEntityOutcome(SceneOutcomeKind.Actor, actor.Name, true));
                appearanceOutcomes.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.CharacterFile,
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
            _observer?.Saved();
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
            // Storage translates supported formats to the same scene document.
            var stored = _documents.Read(path);
            notes.AddRange(stored.Notes);
            var read = stored.Outcome;
            if (!read.Succeeded || read.Scene is not { } scene)
            {
                // Nothing native has run, so there is nothing to roll back:
                // a corrupt, oversized or future file is a plain Failed.
                Finish(OperationReceiptState.Failed, read.Failure!.Detail);
                return;
            }

            // Entities whose own data is invalid were left out of the
            // document by name; the rest of the scene loads beside them. A
            // refusal in a category this load leaves out is not this load's.
            foreach (var refusal in read.Refusals)
            {
                bool included = refusal.Kind switch
                {
                    SceneOutcomeKind.Object => options.IncludeProps,
                    SceneOutcomeKind.Overlay => options.IncludeOverlays,
                    SceneOutcomeKind.WorldObject => options.IncludeWorldObjects,
                    SceneOutcomeKind.Light => options.IncludeLights,
                    SceneOutcomeKind.Camera => options.IncludeCameras,
                    SceneOutcomeKind.Environment => options.IncludeEnvironment,
                    _ => options.IncludeActors,
                };
                if (included)
                    entities.Add(new SceneEntityOutcome(refusal.Kind, refusal.Name, false, refusal.Detail));
            }

            // BORROWING NEVER PERSISTS (ruled 2026-09-01): the borrow is a
            // live-session act — the footer's four marks — and a document
            // always carries spawnable copies, loadable in any map at any
            // position. Files saved before the rule carry borrowed
            // entries; every one loads as a spawn.
            if (scene.WorldObjects != null)
                foreach (var entry in scene.WorldObjects)
                    entry.Spawned = true;

            // The per-category views. An excluded category is an EMPTY view
            // rather than a flag consulted at each of its phases: every phase
            // then reads one list, and a category can never be half-skipped.
            // A list of its own: an actor whose body never draws leaves it
            // (kept in the session, named, and out of every later phase).
            var actors = options.IncludeActors
                ? scene.Actors.ToList()
                : new List<SceneActor>();
            var props = options.IncludeProps
                ? (IReadOnlyList<SceneProp>)scene.Props
                : Array.Empty<SceneProp>();
            var overlays = options.IncludeOverlays
                ? (IReadOnlyList<SceneOverlay>)(scene.Overlays ?? [])
                : Array.Empty<SceneOverlay>();
            var worldObjects = options.IncludeWorldObjects
                ? (IReadOnlyList<SceneWorldObject>)(scene.WorldObjects ?? [])
                : Array.Empty<SceneWorldObject>();
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
                notes, "world objects", options.IncludeWorldObjects,
                scene.WorldObjects?.Count ?? 0);
            AppendSkipNote(
                notes, "the environment", options.IncludeEnvironment,
                scene.Environment is null ? 0 : 1);
            foreach (var actor in actors)
                if (actor.AppearanceNotSaved)
                    notes.Add(SceneSavePolicy.AppearanceNotSavedNote(actor.Name));
            // Scenes saved before #229 carry FABRIK chains beside a pose that
            // already holds their baked result; restoring them would restart
            // a solver over it.
            if (options.IncludeActors && scene.Actors.Any(actor => actor.Fabrik is not null))
                notes.Add("Saved FABRIK chains from an older build were ignored; " +
                    "the baked pose loads as saved.");

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
            }

            // The object-entry placement: the caller resolved the CURRENT
            // anchor; the document carries the SAVED one. A mode whose saved
            // anchor the file does not record refuses before anything is
            // touched.
            if (options.Placement != Poser.Domain.Scene.ObjectPlacementMode.AsSaved)
            {
                Poser.Files.PlacementAnchorData? savedAnchor;
                if (options.Placement ==
                    Poser.Domain.Scene.ObjectPlacementMode.InFrontOfCamera)
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
                        Poser.Domain.Scene.ObjectPlacementMode.RelativeToCamera
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
                        Poser.Domain.Scene.ObjectPlacementMode.RelativeToCamera =>
                            "Placed relative to the camera.",
                        Poser.Domain.Scene.ObjectPlacementMode.InFrontOfCamera =>
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
            var actorTokens = new Dictionary<Guid, SceneEntityHandle>();
            // Per-kind key→token maps feed the structure restore: groups
            // and the root order reference entities by these keys.
            var propTokens = new Dictionary<Guid, SceneEntityHandle>();
            var overlayTokens = new Dictionary<Guid, SceneEntityHandle>();
            var worldObjectTokens = new Dictionary<Guid, SceneEntityHandle>();
            var lightTokens = new Dictionary<Guid, SceneEntityHandle>();
            var cameraTokens = new Dictionary<Guid, SceneEntityHandle>();
            // Named once their models have streamed, below.
            var spawnedWorldObjects = new List<(SceneEntityHandle Token, string Name)>();
            Step(ScenePhase.SpawningEntities);
            bool refusedBeforeClear = false;
            var spawnFailure = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;

                if (options.ClearExistingScene)
                {
                    // The clear cannot be undone, so a load that cannot even
                    // start its required steps refuses BEFORE it.
                    if (_runtime.LoadPreflight(actors.Count) is { } preflight)
                    {
                        refusedBeforeClear = true;
                        return $"{preflight} The session was not cleared.";
                    }
                    operation.SessionCleared = true;
                    if (_runtime.ClearScene().Summary() is { } cleared)
                        notes.Add(cleared);
                }

                // From the first native step to the terminal, the pose slot
                // is this load's: a library or inspector import refuses
                // instead of superseding the import an actor is waiting on.
                _runtime.HoldPoseImports(true);

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
                            SceneOutcomeKind.Object, prop.Name, false,
                            detail ?? "The object could not be spawned."));
                        continue;
                    }
                    operation.SpawnedProps.Add(token);
                    propTokens[prop.Key] = token;
                    entities.Add(new SceneEntityOutcome(SceneOutcomeKind.Object, prop.Name, true));
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
                            SceneOutcomeKind.Overlay, name, false,
                            detail ?? "The overlay could not be staged."));
                        continue;
                    }
                    operation.StagedOverlays.Add(token);
                    overlayTokens[overlay.Key] = token;
                    entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Overlay, name, true, detail));
                }

                // Borrowing back the map's own objects. A refusal here is
                // NAMED and never structural: the map may have been rebuilt,
                // the object may already be borrowed, or it may simply not be
                // standing where this scene recorded it — and a scene is still
                // a scene without it.
                foreach (var worldObject in worldObjects)
                {
                    string name = _runtime.WorldObjectName(worldObject.Path);
                    var token = _runtime.AdoptWorldObject(
                        worldObject, out var detail);
                    if (token is null)
                    {
                        entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.WorldObject, name, false,
                            detail ?? "The map object could not be borrowed."));
                        continue;
                    }
                    operation.BorrowedWorldObjects.Add(token);
                    worldObjectTokens[worldObject.Key] = token;
                    spawnedWorldObjects.Add((token, name));
                }
                return null;
            });
            if (refusedBeforeClear)
            {
                // Nothing native ran: a plain refusal, like an unreadable file.
                Finish(OperationReceiptState.Failed, spawnFailure!);
                return;
            }
            if (spawnFailure != null)
            {
                await Abort(spawnFailure);
                return;
            }
            done = props.Count + overlays.Count + worldObjects.Count;

            // Housing furniture streams its model in after the spawn. One
            // that has not loaded by the bound is KEPT and named — never
            // reported restored and then released behind the outcome's back.
            if (spawnedWorldObjects.Count > 0)
            {
                var unloaded = await _runtime.AwaitWorldObjectsLoaded(
                    spawnedWorldObjects.Select(entry => entry.Token).ToList(),
                    ActorReadyBound, cancellation);
                foreach (var (token, name) in spawnedWorldObjects)
                    entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.WorldObject, name, true,
                        unloaded.Contains(token)
                            ? $"Its model did not finish loading within {ActorReadyBound.TotalSeconds:0} " +
                              "seconds. It was kept and appears once the game streams it in."
                            : null));
            }

            // An actor whose body did not draw within the bound is OPTIONAL:
            // it stays in the session (the load's undo still removes it) and
            // leaves every later phase, so nothing tries to pose, target or
            // hang anything off a body that is not there.
            void DropUnready(IReadOnlyList<SceneEntityHandle> unready)
            {
                foreach (var token in unready)
                {
                    var actor = actors.First(entry => actorTokens[entry.Key] == token);
                    actors.Remove(actor);
                    actorTokens.Remove(actor.Key);
                    done++;
                    entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Actor, actor.Name, false,
                        $"The actor's body did not finish drawing within {ActorReadyBound.TotalSeconds:0} " +
                        "seconds, so it was kept but nothing more was restored onto it."));
                }
            }

            // Phase 3 — bounded readiness barrier: pose needs the spawned
            // actors' skeletons, which build with their draw objects.
            Step(ScenePhase.AwaitingActors);
            var ready = await WaitForActors(operation, actorTokens.Values, cancellation);
            if (ready.Stop != null)
            {
                await Abort(ready.Stop);
                return;
            }
            DropUnready(ready.Unready);

            // Phase 3b — character files, BEFORE anything that hangs off a
            // body. An MCDF import redraws the actor, which destroys its draw
            // object and every skeleton with it: a pose applied first would be
            // thrown away, and a companion attached first would go with the old
            // body. Each import runs through the ORDINARY MCDF transaction, so
            // the ownership it registers — and the by-name unlock-and-restore
            // teardown that ownership buys — is the same one a hand-driven
            // import leaves behind.
            if (actors.Any(entry => entry.Mcdf is not null || entry.PenumbraCollection is not null))
            {
                Step(ScenePhase.ApplyingAppearance);
                // Collections are per-actor redraws with no shared slot: all
                // of them run at once, so N actors cost one redraw's wait,
                // not N of them back to back.
                var collections = actors
                    .Where(actor => actor.Mcdf is null && actor.PenumbraCollection is not null)
                    .Select(actor => (actor.Name, Restore: _runtime.RestoreCollection(
                        actorTokens[actor.Key], actor, ActorReadyTimeout, cancellation)))
                    .ToList();
                await Task.WhenAll(collections.Select(entry => entry.Restore));
                foreach (var (name, restore) in collections)
                    if (restore.Result is { } collectionError)
                        entities.Add(new SceneEntityOutcome(SceneOutcomeKind.Collection, name, false, collectionError));

                foreach (var actor in actors)
                {
                    if (Guard(operation, cancellation) is { } stop)
                    {
                        await Abort(stop);
                        return;
                    }
                    if (actor.Mcdf is null)
                        continue;
                    var appearance = await _runtime.ImportMcdf(
                        path, actorTokens[actor.Key], actor,
                        McdfImportTimeout, cancellation);
                    // A missing package is a refusal by name; a package whose
                    // bytes moved on is restored WITH the divergence named.
                    // Neither is ever a silent skip.
                    if (appearance.Detail is { } detail)
                        entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.CharacterFile, actor.Name,
                            appearance.Restored, detail));
                }

                // The redraws rebuilt the skeletons every later phase reads.
                Step(ScenePhase.AwaitingActors);
                var rebuilt = await WaitForActors(operation, actorTokens.Values, cancellation);
                if (rebuilt.Stop != null)
                {
                    await Abort(rebuilt.Stop);
                    return;
                }
                DropUnready(rebuilt.Unready);
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
                            SceneOutcomeKind.Companion, actor.Name, false, detail));
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
                    var nameDetail = _runtime.RestoreActorName(actorTokens[actor.Key], actor);
                    if (nameDetail != null)
                        entities.Add(new SceneEntityOutcome(SceneOutcomeKind.ActorName, actor.Name, false, nameDetail));
                    var detail = _runtime.FreezeActor(actorTokens[actor.Key]);
                    if (detail != null)
                        entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.Animation, actor.Name, false, detail));
                    if (actor.Gaze?.Mode == GazeTargetMode.Detached)
                    {
                        // Import deltas use the live animated basis. Detaching
                        // only afterward removes the native chest/neck aim
                        // underneath those deltas, drifting every saved pose.
                        var gazeDetail = _runtime.ApplyActorGaze(
                            actorTokens[actor.Key], actor, null);
                        if (gazeDetail != null)
                            entities.Add(new SceneEntityOutcome(
                                SceneOutcomeKind.Gaze, actor.Name, false, gazeDetail));
                    }
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
                    ? new SceneEntityOutcome(SceneOutcomeKind.Actor, actor.Name, true)
                    : new SceneEntityOutcome(SceneOutcomeKind.Actor, actor.Name, false, placement));

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
                    if (companion == null)
                        companion = await _runtime.OnFramework(() =>
                            Guard(operation, cancellation)
                                ?? _runtime.PlaceCompanion(token, actor));
                    if (companion != null)
                        entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.Companion, actor.Name, false, companion));
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
                    if (actor.Gaze?.Mode == GazeTargetMode.Detached)
                        continue; // Already established before the pose's basis was sampled.
                    // Active gaze comes AFTER the pose: the look-at re-drives its
                    // channels every frame, and its Entity target is another
                    // RESTORED actor, so it needs every token to exist. The
                    // document validated the reference; it misses only when
                    // the target was left out or never drew.
                    SceneEntityHandle? target = null;
                    if (actor.Gaze?.TargetActorKey is { } gazeTarget
                        && !actorTokens.TryGetValue(gazeTarget, out target))
                    {
                        entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.Gaze, actor.Name, false,
                            "The actor looks at an actor this load did not restore."));
                        continue;
                    }
                    var detail = _runtime.ApplyActorGaze(
                        actorTokens[actor.Key], actor, target);
                    if (detail != null)
                        entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.Gaze, actor.Name, false, detail));
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

                SceneEntityHandle? liveCamera = null;
                bool liveIsDefault = false;
                foreach (var camera in cameras)
                {
                    SceneEntityHandle? token = null;
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
                            SceneOutcomeKind.Camera, camera.Camera!.Name, false, detail));
                        done++;
                        continue;
                    }

                    if (camera.TargetActorKey is { } targetKey)
                    {
                        // The document validated this reference; it can only
                        // miss here if the target actor never drew or this
                        // load was told to leave the actors out — then the
                        // camera is restored and its target refused BY NAME.
                        if (!actorTokens.ContainsKey(targetKey))
                        {
                            entities.Add(new SceneEntityOutcome(
                                SceneOutcomeKind.Camera, camera.Camera!.Name, false,
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
                                SceneOutcomeKind.Camera, camera.Camera!.Name, false,
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
                        SceneOutcomeKind.Camera, camera.Camera!.Name, true));
                    done++;
                }

                if (cameras.Count > 0)
                {
                    var liveDetail = _runtime.SetLiveCamera(
                        liveIsDefault ? null : liveCamera);
                    if (liveDetail != null)
                        entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.LiveCamera, "Live camera", false, liveDetail));
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
                            SceneOutcomeKind.Light, light.Light!.Name, false,
                            "The light is attached to an actor this load did " +
                            "not restore, so it was not spawned."));
                        done++;
                        continue;
                    }
                    SceneEntityHandle? owner = light.Attachment is { } attachment
                        ? actorTokens[attachment.ActorKey]
                        : null;
                    var token = _runtime.SpawnLight(light, owner, out var detail);
                    if (token is null)
                    {
                        entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.Light, light.Light!.Name, false,
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
                            SceneOutcomeKind.Light, light.Light!.Name, true, detail));
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
                            SceneOutcomeKind.Environment, "Environment", true));
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
                            SceneOutcomeKind.World, "World", false, detail));
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
            var structureTokens = StructureTokens(("actor", actorTokens), ("prop", propTokens),
                ("overlay", overlayTokens), ("worldObject", worldObjectTokens),
                ("light", lightTokens), ("camera", cameraTokens));
            // Only a stop (cancel, session replaced) aborts here: members that
            // never bound are the structure restore's named refusals.
            if (await WaitForStructure(operation, scene, structureTokens, cancellation) is { } structureStop)
            {
                await Abort(structureStop);
                return;
            }
            Step(ScenePhase.Committing, cancellable: false);
            var committed = await _runtime.OnFramework(() =>
            {
                if (Guard(operation, cancellation) is { } stop)
                    return stop;
                RestoreStructure(operation, scene, structureTokens, entities);
                var failures = entities.Where(entity => !entity.Restored).ToList();
                operation.HistoryEntities = structureTokens;
                operation.Committed = true;
                if (operation.Replay is { } replay)
                {
                    foreach (var (key, previous) in replay.Entities)
                        if (structureTokens.TryGetValue(key, out var replacement))
                            _runtime.BindHistoryReplacement(previous, replacement);
                    replay.Entities = structureTokens;
                    replay.Groups = operation.HistoryGroups;
                }
                string detail = failures.Count == 0
                    ? $"Loaded {operation.FileName}: " +
                      $"{Count(actors.Count, "actor")}, " +
                      $"{Count(props.Count, "object")}, " +
                      $"{Count(lights.Count, "light")}, " +
                      $"{Count(cameras.Count, "camera")}."
                    : $"Loaded {operation.FileName} partially: " +
                      $"{Count(failures.Count, "part")} could not be " +
                      "restored (everything that did restore was kept): " +
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
                    notes, Array.Empty<string>());
                // Partial or not, what committed is in the session, so it is
                // ONE undoable step: undo removes exactly what this load made.
                AppendLoadStep(operation, path, options);
                return null;
            });
            if (committed != null)
            {
                await Abort(committed);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A wait that honours the token throws; the cancel is still the
            // user's, so it is reported as one rather than as a failure.
            await Abort("The load was cancelled.");
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
        finally
        {
            // Not during unload: Dispose blocks the framework thread this hop
            // needs, and the slot goes with the plugin.
            if (!_disposed)
            {
                try
                {
                    await _runtime.OnFramework(() =>
                    {
                        _runtime.HoldPoseImports(false);
                        return true;
                    });
                }
                catch (Exception)
                {
                    // The framework thread is gone; so is the slot.
                }
            }
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
        foreach (var overlay in scene.Overlays ?? [])
            if (overlay.Node?.Collider is { } collider)
            {
                sum += collider.Transform.Position;
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
    /// <para>Pending receipts never complete the wait; they only name the
    /// operation to cancel if the wait ends first. The import
    /// engine acknowledges an admitted import by publishing a Pending receipt
    /// synchronously from inside <paramref name="arm"/>
    /// through the shared import coordinator,
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
        Guid? admitted = null;
        void OnReceipt(OperationReceipt receipt)
        {
            if (receipt.State == OperationReceiptState.Pending)
                admitted = receipt.OperationId;
            else
                completion.TrySetResult(receipt);
        }

        // The slot is shared with every other pose feature, and an IK bake or
        // an open gesture holds it for a moment: WAIT for it, within the
        // bound, rather than spend this actor's one attempt on a busy answer.
        (string? Refusal, bool Busy) TryArm()
        {
            if (Guard(operation, cancellation) is { } stop)
                return (stop, false);
            if (_runtime.PoseImportBusy)
                return (null, true);
            return (arm(OnReceipt), false);
        }
        var slotDeadline = DateTime.UtcNow + PoseImportBound;
        while (true)
        {
            (string? Refusal, bool Busy) armed;
            try
            {
                armed = await _runtime.OnFramework(TryArm);
            }
            catch (Exception ex)
            {
                return $"The pose import dispatch failed: {ex.Message}";
            }
            if (!armed.Busy)
            {
                if (armed.Refusal != null)
                    return armed.Refusal;
                break;
            }
            if (DateTime.UtcNow >= slotDeadline)
                return "Another pose edit held the pose import for the whole bound, so the pose was not restored.";
            await Task.Delay(50, cancellation);
        }

        var finished = await Task.WhenAny(
            completion.Task, Task.Delay(PoseImportBound, cancellation));
        if (finished != completion.Task)
        {
            // Never leave the child armed: landing after this answer would
            // pose an actor behind the terminal receipt, or after rollback.
            if (admitted is { } id)
            {
                try
                {
                    await _runtime.OnFramework(() =>
                    {
                        _runtime.CancelPoseImport(id);
                        return true;
                    });
                }
                catch (Exception)
                {
                    // The framework thread is gone, and the import with it.
                }
            }
            // The cancel publishes the import's own Cancelled terminal; only
            // one that had already applied stands.
            if (completion.Task is not { IsCompleted: true, Result.State: OperationReceiptState.Applied })
                return cancellation.IsCancellationRequested
                    ? "The load was cancelled."
                    : "The pose import did not finish within its bound, so it was cancelled.";
        }

        var receipt = completion.Task.Result;
        return receipt.State == OperationReceiptState.Applied
            ? null
            : receipt.Detail ?? $"The pose import ended {receipt.State}.";
    }

    /// <summary>Bounded readiness barrier over the given actors. Stop is a
    /// structural refusal (cancel, shutdown, framework gone); Unready names
    /// the actors still not posable when the bound ran out — each of them
    /// one named refusal, never a reason to roll the others back.</summary>
    private async Task<(string? Stop, IReadOnlyList<SceneEntityHandle> Unready)> WaitForActors(
        Operation operation, IEnumerable<SceneEntityHandle> actors, CancellationToken cancellation)
    {
        var pending = actors.ToList();
        var deadline = DateTime.UtcNow + ActorReadyBound;
        while (true)
        {
            try
            {
                await _runtime.OnFramework(() =>
                {
                    if (!operation.Invalidated) // The guard below reports the refusal.
                        pending.RemoveAll(_runtime.ActorReady);
                    return true;
                });
            }
            catch (Exception ex)
            {
                return ($"The readiness barrier failed: {ex.Message}", pending);
            }

            if (operation.Invalidated || cancellation.IsCancellationRequested)
                return ("The load was cancelled.", pending);
            if (pending.Count == 0 || DateTime.UtcNow >= deadline)
                return (null, pending);
            try
            {
                await Task.Delay(50, _disposal.Token);
            }
            catch (OperationCanceledException)
            {
                return ("Poser is shutting down.", pending);
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
        Dictionary<Guid, SceneEntityHandle> actorTokens,
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

        try
        {
            _structure?.Remove(operation.ImportedGroups);
            operation.ImportedGroups.Clear();
        }
        catch (Exception ex)
        {
            failures.Add($"group removal: {ex.Message}");
        }

        foreach (var child in operation.ImportedLinks)
            _parenting?.Remove(child);
        operation.ImportedLinks.Clear();

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
        List<SceneEntityHandle> tokens,
        Action<SceneEntityHandle> destroy,
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
                : entity with { Remedy = entity.Kind.Remedy() })
            .ToList();

        var progress = new SceneProgress(
            kind, operation.FileName, phase, 0, 0, false,
            new SceneOutcome(state, detail, entities, notes, evidence)
            {
                SessionCleared = operation.SessionCleared,
            });
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
        operation.TerminalDetail = detail;
        _observer?.Completed(operation.OperationId, progress);
        PublishTerminal(operation, progress, receipt);
    }


    /// <summary>Bounded cancel/drain before disposal: admission closes
    /// permanently, tokens cancel, and the active task is joined inside the
    /// bound. An abandoned task cannot mutate anything — every phase
    /// re-guards on the cancelled token. Idempotent: the container disposes a
    /// singleton once per registration, and the workflow is registered as
    /// itself and as its port. The second call must not cancel a disposed
    /// source.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Children first: a cancelled parent must not start a drain that
        // needs the framework thread this Dispose is blocking.
        try
        {
            _runtime.AbandonChildWaits();
        }
        catch (Exception)
        {
            // Disposal must not throw; the join below is still bounded.
        }
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
    }
}
