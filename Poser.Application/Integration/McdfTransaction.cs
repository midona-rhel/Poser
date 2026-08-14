using Poser.Application.Lifecycle;
using Poser.Application.Operations;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// The single-flight owner of the MCDF workflow: admission (exact session
/// generation, owner-local operation epoch, operation id), the import
/// transaction phases and reverse-order rollback, read-only export, the
/// teardown of committed MCDF ownership, and the bounded cancel/drain that
/// runs before the integration port is disposed.
///
/// Two rules shape every path here. First, identity: every framework-thread
/// phase re-checks the operation's invalidation flag, cancellation token,
/// and exact session generation before mutating, and terminal
/// progress/receipt publication is refused for anything but the current
/// operation — a late completion can neither mutate a replacement nor
/// overwrite a newer terminal. Second, file lifetime: the extracted payload
/// directory backs the live temporary collection AND the actor's current
/// draw object, so it is released only after the temporary collection is
/// definitely gone and a bounded exact-actor redraw-complete barrier has
/// passed (or the actor itself is gone); a failed barrier retains the
/// directory as retryable ownership evidence instead of deleting files the
/// game may still read.
///
/// <see cref="ActorIntegrationSession"/> remains the public compatibility
/// facade and the owner of the per-actor override store; this class mutates
/// that store only through the session's internal seam.
/// </summary>
public sealed class McdfTransaction
{
    /// <summary>Same bound the import apply phase uses; a redraw that has
    /// not completed within this window is a failure, never an unbounded
    /// wait.</summary>
    private static readonly TimeSpan RedrawBarrierTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Drain bound for disposal. A task parked on a framework hop
    /// cannot finish while disposal holds the framework thread, so the join
    /// is bounded rather than unconditional; the cancellation and
    /// per-phase guards make an abandoned late completion unable to
    /// mutate anything.</summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    private readonly IIntegrationRuntimePort _port;
    private readonly IMcdfFileBoundary _files;
    private readonly ISessionGenerationSource _sessions;
    private readonly ActorIntegrationSession _owner;
    private readonly Dictionary<string, McdfOperationDirectory> _directories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _publishGate = new();

    /// <summary>Cancelled only by <see cref="Drain"/>: post-rollback and
    /// teardown barriers deliberately keep waiting through a user cancel so
    /// the extracted directory still gets released once the redraw
    /// completes.</summary>
    private readonly CancellationTokenSource _disposal = new();

    private McdfProgress? _progress;
    private OperationReceipt? _receipt;
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private Operation? _inFlight;
    private Operation? _current;
    private OperationEpoch _epoch;
    private bool _disposed;

    internal McdfTransaction(
        IIntegrationRuntimePort port,
        IMcdfFileBoundary files,
        ISessionGenerationSource sessions,
        ActorIntegrationSession owner)
    {
        _port = port;
        _files = files;
        _sessions = sessions;
        _owner = owner;
    }

    /// <summary>Hard validation limits for incoming packages.</summary>
    public McdfLimits Limits { get; set; } = McdfLimits.Default;

    /// <summary>Immutable snapshot of the single running (or last finished)
    /// MCDF operation; null before the first one.</summary>
    public McdfProgress? Progress => _progress;

    /// <summary>Immutable receipt of the single running (or last finished)
    /// MCDF operation, carrying the exact operation id, owner-local epoch,
    /// session generation, and target actor generation.</summary>
    public OperationReceipt? Receipt => _receipt;

    /// <summary>Only one MCDF import/export/teardown transaction runs at a
    /// time.</summary>
    public bool Busy => _task is { IsCompleted: false };

    /// <summary>Cooperative cancellation of the running operation.</summary>
    public void Cancel() => _cancellation?.Cancel();

