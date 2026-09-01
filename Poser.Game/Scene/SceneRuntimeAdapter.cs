using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Entities;
using Poser.Files;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>
/// The production <see cref="ISceneRuntime"/>: thin bindings from the scene
/// transaction's phase vocabulary onto the real owners — the accepted spawn
/// service, the ONE atomic pose import, the lighting/camera/environment
/// services, and the scene codec/store. It owns no transaction state; every
/// method is one materialization step.
/// </summary>
internal sealed class SceneRuntimeAdapter : ISceneRuntime
{
    private readonly IFramework _framework;
    private readonly ISessionGenerationSource _sessions;
    private readonly SceneCaptureService _capture;
    private readonly SceneFileStore _store;
    private readonly CleanPoseFacade _poses;
    private readonly IActorSpawnService _spawns;
    private readonly ISkeletonService _skeletons;
    private readonly IPosingService _posing;
    private readonly PropSpawnService _props;
    private readonly Poser.Game.Overlays.OverlayNodeService _overlays;
    private readonly ILightingService _lighting;
    private readonly IVirtualCameraService _cameras;
    private readonly IEnvironmentService _environment;
    private readonly StableBindingRegistry _bindings;
    private readonly AnimationSession _animation;
    private readonly IGazeService _gaze;
    private readonly Poser.Application.Integration.ActorIntegrationSession _integration;
    private readonly IWorldRenderingService _rendering;
    private readonly IActorManager _actors;
    private readonly IObjectTable _objects;
    private readonly WorldObjects.WorldObjectService _worldObjects;
    private readonly Poser.Services.IPlaceService _place;

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
        ISessionGenerationSource sessions,
        SceneCaptureService capture,
        CleanPoseFacade poses,
        IActorSpawnService spawns,
        ISkeletonService skeletons,
        IPosingService posing,
        PropSpawnService props,
        Poser.Game.Overlays.OverlayNodeService overlays,
        ILightingService lighting,
        IVirtualCameraService cameras,
        IEnvironmentService environment,
        StableBindingRegistry bindings,
        AnimationSession animation,
        IGazeService gaze,
        Poser.Application.Integration.ActorIntegrationSession integration,
        IWorldRenderingService rendering,
        IActorManager actors,
        IObjectTable objects,
        WorldObjects.WorldObjectService worldObjects,
        Poser.Services.IPlaceService place,
        Poser.Library.IMcdfHashIndex mcdfHashes,
        Poser.Application.Selection.SelectionSession selection,
        IPluginLog? log = null)
    {
        _mcdfHashes = mcdfHashes;
        _selection = selection;
        _log = log;
        _actors = actors;
        _objects = objects;
        _worldObjects = worldObjects;
        _place = place;
        _rendering = rendering;
        _integration = integration;
        _bindings = bindings;
        _animation = animation;
        _gaze = gaze;
        _framework = framework;
        _sessions = sessions;
        _capture = capture;
        _store = SceneFileStore.Default;
        _poses = poses;
        _spawns = spawns;
        _skeletons = skeletons;
        _posing = posing;
        _props = props;
        _overlays = overlays;
        _lighting = lighting;
        _cameras = cameras;
        _environment = environment;
    }

    public SessionGeneration? ActiveSession => _sessions.ActiveSessionGeneration;

    public Task<T> OnFramework<T>(Func<T> func) =>
        _framework.RunOnFrameworkThread(func);

    public SceneReadOutcome ReadScene(string path) => _store.Read(path);

    public SceneWriteOutcome WriteScene(SceneFile scene, string path) =>
        _store.Write(scene, path);

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
                created = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"poser-scene-appearance-{Guid.NewGuid():N}.mcdf");
                var exported = await ExportAppearance(
                    id, actor.Name, created, bound, cancellation);
                if (exported != null)
                {
                    notes.Add($"Actor '{actor.Name}': {exported}");
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
    /// provider was unavailable.
    /// </summary>
    private async Task<string?> ExportAppearance(
        Poser.Domain.Identity.ActorId id,
        string name,
        string destination,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        Guid? operationId = null;
        var refusal = await _framework.RunOnFrameworkThread(() =>
        {
            if (_integration.McdfBusy)
                return "another character-file operation is running.";
            var started = _integration.BeginExport(
                id, destination, $"Scene appearance: {name}");
            if (!started.Success)
                return started.Detail ?? "the appearance could not be packaged.";
            operationId = _integration.McdfReceipt?.OperationId;
            return null;
        });
        if (refusal != null)
            return refusal;

        var deadline = DateTime.UtcNow + bound;
        while (true)
        {
            var receipt = _integration.McdfReceipt;
            if (receipt is { } terminal &&
                terminal.OperationId == operationId &&
                terminal.State != OperationReceiptState.Pending)
            {
                return terminal.State == OperationReceiptState.Applied
                    ? null
                    : terminal.Detail
                        ?? $"the appearance export ended {terminal.State}.";
            }
            if (DateTime.UtcNow >= deadline)
                return "the appearance export did not finish within its bound.";
            try
            {
                await Task.Delay(50, cancellation);
            }
            catch (OperationCanceledException)
            {
                return "the save was cancelled.";
            }
        }
    }

    /// <summary>
    /// Brio's <c>CleanObject</c> semantics, in Poser's terms, run BEFORE the
    /// native delete: Brio releases look-at and reverts the character handler
    /// while the object still exists
    /// (<c>Brio/Game/Actor/ActorSpawnService.cs:245-256</c>, called at
    /// <c>:203</c> — before <c>DeleteObjectByIndex</c> at <c>:208</c>), because
    /// after the delete there is nothing left to name.
    ///
    /// <para>Two of the three parts apply here. The actor's own GAZE is
    /// released so Poser stops driving channels on a body that is about to go.
    /// Its APPEARANCE is reverted through the ordinary integration teardown,
    /// which is what releases MCDF ownership, the temporary collection, the
    /// Glamourer design and the Customize+ profile — the adopted equivalent of
    /// what <c>TryDelete</c> already does for an owned actor's Penumbra
    /// collection.</para>
    ///
    /// <para>The third part does NOT apply, deliberately. Brio scrubs the
    /// removed object out of every other actor's look-at. Poser keeps an
    /// Entity gaze target BY ID and marks it stale
    /// (<c>IGazeService.TargetStale</c>), so a target that leaves the scene is
    /// refused by name on reapply instead of being followed. Scrubbing it here
    /// would delete the user's stated intent to look at that actor; leaving it
    /// stale is the stronger behaviour and is already the design.</para>
    ///
    /// <para>A cleanup that fails is NAMED, never skipped silently — the actor
    /// is still removed, but the outcome says what did not come apart.</para>
    /// </summary>
    private void PrepareActorRemoval(IActor actor, List<string> refusals)
    {
        try
        {
            _gaze.ResetGaze(actor);
        }
        catch (Exception ex)
        {
            refusals.Add(
                $"{actor.Name}: the gaze could not be released before removal " +
                $"({ex.Message}).");
        }

        if (_bindings.GetActorId(actor) is not { } id)
            return;
        try
        {
            var reverted = _integration.ResetActor(id);
            if (!reverted.Success)
                refusals.Add(
                    $"{actor.Name}: the appearance could not be reverted before " +
                    $"removal ({reverted.Detail ?? "the revert was refused"}).");
        }
        catch (Exception ex)
        {
            refusals.Add(
                $"{actor.Name}: the appearance could not be reverted before " +
                $"removal ({ex.Message}).");
        }
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

    // The SAME place source the capture stamped the document from
    // (SceneCaptureService.CaptureTerritory), so "the same territory" means one
    // thing on both sides of the file.
    public uint CurrentTerritoryId() => _place.Current.TerritoryId;

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
    /// <para>A removal the native gates refuse — a stale wrapper, a companion
    /// body, the GPose primary, an actor no longer in the table — is named in
    /// the outcome. That is the exception path now, not the design.</para>
    /// </summary>
    public SceneClearOutcome ClearScene()
    {
        int actors = 0;
        var refused = new List<string>();
        var refusedCleanup = new List<string>();
        foreach (var actor in _actors.Actors.ToList())
        {
            // Gaze and appearance are released BEFORE the delete, while the
            // actor still exists to release them against; Brio does the same
            // in CleanObject (Brio/Game/Actor/ActorSpawnService.cs:245-256)
            // and for the same reason — after the delete there is nothing left
            // to name.
            var lineage = _bindings.GetActorId(actor)?.LogicalId;
            PrepareActorRemoval(actor, refusedCleanup);

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


    // ── actors ───────────────────────────────────────────────────────────

    public object? SpawnActor(SceneActor data, out string? detail)
    {
        var actor = _spawns.SpawnNewActor(data.HasCompanionSlot);
        if (actor is null)
        {
            detail = "The spawn service returned no actor.";
            return null;
        }
        if (data.ModelCharaId != 0)
            _spawns.SetModelCharaId(actor, data.ModelCharaId);
        detail = null;
        return actor;
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

    public bool ActorReady(object actor)
    {
        var candidate = (IActor)actor;
        var skeletons = _skeletons.GetSkeletons(candidate);
        if (skeletons.Count == 0 ||
            _bindings.GetActorId(candidate) is not { } id)
            return false;
        if (_bindings.Resolve(id) is not { Success: true, Value: { } bound } ||
            !ReferenceEquals(bound, candidate))
            return false;

        foreach (var skeleton in skeletons)
        {
            // A skeleton still building may have no root yet; that is
            // not-ready, not a refusal.
            if (skeleton.RootBone is not { } root)
            {
                Trace(
                    $"not ready: actor {candidate.Name} wrapper {Ord(candidate)} " +
                    $"slot {skeleton.Slot} skeleton {Ord(skeleton)} has no root yet");
                return false;
            }
            if (_bindings.GetBoneId(root) is null)
            {
                Trace(
                    $"not ready: actor {candidate.Name} wrapper {Ord(candidate)} " +
                    $"slot {skeleton.Slot} skeleton {Ord(skeleton)} " +
                    $"base {skeleton.CharacterBaseAddress:X} root {Ord(root)} " +
                    "is not published to the binding registry");
                return false;
            }
        }

        Trace(
            $"ready: actor {candidate.Name} wrapper {Ord(candidate)} " +
            string.Join(", ", skeletons.Select(skeleton =>
                $"[{skeleton.Slot} skeleton {Ord(skeleton)} " +
                $"base {skeleton.CharacterBaseAddress:X} " +
                $"root {Ord(skeleton.RootBone)} bones {skeleton.Bones.Count}]")));
        return true;
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
    /// ledger. The staged file is deleted once the import reaches its terminal
    /// receipt, whichever way it ended. Its checksum is NOT consulted: the
    /// bytes in the document ARE the package, so there is nothing to identify
    /// them against.</para>
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
        object actor,
        SceneActor data,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        if (data.Mcdf is not { } saved)
            return SceneMcdfOutcome.Silent;

        string? staged = null;
        try
        {
            // File work first, off the framework thread: a missing package is a
            // refusal that never touches the actor, and a changed one is named
            // before anything is applied.
            string? changed = null;
            string source;
            if (saved.IsPortable)
            {
                staged = System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    $"poser-scene-appearance-{Guid.NewGuid():N}.mcdf");
                try
                {
                    // Container entry to disk, as a STREAM. A real package is
                    // hundreds of megabytes; nothing here holds it.
                    using var payload = _store.OpenAppearance(
                        scenePath, saved.PackageEntry!)
                        ?? throw new System.IO.IOException(
                            "the scene holds no such payload.");
                    using var staging = System.IO.File.Create(staged);
                    await payload.CopyToAsync(staging, cancellation);
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

            var target = (IActor)actor;
            Guid? operationId = null;
            var refusal = await _framework.RunOnFrameworkThread(() =>
            {
                if (_bindings.GetActorId(target) is not { } id)
                    return "The actor has no stable identity to import a character file onto.";
                if (_integration.McdfBusy)
                    return "Another character-file operation is running.";
                var started = _integration.BeginImport(id, source);
                if (!started.Success)
                    return started.Detail ?? "The character file import was refused.";
                // The transaction publishes a Pending receipt inside admission, so
                // the id of THIS operation is readable the moment it is admitted.
                operationId = _integration.McdfReceipt?.OperationId;
                return null;
            });
            if (refusal != null)
                return SceneMcdfOutcome.Refused(refusal);

            var deadline = DateTime.UtcNow + bound;
            while (true)
            {
                var receipt = _integration.McdfReceipt;
                if (receipt is { } terminal &&
                    terminal.OperationId == operationId &&
                    terminal.State != OperationReceiptState.Pending)
                {
                    return terminal.State == OperationReceiptState.Applied
                        ? SceneMcdfOutcome.Ok(changed)
                        : SceneMcdfOutcome.Refused(
                            terminal.Detail
                            ?? $"The character file import ended {terminal.State}.");
                }
                if (DateTime.UtcNow >= deadline)
                    return SceneMcdfOutcome.Refused(
                        $"The character file '{saved.FileName}' did not finish " +
                        "importing within its bound.");
                try
                {
                    await Task.Delay(50, cancellation);
                }
                catch (OperationCanceledException)
                {
                    return SceneMcdfOutcome.Refused("The load was cancelled.");
                }
            }
        }
        finally
        {
            if (staged != null)
                DeleteQuietly(staged);
        }
    }

    // Only called for an actor whose attachment is present: the workflow skips
    // an absent kind rather than asking the runtime to detach.
    public string? AttachCompanion(object actor, SceneActor data) =>
        _spawns.SetCompanion(
            (IActor)actor,
            new CompanionAttachment(data.CompanionKind!.Value, data.CompanionId))
            ? null
            : "The companion could not be attached.";

    /// <summary>Every component and every slot: an embedded scene pose is a
    /// complete captured state, not an interactive rotation-only import.
    /// Placement is absolute and separate (<see cref="PlaceActor"/>), so the
    /// difference-based model transform stays off.</summary>
    private static readonly PoseImportOptions SceneImportOptions = new()
    {
        ApplyRotation = true,
        ApplyPosition = true,
        ApplyScale = true,
        ApplyModelTransform = false,
    };

    public string? ArmPoseImport(
        object actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt)
    {
        // The arm's own view of the world, quoted the same way readiness
        // quotes it. If these ordinals differ from the ready line, the plan is
        // being built against a skeleton the registry never bound — which is
        // exactly the shape that reports "Import target n_root could not be
        // resolved" for every bone at once.
        var target = (IActor)actor;
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

        var result = _poses.ImportPose(
            target, data.Pose!, SceneImportOptions, description, onReceipt);
        if (!result.Success)
            Trace($"import refused for {target.Name}: {result.Detail}");
        return result.Success ? null : result.Detail ?? "The pose import refused.";
    }

    public bool CompanionReady(object actor) =>
        _spawns.GetCompanionActor((IActor)actor) is { } companion &&
        _skeletons.GetSkeletons(companion).Count > 0;

    public string? ArmCompanionPoseImport(
        object actor,
        SceneActor data,
        string description,
        Action<OperationReceipt> onReceipt)
    {
        if (_spawns.GetCompanionActor((IActor)actor) is not { } companion)
            return "The companion's body could not be resolved, so its pose was not restored.";
        if (_skeletons.GetSkeletons(companion).Count == 0)
            return "The companion's skeleton had not built, so its pose was not restored.";
        var result = _poses.ImportPose(
            companion, data.CompanionPose!, SceneImportOptions, description, onReceipt);
        return result.Success
            ? null
            : result.Detail ?? "The companion pose import refused.";
    }

    public string? PlaceActor(object actor, SceneActor data)
    {
        // The scene's OWN placement first. The embedded pose's absolute values
        // remain the fallback for files written before placements were stated,
        // and only there does the codec's unset marker (BoneData.Identity —
        // zero position, identity rotation, ZERO scale) have to be guessed at.
        System.Numerics.Vector3 position;
        System.Numerics.Quaternion rotation;
        System.Numerics.Vector3 scale;
        if (data.ModelTransform is { } stated)
        {
            position = stated.Position;
            rotation = stated.Rotation;
            scale = stated.Scale;
        }
        else
        {
            var absolute = data.Pose!.ModelAbsoluteValues;
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
        var target = (IActor)actor;
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
    public string? FreezeActor(object actor)
    {
        if (_bindings.GetActorId((IActor)actor) is not { } id)
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
    public string? ApplyActorGaze(object actor, SceneActor data, object? target)
    {
        if (data.Gaze is not { } saved || saved.Mode == GazeTargetMode.None)
            return null;
        if (!_gaze.IsAvailable)
            return _gaze.UnavailableDetail ?? "Gaze control is unavailable.";

        var source = (IActor)actor;

        // Entity mode IS its target: SetGazeTarget both chooses the actor and
        // enters the mode. A saved Entity gaze whose target the file does not
        // name has nothing to follow, and is refused by name rather than left
        // pointing at whatever the mode transition would pick.
        if (saved.Mode == GazeTargetMode.Entity)
        {
            if (target is not IActor followed)
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

    public void SetActorVisibility(object actor, bool visible) =>
        _spawns.SetVisibility((IActor)actor, visible);

    // ── props ────────────────────────────────────────────────────────────

    public object? SpawnOverlay(SceneOverlay data, out string? detail)
    {
        if (data.Node is not { } document)
        {
            detail = "The overlay entry carries no node document.";
            return null;
        }
        detail = null;
        var display = Dalamud.Bindings.ImGui.ImGui.GetIO().DisplaySize;
        if (data.CenterRelative && display.X > 0f && display.Y > 0f)
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
        if (display.X > 0f && display.Y > 0f &&
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
        return handle;
    }

    /// <summary>
    /// Re-borrows one of the map's own objects. Nothing is created — the object
    /// belongs to the map and was already standing there — so this MATCHES
    /// rather than spawns, and a match that does not come off is a refusal
    /// naming the model rather than a claim on something else.
    /// </summary>
    public object? AdoptWorldObject(SceneWorldObject data, out string? detail)
    {
        // A SPAWNED entry restores by creating its path anew — any zone;
        // a borrowed one matches the object the map is standing.
        var handle = data.Spawned
            ? _worldObjects.Spawn(
                data.Path, data.Transform, data.Visible, out detail)
            : _worldObjects.AdoptByIdentity(
                data.Path,
                data.MapPosition,
                data.Transform,
                data.Visible,
                out detail);
        if (handle != null && data.Name.Length > 0)
            handle.Name = data.Name;
        if (handle != null)
        {
            if (data.Opacity < 1f)
                handle.Opacity = data.Opacity;
            if (data.Tint is { } tint)
                handle.Tint = tint;
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
        return handle;
    }

    public void ReleaseWorldObject(object token) =>
        _worldObjects.Release((WorldObjects.AdoptedWorldObject)token);

    public object? SpawnProp(SceneProp data, out string? detail)
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
        return handle;
    }

    // ── lights ───────────────────────────────────────────────────────────

    public object? SpawnLight(
        SceneLight data, object? attachmentOwner, out string? detail)
    {
        var document = data.Light!;

        // Resolve the exact attachment bone BEFORE any native spawn — a
        // light whose stated parent bone is missing is refused whole, never
        // spawned detached into world space.
        IBone? bone = null;
        if (data.Attachment is { } attachment)
        {
            if (attachmentOwner is not IActor owner)
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

        LightFileService.Apply(document, light);
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
        return light;
    }

    // ── cameras ──────────────────────────────────────────────────────────

    private IVirtualCamera? DefaultCamera =>
        _cameras.Cameras.FirstOrDefault(camera => camera.IsDefault);

    public object? DefaultCameraToken() => DefaultCamera;

    public CameraFile CaptureDefaultCameraState() =>
        DefaultCamera is { } camera
            ? CameraFileService.CreateCameraFile(camera)
            : new CameraFile();

    public string? ApplyDefaultCamera(SceneCamera data)
    {
        if (DefaultCamera is not { } camera)
            return "The session has no default camera.";
        CameraFileService.Apply(data.Camera!, camera);
        return null;
    }

    public object? CreateCamera(SceneCamera data, out string? detail)
    {
        var camera = _cameras.CreateCamera(data.Camera!.Kind);
        if (camera is null)
        {
            detail = "The camera could not be created.";
            return null;
        }
        CameraFileService.Apply(data.Camera!, camera);
        detail = null;
        return camera;
    }

    public string? SetCameraTarget(
        object? camera, object targetActor, string displayName,
        bool targetLocked)
    {
        var target = camera as IVirtualCamera ?? DefaultCamera;
        if (target is null)
            return "The session has no default camera.";
        var exactActor = (IActor)targetActor;
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

    public string? SetLiveCamera(object? camera)
    {
        var target = camera as IVirtualCamera ?? DefaultCamera;
        if (target is null)
            return "The session has no default camera.";
        _cameras.SetLive(target);
        return null;
    }

    public void RestoreDefaultCamera(CameraFile baseline)
    {
        if (DefaultCamera is { } camera)
            CameraFileService.Apply(baseline, camera);
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

    public void ApplyEnvironment(SceneEnvironment target)
    {
        // Writing the clock forces the freeze on; releasing it afterwards is
        // the deliberate order for a scene saved with a running clock.
        _environment.MinuteOfDay = target.MinuteOfDay;
        _environment.DayOfMonth = target.DayOfMonth;
        _environment.IsTimeFrozen = target.IsTimeFrozen;

        _environment.TransitionTime = target.TransitionTime;
        if (target.IsWeatherOverrideEnabled)
            _environment.SetWeather(target.WeatherId, target.TransitionTime);
        else
            _environment.IsWeatherOverrideEnabled = false;

        // Stamp all eight sections: a held section takes its saved values
        // (the setters imply the hold), an unheld one releases to the game.
        foreach (var section in Enum.GetValues<EnvSection>())
        {
            bool held = target.HeldSections.Contains(section);
            if (!held)
            {
                _environment.SetSectionHeld(section, false);
                continue;
            }
            switch (section)
            {
                case EnvSection.Sky when target.Sky is { } sky:
                    _environment.Sky = sky;
                    break;
                case EnvSection.Clouds when target.Clouds is { } clouds:
                    _environment.Clouds = clouds;
                    break;
                case EnvSection.Lighting when target.Lighting is { } lighting:
                    _environment.Lighting = lighting;
                    break;
                case EnvSection.Fog when target.Fog is { } fog:
                    _environment.Fog = fog;
                    break;
                case EnvSection.Rain when target.Rain is { } rain:
                    _environment.Rain = rain;
                    break;
                case EnvSection.Particles when target.Particles is { } particles:
                    _environment.Particles = particles;
                    break;
                case EnvSection.Stars when target.Stars is { } stars:
                    _environment.Stars = stars;
                    break;
                case EnvSection.Wind when target.Wind is { } wind:
                    _environment.Wind = wind;
                    break;
            }
        }
    }

    // ── rollback ─────────────────────────────────────────────────────────

    public void DestroyActor(object actor) => _spawns.DestroyActor((IActor)actor);

    public void DestroyProp(object prop) => _props.Destroy((PropHandle)prop);

    public void DestroyOverlay(object overlay) =>
        _overlays.Destroy((Poser.Game.Overlays.OverlayNodeHandle)overlay);

    public void DestroyLight(object light) => _lighting.DestroyLight((ILight)light);

    public void DestroyCamera(object camera) =>
        _cameras.DestroyCamera((IVirtualCamera)camera);
}
