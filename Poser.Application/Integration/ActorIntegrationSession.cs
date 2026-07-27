using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Scene;

namespace Poser.Application.Integration;

/// <summary>
/// Stable-id ownership of Poser-driven EXTERNAL appearance state: the
/// actor-targeted Penumbra collection, Glamourer design, Customize+
/// temporary profile, and the active MCDF bundle. UI owns none of it — no
/// IPC subscriber, object index, file task, cancellation source, extracted
/// path, or restore snapshot ever leaves this session and its port.
///
/// The incoming state of each component is captured once, before Poser's
/// first change to that component, and never overwritten afterwards — so
/// MCDF import over a Poser-applied design still restores the ORIGINAL
/// state. A failed restore keeps the component owned and retryable. An
/// unresolvable actor is dropped without native writes, but Poser-created
/// temporary resources (collection, profile, extracted files) are still
/// deleted by their own ids.
/// </summary>
public sealed class ActorIntegrationSession
{
    private readonly IIntegrationRuntimePort _port;
    private readonly IMcdfFileBoundary _files;
    private readonly Dictionary<ActorId, IntegrationOverrides> _overrides = new();

    public ActorIntegrationSession(IIntegrationRuntimePort port, IMcdfFileBoundary files)
    {
        _port = port;
        _files = files;
    }

    public event Action? Changed;

    public IntegrationAvailability Penumbra => _port.Penumbra;
    public IntegrationAvailability Glamourer => _port.Glamourer;
    public IntegrationAvailability CustomizePlus => _port.CustomizePlus;

    public IntegrationOverrides OverridesFor(ActorId actor) =>
        _overrides.TryGetValue(actor, out var overrides)
            ? overrides
            : IntegrationOverrides.None;

    /// <summary>Loads the pickable lists; each picker calls this on open.</summary>
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListCollections() => _port.GetCollections();
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListDesigns() => _port.GetDesigns();
    public IntegrationValue<IReadOnlyList<ExternalItem>> ListBodyProfiles() => _port.GetBodyProfiles();

    /// <summary>The collection currently affecting the actor (for the
    /// trigger readout).</summary>
    public IntegrationValue<CollectionAssignment> ReadCollection(ActorId actor) =>
        _port.GetCollectionAssignment(actor);

    public IntegrationResult OpenGlamourer(ActorId actor)
    {
        var result = _port.OpenGlamourer(actor);
        return result.Success ? IntegrationResult.Ok() : IntegrationResult.Fail(result.Detail!);
    }

    // ── Selectors ────────────────────────────────────────────────────────

    public IntegrationResult SetCollection(ActorId actor, Guid collection, string name)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var current = OverridesFor(actor);

        var incoming = _port.GetCollectionAssignment(actor);
        if (!incoming.Success || incoming.Value is not { } assignment)
            return IntegrationResult.Fail(incoming.Detail ?? "The incoming collection could not be captured.");
        if (ForeignTemporaryCollection(current, assignment) is { } foreign)
            return IntegrationResult.Fail(foreign);
        var baseline = current.Baseline.Collection ?? new CollectionBaseline(
            assignment.HasIndividualAssignment,
            assignment.HasIndividualAssignment ? assignment.EffectiveId : null);

