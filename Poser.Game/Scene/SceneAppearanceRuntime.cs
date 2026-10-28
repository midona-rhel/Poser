using Poser.Application.World;
using Poser.Application.Posing;
using Poser.Application.Transforms;
using Poser.Application.Scene;
using System;
using Poser.Domain.Identity;
using Poser.Domain;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Application.Animation;
using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Domain.Animation;
using Poser.Domain.Companions;
using Poser.Game.Bindings;
using Poser.Game.Posing;
using Poser.Domain.Scene;
using Poser.Documents.Files;
using Poser.Documents.Scene;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Scene;

/// <summary>
/// The scene's appearance files: the save-side capture, hash stamp and
/// portable seal, and the load-side collection restore and character-file
/// import. Every character-file child runs through the existing MCDF
/// transaction; this owns only the files a scene stages for it.
/// </summary>
internal sealed class SceneAppearanceRuntime : ISceneCapturePort, IDisposable
{
    private readonly SessionAppearanceFiles _historyAppearanceFiles = new(DeleteQuietly);
    private readonly SceneRuntimeHandles _handles;
    private readonly IFramework _framework;
    private readonly ISceneDocumentStore _documents;
    private readonly ISessionGenerationSource _sessions;
    private readonly SceneCaptureService _capture;
    private readonly IActorSpawnService _spawns;
    private readonly StableBindingRegistry _bindings;
    private readonly Poser.Application.Integration.IntegrationSelectors _integration;
    private readonly Poser.Application.Integration.McdfTransaction _mcdf;
    private readonly IActorManager _actors;

    /// <summary>Finds an appearance package by its bytes. Held as the
    /// interface: the library owns MCDFs and will own this index too.
    /// </summary>
    private readonly Poser.Documents.Library.IMcdfHashIndex _mcdfHashes;

    private readonly IPluginLog _log;

    public SceneAppearanceRuntime(
        SceneRuntimeHandles handles,
        IFramework framework,
        ISceneDocumentStore documents,
        ISessionGenerationSource sessions,
        SceneCaptureService capture,
        IActorSpawnService spawns,
        StableBindingRegistry bindings,
        Poser.Application.Integration.IntegrationSelectors integration,
        Poser.Application.Integration.McdfTransaction mcdf,
        IActorManager actors,
        Poser.Documents.Library.IMcdfHashIndex mcdfHashes,
        IPluginLog log)
    {
        _handles = handles;
        _framework = framework;
        _documents = documents;
        _sessions = sessions;
        _capture = capture;
        _spawns = spawns;
        _bindings = bindings;
        _integration = integration;
        _mcdf = mcdf;
        _actors = actors;
        _mcdfHashes = mcdfHashes;
        _log = log;
        _framework.Update += SweepHistoryAppearance;
        // Remove old packages from the current and legacy temp directories.
        // File age is a heuristic; this does not inspect other running clients.
        _ = Task.Run(() =>
        {
            var cutoff = DateTime.UtcNow - SessionAppearanceFiles.StaleAge;
            SessionAppearanceFiles.DeleteStale(
                SessionAppearanceFiles.TempDirectory, cutoff, DeleteQuietly);
            SessionAppearanceFiles.DeleteStale(
                System.IO.Path.GetTempPath(), cutoff, DeleteQuietly);
        });
    }

    private SessionGeneration? ActiveSession => _sessions.ActiveSessionGeneration;

    private void SweepHistoryAppearance(IFramework _) =>
        _historyAppearanceFiles.Sweep(ActiveSession);

    public void Dispose()
    {
        _framework.Update -= SweepHistoryAppearance;
        _historyAppearanceFiles.Dispose();
    }

    public string? ArmSceneCapture(
        Guid sceneId,
        string? description,
        Action<SceneCaptureOutcome> onCaptured,
        bool includeAnimation = false) =>
        _capture.BeginCapture(sceneId, description, onCaptured, includeAnimation);

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
            var hashed = FileDigest.TryHashFile(mcdf.Path);
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
                // New exports require a spawned actor, the local player,
                // or an actor explicitly adopted into Poser.
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
                string digest = FileDigest.TryHashFile(source)
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
    /// Waits for the shared MCDF operation slot, up to the supplied deadline.
    /// Starts the child on the framework thread once free, or reports timeout
    /// or cancellation. The child's own completion is awaited separately.
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
                if (!_mcdf.Busy)
                    return begin();
                holder = _mcdf.CurrentCompletion;
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

