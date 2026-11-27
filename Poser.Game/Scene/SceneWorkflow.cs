using Poser.Domain;
using Poser.Domain.Scene;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Poser.Domain.Operations;
using Poser.Application.Lifecycle;
using Poser.Application.Transforms;
using Poser.Game.Transforms;
using Poser.Domain.Identity;
using Poser.Documents.Files;
using Poser.Documents.Scene;
using Poser.Application.Scene;

namespace Poser.Game.Scene;

/// <summary>
/// Runs one scene save or load at a time. It checks that GPose is still the
/// same session, reports progress and a final result, and on shutdown waits
/// for running work to stop. <see cref="SceneLoadTransaction"/> does the steps
/// of a load in order, and undoes them if the load fails.
///
/// A save or load is about the whole scene, not one actor, so its result is
/// filed under an id for the scene: <c>new ActorId(SceneScopeId, 0)</c>. For a
/// save that is the scene's own id. For a load it is a new id, because the file
/// has not been read when the load starts, and the result must keep one id from
/// start to finish.
/// </summary>
public sealed class SceneWorkflow : IDisposable, ISceneWorkflow
{
    /// <summary>How long to wait for a spawned actor's skeleton to be ready, the
    /// same time an MCDF import waits for its redraw. An actor that is not ready
    /// in time is reported by name, and the rest of the scene is kept.</summary>
    internal static readonly TimeSpan ActorReadyTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for the pose import queue to be free, and then
    /// for one queued pose import to finish.</summary>
    internal static readonly TimeSpan PoseImportTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long to wait for every loaded actor, object, light and
    /// camera to show up in Poser's scene list, so the scene's groups can be
    /// rebuilt.</summary>
    internal static readonly TimeSpan StructureBindingTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for the scene capture to answer. The pose
    /// refresh it waits for has its own time limit and always answers, so this
    /// only catches a game main thread that has stopped running.</summary>
    private static readonly TimeSpan SceneCaptureTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long building one actor's appearance package may take. It
    /// goes through every file Penumbra loaded for the actor and writes them into
    /// one archive.
    /// </summary>
    private static readonly TimeSpan AppearanceSealTimeout =
        TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly GPoseSceneState _sceneState;
    private readonly SceneAppearance _appearance;
    private readonly SceneEntityLoader _sceneEntities;
    private readonly SceneActorRestore _actorRestore;
    private readonly SceneHistoryBinder _historyBinder;
    private readonly ISceneDocumentStore _documents;
    private readonly SceneWorkflowObserver _observer;
    private readonly ISceneStructure _structure;
    // This Poser install's id, which decides whether a loaded scene is your own.
    private readonly Func<Guid> _thisInstall;
    private readonly TransformParenting _parenting;
    private readonly SceneLoadStructure _loadStructure;
    private readonly SceneLoadRollback _rollback;
    private readonly SceneLoadHistory _loadHistory;

    // Once Cancel is pressed, every step shows Cancelling, except the steps
    // that cannot be cancelled (undoing a failed load, the final step).
    private readonly SingleFlightOwner<SceneOperation, SceneProgress> _flight = new(
        (operation, progress) => operation.CancelRequested && progress.Cancellable
            ? progress with { Phase = ScenePhase.Cancelling, Cancellable = false }
            : progress);

    internal SceneWorkflow(
        GPoseSceneState sceneState,
        SceneAppearance appearance,
        SceneEntityLoader sceneEntities,
        SceneActorRestore actorRestore,
        SceneHistoryBinder historyBinder,
        ISceneDocumentStore documents,
        SceneWorkflowObserver observer,
        EditHistory history,
        ISceneStructure structure,
        TransformParenting parenting,
        Func<Guid> thisInstall)
    {
        _thisInstall = thisInstall;
        _sceneState = sceneState;
        _appearance = appearance;
        _sceneEntities = sceneEntities;
        _actorRestore = actorRestore;
        _historyBinder = historyBinder;
        _documents = documents;
        _observer = observer;
        _structure = structure;
        _parenting = parenting;
        _loadStructure = new SceneLoadStructure(sceneState, structure, parenting);
        _rollback = new SceneLoadRollback(sceneState, sceneEntities, structure, parenting);
        _loadHistory = new SceneLoadHistory(history, sceneState, historyBinder, _rollback, this);
    }

