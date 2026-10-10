using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// The single-flight owner of the MCDF workflow: admission (exact session
/// generation, owner-local operation epoch, operation id), a parent's bounded
/// wait on its own operation, the bounded cancel/drain that runs before the
/// integration ports are disposed, and the one slot that
/// <see cref="McdfImport"/>, <see cref="McdfExport"/> and
/// <see cref="McdfTeardown"/>'s release barrier run in.
///
/// Two rules shape every path. First, identity: every framework-thread
/// phase re-checks the operation's invalidation flag, cancellation token,
/// and exact session generation before mutating, and terminal
/// progress/receipt publication is refused for anything but the current
/// operation — a late completion can neither mutate a replacement nor
/// overwrite a newer terminal. Second, file lifetime: see
/// <see cref="McdfTeardown"/>.
///
/// Committed MCDF ownership lives in <see cref="IntegrationOwnership"/>
/// beside the selectors' state; this class and its collaborators mutate it
/// only through that store.
/// </summary>
public sealed class McdfTransaction
{
    /// <summary>Drain bound for disposal. A task parked on a framework hop
    /// cannot finish while disposal holds the framework thread, so the join
    /// is bounded rather than unconditional; the cancellation and
    /// per-phase guards make an abandoned late completion unable to
    /// mutate anything.</summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>Long enough for a cancelled import's rollback to sit out its
    /// own redraw barrier (10 s) before the parent stops waiting.</summary>
    public static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IIntegrationResolutionPort _actors;
    private readonly IMcdfFileBoundary _files;
    private readonly ISessionGenerationSource _sessions;
    private readonly IntegrationOwnership _ownership;
    private readonly SingleFlightOwner<McdfOperation, McdfProgress> _flight = new();
    private readonly McdfTeardown _teardown;
    private readonly McdfImport _import;
    private readonly McdfExport _export;