        var applied = _port.SetIndividualCollection(actor, collection);
        if (!applied.Success)
            return IntegrationResult.Fail(applied.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { Collection = baseline },
            CollectionOwned = true,
            CollectionName = name,
        });
        // Penumbra applies a changed assignment on the next redraw; a
        // failed request is reported, not swallowed — the assignment
        // itself stands and stays owned either way.
        var redraw = _port.RequestRedraw(actor);
        return redraw.Success
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(
                $"The collection was assigned, but the redraw failed: {redraw.Detail}");
    }

    public IntegrationResult ResetCollection(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (!current.CollectionOwned)
            return IntegrationResult.Ok();
        if (current.Baseline.Collection is not { } baseline)
            return IntegrationResult.Fail("No captured collection baseline exists.");

        var restored = _port.RestoreCollection(actor, baseline);
        if (!restored.Success)
            return IntegrationResult.Fail(restored.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { Collection = null },
            CollectionOwned = false,
            CollectionName = null,
        });
        var redraw = _port.RequestRedraw(actor);
        return redraw.Success
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(
                $"The assignment was restored, but the redraw failed: {redraw.Detail}");
    }

    /// <summary>
    /// A non-individual effective collection that is neither in Penumbra's
    /// installed-collection list nor Poser's own temporary collection is a
    /// temporary assignment from another plugin. Nothing displaces it: the
    /// current API cannot capture it for restoration.
    /// </summary>
    private string? ForeignTemporaryCollection(
        IntegrationOverrides current, CollectionAssignment assignment)
    {
        if (assignment.HasIndividualAssignment)
            return null;
        // The Empty collection is excluded from GetCollections by design;
        // Guid.Empty with no individual assignment is a normal state, not
        // a foreign temporary.
        if (assignment.EffectiveId == Guid.Empty)
            return null;
        if (assignment.EffectiveId == current.Mcdf?.TemporaryCollection)
            return null;
        var known = _port.GetCollections();
        if (!known.Success || known.Value is not { } collections)
            return known.Detail ?? "Penumbra's collections could not be listed.";
        return collections.Any(item => item.Id == assignment.EffectiveId)
            ? null
            : "This actor's effective Penumbra collection is a temporary assignment from another plugin; Poser will not displace it.";
    }

    public IntegrationResult ApplyDesign(ActorId actor, Guid design, string name)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var current = OverridesFor(actor);

        var state = current.Baseline.GlamourerState;
        if (state == null)
        {
            // A state locked by another plugin fails HERE, before mutation.
            var incoming = _port.CaptureGlamourerState(actor);
            if (!incoming.Success || incoming.Value is not { } captured)
                return IntegrationResult.Fail(incoming.Detail ?? "The incoming Glamourer state could not be captured.");
            state = captured;
        }

        var applied = _port.ApplyDesign(actor, design);
        if (!applied.Success)
            return IntegrationResult.Fail(applied.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { GlamourerState = state },
            DesignOwned = true,
            DesignName = name,
        });
        return IntegrationResult.Ok();
    }

    public IntegrationResult ResetDesign(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (!current.DesignOwned)
            return IntegrationResult.Ok();
        if (current.Baseline.GlamourerState is not { } state)
            return IntegrationResult.Fail("No captured Glamourer state exists.");

        // Reapply the captured incoming state exactly — not a revert to the
        // game's own appearance.
        var restored = _port.RestoreGlamourerState(actor, state);
        if (!restored.Success)
            return IntegrationResult.Fail(restored.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { GlamourerState = null },
            DesignOwned = false,
            DesignName = null,
        });
        return IntegrationResult.Ok();
    }

    public IntegrationResult SetBodyProfile(ActorId actor, Guid profile, string name)
    {
        if (McdfGate(actor) is { } gate)
            return gate;
        var current = OverridesFor(actor);

        var probe = _port.ProbeBodyProfile(actor);
        if (!probe.Success || probe.Value is not { } bodyState)
            return IntegrationResult.Fail(probe.Detail ?? "The Customize+ state could not be read.");
        if (ForeignTemporary(current, bodyState))
            return IntegrationResult.Fail(
                "This actor has a temporary Customize+ profile from another plugin; Poser will not displace it.");

        var baseline = current.Baseline;
        if (!baseline.BodyProfileCaptured)
            baseline = baseline with
            {
                SavedBodyProfile = bodyState.ActiveIsSaved ? bodyState.ActiveProfile : null,
                BodyProfileCaptured = true,
            };

        var json = _port.GetBodyProfileJson(profile);
        if (!json.Success || json.Value is not { } profileJson)
            return IntegrationResult.Fail(json.Detail ?? "The profile could not be read.");

        var applied = _port.ApplyTemporaryBodyProfile(actor, profileJson);
        if (!applied.Success || applied.Value == default)
            return IntegrationResult.Fail(applied.Detail ?? "The temporary profile could not be applied.");

        Mutate(actor, current with
        {
            Baseline = baseline,
            TemporaryBodyProfile = applied.Value,
            BodyProfileName = name,
            BodyProfileJson = profileJson,
        });
        return IntegrationResult.Ok();
    }

    public IntegrationResult ResetBodyProfile(ActorId actor)
    {
        var current = OverridesFor(actor);
        if (current.TemporaryBodyProfile is not { } owned)
            return IntegrationResult.Ok();

        // Deleting ONLY Poser's temporary profile — by its OWN id — lets
        // the underlying saved assignment resume naturally. Deleting by
        // actor would remove whatever temporary profile is active now,
        // which may belong to another plugin.
        var deleted = _port.DeleteTemporaryBodyProfileById(owned);
        if (!deleted.Success)
            return IntegrationResult.Fail(deleted.Detail!);

        Mutate(actor, current with
        {
            Baseline = current.Baseline with { SavedBodyProfile = null, BodyProfileCaptured = false },
            TemporaryBodyProfile = null,
            BodyProfileName = null,
            BodyProfileJson = null,
        });
        return IntegrationResult.Ok();
    }

    /// <summary>Whether an actor's active temporary profile belongs to a
    /// plugin other than Poser — the state no C+ or MCDF operation may
    /// displace.</summary>
    public IntegrationResult CheckBodyProfileDisplaceable(ActorId actor)
    {
        var probe = _port.ProbeBodyProfile(actor);
        if (!probe.Success || probe.Value is not { } bodyState)
            return IntegrationResult.Fail(probe.Detail ?? "The Customize+ state could not be read.");
        return ForeignTemporary(OverridesFor(actor), bodyState)
            ? IntegrationResult.Fail(
                "This actor has a temporary Customize+ profile from another plugin; Poser will not displace it.")
            : IntegrationResult.Ok();
    }

    private static bool ForeignTemporary(IntegrationOverrides current, BodyProfileProbe probe) =>
        probe.ActiveProfile is { } active
        && !probe.ActiveIsSaved
        && active != current.TemporaryBodyProfile
        && active != current.Mcdf?.TemporaryProfile;

    // ── Reset ────────────────────────────────────────────────────────────

    public IntegrationResult ResetActor(ActorId actor)
    {
        // A running import for this actor invalidates NOW: queued
        // framework actions refuse before mutating, the ownership the
        // import already registered is cleaned here (leftovers land in
        // _overrides and are torn down below), and the background task is
        // left with file cleanup and reporting only. A running export is
        // read-only and merely cancels.
        if (_inFlight is { } inFlight && inFlight.Target.Equals(actor))
            InvalidateInFlight();
        else if (McdfBusy && _mcdfProgress?.Target.Equals(actor) == true)
            CancelMcdf();
        var current = OverridesFor(actor);
        if (!current.HasAny)
            return IntegrationResult.Ok();

        bool resolvable = _port.IsResolvable(actor);
        var failures = new List<string>();
        bool touchedNative = false;

        // MCDF teardown first: it holds the lock and the temporary
        // resources that sit on top of everything else. It requests (and
        // retries) its own redraw.
        if (current.Mcdf is { } mcdf)
        {
            current = TearDownMcdf(actor, current, mcdf, resolvable, failures);
        }

        current = RetryPendingDirectories(current, failures);

        // Body profile: delete only Poser's temporary profile, by its own
        // id — never whichever temporary profile is currently active.
        if (current.TemporaryBodyProfile is { } temporary)
        {
            var deleted = _port.DeleteTemporaryBodyProfileById(temporary);
            if (deleted.Success)
                current = current with
                {
                    Baseline = current.Baseline with
                    {
                        SavedBodyProfile = null,
                        BodyProfileCaptured = false,
                    },
                    TemporaryBodyProfile = null,
                    BodyProfileName = null,
                    BodyProfileJson = null,
                };
            else
                failures.Add(deleted.Detail!);
        }

        // Design / Glamourer state: reapply the captured incoming state.
        if (current.DesignOwned)
        {
            if (!resolvable)
            {
                // No native object to write into; the state died with it.
                current = current with
                {
                    Baseline = current.Baseline with { GlamourerState = null },
                    DesignOwned = false,
                    DesignName = null,
                };
            }
            else if (current.Baseline.GlamourerState is { } state)
            {
                var restored = _port.RestoreGlamourerState(actor, state);
                if (restored.Success)
                    current = current with
                    {
                        Baseline = current.Baseline with { GlamourerState = null },
                        DesignOwned = false,
                        DesignName = null,
                    };
                else
                    failures.Add(restored.Detail!);
            }
        }

        // Collection: restore the assignment-vs-inheritance distinction.
        if (current.CollectionOwned)
        {
            if (!resolvable)
            {
                current = current with
                {
                    Baseline = current.Baseline with { Collection = null },
                    CollectionOwned = false,
                    CollectionName = null,
                };
            }
            else if (current.Baseline.Collection is { } baseline)
            {
                var restored = _port.RestoreCollection(actor, baseline);
                if (restored.Success)
                {
                    touchedNative = true;
                    current = current with
                    {
                        Baseline = current.Baseline with { Collection = null },
                        CollectionOwned = false,
                        CollectionName = null,
                    };
                }
                else
                {
                    failures.Add(restored.Detail!);
                }
            }
        }

        if (touchedNative && resolvable)
        {
            var redraw = _port.RequestRedraw(actor);
            if (!redraw.Success)
                failures.Add($"The redraw request failed: {redraw.Detail}");
        }

        if (current.HasAny)
            _overrides[actor] = current;
        else
            _overrides.Remove(actor);
        Changed?.Invoke();

        return failures.Count == 0
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(string.Join("; ", failures));
    }

    private IntegrationOverrides TearDownMcdf(
        ActorId actor,
        IntegrationOverrides current,
        McdfOwnership mcdf,
        bool resolvable,
        List<string> failures)
    {
        bool complete = true;
        // Redraw whenever temporary Penumbra ownership was removed — now
        // or, still pending, by an earlier partially failed teardown.
        bool removedPenumbra = mcdf.RedrawPending;

        bool locked = mcdf.GlamourerLocked;
        if (locked && resolvable)
        {
            var unlocked = _port.UnlockGlamourerState(actor);
            if (unlocked.Success)
                locked = false;
            else
            {
                failures.Add(unlocked.Detail!);
                complete = false;
            }
        }
        else if (locked)
        {
            // The lock died with the actor's state; nothing left to unlock.
            locked = false;
        }

        // A pending working-recipe recovery (left by a failed import
        // rollback) supersedes the durable-baseline restore: the actor
        // returns to its pre-import recipe, released only on success, and
        // the durable baseline stays captured for the selector resets.
        // Without one, tearing down a committed MCDF reapplies the
        // ORIGINAL captured state as before.
        string? pendingGlamourer = mcdf.PendingGlamourerRecovery;
        if (!locked && pendingGlamourer is { } recovery)
        {
            if (resolvable)
            {
                var recovered = _port.RestoreGlamourerState(actor, recovery);
                if (recovered.Success)
                    pendingGlamourer = null;
                else
                {
                    failures.Add(recovered.Detail!);
                    complete = false;
                }
            }
            else
            {
                pendingGlamourer = null;
            }
        }
        else if (!locked && resolvable && !current.DesignOwned
            && current.Baseline.GlamourerState is { } state)
        {
            var restored = _port.RestoreGlamourerState(actor, state);
            if (restored.Success)
                current = current with
                {
                    Baseline = current.Baseline with { GlamourerState = null },
                };
            else
            {
                failures.Add(restored.Detail!);
                complete = false;
            }
        }

        Guid? temporaryProfile = mcdf.TemporaryProfile;
        if (temporaryProfile is { } profile)
        {
            var deleted = _port.DeleteTemporaryBodyProfileById(profile);
            if (deleted.Success)
                temporaryProfile = null;
            else
            {
                failures.Add(deleted.Detail!);
                complete = false;
            }
        }

        // The displaced working body profile comes back before ownership
        // releases; the new id Customize+ returns lands in the selector's
        // ownership so its Reset stays truthful.
        string? pendingBody = mcdf.PendingBodyRecoveryJson;
        if (temporaryProfile == null && pendingBody is { } bodyRecovery)
        {
            if (resolvable)
            {
                var reapplied = _port.ApplyTemporaryBodyProfile(actor, bodyRecovery);
                if (reapplied.Success && reapplied.Value != default)
                {
                    if (current.TemporaryBodyProfile != null)
                        current = current with { TemporaryBodyProfile = reapplied.Value };
                    pendingBody = null;
                }
                else
                {
                    failures.Add(reapplied.Detail
                        ?? "The previous Customize+ profile could not be reapplied.");
                    complete = false;
                }
            }
            else
            {
                pendingBody = null;
            }
        }

        // The temporary collection deletes by its own id even after the
        // actor is gone, removing Poser's temporary mods and assignment.
        Guid? temporaryCollection = mcdf.TemporaryCollection;
        if (temporaryCollection is { } tempCollection)
        {
            var collectionDeleted = _port.DeleteTemporaryCollection(tempCollection);
            if (collectionDeleted.Success)
            {
                temporaryCollection = null;
                removedPenumbra = true;
            }
            else
            {
                failures.Add(collectionDeleted.Detail!);
                complete = false;
            }
        }

        // Redraw before releasing anything file-backed: a removed
        // temporary collection leaves the removed resources visually
        // cached until the actor redraws. Failure is reported and stays
        // owned as a retryable redraw-pending state.
        bool redrawPending = false;
        if (removedPenumbra && resolvable)
        {
            var redraw = _port.RequestRedraw(actor);
            if (!redraw.Success)
            {
                failures.Add($"The redraw request failed: {redraw.Detail}");
                redrawPending = true;
                complete = false;
            }
        }

        // Extracted payloads outlive everything that references them: the
        // directory is deleted only once the temporary collection is
        // definitely gone, and a failed deletion keeps directory ownership
        // so Reset MCDF retries it.
        string? operationDirectory = mcdf.OperationDirectory;
        if (temporaryCollection == null && operationDirectory != null)
        {
            var directoryDeleted = _files.DeleteOperationDirectory(operationDirectory);
            if (directoryDeleted.Success)
                operationDirectory = null;
            else
            {
                failures.Add(directoryDeleted.Detail!);
                complete = false;
            }
        }

        if (complete)
            return current with { Mcdf = null };

        // Keep only the still-unresolved pieces owned so Reset can retry.
        return current with
        {
            Mcdf = mcdf with
            {
                GlamourerLocked = locked,
                TemporaryProfile = temporaryProfile,
                TemporaryCollection = temporaryCollection,
                OperationDirectory = operationDirectory,
                RedrawPending = redrawPending,
                PendingGlamourerRecovery = pendingGlamourer,
                PendingBodyRecoveryJson = pendingBody,
            },
        };
    }

    public IntegrationResult ResetAll()
    {
        // Invalidation cleans the in-flight import's registered ownership
        // first, so its leftovers join _overrides and reset with the rest.
        InvalidateInFlight();
        var failures = new List<string>();
        foreach (var actor in _overrides.Keys.ToList())
        {
            var result = ResetActor(actor);
            if (!result.Success && result.Detail is { } detail)
                failures.Add($"{actor}: {detail}");
        }
        return failures.Count == 0
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(string.Join(" | ", failures));
    }

    /// <summary>Restores-or-releases every owned actor that left the exact
    /// scene — a replaced generation never receives the old capture.</summary>
    public void Reconcile(SceneSnapshot snapshot)
    {
        var present = new HashSet<ActorId>(snapshot.Actors.Select(actor => actor.Id));
        // An in-flight import whose exact target generation left the scene
        // invalidates NOW — committed ownership is not the only state the
        // lifecycle must police.
        if (_inFlight is { Invalidated: false } inFlight
            && !present.Contains(inFlight.Target))
            InvalidateInFlight();
        foreach (var actor in _overrides.Keys.Where(id => !present.Contains(id)).ToList())
            ResetActor(actor);
    }

    // ── MCDF ─────────────────────────────────────────────────────────────

    /// <summary>Hard validation limits for incoming packages.</summary>
    public McdfLimits Limits { get; set; } = McdfLimits.Default;

    private McdfProgress? _mcdfProgress;
    private CancellationTokenSource? _mcdfCancellation;
    private Task? _mcdfTask;

    /// <summary>Immutable snapshot of the single running (or last finished)
    /// MCDF operation; null before the first one.</summary>
    public McdfProgress? Mcdf => _mcdfProgress;

    /// <summary>Only one MCDF import/export runs at a time.</summary>
    public bool McdfBusy => _mcdfTask is { IsCompleted: false };

    /// <summary>Cooperative cancellation of the running operation.</summary>
    public void CancelMcdf() => _mcdfCancellation?.Cancel();

    /// <summary>
    /// The synchronized in-flight import record. Framework-thread
    /// confined: BeginImport (the UI thread IS the framework thread)
    /// creates it, every mutation registers its owned id/state inside the
    /// SAME framework-thread action that performed it, and invalidation
    /// and cleanup run there too — the lifecycle can never race the
    /// background orchestration, which touches the record only from inside
    /// OnFrameworkThread actions.
    /// </summary>
    private sealed class InFlightImport
    {
        public required ActorId Target { get; init; }
        public required string FileName { get; init; }
        public bool Invalidated;
        public string? OperationDirectory;
        public Guid? TemporaryCollection;
        public bool GlamourerLocked;
        public Guid? TemporaryProfile;
        public string? BodyJson;
        public IntegrationBaseline Baseline = IntegrationBaseline.None;
        // The transaction WORKING snapshot — the live Poser-authored
        // recipe immediately before this import — as opposed to the
        // durable baseline above, which is what Reset restores. Rollback
        // returns the actor to the working recipe.
        public string? WorkingGlamourerState;
        public bool ReplacedWorkingBodyProfile;
        public string? WorkingBodyProfileJson;
        public bool RedrawPending;
        // Working-recipe RECOVERY obligations: set once the imported state
        // displaced the working recipe, released only after the recipe is
        // successfully back. They persist into McdfOwnership so Reset MCDF
        // retries them.
        public string? PendingGlamourerRecovery;
        public string? PendingBodyRecoveryJson;
        // True once PrepareImport captured the merged baseline; an
        // unprepared record's default baseline must never replace the
        // actor's existing one.
        public bool Prepared;
    }

    private InFlightImport? _inFlight;

    /// <summary>
    /// Invalidates the in-flight import: the flag makes every queued
    /// framework action refuse before mutating, the token cancels
    /// cooperative waits, and the ownership registered so far is cleaned
    /// NOW (unresolved pieces become retryable MCDF ownership). After
    /// this, the background task can only finish file cleanup and
    /// reporting. Framework thread only; no blocking wait involved.
    /// </summary>
    private void InvalidateInFlight()
    {
        CancelMcdf();
        if (_inFlight is not { Invalidated: false } operation)
            return;
        operation.Invalidated = true;
        RollbackInFlight(operation);
    }

    public IntegrationResult BeginImport(ActorId actor, string path)
    {
        if (McdfBusy)
            return IntegrationResult.Fail("Another MCDF operation is already running.");
        _mcdfCancellation?.Dispose();
        _mcdfCancellation = new CancellationTokenSource();
        var cancellation = _mcdfCancellation.Token;
        var operation = new InFlightImport
        {
            Target = actor,
            FileName = System.IO.Path.GetFileName(path),
        };
        _inFlight = operation;
        _mcdfProgress = new McdfProgress(
            actor, operation.FileName, McdfOperationKind.Import,
            McdfPhase.Reading, 0, 0, 0, 0, true, null);
        Changed?.Invoke();
        _mcdfTask = Task.Run(
            () => RunImport(operation, path, cancellation), CancellationToken.None);
        return IntegrationResult.Ok();
    }

    private async Task RunImport(
        InFlightImport operation, string path, CancellationToken cancellation)
    {
        var actor = operation.Target;
        string fileName = operation.FileName;
        int filesTotal = 0;
        long bytesTotal = 0;
        const string cancelledDetail = "The import was cancelled.";

        void Step(McdfPhase phase, int filesDone, long bytesDone, bool cancellable = true) =>
            _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Import,
                phase, filesDone, filesTotal, bytesDone, bytesTotal, cancellable, null);
        void Finish(string detail, bool success) =>
            _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Import,
                success ? McdfPhase.Completed
                    : cancellation.IsCancellationRequested || operation.Invalidated
                        ? McdfPhase.Cancelled : McdfPhase.Failed,
                filesTotal, filesTotal, bytesTotal, bytesTotal, false,
                new McdfOutcome(success,
                    !success && (cancellation.IsCancellationRequested || operation.Invalidated),
                    detail, filesTotal, bytesTotal, Array.Empty<string>()));

        // Checked at the top of every framework-thread action, immediately
        // before its mutations, and once more before commit.
        string? Guard() =>
            operation.Invalidated || cancellation.IsCancellationRequested
                ? cancelledDetail
                : null;

        async Task<string?> RollbackRegistered()
        {
            try
            {
                // Idempotent: invalidation may already have cleaned pieces;
                // each nulls out as it is released, so this only touches
                // what remains.
                return await _port.OnFrameworkThread(() => RollbackInFlight(operation));
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
            Finish(leftover == null
                ? failure
                : $"{failure} Rollback also failed: {leftover} Reset MCDF retries the cleanup.",
                success: false);
        }

        try
        {
            // Phase 1 — the session GENERATES and REGISTERS the operation
            // directory before the boundary touches it, so even a read
            // that fails mid-extraction leaves a visible, retryable
            // cleanup obligation instead of an orphaned directory. Then
            // read, validate, extract: pure file work, off the framework
            // thread and entirely off the actor.
            string operationDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "Poser", $"mcdf-{Guid.NewGuid():N}");
            await _port.OnFrameworkThread(() =>
            {
                operation.OperationDirectory = operationDirectory;
                return true;
            });
            var read = await _files.ReadPackage(path, Limits, operationDirectory, step =>
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
            var prepared = await _port.OnFrameworkThread(() =>
            {
                if (Guard() is { } stop)
                    return stop;
                var missing = new List<string>();
                if (package.HasResources && !_port.Penumbra.Available)
                    missing.Add(_port.Penumbra.Detail);
                if (package.GlamourerData.Length > 0 && !_port.Glamourer.Available)
                    missing.Add(_port.Glamourer.Detail);
                if (package.CustomizePlusData.Length > 0 && !_port.CustomizePlus.Available)
                    missing.Add(_port.CustomizePlus.Detail);
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

            // Phases 4/5 — apply. Every mutating action re-guards first and
            // registers its owned id in the same action; any failure or
            // cancellation from here rolls back in reverse order.
            string? failure = null;

            if (package.HasResources)
            {
                Step(McdfPhase.ApplyingResources, filesTotal, bytesTotal);
                failure = await _port.OnFrameworkThread(() =>
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
                    var assignment = _port.GetCollectionAssignment(actor);
                    if (!assignment.Success || assignment.Value is not { } collectionState)
                        return assignment.Detail ?? "The Penumbra assignment could not be read.";
                    if (ForeignTemporaryCollection(OverridesFor(actor), collectionState)
                        is { } foreign)
                        return foreign;
                    var created = _port.CreateTemporaryCollection($"Poser MCDF {fileName}");
                    if (!created.Success)
                        return created.Detail;
                    // Registered BEFORE assignment: a failed assignment
                    // leaves a tracked collection for rollback to delete
                    // (kept owned and retryable when deletion fails too).
                    operation.TemporaryCollection = created.Value;
                    var assigned = _port.AssignTemporaryCollection(created.Value, actor);
                    if (!assigned.Success)
                        return assigned.Detail;
                    var paths = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var pair in package.ReplacedGamePaths)
                        paths[pair.Key] = pair.Value;
                    foreach (var pair in package.SwappedGamePaths)
                        paths[pair.Key] = pair.Value;
                    var mods = _port.AddTemporaryMods(
                        created.Value, paths, package.ManipulationData);
                    return mods.Success ? null : mods.Detail;
                });
            }

            if (failure == null && package.GlamourerData.Length > 0)
            {
                Step(McdfPhase.ApplyingAppearance, filesTotal, bytesTotal);
                failure = await _port.OnFrameworkThread(() =>
                {
                    if (Guard() is { } stop)
                        return stop;
                    var applied = _port.HoldGlamourerState(actor, package.GlamourerData);
                    if (applied.Success)
                        operation.GlamourerLocked = true;
                    return applied.Success ? null : applied.Detail;
                });
            }

            if (failure == null && package.HasResources)
            {
                Step(McdfPhase.AwaitingRedraw, filesTotal, bytesTotal);
                var redraw = await _port.RedrawAndWait(
                    actor, TimeSpan.FromSeconds(10), cancellation);
                if (!redraw.Success)
                    failure = redraw.Detail;
            }

            if (failure == null && bodyJson != null)
            {
                Step(McdfPhase.ApplyingBodyProfile, filesTotal, bytesTotal);
                failure = await _port.OnFrameworkThread(() =>
                {
                    if (Guard() is { } stop)
                        return stop;
                    var applied = _port.ApplyTemporaryBodyProfile(actor, bodyJson);
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
            // component succeeded, re-guarded: a cancellation or
            // invalidation landing after the body profile applied rolls
            // BACK here instead of committing success. Components the
            // package replaced drop their per-selector ownership; the
            // ORIGINAL baseline stays.
            Step(McdfPhase.Committing, filesTotal, bytesTotal, cancellable: false);
            var committed = await _port.OnFrameworkThread(() =>
            {
                if (Guard() is { } stop)
                    return stop;
                // The exact generation must still resolve at the moment of
                // ownership mutation — a despawned or replaced actor rolls
                // back instead of committing onto a stale id.
                if (!_port.IsResolvable(actor))
                    return "The actor is no longer available.";
                var current = OverridesFor(actor);
                bool replacedGlamourer = package.GlamourerData.Length > 0;
                bool replacedBody = bodyJson != null;
                Mutate(actor, current with
                {
                    Baseline = operation.Baseline,
                    Mcdf = new McdfOwnership(
                        fileName, operation.TemporaryCollection,
                        operation.OperationDirectory, operation.GlamourerLocked,
                        operation.TemporaryProfile, operation.BodyJson),
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
            Finish(leftover == null
                ? $"The import failed unexpectedly: {ex.Message}"
                : $"The import failed unexpectedly: {ex.Message} Rollback also failed: {leftover} Reset MCDF retries the cleanup.",
                success: false);
        }
    }

    private (IntegrationBaseline? Baseline, string? Detail) PrepareImport(
        InFlightImport operation, McdfPackage package)
    {
        var actor = operation.Target;
        var current = OverridesFor(actor);
        if (current.Mcdf is { } mcdf)
        {
            var failures = new List<string>();
            bool stillThere = _port.IsResolvable(actor);
            current = TearDownMcdf(actor, current, mcdf, stillThere, failures);
            Mutate(actor, current);
            if (failures.Count > 0)
                return (null, "Tearing down the active MCDF failed: "
                    + string.Join("; ", failures));
        }

        if (!_port.IsResolvable(actor))
            return (null, "The actor is no longer available.");

        // A foreign temporary Penumbra assignment refuses the import
        // before mutation. The later assignment deliberately uses FORCE —
        // required to overlay an ordinary individual assignment — after
        // re-classifying in its own framework action, so it still cannot
        // delete another plugin's temporary assignment.
        if (package.HasResources)
        {
            var assignment = _port.GetCollectionAssignment(actor);
            if (!assignment.Success || assignment.Value is not { } collectionState)
                return (null, assignment.Detail ?? "The Penumbra assignment could not be read.");
            if (ForeignTemporaryCollection(current, collectionState) is { } foreign)
                return (null, foreign);
        }

        var baseline = current.Baseline;
        if (package.GlamourerData.Length > 0)
        {
            // ONE live capture serves two roles: the transaction working
            // snapshot rollback returns to (which includes any active
            // Poser design), and — only when nothing was captured yet —
            // the durable baseline Reset restores.
            var incoming = _port.CaptureGlamourerState(actor);
            if (!incoming.Success || incoming.Value is not { } state)
                return (null, incoming.Detail ?? "The incoming Glamourer state could not be captured.");
            operation.WorkingGlamourerState = state;
            if (baseline.GlamourerState == null)
                baseline = baseline with { GlamourerState = state };
        }
        if (package.CustomizePlusData.Length > 0)
        {
            var probe = _port.ProbeBodyProfile(actor);
            if (!probe.Success || probe.Value is not { } bodyState)
                return (null, probe.Detail ?? "The Customize+ state could not be read.");
            if (ForeignTemporary(current, bodyState))
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
    /// failure path can both run it without double-cleaning. Extracted
    /// payloads are deleted only once no temporary collection references
    /// them. Unresolved pieces commit as retryable MCDF ownership; returns
    /// the failure detail, or null when everything came back.
    /// </summary>
    private string? RollbackInFlight(InFlightImport operation)
    {
        var actor = operation.Target;
        bool resolvable = _port.IsResolvable(actor);
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
            var deleted = _port.DeleteTemporaryBodyProfileById(profile);
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
                var reapplied = _port.ApplyTemporaryBodyProfile(actor, workingJson);
                if (reapplied.Success && reapplied.Value != default)
                {
                    var owner = OverridesFor(actor);
                    if (owner.TemporaryBodyProfile != null)
                        Mutate(actor, owner with { TemporaryBodyProfile = reapplied.Value });
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
            var unlocked = _port.UnlockGlamourerState(actor);
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
            // The lock (and the state to restore into) died with the actor.
            operation.GlamourerLocked = false;
            operation.PendingGlamourerRecovery = null;
        }

        if (!operation.GlamourerLocked
            && operation.PendingGlamourerRecovery is { } recovery)
        {
            if (resolvable)
            {
                var restored = _port.RestoreGlamourerState(actor, recovery);
                if (restored.Success)
                    operation.PendingGlamourerRecovery = null;
                else
                    failures.Add(restored.Detail!);
            }
            else
            {
                operation.PendingGlamourerRecovery = null;
            }
        }

        if (operation.TemporaryCollection is { } collection)
        {
            var deleted = _port.DeleteTemporaryCollection(collection);
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

        if (removedPenumbra && resolvable)
        {
            var redraw = _port.RequestRedraw(actor);
            if (redraw.Success)
            {
                operation.RedrawPending = false;
            }
            else
            {
                failures.Add($"The redraw request failed: {redraw.Detail}");
                operation.RedrawPending = true;
            }
        }

        if (operation.TemporaryCollection == null
            && operation.OperationDirectory is { } directory)
        {
            var deletedDirectory = _files.DeleteOperationDirectory(directory);
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
        {
            Changed?.Invoke();
            return null;
        }

        var current = OverridesFor(actor);
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
                if (!current.PendingDirectories.Contains(orphan))
                    Mutate(actor, current with
                    {
                        PendingDirectories =
                            current.PendingDirectories.Append(orphan).ToList(),
                    });
            }
            return failures.Count == 0 ? null : string.Join("; ", failures);
        }

        // Native state is outstanding, which means PrepareImport ran and
        // any previous MCDF teardown completed — current.Mcdf is null here,
        // so this never overwrites an older teardown obligation. The
        // baseline merges only when Prepare actually captured it.
        Mutate(actor, current with
        {
            Baseline = operation.Prepared ? operation.Baseline : current.Baseline,
            Mcdf = new McdfOwnership(
                operation.FileName,
                operation.TemporaryCollection,
                operation.OperationDirectory,
                operation.GlamourerLocked,
                operation.TemporaryProfile,
                operation.BodyJson,
                operation.RedrawPending,
                operation.PendingGlamourerRecovery,
                operation.PendingBodyRecoveryJson),
        });
        return string.Join("; ", failures);
    }

    /// <summary>Retries deletion of extraction directories orphaned by
    /// pre-mutation import failures; whatever still fails stays owned.</summary>
    private IntegrationOverrides RetryPendingDirectories(
        IntegrationOverrides current, List<string> failures)
    {
        if (current.PendingDirectories.Count == 0)
            return current;
        var remaining = new List<string>();
        foreach (var directory in current.PendingDirectories)
        {
            var deleted = _files.DeleteOperationDirectory(directory);
            if (!deleted.Success)
            {
                failures.Add(deleted.Detail!);
                remaining.Add(directory);
            }
        }
        return current with { PendingDirectories = remaining };
    }

    /// <summary>Removes everything the active MCDF created and restores the
    /// complete pre-integration external baseline. Selector-owned
    /// components stay owned and keep their own resets.</summary>
    public IntegrationResult ResetMcdf(ActorId actor)
    {
        if (McdfBusy)
            return IntegrationResult.Fail("An MCDF operation is still running.");
        var current = OverridesFor(actor);
        if (current.Mcdf is not { } mcdf)
        {
            // No active MCDF — but standalone pending-directory cleanup
            // obligations still retry from this action.
            if (current.PendingDirectories.Count == 0)
                return IntegrationResult.Ok();
            var cleanupFailures = new List<string>();
            Mutate(actor, RetryPendingDirectories(current, cleanupFailures));
            return cleanupFailures.Count == 0
                ? IntegrationResult.Ok()
                : IntegrationResult.Fail(string.Join("; ", cleanupFailures));
        }

        bool resolvable = _port.IsResolvable(actor);
        var failures = new List<string>();
        current = TearDownMcdf(actor, current, mcdf, resolvable, failures);
        current = RetryPendingDirectories(current, failures);
        Mutate(actor, current);
        return failures.Count == 0
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(string.Join("; ", failures));
    }

    // ── MCDF export ──────────────────────────────────────────────────────

    /// <summary>
    /// Captures the exact selected actor's supported external state
    /// synchronously (read-only — export never changes the actor), then
    /// writes the package off-thread. Refuses while an MCDF is active on
    /// the actor (no repackaging), while another operation runs, and when
    /// the Glamourer state is locked by another plugin.
    /// </summary>
    public IntegrationResult BeginExport(ActorId actor, string path, string description)
    {
        if (McdfBusy)
            return IntegrationResult.Fail("Another MCDF operation is already running.");
        var current = OverridesFor(actor);
        if (current.Mcdf != null)
            return IntegrationResult.Fail(
                "This actor is wearing an imported character file; exporting would repackage it. Reset MCDF first.");
        if (!_port.Penumbra.Available)
            return IntegrationResult.Fail(_port.Penumbra.Detail);
        if (!_port.Glamourer.Available)
            return IntegrationResult.Fail(_port.Glamourer.Detail);

        var glamourer = _port.CaptureGlamourerState(actor);
        if (!glamourer.Success || glamourer.Value is not { } glamourerState)
            return IntegrationResult.Fail(
                glamourer.Detail ?? "The Glamourer state could not be captured.");
        var manipulations = _port.GetActorMetaManipulations(actor);
        if (!manipulations.Success || manipulations.Value is not { } manipulationData)
            return IntegrationResult.Fail(
                manipulations.Detail ?? "The meta manipulations could not be captured.");
        var resources = _port.GetActorResourcePaths(actor);
        if (!resources.Success || resources.Value is not { } tree)
            return IntegrationResult.Fail(
                resources.Detail ?? "The actor's resources could not be captured.");
        var modRoot = _port.GetModDirectory();
        if (!modRoot.Success || modRoot.Value is not { } root)
            return IntegrationResult.Fail(
                modRoot.Detail ?? "Penumbra's mod directory could not be read.");

        string customizeData = string.Empty;
        if (_port.CustomizePlus.Available)
        {
            var probe = _port.ProbeBodyProfile(actor);
            if (!probe.Success || probe.Value is not { } bodyState)
                return IntegrationResult.Fail(
                    probe.Detail ?? "The Customize+ state could not be read.");
            if (bodyState.ActiveProfile is { } active)
            {
                if (bodyState.ActiveIsSaved)
                {
                    var json = _port.GetBodyProfileJson(active);
                    if (!json.Success || json.Value is not { } profileJson)
                        return IntegrationResult.Fail(
                            json.Detail ?? "The active profile could not be read.");
                    customizeData = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes(profileJson));
                }
                else if (active == current.TemporaryBodyProfile
                    && current.BodyProfileJson is { } retained)
                {
                    // Poser's own temporary profile exports from the
                    // session's retained JSON.
                    customizeData = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes(retained));
                }
                else
                {
                    return IntegrationResult.Fail(
                        "This actor's body scale is a temporary profile from another plugin that cannot be read back; exporting would silently lose it.");
                }
            }
        }

        var (content, skipped, contentError) = BuildExportContent(
            description, glamourerState, customizeData, manipulationData, tree, root);
        if (contentError != null || content == null)
            return IntegrationResult.Fail(contentError ?? "The export content could not be built.");

        _mcdfCancellation?.Dispose();
        _mcdfCancellation = new CancellationTokenSource();
        var cancellation = _mcdfCancellation.Token;
        string fileName = System.IO.Path.GetFileName(path);
        _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
            McdfPhase.WritingPackage, 0, content.Files.Count, 0, 0, true, null);
        Changed?.Invoke();
        _mcdfTask = Task.Run(
            () => RunExport(actor, path, content, skipped, cancellation),
            CancellationToken.None);
        return IntegrationResult.Ok();
    }

    private async Task RunExport(
        ActorId actor,
        string path,
        McdfExportContent content,
        List<string> skipped,
        CancellationToken cancellation)
    {
        string fileName = System.IO.Path.GetFileName(path);
        int filesTotal = content.Files.Count;
        long bytesTotal = 0;
        try
        {
            var written = await _files.WritePackage(path, content, step =>
            {
                bytesTotal = step.BytesTotal;
                _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
                    step.Phase, step.FilesDone, step.FilesTotal,
                    step.BytesDone, step.BytesTotal, true, null);
            }, cancellation);
            if (!written.Success || written.Value is not { } stats)
            {
                _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
                    cancellation.IsCancellationRequested ? McdfPhase.Cancelled : McdfPhase.Failed,
                    0, filesTotal, 0, bytesTotal, false,
                    new McdfOutcome(false, cancellation.IsCancellationRequested,
                        written.Detail ?? "The package could not be written.",
                        0, 0, skipped));
                return;
            }
            string detail = skipped.Count == 0
                ? $"Exported {stats.Files} files ({stats.UncompressedBytes:N0} bytes)."
                : $"Exported {stats.Files} files ({stats.UncompressedBytes:N0} bytes); {skipped.Count} resources skipped.";
            _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
                McdfPhase.Completed, stats.Files, stats.Files,
                stats.UncompressedBytes, stats.UncompressedBytes, false,
                new McdfOutcome(true, false, detail,
                    stats.Files, stats.UncompressedBytes, skipped));
        }
        catch (Exception ex)
        {
            _mcdfProgress = new McdfProgress(actor, fileName, McdfOperationKind.Export,
                McdfPhase.Failed, 0, filesTotal, 0, bytesTotal, false,
                new McdfOutcome(false, false,
                    $"The export failed unexpectedly: {ex.Message}", 0, 0, skipped));
        }
    }

    /// <summary>
    /// Turns Penumbra's actual-path → game-paths tree into MCDF content:
    /// only real replacements, swap targets validated as game paths and
    /// kept game-path to game-path, allowed extensions only, local files
    /// only from under the CANONICAL Penumbra mod root, Brio's
    /// compatibility filter applied, every skipped or missing resource
    /// reported by name, and conflicting duplicate game-path mappings
    /// rejected outright — a package that lies about a path is worse than
    /// no package.
    /// </summary>
    private static (McdfExportContent? Content, List<string> Skipped, string? Error)
        BuildExportContent(
            string description,
            string glamourerState,
            string customizeData,
            string manipulationData,
            IReadOnlyDictionary<string, IReadOnlyList<string>> resources,
            string modRoot)
    {
        var skipped = new List<string>();
        var files = new List<McdfExportFile>();
        var swaps = new Dictionary<string, string>(StringComparer.Ordinal);
        // Which exported source already serves each game path; identical
        // duplicates are ignored, conflicting ones fail the export.
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        string realRoot;
        try
        {
            var resolvedRoot = ResolveRealPath(System.IO.Path.GetFullPath(modRoot));
            if (resolvedRoot == null)
                return (null, skipped, "Penumbra's mod directory could not be resolved to a real path.");
            realRoot = resolvedRoot;
        }
        catch (Exception ex)
        {
            return (null, skipped, $"Penumbra's mod directory is not a usable path: {ex.Message}");
        }

        foreach (var (actualRaw, gamePathsRaw) in resources)
        {
            // A local filesystem path is a file replacement; anything else
            // is a game path — identical means unmodified, different means
            // a swap.
            bool isLocalFile = actualRaw.Length > 1 && actualRaw[1] == ':';

            string sourceKey;
            string? localFull = null;
            string swapTarget = string.Empty;
            if (isLocalFile)
            {
                try
                {
                    localFull = System.IO.Path.GetFullPath(actualRaw);
                }
                catch (Exception)
                {
                    skipped.Add($"{actualRaw} (not a usable path)");
                    continue;
                }
                if (!System.IO.File.Exists(localFull))
                {
                    skipped.Add($"{actualRaw} (missing on disk)");
                    continue;
                }
                // REAL containment: every reparse point along the path —
                // including intermediate directory junctions/symlinks — is
                // resolved to its final filesystem target, so a file under
                // <root>\junction\… that really lives elsewhere is caught,
                // and lexical tricks like <root>\..\outside never pass.
                string? realFile;
                try
                {
                    realFile = ResolveRealPath(localFull);
                }
                catch (Exception)
                {
                    // Malformed or inaccessible reparse data must become a
                    // skipped resource, never escape the export.
                    realFile = null;
                }
                if (realFile == null)
                {
                    skipped.Add($"{actualRaw} (could not resolve the real path)");
                    continue;
                }
                if (EscapesRoot(System.IO.Path.GetRelativePath(realRoot, realFile)))
                {
                    skipped.Add($"{actualRaw} (outside the Penumbra mod directory)");
                    continue;
                }
                localFull = realFile;
                sourceKey = realFile;
            }
            else
            {
                swapTarget = McdfFormat.NormalizeGamePath(actualRaw);
                if (McdfFormat.ValidateGamePath(swapTarget) != null)
                {
                    skipped.Add($"{actualRaw} (unsupported swap target)");
                    continue;
                }
                sourceKey = swapTarget;
            }

            var replaced = new List<string>();
            foreach (var rawGamePath in gamePathsRaw)
            {
                string gamePath = McdfFormat.NormalizeGamePath(rawGamePath);
                if (!isLocalFile && gamePath == swapTarget)
                    continue; // Unmodified resource, not a replacement.
                if (McdfFormat.ValidateGamePath(gamePath) != null)
                {
                    skipped.Add($"{gamePath} (unsupported resource path)");
                    continue;
                }
                if (!McdfFormat.ExportFilterAllows(gamePath))
                {
                    skipped.Add($"{gamePath} (omitted for MCDF compatibility)");
                    continue;
                }
                if (sources.TryGetValue(gamePath, out var previous))
                {
                    if (string.Equals(previous, sourceKey, StringComparison.OrdinalIgnoreCase))
                        continue; // Identical duplicate mapping.
                    return (null, skipped,
                        $"Penumbra reported conflicting replacements for {gamePath}.");
                }
                sources[gamePath] = sourceKey;
                replaced.Add(gamePath);
            }
            if (replaced.Count == 0)
                continue;

            if (isLocalFile)
                files.Add(new McdfExportFile(replaced, localFull!));
            else
                foreach (var gamePath in replaced)
                    swaps[gamePath] = swapTarget;
        }

        return (new McdfExportContent(
            description, glamourerState, customizeData, manipulationData, files, swaps),
            skipped, null);
    }

    /// <summary>
    /// Resolves the REAL final filesystem path: every reparse point
    /// (symbolic link, junction) the walk encounters — including
    /// intermediate directories — is followed to its final target, and the
    /// walk restarts on the target so ITS ancestors resolve too. Returns
    /// null on a broken link or a link cycle.
    /// </summary>
    private static string? ResolveRealPath(string fullPath)
    {
        var separators = new[]
        {
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar,
        };
        string path = fullPath;
        for (int pass = 0; pass < 8; pass++)
        {
            string? root = System.IO.Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return null;
            string current = root;
            bool jumped = false;
            foreach (var segment in path[root.Length..]
                .Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                current = System.IO.Path.Combine(current, segment);
                System.IO.FileSystemInfo info = System.IO.Directory.Exists(current)
                    ? new System.IO.DirectoryInfo(current)
                    : new System.IO.FileInfo(current);
                if (info.LinkTarget == null)
                    continue;
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved == null)
                    return null;
                // Splice the untraversed remainder onto the resolved
                // target and restart the walk from the top.
                var remainder = path[current.Length..].TrimStart('\\', '/');
                path = remainder.Length == 0
                    ? resolved.FullName
                    : System.IO.Path.Combine(resolved.FullName, remainder);
                jumped = true;
                break;
            }
            if (!jumped)
                return path;
        }
        return null; // Unresolvable nesting depth; treat as a cycle.
    }

    /// <summary>Segment-exact escape test: only a leading ".." SEGMENT (or
    /// a rooted result) escapes — a legitimate directory whose name merely
    /// begins with ".." does not.</summary>
    private static bool EscapesRoot(string relative) =>
        System.IO.Path.IsPathRooted(relative)
        || relative == ".."
        || relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || relative.StartsWith("../", StringComparison.Ordinal);

    private IntegrationResult? McdfGate(ActorId actor)
    {
        if (McdfBusy)
            return IntegrationResult.Fail("An MCDF operation is running; wait for it to finish.");
        return OverridesFor(actor).Mcdf != null
            ? IntegrationResult.Fail(
                "An imported character file owns this actor's external appearance. Reset MCDF first.")
            : null;
    }

    private void Mutate(ActorId actor, IntegrationOverrides updated)
    {
        if (updated.HasAny)
            _overrides[actor] = updated;
        else
            _overrides.Remove(actor);
        Changed?.Invoke();
    }
}