    /// <summary>The interface reads this every frame, so it is only a quick sum
    /// over the actors in the scene.</summary>
    public long EstimatedAppearanceBytes => _appearance.EstimateAppearanceBytes();

    /// <summary>Raised whenever the progress or the result changes. The
    /// interface reads only <see cref="Progress"/> and <see cref="Receipt"/>,
    /// which are copies that never change.</summary>
    public event Action? Changed
    {
        add => _flight.Changed += value;
        remove => _flight.Changed -= value;
    }

    public SceneProgress? Progress => _flight.Progress;

    public OperationReceipt? Receipt => _flight.Receipt;

    public bool Busy => _flight.Busy;

    /// <summary>Finishes when the running save or load has finished. The final
    /// result is always published first, so once this finishes the operation is
    /// fully done.</summary>
    internal Task Drain => _flight.Completion;

    /// <summary>True once Poser is shutting down. No new save or load can
    /// start.</summary>
    internal bool Disposed => _flight.Closed;

    /// <summary>Cancelled when Poser shuts down. Waits that must keep going
    /// after Cancel is pressed still stop here.</summary>
    internal CancellationToken DisposalToken => _flight.Disposal;

    /// <summary>Asks the running save or load to stop. A step that can be
    /// cancelled shows Cancelling at once. The final Cancelled result can come a
    /// little later, while the work in progress winds down.</summary>
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

    // ── Starting ─────────────────────────────────────────────────────────

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

    /// <summary>Makes this the running operation and publishes its first step,
    /// the same safe way every later step is published.</summary>
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

    /// <summary>Starts saving the whole scene. Poser first asks for every bone
    /// position to be read fresh. Then the scene is copied on the game's main
    /// thread, keeping no links into game memory. The copy is checked and written
    /// on another thread, in one go, so a failed write never leaves half a file.</summary>
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

    /// <summary>Starts loading a whole scene. With no options, every category is
    /// loaded where it was saved; see <see cref="SceneLoadOptions.Default"/>.
    /// </summary>
    public Outcome BeginLoad(
        string path, SceneLoadOptions? options = null) => BeginLoad(path, options, null);

    /// <summary>Redo of a scene load: loads the scene again, linked to the undo
    /// step it redoes.</summary>
    internal Outcome BeginReplay(string path, SceneLoadOptions options, SceneLoadReplay replay) =>
        BeginLoad(path, options, replay);

