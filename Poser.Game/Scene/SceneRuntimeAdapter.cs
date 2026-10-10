using Poser.Application.World;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using Poser.Scene;
using System;
using Poser.Domain.Identity;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;
using Poser.Domain.Scene;

namespace Poser.Game.Scene;

/// <summary>
/// The production <see cref="ISceneRuntime"/>: thin bindings from the scene
/// transaction's phase vocabulary onto the real owners — the accepted spawn
/// service, the ONE atomic pose import, the lighting/camera/environment
/// services. It owns no transaction state; every
/// method is one materialization step.
/// </summary>
internal sealed partial class SceneRuntimeAdapter : ISceneRuntime, IDisposable
{
    private readonly SceneRuntimeHandles _handles;
    private readonly IEntityHistoryBinding<IActor> _actorHistory;
    private readonly IEntityHistoryBinding<IPropHandle> _propHistory;
    private readonly IEntityHistoryBinding<IOverlayNode> _overlayHistory;
    private readonly IEntityHistoryBinding<IWorldObject> _worldHistory;
    private readonly IEntityHistoryBinding<ILight> _lightHistory;
    private readonly IEntityHistoryBinding<IVirtualCamera> _cameraHistory;
    private readonly SessionAppearanceFiles _historyAppearanceFiles = new(DeleteQuietly);
    private readonly IFramework _framework;
    private readonly ISceneDocumentStore _documents;
    private readonly ISessionGenerationSource _sessions;
    private readonly SceneCaptureService _capture;
    private readonly Poser.Config.ConfigurationService _configuration;
    private readonly IPoseImportCommands _poses;
    private readonly PoseImportCoordinator _imports;
    private readonly IActorSpawnService _spawns;
    private readonly ISkeletonService _skeletons;
    private readonly IPosingService _posing;
    private readonly PropSpawnService _props;
    private readonly Poser.Game.Overlays.OverlayNodeService _overlays;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly IEnvironmentRuntimePort _environment;
    private readonly IEnvironmentControl _environmentControl;
    private readonly StableBindingRegistry _bindings;
    private readonly AnimationSession _animation;
    private readonly IGazeService _gaze;
    private readonly IBonePosingService _bonePosing;
    private readonly Poser.Application.Integration.ActorIntegrationSession _integration;
    private readonly IWorldRenderingRuntimePort _rendering;
    private readonly IActorManager _actors;
    private readonly IObjectTable _objects;
    private readonly WorldObjects.WorldService _worldObjects;

    /// <summary>Finds an appearance package by its bytes. Held as the
    /// interface: the library owns MCDFs and will own this index too.
    /// </summary>
    private readonly Poser.Library.IMcdfHashIndex _mcdfHashes;

    /// <summary>The selection, so a destroy path can never leave it pointing
    /// at something that no longer exists. Injected directly, matching
    /// <c>TargetSyncService</c> — there is no despawn event the selection
    /// listens to, so a service that destroys entities holds it.</summary>
    private readonly Poser.Application.Selection.SelectionSession _selection;

    /// <summary>Breadcrumbs for the scene pose leg. Null under the contract
    /// tests, which assert read models rather than the log.</summary>
    private readonly IPluginLog? _log;

    public SceneRuntimeAdapter(
        IFramework framework,
        ISceneDocumentStore documents,
        ISessionGenerationSource sessions,
        SceneCaptureService capture,
        IPoseImportCommands poses,
        PoseImportCoordinator imports,
        IActorSpawnService spawns,
        ISkeletonService skeletons,
        IPosingService posing,
        PropSpawnService props,
        Poser.Game.Overlays.OverlayNodeService overlays,
        ILightingService lighting,
        IVirtualCameraService cameras,
        IEnvironmentRuntimePort environment,
        IEnvironmentControl environmentControl,
        StableBindingRegistry bindings,
        AnimationSession animation,
        IGazeService gaze,
        Poser.Application.Integration.ActorIntegrationSession integration,
        IWorldRenderingRuntimePort rendering,
        IActorManager actors,
        IObjectTable objects,
        WorldObjects.WorldService worldObjects,
        Poser.Library.IMcdfHashIndex mcdfHashes,
        Poser.Application.Selection.SelectionSession selection,
        IBonePosingService bonePosing,
        IEntityHistoryBinding<IActor> actorHistory,
        IEntityHistoryBinding<IPropHandle> propHistory,
        IEntityHistoryBinding<IOverlayNode> overlayHistory,
        IEntityHistoryBinding<IWorldObject> worldHistory,
        IEntityHistoryBinding<ILight> lightHistory,
        IEntityHistoryBinding<IVirtualCamera> cameraHistory,
        Poser.Config.ConfigurationService configuration,
        IPluginLog? log = null)
    {
        _configuration = configuration;
        _actorHistory = actorHistory;
        _propHistory = propHistory;
        _overlayHistory = overlayHistory;
        _worldHistory = worldHistory;
        _lightHistory = lightHistory;
        _cameraHistory = cameraHistory;
        _bonePosing = bonePosing;
        _mcdfHashes = mcdfHashes;
        _selection = selection;
        _log = log;
        _actors = actors;
        _objects = objects;
        _worldObjects = worldObjects;
        _rendering = rendering;
        _integration = integration;
        _bindings = bindings;
        _animation = animation;
        _gaze = gaze;
        _framework = framework;
        _documents = documents;
        _sessions = sessions;
        _handles = new(() => ActiveSession);
        _capture = capture;
        _poses = poses;
        _imports = imports;
        _spawns = spawns;
        _skeletons = skeletons;
        _posing = posing;
        _props = props;
        _overlays = overlays;
        _lighting = lighting;
        _cameras = cameras;
        _environment = environment;
        _environmentControl = environmentControl;
        _framework.Update += SweepHistoryAppearance;
        // Packages a crash left behind are never retained by any session.
        // The legacy temp root held them before Poser had its own folder.
        _ = Task.Run(() =>
        {
            var cutoff = DateTime.UtcNow - SessionAppearanceFiles.StaleAge;
            SessionAppearanceFiles.DeleteStale(
                SessionAppearanceFiles.TempDirectory, cutoff, DeleteQuietly);
            SessionAppearanceFiles.DeleteStale(
                System.IO.Path.GetTempPath(), cutoff, DeleteQuietly);
        });
    }

    private void SweepHistoryAppearance(IFramework _)
    {
        _historyAppearanceFiles.Sweep(ActiveSession);
        _handles.Synchronize();
    }

    public void Dispose()
    {
        _framework.Update -= SweepHistoryAppearance;
        _historyAppearanceFiles.Dispose();
        _handles.Clear();
    }

    public SessionGeneration? ActiveSession => _sessions.ActiveSessionGeneration;

    public Task<T> OnFramework<T>(Func<T> func) =>
        _framework.RunOnFrameworkThread(func);

    public void AbandonChildWaits() => _integration.AbandonMcdfWaits();

    public SelectionId? ResolveSceneEntity(SceneEntityHandle token) => SelectionOf(_handles.Resolve(token));

    public SelectionId? ResolveHistoryEntity(SceneEntityHandle token) => SelectionOf(_handles.Resolve(token) switch
    {
        IActor actor => _actorHistory.Resolve(actor),
        IPropHandle prop => _propHistory.Resolve(prop),
        IOverlayNode overlay => _overlayHistory.Resolve(overlay),
        IWorldObject world => _worldHistory.Resolve(world),
        ILight light => _lightHistory.Resolve(light),
        IVirtualCamera camera => _cameraHistory.Resolve(camera),
        _ => null,
    });

