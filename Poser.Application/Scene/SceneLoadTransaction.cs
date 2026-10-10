using Poser.Domain.Operations;
using Poser.Domain.Scene;
using Poser.Files;
using Poser.Documents.Files;
using Poser.Documents.Scene;

namespace Poser.Application.Scene;

/// <summary>
/// ONE whole-scene load, run as an ordered list of phases with reverse-order
/// rollback. <see cref="SceneWorkflow"/> admits it, publishes what it reports
/// and drains it; everything the load decides lives here.
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
internal sealed class SceneLoadTransaction(
    SceneWorkflow workflow,
    ISceneStatePort sceneState,
    ISceneMaterializer materializer,
    IActorRestorePort actorRestore,
    ISceneHistoryPort historyPort,
    ISceneDocumentStore documents,
    SceneLoadStructure structure,
    SceneLoadRollback rollback,
    SceneLoadHistory history,
    SceneOperation operation,
    string path,
    SceneLoadOptions options,
    CancellationToken cancellation)
{
    /// <summary>
    /// A character-file import's bound. Real packages run to hundreds of
    /// megabytes and the import decompresses, extracts, applies and waits for
    /// a redraw, so this is minutes rather than the one minute it used to be —
    /// a bound that expires mid-import turns a working restore into a named
    /// failure for no reason but impatience. Cancelling the load cuts it short.
    /// </summary>
    private static readonly TimeSpan McdfImportTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How a phase stops the load. Refused: nothing native ran, so it
    /// is a plain Failed. RollBack: undo what this operation created.</summary>
    private sealed record Stop(string Detail, bool RollBack)
    {
        public static Stop Refused(string detail) => new(detail, false);
        public static Stop Abort(string detail) => new(detail, true);
    }

    private SceneLoadBarriers? _barriers;
    private readonly List<SceneEntityOutcome> _entities = new();
    // Facts about the OPERATION rather than about any one entity: what a
    // destroy-first clear cost, and which categories the user left out.
    private readonly List<string> _notes = new();
    private int _total;
    private int _done;

    private SceneFile _scene = null!;
    private List<SceneActor> _actors = null!;
    private IReadOnlyList<SceneProp> _props = null!;
    private IReadOnlyList<SceneOverlay> _overlays = null!;
    private IReadOnlyList<SceneWorldObject> _worldObjects = null!;
    private IReadOnlyList<SceneLight> _lights = null!;
    private IReadOnlyList<SceneCamera> _cameras = null!;
    private SceneEnvironment? _environment;

    private readonly Dictionary<Guid, SceneEntityHandle> _actorTokens = new();
    // Per-kind key→token maps feed the structure restore: groups
    // and the root order reference entities by these keys.
    private readonly Dictionary<Guid, SceneEntityHandle> _propTokens = new();
    private readonly Dictionary<Guid, SceneEntityHandle> _overlayTokens = new();
    private readonly Dictionary<Guid, SceneEntityHandle> _worldObjectTokens = new();
    private readonly Dictionary<Guid, SceneEntityHandle> _lightTokens = new();
    private readonly Dictionary<Guid, SceneEntityHandle> _cameraTokens = new();
    // Named once their models have streamed, below.
    private readonly List<(SceneEntityHandle Token, string Name)> _spawnedWorldObjects = new();

    public async Task Run()
    {
        try
        {
            IReadOnlyList<Func<Task<Stop?>>> phases = new Func<Task<Stop?>>[]
            {
                Read, Place, Spawn, AwaitActors, ApplyAppearance, AttachCompanions,
                AwaitCompanions, Freeze, Pose, Present, ApplyCameras, ApplyLights,
                ApplyEnvironment, Commit,
            };
            foreach (var phase in phases)
            {
                if (await phase() is not { } stop)
                    continue;
                if (stop.RollBack)
                    await Abort(stop.Detail);
                else
                    Finish(OperationReceiptState.Failed, stop.Detail);
                return;
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
            if (!workflow.Disposed)
            {
                try
                {
                    await sceneState.OnFramework(() =>
                    {
                        actorRestore.HoldPoseImports(false);
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

    private void Step(ScenePhase phase, bool cancellable = true) =>
        workflow.PublishStep(operation, new SceneProgress(
            SceneOperationKind.Load, operation.FileName,
            phase, _done, _total, cancellable, null));

    private void Finish(OperationReceiptState state, string detail) =>
        workflow.FinishTerminal(
            operation, SceneOperationKind.Load, state, detail,
            _entities, _notes, Array.Empty<string>());

    private string? Guard() => operation.Guard(sceneState, cancellation);

    private SceneLoadBarriers Barriers =>
        _barriers ??= new SceneLoadBarriers(workflow, sceneState, actorRestore, operation, cancellation);

    private async Task<string?> RollbackCreated()
    {
        Step(ScenePhase.RollingBack, cancellable: false);
        try
        {
            return await sceneState.OnFramework(() => rollback.Run(operation));
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
    private async Task Abort(string failure)
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

    // ── Phase 1 — read and validate the WHOLE document off-thread ────────

    private Task<Stop?> Read()
    {
        // Nothing native has happened yet; a corrupt, oversized, or
        // future file is a pure typed refusal.
        // Storage translates supported formats to the same scene document.
        var stored = documents.Read(path);
        _notes.AddRange(stored.Notes);
        var read = stored.Outcome;
        if (!read.Succeeded || read.Scene is not { } scene)
        {
            // Nothing native has run, so there is nothing to roll back:
            // a corrupt, oversized or future file is a plain Failed.
            return Task.FromResult<Stop?>(Stop.Refused(read.Failure!.Detail));
        }
        _scene = scene;

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
                _entities.Add(new SceneEntityOutcome(refusal.Kind, refusal.Name, false, refusal.Detail));
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
        _actors = options.IncludeActors
            ? scene.Actors.ToList()
            : new List<SceneActor>();
        _props = options.IncludeProps
            ? (IReadOnlyList<SceneProp>)scene.Props
            : Array.Empty<SceneProp>();
        _overlays = options.IncludeOverlays
            ? (IReadOnlyList<SceneOverlay>)(scene.Overlays ?? [])
            : Array.Empty<SceneOverlay>();
        _worldObjects = options.IncludeWorldObjects
            ? (IReadOnlyList<SceneWorldObject>)(scene.WorldObjects ?? [])
            : Array.Empty<SceneWorldObject>();
        _lights = options.IncludeLights
            ? (IReadOnlyList<SceneLight>)scene.Lights
            : Array.Empty<SceneLight>();
        _cameras = options.IncludeCameras
            ? (IReadOnlyList<SceneCamera>)scene.Cameras
            : Array.Empty<SceneCamera>();
        _environment = options.IncludeEnvironment
            ? scene.Environment
            : null;

        // What the file HAS that this load was told to leave alone. Stated
        // once, as a note, so a scene that came back with fewer entities
        // than it was saved with says why rather than looking short.
        AppendSkipNote("actors", options.IncludeActors, scene.Actors.Count);
        AppendSkipNote("objects", options.IncludeProps, scene.Props.Count);
        AppendSkipNote("lights", options.IncludeLights, scene.Lights.Count);
        AppendSkipNote("cameras", options.IncludeCameras, scene.Cameras.Count);
        AppendSkipNote("overlays", options.IncludeOverlays, scene.Overlays?.Count ?? 0);
        AppendSkipNote("world objects", options.IncludeWorldObjects, scene.WorldObjects?.Count ?? 0);
        AppendSkipNote("the environment", options.IncludeEnvironment, scene.Environment is null ? 0 : 1);
        foreach (var actor in _actors)
            if (actor.AppearanceNotSaved)
                _notes.Add(SceneSavePolicy.AppearanceNotSavedNote(actor.Name));
        // Scenes saved before #229 carry FABRIK chains beside a pose that
        // already holds their baked result; restoring them would restart
        // a solver over it.
        if (options.IncludeActors && scene.Actors.Any(actor => actor.Fabrik is not null))
            _notes.Add("Saved FABRIK chains from an older build were ignored; " +
                "the baked pose loads as saved.");
        return Task.FromResult<Stop?>(null);
    }

    /// <summary>One line stating a category the user left out, and only when
    /// the FILE actually carries something in it: "props were not loaded" over
    /// a scene with no props says nothing true about this load.</summary>
    private void AppendSkipNote(string category, bool included, int count)
    {
        if (included || count == 0)
            return;
        _notes.Add(count == 1 && category.StartsWith("the ", StringComparison.Ordinal)
            ? $"The file's {category[4..]} was not loaded."
            : $"The file's {count} {category} were not loaded.");
    }

    // ── Placement — still before any native call ─────────────────────────

    private async Task<Stop?> Place()
    {
        var origin = options.PlaceRelativeToCurrentOrigin
            ? await sceneState.OnFramework(sceneState.CurrentOrigin)
            : null;
        return SceneLoadPlacement.Apply(_scene, options, origin, _notes) is { } refusal
            ? Stop.Refused(refusal)
            : null;
    }

    // ── Phase 2 — baselines, then spawn/admit every entity ───────────────

    private async Task<Stop?> Spawn()
    {
        _total = _actors.Count + _props.Count +
            _lights.Count + _cameras.Count +
            _overlays.Count + _worldObjects.Count +
            (_environment is null ? 0 : 1);

        // Every entity that other phases depend on. Actor spawn failures are
        // structural: pose and relationships cannot proceed against a hole.
        //
        // The destroy-first clear runs at the head of the same framework
        // action, so nothing this load creates can be caught by the sweep
        // that was meant to precede it. It is deliberately OUTSIDE the
        // rollback ledger: rollback undoes what this operation CREATED, and
        // no ledger can resurrect an actor the user asked to be rid of —
        // which is why the clear reports what it cost.
        Step(ScenePhase.SpawningEntities);
        bool refusedBeforeClear = false;
        var spawnFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;

            if (options.ClearExistingScene)
            {
                // The clear cannot be undone, so a load that cannot even
                // start its required steps refuses BEFORE it.
                if (sceneState.LoadPreflight(_actors.Count) is { } preflight)
                {
                    refusedBeforeClear = true;
                    return $"{preflight} The session was not cleared.";
                }
                operation.SessionCleared = true;
                if (sceneState.ClearScene().Summary() is { } cleared)
                    _notes.Add(cleared);
            }

            // From the first native step to the terminal, the pose slot
            // is this load's: a library or inspector import refuses
            // instead of superseding the import an actor is waiting on.
            actorRestore.HoldPoseImports(true);

            // A baseline is captured only for what this load will WRITE:
            // restoring an environment the load never touched would undo
            // edits the user made before it.
            if (_environment is not null || options.IncludeEnvironment)
            {
                operation.EnvironmentBaseline = sceneState.CaptureEnvironmentState();
                operation.WorldBaseline = sceneState.CaptureWorldState();
            }
            if (_cameras.Count > 0)
                operation.DefaultCameraBaseline =
                    materializer.CaptureDefaultCameraState();

            foreach (var actor in _actors)
            {
                var token = materializer.SpawnActor(actor, out var detail);
                if (token is null)
                    return $"Actor '{actor.Name}' could not be spawned: " +
                        $"{detail ?? "the spawn failed."}";
                operation.SpawnedActors.Add(token);
                _actorTokens[actor.Key] = token;
            }

            foreach (var prop in _props)
            {
                var token = materializer.SpawnProp(prop, out var detail);
                if (token is null)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Object, prop.Name, false,
                        detail ?? "The object could not be spawned."));
                    continue;
                }
                operation.SpawnedProps.Add(token);
                _propTokens[prop.Key] = token;
                _entities.Add(new SceneEntityOutcome(SceneOutcomeKind.Object, prop.Name, true));
            }

            // An overlay node that will not stage is a NAMED refusal, not
            // a structural one: the scene it decorates is still a scene
            // without it, exactly as a prop's is.
            foreach (var overlay in _overlays)
            {
                string name = overlay.Node?.Name ?? "Overlay";
                var token = materializer.SpawnOverlay(overlay, out var detail);
                if (token is null)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Overlay, name, false,
                        detail ?? "The overlay could not be staged."));
                    continue;
                }
                operation.StagedOverlays.Add(token);
                _overlayTokens[overlay.Key] = token;
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.Overlay, name, true, detail));
            }

            // Borrowing back the map's own objects. A refusal here is
            // NAMED and never structural: the map may have been rebuilt,
            // the object may already be borrowed, or it may simply not be
            // standing where this scene recorded it — and a scene is still
            // a scene without it.
            foreach (var worldObject in _worldObjects)
            {
                string name = materializer.WorldObjectName(worldObject.Path);
                var token = materializer.AdoptWorldObject(
                    worldObject, out var detail);
                if (token is null)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.WorldObject, name, false,
                        detail ?? "The map object could not be borrowed."));
                    continue;
                }
                operation.BorrowedWorldObjects.Add(token);
                _worldObjectTokens[worldObject.Key] = token;
                _spawnedWorldObjects.Add((token, name));
            }
            return null;
        });
        // Nothing native ran: a plain refusal, like an unreadable file.
        if (refusedBeforeClear)
            return Stop.Refused(spawnFailure!);
        if (spawnFailure != null)
            return Stop.Abort(spawnFailure);
        _done = _props.Count + _overlays.Count + _worldObjects.Count;

        // Housing furniture streams its model in after the spawn. One
        // that has not loaded by the bound is KEPT and named — never
        // reported restored and then released behind the outcome's back.
        if (_spawnedWorldObjects.Count > 0)
        {
            var unloaded = await materializer.AwaitWorldObjectsLoaded(
                _spawnedWorldObjects.Select(entry => entry.Token).ToList(),
                workflow.ActorReadyBound, cancellation);
            foreach (var (token, name) in _spawnedWorldObjects)
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.WorldObject, name, true,
                    unloaded.Contains(token)
                        ? $"Its model did not finish loading within {workflow.ActorReadyBound.TotalSeconds:0} " +
                          "seconds. It was kept and appears once the game streams it in."
                        : null));
        }
        return null;
    }

    // ── Phase 3 — bounded readiness barrier ──────────────────────────────

    /// <summary>An actor whose body did not draw within the bound is OPTIONAL:
    /// it stays in the session (the load's undo still removes it) and
    /// leaves every later phase, so nothing tries to pose, target or
    /// hang anything off a body that is not there.</summary>
    private void DropUnready(IReadOnlyList<SceneEntityHandle> unready)
    {
        foreach (var token in unready)
        {
            var actor = _actors.First(entry => _actorTokens[entry.Key] == token);
            _actors.Remove(actor);
            _actorTokens.Remove(actor.Key);
            _done++;
            _entities.Add(new SceneEntityOutcome(
                SceneOutcomeKind.Actor, actor.Name, false,
                $"The actor's body did not finish drawing within {workflow.ActorReadyBound.TotalSeconds:0} " +
                "seconds, so it was kept but nothing more was restored onto it."));
        }
    }

    private async Task<Stop?> AwaitActors()
    {
        // Pose needs the spawned actors' skeletons, which build with their
        // draw objects.
        Step(ScenePhase.AwaitingActors);
        var ready = await Barriers.WaitForActors(_actorTokens.Values);
        if (ready.Stop != null)
            return Stop.Abort(ready.Stop);
        DropUnready(ready.Unready);
        return null;
    }

    // ── Phase 3b — character files, BEFORE anything that hangs off a body ─

    private async Task<Stop?> ApplyAppearance()
    {
        // An MCDF import redraws the actor, which destroys its draw
        // object and every skeleton with it: a pose applied first would be
        // thrown away, and a companion attached first would go with the old
        // body. Each import runs through the ORDINARY MCDF transaction, so
        // the ownership it registers — and the by-name unlock-and-restore
        // teardown that ownership buys — is the same one a hand-driven
        // import leaves behind.
        if (!_actors.Any(entry => entry.Mcdf is not null || entry.PenumbraCollection is not null))
            return null;
        Step(ScenePhase.ApplyingAppearance);
        // Collections are per-actor redraws with no shared slot: all
        // of them run at once, so N actors cost one redraw's wait,
        // not N of them back to back.
        var collections = _actors
            .Where(actor => actor.Mcdf is null && actor.PenumbraCollection is not null)
            .Select(actor => (actor.Name, Restore: actorRestore.RestoreCollection(
                _actorTokens[actor.Key], actor, SceneWorkflow.ActorReadyTimeout, cancellation)))
            .ToList();
        await Task.WhenAll(collections.Select(entry => entry.Restore));
        foreach (var (name, restore) in collections)
            if (restore.Result is { } collectionError)
                _entities.Add(new SceneEntityOutcome(SceneOutcomeKind.Collection, name, false, collectionError));

        foreach (var actor in _actors)
        {
            if (Guard() is { } stop)
                return Stop.Abort(stop);
            if (actor.Mcdf is null)
                continue;
            var appearance = await actorRestore.ImportMcdf(
                path, _actorTokens[actor.Key], actor,
                McdfImportTimeout, cancellation);
            // A missing package is a refusal by name; a package whose
            // bytes moved on is restored WITH the divergence named.
            // Neither is ever a silent skip.
            if (appearance.Detail is { } detail)
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.CharacterFile, actor.Name,
                    appearance.Restored, detail));
        }

        // The redraws rebuilt the skeletons every later phase reads.
        Step(ScenePhase.AwaitingActors);
        var rebuilt = await Barriers.WaitForActors(_actorTokens.Values);
        if (rebuilt.Stop != null)
            return Stop.Abort(rebuilt.Stop);
        DropUnready(rebuilt.Unready);
        return null;
    }

    // ── Phase 4 — explicit relationships ────────────────────────────────

    private async Task<Stop?> AttachCompanions()
    {
        Step(ScenePhase.ApplyingRelationships);
        var relationshipFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            foreach (var actor in _actors)
            {
                if (actor.CompanionKind is null)
                    continue;
                var detail = actorRestore.AttachCompanion(
                    _actorTokens[actor.Key], actor);
                if (detail != null)
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Companion, actor.Name, false, detail));
            }
            return null;
        });
        return relationshipFailure != null ? Stop.Abort(relationshipFailure) : null;
    }

    private async Task<Stop?> AwaitCompanions()
    {
        // Phase 4a — a companion's own BODY builds several frames after
        // its attachment lands, and a companion pose has nothing to land
        // on until its skeleton exists. Bounded, and deliberately NOT
        // structural: a companion that never draws costs one named refusal
        // in the pose phase, never the whole scene.
        if (_actors.Any(entry => entry.CompanionPose is not null))
        {
            Step(ScenePhase.AwaitingActors);
            await Barriers.WaitForCompanions(_actors, _actorTokens);
        }
        return null;
    }

    // ── Phase 4b — FREEZE, before the pose ──────────────────────────────

    private async Task<Stop?> Freeze()
    {
        // A scene carries pose data and no animation: a timeline id resolves
        // against the loading client's own game and mods, so replaying one
        // would show a different thing on every machine, or nothing. Stopping
        // the actor first is what makes the pose land on a held frame and the
        // load deterministic.
        Step(ScenePhase.FreezingActors);
        var freezeFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            foreach (var actor in _actors)
            {
                // VISIBILITY BEFORE THE POSE, and the ordering is the
                // invariant, not the mechanism. Hiding is a fade today
                // (ActorSpawnNativeAdapter.SetAlpha) and a fade cannot
                // cost an actor its skeleton — but this phase used to run
                // after the pose, so when hiding WAS a draw-state
                // teardown a scene saved with a hidden actor threw away
                // the pose it had just applied to it. Stated here so no
                // later change to how an actor hides can bring that back.
                actorRestore.SetActorVisibility(_actorTokens[actor.Key], actor.Visible);
                var nameDetail = actorRestore.RestoreActorName(_actorTokens[actor.Key], actor);
                if (nameDetail != null)
                    _entities.Add(new SceneEntityOutcome(SceneOutcomeKind.ActorName, actor.Name, false, nameDetail));
                var detail = actorRestore.FreezeActor(_actorTokens[actor.Key]);
                if (detail != null)
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Animation, actor.Name, false, detail));
                if (actor.Gaze?.Mode == GazeTargetMode.Detached)
                {
                    // Import deltas use the live animated basis. Detaching
                    // only afterward removes the native chest/neck aim
                    // underneath those deltas, drifting every saved pose.
                    var gazeDetail = actorRestore.ApplyActorGaze(
                        _actorTokens[actor.Key], actor, null);
                    if (gazeDetail != null)
                        _entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.Gaze, actor.Name, false, gazeDetail));
                }
            }
            return null;
        });
        return freezeFailure != null ? Stop.Abort(freezeFailure) : null;
    }

    // ── Phase 5 — pose ──────────────────────────────────────────────────

    private async Task<Stop?> Pose()
    {
        // One atomic pose import per actor, strictly sequential (the import
        // engine is single-flight), each awaited to its own terminal receipt
        // within a bound. A pose failure rolls ITSELF back and becomes a
        // typed entity outcome; the actor stays restored.
        Step(ScenePhase.ApplyingPose);
        foreach (var actor in _actors)
        {
            if (Guard() is { } stop)
                return Stop.Abort(stop);

            var token = _actorTokens[actor.Key];
            var poseResult = await Barriers.ImportPose(
                receipt => actorRestore.ArmPoseImport(
                    token, actor, $"Scene pose: {actor.Name}", receipt));
            var placement = poseResult == null
                ? await sceneState.OnFramework(() =>
                    Guard() ?? actorRestore.PlaceActor(token, actor))
                : poseResult;
            _entities.Add(placement == null
                ? new SceneEntityOutcome(SceneOutcomeKind.Actor, actor.Name, true)
                : new SceneEntityOutcome(SceneOutcomeKind.Actor, actor.Name, false, placement));

            // The companion's OWN pose, after its owner's: the same
            // single-flight engine takes one import at a time, and a
            // companion that could not be posed is a named refusal beside
            // a restored actor, never a failed scene.
            if (actor.CompanionPose is not null)
            {
                var companion = await Barriers.ImportPose(
                    receipt => actorRestore.ArmCompanionPoseImport(
                        token, actor, $"Scene companion pose: {actor.Name}",
                        receipt));
                if (companion == null)
                    companion = await sceneState.OnFramework(() =>
                        Guard() ?? actorRestore.PlaceCompanion(token, actor));
                if (companion != null)
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Companion, actor.Name, false, companion));
            }
            _done++;
            Step(ScenePhase.ApplyingPose);
        }
        return null;
    }

    // ── Phase 6 — presentation ──────────────────────────────────────────

    private async Task<Stop?> Present()
    {
        // Visibility is NOT here; it rode with the animation, before the pose
        // (see phase 4b).
        Step(ScenePhase.ApplyingPresentation);
        var presentationFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            foreach (var actor in _actors)
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
                    && !_actorTokens.TryGetValue(gazeTarget, out target))
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Gaze, actor.Name, false,
                        "The actor looks at an actor this load did not restore."));
                    continue;
                }
                var detail = actorRestore.ApplyActorGaze(
                    _actorTokens[actor.Key], actor, target);
                if (detail != null)
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Gaze, actor.Name, false, detail));
            }
            return null;
        });
        return presentationFailure != null ? Stop.Abort(presentationFailure) : null;
    }

    // ── Phase 7 — cameras ───────────────────────────────────────────────

    private async Task<Stop?> ApplyCameras()
    {
        // The default camera takes the saved default document, additional
        // cameras are created, targets re-resolve against the RESTORED actors,
        // and exactly one camera goes live.
        Step(ScenePhase.ApplyingCameras);
        var cameraFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;

            SceneEntityHandle? liveCamera = null;
            bool liveIsDefault = false;
            foreach (var camera in _cameras)
            {
                SceneEntityHandle? token = null;
                string? detail;
                if (camera.IsDefault)
                {
                    detail = materializer.ApplyDefaultCamera(camera);
                    // The default camera mints a structure token too:
                    // without one, a saved group that held the Main
                    // Camera silently lost it on every load.
                    if (detail == null
                        && materializer.DefaultCameraToken() is { } main)
                        _cameraTokens[camera.Key] = main;
                }
                else
                {
                    token = materializer.CreateCamera(camera, out detail);
                    if (token != null)
                    {
                        operation.CreatedCameras.Add(token);
                        _cameraTokens[camera.Key] = token;
                    }
                }

                if (detail != null)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Camera, camera.Camera!.Name, false, detail));
                    _done++;
                    continue;
                }

                if (camera.TargetActorKey is { } targetKey)
                {
                    // The document validated this reference; it can only
                    // miss here if the target actor never drew or this
                    // load was told to leave the actors out — then the
                    // camera is restored and its target refused BY NAME.
                    if (!_actorTokens.ContainsKey(targetKey))
                    {
                        _entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.Camera, camera.Camera!.Name, false,
                            "The camera was restored but it follows an " +
                            "actor this load did not restore."));
                        _done++;
                        continue;
                    }
                    var targetDetail = materializer.SetCameraTarget(
                        token, _actorTokens[targetKey],
                        camera.TargetActorName, camera.IsTargetLocked);
                    if (targetDetail != null)
                    {
                        _entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.Camera, camera.Camera!.Name, false,
                            $"The camera was restored but its target was not: {targetDetail}"));
                        _done++;
                        continue;
                    }
                }

                if (camera.IsLive)
                {
                    liveCamera = token;
                    liveIsDefault = camera.IsDefault;
                }
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.Camera, camera.Camera!.Name, true));
                _done++;
            }

            if (_cameras.Count > 0)
            {
                var liveDetail = materializer.SetLiveCamera(
                    liveIsDefault ? null : liveCamera);
                if (liveDetail != null)
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.LiveCamera, "Live camera", false, liveDetail));
            }
            return null;
        });
        if (cameraFailure != null)
            return Stop.Abort(cameraFailure);
        Step(ScenePhase.ApplyingCameras);
        return null;
    }

    // ── Phase 8 — lights ────────────────────────────────────────────────

    private async Task<Stop?> ApplyLights()
    {
        // An unresolvable attachment is a typed refusal of that light, never
        // a world-space spawn.
        Step(ScenePhase.ApplyingLights);
        var lightFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            foreach (var light in _lights)
            {
                // An attachment whose owner was not loaded is a NAMED
                // refusal of that light, exactly as an unresolvable
                // attachment already is: a light is never silently
                // detached into world space.
                if (light.Attachment is { } unresolved &&
                    !_actorTokens.ContainsKey(unresolved.ActorKey))
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Light, light.Light!.Name, false,
                        "The light is attached to an actor this load did " +
                        "not restore, so it was not spawned."));
                    _done++;
                    continue;
                }
                SceneEntityHandle? owner = light.Attachment is { } attachment
                    ? _actorTokens[attachment.ActorKey]
                    : null;
                var token = materializer.SpawnLight(light, owner, out var detail);
                if (token is null)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Light, light.Light!.Name, false,
                        detail ?? "The light could not be spawned."));
                }
                else
                {
                    operation.SpawnedLights.Add(token);
                    _lightTokens[light.Key] = token;
                    // A non-null detail beside a token is a named
                    // degradation (a gobo the client no longer ships),
                    // reported without refusing the light.
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Light, light.Light!.Name, true, detail));
                }
                _done++;
            }
            return null;
        });
        if (lightFailure != null)
            return Stop.Abort(lightFailure);
        Step(ScenePhase.ApplyingLights);
        return null;
    }

    // ── Phase 9 — environment and the session-wide toggles ──────────────

    private async Task<Stop?> ApplyEnvironment()
    {
        // Stamped last exactly as both references order it. The world block
        // runs even when the file states none: "no frozen water, no frozen
        // physics" is what a scene taken with the game running says, so a
        // load into a session that froze either one must RELEASE it, or the
        // scene did not restore what it saved.
        Step(ScenePhase.ApplyingEnvironment);
        var environmentFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            if (_environment is { } stated)
            {
                sceneState.ApplyEnvironment(stated);
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.Environment, "Environment", true));
                _done++;
            }
            // Reported only when something DEGRADED: a toggle that
            // landed is not worth a row beside the entities. The
            // session-wide toggles belong to the environment category,
            // so a load that leaves the environment out leaves them
            // exactly as the user set them.
            if (options.IncludeEnvironment &&
                sceneState.ApplyWorld(_scene.World ?? new SceneWorld())
                is { } detail)
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.World, "World", false, detail));
            return null;
        });
        return environmentFailure != null ? Stop.Abort(environmentFailure) : null;
    }

    // ── Commit — re-guarded ─────────────────────────────────────────────

    private async Task<Stop?> Commit()
    {
        // A cancellation or session replacement landing after the last phase
        // rolls back instead of committing.
        var structureTokens = SceneLoadStructure.Tokens(("actor", _actorTokens), ("prop", _propTokens),
            ("overlay", _overlayTokens), ("worldObject", _worldObjectTokens),
            ("light", _lightTokens), ("camera", _cameraTokens));
        // Only a stop (cancel, session replaced) aborts here: members that
        // never bound are the structure restore's named refusals.
        if (await structure.WaitForBindings(operation, _scene, structureTokens,
                workflow.StructureBindingBound, cancellation) is { } structureStop)
            return Stop.Abort(structureStop);
        Step(ScenePhase.Committing, cancellable: false);
        var committed = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            structure.Restore(operation, _scene, structureTokens, _entities);
            var failures = _entities.Where(entity => !entity.Restored).ToList();
            operation.HistoryEntities = structureTokens;
            operation.Committed = true;
            if (operation.Replay is { } replay)
            {
                foreach (var (key, previous) in replay.Entities)
                    if (structureTokens.TryGetValue(key, out var replacement))
                        historyPort.BindHistoryReplacement(previous, replacement);
                replay.Entities = structureTokens;
                replay.Groups = operation.HistoryGroups;
            }
            string detail = failures.Count == 0
                ? $"Loaded {operation.FileName}: " +
                  $"{Count(_actors.Count, "actor")}, " +
                  $"{Count(_props.Count, "object")}, " +
                  $"{Count(_lights.Count, "light")}, " +
                  $"{Count(_cameras.Count, "camera")}."
                : $"Loaded {operation.FileName} partially: " +
                  $"{Count(failures.Count, "part")} could not be " +
                  "restored (everything that did restore was kept): " +
                  string.Join("; ", failures.Select(failure =>
                      $"{failure.Kind} '{failure.Name}': {failure.Detail}"));
            // Publishing inside the framework action orders the terminal
            // before any subsequent framework-thread invalidation. Named
            // refusals beside restored entities are typed partial
            // recovery: Failed, with everything that DID restore kept.
            Finish(
                failures.Count == 0
                    ? OperationReceiptState.Applied
                    : OperationReceiptState.Failed,
                detail);
            // Partial or not, what committed is in the session, so it is
            // ONE undoable step: undo removes exactly what this load made.
            history.Append(operation, path, options);
            return null;
        });
        return committed != null ? Stop.Abort(committed) : null;
    }

    /// <summary>A count and its noun, agreeing. Scene outcomes are read by a
    /// user who just watched the thing happen; "1 actors" reads as a bug in
    /// the count, not a bug in the grammar.</summary>
    internal static string Count(int value, string noun) =>
        $"{value} {noun}{(value == 1 ? string.Empty : "s")}";
}
