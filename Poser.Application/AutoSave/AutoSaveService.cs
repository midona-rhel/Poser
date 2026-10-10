using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Poser.Config;
using Poser.Application.AutoSave;
using CapturedPose = Poser.Files.NamedAutoSavePose;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Pose autosave cadence, admission and the exit/final-capture state machine.
/// Disk writes belong to <see cref="AutoSaveWriter"/>; the health record
/// belongs to <see cref="AutoSaveHealthLedger"/>.
/// </summary>
public class AutoSaveService : IAutoSaveService
{
    private readonly Action<string> _error;
    private readonly Action<string> _debug;
    private readonly IPoseAutoSaveCapture _capture;
    private readonly PoseAutoSaveStore _store;
    private readonly ConfigurationService _configuration;
    private readonly Func<DateTime> _clock;
    private readonly AutoSaveHealthLedger _health;
    private readonly AutoSaveWriter _writer;

    private DateTime? _nextDueUtc;
    private bool _disposed;

    // Guards the exit state below and every Locked member of _writer, so an
    // admission check and the queue mutation it permits are one atomic step.
    private readonly object _queueGate = new();
    private bool _exitReserved;
    private bool _exitCompleted;
    private bool _cleanOnExit;
    private bool _finalCaptureStarted;
    private AutoSaveCaptureResult _finalCapture;
    private bool _hasFinalCapture;
    private bool _wasGPosing;
    private bool _exitCompletedWhileDisabled;
    private bool _sessionReopenedAfterCompletedExit;

    public string RootDirectory { get; }

    public AutoSaveTerminalResult LastTerminalResult
    {
        get
        {
            lock (_queueGate)
                return _writer.TerminalResult;
        }
    }

    public AutoSaveHealthRecord? LastHealthRecord => _health.LastRecord;

    public AutoSaveService(
        IPoseAutoSaveCapture capture,
        ConfigurationService configuration,
        PoseAutoSaveStore store,
        Action<string> error,
        Action<string> debug,
        Func<DateTime>? utcClock = null,
        Func<Action, bool>? dispatch = null,
        AutoSaveHealthStore? healthStore = null)
    {
        _capture = capture;
        _store = store;
        _error = error;
        _debug = debug;
        _configuration = configuration;
        _clock = utcClock ?? (() => DateTime.UtcNow);
        RootDirectory = store.RootDirectory;
        _health = new AutoSaveHealthLedger(
            healthStore ?? new AutoSaveHealthStore(RootDirectory), error);
        _writer = new AutoSaveWriter(
            _queueGate,
            store,
            _health,
            dispatch ?? (work =>
            {
                _ = Task.Run(work);
                return true;
            }),
            error);
    }

    private AutoSaveConfiguration Settings => _configuration.Config.AutoSave;

    private static string HealthFailureDetail(
        AutoSaveSnapshotJob job,
        string? detail) =>
        $"operation {job.OperationId} ({job.Reason}) HealthTransition: {detail}";

    private AutoSaveHealthWriteResult PublishCancelled(
        AutoSaveSnapshotJob job,
        string detail,
        string phase) =>
        _health.Publish(AutoSaveHealthRecord.Create(
            job.OperationId,
            job.Reason,
            AutoSaveHealthStatus.Cancelled,
            job.NowUtc,
            DateTime.UtcNow,
            intendedActors: job.Captured.Count,
            detail: detail,
            failurePhase: phase),
            job.HealthGeneration,
            retainFailure: true);

    private AutoSaveHealthWriteResult PublishRecovery(
        AutoSaveSnapshotJob job,
        string phase,
        string detail) =>
        _health.Publish(AutoSaveHealthRecord.Create(
            job.OperationId,
            job.Reason,
            AutoSaveHealthStatus.RecoveryRequired,
            job.NowUtc,
            DateTime.UtcNow,
            intendedActors: job.Captured.Count,
            detail: detail,
            failurePhase: phase),
            job.HealthGeneration,
            retainFailure: true);

    private void ResetCompletedExitForNewSession()
    {
        lock (_queueGate)
        {
            if (!_exitCompleted || _writer.IsRunningLocked)
                return;

            _exitReserved = false;
            _exitCompleted = false;
            _cleanOnExit = false;
            _finalCaptureStarted = false;
            _finalCapture = default;
            _hasFinalCapture = false;
            _exitCompletedWhileDisabled = false;
            _writer.Failure = null;
            _writer.TerminalResult = AutoSaveTerminalResult.PendingResult;
            _nextDueUtc = null;
        }
    }