    /// <summary>
    /// The synchronized operation record. Framework-thread confined for
    /// mutation: admission (the UI thread IS the framework thread) creates
    /// it, every phase registers its owned id/state inside the SAME
    /// framework-thread action that performed it, and invalidation and
    /// cleanup run there too — the lifecycle can never race the background
    /// orchestration, which touches the record only from inside
    /// OnFrameworkThread actions.
    /// </summary>
    private sealed class Operation
    {
        public required ActorId Target { get; init; }
        public required string FileName { get; init; }
        public required Guid OperationId { get; init; }
        public required OperationEpoch Epoch { get; init; }
        public required SessionGeneration Session { get; init; }
        public required McdfOperationKind Kind { get; init; }
        public bool Invalidated;
        public bool TerminalPublished;
        public McdfOperationDirectory? OperationDirectory;
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

    // ── Publication (late-completion armor) ──────────────────────────────

    /// <summary>Publishes a non-terminal progress step for the exact
    /// current operation only.</summary>
    private void PublishStep(Operation operation, McdfProgress progress)
    {
        lock (_publishGate)
        {
            if (!ReferenceEquals(_current, operation) || operation.TerminalPublished)
                return;
            _progress = progress;
        }
    }

    /// <summary>Publishes the terminal progress and receipt exactly once,
    /// and only while the operation is still the current one — a late
    /// completion cannot overwrite a newer operation's read models.</summary>
    private void PublishTerminal(
        Operation operation, McdfProgress progress, OperationReceipt receipt)
    {
        lock (_publishGate)
        {
            if (!ReferenceEquals(_current, operation) || operation.TerminalPublished)
                return;
            operation.TerminalPublished = true;
            _progress = progress;
            _receipt = receipt;
        }
    }

    // ── Admission ────────────────────────────────────────────────────────

    private IntegrationResult? AdmissionGate()
    {
        if (_disposed)
            return IntegrationResult.Fail(
                "Poser is shutting down; no new MCDF operation can start.");
        if (Busy)
            return IntegrationResult.Fail("Another MCDF operation is already running.");
        return null;
    }

    private Operation Admit(
        ActorId actor, string fileName, McdfOperationKind kind, SessionGeneration session)
    {
        _cancellation?.Dispose();
        _cancellation = new CancellationTokenSource();
        _epoch = _epoch.IsValid ? _epoch.Next() : OperationEpoch.First;
        var operation = new Operation
        {
            Target = actor,
            FileName = fileName,
            OperationId = Guid.NewGuid(),
            Epoch = _epoch,
            Session = session,
            Kind = kind,
        };
        lock (_publishGate)
        {
            _current = operation;
            _receipt = OperationReceipt.Pending(
                operation.OperationId, operation.Epoch, session, actor);
        }
        return operation;
    }

    internal IntegrationResult BeginImport(ActorId actor, string path)
    {
        if (AdmissionGate() is { } refused)
            return refused;
        if (_sessions.ActiveSessionGeneration is not { } session)
            return IntegrationResult.Fail(
                "No GPose session is active; an MCDF import needs the exact session identity.");
        var operation = Admit(actor, _files.GetFileName(path), McdfOperationKind.Import, session);
        var cancellation = _cancellation!.Token;
        _inFlight = operation;
        _progress = new McdfProgress(
            actor, operation.FileName, McdfOperationKind.Import,
            McdfPhase.Reading, 0, 0, 0, 0, true, null);
        _owner.RaiseChanged();
        _task = Task.Run(
            () => RunImport(operation, path, cancellation), CancellationToken.None);
        return IntegrationResult.Ok();
    }

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
        Cancel();
        if (_inFlight is not { Invalidated: false } operation)
            return;
        operation.Invalidated = true;
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

    /// <summary>A running import for this actor invalidates NOW: queued
    /// framework actions refuse before mutating, the ownership the import
    /// already registered is cleaned here, and the background task is left
    /// with file cleanup and reporting only. A running export is read-only
    /// and merely cancels.</summary>
    internal void OnResetActor(ActorId actor)
    {
        if (_inFlight is { } inFlight && inFlight.Target.Equals(actor))
            InvalidateInFlight();
        else if (Busy && _progress?.Target.Equals(actor) == true)
            Cancel();
    }

