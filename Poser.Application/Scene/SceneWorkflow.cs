using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Application.Scene;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Application.Lifecycle;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Documents.Files;
using Poser.Documents.Scene;

namespace Poser.Application.Scene;

/// <summary>
/// The single-flight owner of the whole-scene workflow: admission
/// (exact session generation, owner-local operation epoch, operation id),
/// progress and receipt publication, the save capture/write pipeline, and
/// the bounded cancel/drain that runs before disposal. A load runs as a
/// <see cref="SceneLoadTransaction"/>, whose class documentation is THE LOAD
/// POLICY. It reuses <see cref="OperationReceipt"/>,
/// <see cref="OperationEpoch"/> and <see cref="SessionGeneration"/> wholesale —
/// there is no scene-specific receipt or epoch type.
///
/// A whole-scene operation has no single target actor, so its receipts target
/// the scene's own logical identity: <c>new ActorId(SceneScopeId, 0)</c>,
/// where the scope id is the document's SceneId for a save and a minted
/// load-scope identity for a load (the file's id is unknown at admission and
/// receipt identity must be stable from Pending to terminal).
/// </summary>
public sealed class SceneWorkflow : IDisposable, ISceneWorkflow
{
    /// <summary>Bound for the spawned actors' skeleton readiness barrier —
    /// same bound the MCDF redraw barrier uses. Per actor: one that misses it
    /// is a named refusal, never the scene's rollback.</summary>
    internal static readonly TimeSpan ActorReadyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Bound for the shared pose slot to come free, and then for one
    /// armed pose import to reach its terminal receipt.</summary>
    private static readonly TimeSpan PoseImportTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Bound for the armed whole-scene capture to answer. The refresh
    /// it waits on has its own tick bound and answers either way, so this only
    /// catches a framework thread that stopped ticking entirely.</summary>
    private static readonly TimeSpan SceneCaptureTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Building a package from live provider state, per actor. The
    /// exporter walks Penumbra's resource tree and writes the archive.
    /// </summary>
    private static readonly TimeSpan AppearanceSealTimeout =
        TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly ISceneStatePort _sceneState;
    private readonly ISceneCapturePort _capture;
    private readonly ISceneMaterializer _materializer;
    private readonly IActorRestorePort _actorRestore;
    private readonly ISceneHistoryPort _historyPort;
    private readonly ISceneDocumentStore _documents;
    private readonly ISceneWorkflowObserver _observer;
    private readonly ISceneStructure _structure;
    private readonly TransformParenting _parenting;
    private readonly SceneLoadStructure _loadStructure;
    private readonly SceneLoadRollback _rollback;
    private readonly SceneLoadHistory _loadHistory;

    // Non-cancellable steps (rollback, commit) still show as themselves;
    // anything else is the cancel winding down.
    private readonly SingleFlightOwner<SceneOperation, SceneProgress> _flight = new(
        (operation, progress) => operation.CancelRequested && progress.Cancellable
            ? progress with { Phase = ScenePhase.Cancelling, Cancellable = false }
            : progress);

    public SceneWorkflow(
        ISceneStatePort sceneState,
        ISceneCapturePort capture,
        ISceneMaterializer materializer,
        IActorRestorePort actorRestore,
        ISceneHistoryPort historyPort,
        ISceneDocumentStore documents,
        ISceneWorkflowObserver observer,
        TransformHistory history,
        ISceneStructure structure,
        TransformParenting parenting)
    {
        _sceneState = sceneState;
        _capture = capture;
        _materializer = materializer;
        _actorRestore = actorRestore;
        _historyPort = historyPort;
        _documents = documents;
        _observer = observer;
        _structure = structure;
        _parenting = parenting;
        _loadStructure = new SceneLoadStructure(sceneState, structure, parenting);
        _rollback = new SceneLoadRollback(sceneState, materializer, structure, parenting);
        _loadHistory = new SceneLoadHistory(history, sceneState, historyPort, _rollback, this);
    }