    private SelectionId? SelectionOf(object? entity)
    {
        IEntityBindings bindings = _bindings;
        return entity switch
        {
            IActor actor when bindings.GetActorId(actor) is { } id => SelectionId.ForActor(id),
            IPropHandle prop when bindings.GetPropId(prop) is { } id => SelectionId.ForProp(id),
            IOverlayNode overlay when bindings.GetOverlayId(overlay) is { } id => SelectionId.ForOverlay(id),
            IWorldObject world when bindings.GetWorldObjectId(world) is { } id => SelectionId.ForWorldObject(id),
            ILight light when bindings.GetLightId(light) is { } id => SelectionId.ForLight(id),
            IVirtualCamera camera when bindings.GetCameraId(camera) is { } id => SelectionId.ForCamera(id),
            _ => null,
        };
    }

    public IReadOnlyList<string> StampMcdfHashes(SceneFile scene)
    {
        var notes = new List<string>();
        foreach (var actor in scene.Actors)
        {
            if (actor.Mcdf is not { } mcdf)
                continue;
            // A sealed portable payload already carries the digest of the
            // bytes in the document. Re-hashing the source path would stamp a
            // file the document no longer depends on.
            if (mcdf.IsPortable)
                continue;
            var hashed = HashFile(mcdf.Path);
            if (hashed is null)
            {
                // The reference is still worth saving: the load can follow the
                // path, it just cannot vouch that the bytes are the same.
                notes.Add(
                    $"Actor '{actor.Name}''s character file '{mcdf.FileName}' " +
                    "could not be read while saving; the scene records where it " +
                    "was but cannot check it has not changed.");
                continue;
            }
            mcdf.ContentHash = hashed;
        }
        return notes;
    }

    private static string? HashFile(string path)
    {
        try
        {
            using var stream = System.IO.File.OpenRead(path);
            return Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── portable appearance ──────────────────────────────────────────────

    public async Task<SceneSealOutcome> SealAppearance(
        SceneFile scene,
        IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId> identities,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        var notes = new List<string>();
        var temporaries = new List<string>();
        long total = 0;
        foreach (var actor in scene.Actors)
        {
            if (cancellation.IsCancellationRequested)
                return new SceneSealOutcome(notes, temporaries);

            // The package Poser already owns for this actor is the source of
            // truth; only when the actor wears none does a new one get built.
            string? source = actor.Mcdf is { } existing &&
                !string.IsNullOrWhiteSpace(existing.Path) &&
                System.IO.File.Exists(existing.Path)
                ? existing.Path
                : null;
            string? created = null;

            if (source is null)
            {
                if (!identities.TryGetValue(actor.Key, out var id))
                {
                    notes.Add(
                        $"Actor '{actor.Name}' has no stable identity, so its " +
                        "appearance could not be packaged.");
                    continue;
                }
                // Only an owned actor's appearance is packaged: one Poser
                // spawned, or the player's own character. Anyone else's is
                // posed, never taken.
                // SealAppearance runs on the save worker. Ownership reads
                // the live object table, so resolve and check in one game-
                // thread dispatch; only the resulting value leaves it.
                var owned = await _framework.RunOnFrameworkThread(() =>
                    _bindings.Resolve(id) is { Success: true, Value: { } live }
                    && (_spawns.IsSpawnedActor(live)
                        || _actors.IsLocalPlayer(live)
                        || _actors.IsAdopted(live)));
                if (!owned)
                {
                    notes.Add(
                        $"Actor '{actor.Name}' is not yours, so its appearance " +
                        "was not packaged.");
                    continue;
                }
                created = SessionAppearanceFiles.NewTempPath();
                var (exported, delete) = await ExportAppearance(
                    id, actor.Name, created, bound, cancellation);
                if (exported != null)
                {
                    notes.Add($"Actor '{actor.Name}': {exported}");
                    if (delete)
                        DeleteQuietly(created);
                    continue;
                }
                source = created;
            }

            try
            {
                var info = new System.IO.FileInfo(source);
                if (!info.Exists)
                {
                    notes.Add(
                        $"Actor '{actor.Name}''s appearance package was gone " +
                        "before it could be read into the scene.");
                    if (created != null)
                        DeleteQuietly(created);
                    continue;
                }
                if (info.Length > SceneFileLimits.MaxEmbeddedAppearanceBytes)
                {
                    // The one remaining refusal, and it is the IMPORTER's own
                    // ceiling: a package Poser could not import back is a
                    // package there is no point saving.
                    notes.Add(
                        $"Actor '{actor.Name}''s appearance is " +
                        $"{Megabytes(info.Length)}, over the " +
                        $"{Megabytes(SceneFileLimits.MaxEmbeddedAppearanceBytes)} " +
                        "that Poser can import back; the scene saved without it.");
                    if (created != null)
                        DeleteQuietly(created);
                    continue;
                }

                // Hashed by STREAM, and the bytes stay on disk: the writer
                // copies them straight into the container entry, so a
                // half-gigabyte package never becomes a half-gigabyte array.
                string digest = HashFile(source)
                    ?? throw new System.IO.IOException(
                        "the package could not be checksummed.");
                total += info.Length;
                actor.Mcdf = new SceneActorMcdf
                {
                    Path = string.Empty,
                    FileName = actor.Mcdf?.FileName is { Length: > 0 } named
                        ? named
                        : $"{actor.Name}.mcdf",
                    ContentHash = digest,
                    PackageEntry = SceneFileStore.AppearanceEntry(digest),
                    PackageBytes = info.Length,
                    PackageSourcePath = source,
                };
                if (created != null)
                    temporaries.Add(created);
            }
            catch (Exception ex)
            {
                notes.Add(
                    $"Actor '{actor.Name}''s appearance package could not be " +
                    $"read into the scene: {ex.Message}");
                if (created != null)
                    DeleteQuietly(created);
            }
        }

        if (total > SceneFileLimits.LargeAppearanceWarningBytes)
        {
            notes.Add(
                $"This scene carries {Megabytes(total)} of appearance data. " +
                "It saved in full; expect it to take a while to move or share.");
        }

        return new SceneSealOutcome(notes, temporaries);
    }

    private static string Megabytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024d * 1024 * 1024):N1} GB"
            : $"{bytes / (1024d * 1024):N0} MB";

    /// <summary>
    /// Builds ONE new package from the actor's live supported state through
    /// the existing MCDF export transaction — the same admission, the same
    /// capability refusals, the same receipt. Returns null on success, else the
    /// refusal detail, which is already the exporter's own words about which
    /// provider was unavailable. <c>Delete</c> is false while the exporter may
    /// still own the destination file.
    /// </summary>
    /// <summary>
    /// Starts one character-file child once the single-flight MCDF slot is
    /// free. The slot can be held for seconds by this operation's own
    /// teardown (a clear-first load's reset releasing its package directory),
    /// so a busy slot is WAITED out within the bound; only a slot still held
    /// at the deadline is refused. <paramref name="begin"/> runs on the
    /// framework thread with the slot free and returns its own refusal.
    /// </summary>
    private async Task<string?> BeginMcdfChild(
        Func<string?> begin, string busy, string cancelled, TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + bound;
        while (true)
        {
            Task? holder = null;
            var refusal = await _framework.RunOnFrameworkThread(() =>
            {
                if (!_integration.McdfBusy)
                    return begin();
                holder = _integration.PendingCompletion;
                return null;
            });
            if (holder is null)
                return refusal;
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return busy;
            await Task.WhenAny(holder, Task.Delay(remaining, cancellation));
            if (cancellation.IsCancellationRequested)
                return cancelled;
        }
    }

