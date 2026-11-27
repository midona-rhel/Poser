using Poser.Domain.Operations;
using Poser.Domain.Scene;
using Poser.Documents.Files;
using Poser.Documents.Scene;
using Poser.Application.Scene;

namespace Poser.Game.Scene;

/// <summary>
/// Loads one scene step by step and records, for each item, whether it came
/// back. If the file cannot be read, nothing changes.
/// If the load is cancelled, the session changes, or an actor cannot be
/// spawned, everything this load created is removed again. Any other item that
/// fails is named in the result, and everything that did load stays, as one
/// undo step. When the load first clears the scene, undo cannot bring back
/// what was cleared, and the result says so.
/// </summary>
internal sealed class SceneLoadTransaction(
    SceneWorkflow workflow,
    GPoseSceneState sceneState,
    SceneEntityLoader sceneEntities,
    SceneActorRestore actorRestore,
    SceneAppearance appearance,
    SceneHistoryBinder historyBinder,
    ISceneDocumentStore documents,
    SceneLoadStructure structure,
    SceneLoadRollback rollback,
    SceneLoadHistory history,
    SceneOperation operation,
    string path,
    SceneLoadOptions options,
    Guid thisInstall,
    CancellationToken cancellation)
{
    /// <summary>
    /// The longest one MCDF may take: reading the file, applying it and
    /// waiting for the actor to redraw. Cancelling can end the wait sooner.
    /// </summary>
    private static readonly TimeSpan McdfImportTimeout = TimeSpan.FromMinutes(10);

    /// <summary>How a step ends the load. Refused stops before this load has
    /// created anything. Abort removes what this load created.</summary>
    private sealed record Stop(string Detail, bool RollBack)
    {
        public static Stop Refused(string detail) => new(detail, false);
        public static Stop Abort(string detail) => new(detail, true);
    }

    private SceneLoadBarriers? _barriers;
    private readonly List<SceneEntityOutcome> _entities = new();
    // Notes about the load as a whole, such as what clearing the scene cannot undo, or a kind of item left out.
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
    // Actors and companions saved with an animation that would not play here.
    // They get the still pose the file also holds for this case.
    private readonly HashSet<Guid> _animationRefused = new();
    private readonly HashSet<Guid> _companionAnimationRefused = new();
    // For each kind of item, its key in the file and its handle in the scene.
    // Groups and the order of the scene's items refer to items by these keys.
    private readonly Dictionary<Guid, SceneEntityHandle> _propTokens = new();
    private readonly Dictionary<Guid, SceneEntityHandle> _overlayTokens = new();
    private readonly Dictionary<Guid, SceneEntityHandle> _worldObjectTokens = new();
    private readonly Dictionary<Guid, SceneEntityHandle> _lightTokens = new();
    private readonly Dictionary<Guid, SceneEntityHandle> _cameraTokens = new();
    // Spawned world objects. Each is reported once its model has loaded, further down.
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
            // A cancelled wait throws. Cancel was still pressed, so report a
            // cancel, not a failure.
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
            // Skip this while the plugin unloads: unloading blocks the game
            // thread this call needs, and the hold on pose imports goes with
            // the plugin anyway.
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
                    // The game thread is gone, and the hold on pose imports with it.
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
            // The game thread is gone because the plugin is shutting down;
            // there is nothing left to restore.
            return ex.Message;
        }
    }

    // Remove what this load created. If anything cannot be removed, name it
    // so it can be removed by hand.
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

    // ── Step 1: read and check the whole file, off the game thread ───────

    private Task<Stop?> Read()
    {
        // Older scene formats are read into the same scene document.
        var stored = documents.Read(path);
        _notes.AddRange(stored.Notes);
        var read = stored.Outcome;
        if (!read.Succeeded || read.Scene is not { } scene)
        {
            // Nothing in the game has changed yet, so there is nothing to
            // undo. A damaged file, one that is too large, or one from a newer
            // Poser simply fails.
            return Task.FromResult<Stop?>(Stop.Refused(read.Failure!.Detail));
        }
        _scene = scene;
        options = SceneLoadPreferences.ResolveDefault(options, scene.TerritoryId);

        // Items whose own data is broken were left out of the document and
        // are named here; the rest of the scene still loads. A broken item of
        // a kind this load leaves out is not reported.
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

        // One list per kind of item. A kind that is left out gets an empty
        // list instead of a flag each step must check, so a kind can never be
        // skipped by only some steps. The actors get their own copy: an actor
        // whose body never draws is taken out of it, stays in the scene, is
        // named in the result, and is skipped by every later step.
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

        // Note what the file holds that this load was told to leave out, so a
        // scene that comes back with fewer items than it was saved with says
        // why.
        AppendSkipNote("actors", options.IncludeActors, scene.Actors.Count);
        AppendSkipNote("objects", options.IncludeProps, scene.Props.Count);
        AppendSkipNote("lights", options.IncludeLights, scene.Lights.Count);
        AppendSkipNote("cameras", options.IncludeCameras, scene.Cameras.Count);
        AppendSkipNote("overlays", options.IncludeOverlays, scene.Overlays?.Count ?? 0);
        AppendSkipNote("world objects", options.IncludeWorldObjects, scene.WorldObjects?.Count ?? 0);
        AppendSkipNote("the environment", options.IncludeEnvironment, scene.Environment is null ? 0 : 1);
        // Older scene files hold FABRIK chains next to a pose that already
        // includes what they did. Turning them back on would run IK over that
        // pose again.
        if (options.IncludeActors && scene.Actors.Any(actor => actor.Fabrik is not null))
            _notes.Add("Saved FABRIK chains from an older build were ignored; " +
                "the baked pose loads as saved.");
        return Task.FromResult<Stop?>(null);
    }

    /// <summary>Adds a note for a kind of item that was left out, but only when
    /// the file holds some. Saying objects were not loaded for a scene with no
    /// objects would be misleading.</summary>
    private void AppendSkipNote(string category, bool included, int count)
    {
        if (included || count == 0)
            return;
        _notes.Add(count == 1 && category.StartsWith("the ", StringComparison.Ordinal)
            ? $"The file's {category[4..]} was not loaded."
            : $"The file's {count} {category} were not loaded.");
    }

    // ── Placement, still before anything changes in the game ────────────

    private async Task<Stop?> Place()
    {
        var origin = options.PlaceRelativeToCurrentOrigin
            ? await sceneState.OnFramework(sceneState.CurrentOrigin)
            : null;
        return SceneLoadPlacement.Apply(_scene, options, origin, _notes) is { } refusal
            ? Stop.Refused(refusal)
            : null;
    }

    // ── Step 2: remember what to restore, then create every item ────────

    private async Task<Stop?> Spawn()
    {
        _total = _actors.Count + _props.Count +
            _lights.Count + _cameras.Count +
            _overlays.Count + _worldObjects.Count +
            (_environment is null ? 0 : 1);

        // Remember the actors this load reuses before clearing or loading
        // anything. Undo puts those actors back and removes what this load
        // creates. Undo does not bring back what clearing the scene removed.
        Step(ScenePhase.SpawningEntities);
        bool refusedBeforeClear = false;
        var spawnFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;

            if (options.ReuseExistingActors)
                foreach (var actor in _actors)
                {
                    var existing = sceneEntities.FindMatchingActor(actor, _actorTokens.Values.ToArray());
                    if (existing == null && actor.LocalPlayer && actor.IdentityHash == null)
                        existing = sceneEntities.FindLocalPlayer();
                    if (existing == null) continue;
                    operation.ActorRestores.Add(sceneEntities.CaptureActorRestore(existing));
                    _actorTokens[actor.Key] = existing;
                }

            if (options.ClearExistingScene)
            {
                // Check actor capacity before clearing the current scene.
                if (sceneState.LoadPreflight(_actors.Count) is { } preflight)
                {
                    refusedBeforeClear = true;
                    return $"{preflight} The session was not cleared.";
                }
                operation.SessionCleared = true;
                // A scene that holds the player's own character keeps it, so
                // the load can reuse it instead of spawning a copy.
                if (sceneState.ClearScene(keepLocalPlayer: _actors.Any(actor => actor.LocalPlayer && actor.IdentityHash == null),
                        keepActors: _actorTokens.Values.ToArray())
                    .Summary() is { } cleared)
                    _notes.Add(cleared);
            }

            // Keep other pose imports out while this scene is restoring actors.
            actorRestore.HoldPoseImports(true);

            // Remember the current state only for what this load will change.
            // Putting back an environment the load never touched would undo
            // changes made before the load.
            if (options.IncludeEnvironment)
            {
                operation.EnvironmentBaseline = sceneState.CaptureEnvironmentState();
                operation.WorldBaseline = sceneState.CaptureWorldState();
            }
            if (_cameras.Count > 0)
                operation.DefaultCameraBaseline =
                    sceneEntities.CaptureDefaultCameraState();

            int actorNumber = 0;
            foreach (var actor in _actors)
            {
                actorNumber++;
                if (_actorTokens.ContainsKey(actor.Key)) continue;
                if (actor.IdentityHash != null)
                    actor.Name = $"Actor {actorNumber}";
                var token = sceneEntities.SpawnActor(actor, out var detail);
                if (token is null)
                    return $"Actor '{actor.Name}' could not be spawned: {detail}";
                operation.SpawnedActors.Add(token);
                _actorTokens[actor.Key] = token;
                if (actor.AppearanceNotSaved)
                    _notes.Add(SceneSavePolicy.AppearanceNotSavedNote(actor.Name));
            }

            foreach (var prop in _props)
            {
                var token = sceneEntities.SpawnProp(prop, out var detail);
                if (token is null)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Object, prop.Name, false,
                        detail));
                    continue;
                }
                operation.SpawnedProps.Add(token);
                _propTokens[prop.Key] = token;
                _entities.Add(new SceneEntityOutcome(SceneOutcomeKind.Object, prop.Name, true));
            }

            // An overlay that cannot be added is named in the result but does
            // not stop the load, just like an object: the scene still works
            // without it.
            foreach (var overlay in _overlays)
            {
                string name = overlay.Node!.Name;
                var token = sceneEntities.SpawnOverlay(overlay, out var detail);
                if (token is null)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Overlay, name, false,
                        detail));
                    continue;
                }
                operation.StagedOverlays.Add(token);
                _overlayTokens[overlay.Key] = token;
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.Overlay, name, true, detail));
            }

            // A missing map object is reported beside the rest of the loaded scene.
            foreach (var worldObject in _worldObjects)
            {
                string name = sceneEntities.WorldObjectName(worldObject.Path);
                var token = sceneEntities.AdoptWorldObject(
                    worldObject, _scene.TerritoryId, out var detail);
                if (token is null)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.WorldObject, name, false,
                        detail));
                    continue;
                }
                operation.BorrowedWorldObjects.Add(token);
                _worldObjectTokens[worldObject.Key] = token;
                if (worldObject.Spawned) _spawnedWorldObjects.Add((token, name));
                else _entities.Add(new SceneEntityOutcome(SceneOutcomeKind.WorldObject, name, true, detail));
            }
            return null;
        });
        // Nothing in the game changed yet, so this simply fails, like an unreadable file.
        if (refusedBeforeClear)
            return Stop.Refused(spawnFailure!);
        if (spawnFailure != null)
            return Stop.Abort(spawnFailure);
        _done = _props.Count + _overlays.Count + _worldObjects.Count;

        // Housing furniture loads its model after it is spawned. One that
        // has not loaded in time is kept and named in the result, rather than
        // reported as loaded and then quietly removed.
        if (_spawnedWorldObjects.Count > 0)
        {
            var unloaded = await sceneEntities.AwaitWorldObjectsLoaded(
                _spawnedWorldObjects.Select(entry => entry.Token).ToList(),
                SceneWorkflow.ActorReadyTimeout, cancellation);
            foreach (var (token, name) in _spawnedWorldObjects)
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.WorldObject, name, true,
                    unloaded.Contains(token)
                        ? $"Its model did not finish loading within {SceneWorkflow.ActorReadyTimeout.TotalSeconds:0} " +
                          "seconds. It was kept and appears once the game streams it in."
                        : null));
        }
        return null;
    }

    // ── Step 3: wait, up to a time limit, for the actors to draw ────────

    /// <summary>An actor whose body did not draw in time stays in the scene,
    /// and undoing the load still removes it. Every later step skips it, so
    /// nothing tries to pose it, look at it or attach anything to a body that
    /// is not there.</summary>
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
                $"The actor's body did not finish drawing within {SceneWorkflow.ActorReadyTimeout.TotalSeconds:0} " +
                "seconds, so it was kept but nothing more was restored onto it."));
        }
    }

    private async Task<Stop?> AwaitActors()
    {
        // Posing needs the actors' skeletons, which only exist once their
        // bodies are drawn.
        Step(ScenePhase.AwaitingActors);
        var ready = await Barriers.WaitForActors(_actorTokens.Values);
        if (ready.Stop != null)
            return Stop.Abort(ready.Stop);
        DropUnready(ready.Unready);
        return null;
    }

    // ── Step 3b: appearance, before anything is attached to a body ──────

    private async Task<Stop?> ApplyAppearance()
    {
        // Applying an MCDF redraws the actor, which throws away its body and
        // skeletons. A pose applied before it would be lost, and a companion
        // attached before it would go with the old body. Each MCDF is applied
        // the same way as one applied by hand, so removing it later works the
        // same way too.
        if (!_actors.Any(entry => entry.Mcdf is not null || entry.PenumbraCollection is not null || entry.ClothingOnly))
            return null;
        Step(ScenePhase.ApplyingAppearance);
        // Setting a Penumbra collection redraws only that actor, so all of
        // them run at once. Many actors then wait for one redraw, not for
        // one redraw after another.
        bool Editable(SceneActor actor) => SceneAppearanceRule.LoadsEditable(_scene, actor, thisInstall);
        var collections = _actors
            .Where(actor => (actor.Mcdf is null || Editable(actor)) && actor.PenumbraCollection is not null)
            .Select(actor => (actor.Name, Restore: appearance.RestoreCollection(
                _actorTokens[actor.Key], actor, SceneWorkflow.ActorReadyTimeout, cancellation)))
            .ToList();
        await Task.WhenAll(collections.Select(entry => entry.Restore));
        foreach (var (name, restore) in collections)
            if (!restore.Result.Success || restore.Result.Detail is not null)
                _entities.Add(new SceneEntityOutcome(SceneOutcomeKind.Collection, name,
                    restore.Result.Success, restore.Result.Detail));

        foreach (var actor in _actors)
        {
            if (Guard() is { } stop)
                return Stop.Abort(stop);
            if (actor.Mcdf is null)
                continue;
            var applied = Editable(actor)
                ? await appearance.ApplySceneAppearance(path, _actorTokens[actor.Key], actor,
                    McdfImportTimeout, cancellation)
                : await appearance.ImportMcdf(
                    path, _actorTokens[actor.Key], actor,
                    McdfImportTimeout, cancellation);
            // A missing MCDF is named as not loaded. An MCDF that changed since
            // the scene was saved is applied, and the result says it changed.
            // Neither is skipped silently.
            if (applied.Detail is { } detail)
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.CharacterFile, actor.Name,
                    applied.Restored, detail));
        }

        // Clothing only also redraws the actor. It must come after the
        // appearance and before the wait below and the pose, which need the final body.
        foreach (var actor in _actors.Where(actor => actor.ClothingOnly))
        {
            if (Guard() is { } stop) return Stop.Abort(stop);
            var detail = await actorRestore.RestoreClothingOnly(_actorTokens[actor.Key], cancellation);
            if (detail != null)
                _entities.Add(new SceneEntityOutcome(SceneOutcomeKind.CharacterFile, actor.Name, false, detail));
        }

        // The redraws rebuilt the skeletons, so wait for them again before later steps read them.
        Step(ScenePhase.AwaitingActors);
        var rebuilt = await Barriers.WaitForActors(_actorTokens.Values);
        if (rebuilt.Stop != null)
            return Stop.Abort(rebuilt.Stop);
        DropUnready(rebuilt.Unready);
        return null;
    }

    // ── Step 4: companions ──────────────────────────────────────────────

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
        // A companion's body is built several frames after it is attached,
        // and its pose cannot be applied until its skeleton exists. The wait
        // has a time limit. A companion that never draws is named in the
        // result when posing, but does not stop the load.
        if (_actors.Any(entry => entry.CompanionPose is not null))
        {
            Step(ScenePhase.AwaitingActors);
            await Barriers.WaitForCompanions(_actors, _actorTokens);
        }
        return null;
    }

    // ── Step 4b: stop the animation, before the pose ────────────────────

    private async Task<Stop?> Freeze()
    {
        // Restore saved animation when present; otherwise freeze the actor before
        // applying its pose. If the animation cannot play, the saved still pose is used.
        Step(ScenePhase.FreezingActors);
        var freezeFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            foreach (var actor in _actors)
            {
                // Set visibility before posing. If hiding an actor rebuilt its
                // body, it would throw away a pose applied earlier.
                actorRestore.SetActorVisibility(_actorTokens[actor.Key], actor.Visible);
                var nameDetail = actorRestore.RestoreActorName(_actorTokens[actor.Key], actor);
                if (nameDetail != null)
                    _entities.Add(new SceneEntityOutcome(SceneOutcomeKind.ActorName, actor.Name, false, nameDetail));
                // An actor saved with its animation plays it again instead of
                // stopping, and its bone edits go on top when posing. If the
                // animation will not play, the actor is frozen in the saved still pose.
                var token = _actorTokens[actor.Key];
                string? detail = null;
                if (actor.Animation is { } animation)
                {
                    detail = actorRestore.RestoreAnimation(token, animation, companion: false);
                    if (detail != null && actor.Pose is not null)
                    {
                        _animationRefused.Add(actor.Key);
                        detail = $"{detail} The saved pose was used instead.";
                        if (actorRestore.FreezeActor(token) is { } frozen)
                            detail = $"{detail} {frozen}";
                    }
                }
                else
                    detail = actorRestore.FreezeActor(token);
                if (detail != null)
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Animation, actor.Name, false, detail));
                if (actor.CompanionAnimation is { } companionAnimation
                    && actorRestore.RestoreAnimation(token, companionAnimation, companion: true) is { } companionDetail)
                {
                    if (actor.CompanionPose is not null)
                        _companionAnimationRefused.Add(actor.Key);
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Animation, actor.Name, false, companionDetail));
                }
                if (actor.Gaze?.Mode == GazeTargetMode.Detached)
                {
                    // The pose is applied as changes on top of what the
                    // animation shows now. Detaching the gaze after that would
                    // remove the game's own chest and neck turn from under
                    // those changes, and every saved pose would end up off.
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

    // ── Step 5: pose ────────────────────────────────────────────────────

    private async Task<Stop?> Pose()
    {
        // One pose per actor, one after another, because only one pose can be
        // applied at a time. Each pose is applied whole or not at all, within
        // a time limit. A pose that fails undoes itself and is named in the
        // result; the actor stays.
        Step(ScenePhase.ApplyingPose);
        foreach (var actor in _actors)
        {
            if (Guard() is { } stop)
                return Stop.Abort(stop);

            var token = _actorTokens[actor.Key];
            string? placement;
            if (actor.Animation is not null && !_animationRefused.Contains(actor.Key))
            {
                // Saved with its animation: apply only the bone edits made in
                // Poser, as offsets on top of the animation, not the still pose.
                // An edit for a bone this skeleton lacks is named, and the actor
                // is still placed.
                var edits = await sceneState.OnFramework(() =>
                    Guard() ?? actorRestore.RestorePoseEdits(token, actor, companion: false));
                var placed = await sceneState.OnFramework(() =>
                    Guard() ?? actorRestore.PlaceActor(token, actor));
                placement = edits == null ? placed : placed == null ? edits : $"{edits} {placed}";
            }
            else
            {
                var poseResult = await Barriers.ImportPose(
                    receipt => actorRestore.ArmPoseImport(
                        token, actor, $"Scene pose: {actor.Name}", receipt));
                placement = poseResult == null
                    ? await sceneState.OnFramework(() =>
                        Guard() ?? actorRestore.PlaceActor(token, actor))
                    : poseResult;
            }
            _entities.Add(placement == null
                ? new SceneEntityOutcome(SceneOutcomeKind.Actor, actor.Name, true)
                : new SceneEntityOutcome(SceneOutcomeKind.Actor, actor.Name, false, placement));

            if (actor.CompanionAnimation is not null && !_companionAnimationRefused.Contains(actor.Key))
            {
                // The companion's edits over its animation, then its placement.
                var edits = await sceneState.OnFramework(() =>
                    Guard() ?? actorRestore.RestorePoseEdits(token, actor, companion: true));
                var placed = await sceneState.OnFramework(() =>
                    Guard() ?? actorRestore.PlaceCompanion(token, actor));
                var companion = edits == null ? placed : placed == null ? edits : $"{edits} {placed}";
                if (companion != null)
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Companion, actor.Name, false, companion));
            }
            // The companion's own pose comes after its actor's, because only
            // one pose can be applied at a time. A companion that cannot be
            // posed is named in the result; it does not fail the scene.
            else if (actor.CompanionPose is not null)
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

    // ── Step 6: where the actors look ───────────────────────────────────

    private async Task<Stop?> Present()
    {
        // Visibility was already set in step 4b, before the pose.
        Step(ScenePhase.ApplyingPresentation);
        var presentationFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            foreach (var actor in _actors)
            {
                if (actor.Gaze?.Mode == GazeTargetMode.Detached)
                    continue; // Already set before the pose was applied.
                // Looking at something comes after the pose. The game turns
                // the head and eyes again every frame anyway, and an actor
                // being looked at must already be loaded. The file was checked,
                // so a target is only missing when it was left out or never drew.
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

    // ── Step 7: cameras ─────────────────────────────────────────────────

    private async Task<Stop?> ApplyCameras()
    {
        // Every saved camera becomes a new camera. A camera that followed an
        // actor follows the loaded actor, and the camera that was live when
        // saving becomes live again.
        Step(ScenePhase.ApplyingCameras);
        var cameraFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;

            SceneEntityHandle? liveCamera = null;
            foreach (var camera in _cameras)
            {
                // A saved Main camera loads as a new camera; it does not
                // overwrite the Main camera already in the session.
                if (sceneEntities.CreateCamera(camera, out var detail) is not { } token)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Camera, camera.Camera!.Name, false, detail));
                    _done++;
                    continue;
                }
                operation.CreatedCameras.Add(token);
                _cameraTokens[camera.Key] = token;

                if (camera.IsLive)
                    liveCamera = token;

                if (camera.TargetActorKey is { } targetKey)
                {
                    // Loading cameras without actors still restores the saved view.
                    if (!_actorTokens.TryGetValue(targetKey, out var targetActor))
                    {
                        _entities.Add(new SceneEntityOutcome(
                            SceneOutcomeKind.Camera, camera.Camera!.Name, false,
                            "The camera was restored but it follows an " +
                            "actor this load did not restore."));
                        _done++;
                        continue;
                    }
                    var targetDetail = sceneEntities.SetCameraTarget(
                        token, targetActor,
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

                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.Camera, camera.Camera!.Name, true));
                _done++;
            }

            if (_cameras.Count > 0)
            {
                var liveDetail = sceneEntities.SetLiveCamera(
                    liveCamera);
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

    // ── Step 8: lights ──────────────────────────────────────────────────

    private async Task<Stop?> ApplyLights()
    {
        // A light attached to something that cannot be found is not loaded
        // and is named. It is never spawned loose in the world instead.
        Step(ScenePhase.ApplyingLights);
        var lightFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            foreach (var light in _lights)
            {
                // The same goes for a light attached to an actor this load
                // did not load: it is named and not spawned, never quietly
                // placed loose in the world.
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
                var token = sceneEntities.SpawnLight(light, owner, out var detail);
                if (token is null)
                {
                    _entities.Add(new SceneEntityOutcome(
                        SceneOutcomeKind.Light, light.Light!.Name, false,
                        detail));
                }
                else
                {
                    operation.SpawnedLights.Add(token);
                    _lightTokens[light.Key] = token;
                    // A light can load with a problem, such as a gobo the
                    // game no longer has. The light is kept and the problem
                    // is named.
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

    // ── Step 9: environment, and frozen water and physics ───────────────

    private async Task<Stop?> ApplyEnvironment()
    {
        // When the environment is loaded and the file has no water or physics
        // settings, both go back to normal. This unfreezes anything still
        // frozen from before the load.
        Step(ScenePhase.ApplyingEnvironment);
        var environmentFailure = await sceneState.OnFramework(() =>
        {
            if (Guard() is { } stop)
                return stop;
            if (_environment is { } stated)
            {
                // A scene comes back looking as it did when it was saved. The file always
                // holds the time of day and the weather, also when the game had chosen them
                // by itself, so both are held here instead of being left to today's. The
                // sky, light, fog and the rest follow from those two, apart from the
                // sections that were changed by hand, which the file holds as well.
                stated.IsTimeFrozen = true;
                stated.IsWeatherOverrideEnabled = true;
                sceneState.ApplyEnvironment(stated);
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.Environment, "Environment", true));
                _done++;
            }
            // Reported only when something went wrong; a setting that worked
            // needs no line in the result. Frozen water and physics count as
            // part of the environment, so a load that leaves the environment
            // out leaves them as they were.
            if (options.IncludeEnvironment &&
                sceneState.ApplyWorld(_scene.World ?? new SceneWorld())
                is { } detail)
                _entities.Add(new SceneEntityOutcome(
                    SceneOutcomeKind.World, "World", false, detail));
            return null;
        });
        return environmentFailure != null ? Stop.Abort(environmentFailure) : null;
    }

    // ── Finish: check once more, then keep the result ───────────────────

    private async Task<Stop?> Commit()
    {
        // A cancel or a new session that arrives after the last step still
        // removes everything instead of keeping it.
        var structureTokens = SceneLoadStructure.Tokens((SceneStructureKind.Actor, _actorTokens), (SceneStructureKind.Prop, _propTokens),
            (SceneStructureKind.Overlay, _overlayTokens), (SceneStructureKind.WorldObject, _worldObjectTokens),
            (SceneStructureKind.Light, _lightTokens), (SceneStructureKind.Camera, _cameraTokens));
        // Only a cancel or a new session stops the load here. Group members
        // that never turned up are named when the groups are restored.
        if (await structure.WaitForBindings(operation, _scene, structureTokens,
                SceneWorkflow.StructureBindingTimeout, cancellation) is { } structureStop)
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
                        historyBinder.BindHistoryReplacement(previous, replacement);
                replay.Entities = structureTokens;
                replay.Groups = operation.HistoryGroups;
            }
            string detail = failures.Count == 0
                ? $"Loaded {operation.FileName}: " +
                  $"{Count(_actors.Count, "actor")}, " +
                  $"{Count(_props.Count + _worldObjects.Count, "object")}, " +
                  $"{Count(_lights.Count, "light")}, " +
                  $"{Count(_cameras.Count, "camera")}."
                : $"Loaded {operation.FileName} partially: " +
                  $"{Count(failures.Count, "part")} could not be " +
                  "restored (everything that did restore was kept): " +
                  string.Join("; ", failures.Select(failure =>
                      $"{failure.Kind} '{failure.Name}': {failure.Detail}"));
            // Reporting the result here, on the game thread, makes sure it is
            // reported before anything later on that thread can cancel the
            // load. A load where some items failed is reported as Failed, but
            // everything that did load is kept.
            Finish(
                failures.Count == 0
                    ? OperationReceiptState.Applied
                    : OperationReceiptState.Failed,
                detail);
            // Whether every item loaded or not, what loaded is now in the
            // scene, as one undo step. Undo removes exactly what this load made.
            history.Append(operation, path, options);
            return null;
        });
        return committed != null ? Stop.Abort(committed) : null;
    }

    /// <summary>Formats a count with a noun whose plural is formed by adding s.</summary>
    internal static string Count(int value, string noun) =>
        $"{value} {noun}{(value == 1 ? string.Empty : "s")}";
}