    /// <summary>What including modded appearance would add to a save right
    /// now, in bytes. Read every frame by the save surface, so it stays a
    /// cheap stat over the actors in the session and nothing more.</summary>
    public long EstimatedAppearanceBytes => _capture.EstimateAppearanceBytes();

    /// <summary>The armed capture's bound. Only the contract tests set it —
    /// waiting the real bound out would make asserting the timeout a
    /// fifteen-second test.</summary>
    internal TimeSpan CaptureBound { get; init; } = SceneCaptureTimeout;

    /// <summary>The actor readiness bound, settable for the same reason.</summary>
    internal TimeSpan ActorReadyBound { get; init; } = ActorReadyTimeout;

    /// <summary>The pose slot and pose import bound, settable for the same
    /// reason.</summary>
    internal TimeSpan PoseImportBound { get; init; } = PoseImportTimeout;

    /// <summary>How long a load waits for the members its structure names to
    /// bind, settable for the same reason.</summary>
    internal TimeSpan StructureBindingBound { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Raised after any progress/receipt publication; UI reads the
    /// immutable snapshots, never workflow internals.</summary>
    public event Action? Changed
    {
        add => _flight.Changed += value;
        remove => _flight.Changed -= value;
    }

    public SceneProgress? Progress => _flight.Progress;

    public OperationReceipt? Receipt => _flight.Receipt;

    /// <summary>Only one scene save/load runs at a time.</summary>
    public bool Busy => _flight.Busy;

    /// <summary>The running operation's join handle. The terminal receipt is
    /// always published before it completes, so awaiting it is the exact
    /// "the operation is finished" barrier.</summary>
    internal Task Drain => _flight.Completion;

    /// <summary>Admission is closed for good: the workflow is draining or
    /// gone.</summary>
    internal bool Disposed => _flight.Closed;

    /// <summary>Cancelled at unload; child waits that must outlive a user
    /// cancel still end here.</summary>
    internal CancellationToken DisposalToken => _flight.Disposal;

    /// <summary>Cooperative cancellation of the running operation. A
    /// cancellable step reads "Cancelling" at once: the terminal Cancelled
    /// can trail it by a child's bounded drain.</summary>
    public void Cancel()
    {
        bool published = _flight.TryReplaceProgress((operation, progress) =>
        {
            if (operation.CancelRequested || progress is not { Cancellable: true } cancellable)
                return null;
            operation.CancelRequested = true;
            return cancellable with { Phase = ScenePhase.Cancelling, Cancellable = false };
        });
        _flight.Cancel();
        if (published)
            _flight.RaiseChanged();
    }

    internal void PublishStep(SceneOperation operation, SceneProgress progress) =>
        _flight.PublishStep(operation, progress);

    // ── Admission ────────────────────────────────────────────────────────

    private Outcome? AdmissionGate()
    {
        if (_flight.Closed)
            return Outcome.Fail(
                "Poser is shutting down; no new scene operation can start.");
        if (Busy)
            return Outcome.Fail(
                "Another scene operation is already running.");
        return null;
    }

    /// <summary>Makes the operation current and publishes its first phase
    /// under the publication gate, like every later step.</summary>
    private SceneOperation Admit(
        Guid sceneScopeId,
        string fileName,
        SceneOperationKind kind,
        SessionGeneration session,
        ScenePhase firstPhase,
        out CancellationToken cancellation) =>
        _flight.Admit(
            epoch => new SceneOperation
            {
                SceneScopeId = sceneScopeId,
                FileName = fileName,
                OperationId = Guid.NewGuid(),
                Epoch = epoch,
                Session = session,
                Target = new ActorId(sceneScopeId, 0),
                Kind = kind,
            },
            new SceneProgress(kind, fileName, firstPhase, 0, 0, true, null),
            out cancellation);

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
        if (_sceneState.ActiveSession is not { } session)
            return Outcome.Fail(
                "No GPose session is active; a scene save needs the exact session identity.");

        var sceneId = Guid.NewGuid();
        var operation = Admit(
            sceneId, System.IO.Path.GetFileName(path), SceneOperationKind.Save, session,
            ScenePhase.RefreshingPoses, out var cancellation);
        _flight.RaiseChanged();
        _flight.Run(
            () => RunSave(
                operation,
                path,
                description,
                options ?? SceneSaveOptions.Default,
                cancellation));
        return Outcome.Ok();
    }