    private async Task<(string? Refusal, bool Delete)> ExportAppearance(
        Poser.Domain.Identity.ActorId id,
        string name,
        string destination,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        Guid? operationId = null;
        var refusal = await BeginMcdfChild(() =>
        {
            var started = _integration.BeginExport(
                id, destination, $"Scene appearance: {name}");
            if (!started.Success)
                return started.Detail ?? "the appearance could not be packaged.";
            operationId = _integration.McdfReceipt?.OperationId;
            return null;
        }, "another character-file operation held the slot for the whole bound.",
            "the save was cancelled.", bound, cancellation);
        if (refusal != null)
            return (refusal, true);

        if (operationId is not { } admitted)
            return ("the appearance export did not publish its receipt.", true);

        // The export is this save's child: on the deadline or a cancelled
        // save it is cancelled by its own id and drained, never abandoned.
        var waited = await _integration.AwaitMcdf(admitted, bound, cancellation);
        if (waited.Applied)
            return (null, false);
        if (!waited.Terminal)
        {
            // Still writing past the drain bound: the destination stays the
            // child's until it stops, and is deleted then — never under it.
            DeleteWhenSettled(destination, _integration.McdfSettled(admitted));
            _log?.Warning(
                $"Scene save: the appearance export for '{name}' did not stop within " +
                "its drain bound; its partial file is deleted once it stops.");
            return ("the appearance export did not stop in time; its partial file is " +
                "deleted once it stops.", false);
        }
        return (waited.End switch
        {
            Poser.Application.Integration.McdfWaitEnd.ParentCancelled => "the save was cancelled.",
            Poser.Application.Integration.McdfWaitEnd.DeadlinePassed => "the appearance export did not finish within its bound.",
            _ => waited.Receipt?.Detail
                ?? $"the appearance export ended {waited.Receipt?.State.ToString() ?? "replaced"}.",
        }, true);
    }

    public long EstimateAppearanceBytes()
    {
        long total = 0;
        foreach (var actor in _actors.Actors)
        {
            if (_bindings.GetActorId(actor) is not { } id)
                continue;
            if (_integration.OverridesFor(id).Mcdf is not { } worn)
                continue;
            if (string.IsNullOrWhiteSpace(worn.SourcePath))
                continue;
            try
            {
                var info = new System.IO.FileInfo(worn.SourcePath);
                if (info.Exists)
                    total += info.Length;
            }
            catch (Exception)
            {
                // A package that cannot be stat'd contributes nothing to the
                // estimate; the save will name it if it also cannot read it.
            }
        }
        return total;
    }

    public void DeleteTemporary(string path) => DeleteQuietly(path);

