using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Integration;

/// <summary>
/// Reads an MCDF, applies its external appearance, and records resources for Reset.
/// Actor-changing callbacks check cancellation and the GPose session. On failure,
/// rollback attempts to restore the pre-import appearance and retains failed cleanup.
/// </summary>
internal sealed class McdfImport(
    IIntegrationResolutionPort actors,
    IPenumbraPort penumbra,
    IGlamourerPort glamourer,
    ICustomizePlusPort customizePlus,
    IMcdfFileBoundary files,
    ISessionGenerationSource sessions,
    IntegrationOwnership ownership,
    McdfTeardown teardown,
    SingleFlightOwner<McdfOperation, McdfProgress> flight)
{
    private McdfOperation? _inFlight;

    /// <summary>Hard validation limits for incoming packages.</summary>
    public McdfLimits Limits { get; set; } = McdfLimits.Default;

    /// <summary>The import still able to mutate the actor, if any.</summary>
    internal McdfOperation? InFlight => _inFlight;

    /// <summary>
    /// Records the import targeted by cancellation and scene-removal cleanup.
    /// </summary>
    internal void Track(McdfOperation operation) => _inFlight = operation;

    // ── In-flight invalidation ───────────────────────────────────────────

    /// <summary>
    /// Marks the current import invalid before cancelling it, then rolls back its
    /// recorded changes on the framework thread. Later guarded callbacks reject it;
    /// the worker may still finish file work and retry cleanup.
    /// </summary>
    internal void InvalidateInFlight()
    {
        if (_inFlight is not { Invalidated: false } operation)
        {
            // Nothing to invalidate — a read-only export, or an import
            // already invalidated — but the cooperative cancel still applies.
            flight.Cancel();
            return;
        }
        // Invalidate first: cancelling a token can synchronously run callbacks.
        // Those callbacks must already see that this import cannot continue.
        operation.Invalidated = true;
        flight.Cancel();
        Rollback(operation);
    }

    /// <summary>
    /// Invalidates the import if its actor is absent from the new scene snapshot.
    /// </summary>
    internal void InvalidateIfTargetMissing(HashSet<ActorId> present)
    {
        if (_inFlight is { Invalidated: false } inFlight
            && !present.Contains(inFlight.Target))
            InvalidateInFlight();
    }

    // ── Import ───────────────────────────────────────────────────────────

    internal async Task Run(
        McdfOperation operation, string path, CancellationToken cancellation, McdfPackage? retained, McdfOperationDirectory? retainedDirectory)
    {
        var actor = operation.Target;
        string fileName = operation.FileName;
        int filesTotal = 0;
        long bytesTotal = 0;
        const string cancelledDetail = "The import was cancelled.";

        void Step(McdfPhase phase, int filesDone, long bytesDone, bool cancellable = true) =>
            flight.PublishStep(operation, new McdfProgress(
                actor, fileName, McdfOperationKind.Import,
                phase, filesDone, filesTotal, bytesDone, bytesTotal, cancellable, null));
        void Finish(string detail, bool success)
        {
            bool cancelled = !success
                && (cancellation.IsCancellationRequested || operation.Invalidated);
            var progress = new McdfProgress(actor, fileName, McdfOperationKind.Import,
                success ? McdfPhase.Completed
                    : cancelled ? McdfPhase.Cancelled : McdfPhase.Failed,
                filesTotal, filesTotal, bytesTotal, bytesTotal, false,
                new McdfOutcome(success, cancelled, detail,
                    filesTotal, bytesTotal, Array.Empty<string>()));
            var receipt = success
                ? OperationReceipt.Applied(
                    operation.OperationId, operation.Epoch, operation.Session, actor, detail)
                : cancelled
                    ? OperationReceipt.Cancelled(
                        operation.OperationId, operation.Epoch, operation.Session, actor, detail)
                    : OperationReceipt.Failed(
                        operation.OperationId, operation.Epoch, operation.Session, actor, detail);
            flight.PublishTerminal(operation, progress, receipt);
        }

        // Actor-changing callbacks check this before applying the package.
        // Cleanup callbacks have different rules because they must run after cancellation.
        string? Guard()
        {
            if (operation.Invalidated || cancellation.IsCancellationRequested)
                return cancelledDetail;
            if (sessions.ActiveSessionGeneration is not { } live
                || live != operation.Session)
            {
                operation.Invalidated = true;
                return "The GPose session ended before the import completed.";
            }
            return null;
        }

        async Task<string?> RollbackRegistered()
        {
            try
            {
                // Idempotent: invalidation may already have cleaned pieces;
                // each nulls out as it is released, so this only touches
                // what remains.
                return await actors.OnFrameworkThread(() => Rollback(operation));
            }
            catch (Exception ex)
            {
                // Report the failed dispatch or cleanup; the exception does not
                // establish whether the actor or provider state still exists.
                return ex.Message;
            }
        }

        async Task FailAsync(string failure)
        {
            Step(McdfPhase.RollingBack, filesTotal, bytesTotal, cancellable: false);
            var leftover = await RollbackRegistered();
            // Cleanup may need a redraw before files can be deleted. Use the disposal
            // token for that wait so a user cancellation does not skip cleanup.
            var retention = await teardown.ReleaseRetainedDirectory(actor, flight.Disposal);
            string detail = failure;
            if (leftover != null)
                detail += $" Rollback also failed: {leftover} Reset MCDF retries the cleanup.";
            if (retention != null)
                detail += $" {retention} Reset MCDF retries the cleanup.";
            Finish(detail, success: false);
        }

        try
        {
            // Allocate and register an extraction directory before reading the package.
            // A failed extraction can then leave its directory recorded for cleanup.
            // Package reading and extraction run off the framework thread.
            var allocated = files.CreateOperationDirectory();
            if (!allocated.Success || allocated.Value is not { } operationDirectory)
            {
                await FailAsync(allocated.Detail
                    ?? "The MCDF operation directory could not be allocated.");
                return;
            }
            await actors.OnFrameworkThread(() =>
            {
                operation.OperationDirectory = operationDirectory;
                teardown.Register(operationDirectory);
                return true;
            });
            var read = retained != null
                ? await files.CopyPackage(retained, retainedDirectory!, operationDirectory, cancellation)
                : await files.ReadPackage(path, Limits, operationDirectory, step =>
            {
                filesTotal = step.FilesTotal;
                bytesTotal = step.BytesTotal;
                Step(step.Phase, step.FilesDone, step.BytesDone);
            }, cancellation);
            if (!read.Success || read.Value is not { } package)
            {
                await FailAsync(read.Detail ?? "The package could not be read.");
                return;
            }
            filesTotal = package.FileCount;
            bytesTotal = package.TotalBytes;

            string? bodyJson = null;
            if (package.CustomizePlusData.Length > 0)
            {
                try
                {
                    bodyJson = System.Text.Encoding.UTF8.GetString(
                        Convert.FromBase64String(package.CustomizePlusData));
                }
                catch (FormatException)
                {
                    await FailAsync("The package's Customize+ payload is not valid base64.");
                    return;
                }
            }

            // Check provider availability on the framework thread before applying data.
            Step(McdfPhase.Preparing, filesTotal, bytesTotal);
            var prepared = await actors.OnFrameworkThread(() =>
            {
                if (Guard() is { } stop)
                    return stop;
                var missing = new List<string>();
                if (package.HasResources && !penumbra.Penumbra.Available)
                    missing.Add(penumbra.Penumbra.Detail);
                if (package.GlamourerData.Length > 0 && !glamourer.Glamourer.Available)
                    missing.Add(glamourer.Glamourer.Detail);
                if (package.CustomizePlusData.Length > 0 && !customizePlus.CustomizePlus.Available)
                    missing.Add(customizePlus.CustomizePlus.Detail);
                if (missing.Count > 0)
                    return "This package needs: " + string.Join(" ", missing);

                // Remove a previous MCDF and capture restore values for the new import.
                // Preparation can change the actor while removing the previous MCDF.
                var (baseline, detail) = PrepareImport(operation, package);
                if (detail != null || baseline == null)
                    return detail ?? "The import could not be prepared.";
                operation.Baseline = baseline;
                operation.Prepared = true;
                return null;
            });
            if (prepared != null)
            {
                await FailAsync(prepared);
                return;
            }

            // Finish any redraw required by the previous MCDF before applying this one.
            // If that cleanup fails, keep its resources recorded and stop the import.
            bool priorRetained = await actors.OnFrameworkThread(() =>
                ownership.OverridesFor(actor).Mcdf
                    is { RedrawPending: true, OperationDirectory: not null });
            if (priorRetained)
            {
                Step(McdfPhase.Preparing, filesTotal, bytesTotal);
                var release = await teardown.ReleaseRetainedDirectory(actor, cancellation);
                if (release != null)
                {
                    await FailAsync(
                        "Tearing down the active character file failed: " + release);
                    return;
                }
            }

            // Apply each component and record resources as they are created.
            // Later failures use those records to attempt rollback.
            string? failure = null;

            if (package.HasResources)
            {
                Step(McdfPhase.ApplyingResources, filesTotal, bytesTotal);
                failure = await actors.OnFrameworkThread(() =>
                {
                    if (Guard() is { } stop)
                        return stop;
                    // Check the current collection immediately before assignment.
                    // Reject unrecognized temporary collections so they can be
                    // left under the plugin that created them.
                    var assignment = penumbra.GetCollectionAssignment(actor);
                    if (!assignment.Success || assignment.Value is not { } collectionState)
                        return assignment.Detail ?? "The Penumbra assignment could not be read.";
                    if (ownership.ForeignTemporaryCollectionDetail(
                            actor, ownership.OverridesFor(actor), collectionState) is { } foreign)
                        return foreign;
                    var created = penumbra.CreateTemporaryCollection($"Poser MCDF {fileName}");
                    if (!created.Success)
                        return created.Detail;
                    // Record the collection before assigning it, so rollback can delete it
                    // even when assignment fails.
                    operation.TemporaryCollection = created.Value;
                    var assigned = penumbra.AssignTemporaryCollection(created.Value, actor);
                    if (!assigned.Success)
                        return assigned.Detail;
                    var paths = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var pair in package.ReplacedGamePaths)
                        paths[pair.Key] = pair.Value;
                    foreach (var pair in package.SwappedGamePaths)
                        paths[pair.Key] = pair.Value;
                    var mods = penumbra.AddTemporaryMods(
                        created.Value, paths, package.ManipulationData);
                    return mods.Success ? null : mods.Detail;
                });
            }

            if (failure == null && package.GlamourerData.Length > 0)
            {
                Step(McdfPhase.ApplyingAppearance, filesTotal, bytesTotal);
                failure = await actors.OnFrameworkThread(() =>
                {
                    if (Guard() is { } stop)
                        return stop;
                    var applied = glamourer.HoldGlamourerState(actor, package.GlamourerData);
                    if (applied.Success)
                        operation.GlamourerLocked = true;
                    return applied.Success ? null : applied.Detail;
                });
            }

            if (failure == null && package.HasResources)
            {
                Step(McdfPhase.AwaitingRedraw, filesTotal, bytesTotal);
                var redraw = await penumbra.RedrawAndWait(
                    actor, McdfTeardown.RedrawBarrierTimeout, cancellation);
                if (!redraw.Success)
                    failure = redraw.Detail;
            }

            if (failure == null && operation.TemporaryCollection is { } loadedCollection)
            {
                failure = await actors.OnFrameworkThread(() =>
                {
                    if (Guard() is { } stop)
                        return stop;
                    // Brio releases the load-only assignment after redraw so
                    // subsequent animations resolve against the normal collection.
                    // The loaded draw object still owns references to our files.
                    var released = penumbra.DeleteTemporaryCollection(loadedCollection);
                    if (!released.Success)
                        return released.Detail;
                    operation.TemporaryCollection = null;
                    operation.RedrawPending = true;
                    return null;
                });
            }

            if (failure == null && bodyJson != null)
            {
                Step(McdfPhase.ApplyingBodyProfile, filesTotal, bytesTotal);
                failure = await actors.OnFrameworkThread(() =>
                {
                    if (Guard() is { } stop)
                        return stop;
                    var applied = customizePlus.ApplyTemporaryBodyProfile(actor, bodyJson);
                    if (applied.Success)
                    {
                        operation.TemporaryProfile = applied.Value;
                        operation.BodyJson = bodyJson;
                        return null;
                    }
                    return applied.Detail;
                });
            }

            if (failure != null)
            {
                await FailAsync(failure);
                return;
            }

            // After all components succeed, check cancellation and actor identity again.
            // Transfer resources to persistent MCDF state while retaining the original
            // restore values. Replaced components no longer use their selector state.
            Step(McdfPhase.Committing, filesTotal, bytesTotal, cancellable: false);
            var committed = await actors.OnFrameworkThread(() =>
            {
                if (Guard() is { } stop)
                    return stop;
                // The exact generation must still resolve at the moment of
                // ownership mutation — a despawned or replaced actor rolls
                // back instead of committing onto a stale id.
                if (!actors.IsResolvable(actor))
                    return "The actor is no longer available.";
                var current = ownership.OverridesFor(actor);
                teardown.Commit(package, operation.Session);
                bool replacedGlamourer = package.GlamourerData.Length > 0;
                bool replacedBody = bodyJson != null;
                ownership.Mutate(actor, current with
                {
                    Baseline = operation.Baseline,
                    Mcdf = new McdfOwnership(
                        fileName, operation.TemporaryCollection,
                        operation.OperationDirectory?.Path, operation.GlamourerLocked,
                        operation.TemporaryProfile, operation.BodyJson,
                        ActorName: operation.ActorName,
                        SourcePath: operation.SourcePath,
                        DrawResourcesLoaded: operation.RedrawPending),
                    DesignOwned = !replacedGlamourer && current.DesignOwned,
                    DesignName = replacedGlamourer ? null : current.DesignName,
                    TemporaryBodyProfile = replacedBody ? null : current.TemporaryBodyProfile,
                    BodyProfileName = replacedBody ? null : current.BodyProfileName,
                    BodyProfileJson = replacedBody ? null : current.BodyProfileJson,
                });
                if (ReferenceEquals(_inFlight, operation))
                    _inFlight = null;
                // Publish success in the same framework callback as the commit, before
                // a later Reset callback can change the recorded state.
                Finish($"Imported {fileName}.", success: true);
                return null;
            });
            if (committed != null)
            {
                await FailAsync(committed);
                return;
            }
        }
        catch (Exception ex)
        {
            // The unexpected-exception path rolls back the registered
            // mutations too; it never merely reports.
            var leftover = await RollbackRegistered();
            var retention = await teardown.ReleaseRetainedDirectory(actor, flight.Disposal);
            string detail = $"The import failed unexpectedly: {ex.Message}";
            if (leftover != null)
                detail += $" Rollback also failed: {leftover} Reset MCDF retries the cleanup.";
            if (retention != null)
                detail += $" {retention} Reset MCDF retries the cleanup.";
            Finish(detail, success: false);
        }
    }

    private (IntegrationBaseline? Baseline, string? Detail) PrepareImport(
        McdfOperation operation, McdfPackage package)
    {
        var actor = operation.Target;
        var current = ownership.OverridesFor(actor);
        if (current.Mcdf is { } mcdf)
        {
            var failures = new List<string>();
            bool stillThere = actors.IsResolvable(actor);
            current = teardown.TearDown(actor, current, mcdf, stillThere, failures);
            ownership.Mutate(actor, current);
            if (failures.Count > 0)
                return (null, "Tearing down the active MCDF failed: "
                    + string.Join("; ", failures));
        }

        if (!actors.IsResolvable(actor))
            return (null, "The actor is no longer available.");

        // Capture the character name while the actor exists. Glamourer can use it
        // for cleanup after the game object disappears. A missing name does not
        // block import, but removes that cleanup fallback.
        var named = actors.GetActorName(actor);
        if (named.Success && named.Value is { Length: > 0 } actorName)
            operation.ActorName = actorName;

        // Reject unrecognized temporary collections. Assignment checks again
        // later because the provider state can change between phases.
        if (package.HasResources)
        {
            var assignment = penumbra.GetCollectionAssignment(actor);
            if (!assignment.Success || assignment.Value is not { } collectionState)
                return (null, assignment.Detail ?? "The Penumbra assignment could not be read.");
            if (ownership.ForeignTemporaryCollectionDetail(actor, current, collectionState) is { } foreign)
                return (null, foreign);
        }

        var baseline = current.Baseline;
        if (package.GlamourerData.Length > 0)
        {
            // Save the current appearance for rollback. If no earlier baseline exists,
            // use the same capture for the eventual Reset too.
            var incoming = glamourer.CaptureGlamourerState(actor);
            if (!incoming.Success || incoming.Value is not { } state)
                return (null, incoming.Detail ?? "The incoming Glamourer state could not be captured.");
            operation.WorkingGlamourerState = state;
            if (baseline.GlamourerState == null)
                baseline = baseline with { GlamourerState = state };
        }
        if (package.CustomizePlusData.Length > 0)
        {
            var probe = customizePlus.ProbeBodyProfile(actor);
            if (!probe.Success || probe.Value is not { } bodyState)
                return (null, probe.Detail ?? "The Customize+ state could not be read.");
            if (IntegrationOwnership.ForeignTemporaryBody(current, bodyState))
                return (null, "This actor has a temporary Customize+ profile from another plugin; Poser will not displace it.");
            if (!baseline.BodyProfileCaptured)
                baseline = baseline with
                {
                    SavedBodyProfile = bodyState.ActiveIsSaved ? bodyState.ActiveProfile : null,
                    BodyProfileCaptured = true,
                };
            // The MCDF profile replaces Poser's current temporary profile. Keep its
            // JSON so a failed import can recreate it.
            if (current.TemporaryBodyProfile != null
                && current.BodyProfileJson is { } workingJson)
            {
                operation.ReplacedWorkingBodyProfile = true;
                operation.WorkingBodyProfileJson = workingJson;
            }
        }
        return (baseline, null);
    }

    /// <summary>
    /// Attempts to undo the resources and appearance changes recorded by this import.
    /// Clears each item after successful cleanup. Failed restores and directories
    /// awaiting redraw remain recorded so Reset MCDF can retry. Framework thread only.
    /// </summary>
    private string? Rollback(McdfOperation operation)
    {
        var actor = operation.Target;
        bool resolvable = actors.IsResolvable(actor);
        // Glamourer can retain appearance state after the actor leaves the scene.
        // Use its captured character name when the live actor is no longer available.
        string? byName = resolvable ? null : McdfTeardown.NonEmpty(operation.ActorName);
        // A Penumbra redraw belongs to Penumbra changes only: Glamourer
        // and Customize+ apply their own updates, and Penumbra is
        // legitimately optional for packages without resources — tying a
        // redraw to them would leave a Glamourer-only rollback pending
        // forever when Penumbra is absent.
        bool removedPenumbra = operation.RedrawPending;
        var failures = new List<string>();

        // Restore the pre-import profile only if this import replaced it.
        // Keep its JSON until Customize+ has accepted the replacement.
        if (operation.ReplacedWorkingBodyProfile)
        {
            if (operation.TemporaryProfile != null)
                operation.PendingBodyRecoveryJson = operation.WorkingBodyProfileJson;
            operation.ReplacedWorkingBodyProfile = false;
        }

        var owned = ownership.OverridesFor(actor);
        var body = teardown.ReleaseBodyProfile(actor, owned, operation.TemporaryProfile,
            operation.PendingBodyRecoveryJson, resolvable, failures);
        operation.TemporaryProfile = body.Profile;
        operation.PendingBodyRecoveryJson = body.RestoreJson;
        if (body.Overrides != owned)
            ownership.Mutate(actor, body.Overrides);

        if (operation.GlamourerLocked)
        {
            operation.GlamourerLocked = teardown.ReleaseGlamourerLock(
                actor, resolvable, byName, operation.GlamourerLocked, failures);
            if (!operation.GlamourerLocked)
                operation.PendingGlamourerRecovery = operation.WorkingGlamourerState;
        }

        if (!operation.GlamourerLocked
            && operation.PendingGlamourerRecovery is { } recovery)
        {
            // Restore only after releasing our Glamourer lock. The provider may
            // reapply automation between these calls.
            if (teardown.RestoreEither(actor, resolvable, byName, recovery) is { } restored)
            {
                if (restored.Success)
                    operation.PendingGlamourerRecovery = null;
                else
                    failures.Add(restored.Detail!);
            }
            else
            {
                // Neither the object nor a name is addressable.
                operation.PendingGlamourerRecovery = null;
            }
        }

        if (operation.TemporaryCollection is { } collection)
        {
            var deleted = penumbra.DeleteTemporaryCollection(collection);
            if (deleted.Success)
            {
                operation.TemporaryCollection = null;
                removedPenumbra = true;
            }
            else
            {
                failures.Add(deleted.Detail!);
            }
        }

        // A live actor may still use extracted files after its collection is
        // removed. Retain those files until redraw completes. If the actor no
        // longer resolves, no new redraw wait is added here.
        if (removedPenumbra && resolvable)
            operation.RedrawPending = true;

        if (operation.TemporaryCollection == null
            && !operation.RedrawPending
            && operation.OperationDirectory is { } directory)
        {
            var deletedDirectory = teardown.DeleteOperationDirectory(directory.Path);
            if (deletedDirectory.Success)
                operation.OperationDirectory = null;
            else
                failures.Add(deletedDirectory.Detail!);
        }

        // If unlocking failed, carry the pre-import appearance into later cleanup
        // so Reset can retry restoration after releasing that lock.
        if (operation.GlamourerLocked && operation.PendingGlamourerRecovery == null)
            operation.PendingGlamourerRecovery = operation.WorkingGlamourerState;

        if (ReferenceEquals(_inFlight, operation))
            _inFlight = null;

        bool clean = operation.TemporaryCollection == null
            && !operation.GlamourerLocked
            && operation.TemporaryProfile == null
            && operation.OperationDirectory == null
            && !operation.RedrawPending
            && operation.PendingGlamourerRecovery == null
            && operation.PendingBodyRecoveryJson == null;
        if (clean && failures.Count == 0)
            return null;

        var current = ownership.OverridesFor(actor);
        bool nativeOutstanding = operation.TemporaryCollection != null
            || operation.GlamourerLocked
            || operation.TemporaryProfile != null
            || operation.RedrawPending
            || operation.PendingGlamourerRecovery != null
            || operation.PendingBodyRecoveryJson != null;
        if (!nativeOutstanding)
        {
            // No native cleanup remains. Keep an undeletable extraction directory
            // separately without replacing any existing appearance restore state.
            if (operation.OperationDirectory is { } orphan)
            {
                // Transferred, not dropped: clearing the record makes a
                // second rollback pass idempotent, and the dedupe keeps a
                // repeated transfer from appending the same path twice.
                operation.OperationDirectory = null;
                if (!current.PendingDirectories.Contains(orphan.Path))
                    ownership.Mutate(actor, current with
                    {
                        PendingDirectories =
                            current.PendingDirectories.Append(orphan.Path).ToList(),
                    });
            }
            return failures.Count == 0 ? null : string.Join("; ", failures);
        }

        // Store remaining native cleanup and restore values for Reset MCDF.
        // Only replace the baseline if preparation captured one.
        ownership.Mutate(actor, current with
        {
            Baseline = operation.Prepared ? operation.Baseline : current.Baseline,
            Mcdf = new McdfOwnership(
                operation.FileName,
                operation.TemporaryCollection,
                operation.OperationDirectory?.Path,
                operation.GlamourerLocked,
                operation.TemporaryProfile,
                operation.BodyJson,
                operation.RedrawPending,
                operation.PendingGlamourerRecovery,
                operation.PendingBodyRecoveryJson,
                operation.ActorName,
                operation.SourcePath),
        });
        return failures.Count == 0 ? null : string.Join("; ", failures);
    }
}