    /// <summary>Starts the whole-scene load transaction. Null options is the
    /// load as it has always been — see <see cref="SceneLoadOptions.Default"/>.
    /// </summary>
    public Outcome BeginLoad(
        string path, SceneLoadOptions? options = null) => BeginLoad(path, options, null);

    /// <summary>A history redo: the load again, attached to the step it
    /// replays.</summary>
    internal Outcome BeginReplay(string path, SceneLoadOptions options, SceneLoadReplay replay) =>
        BeginLoad(path, options, replay);

    private Outcome BeginLoad(
        string path, SceneLoadOptions? options, SceneLoadReplay? replay)
    {
        var chosen = options ?? SceneLoadOptions.Default;
        // An anchored placement and the origin rebase both move the content;
        // run together they land it twice. The placement the caller resolved
        // is the more specific answer, so it wins.
        if (chosen.Placement != Poser.Domain.Scene.ObjectPlacementMode.AsSaved)
            chosen = chosen with { PlaceRelativeToCurrentOrigin = false };
        if (AdmissionGate() is { } refused)
            return refused;
        if (_sceneState.ActiveSession is not { } session)
            return Outcome.Fail(
                "No GPose session is active; a scene load needs the exact session identity.");
        // A load that includes no category would report success over a session
        // it never touched; refused at admission, where nothing has happened.
        if (!chosen.IncludesAnything)
            return Outcome.Fail(
                "The load has every category switched off, so there is nothing to restore.");

        var operation = Admit(
            Guid.NewGuid(), System.IO.Path.GetFileName(path),
            SceneOperationKind.Load, session, ScenePhase.Reading, out var cancellation);
        operation.Replay = replay;
        if (replay is not null)
            replay.Current = operation;
        var load = new SceneLoadTransaction(
            this, _sceneState, _materializer, _actorRestore, _historyPort, _documents,
            _loadStructure, _rollback, _loadHistory,
            operation, path, chosen, cancellation);
        _flight.RaiseChanged();
        _flight.Run(() => load.Run());
        return Outcome.Ok();
    }

    // ── Save ─────────────────────────────────────────────────────────────

    private async Task RunSave(
        SceneOperation operation,
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
                armRefusal = await _sceneState.OnFramework(() =>
                    operation.Guard(_sceneState, cancellation)
                        ?? _capture.ArmSceneCapture(
                            operation.SceneScopeId, description,
                            outcome =>
                            {
                                try
                                {
                                    if (outcome.Success && outcome.Scene is { } document
                                        && options.IncludeStructure)
                                        SceneStructureCodec.Write(document, _structure.Capture(), outcome.ActorIdentities);
                                    if (outcome.Success && outcome.Scene is { } parentDocument)
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
                var sealed_ = await _capture.SealAppearance(
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
            if (_capture.StampMcdfHashes(scene) is { Count: > 0 } stamped)
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
                _capture.DeleteTemporary(temporary);
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
            _observer.Saved();
        }
        catch (Exception ex)
        {
            Finish(false, $"The save failed unexpectedly: {ex.Message}");
        }
    }


    /// <summary>The ONE terminal publication: the receipt state, the progress
    /// phase and the outcome state are derived from a single decision so a UI
    /// can never read a phase that disagrees with its receipt.</summary>
    internal void FinishTerminal(
        SceneOperation operation,
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
        _observer.Completed(operation.OperationId, progress);
        _flight.PublishTerminal(operation, progress, receipt);
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
        if (!_flight.Close())
            return;
        // Children first: a cancelled parent must not start a drain that
        // needs the framework thread this Dispose is blocking.
        try
        {
            _sceneState.AbandonChildWaits();
        }
        catch (Exception)
        {
            // Disposal must not throw; the join below is still bounded.
        }
        _flight.Drain(DisposeDrainTimeout);
    }
}
