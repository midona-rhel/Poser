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
/// The appearance part of saving and loading a scene. Saving records each
/// actor's MCDF and its hash, and packs appearances into the scene file.
/// Loading sets each actor's Penumbra collection and puts the saved
/// appearance back. Every MCDF import and export goes through the usual MCDF
/// code; this class only handles the files a scene needs for it.
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

    /// <summary>Finds an MCDF in the library by its content.
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
        // Delete old appearance files from Poser's temp folder and from the
        // one older versions used. Only a file's age is checked, not whether
        // another running game is still using it.
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

    internal void RememberActorIdentity(IActor actor, string? hash) => _capture.RememberIdentity(actor, hash);

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
            // An appearance saved in the scene already has the hash of the copy inside the
            // scene file. Hashing the original path would record a file the
            // scene no longer uses.
            if (mcdf.IsPortable)
                continue;
            var hashed = FileDigest.TryHashFile(mcdf.Path);
            if (hashed is null)
            {
                // Still save the path: a load can follow it, it just cannot
                // check that the file is the same.
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

            // Use the MCDF already applied to the actor. Only an actor without one
            // gets a new MCDF exported.
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
                // Only export the appearance of an actor Poser spawned, the
                // player's own character, or an actor taken over into Poser.
                // This method runs in the background, but these checks read
                // the game's actor list, so they run together on the game's
                // main thread.
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
                    // Poser could not import an MCDF this large again, so
                    // there is no point saving it.
                    notes.Add(
                        $"Actor '{actor.Name}''s appearance is " +
                        $"{Megabytes(info.Length)}, over the " +
                        $"{Megabytes(SceneFileLimits.MaxEmbeddedAppearanceBytes)} " +
                        "that Poser can import back; the scene saved without it.");
                    if (created != null)
                        DeleteQuietly(created);
                    continue;
                }

                // Hash the file piece by piece and leave it on disk. The
                // writer copies it straight into the scene file, so a large
                // MCDF is never held in memory whole.
                string digest = FileDigest.TryHashFile(source)
                    ?? throw new System.IO.IOException(
                        "the package could not be checksummed.");
                total += info.Length;
                actor.Mcdf = new SceneActorMcdf
                {
                    LoadedOnto = actor.Mcdf?.LoadedOnto ?? false,
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
    /// Waits, up to <paramref name="bound"/>, until no other MCDF import or
    /// export is running, then runs <paramref name="begin"/> on the game's
    /// main thread to start one. Returns why it could not start: the time ran
    /// out, the work was cancelled, or the reason <paramref name="begin"/>
    /// gave. Waiting for the started work to finish is done separately.
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
    /// Exports one actor's appearance as an MCDF. Returns null on success, or
    /// why it failed. Delete is false while the export may still be writing
    /// the file: the caller must not delete it, because it is deleted once
    /// the export stops.
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

        // The export belongs to this save. If the save runs out of time or is
        // cancelled, the export is cancelled too and waited for, never left
        // running on its own.
        var waited = await _mcdf.AwaitOperation(admitted, bound, cancellation);
        if (waited.Applied)
            return (null, false);
        if (!waited.Terminal)
        {
            // The export is still writing after the wait. Leave the file alone
            // until it stops, then delete it.
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
                // A file whose size cannot be read adds nothing to the
                // estimate. The save names it if it cannot read it either.
            }
        }
        return total;
    }

    public void DeleteTemporary(string path) => DeleteQuietly(path);

    /// <summary>Deletes a temporary file once the MCDF import or export using
    /// it has stopped, whether or not it succeeded.</summary>
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
            // A leftover temporary file only costs disk space. The save must
            // not fail because a cleanup did.
        }
    }

    // ── load side ────────────────────────────────────────────────────────

    public async Task<Outcome> RestoreCollection(SceneEntityHandle actor, SceneActor data, TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        if (data.PenumbraCollection is not { } collection)
            return Outcome.Ok();
        var target = await _framework.RunOnFrameworkThread(() =>
            _bindings.GetActorId(_handles.Require<IActor>(actor, SceneEntityKind.Actor)));
        if (target is not { } id) return Outcome.Fail("The actor is no longer bound.");
        // The actor already has this collection, as with an older file that
        // saved the collection a spawned actor got from the player. Nothing to
        // set, and no redraw.
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

    /// <summary>Finds the saved MCDF. A packed one is copied out to a temporary file and
    /// checked against its saved hash. A referenced one is looked up in the library by its
    /// content first, then at its saved path. <c>Changed</c> is a message when the MCDF
    /// differs from what the scene saved. The caller deletes <c>Staged</c> when done.</summary>
    private async Task<(string? Source, string? Staged, string? Changed, string? Refusal)> LocatePackage(
        string scenePath, SceneActorMcdf saved, System.Threading.CancellationToken cancellation)
    {
        string? changed = null;
        string? staged = null;
        string source;
        (string?, string?, string?, string?) Refused(string detail) => (null, staged, null, detail);
        if (saved.IsPortable)
        {
            staged = SessionAppearanceFiles.NewTempPath(staged: true);
            try
            {
                // Copy the MCDF out of the scene file piece by piece, since a
                // real one can be hundreds of megabytes, and hash it on the
                // way. Only the saved hash shows the copy is intact, so an
                // MCDF that does not match it is not applied.
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
                    return Refused(
                        $"The appearance package '{saved.FileName}' does not match the " +
                        "digest the scene recorded for it, so it was not imported.");
            }
            catch (Exception ex)
            {
                return Refused(
                    $"The appearance package '{saved.FileName}' could not be " +
                    $"staged for import: {ex.Message}");
            }
            source = staged;
        }
        else
        {
            // Look the file up by its content first. SceneAppearanceSource
            // makes that choice, so it can be tested without the game.
            var resolved = SceneAppearanceSource.Resolve(
                saved, _mcdfHashes, System.IO.File.Exists, cancellation);
            if (resolved.Origin == SceneAppearanceOrigin.None ||
                resolved.Path is not { } found)
                return Refused(
                    resolved.Detail
                    ?? $"The character file '{saved.FileName}' could not be found.");

            changed = resolved.Detail;
            if (resolved.Origin == SceneAppearanceOrigin.RecordedPath &&
                saved.ContentHash.Length > 0)
            {
                // Not finding it in the library does not mean the file
                // changed. Hash the file at the saved path and compare.
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
        return (source, staged, changed, null);
    }

    /// <summary>
    /// Puts a scene appearance back on the actor and leaves it editable: the Glamourer
    /// settings from the saved package, and, for anyone but your own character, its
    /// Customize+ body. The actor keeps the mods of its own collection. Your own character
    /// keeps its own Customize+ profile.
    /// </summary>
    public async Task<SceneMcdfOutcome> ApplySceneAppearance(
        string scenePath,
        SceneEntityHandle actor,
        SceneActor data,
        TimeSpan bound,
        System.Threading.CancellationToken cancellation)
    {
        if (data.Mcdf is not { } saved)
            return SceneMcdfOutcome.Silent;
        string? staged = null;
        try
        {
            var located = await LocatePackage(scenePath, saved, cancellation);
            staged = located.Staged;
            if (located.Refusal is { } missing)
                return SceneMcdfOutcome.Refused(missing);
            var read = _mcdf.ReadLook(located.Source!);
            if (!read.Success || read.Value is not { } look)
                return SceneMcdfOutcome.Refused(read.Detail ?? "The scene appearance could not be read.");
            string? body = null;
            if (look.CustomizePlusData.Length > 0)
            {
                try { body = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(look.CustomizePlusData)); }
                catch (FormatException) { return SceneMcdfOutcome.Refused("The scene appearance's body is not readable."); }
            }
            // An actor the scene reuses, such as your own character, may have an MCDF
            // applied. The scene appearance replaces it, so that MCDF comes off first.
            var removed = await _framework.RunOnFrameworkThread(() =>
            {
                var target = _handles.Resolve<IActor>(actor, SceneEntityKind.Actor);
                if (target == null || _bindings.GetActorId(target) is not { } id)
                    return "The actor is no longer available.";
                if (_integration.OverridesFor(id).Mcdf is null)
                    return null;
                var reset = _mcdf.Reset(id);
                return reset.Success ? null : reset.Detail ?? "The MCDF applied to the actor could not be removed.";
            });
            if (removed != null)
                return SceneMcdfOutcome.Refused(removed);
            // Removing an MCDF can leave its clean-up running; the look waits for it.
            var refusal = await BeginMcdfChild(() =>
            {
                var target = _handles.Resolve<IActor>(actor, SceneEntityKind.Actor);
                if (target == null || _bindings.GetActorId(target) is not { } id)
                    return "The actor is no longer available.";
                if (look.GlamourerData.Length > 0
                    && _integration.ApplySavedLook(id, look.GlamourerData, "Scene appearance") is { Success: false } lookFailed)
                    return lookFailed.Detail ?? "The scene appearance could not be applied.";
                if (body != null && !_actors.IsLocalPlayer(target)
                    && _integration.ApplyBodyProfileJson(id, body, "Scene appearance") is { Success: false } bodyFailed)
                    return bodyFailed.Detail ?? "The scene appearance's body could not be applied.";
                return null;
            }, "An MCDF operation held the actor for the whole bound.", "The load was cancelled.", bound, cancellation);
            return refusal is null ? SceneMcdfOutcome.Ok(located.Changed) : SceneMcdfOutcome.Refused(refusal);
        }
        finally
        {
            if (staged != null)
                DeleteQuietly(staged);
        }
    }

    /// <summary>
    /// Applies the saved MCDF to the actor. A packed MCDF is copied to a temporary file
    /// and checked against its saved hash. After a successful import that file is kept so
    /// undo and redo can use it; after a failed one it is deleted once the import stops.
    /// A referenced MCDF is looked up in the library by content first, then at its saved
    /// path.
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
            // Find the file first, in the background. If it is missing, the
            // actor is never touched, and a changed file is reported before
            // anything is applied.
            var located = await LocatePackage(scenePath, saved, cancellation);
            staged = located.Staged;
            if (located.Refusal is { } missing)
                return SceneMcdfOutcome.Refused(missing);
            string source = located.Source!;
            string? changed = located.Changed;

            Guid? operationId = null;
            var refusal = await BeginMcdfChild(() =>
            {
                var target = _handles.Resolve<IActor>(actor, SceneEntityKind.Actor);
                if (target == null) return "The actor is no longer available.";
                if (_bindings.GetActorId(target) is not { } id)
                    return "The actor has no stable identity to import a character file onto.";
                // The scene restores its saved pose after all appearances are ready.
                var started = _mcdf.BeginImport(id, source, preservePose: false, name: saved.FileName);
                if (!started.Success)
                    return started.Detail ?? "The character file import was refused.";
                // The import's id can be read as soon as it has started.
                operationId = _mcdf.Receipt?.OperationId;
                return null;
            }, "Another character-file operation held the slot for the whole bound.",
                "The load was cancelled.", bound, cancellation);
            if (refusal != null)
                return SceneMcdfOutcome.Refused(refusal);

            if (operationId is not { } admitted)
                return SceneMcdfOutcome.Refused(
                    "The character file import did not publish its receipt.");

            // The import belongs to this load. If the load runs out of time or
            // is cancelled, the import is cancelled too and waited for. A
            // cancelled import changes nothing in its later steps, so one that
            // finishes late undoes itself instead of changing the actor.
            var waited = await _mcdf.AwaitOperation(admitted, bound, cancellation);
            if (waited.Applied)
            {
                // The import finished, possibly just before it was cancelled.
                // Keep the temporary file: undo and redo may import it again.
                if (staged is not null &&
                    historySession is { } session && ActiveSession == session &&
                    _historyAppearanceFiles.Retain(staged, session))
                    staged = null;
                return SceneMcdfOutcome.Ok(changed);
            }
            if (!waited.Terminal)
            {
                // Cancelled but still running after the wait. It may still read
                // the temporary file, so the file is deleted when the import
                // stops, not now.
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