    // ── Import ───────────────────────────────────────────────────────────

    private async Task RunImport(
        Operation operation, string path, CancellationToken cancellation)
    {
        var actor = operation.Target;
        string fileName = operation.FileName;
        int filesTotal = 0;
        long bytesTotal = 0;
        const string cancelledDetail = "The import was cancelled.";

        void Step(McdfPhase phase, int filesDone, long bytesDone, bool cancellable = true) =>
            PublishStep(operation, new McdfProgress(
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
            PublishTerminal(operation, progress, receipt);
        }

        // Checked at the top of every framework-thread action, immediately
        // before its mutations, and once more before commit. A replaced
        // session generation is an invalidation: the token that admitted
        // this operation no longer exists, so no further mutation may run.
        string? Guard()
        {
            if (operation.Invalidated || cancellation.IsCancellationRequested)
                return cancelledDetail;
            if (_sessions.ActiveSessionGeneration is not { } live
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
                return await _port.OnFrameworkThread(() => Rollback(operation));
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
            var retention = await ReleaseRetainedDirectory(actor, _disposal.Token);
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
            var allocated = _files.CreateOperationDirectory();
            if (!allocated.Success || allocated.Value is not { } operationDirectory)
            {
                await FailAsync(allocated.Detail
                    ?? "The MCDF operation directory could not be allocated.");
                return;
            }
            await _port.OnFrameworkThread(() =>
            {
                operation.OperationDirectory = operationDirectory;
                _directories[operationDirectory.Path] = operationDirectory;
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

            // The prior MCDF's teardown may have left its extracted
            // directory owned pending a redraw. Release it NOW, behind the
            // bounded exact-actor barrier, before this import stacks new
            // ownership on the actor — a re-import never starts on top of an
            // unreleased predecessor.
            bool priorRetained = await _port.OnFrameworkThread(() =>
                _owner.OverridesFor(actor).Mcdf
                    is { RedrawPending: true, OperationDirectory: not null });
            if (priorRetained)
            {
                Step(McdfPhase.Preparing, filesTotal, bytesTotal);
                var release = await ReleaseRetainedDirectory(actor, cancellation);
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
                    if (_owner.ForeignTemporaryCollectionDetail(
                            _owner.OverridesFor(actor), collectionState) is { } foreign)
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
                    actor, RedrawBarrierTimeout, cancellation);
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
            // component succeeded, re-guarded: a cancellation, invalidation,
            // or session replacement landing after the body profile applied
            // rolls BACK here instead of committing success. Components the
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
                var current = _owner.OverridesFor(actor);
                bool replacedGlamourer = package.GlamourerData.Length > 0;
                bool replacedBody = bodyJson != null;
                _owner.MutateOverrides(actor, current with
                {
                    Baseline = operation.Baseline,
                    Mcdf = new McdfOwnership(
                        fileName, operation.TemporaryCollection,
                        operation.OperationDirectory?.Path, operation.GlamourerLocked,
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
            var retention = await ReleaseRetainedDirectory(actor, _disposal.Token);
            string detail = $"The import failed unexpectedly: {ex.Message}";
            if (leftover != null)
                detail += $" Rollback also failed: {leftover} Reset MCDF retries the cleanup.";
            if (retention != null)
                detail += $" {retention} Reset MCDF retries the cleanup.";
            Finish(detail, success: false);
        }
    }

    private (IntegrationBaseline? Baseline, string? Detail) PrepareImport(
        Operation operation, McdfPackage package)
    {
        var actor = operation.Target;
        var current = _owner.OverridesFor(actor);
        if (current.Mcdf is { } mcdf)
        {
            var failures = new List<string>();
            bool stillThere = _port.IsResolvable(actor);
            current = TearDown(actor, current, mcdf, stillThere, failures);
            _owner.MutateOverrides(actor, current);
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
            if (_owner.ForeignTemporaryCollectionDetail(current, collectionState) is { } foreign)
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
            if (ActorIntegrationSession.ForeignTemporaryBody(current, bodyState))
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
    private string? Rollback(Operation operation)
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
                    var owner = _owner.OverridesFor(actor);
                    if (owner.TemporaryBodyProfile != null)
                        _owner.MutateOverrides(
                            actor, owner with { TemporaryBodyProfile = reapplied.Value });
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
            var deletedDirectory = DeleteOperationDirectory(directory.Path);
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
            _owner.RaiseChanged();
            return null;
        }

        var current = _owner.OverridesFor(actor);
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
                    _owner.MutateOverrides(actor, current with
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
        _owner.MutateOverrides(actor, current with
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
                operation.PendingBodyRecoveryJson),
        });
        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    // ── Teardown of committed ownership ──────────────────────────────────

    /// <summary>
    /// Synchronous teardown stage for committed MCDF ownership: releases
    /// the native pieces in reverse order and restores the captured
    /// baseline/recovery states. The extracted directory is NOT released
    /// here when a removed temporary collection leaves a redraw
    /// outstanding on a live actor — it stays owned with a redraw-pending
    /// mark, and <see cref="ScheduleDirectoryReleaseIfPending"/> (or the
    /// re-importing task) runs the bounded exact-actor barrier that
    /// deletes it. Failures stay owned so Reset MCDF retries.
    /// </summary>
    internal IntegrationOverrides TearDown(
        ActorId actor,
        IntegrationOverrides current,
        McdfOwnership mcdf,
        bool resolvable,
        List<string> failures)
    {
        bool complete = true;
        // A redraw is owed whenever temporary Penumbra ownership was
        // removed — now or, still pending, by an earlier partial teardown.
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

        // Extracted payloads outlive everything that references them: the
        // directory is deleted only once the temporary collection is
        // definitely gone AND — on a live actor with removed Penumbra
        // ownership — only after the exact actor's bounded
        // redraw-complete barrier passes, because the current draw object
        // may still read the files until it rebuilds. Until then the
        // directory stays owned with a redraw-pending mark; the caller
        // schedules the barrier, and a failed barrier keeps the ownership
        // as retryable evidence for Reset MCDF.
        string? operationDirectory = mcdf.OperationDirectory;
        bool redrawPending = false;
        if (temporaryCollection == null && operationDirectory != null)
        {
            if (removedPenumbra && resolvable)
            {
                redrawPending = true;
                complete = false;
            }
            else
            {
                var directoryDeleted = DeleteOperationDirectory(operationDirectory);
                if (directoryDeleted.Success)
                    operationDirectory = null;
                else
                {
                    failures.Add(directoryDeleted.Detail!);
                    complete = false;
                }
            }
        }
        else if (removedPenumbra && resolvable && operationDirectory == null)
        {
            // No files left to guard — the owed redraw is visual only.
            var redraw = _port.RequestRedraw(actor);
            if (!redraw.Success)
            {
                failures.Add($"The redraw request failed: {redraw.Detail}");
                redrawPending = true;
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

    /// <summary>Removes everything the active MCDF created and restores the
    /// complete pre-integration external baseline. Selector-owned
    /// components stay owned and keep their own resets. When the teardown
    /// leaves the extracted directory owned pending a redraw, the bounded
    /// release barrier is scheduled as the one active transaction.</summary>
    internal IntegrationResult Reset(ActorId actor)
    {
        if (Busy)
            return IntegrationResult.Fail("An MCDF operation is still running.");
        var current = _owner.OverridesFor(actor);
        if (current.Mcdf is not { } mcdf)
        {
            // No active MCDF — but standalone pending-directory cleanup
            // obligations still retry from this action.
            if (current.PendingDirectories.Count == 0)
                return IntegrationResult.Ok();
            var cleanupFailures = new List<string>();
            _owner.MutateOverrides(
                actor, RetryPendingDirectories(current, cleanupFailures));
            return cleanupFailures.Count == 0
                ? IntegrationResult.Ok()
                : IntegrationResult.Fail(string.Join("; ", cleanupFailures));
        }

        bool resolvable = _port.IsResolvable(actor);
        var failures = new List<string>();
        current = TearDown(actor, current, mcdf, resolvable, failures);
        current = RetryPendingDirectories(current, failures);
        _owner.MutateOverrides(actor, current);
        ScheduleDirectoryReleaseIfPending(actor);
        return failures.Count == 0
            ? IntegrationResult.Ok()
            : IntegrationResult.Fail(string.Join("; ", failures));
    }

    /// <summary>
    /// Starts the bounded redraw-complete barrier task for a teardown that
    /// left the extracted directory owned pending a redraw. The barrier is
    /// the one active MCDF transaction while it runs; when the slot is
    /// already busy the ownership simply stays retained and a later Reset
    /// MCDF (or the re-importing task) retries the release.
    /// </summary>
    internal void ScheduleDirectoryReleaseIfPending(ActorId actor)
    {
        if (_disposed || Busy)
            return;
        if (_owner.OverridesFor(actor).Mcdf
            is not { RedrawPending: true, OperationDirectory: not null })
            return;
        _task = Task.Run(
            () => ReleaseRetainedDirectory(actor, _disposal.Token),
            CancellationToken.None);
    }

    /// <summary>
    /// The bounded exact-actor redraw-complete barrier that releases a
    /// retained extracted directory. Success — or an actor that no longer
    /// resolves, whose draw object cannot reference the files — deletes
    /// the directory and clears the retained ownership; a failed barrier
    /// on a live actor leaves the ownership untouched as retryable
    /// evidence and returns the detail. Reads and mutates only the
    /// override store's retained state, so repeated runs are idempotent.
    /// </summary>
    private async Task<string?> ReleaseRetainedDirectory(
        ActorId actor, CancellationToken cancellation)
    {
        try
        {
            bool pending = await _port.OnFrameworkThread(() =>
                _owner.OverridesFor(actor).Mcdf
                    is { RedrawPending: true, OperationDirectory: not null });
            if (!pending)
                return null;
            var wait = await _port.RedrawAndWait(actor, RedrawBarrierTimeout, cancellation);
            return await _port.OnFrameworkThread(() =>
            {
                var current = _owner.OverridesFor(actor);
                if (current.Mcdf is not
                    { RedrawPending: true, OperationDirectory: { } path } mcdf)
                    return null;
                if (!wait.Success && _port.IsResolvable(actor))
                    return "The extracted files stay owned until the actor's redraw "
                        + $"completes: {wait.Detail}";
                var deleted = DeleteOperationDirectory(path);
                if (!deleted.Success)
                {
                    // The redraw completed; only the deletion remains, and
                    // Reset MCDF retries it without another barrier.
                    _owner.MutateOverrides(actor, current with
                    {
                        Mcdf = mcdf with { RedrawPending = false },
                    });
                    return deleted.Detail;
                }
                _owner.MutateOverrides(actor, current with
                {
                    Mcdf = Normalize(mcdf with
                    {
                        RedrawPending = false,
                        OperationDirectory = null,
                    }),
                });
                return null;
            });
        }
        catch (Exception ex)
        {
            // The framework thread is gone (shutdown teardown); the
            // retained ownership stays as evidence.
            return ex.Message;
        }
    }

    /// <summary>An ownership record whose every owned piece has been
    /// released is no ownership at all.</summary>
    private static McdfOwnership? Normalize(McdfOwnership mcdf) =>
        mcdf is
        {
            TemporaryCollection: null,
            OperationDirectory: null,
            GlamourerLocked: false,
            TemporaryProfile: null,
            RedrawPending: false,
            PendingGlamourerRecovery: null,
            PendingBodyRecoveryJson: null,
        }
            ? null
            : mcdf;

    /// <summary>Retries deletion of extraction directories orphaned by
    /// pre-mutation import failures; whatever still fails stays owned.</summary>
    private IntegrationPortResult DeleteOperationDirectory(string path)
    {
        if (!_directories.TryGetValue(path, out var ownership))
            return IntegrationPortResult.Fail(
                "The extraction directory ownership proof is unavailable; cleanup was refused.");
        var result = _files.DeleteOperationDirectory(ownership);
        if (result.Success)
            _directories.Remove(path);
        return result;
    }

    internal IntegrationOverrides RetryPendingDirectories(
        IntegrationOverrides current, List<string> failures)
    {
        if (current.PendingDirectories.Count == 0)
            return current;
        var remaining = new List<string>();
        foreach (var directory in current.PendingDirectories)
        {
            var deleted = DeleteOperationDirectory(directory);
            if (!deleted.Success)
            {
                failures.Add(deleted.Detail!);
                remaining.Add(directory);
            }
        }
        return current with { PendingDirectories = remaining };
    }

    // ── Export ───────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the exact selected actor's supported external state
    /// synchronously (read-only — export never changes the actor), then
    /// writes the package off-thread. Refuses while an MCDF is active on
    /// the actor (no repackaging), while another operation runs, and when
    /// the Glamourer state is locked by another plugin.
    /// </summary>
    internal IntegrationResult BeginExport(ActorId actor, string path, string description)
    {
        if (AdmissionGate() is { } refused)
            return refused;
        if (_sessions.ActiveSessionGeneration is not { } session)
            return IntegrationResult.Fail(
                "No GPose session is active; an MCDF export needs the exact session identity.");
        var current = _owner.OverridesFor(actor);
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

        // Every vendor read above is frozen synchronously on the framework
        // thread. Inspection, hashing, semantic filtering, and package
        // writing begin only after the cancellable operation is published.
        var operation = Admit(actor, _files.GetFileName(path), McdfOperationKind.Export, session);
        var cancellation = _cancellation!.Token;
        _progress = new McdfProgress(actor, operation.FileName, McdfOperationKind.Export,
            McdfPhase.CapturingExport, 0, 0, 0, 0, true, null);
        _owner.RaiseChanged();
        _task = Task.Run(
            () => RunExport(
                operation, path, description, glamourerState, customizeData,
                manipulationData, root, tree, cancellation),
            CancellationToken.None);
        return IntegrationResult.Ok();
    }

    private async Task RunExport(
        Operation operation,
        string path,
        string description,
        string glamourerState,
        string customizeData,
        string manipulationData,
        string root,
        IReadOnlyDictionary<string, IReadOnlyList<string>> tree,
        CancellationToken cancellation)
    {
        var actor = operation.Target;
        string fileName = operation.FileName;
        int filesTotal = 0;
        long bytesTotal = 0;
        var skipped = new List<string>();

        void FinishFailure(string detail, int filesDone = 0, long bytesDone = 0)
        {
            bool cancelled = cancellation.IsCancellationRequested;
            var progress = new McdfProgress(
                actor, fileName, McdfOperationKind.Export,
                cancelled ? McdfPhase.Cancelled : McdfPhase.Failed,
                filesDone, filesTotal, bytesDone, bytesTotal, false,
                new McdfOutcome(false, cancelled, detail, 0, 0, skipped));
            var receipt = cancelled
                ? OperationReceipt.Cancelled(
                    operation.OperationId, operation.Epoch, operation.Session, actor, detail)
                : OperationReceipt.Failed(
                    operation.OperationId, operation.Epoch, operation.Session, actor, detail);
            PublishTerminal(operation, progress, receipt);
        }

        try
        {
            var inspected = _files.InspectExportCandidates(root, tree, cancellation);
            if (!inspected.Success || inspected.Value is not { } observation)
            {
                FinishFailure(inspected.Detail
                    ?? "The export resources could not be inspected.");
                return;
            }
            cancellation.ThrowIfCancellationRequested();
            var (content, contentSkipped, contentError) = BuildExportContent(
                description, glamourerState, customizeData, manipulationData, observation);
            skipped = contentSkipped;
            if (contentError != null || content == null)
            {
                FinishFailure(contentError ?? "The export content could not be built.");
                return;
            }
            filesTotal = content.Files.Count;
            var written = await _files.WritePackage(path, content, step =>
            {
                bytesTotal = step.BytesTotal;
                PublishStep(operation, new McdfProgress(
                    actor, fileName, McdfOperationKind.Export,
                    step.Phase, step.FilesDone, step.FilesTotal,
                    step.BytesDone, step.BytesTotal, true, null));
            }, cancellation);
            if (!written.Success || written.Value is not { } stats)
            {
                FinishFailure(written.Detail ?? "The package could not be written.");
                return;
            }
            string detail = skipped.Count == 0
                ? $"Exported {stats.Files} files ({stats.UncompressedBytes:N0} bytes)."
                : $"Exported {stats.Files} files ({stats.UncompressedBytes:N0} bytes); {skipped.Count} resources skipped.";
            PublishTerminal(
                operation,
                new McdfProgress(actor, fileName, McdfOperationKind.Export,
                    McdfPhase.Completed, stats.Files, stats.Files,
                    stats.UncompressedBytes, stats.UncompressedBytes, false,
                    new McdfOutcome(true, false, detail,
                        stats.Files, stats.UncompressedBytes, skipped)),
                OperationReceipt.Applied(
                    operation.OperationId, operation.Epoch, operation.Session, actor, detail));
        }
        catch (OperationCanceledException)
        {
            FinishFailure("The export was cancelled.");
        }
        catch (Exception ex)
        {
            FinishFailure($"The export failed unexpectedly: {ex.Message}");
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
            McdfExportInspection observation)
    {
        var skipped = observation.Skipped.ToList();
        var files = new List<McdfExportFile>();
        var swaps = new Dictionary<string, string>(StringComparer.Ordinal);
        // Which exported source already serves each game path; identical
        // duplicates are ignored, conflicting ones fail the export.
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in observation.Candidates)
        {
            // A local filesystem path is a file replacement; anything else
            // is a game path — identical means unmodified, different means
            // a swap.
            bool isLocalFile = candidate.Kind == McdfExportCandidateKind.LocalFile;
            string actualRaw = candidate.ActualPath;
            var gamePathsRaw = candidate.GamePaths;

            string sourceKey;
            string? localFull = candidate.LocalPath;
            string swapTarget = string.Empty;
            if (isLocalFile)
            {
                if (localFull is null)
                    continue;
                sourceKey = localFull;
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
                files.Add(new McdfExportFile(replaced, localFull!, candidate.Source));
            else
                foreach (var gamePath in replaced)
                    swaps[gamePath] = swapTarget;
        }

        return (new McdfExportContent(
            description, glamourerState, customizeData, manipulationData, files, swaps),
            skipped, null);
    }

    // ── Drain ────────────────────────────────────────────────────────────

    /// <summary>
    /// Bounded cancel/drain before the integration port and provider are
    /// disposed: admission closes permanently, the active operation's token
    /// and the barrier token cancel, and the active task is joined inside
    /// <see cref="DisposeDrainTimeout"/>. The join is bounded because a
    /// task parked on a framework hop cannot complete while disposal holds
    /// the framework thread; an abandoned task cannot mutate anything —
    /// every phase re-guards on the cancelled token, and retained
    /// directory ownership survives as recovery evidence.
    /// </summary>
    internal void Drain()
    {
        _disposed = true;
        _cancellation?.Cancel();
        _disposal.Cancel();
        try
        {
            _task?.Wait(DisposeDrainTimeout);
        }
        catch (AggregateException)
        {
            // A cancelled or faulted task is a completed drain; its
            // failure evidence already published through its own path.
        }
    }
}