    private Outcome BeginLoad(
        string path, SceneLoadOptions? options, SceneLoadReplay? replay)
    {
        var chosen = options ?? SceneLoadOptions.Default;
        // Placing the scene at a chosen spot and placing it relative to the
        // current origin both move everything, so doing both would move it
        // twice. The chosen spot is more specific, so it wins.
        if (chosen.Placement != Poser.Domain.Scene.ObjectPlacementMode.AsSaved)
            chosen = chosen with { PlaceRelativeToCurrentOrigin = false };
        if (AdmissionGate() is { } refused)
            return refused;
        if (_sceneState.ActiveSession is not { } session)
            return Outcome.Fail(
                "No GPose session is active; a scene load needs the exact session identity.");
        // A load with every category switched off would report success without
        // changing anything, so it is turned down before it starts.
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
            this, _sceneState, _sceneEntities, _actorRestore, _appearance, _historyBinder, _documents,
            _loadStructure, _rollback, _loadHistory,
            operation, path, chosen, _thisInstall(), cancellation);
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
        // Every temporary appearance package this save makes. They are all
        // deleted when the save ends, however it ends.
        var temporaries = new List<string>();
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
            // Ask for the capture, so bone positions are read fresh before the
            // scene is copied. The answer comes on a later frame, so the wait
            // has a time limit.
            var completion = new TaskCompletionSource<SceneCaptureOutcome>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            string? armRefusal;
            try
            {
                armRefusal = await _sceneState.OnFramework(() =>
                    operation.Guard(_sceneState, cancellation)
                        ?? _appearance.ArmSceneCapture(
                            operation.SceneScopeId, description,
                            includeAnimation: options.IncludeAnimation,
                            onCaptured: outcome =>
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
                Task.Delay(SceneCaptureTimeout, cancellation));
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

            // Package appearance only for the actors included in this save.
            var actorIdentities = captured.ActorIdentities;
            if (SceneSaveNarrowing.Narrow(scene, options.OnlyEntityKeys, ref actorIdentities)
                is { } narrowRefusal)
            {
                Finish(false, narrowRefusal);
                return;
            }

            SceneSaveNarrowing.ApplyEntryName(scene, options.EntryName);

            // Build appearance packages before the save options remove the ones that failed.
            if (options.IncludeModdedAppearance)
            {
                PublishStep(operation, new SceneProgress(
                    SceneOperationKind.Save, operation.FileName,
                    ScenePhase.ApplyingAppearance, 0, 0, false, null));
                notes.AddRange(await _appearance.SealAppearance(
                    scene, actorIdentities, temporaries, AppearanceSealTimeout,
                    cancellation));
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

            // Hash the packages here, off the game's main thread, before writing the scene.
            if (_appearance.StampMcdfHashes(scene) is { Count: > 0 } stamped)
                notes.AddRange(stamped);

            // Validate the final contents, including appearance package sizes.
            var validated = SceneFileValidation.Validate(scene);
            if (!validated.Succeeded)
            {
                Finish(
                    false,
                    $"The scene did not validate: {validated.Failure!.Detail}",
                    notes);
                return;
            }

            // Anything lost when converting the file format is listed in the save's result.
            var stored = _documents.Write(scene, path);
            notes.AddRange(stored.Notes);
            var written = stored.Outcome;
            if (!written.Succeeded)
            {
                Finish(
                    false,
                    $"The scene could not be written: {written.Failure!.Detail}",
                    notes,
                    written.RecoveryEvidencePaths);
                return;
            }
            // The file exists from here on, also when the save ends as Failed
            // below, so the Library lists it.
            _observer.Saved();

            var summary =
                $"Saved {scene.Actors.Count} actors, {scene.Props.Count} objects, " +
                $"{scene.Lights.Count} lights and {scene.Cameras.Count} cameras to " +
                $"{operation.FileName}.";
            if (notes.Count > 0)
                summary += $" {notes.Count} entities carried notes.";

            // Report Failed, saying what was saved, when the file was written
            // but some requested appearance packages could not be included.
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
        }
        catch (Exception ex)
        {
            Finish(false, $"The save failed unexpectedly: {ex.Message}");
        }
        finally
        {
            foreach (var temporary in temporaries)
                _appearance.DeleteTemporary(temporary);
        }
    }


    /// <summary>Publishes the final result, the last progress step and the
    /// outcome, all built from the same result.</summary>
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
        // Every row for something that failed gets a next step to try. It is
        // filled in here, where every result passes, rather than at each of
        // the many places a load can fail, so no failed row can miss one.
        // A load that was cancelled or undone left nothing behind, so no row
        // may tell the person to select what it made.
        bool kept = state is not (OperationReceiptState.RolledBack or OperationReceiptState.Cancelled);
        entities = entities
            .Select(entity => entity.Restored || entity.Remedy != null
                ? entity
                : entity with { Remedy = entity.Kind.Remedy(kept) })
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


    /// <summary>Stops the running operation when Poser shuts down: no new
    /// operation can start, running work is cancelled, and Poser waits a short
    /// time for it to end. Work still running after that changes nothing,
    /// because every step checks for the cancel first. Safe to call twice: the
    /// workflow is registered both as itself and as its interface, so it is
    /// disposed twice, and the second call must not cancel what is already
    /// disposed.</summary>
    public void Dispose()
    {
        if (!_flight.Close())
            return;
        // Stop the inner waits first. Otherwise the cancelled operation could
        // start cleanup that needs the game's main thread, which this method
        // is holding up.
        try
        {
            _sceneState.AbandonChildWaits();
        }
        catch (Exception)
        {
            // Shutdown must not throw. The wait below still has a time limit.
        }
        _flight.Drain(DisposeDrainTimeout);
    }
}