    /// <summary>
    /// Interval logic. Idle (not enabled, or not in GPose) disarms the timer;
    /// the first tick after arming schedules one full interval out, so entering
    /// GPose never saves immediately (parity with both references).
    /// </summary>
    public void Tick(DateTime nowUtc, bool isGposing)
    {
        var settings = Settings;
        if (isGposing && !_wasGPosing)
        {
            var reopenedAfterCompletedExit = false;
            lock (_queueGate)
                reopenedAfterCompletedExit = _exitCompleted && !_writer.IsRunningLocked;

            ResetCompletedExitForNewSession();
            if (reopenedAfterCompletedExit)
                _sessionReopenedAfterCompletedExit = true;
        }
        else if (isGposing && settings.Enabled && _exitCompletedWhileDisabled &&
            !_sessionReopenedAfterCompletedExit)
        {
            // A direct disabled exit can arrive before the framework reports the
            // false GPose edge. Preserve the legacy re-enable boundary in that
            // case, while a genuinely re-entered session remains idempotent.
            ResetCompletedExitForNewSession();
        }
        _wasGPosing = isGposing;

        if (!settings.Enabled || !isGposing)
        {
            _nextDueUtc = null;
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.IntervalSeconds));

        if (_nextDueUtc is null)
        {
            _nextDueUtc = nowUtc + interval;
            return;
        }

        if (nowUtc < _nextDueUtc.Value)
            return;