    /// <summary>
    /// Exports one appearance package through McdfTransaction. Returns null
    /// on success or a failure reason. Delete is false while the transaction
    /// may still be writing the destination, so cleanup must wait for it.
    /// </summary>
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
            var started = _mcdf.BeginExport(
                id, destination, $"Scene appearance: {name}");
            if (!started.Success)
                return started.Detail ?? "the appearance could not be packaged.";
            operationId = _mcdf.Receipt?.OperationId;
            return null;
        }, "another character-file operation held the slot for the whole bound.",
            "the save was cancelled.", bound, cancellation);
        if (refusal != null)
            return (refusal, true);

        if (operationId is not { } admitted)
            return ("the appearance export did not publish its receipt.", true);

        // The export is this save's child: on the deadline or a cancelled
        // save it is cancelled by its own id and drained, never abandoned.
        var waited = await _mcdf.AwaitOperation(admitted, bound, cancellation);
        if (waited.Applied)
            return (null, false);
        if (!waited.Terminal)
        {
            // Still writing past the drain bound: the destination stays the
            // child's until it stops, and is deleted then — never under it.
            DeleteWhenSettled(destination, _mcdf.Settled(admitted));
            _log.Warning(
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

    /// <summary>Deletes a temporary file after the MCDF child stops reading
    /// or writing it, regardless of the child's outcome.</summary>
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

    // ── load side ────────────────────────────────────────────────────────

    public async Task<Outcome> RestoreCollection(SceneEntityHandle actor, SceneActor data, TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        if (data.PenumbraCollection is not { } collection || data.Mcdf is not null)
            return Outcome.Ok();
        var target = await _framework.RunOnFrameworkThread(() =>
            _bindings.GetActorId(_handles.Require<IActor>(actor, SceneEntityKind.Actor)));
        if (target is not { } id) return Outcome.Fail("The actor is no longer bound.");
        // Already wearing it (an older file that recorded the player's
        // collection a spawn inherits): no assignment, and no redraw.
        if (await _framework.RunOnFrameworkThread(() => _integration.ReadCollection(id)) is
            { Success: true, Value: { HasIndividualAssignment: true } current }
            && current.EffectiveId == collection)
            return Outcome.Ok();
        string name = "None";
        if (collection != Guid.Empty)
        {
            var available = await _framework.RunOnFrameworkThread(() => _integration.ListCollections());
            if (!available.Success || available.Value is null)
                return Outcome.Fail(available.Detail ?? "Could not read the local Penumbra collections.");
            var saved = available.Value.FirstOrDefault(x => x.Id == collection);
            if (saved is null)
                return new Outcome(true, "The saved Penumbra collection is not available on this machine. " +
                    "This actor kept its local appearance settings; you can choose another collection in Appearance.");
            name = saved.Name;
        }
        var result = await _integration.SetCollectionAndWait(id, collection, name, bound, cancellation);
        return result.Outcome;
    }

    /// <summary>
    /// Imports through McdfTransaction. Portable packages are streamed to a temporary
    /// file and checked against their saved digest. Successful imports retain that
    /// file for session history; failed imports delete it after the child stops.
    /// References resolve by library checksum first, then by recorded path.
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
                    // A missing library match does not prove the file changed.
                    // Hash the fallback path and compare with the saved digest.
                    var hash = FileDigest.TryHashFile(found);
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
                var started = _mcdf.BeginImport(id, source);
                if (!started.Success)
                    return started.Detail ?? "The character file import was refused.";
                // The transaction publishes a Pending receipt inside admission, so
                // the id of THIS operation is readable the moment it is admitted.
                operationId = _mcdf.Receipt?.OperationId;
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
            var waited = await _mcdf.AwaitOperation(admitted, bound, cancellation);
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
                    DeleteWhenSettled(staged, _mcdf.Settled(admitted));
                    staged = null;
                }
                _log.Warning(
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
}