    /// <summary>Cancelled once at unload; never disposed, so a late reader
    /// cannot fault on it.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    public McdfTransaction(
        IIntegrationResolutionPort actors,
        IPenumbraPort penumbra,
        IGlamourerPort glamourer,
        ICustomizePlusPort customizePlus,
        IMcdfFileBoundary files,
        ISessionGenerationSource sessions,
        IntegrationOwnership ownership)
    {
        _actors = actors;
        _files = files;
        _sessions = sessions;
        _ownership = ownership;
        _teardown = new McdfTeardown(actors, penumbra, glamourer, customizePlus, files, sessions, ownership);
        _import = new McdfImport(actors, penumbra, glamourer, customizePlus, files, sessions, ownership, _teardown, _flight);
        _export = new McdfExport(penumbra, glamourer, files, ownership, _flight);
    }

    /// <summary>Hard validation limits for incoming packages.</summary>
    public McdfLimits Limits
    {
        get => _import.Limits;
        set => _import.Limits = value;
    }

    /// <summary>Immutable snapshot of the single running (or last finished)
    /// MCDF operation; null before the first one.</summary>
    public McdfProgress? Progress => _flight.Progress;

    /// <summary>Immutable receipt of the single running (or last finished)
    /// MCDF operation, carrying the exact operation id, owner-local epoch,
    /// session generation, and target actor generation.</summary>
    public OperationReceipt? Receipt => _flight.Receipt;

    /// <summary>Only one MCDF import/export/teardown transaction runs at a
    /// time.</summary>
    public bool Busy => _flight.Busy;

    /// <summary>Await the transaction already admitted by import/reset; never
    /// start a competing redraw.</summary>
    public Task CurrentCompletion => _flight.Completion;

    /// <summary>Cooperative cancellation of the running operation.</summary>
    public void Cancel() => _flight.Cancel();

    /// <summary>
    /// What a package says about itself, header only. Deliberately NOT routed
    /// through the single slot: it takes no actor, claims none of the
    /// operation slot, and writes nothing — a highlight must never occupy the
    /// machinery an import needs. Blocking file work; call it off the frame.
    /// </summary>
    public IntegrationValue<McdfSummary> ReadSummary(string path) =>
        _files.ReadSummary(path);

    // ── Admission ────────────────────────────────────────────────────────

    private IntegrationResult? AdmissionGate()
    {
        if (_flight.Closed)
            return IntegrationResult.Fail(
                "Poser is shutting down; no new MCDF operation can start.");
        if (Busy)
            return IntegrationResult.Fail("Another MCDF operation is already running.");
        return null;
    }

    private McdfOperation Admit(
        ActorId actor, string fileName, McdfOperationKind kind, SessionGeneration session,
        McdfPhase firstPhase, out CancellationToken cancellation) =>
        _flight.Admit(
            epoch => new McdfOperation
            {
                Target = actor,
                FileName = fileName,
                OperationId = Guid.NewGuid(),
                Epoch = epoch,
                Session = session,
                Kind = kind,
            },
            new McdfProgress(actor, fileName, kind, firstPhase, 0, 0, 0, 0, true, null),
            out cancellation);

    public IntegrationResult BeginImport(ActorId actor, string path) => BeginImport(actor, path, null);

    private IntegrationResult BeginImport(ActorId actor, string path, McdfPackage? retained)
    {
        if (AdmissionGate() is { } refused)
            return refused;
        if (_sessions.ActiveSessionGeneration is not { } session)
            return IntegrationResult.Fail(
                "No GPose session is active; an MCDF import needs the exact session identity.");
        var retainedDirectory = retained == null ? null : _teardown.Directory(retained.OperationDirectory);
        var operation = Admit(
            actor, _files.GetFileName(path), McdfOperationKind.Import, session,
            McdfPhase.Reading, out var cancellation);
        operation.SourcePath = path;
        _import.Track(operation);
        _flight.Run(() => _import.Run(operation, path, cancellation, retained, retainedDirectory));
        return IntegrationResult.Ok();
    }

    // ── A parent's wait on its own operation ─────────────────────────────

    /// <summary>
    /// A parent's wait on ONE admitted MCDF operation. Returns at its terminal
    /// receipt; on the deadline or the parent's cancellation it cancels and
    /// drains that operation (<see cref="CancelAndDrain"/>) instead of
    /// walking away from it. Never awaited on the framework thread.
    /// </summary>
    public async Task<McdfWait> AwaitOperation(
        Guid operationId, TimeSpan bound, CancellationToken cancellation,
        TimeSpan? drainBound = null)
    {
        McdfWait? done = null;
        McdfWaitEnd end;
        try
        {
            if (await FrameworkPoll.Until(
                    () => Task.FromResult((done = TerminalOf(operationId)) is not null),
                    bound, PollInterval, cancellation))
                return done!.Value;
            end = McdfWaitEnd.DeadlinePassed;
        }
        catch (OperationCanceledException)
        {
            end = McdfWaitEnd.ParentCancelled;
        }
        var drained = await CancelAndDrain(operationId, drainBound ?? DrainBound);
        return drained with { End = end };
    }

    /// <summary>
    /// Cancels the operation only while it is still THIS one — matched by
    /// receipt id on the framework thread, never the shared slot blindly —
    /// then waits up to <paramref name="bound"/> for it to finish. A result
    /// that is not <see cref="McdfWait.Terminal"/> means the child still runs
    /// (cancelled, so every later mutation refuses) and the caller keeps
    /// owning whatever the child reads or writes.
    /// </summary>
    public async Task<McdfWait> CancelAndDrain(Guid operationId, TimeSpan bound)
    {
        if (_shutdown.IsCancellationRequested)
        {
            // Unload: the framework thread is blocked in Dispose, so a hop
            // would never run. AbandonWaits already cancelled the child;
            // answer at once and leave the join to Drain.
            CancelQuietly();
        }
        else
        {
            async Task CancelAndJoin()
            {
                var running = await _actors.OnFrameworkThread(() => CancelIfCurrent(operationId));
                await running;
            }
            try
            {
                // The hop is inside the bound too, and unload cuts both short.
                await CancelAndJoin().WaitAsync(bound, _shutdown.Token);
            }
            catch (Exception)
            {
                // Timeout: still running, reported through the non-terminal
                // result below. Cancelled: unload. Faulted: the child is
                // finished and its receipt says how.
            }
        }
        return TerminalOf(operationId)
            ?? new McdfWait(McdfWaitEnd.Finished, false, Receipt);
    }

    /// <summary>
    /// Completes once the operation has stopped running: at once when its
    /// receipt is terminal, else with the transaction's task. A caller that
    /// got a non-terminal <see cref="McdfWait"/> deletes the files the child
    /// reads or writes on this, never before.
    /// </summary>
    public Task Settled(Guid operationId) =>
        TerminalOf(operationId) is null ? CurrentCompletion : Task.CompletedTask;

    /// <summary>
    /// Unload edge, called on the disposing thread before any parent is
    /// joined. Every later <see cref="CancelAndDrain"/>, and any drain
    /// already waiting, returns at once instead of waiting on a hop the
    /// blocked framework thread can never run; the running child is
    /// cancelled directly. Idempotent.
    /// </summary>
    public void AbandonWaits()
    {
        _shutdown.Cancel();
        CancelQuietly();
    }

    private void CancelQuietly()
    {
        try
        {
            Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation already finished and released its source.
        }
    }

    /// <summary>Framework thread only.</summary>
    internal Task CancelIfCurrent(Guid operationId)
    {
        if (Receipt is not { State: OperationReceiptState.Pending } receipt
            || receipt.OperationId != operationId)
            return Task.CompletedTask;
        Cancel();
        return CurrentCompletion;
    }

    /// <summary>The operation's terminal result, or null while it runs. A
    /// different receipt means a newer operation was admitted, which the
    /// single slot allows only after this one finished.</summary>
    private McdfWait? TerminalOf(Guid operationId) => Receipt switch
    {
        { } receipt when receipt.OperationId != operationId =>
            new McdfWait(McdfWaitEnd.Finished, true, null),
        { State: not OperationReceiptState.Pending } receipt =>
            new McdfWait(McdfWaitEnd.Finished, true, receipt),
        _ => null,
    };

    // ── In-flight invalidation ───────────────────────────────────────────

    /// <summary>See <see cref="McdfImport.InvalidateInFlight"/>.</summary>
    internal void InvalidateInFlight() => _import.InvalidateInFlight();

    /// <summary>An in-flight import whose exact target generation left the
    /// scene invalidates NOW — committed ownership is not the only state the
    /// lifecycle must police.</summary>
    internal void InvalidateIfTargetMissing(HashSet<ActorId> present) =>
        _import.InvalidateIfTargetMissing(present);

    /// <summary>A running import for this actor invalidates NOW: queued
    /// framework actions refuse before mutating, the ownership the import
    /// already registered is cleaned here, and the background task is left
    /// with file cleanup and reporting only. A running export is read-only
    /// and merely cancels.</summary>
    internal void OnResetActor(ActorId actor)
    {
        if (_import.InFlight is { } inFlight && inFlight.Target.Equals(actor))
            _import.InvalidateInFlight();
        else if (Busy && Progress?.Target.Equals(actor) == true)
            Cancel();
    }

    // ── Teardown of committed ownership ──────────────────────────────────

    /// <summary>See <see cref="McdfTeardown.TearDown"/>.</summary>
    internal IntegrationOverrides TearDown(
        ActorId actor,
        IntegrationOverrides current,
        McdfOwnership mcdf,
        bool resolvable,
        List<string> failures) =>
        _teardown.TearDown(actor, current, mcdf, resolvable, failures);

    internal IntegrationOverrides RetryPendingDirectories(
        IntegrationOverrides current, List<string> failures) =>
        _teardown.RetryPendingDirectories(current, failures);

    /// <summary>Removes everything the active MCDF created and restores the
    /// complete pre-integration external baseline. Selector-owned
    /// components stay owned and keep their own resets. When the teardown
    /// leaves the extracted directory owned pending a redraw, the bounded
    /// release barrier is scheduled as the one active transaction.</summary>
    public IntegrationResult Reset(ActorId actor)
    {
        if (Busy)
            return IntegrationResult.Fail("An MCDF operation is still running.");
        var current = _ownership.OverridesFor(actor);
        if (current.Mcdf is not { } mcdf)
        {
            // No active MCDF — but standalone pending-directory cleanup
            // obligations still retry from this action.
            if (current.PendingDirectories.Count == 0)
                return IntegrationResult.Ok();
            var cleanupFailures = new List<string>();
            _ownership.Mutate(
                actor, RetryPendingDirectories(current, cleanupFailures));
            return cleanupFailures.Count == 0
                ? IntegrationResult.Ok()
                : IntegrationResult.Fail(string.Join("; ", cleanupFailures));
        }

        bool resolvable = _actors.IsResolvable(actor);
        var failures = new List<string>();
        current = TearDown(actor, current, mcdf, resolvable, failures);
        current = RetryPendingDirectories(current, failures);
        _ownership.Mutate(actor, current);
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
        if (_flight.Closed || Busy)
            return;
        if (_ownership.OverridesFor(actor).Mcdf
            is not { RedrawPending: true, OperationDirectory: not null })
            return;
        _flight.Run(() => _teardown.ReleaseRetainedDirectory(actor, _flight.Disposal));
    }

    // ── Export ───────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the exact selected actor's supported external state
    /// synchronously (read-only — export never changes the actor), then
    /// writes the package off-thread. Refuses while an MCDF is active on
    /// the actor (no repackaging), while another operation runs, and when
    /// the Glamourer state is locked by another plugin.
    /// </summary>
    public IntegrationResult BeginExport(ActorId actor, string path, string description)
    {
        if (AdmissionGate() is { } refused)
            return refused;
        if (_sessions.ActiveSessionGeneration is not { } session)
            return IntegrationResult.Fail(
                "No GPose session is active; an MCDF export needs the exact session identity.");
        var (refusal, captured) = _export.Capture(actor);
        if (refusal is { } capturing)
            return capturing;

        // Every vendor read above is frozen synchronously on the framework
        // thread. Inspection, hashing, semantic filtering, and package
        // writing begin only after the cancellable operation is published.
        var operation = Admit(
            actor, _files.GetFileName(path), McdfOperationKind.Export, session,
            McdfPhase.CapturingExport, out var cancellation);
        _flight.Run(() => _export.Run(operation, path, description, captured!, cancellation));
        return IntegrationResult.Ok();
    }

    // ── History retention ────────────────────────────────────────────────

    internal IntegrationValue<Guid> RetainHistory(string directory) =>
        _teardown.RetainHistory(directory);

    internal IntegrationResult RestoreHistory(ActorId actor, Guid resource, string sourcePath) =>
        _teardown.RetainedHistory(resource) is { } package
            ? BeginImport(actor, sourcePath, package)
            : IntegrationResult.Fail("The retained character-file resources are no longer available.");

    internal string? ReleaseHistoryResources() =>
        _teardown.ReleaseHistoryResources(_import.InFlight?.OperationDirectory?.Path);

    // ── Drain ────────────────────────────────────────────────────────────

    /// <summary>
    /// Bounded cancel/drain before the integration ports and providers are
    /// disposed: admission closes permanently, the active operation's token
    /// and the barrier token cancel, and the active task is joined inside
    /// <see cref="DisposeDrainTimeout"/>. An abandoned task cannot mutate
    /// anything — every phase re-guards on the cancelled token, and retained
    /// directory ownership survives as recovery evidence.
    /// </summary>
    internal void Drain()
    {
        _flight.Close();
        _flight.Drain(DisposeDrainTimeout);
    }
}

/// <summary>Why a parent stopped waiting on its MCDF child.</summary>
public enum McdfWaitEnd
{
    Finished,
    DeadlinePassed,
    ParentCancelled,
}

/// <summary>
/// What a parent learned about its own MCDF operation. <see cref="Terminal"/>
/// false means the child was cancelled but has not finished within the drain
/// bound: it can no longer commit, yet it may still read its input, so the
/// caller retains that input. <see cref="Receipt"/> is the operation's own
/// receipt, or null when a newer operation has replaced it.
/// </summary>
public readonly record struct McdfWait(McdfWaitEnd End, bool Terminal, OperationReceipt? Receipt)
{
    /// <summary>The child committed. Possible after the deadline too: a
    /// commit ordered before the matched cancel on the framework thread is
    /// a real, owned result, not a late write.</summary>
    public bool Applied => Receipt is { State: OperationReceiptState.Applied };
}
