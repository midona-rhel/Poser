using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;
using Poser.Domain.Operations;
using Poser.Domain.Identity;
using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// The single-flight owner of the MCDF workflow: admission (exact session
/// generation, owner-local operation epoch, operation id), the bounded
/// cancel/drain that runs before the integration port is disposed, and the
/// one slot that <see cref="McdfImport"/>, <see cref="McdfExport"/> and
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
/// <see cref="ActorIntegrationSession"/> remains the public compatibility
/// facade and the owner of the per-actor override store; this class and its
/// collaborators mutate that store only through the session's internal seam.
/// </summary>
public sealed class McdfTransaction
{
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
    private readonly SingleFlightOwner<McdfOperation, McdfProgress> _flight = new();
    private readonly McdfTeardown _teardown;
    private readonly McdfImport _import;
    private readonly McdfExport _export;

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
        _teardown = new McdfTeardown(port, files, sessions, owner);
        _import = new McdfImport(port, files, sessions, owner, _teardown, _flight);
        _export = new McdfExport(port, files, owner, _flight);
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

    internal Task CurrentCompletion => _flight.Completion;

    /// <summary>Cooperative cancellation of the running operation.</summary>
    public void Cancel() => _flight.Cancel();

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

    internal IntegrationResult BeginImport(ActorId actor, string path, McdfPackage? retained = null)
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
        if (_flight.Closed || Busy)
            return;
        if (_owner.OverridesFor(actor).Mcdf
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
    internal IntegrationResult BeginExport(ActorId actor, string path, string description)
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
    /// Bounded cancel/drain before the integration port and provider are
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