    /// <summary>A temporary file a still-running MCDF child reads or writes:
    /// deleted once that child has stopped, whatever the outcome. The child
    /// is cancelled and joined by the transaction's own drain at unload, so
    /// this runs then at the latest.</summary>
    internal static void DeleteWhenSettled(string path, Task settled) =>
        _ = settled.ContinueWith(
            _ => DeleteQuietly(path),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void DeleteQuietly(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (Exception)
        {
            // A temporary export that outlives the save costs disk, not
            // correctness; the save must not fail on a cleanup.
        }
    }

    public string? ArmSceneCapture(
        Guid sceneId,
        string? description,
        Action<SceneCaptureOutcome> onCaptured) =>
        _capture.BeginCapture(sceneId, description, onCaptured);

    // ── session-wide load preamble ───────────────────────────────────────

    public System.Numerics.Vector3? CurrentOrigin() =>
        _objects.LocalPlayer?.Position;

    public string WorldObjectName(string path) => WorldObjects.WorldObjectService.DisplayName(path);

    /// <summary>
    /// The destroy-first clear. Actors go through the spawn service one at a
    /// time because only the spawned ones are this session's to destroy — the
    /// GPose target the user brought in is not — while props, overlays, lights
    /// and cameras each have a bulk verb that already applies the same
    /// ownership rule (a borrowed world light is released, the default camera
    /// cannot be destroyed), so their counts are read before the sweep.
    /// </summary>
    /// <summary>
    /// Empties the session of everything it can empty, and NAMES what it
    /// cannot.
    ///
    /// <para>EVERY kind comes out, actors included. An actor Poser spawned
    /// goes through its ownership ledger; one that was already in the GPose
    /// scene goes through the native scene-removal route, which deletes it
    /// from the temporary GPose object table and never touches the overworld
    /// actor. "Clear the session first" means the session, not the part of it
    /// Poser happens to own.</para>
    ///
    /// <para>Companion bodies are skipped: they leave with their owner. A
    /// removal the native gates refuse — a stale wrapper, the GPose primary,
    /// an actor no longer in the table — is named in the outcome. That is the
    /// exception path now, not the design.</para>
    /// </summary>
    public SceneClearOutcome ClearScene()
    {
        int actors = 0;
        var refused = new List<string>();
        var refusedCleanup = new List<string>();
        foreach (var actor in _actors.Actors.ToList())
        {
            // A companion body goes with its owner, and removing an owner
            // earlier in this sweep can retire later wrappers in the
            // snapshot: neither is a refusal worth naming.
            if (actor.ActorKind is ActorKind.Companion or ActorKind.Mount or ActorKind.Ornament
                || actor.Address == nint.Zero || !_actors.Actors.Contains(actor))
                continue;
            // Gaze and appearance are released BEFORE the delete, while the
            // actor still exists to release them against; Brio does the same
            // in CleanObject (Brio/Game/Actor/ActorSpawnService.cs:245-256)
            // and for the same reason — after the delete there is nothing left
            // to name.
            var lineage = _bindings.GetActorId(actor)?.LogicalId;
            ActorRemovalCleanup.Prepare(actor, _gaze, _integration, _bindings, refusedCleanup.Add);

            // One verb for both provenances: the service routes an owned
            // actor to its ledger and an adopted one to the scene table.
            if (_spawns.RemoveActorFromScene(actor))
            {
                actors++;
                // Deselect the moment it is gone, per actor, rather than only
                // at the end: a removal that succeeds for some actors and is
                // refused for others must not leave the successful ones
                // selected.
                if (lineage is { } gone)
                    _selection.RemoveActorLineage(gone);
            }
            else
            {
                // The row says why, in the gate's own words, not just who.
                refused.Add(_spawns.RemovalRefusal(actor) is { } why
                    ? $"{actor.Name}: {why}"
                    : actor.Name);
            }
        }

        int props = _props.Props.Count;
        _props.DestroyAll();

        int overlays = _overlays.Nodes.Count;
        _overlays.DestroyAll();

        int lights = _lighting.Lights.Count(_lighting.IsSpawnedLight);
        _lighting.DestroyAllLights();

        int cameras = _cameras.Cameras.Count(camera => !camera.IsDefault);
        _cameras.DestroyAllCameras();

        // Not a destruction: releasing writes each borrowed object's captured
        // placement and flags back to the map. Clearing the scene is one of the
        // four exits the restore contract names, and this is where it runs.
        int worldObjects = _worldObjects.Count;
        _worldObjects.ReleaseAll();

        // Everything this session pointed at is gone, so the selection is
        // gone with it. The per-actor deselect above covers a partial clear;
        // this covers the props, overlays, lights, cameras and borrowed
        // objects that have no lineage of their own.
        _selection.Clear();

        foreach (var note in refusedCleanup)
            refused.Add(note);

        return new SceneClearOutcome(
            actors, props, overlays, lights, cameras, worldObjects, refused);
    }


    public string? LoadPreflight(int actors) =>
        actors > 0 && _objects.LocalPlayer is null
            ? "There is no local player to spawn the scene's actors from."
            : null;

    // ── actors ───────────────────────────────────────────────────────────

    public SceneEntityHandle? SpawnActor(SceneActor data, out string? detail)
    {
        // Set the model inside the spawn's deferred-draw window. A second
        // SetModelCharaId redraw races the next-tick collection assignment.
        // A saved catalog kind respawns through the catalog, as history's
        // recreate does, so a minion comes back as a minion.
        string? refusal = null;
        var actor = data.SpawnedKind is { } kind
            ? _spawns.SpawnCatalogActor(new(kind, 0, "", "", 0, data.ModelCharaId))
            : _spawns.SpawnNewActor(data.HasCompanionSlot, data.ModelCharaId, out refusal);
        if (actor is null)
        {
            detail = refusal ?? "The spawn service returned no actor.";
            return null;
        }
        detail = null;
        return _handles.Track(SceneEntityKind.Actor, actor);
    }

    /// <summary>
    /// Whether this actor can be POSED yet — which is a stricter question than
    /// whether it exists.
    ///
    /// <para>Three things have to be true, and they land at different times.
    /// The slot skeletons have to be built. The actor's own binding has to
    /// name this exact live generation. And — the one that bit — the BONE
    /// bindings have to have been republished for these skeleton instances.
    /// </para>
    ///
    /// <para>Bone ids are published by the binding registry's staged
    /// candidate/commit pass, not by the skeleton service, so after a redraw
    /// the skeleton service hands out NEW bone objects while the registry
    /// still holds the pre-redraw ones. <c>GetBoneId</c> requires the id to
    /// bind to the very same instance (<c>ReferenceEquals</c>), so every bone
    /// of a freshly rebuilt skeleton resolves to null until that pass runs.
    /// The pose import resolves its targets up front and fails on the FIRST
    /// one, which is why a clear-first load of a scene carrying appearance
    /// reported "Import target n_root could not be resolved" — the MCDF redraw
    /// had replaced the skeleton and the barrier had already let the load
    /// through.</para>
    ///
    /// <para>Probing the root bone through the registry is the whole test: if
    /// the maps resolve THAT instance they were rebuilt for this skeleton, and
    /// every other bone of it resolves too. The barrier polls, so a skeleton
    /// mid-publication is WAITED for; only a skeleton that never publishes
    /// inside the bound is refused.</para>
    /// </summary>
    /// <summary>
    /// A stable short ordinal for an object INSTANCE, for breadcrumbs. Two
    /// lines quoting different ordinals for "the same" skeleton is the whole
    /// diagnosis of a rebind race, and it fits on one screen.
    /// </summary>
    private static string Ord(object? instance) =>
        instance is null
            ? "none"
            : System.Runtime.CompilerServices.RuntimeHelpers
                .GetHashCode(instance).ToString("X8");

    /// <summary>
    /// One breadcrumb for the scene pose leg. Debug level: it must be there
    /// when a load misbehaves and invisible in ordinary play.
    /// </summary>
    private void Trace(string message) =>
        _log?.Debug($"Scene pose leg: {message}");

    public bool ActorReady(SceneEntityHandle actor) =>
        Posable(_handles.Require<IActor>(actor, SceneEntityKind.Actor));

    /// <summary>The three-part test above, for an actor or a companion body:
    /// a companion's skeleton races its bone bindings exactly as an actor's
    /// does after a redraw.</summary>
    private bool Posable(IActor candidate)
    {
        var skeletons = _skeletons.GetSkeletons(candidate);
        if (!ActorPoseReadiness.IsReady(skeletons, _bindings) ||
            _bindings.GetActorId(candidate) is not { } id)
            return false;
        if (_bindings.Resolve(id) is not { Success: true, Value: { } bound } ||
            !ReferenceEquals(bound, candidate))
            return false;

        Trace(
            $"ready: actor {candidate.Name} wrapper {Ord(candidate)} " +
            string.Join(", ", skeletons.Select(skeleton =>
                $"[{skeleton.Slot} skeleton {Ord(skeleton)} " +
                $"base {skeleton.CharacterBaseAddress:X} " +
                $"root {Ord(skeleton.RootBone)} bones {skeleton.Bones.Count}]")));
        return true;
    }

    public async Task<string?> RestoreCollection(SceneEntityHandle actor, SceneActor data, TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        if (data.PenumbraCollection is not { } collection || data.Mcdf is not null)
            return null;
        var target = await OnFramework(() => _bindings.GetActorId(_handles.Require<IActor>(actor, SceneEntityKind.Actor)));
        if (target is not { } id) return "The actor is no longer bound.";
        // Already wearing it (an older file that recorded the player's
        // collection a spawn inherits): no assignment, and no redraw.
        if (await OnFramework(() => _integration.ReadCollection(id)) is
            { Success: true, Value: { HasIndividualAssignment: true } current }
            && current.EffectiveId == collection)
            return null;
        var available = await OnFramework(() => _integration.ListCollections());
        var name = collection == Guid.Empty ? "None"
            : available.Value?.FirstOrDefault(x => x.Id == collection)?.Name;
        if (name is null) return "The saved Penumbra collection is not available on this machine.";
        var result = await _integration.SetCollectionAndWait(id, collection, name, bound, cancellation);
        return result.Success ? null : result.Detail;
    }

    /// <summary>
    /// Re-imports the saved character file through <c>McdfTransaction</c> —
    /// the ONE import path. Nothing here reimplements a phase: the file is
    /// checked, the existing transaction is started, and this waits for the
    /// receipt that transaction publishes. That is what keeps the ownership it
    /// registers, and therefore the by-name unlock-and-restore teardown, the
    /// same for a scene-restored actor as for a hand-imported one.
    ///
    /// <para>A PORTABLE entry carries the package itself, and is staged into
    /// one owned temporary file the import runs from — the transaction takes a
    /// path, and inventing a second import route for embedded bytes would mean
    /// a second set of phases, a second rollback and a second ownership
    /// ledger. Successful imports retain that file for history until the session
    /// ends; failed imports delete it immediately. The bytes are hashed while
    /// they are staged and refused unless they match the recorded digest —
    /// the digest names the entry, so it is the payload's identity.</para>
    ///
    /// <para>A REFERENCE entry is resolved by CONTENT first. The scene records
    /// the package's SHA-256, so the user's MCDF library is searched for those
    /// exact bytes before the recorded path is tried — a package that was
    /// renamed, filed into a subfolder or re-downloaded elsewhere is still the
    /// package this scene was saved against, and only its checksum can say so.
    /// The recorded path is the fallback, not the identity. When neither
    /// answers, the refusal states BOTH things that were tried.</para>
    /// </summary>
    public async Task<SceneMcdfOutcome> ImportMcdf(
        string scenePath,
        SceneEntityHandle actor,
        SceneActor data,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        if (data.Mcdf is not { } saved)
            return SceneMcdfOutcome.Silent;

        string? staged = null;
        var historySession = ActiveSession;
        try
        {
            // File work first, off the framework thread: a missing package is a
            // refusal that never touches the actor, and a changed one is named
            // before anything is applied.
            string? changed = null;
            string source;
            if (saved.IsPortable)
            {
                staged = SessionAppearanceFiles.NewTempPath();
                try
                {
                    // Container entry to disk, as a STREAM. A real package is
                    // hundreds of megabytes; nothing here holds it. Hashed
                    // on the way through: the digest the document carries is
                    // what makes embedded bytes trustworthy, so bytes that do
                    // not match it are refused rather than worn.
                    var opened = _documents.OpenAppearance(scenePath, saved.PackageEntry!);
                    using var payload = opened.Stream
                        ?? throw new System.IO.IOException(opened.Error ?? "the payload could not be opened.");
                    using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
                        System.Security.Cryptography.HashAlgorithmName.SHA256);
                    using (var staging = System.IO.File.Create(staged))
                    {
                        var buffer = new byte[81920];
                        int read;
                        while ((read = await payload.ReadAsync(buffer, cancellation)) > 0)
                        {
                            hash.AppendData(buffer, 0, read);
                            await staging.WriteAsync(buffer.AsMemory(0, read), cancellation);
                        }
                    }
                    if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()),
                            saved.ContentHash, StringComparison.OrdinalIgnoreCase))
                        return SceneMcdfOutcome.Refused(
                            $"The appearance package '{saved.FileName}' does not match the " +
                            "digest the scene recorded for it, so it was not imported.");
                }
                catch (Exception ex)
                {
                    return SceneMcdfOutcome.Refused(
                        $"The appearance package '{saved.FileName}' could not be " +
                        $"staged for import: {ex.Message}");
                }
                source = staged;
            }
            else
            {
                // BY CONTENT first; the decision itself lives in
                // SceneAppearanceSource so the order can be stated and tested
                // without a live client.
                var resolved = SceneAppearanceSource.Resolve(
                    saved, _mcdfHashes, System.IO.File.Exists, cancellation);
                if (resolved.Origin == SceneAppearanceOrigin.None ||
                    resolved.Path is not { } found)
                    return SceneMcdfOutcome.Refused(
                        resolved.Detail
                        ?? $"The character file '{saved.FileName}' could not be found.");

                changed = resolved.Detail;
                if (resolved.Origin == SceneAppearanceOrigin.RecordedPath &&
                    saved.ContentHash.Length > 0)
                {
                    // The library had no match and this file is still here, so
                    // its bytes cannot be the saved ones — but say WHY rather
                    // than inferring it, since the digest may simply have been
                    // unreadable when the scene was saved.
                    var hash = HashFile(found);
                    changed = hash is null
                        ? $"The character file '{saved.FileName}' could not be " +
                            "read to check it against the scene."
                        : string.Equals(
                            hash, saved.ContentHash, StringComparison.OrdinalIgnoreCase)
                            ? null
                            : $"The character file '{saved.FileName}' has changed " +
                                "since this scene was saved; the actor is wearing " +
                                "the file as it is now.";
                }
                source = found;
            }

            Guid? operationId = null;
            var refusal = await BeginMcdfChild(() =>
            {
                var target = _handles.Resolve<IActor>(actor, SceneEntityKind.Actor);
                if (target == null) return "The actor is no longer available.";
                if (_bindings.GetActorId(target) is not { } id)
                    return "The actor has no stable identity to import a character file onto.";
                var started = _integration.BeginImport(id, source);
                if (!started.Success)
                    return started.Detail ?? "The character file import was refused.";
                // The transaction publishes a Pending receipt inside admission, so
                // the id of THIS operation is readable the moment it is admitted.
                operationId = _integration.McdfReceipt?.OperationId;
                return null;
            }, "Another character-file operation held the slot for the whole bound.",
                "The load was cancelled.", bound, cancellation);
            if (refusal != null)
                return SceneMcdfOutcome.Refused(refusal);

            if (operationId is not { } admitted)
                return SceneMcdfOutcome.Refused(
                    "The character file import did not publish its receipt.");

            // The import is this load's child. On the deadline or a cancelled
            // load it is cancelled by its own id and drained: once cancelled,
            // every later phase refuses before mutating, so a late completion
            // rolls back instead of changing the actor.
            var waited = await _integration.AwaitMcdf(admitted, bound, cancellation);
            if (waited.Applied)
            {
                // Committed (possibly just before a matched cancel): owned by
                // the transaction, and history may re-import the staged package.
                if (staged is not null &&
                    historySession is { } session && ActiveSession == session &&
                    _historyAppearanceFiles.Retain(staged, session))
                    staged = null;
                return SceneMcdfOutcome.Ok(changed);
            }
            if (!waited.Terminal)
            {
                // Cancelled but still running past the drain bound: it may still
                // read the staged package, so it is deleted when the child
                // stops — not here, and not by a session sweep that does not
                // know the child is alive.
                if (staged is not null)
                {
                    DeleteWhenSettled(staged, _integration.McdfSettled(admitted));
                    staged = null;
                }
                _log?.Warning(
                    $"Scene load: the import of '{saved.FileName}' was cancelled but had not " +
                    "stopped within its drain bound; its staged package is deleted once it stops.");
                return SceneMcdfOutcome.Refused(
                    $"The character file '{saved.FileName}' was cancelled and is still " +
                    "stopping; it cannot apply.");
            }
            return SceneMcdfOutcome.Refused(waited.End switch
            {
                Poser.Application.Integration.McdfWaitEnd.ParentCancelled => "The load was cancelled.",
                Poser.Application.Integration.McdfWaitEnd.DeadlinePassed =>
                    $"The character file '{saved.FileName}' did not finish " +
                    "importing within its bound.",
                _ => waited.Receipt?.Detail
                    ?? $"The character file import ended {waited.Receipt?.State.ToString() ?? "replaced"}.",
            });
        }
        finally
        {
            if (staged != null)
                DeleteQuietly(staged);
        }
    }

    // Only called for an actor whose attachment is present: the workflow skips
    // an absent kind rather than asking the runtime to detach.
    public string? AttachCompanion(SceneEntityHandle actor, SceneActor data) =>
        _spawns.SetCompanion(
            _handles.Require<IActor>(actor, SceneEntityKind.Actor),
            new CompanionAttachment(data.CompanionKind!.Value, data.CompanionId))
            ? null
            : "The companion could not be attached.";

    /// <summary>Every component and every slot: an embedded scene pose is a
    /// complete captured state, not an interactive rotation-only import.
    /// Placement is absolute and separate (<see cref="PlaceActor"/>), so the
    /// difference-based model transform stays off.</summary>
    internal static readonly PoseImportOptions SceneImportOptions = new()
    {
        ApplyRotation = true,
        ApplyPosition = true,
        ApplyScale = true,
        ApplyModelTransform = false,
        // The whole load owns history; its internal actor/companion imports
        // must neither append extra steps nor resume the frozen scene pose.
        SuppressHistory = true,
        FreezeOnImport = true,
    };

    public string? ArmPoseImport(
        SceneEntityHandle actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt)
    {
        // The arm's own view of the world, quoted the same way readiness
        // quotes it. If these ordinals differ from the ready line, the plan is
        // being built against a skeleton the registry never bound — which is
        // exactly the shape that reports "Import target n_root could not be
        // resolved" for every bone at once.
        var target = _handles.Require<IActor>(actor, SceneEntityKind.Actor);
        var skeletons = _skeletons.GetSkeletons(target);
        int resolvable = 0;
        int total = 0;
        foreach (var skeleton in skeletons)
        {
            foreach (var bone in skeleton.Bones)
            {
                total++;
                if (_bindings.GetBoneId(bone) is not null)
                    resolvable++;
            }
        }
        Trace(
            $"arming import for actor {target.Name} wrapper {Ord(target)}: " +
            string.Join(", ", skeletons.Select(skeleton =>
                $"[{skeleton.Slot} skeleton {Ord(skeleton)} " +
                $"base {skeleton.CharacterBaseAddress:X} " +
                $"root {Ord(skeleton.RootBone)}]")) +
            $" — {resolvable} of {total} bones resolve through the registry");

        if (_bindings.GetActorId(target) is not { } actorId)
            return "The actor is no longer available.";
        var result = _imports.AdmitHeld(this, () => _poses.ImportPose(
            actorId, data.Pose!, SceneImportOptions, description, onReceipt));
        if (!result.Success)
            Trace($"import refused for {target.Name}: {result.Detail}");
        return result.Success ? null : result.Detail ?? "The pose import refused.";
    }

    public bool CompanionReady(SceneEntityHandle actor) =>
        _spawns.GetCompanionActor(_handles.Require<IActor>(actor, SceneEntityKind.Actor)) is { } companion &&
        Posable(companion);

    public bool PoseImportBusy => _imports.IsSlotBusy;

    public void HoldPoseImports(bool held)
    {
        if (held)
            _imports.Hold(this);
        else
            _imports.Release(this);
    }

    public void CancelPoseImport(Guid operationId) =>
        _imports.Cancel(operationId, "The scene load stopped waiting for this pose import.");

    public string? ArmCompanionPoseImport(
        SceneEntityHandle actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt)
    {
        if (_spawns.GetCompanionActor(_handles.Require<IActor>(actor, SceneEntityKind.Actor)) is not { } companion)
            return "The companion's body could not be resolved, so its pose was not restored.";
        if (!Posable(companion))
            return "The companion's skeleton had not built, so its pose was not restored.";
        if (_bindings.GetActorId(companion) is not { } companionId)
            return "The companion is no longer available.";
        var result = _imports.AdmitHeld(this, () => _poses.ImportPose(
            companionId, data.CompanionPose!, SceneImportOptions, description, onReceipt));
        return result.Success
            ? null
            : result.Detail ?? "The companion pose import refused.";
    }

    public string? RestoreActorName(SceneEntityHandle actor, SceneActor data)
    {
        var target = _handles.Require<IActor>(actor, SceneEntityKind.Actor);
        if (_bindings.GetActorId(target) is not { } id)
            return "The actor is no longer bound.";
        // Native names remain untouched: appearance providers identify by them.
        _configuration.SetNickname(id.LogicalId, SceneActorNames.Resolve(data));
        return null;
    }

    public string? PlaceActor(SceneEntityHandle actor, SceneActor data) =>
        PlaceModel(_handles.Require<IActor>(actor, SceneEntityKind.Actor), data.ModelTransform, data.Pose);

    public string? PlaceCompanion(SceneEntityHandle actor, SceneActor data) =>
        _spawns.GetCompanionActor(_handles.Require<IActor>(actor, SceneEntityKind.Actor)) is { } companion
            ? PlaceModel(companion, null, data.CompanionPose)
            : "The companion's body could not be resolved, so its placement was not restored.";

    private string? PlaceModel(IActor target, LightFile.TransformData? model, PoseFile? pose)
    {
        // The scene's OWN placement first. The embedded pose's absolute values
        // remain the fallback for files written before placements were stated,
        // and only there does the codec's unset marker (BoneData.Identity —
        // zero position, identity rotation, ZERO scale) have to be guessed at.
        System.Numerics.Vector3 position;
        System.Numerics.Quaternion rotation;
        System.Numerics.Vector3 scale;
        if (model is { } stated)
        {
            position = stated.Position;
            rotation = stated.Rotation;
            scale = stated.Scale;
        }
        else
        {
            if (pose is null)
                return null;
            var absolute = pose.ModelAbsoluteValues;
            bool unset = absolute.Position == System.Numerics.Vector3.Zero &&
                absolute.Rotation == System.Numerics.Quaternion.Identity &&
                absolute.Scale == System.Numerics.Vector3.Zero;
            if (unset)
                return null;
            position = absolute.Position;
            rotation = absolute.Rotation;
            scale = absolute.Scale;
        }

        if (rotation.LengthSquared() < SceneFileLimits.MinQuaternionLengthSquared)
            return "The saved actor placement carries a degenerate rotation.";

        var placement = new Transform(
            position,
            System.Numerics.Quaternion.Normalize(rotation),
            scale == System.Numerics.Vector3.Zero
                ? System.Numerics.Vector3.One
                : scale);
        _posing.SetTransformOverride(target, placement);

        // The override setter REFUSES silently — outside GPose, on an actor
        // the live set does not yet carry, on a value it cannot sanitize —
        // and a scene reporting a placement it never made is exactly the
        // failure the user sees as "it did not restore where they stood".
        // Ask whether it landed.
        if (_posing.GetTransformOverride(target) is null)
            return "The actor's placement was refused by the transform owner.";
        return null;
    }

    /// <summary>
    /// FREEZES the actor. A scene restores a picture, not a performance: it
    /// carries pose data, which is self-contained, and deliberately carries no
    /// animation at all — a timeline id resolves against the LOADING client's
    /// game and mod list, so the same scene file would play something
    /// different on someone else's machine, or nothing.
    ///
    /// <para>So every restored actor is stopped at speed 0 and the pose lands
    /// on a held frame. That is the definition of a successful load: the same
    /// picture every time, on every client. Expressions come back as part of
    /// the pose, on the frozen face.</para>
    /// </summary>
    public string? FreezeActor(SceneEntityHandle actor)
    {
        if (_bindings.GetActorId(_handles.Require<IActor>(actor, SceneEntityKind.Actor)) is not { } id)
            return "The actor has no stable identity to freeze.";
        var paused = _animation.Pause(id);
        return paused.Success
            ? null
            : paused.Detail ?? "The actor could not be frozen for its pose.";
    }

    /// <summary>
    /// Restores the saved gaze in the order the service's own transitions
    /// require: the mode first (entering a mode with no parts enables all
    /// three), then the exact participation mask, then the anchor and each
    /// part's own point, then the locks — a lock freezes a part at the target
    /// it currently holds, so it must land after that target is written.
    /// </summary>
    public string? ApplyActorGaze(SceneEntityHandle actor, SceneActor data, SceneEntityHandle? target)
    {
        if (data.Gaze is not { } saved || (saved.Mode == GazeTargetMode.None && !saved.PoseAware))
            return null;
        if (!_gaze.IsAvailable)
            return _gaze.UnavailableDetail ?? "Gaze control is unavailable.";

        var source = _handles.Require<IActor>(actor, SceneEntityKind.Actor);

        // Entity mode IS its target: SetGazeTarget both chooses the actor and
        // enters the mode. A saved Entity gaze whose target the file does not
        // name has nothing to follow, and is refused by name rather than left
        // pointing at whatever the mode transition would pick.
        if (saved.Mode == GazeTargetMode.Entity)
        {
            if (_handles.Resolve<IActor>(target, SceneEntityKind.Actor) is not { } followed)
                return "The saved gaze followed an actor the scene does not carry.";
            var chosen = _gaze.SetGazeTarget(source, followed);
            if (!chosen.Success)
                return chosen.Detail ?? "The gaze target was refused.";
        }
        else
        {
            var mode = _gaze.SetGazeMode(source, saved.Mode);
            if (!mode.Success)
                return mode.Detail ?? "The gaze mode was refused.";
        }

        var parts = _gaze.SetGazeParts(source, saved.Parts);
        if (!parts.Success)
            return parts.Detail ?? "The gaze parts were refused.";
        var aware = _gaze.SetPoseAware(source, saved.PoseAware);
        if (saved.PoseAware && !aware.Success)
            return aware.Detail ?? "Pose-aware gaze was refused.";

        if (saved.Mode == GazeTargetMode.Position)
        {
            _gaze.SetGazePosition(source, saved.Position);
            _gaze.SetPartPosition(source, GazeTargetType.Eyes, saved.EyesPosition);
            _gaze.SetPartPosition(source, GazeTargetType.Head, saved.HeadPosition);
            _gaze.SetPartPosition(source, GazeTargetType.Body, saved.BodyPosition);
        }

        foreach (var part in new[]
                 {
                     GazeTargetType.Body, GazeTargetType.Head, GazeTargetType.Eyes,
                 })
        {
            if (saved.LockedParts.HasFlag(part))
                _gaze.SetPartLock(source, part, true);
        }
        return null;
    }

    public void SetActorVisibility(SceneEntityHandle actor, bool visible) =>
        _spawns.SetVisibility(_handles.Require<IActor>(actor, SceneEntityKind.Actor), visible);

    // ── props ────────────────────────────────────────────────────────────

    public SceneEntityHandle? SpawnOverlay(SceneOverlay data, out string? detail)
    {
        if (data.Node is not { } document)
        {
            detail = "The overlay entry carries no node document.";
            return null;
        }
        detail = null;
        var display = Dalamud.Bindings.ImGui.ImGui.GetIO().DisplaySize;
        if (document.Collider == null && data.CenterRelative && display.X > 0f && display.Y > 0f)
        {
            document = document with
            {
                Position = document.Position + new System.Numerics.Vector2(
                    display.X / 2f, display.Y / 2f),
            };
        }
        // An overlay entirely outside the screen exists but shows nothing —
        // the restore says so instead of leaving the user hunting for it.
        var size = Poser.Domain.Presentation.OverlayNodeGeometry
            .DesignSize(document.Kind) * document.Scale;
        if (document.Collider == null && display.X > 0f && display.Y > 0f &&
            (document.Position.X + size.X < 0f ||
             document.Position.Y + size.Y < 0f ||
             document.Position.X > display.X ||
             document.Position.Y > display.Y))
        {
            detail = $"'{document.Name}' sits entirely off screen at this " +
                "resolution, so it will not be visible.";
        }
        var handle = _overlays.Create(document);
        if (handle is null)
        {
            detail = "The overlay node could not be staged.";
            return null;
        }
        return _handles.Track(SceneEntityKind.Overlay, handle);
    }

    /// <summary>
    /// Restores one world-object entry by SPAWNING its path anew — any
    /// zone, any position. A document never carries a borrow (ruled
    /// 2026-09-01): borrowing is a live-session act, and the load owes
    /// nothing to whatever the map may or may not be standing.
    /// </summary>
    public SceneEntityHandle? AdoptWorldObject(SceneWorldObject data, out string? detail)
    {
        var handle = _worldObjects.Spawn(
            data.Path, data.Transform, data.Visible, out detail);
        if (handle != null && data.Name.Length > 0)
            handle.Name = data.Name;
        if (handle != null)
        {
            if (data.Opacity < 1f)
                handle.Opacity = data.Opacity;
            if (data.Tint is { } tint)
                handle.Tint = tint;
            handle.Stain = data.Stain;
            handle.FurnitureLights = data.FurnitureLights;
            handle.LoopVfx = data.VfxLoop;
            if (Math.Abs(data.VfxSpeed - 1f) > 0.001f)
                handle.VfxSpeed = data.VfxSpeed;
            if (Math.Abs(data.VfxIntensity - 1f) > 0.001f)
                handle.VfxIntensity = data.VfxIntensity;
            if (data.VfxPaused)
                handle.VfxPaused = true;
            handle.NightState = data.NightState;
            if (data.AnimPaused)
                handle.AnimationPaused = true;
        }
        return handle == null ? null : _handles.Track(SceneEntityKind.WorldObject, handle);
    }

    public async Task<IReadOnlyList<SceneEntityHandle>> AwaitWorldObjectsLoaded(
        IReadOnlyList<SceneEntityHandle> worldObjects, TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        var deadline = DateTime.UtcNow + bound;
        while (true)
        {
            bool expired = DateTime.UtcNow >= deadline;
            var loading = await OnFramework(() =>
            {
                var pending = new List<SceneEntityHandle>();
                foreach (var token in worldObjects)
                {
                    if (_handles.Resolve<IWorldObject>(token, SceneEntityKind.WorldObject)
                            is not WorldObjects.AdoptedWorldObject handle
                        || !_worldObjects.IsLoading(handle))
                        continue;
                    pending.Add(token);
                    // Same framework action as the answer: no timed release
                    // can land between the load naming it and keeping it.
                    if (expired)
                        _worldObjects.KeepUnloaded(handle);
                }
                return pending;
            });
            if (loading.Count == 0 || expired)
                return loading;
            await Task.Delay(50, cancellation);
        }
    }

    public void ReleaseWorldObject(SceneEntityHandle token) =>
        _handles.Remove<IWorldObject>(token, SceneEntityKind.WorldObject, _worldHistory.Resolve, entity =>
        {
            var world = (WorldObjects.AdoptedWorldObject)entity;
            if (!_worldObjects.Release(world) && _worldObjects.Adopted.Contains(world))
                throw new InvalidOperationException("The scene world object could not be released.");
        });

    public SceneEntityHandle? SpawnProp(SceneProp data, out string? detail)
    {
        var handle = _props.SpawnProp(new PropModel(
            data.Name, data.Model, data.Submodel, data.Variant, string.Empty,
            data.Stain0, data.Stain1, data.AnimationVariant));
        if (handle is null)
        {
            detail = "The object spawn failed.";
            return null;
        }
        handle.Transform = data.Transform;
        handle.Visible = data.Visible;
        detail = null;
        return _handles.Track(SceneEntityKind.Prop, handle);
    }

    // ── lights ───────────────────────────────────────────────────────────

    public SceneEntityHandle? SpawnLight(
        SceneLight data, SceneEntityHandle? attachmentOwner, out string? detail)
    {
        var document = data.Light!;

        // Resolve the exact attachment bone BEFORE any native spawn — a
        // light whose stated parent bone is missing is refused whole, never
        // spawned detached into world space.
        IBone? bone = null;
        if (data.Attachment is { } attachment)
        {
            if (_handles.Resolve<IActor>(attachmentOwner, SceneEntityKind.Actor) is not { } owner)
            {
                detail = "The attachment owner was not restored.";
                return null;
            }
            var skeleton = _skeletons.GetSkeletons(owner)
                .FirstOrDefault(candidate => candidate.Slot == attachment.Slot);
            bone = skeleton?.Bones.FirstOrDefault(candidate =>
                candidate.PartialId == attachment.PartialId &&
                string.Equals(
                    candidate.BoneName, attachment.BoneName,
                    StringComparison.Ordinal));
            if (bone is null)
            {
                detail = $"The attachment bone '{attachment.BoneName}' " +
                    $"({attachment.Slot}/{attachment.PartialId}) does not exist " +
                    "on the restored actor.";
                return null;
            }
        }

        var light = _lighting.SpawnLight(document.Kind);
        if (light is null)
        {
            detail = "The light spawn failed.";
            return null;
        }

        Lighting.LightDocument.Apply(document, light);
        if (bone is not null)
            light.AttachedBone = bone;

        // A gobo the running client no longer ships degrades with a named
        // detail; the light itself is restored.
        detail = null;
        if (!string.IsNullOrEmpty(document.Gobo))
        {
            var gobo = _lighting.Gobos.FirstOrDefault(candidate =>
                string.Equals(
                    candidate.Path, document.Gobo,
                    StringComparison.OrdinalIgnoreCase));
            if (gobo == default)
                detail = $"The saved gobo '{document.Gobo}' is not in the library.";
            else if (!_lighting.ApplyGobo(light, gobo))
                detail = $"The gobo '{gobo.Name}' could not be applied.";
        }
        return _handles.Track(SceneEntityKind.Light, light);
    }

    // ── cameras ──────────────────────────────────────────────────────────

    private IVirtualCamera? DefaultCamera =>
        _cameras.Cameras.FirstOrDefault(camera => camera.IsDefault);

    public SceneEntityHandle? DefaultCameraToken() => DefaultCamera is { } camera
        ? _handles.Track(SceneEntityKind.Camera, camera) : null;

    /// <summary>The default camera's document AND what the load can change
    /// beside it: the followed actor and which camera is live. A document
    /// alone left the default camera following a rolled-back actor.</summary>
    public SceneCameraBaseline CaptureDefaultCameraState()
    {
        var camera = DefaultCamera;
        var target = camera?.TargetActor is { } followed
            ? _handles.Track(SceneEntityKind.Actor, followed) : null;
        var live = _cameras.LiveCamera is { } current
            ? _handles.Track(SceneEntityKind.Camera, current) : null;
        return new(
            camera is null ? new CameraFile() : Cameras.CameraDocument.Capture(camera),
            target, camera?.TargetActorName ?? string.Empty,
            camera?.IsTargetLocked ?? false, live);
    }

    public string? ApplyDefaultCamera(SceneCamera data)
    {
        if (DefaultCamera is not { } camera)
            return "The session has no default camera.";
        Cameras.CameraDocument.Apply(data.Camera!, camera);
        return null;
    }

    public SceneEntityHandle? CreateCamera(SceneCamera data, out string? detail)
    {
        // SceneWorkflow activates the saved view after every camera and target is restored.
        var camera = _cameras.CreateCamera(data.Camera!.Kind, makeLive: false);
        if (camera is null)
        {
            detail = "The camera could not be created.";
            return null;
        }
        Cameras.CameraDocument.Apply(data.Camera!, camera);
        detail = null;
        return _handles.Track(SceneEntityKind.Camera, camera);
    }

    public string? SetCameraTarget(
        SceneEntityHandle? camera, SceneEntityHandle targetActor, string displayName,
        bool targetLocked)
    {
        var target = camera == null ? DefaultCamera : _handles.Resolve<IVirtualCamera>(camera, SceneEntityKind.Camera);
        if (target is null)
            return "The session has no default camera.";
        var exactActor = _handles.Require<IActor>(targetActor, SceneEntityKind.Actor);
        // Validate the exact generation before SetTargetActor can touch any
        // native target state; a replacement occupant is never rebound.
        if (_bindings.GetActorId(exactActor) is not { } targetId ||
            _bindings.Resolve(targetId) is not
                { Success: true, Value: { } resolved } ||
            !ReferenceEquals(resolved, exactActor))
            return "The target actor is no longer available.";
        if (!_cameras.SetTargetActor(target, exactActor, targetId, displayName))
            return "The target actor has no draw object.";
        target.IsTargetLocked = targetLocked;
        return null;
    }

    public string? SetLiveCamera(SceneEntityHandle? camera)
    {
        var target = camera == null ? DefaultCamera : _handles.Resolve<IVirtualCamera>(camera, SceneEntityKind.Camera);
        if (target is null)
            return "The session has no default camera.";
        _cameras.SetLive(target);
        return null;
    }

    public void RestoreDefaultCamera(SceneCameraBaseline baseline)
    {
        if (DefaultCamera is not { } camera)
            return;
        Cameras.CameraDocument.Apply(baseline.Camera, camera);
        // The pre-load target only if it is still the same live actor; the
        // load's own target never survives its rollback.
        if (_handles.Resolve<IActor>(baseline.Target, SceneEntityKind.Actor) is { } actor
            && _bindings.GetActorId(actor) is { } id
            && _cameras.SetTargetActor(camera, actor, id, baseline.TargetName))
            camera.IsTargetLocked = baseline.TargetLocked;
        else
            _cameras.ClearTargetActor(camera);
        _cameras.SetLive(
            _handles.Resolve<IVirtualCamera>(baseline.Live, SceneEntityKind.Camera) is { IsValid: true } live
                ? live : camera);
    }

    // ── environment ──────────────────────────────────────────────────────

    public SceneEnvironment CaptureEnvironmentState() =>
        _capture.CaptureEnvironment();

    public SceneWorld CaptureWorldState() => _capture.CaptureWorld();

    /// <summary>
    /// Stamps the session-wide toggles. Both are patches whose enabled state
    /// is their whole state, so a scene that asks for neither RELEASES them —
    /// loading a scene taken with running water into a session that froze it
    /// must give the water back, or the scene did not restore what it saved.
    /// A toggle the running client cannot reach is a named degradation, never
    /// a silent no-op.
    /// </summary>
    public string? ApplyWorld(SceneWorld world)
    {
        var failures = new List<string>();
        if (world.IsWaterFrozen && !_rendering.IsWaterFreezeAvailable)
            failures.Add(
                "the water freeze could not be hooked on this client, so the " +
                "surface is still moving");
        else
            _rendering.IsWaterFrozen = world.IsWaterFrozen;

        var physics = _animation.SetScenePhysicsFrozen(world.IsPhysicsFrozen);
        if (!physics.Success)
            failures.Add(physics.Detail ?? "the physics freeze was refused");

        return failures.Count == 0
            ? null
            : "The scene was restored except that " + string.Join("; ", failures) + ".";
    }

    public void ApplyEnvironment(SceneEnvironment target) =>
        _environmentControl.Apply(target, recordHistory: false);

    // ── rollback ─────────────────────────────────────────────────────────

    public void BindHistoryReplacement(SceneEntityHandle previous, SceneEntityHandle replacement)
    {
        if (previous.Kind != replacement.Kind) return;
        var original = _handles.ResolveHistory(previous);
        var current = _handles.Resolve(replacement);
        switch (original, current)
        {
            case (IActor from, IActor to): _actorHistory.BindReplacement(from, to); break;
            case (IPropHandle from, IPropHandle to): _propHistory.BindReplacement(from, to); break;
            case (IOverlayNode from, IOverlayNode to): _overlayHistory.BindReplacement(from, to); break;
            case (IWorldObject from, IWorldObject to): _worldHistory.BindReplacement(from, to); break;
            case (ILight from, ILight to): _lightHistory.BindReplacement(from, to); break;
            case (IVirtualCamera from, IVirtualCamera to): _cameraHistory.BindReplacement(from, to); break;
        }
    }

    /// <summary>A load's rollback and undo: the same pre-delete cleanup a
    /// clear runs, so gaze and appearance ownership go with the actor rather
    /// than being reconciled later by name.</summary>
    public void DestroyActor(SceneEntityHandle actor) => _handles.Remove<IActor>(
        actor, SceneEntityKind.Actor, _actorHistory.Resolve, entity =>
    {
        ActorRemovalCleanup.Prepare(entity, _gaze, _integration, _bindings,
            refusal => _log?.Warning($"Scene rollback: {refusal}"));
        var lineage = _bindings.GetActorId(entity)?.LogicalId;
        if (!_spawns.DestroyActor(entity) && _actors.Actors.Contains(entity))
            throw new InvalidOperationException("The scene actor could not be destroyed.");
        if (lineage is { } gone)
            _selection.RemoveActorLineage(gone);
    });

    public void DestroyProp(SceneEntityHandle prop) => _handles.Remove<IPropHandle>(
        prop, SceneEntityKind.Prop, _propHistory.Resolve, entity => _props.Destroy((PropHandle)entity));

    public void DestroyOverlay(SceneEntityHandle overlay) =>
        _handles.Remove<IOverlayNode>(overlay, SceneEntityKind.Overlay, _overlayHistory.Resolve,
            entity => _overlays.Destroy((Poser.Game.Overlays.OverlayNodeHandle)entity));

    public void DestroyLight(SceneEntityHandle light) => _handles.Remove<ILight>(
        light, SceneEntityKind.Light, _lightHistory.Resolve, _lighting.DestroyLight);

    public void DestroyCamera(SceneEntityHandle camera) =>
        _handles.Remove<IVirtualCamera>(
            camera, SceneEntityKind.Camera, _cameraHistory.Resolve, _cameras.DestroyCamera);
}
