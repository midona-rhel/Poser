using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Integration;

/// <summary>
/// The MCDF import transaction: read, prepare, apply, commit, and the
/// reverse-order rollback of everything an import registered. Every
/// framework-thread phase re-checks the operation's invalidation flag,
/// cancellation token, and exact session generation before mutating, so a
/// late completion cannot mutate a replacement.
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

    /// <summary>Marks an admitted import as the one invalidation polices.</summary>
    internal void Track(McdfOperation operation) => _inFlight = operation;

    // ── In-flight invalidation ───────────────────────────────────────────

    /// <summary>
    /// Invalidates the in-flight import: the flag flips FIRST so every
    /// queued framework action refuses before mutating, the token cancels
    /// cooperative waits, and only then is the ownership registered so far
    /// rolled back (unresolved pieces become retryable MCDF ownership).
    /// After this, the background task can only finish file cleanup and
    /// reporting. Framework thread only; no blocking wait involved.
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
        // The flag flips BEFORE the token, per the order above: cancelling
        // first would let anything the token releases (a cancellation
        // callback, a port whose parked wait completes from inside Cancel)
        // resume the background task against a record that still reads as
        // live, and its own rollback would then run against the pieces this
        // one is still releasing.
        operation.Invalidated = true;
        flight.Cancel();
        Rollback(operation);
    }

    /// <summary>An in-flight import whose exact target generation left the
    /// scene invalidates NOW — committed ownership is not the only state the
    /// lifecycle must police.</summary>
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

        // Checked at the top of every framework-thread action, immediately
        // before its mutations, and once more before commit. A replaced
        // session generation is an invalidation: the token that admitted
        // this operation no longer exists, so no further mutation may run.
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
                // The framework thread is gone (shutdown teardown); there
                // is nothing left to restore into.
                return ex.Message;
            }
        }

        async Task FailAsync(string failure)
        {
            Step(McdfPhase.RollingBack, filesTotal, bytesTotal, cancellable: false);
            var leftover = await RollbackRegistered();
            // The rollback retains a redraw-pending directory instead of
            // requesting a fire-and-forget redraw; the release barrier runs
            // HERE, in the task, on the disposal token — a user cancel must
            // not skip the wait that lets the files be released safely.
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
            // Phase 1 — the transaction GENERATES and REGISTERS the operation
            // directory before the boundary touches it, so even a read
            // that fails mid-extraction leaves a visible, retryable
            // cleanup obligation instead of an orphaned directory. Then
            // read, validate, extract: pure file work, off the framework
            // thread and entirely off the actor.
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

            // Phase 2 — register the extraction directory and read the
            // content-derived requirements ON the framework thread, per the
            // port contract; anything missing fails before any actor change.
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

                // Phase 3 — tear down a previous MCDF (never stack
                // anonymous temporary resources), revalidate the exact
                // generation, capture the baseline. Refusals happen here,
                // before any mutation.
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

            // The prior MCDF's teardown may have left its extracted
            // directory owned pending a redraw. Release it NOW, behind the
            // bounded exact-actor barrier, before this import stacks new
            // ownership on the actor — a re-import never starts on top of an
            // unreleased predecessor.
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

            // Phases 4/5 — apply. Every mutating action re-guards first and
            // registers its owned id in the same action; any failure or
            // cancellation from here rolls back in reverse order.
            string? failure = null;

            if (package.HasResources)
            {
                Step(McdfPhase.ApplyingResources, filesTotal, bytesTotal);
                failure = await actors.OnFrameworkThread(() =>
                {
                    if (Guard() is { } stop)
                        return stop;
                    // Re-read and classify the effective assignment in the
                    // SAME action that assigns: Empty, installed
                    // collections, ordinary individual assignments, and
                    // Poser's own temporary pass; a foreign temporary
                    // refuses — and because nothing can interleave on the
                    // framework thread, the forced assignment below can
                    // never race into deleting one.
                    var assignment = penumbra.GetCollectionAssignment(actor);
                    if (!assignment.Success || assignment.Value is not { } collectionState)
                        return assignment.Detail ?? "The Penumbra assignment could not be read.";
                    if (ownership.ForeignTemporaryCollectionDetail(
                            actor, ownership.OverridesFor(actor), collectionState) is { } foreign)
                        return foreign;
                    var created = penumbra.CreateTemporaryCollection($"Poser MCDF {fileName}");
                    if (!created.Success)
                        return created.Detail;
                    // Registered BEFORE assignment: a failed assignment
                    // leaves a tracked collection for rollback to delete
                    // (kept owned and retryable when deletion fails too).
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

            // Phase 6 — commit ownership only after every required
            // component succeeded, re-guarded: a cancellation, invalidation,
            // or session replacement landing after the body profile applied
            // rolls BACK here instead of committing success. Components the
            // package replaced drop their per-selector ownership; the
            // ORIGINAL baseline stays.
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
                // The success outcome publishes INSIDE this action: a reset
                // that runs after commit is ordered after this publication
                // on the framework thread, so the background task can never
                // overwrite it with a stale success message.
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

        // Captured HERE, while the actor still resolves: a GPose exit
        // destroys the clone before the teardown runs, and the character
        // name is the only handle Glamourer's identity-scoped locked state
        // can still be released through. A name that cannot be read is not
        // a refusal — the import is still valid, only its post-mortem
        // release loses its fallback, which the teardown reports.
        var named = actors.GetActorName(actor);
        if (named.Success && named.Value is { Length: > 0 } actorName)
            operation.ActorName = actorName;

        // A foreign temporary Penumbra assignment refuses the import
        // before mutation. The later assignment deliberately uses FORCE —
        // required to overlay an ordinary individual assignment — after
        // re-classifying in its own framework action, so it still cannot
        // delete another plugin's temporary assignment.
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
            // ONE live capture serves two roles: the transaction working
            // snapshot rollback returns to (which includes any active
            // Poser design), and — only when nothing was captured yet —
            // the durable baseline Reset restores.
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
            // Applying the MCDF profile DISPLACES an active Poser
            // temporary profile; remember its retained JSON so rollback
            // can put the working recipe back.
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
    /// Reverse-order cleanup of everything an in-flight import REGISTERED.
    /// Framework-thread only and idempotent: each piece nulls out of the
    /// record as it is released, so invalidation and the task's own
    /// failure path can both run it without double-cleaning. A removed
    /// temporary collection leaves the extracted directory owned with a
    /// redraw-pending mark — the release barrier in the task (or a later
    /// Reset MCDF) deletes it only after the exact actor's redraw
    /// completes. Unresolved pieces commit as retryable MCDF ownership;
    /// returns the failure detail, or null when nothing failed.
    /// </summary>
    private string? Rollback(McdfOperation operation)
    {
        var actor = operation.Target;
        bool resolvable = actors.IsResolvable(actor);
        // The captured character name, and only while the object itself is
        // unreachable: Glamourer's state is keyed to the identity, so it is
        // still addressable when the exact generation is not.
        string? byName = resolvable ? null : McdfTeardown.NonEmpty(operation.ActorName);
        // A Penumbra redraw belongs to Penumbra changes only: Glamourer
        // and Customize+ apply their own updates, and Penumbra is
        // legitimately optional for packages without resources — tying a
        // redraw to them would leave a Glamourer-only rollback pending
        // forever when Penumbra is absent.
        bool removedPenumbra = operation.RedrawPending;
        var failures = new List<string>();

        // The displaced working profile's recovery obligation exists the
        // moment displacement is KNOWN — before any deletion attempt — so
        // a failed deletion of the MCDF profile persists BOTH the MCDF
        // profile id and the recovery JSON; a later Reset deletes the
        // profile and then reapplies the working recipe. Displacement only
        // actually happened if the MCDF profile was applied; a rollback
        // that never reached the body phase clears the flag untouched.
        if (operation.ReplacedWorkingBodyProfile)
        {
            if (operation.TemporaryProfile != null)
                operation.PendingBodyRecoveryJson = operation.WorkingBodyProfileJson;
            operation.ReplacedWorkingBodyProfile = false;
        }

        if (operation.TemporaryProfile is { } profile)
        {
            var deleted = customizePlus.DeleteTemporaryBodyProfileById(profile);
            if (deleted.Success)
                operation.TemporaryProfile = null;
            else
                failures.Add(deleted.Detail!);
        }

        if (operation.TemporaryProfile == null
            && operation.PendingBodyRecoveryJson is { } workingJson)
        {
            if (resolvable)
            {
                // Put the working recipe back and record the NEW id
                // Customize+ returns, preserving selector ownership.
                var reapplied = customizePlus.ApplyTemporaryBodyProfile(actor, workingJson);
                if (reapplied.Success && reapplied.Value != default)
                {
                    var owned = ownership.OverridesFor(actor);
                    if (owned.TemporaryBodyProfile != null)
                        ownership.Mutate(
                            actor, owned with { TemporaryBodyProfile = reapplied.Value });
                    operation.PendingBodyRecoveryJson = null;
                }
                else
                {
                    failures.Add(reapplied.Detail
                        ?? "The previous Customize+ profile could not be reapplied.");
                }
            }
            else
            {
                // Nothing left to restore into.
                operation.PendingBodyRecoveryJson = null;
            }
        }

        if (operation.GlamourerLocked && resolvable)
        {
            var unlocked = glamourer.UnlockGlamourerState(actor);
            if (unlocked.Success)
            {
                operation.GlamourerLocked = false;
                // Restoring the WORKING snapshot — the exact pre-import
                // state, including any active Poser design — becomes a
                // tracked obligation, released only on success; the
                // durable baseline stays captured for Reset.
                operation.PendingGlamourerRecovery = operation.WorkingGlamourerState;
            }
            else
            {
                failures.Add(unlocked.Detail!);
            }
        }
        else if (operation.GlamourerLocked)
        {
            // The OBJECT died with the actor, but the lock did not:
            // Glamourer holds it against the character's identity. Forgetting
            // it here would weld the imported look on, so unlock by name and
            // let the recovery below put the working recipe back the same
            // way. The obligation is registered exactly as on the resolvable
            // branch; a failure keeps the lock owned and retryable.
            var unlocked = teardown.ByNameUnlock(byName);
            if (unlocked.Success)
            {
                operation.GlamourerLocked = false;
                operation.PendingGlamourerRecovery = operation.WorkingGlamourerState;
            }
            else
            {
                failures.Add(unlocked.Detail!);
            }
        }

        if (!operation.GlamourerLocked
            && operation.PendingGlamourerRecovery is { } recovery)
        {
            // Between the unlock above and this write, Glamourer's own
            // automation may briefly reassert itself on the character — a
            // frame-scale flicker on a torn-down clone, and the price of
            // never stealing another plugin's lock by writing first.
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

        // Removed Penumbra ownership means the actor's current draw object
        // may still read the extracted payloads until it redraws: mark the
        // directory redraw-pending instead of requesting a fire-and-forget
        // redraw and deleting files the game may still map. The bounded
        // barrier that releases it runs in the owning task (or a later
        // Reset MCDF). An unresolvable actor has no draw object, so its
        // directory releases inline below.
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

        // A leftover that still holds the lock (unlock failed) carries the
        // working snapshot forward as its recovery obligation, so the
        // eventual teardown restores the pre-import recipe, not the
        // durable baseline.
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
            // The import never mutated the actor (a pre-prepare failure);
            // an undeletable extraction directory is a STANDALONE cleanup
            // obligation. Existing ownership — selector baselines, an
            // older MCDF's unresolved teardown — stays untouched.
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

        // Native state is outstanding, which means PrepareImport ran and
        // any previous MCDF teardown completed — current.Mcdf is null here,
        // so this never overwrites an older teardown obligation. The
        // baseline merges only when Prepare actually captured it.
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