        _nextDueUtc = nowUtc + interval;
        SaveNow("interval");
    }

    /// <summary>
    /// Reserves exactly one final-capture attempt for this exit edge. The
    /// reservation is independent of an active periodic write; the immutable
    /// final job waits behind that write. A duplicate call returns the original
    /// compatibility result without recapturing live state.
    /// </summary>
    public AutoSaveCaptureResult CaptureForExit()
    {
        AutoSaveSnapshotJob? cancelled = null;
        lock (_queueGate)
        {
            if (_hasFinalCapture)
                return _finalCapture;
            if (_finalCaptureStarted)
                return AutoSaveCaptureResult.NotCaptured(
                    "Final auto-save capture is already in progress.");

            _finalCaptureStarted = true;
            _exitReserved = true;
            _cleanOnExit = Settings.Enabled && Settings.CleanOnExit;
            _nextDueUtc = null;
            cancelled = _writer.TakePeriodicLocked();
        }

        if (cancelled is { } cancelledJob)
        {
            var cancelledHealth = PublishCancelled(
                cancelledJob,
                "Periodic autosave was coalesced by final reservation.",
                "Admission");
            if (!cancelledHealth.Succeeded)
            {
                lock (_queueGate)
                    _writer.Failure ??= HealthFailureDetail(cancelledJob, cancelledHealth.Detail);
            }
        }

        var settings = Settings;
        AutoSaveCaptureResult result;
        if (!settings.Enabled)
        {
            result = AutoSaveCaptureResult.NotCaptured("Auto-save is disabled.");
        }
        else if (settings.CleanOnExit)
        {
            result = AutoSaveCaptureResult.NotCaptured(
                "Clean-on-exit is enabled; cleanup is pending.");
        }
        else
        {
            result = CaptureAndDispatch("gpose-exit", isFinal: true);
        }

        lock (_queueGate)
        {
            _finalCapture = result;
            _hasFinalCapture = true;
            _exitCompletedWhileDisabled = !settings.Enabled;
            _writer.TerminalResult = AutoSaveTerminalResult.PendingResult;
        }

        // Clean-on-exit has no final pose reservation, but direct callers still
        // receive the historical synchronous cleanup behavior. The lifecycle
        // port calls CompleteForExit again, which is idempotent.
        if (!settings.Enabled || _cleanOnExit)
            CompleteForExit();

        return result;
    }

    /// <summary>
    /// Takes a periodic snapshot immediately, regardless of the interval. The
    /// interval tick and tests drive this; it is not part of the service port.
    /// Returns the number of actors CAPTURED, not the number of files that
    /// landed: the writes outlive this call. Zero therefore also covers
    /// "nothing had authored edits" and "a periodic item was coalesced into the
    /// bounded pending slot", both of which may produce zero.
    /// </summary>
    internal int SaveNow(string reason) =>
        CaptureAndDispatch(reason, isFinal: false).CapturedActors;

    private AutoSaveCaptureResult CaptureAndDispatch(string reason, bool isFinal)
    {
        var startupFailure = _health.StartupFailure;
        lock (_queueGate)
        {
            if (_disposed || startupFailure is not null || (_exitReserved && !isFinal))
                return AutoSaveCaptureResult.NotCaptured(
                    startupFailure ?? "Auto-save admission is closed.");
        }

        var dispatchAccepted = false;
        try
        {
            var detached = _capture.Capture(reason);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var captured = detached.Poses.Select(pose => new CapturedPose(pose.ActorName,
                PoseAutoSaveStore.UniqueFileName(pose.ActorName, used) + ".pose", pose.Pose)).ToList();
            var captureFailure = detached.Failure;

            if (captured.Count == 0)
            {
                if (captureFailure != null)
                {
                    return AutoSaveCaptureResult.Failure(
                        $"Auto-save ({reason}) could not capture an actor: {captureFailure}");
                }

                _debug($"Auto-save ({reason}): no actors with authored edits, skipping");
                return AutoSaveCaptureResult.NotCaptured(
                    "No actors had authored edits.");
            }

            // Read on this thread so the worker never touches configuration.
            var keep = Math.Max(1, Settings.MaxAutoSaves);
            var nowUtc = _clock();

            var job = new AutoSaveSnapshotJob(
                Guid.NewGuid().ToString("N"), reason, nowUtc, keep, captured, isFinal, 0);

            // A pending periodic item is canceled before the replacement's
            // Queued record is admitted.  This keeps the single health file's
            // current record ordered with the bounded queue and ensures a
            // failed cancellation transition remains actionable instead of
            // being hidden by the newer admission.
            AutoSaveSnapshotJob? displaced = null;
            string? admissionFailure = null;
            if (!isFinal)
            {
                lock (_queueGate)
                {
                    if (_disposed || startupFailure is not null || _exitReserved)
                        admissionFailure = startupFailure ?? "Auto-save admission is closed.";
                    else
                        displaced = _writer.TakePeriodicLocked();
                }

                if (admissionFailure is not null)
                    return AutoSaveCaptureResult.Failure(
                        $"Auto-save ({reason}) was not admitted: {admissionFailure}",
                        captured.Count);

                if (displaced is { } displacedJob)
                {
                    var cancelled = PublishCancelled(
                        displacedJob,
                        "Periodic autosave was coalesced by a newer periodic capture.",
                        "Admission");
                    if (!cancelled.Succeeded)
                    {
                        lock (_queueGate)
                            _writer.Failure ??= HealthFailureDetail(displacedJob, cancelled.Detail);
                        return AutoSaveCaptureResult.Failure(
                            $"Auto-save ({reason}) coalescing evidence failed: {cancelled.Detail}",
                            captured.Count);
                    }
                }
            }

            var admission = _health.PublishAdmission(AutoSaveHealthRecord.Create(
                job.OperationId,
                reason,
                AutoSaveHealthStatus.Queued,
                nowUtc,
                nowUtc,
                intendedActors: captured.Count,
                affectedPaths: captured.Select(entry => entry.FileName).ToArray()));
            if (!admission.Result.Succeeded)
            {
                return AutoSaveCaptureResult.Failure(
                    $"Auto-save ({reason}) health admission failed: {admission.Result.Detail}",
                    captured.Count);
            }
            job = job with { HealthGeneration = admission.Generation };

            lock (_queueGate)
            {
                if (_disposed || startupFailure is not null || (_exitReserved && !isFinal))
                    admissionFailure = startupFailure ?? "Auto-save admission is closed.";
                else
                    _writer.AdmitLocked(job);
            }

            if (admissionFailure is not null)
            {
                PublishRecovery(job, "Admission", admissionFailure);
                return AutoSaveCaptureResult.Failure(
                    $"Auto-save ({reason}) was not admitted: {admissionFailure}",
                    captured.Count);
            }

            lock (_queueGate)
            {
                dispatchAccepted = _writer.EnsureRunningLocked();
            }

            if (!dispatchAccepted)
            {
                lock (_queueGate)
                {
                    _writer.WithdrawLocked(job);
                    _writer.Failure ??= $"Auto-save ({reason}) dispatch was not accepted.";
                }
                PublishRecovery(job, "Dispatch", "Auto-save worker dispatch was not accepted.");
                return AutoSaveCaptureResult.Captured(
                    captured.Count,
                    $"Auto-save ({reason}) dispatch was not accepted.");
            }

            if (captureFailure != null)
            {
                return AutoSaveCaptureResult.Failure(
                    $"Auto-save ({reason}) captured {captured.Count} actor(s), " +
                    $"but another actor failed: {captureFailure}",
                    captured.Count,
                    dispatchAccepted: true);
            }

            return AutoSaveCaptureResult.DispatchStarted(captured.Count);
        }
        catch (Exception ex)
        {
            _error($"Auto-save ({reason}) failed: {ex}");
            return AutoSaveCaptureResult.Failure(
                $"Auto-save ({reason}) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Closes admission, joins every owned writer, then performs clean-on-exit
    /// cleanup if requested. No timeout path releases ownership of the writer.
    /// </summary>
    public AutoSaveTerminalResult CompleteForExit()
    {
        Task? writer;
        bool clean;
        AutoSaveSnapshotJob? cancelled = null;
        lock (_queueGate)
        {
            if (_exitCompleted)
                return _writer.TerminalResult;

            _exitReserved = true;
            cancelled = _writer.TakePeriodicLocked();
            clean = _cleanOnExit;
            writer = _writer.WriterTaskLocked;
        }

        if (cancelled is { } cancelledJob)
        {
            var cancelledHealth = PublishCancelled(
                cancelledJob,
                "Periodic autosave was cancelled during exit drain.",
                "Shutdown");
            if (!cancelledHealth.Succeeded)
            {
                lock (_queueGate)
                    _writer.Failure ??= HealthFailureDetail(cancelledJob, cancelledHealth.Detail);
            }
        }

        try
        {
            writer?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            lock (_queueGate)
                _writer.Failure ??= ex.Message;
        }

        AutoSaveTerminalResult result;
        lock (_queueGate)
        {
            if (_writer.HasOutstandingWorkLocked)
            {
                // A callback can only reach here if a custom dispatcher violated
                // its ownership contract. Keep the service in recovery rather
                // than claiming that unload is safe.
                _writer.Failure ??= "Auto-save worker did not reach a terminal state.";
            }

            var startupFailure = _health.StartupFailure;
            result = _writer.Failure is not null
                ? AutoSaveTerminalResult.RecoveryRequired(_writer.Failure)
                : startupFailure is not null
                    ? AutoSaveTerminalResult.RecoveryRequired(startupFailure)
                : _hasFinalCapture &&
                  _finalCapture.Status == AutoSaveCaptureStatus.Failure
                    ? AutoSaveTerminalResult.RecoveryRequired(
                        _finalCapture.Detail ?? "Final auto-save capture failed.")
                : clean
                    ? AutoSaveTerminalResult.PendingResult
                    : _hasFinalCapture &&
                      (_finalCapture.Status is AutoSaveCaptureStatus.Captured
                        or AutoSaveCaptureStatus.DispatchStarted)
                        ? AutoSaveTerminalResult.Written()
                        : AutoSaveTerminalResult.NotAttempted();
        }

        if (clean && result.Status != AutoSaveTerminalStatus.RecoveryRequired)
        {
            if (_store.CleanAll())
                result = AutoSaveTerminalResult.Cleaned();
            else
                result = AutoSaveTerminalResult.RecoveryRequired(
                    "Clean-on-exit could not remove every snapshot.");
        }

        result = _health.PublishExit(result, clean);

        lock (_queueGate)
        {
            _writer.TerminalResult = result;
            _exitCompleted = true;
            return result;
        }
    }

    internal bool WaitForIdle(TimeSpan timeout) => _writer.WaitForIdle(timeout);

    /// <summary>Close admission and join the owned worker before disposal.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_queueGate)
        {
            _disposed = true;
            _exitReserved = true;
            _writer.TakePeriodicLocked();
        }

        CompleteForExit();
        GC.SuppressFinalize(this);
    }
}
