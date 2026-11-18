using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Application.Transforms;

namespace Poser.Application.Integration;

/// <summary>
/// Runs one MCDF job at a time: applying one, saving one, removing one, or waiting
/// to delete its extracted files. Each step on the game's main thread first checks
/// that its job and the GPose session are still the current ones. Cancelling and
/// unloading wait only for a limited time; cleanup that could not finish is
/// remembered and tried again later. <see cref="McdfTeardown"/> decides when
/// extracted files are deleted.
/// </summary>
public sealed class McdfTransaction
{
    /// <summary>How long unloading waits for a running job. A job waiting for the
    /// game's main thread cannot finish while unloading holds that thread.</summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a caller such as a scene load waits for a job to cancel
    /// and undo its changes, including waiting for the actor's redraw.</summary>
    public static readonly TimeSpan DrainBound = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);

    private readonly IIntegrationResolutionPort _actors;
    private readonly IMcdfFileBoundary _files;
    private readonly ISessionGenerationSource _sessions;
    private readonly IntegrationOwnership _ownership;
    private readonly SingleFlightOwner<McdfOperation, McdfProgress> _flight = new(
        (operation, progress) => operation.CancelRequested && progress.Cancellable
            ? progress with { Phase = McdfPhase.Cancelling, Cancellable = false }
            : progress);
    private readonly McdfTeardown _teardown;
    private readonly McdfImport _import;
    private readonly McdfExport _export;

    /// <summary>Cancelled once when Poser unloads. It is never disposed, so code
    /// that checks it late cannot crash on it.</summary>
    private readonly CancellationTokenSource _shutdown = new();

    public McdfTransaction(
        IIntegrationResolutionPort actors,
        IPenumbraPort penumbra,
        IGlamourerPort glamourer,
        ICustomizePlusPort customizePlus,
        IMcdfFileBoundary files,
        ISessionGenerationSource sessions,
        IntegrationOwnership ownership,
        Lazy<IPoseSnapshotPort>? poses = null)
    {
        _actors = actors;
        _files = files;
        _sessions = sessions;
        _ownership = ownership;
        _teardown = new McdfTeardown(actors, penumbra, glamourer, customizePlus, files, sessions, ownership);
        _import = new McdfImport(actors, penumbra, glamourer, customizePlus, files, sessions, ownership, _teardown, _flight, poses);
        _export = new McdfExport(penumbra, glamourer, files, ownership, _flight);
    }

    /// <summary>Limits a package must stay within to be applied.</summary>
    public McdfLimits Limits
    {
        get => _import.Limits;
        set => _import.Limits = value;
    }

    /// <summary>Progress of the running MCDF job, or of the last one that
    /// finished; null before the first one.</summary>
    public McdfProgress? Progress => _flight.Progress;

    /// <summary>The receipt of the running MCDF job, or of the last one that
    /// finished. It records which job it was, which GPose session it ran in,
    /// and which actor it was for.</summary>
    public OperationReceipt? Receipt => _flight.Receipt;

    /// <summary>True while an MCDF job runs. Only one runs at a time.</summary>
    public bool Busy => _flight.Busy;

    /// <summary>Finishes when the running job finishes. Wait on this instead of
    /// starting a second redraw of the same actor.</summary>
    public Task CurrentCompletion => _flight.Completion;

    /// <summary>Asks the running job to stop. Its progress shows Cancelling at
    /// once; undoing its changes in the other plugins and deleting its extracted
    /// files can take longer.</summary>
    public void Cancel()
    {
        bool published = _flight.TryReplaceProgress((operation, progress) =>
        {
            if (operation.CancelRequested || progress is not { Cancellable: true } cancellable)
                return null;
            operation.CancelRequested = true;
            return cancellable with { Phase = McdfPhase.Cancelling, Cancellable = false };
        });
        _flight.Cancel();
        if (published)
            _flight.RaiseChanged();
    }

    /// <summary>
    /// Reads a package's header without starting a job, so it works while
    /// another job runs. It waits on the disk, so do not call it on the game's
    /// main thread.
    /// </summary>
    public IntegrationValue<McdfSummary> ReadSummary(string path) =>
        _files.ReadSummary(path);

    public IntegrationValue<McdfLook> ReadLook(string path) => _files.ReadLook(path);

    // ── Starting a job ───────────────────────────────────────────────────

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

    /// <param name="name">The name the MCDF is shown under; the file's own name when null.
    /// A scene passes the name it saved, since it loads the MCDF from a temporary file.</param>
    public IntegrationResult BeginImport(ActorId actor, string path, bool preservePose = true, string? name = null) =>
        BeginImport(actor, path, null, preservePose, name);

    private IntegrationResult BeginImport(ActorId actor, string path, McdfPackage? retained,
        bool preservePose = true, string? name = null)
    {
        if (AdmissionGate() is { } refused)
            return refused;
        if (_sessions.ActiveSessionGeneration is not { } session)
            return IntegrationResult.Fail(
                "No GPose session is active; an MCDF import needs the exact session identity.");
        var retainedDirectory = retained == null ? null : _teardown.Directory(retained.OperationDirectory);
        var operation = Admit(
            actor, name ?? _files.GetFileName(path), McdfOperationKind.Import, session,
            McdfPhase.Reading, out var cancellation);
        operation.SourcePath = path;
        _import.Track(operation);
        _flight.Run(() => _import.Run(operation, path, cancellation, retained, retainedDirectory, preservePose));
        return IntegrationResult.Ok();
    }

    // ── Waiting for a job a caller started ───────────────────────────────

    /// <summary>
    /// Waits for one MCDF job that a caller, such as a scene load, started.
    /// Returns when the job has finished. If the time runs out or the caller is
    /// cancelled, it cancels the job and waits for it to stop
    /// (<see cref="CancelAndDrain"/>) rather than leaving it running. Never wait
    /// on this on the game's main thread.
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
    /// Asks the given job to stop, then waits up to <paramref name="bound"/>.
    /// If the result says the job has not finished, it may still be using its
    /// files, so the caller must keep them.
    /// </summary>
    public async Task<McdfWait> CancelAndDrain(Guid operationId, TimeSpan bound)
    {
        if (_shutdown.IsCancellationRequested)
        {
            // Poser is unloading. The game's main thread is busy unloading, so
            // work sent to it would never run. AbandonWaits has already
            // cancelled the job; return at once and let Drain wait for it.
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
                // The time limit also covers getting onto the game's main
                // thread, and unloading cuts both short.
                await CancelAndJoin().WaitAsync(bound, _shutdown.Token);
            }
            catch (Exception)
            {
                // Timed out: the job is still running, and the result below
                // says so. Cancelled: Poser is unloading. Failed: the job has
                // finished and its receipt says how.
            }
        }
        return TerminalOf(operationId)
            ?? new McdfWait(McdfWaitEnd.Finished, false, Receipt);
    }

    /// <summary>
    /// Finishes once the job has stopped running, at once if it already has.
    /// A caller whose <see cref="McdfWait"/> ended before the job finished
    /// deletes the job's files only after this, never before.
    /// </summary>
    public Task Settled(Guid operationId) =>
        TerminalOf(operationId) is null ? CurrentCompletion : Task.CompletedTask;

    /// <summary>
    /// Called when Poser unloads, before it waits for callers such as a scene
    /// load. From then on every <see cref="CancelAndDrain"/>, including one
    /// already waiting, returns at once: the game's main thread is busy
    /// unloading and would never run its work. The running job is cancelled
    /// directly. Calling this twice is harmless.
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

    /// <summary>Call only on the game's main thread.</summary>
    internal Task CancelIfCurrent(Guid operationId)
    {
        if (Receipt is not { State: OperationReceiptState.Pending } receipt
            || receipt.OperationId != operationId)
            return Task.CompletedTask;
        Cancel();
        return CurrentCompletion;
    }

    /// <summary>The job's final result, or null while it runs. A receipt for a
    /// different job means a newer one started, which can only happen after
    /// this one finished.</summary>
    private McdfWait? TerminalOf(Guid operationId) => Receipt switch
    {
        { } receipt when receipt.OperationId != operationId =>
            new McdfWait(McdfWaitEnd.Finished, true, null),
        { State: not OperationReceiptState.Pending } receipt =>
            new McdfWait(McdfWaitEnd.Finished, true, receipt),
        _ => null,
    };

    // ── Stopping a running import ────────────────────────────────────────

    /// <summary>See <see cref="McdfImport.InvalidateInFlight"/>.</summary>
    internal void InvalidateInFlight() => _import.InvalidateInFlight();

    /// <summary>
    /// Stops an import whose actor is no longer in the scene.
    /// </summary>
    internal void InvalidateIfTargetMissing(HashSet<ActorId> present) =>
        _import.InvalidateIfTargetMissing(present);

    /// <summary>
    /// Stops an import running on this actor and undoes it. Any other job
    /// running on the actor is asked to cancel.
    /// </summary>
    internal void OnResetActor(ActorId actor)
    {
        if (_import.InFlight is { } inFlight && inFlight.Target.Equals(actor))
            _import.InvalidateInFlight();
        else if (Busy && Progress?.Target.Equals(actor) == true)
            Cancel();
    }

    // ── Removing an applied MCDF ─────────────────────────────────────────

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

    /// <summary>Removes everything the applied MCDF added and puts the actor's
    /// Penumbra, Glamourer and Customize+ state back to how it was before. A
    /// collection, design or profile applied separately through Poser stays and
    /// has its own reset. If the extracted files must wait for a redraw before
    /// they can be deleted, that wait runs as the one MCDF job.</summary>
    public IntegrationResult Reset(ActorId actor)
    {
        if (Busy)
            return IntegrationResult.Fail("An MCDF operation is still running.");
        var current = _ownership.OverridesFor(actor);
        if (current.Mcdf is not { } mcdf)
        {
            // No MCDF is applied, but extracted folders that could not be
            // deleted earlier are tried again here.
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
    /// When removing an MCDF left its extracted files waiting for a redraw,
    /// starts the job that waits for the redraw, for a limited time, and then
    /// deletes them. That wait counts as the one running MCDF job. If another
    /// job is already running, the files are kept, and a later Reset MCDF or
    /// the next import tries again.
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
    /// Reads the selected actor's Penumbra, Glamourer and Customize+ state right
    /// away, without changing the actor, then writes the package in the
    /// background. It is turned away when the actor already wears an MCDF (an
    /// MCDF is never packed again), while another job runs, and when another
    /// plugin has locked the actor's Glamourer state.
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

        // The plugin state was read above. Checking, filtering and writing the
        // files happen in the background, after the progress is shown.
        var operation = Admit(
            actor, _files.GetFileName(path), McdfOperationKind.Export, session,
            McdfPhase.CapturingExport, out var cancellation);
        _flight.Run(() => _export.Run(operation, path, description, captured!, cancellation));
        return IntegrationResult.Ok();
    }

    // ── Keeping files for undo ───────────────────────────────────────────

    internal IntegrationValue<Guid> RetainHistory(string directory) =>
        _teardown.RetainHistory(directory);

    internal IntegrationResult RestoreHistory(ActorId actor, Guid resource, string sourcePath) =>
        _teardown.RetainedHistory(resource) is { } package
            ? BeginImport(actor, sourcePath, package)
            : IntegrationResult.Fail("The retained character-file resources are no longer available.");

    internal string? ReleaseHistoryResources() =>
        _teardown.ReleaseHistoryResources(_import.InFlight?.OperationDirectory?.Path);

    // ── Unloading ────────────────────────────────────────────────────────

    /// <summary>
    /// Stops new jobs from starting, cancels the running one, and waits up to
    /// <see cref="DisposeDrainTimeout"/>. The job may still run after that;
    /// cancelling it does not mean its cleanup has finished.
    /// </summary>
    internal void Drain()
    {
        _flight.Close();
        _flight.Drain(DisposeDrainTimeout);
    }
}

/// <summary>Why a caller stopped waiting for its MCDF job.</summary>
public enum McdfWaitEnd
{
    Finished,
    DeadlinePassed,
    ParentCancelled,
}

/// <summary>
/// The result of waiting for an MCDF job. When Terminal is false, the job may not
/// have finished, so its input files must be kept. Receipt can belong to a newer
/// job if the one waited for was replaced.
/// </summary>
public readonly record struct McdfWait(McdfWaitEnd End, bool Terminal, OperationReceipt? Receipt)
{
    /// <summary>The job finished and its changes stayed. This can be true even
    /// after the time ran out: a job that finished on the game's main thread
    /// before the cancel reached it really did apply.</summary>
    public bool Applied => Receipt is { State: OperationReceiptState.Applied };
}
