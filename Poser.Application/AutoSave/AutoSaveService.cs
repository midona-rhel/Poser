using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Poser.Config;
using Poser.Application.AutoSave;
using CapturedPose = Poser.Files.NamedAutoSavePose;
using Poser.Services;

namespace Poser.Files;

/// <summary>Pose autosave cadence, admission, final capture and persistence ownership.</summary>
public class AutoSaveService : IAutoSaveService
{
    private readonly Action<string> _error;
    private readonly Action<string> _debug;
    private readonly IPoseAutoSaveCapture _capture;
    private readonly PoseAutoSaveStore _store;
    private readonly ConfigurationService _configuration;
    private readonly Func<DateTime> _clock;
    private readonly Func<Action, bool> _dispatch;
    private readonly AutoSaveHealthStore _health;

    private DateTime? _nextDueUtc;
    private bool _disposed;

    private readonly object _queueGate = new();
    // Health transitions have their own serial owner.  Admission obtains a
    // monotonically increasing generation before the job becomes visible to
    // the queue; older worker terminal evidence can therefore never replace a
    // newer admitted operation.  No queue lock is held while this gate does
    // filesystem I/O, so the worker can always reach its next queue item.
    private readonly object _healthGate = new();
    private SnapshotJob? _pendingPeriodic;
    private SnapshotJob? _finalJob;
    private Task? _writerTask;
    private bool _writerRunning;
    private bool _exitReserved;
    private bool _exitCompleted;
    private bool _cleanOnExit;
    private bool _finalCaptureStarted;
    private AutoSaveCaptureResult _finalCapture;
    private bool _hasFinalCapture;
    private bool _wasGPosing;
    private bool _exitCompletedWhileDisabled;
    private bool _sessionReopenedAfterCompletedExit;
    private string? _workerFailure;
    private string? _startupHealthFailure;
    private AutoSaveHealthRecord? _lastHealthRecord;
    private AutoSaveHealthRecord? _pendingHealthRecovery;
    private long _nextHealthGeneration;
    private long _currentHealthGeneration;
    private AutoSaveTerminalResult _lastTerminalResult =
        AutoSaveTerminalResult.PendingResult;

    public string RootDirectory { get; }

    public DateTime? LastSaveUtc { get; private set; }

    public AutoSaveTerminalResult LastTerminalResult
    {
        get
        {
            lock (_queueGate)
                return _lastTerminalResult;
        }
    }

    public AutoSaveHealthRecord? LastHealthRecord
    {
        get
        {
            lock (_healthGate)
                return _lastHealthRecord;
        }
    }

    private readonly record struct SnapshotJob(
        string OperationId,
        string Reason,
        DateTime NowUtc,
        int Keep,
        IReadOnlyList<CapturedPose> Captured,
        bool IsFinal,
        long HealthGeneration);

    private readonly record struct HealthAdmission(
        AutoSaveHealthWriteResult Result,
        long Generation);

    private readonly record struct WorkerResult(bool Success, string? Detail);

    private readonly record struct RecoveryEntryIdentity(
        string OperationId,
        AutoSaveHealthStatus Status,
        string? FailurePhase,
        DateTime CreatedUtc,
        DateTime UpdatedUtc,
        string? Detail,
        string AffectedPaths,
        string EvidencePaths);

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
        _dispatch = dispatch ?? (work =>
        {
            _ = Task.Run(work);
            return true;
        });
        RootDirectory = store.RootDirectory;
        _health = healthStore ?? new AutoSaveHealthStore(RootDirectory);
        var stale = _health.RecoverStale();
        // A terminal record (or no record) is a successful observation: only
        // an attempted promotion whose write failed closes new admissions.
        if (!stale.Succeeded)
        {
            _startupHealthFailure = stale.Write?.Detail ??
                "Autosave stale health recovery could not be persisted.";
            _lastHealthRecord = stale.Record?.With(
                status: AutoSaveHealthStatus.RecoveryRequired,
                updatedUtc: DateTime.UtcNow,
                failurePhase: "HealthTransition",
                detail: _startupHealthFailure,
                recoveryEvidencePaths: stale.Write?.RecoveryEvidencePaths);
            _error($"Auto-save: {_startupHealthFailure}");
        }
        else if (stale.Record is not null)
        {
            _lastHealthRecord = stale.Record;
        }

    }

    private AutoSaveConfiguration Settings => _configuration.Config.AutoSave;

    private HealthAdmission PublishAdmissionHealth(AutoSaveHealthRecord record)
    {
        AutoSaveHealthWriteResult result;
        long generation;
        lock (_healthGate)
        {
            generation = ++_nextHealthGeneration;
            result = WriteHealthLocked(record);
            if (result.Succeeded)
                _currentHealthGeneration = generation;
        }
        LogHealthFailure(result);
        return new HealthAdmission(result, generation);
    }

    private AutoSaveHealthWriteResult PublishHealth(
        AutoSaveHealthRecord record,
        long healthGeneration = 0,
        bool retainFailure = false)
    {
        AutoSaveHealthWriteResult result;
        lock (_healthGate)
        {
            // Once a newer operation has been admitted, an older worker may
            // still finish its disk work, but its terminal record is evidence
            // for that operation only and cannot become the current record.
            if (healthGeneration > 0 && healthGeneration < _currentHealthGeneration)
            {
                if (retainFailure || record.Status == AutoSaveHealthStatus.RecoveryRequired)
                {
                    RetainHealthRecoveryLocked(record);
                }
                result = AutoSaveHealthWriteResult.Success();
            }
            else
            {
                result = WriteHealthLocked(record);
            }
        }
        LogHealthFailure(result);
        return result;
    }

    private AutoSaveHealthWriteResult WriteHealthLocked(
        AutoSaveHealthRecord record,
        bool allowCurrentUpdate = true)
    {
        AutoSaveHealthWriteResult result;
        try
        {
            result = _health.Write(record);
        }
        catch (Exception ex)
        {
            result = AutoSaveHealthWriteResult.Failed(
                $"Autosave health transition threw: {ex.Message}");
        }

        if (result.Succeeded)
        {
            if (allowCurrentUpdate)
                _lastHealthRecord = record;
        }
        else
        {
            var recovery = record.With(
                status: AutoSaveHealthStatus.RecoveryRequired,
                updatedUtc: DateTime.UtcNow,
                failurePhase: "HealthTransition",
                detail: result.Detail,
                recoveryEvidencePaths: result.RecoveryEvidencePaths);
            if (allowCurrentUpdate)
                _lastHealthRecord = recovery;
            RetainHealthRecoveryLocked(recovery);
        }

        return result;
    }

    private void LogHealthFailure(AutoSaveHealthWriteResult result)
    {
        if (!result.Succeeded)
            _error($"Auto-save health transition failed: {result.Detail}");
    }

    private void RetainHealthRecoveryLocked(AutoSaveHealthRecord recovery)
    {
        var entry = AutoSaveHealthRecoveryEntry.Create(
            recovery.OperationId,
            recovery.Reason,
            recovery.Status,
            recovery.CreatedUtc,
            recovery.UpdatedUtc,
            recovery.IntendedActors,
            recovery.WrittenActors,
            recovery.AffectedPaths,
            recovery.FailurePhase,
            recovery.Detail,
            recovery.RecoveryEvidencePaths);
        var prior = _pendingHealthRecovery;
        var merged = MergeRecoveryEntries(
            prior?.RecoveryEntries ?? Array.Empty<AutoSaveHealthRecoveryEntry>(),
            prior?.RecoveryOverflowCount ?? 0,
            new[] { entry }, 0);
        _pendingHealthRecovery = (prior ?? recovery).With(
            status: AutoSaveHealthStatus.RecoveryRequired,
            updatedUtc: DateTime.UtcNow,
            recoveryEntries: merged.Entries,
            recoveryOverflowCount: merged.OverflowCount);
    }

    private static (IReadOnlyList<AutoSaveHealthRecoveryEntry> Entries, int OverflowCount)
        MergeRecoveryEntries(
            IEnumerable<AutoSaveHealthRecoveryEntry> first,
            int firstOverflow,
            IEnumerable<AutoSaveHealthRecoveryEntry> second,
            int secondOverflow)
    {
        var seen = new HashSet<RecoveryEntryIdentity>();
        var unique = new List<AutoSaveHealthRecoveryEntry>();
        foreach (var entry in first.Concat(second))
        {
            var identity = new RecoveryEntryIdentity(
                entry.OperationId,
                entry.Status,
                entry.FailurePhase,
                entry.CreatedUtc,
                entry.UpdatedUtc,
                entry.Detail,
                string.Join("\u001f", entry.AffectedPaths),
                string.Join("\u001f", entry.RecoveryEvidencePaths));
            if (seen.Add(identity))
                unique.Add(entry);
        }

        var discardedRecoveryEntries = Math.Max(
            0, unique.Count - AutoSaveHealthRecord.MaxRecoveryEntries);
        var overflow = Math.Max(0L, (long)firstOverflow) +
            Math.Max(0L, (long)secondOverflow) +
            Math.Max(0L, (long)discardedRecoveryEntries);
        return (
            unique.Take(AutoSaveHealthRecord.MaxRecoveryEntries).ToArray(),
            (int)Math.Min((long)int.MaxValue, overflow));
    }

    private static string LimitHealthText(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private static string HealthFailureDetail(
        SnapshotJob job,
        string? detail) =>
        $"operation {job.OperationId} ({job.Reason}) HealthTransition: {detail}";

    private static string DescribeHealthRecovery(AutoSaveHealthRecord recovery) =>
        LimitHealthText(
            $"operation {recovery.OperationId} ({recovery.Reason}) " +
            $"status={recovery.Status}, intended={recovery.IntendedActors}, " +
            $"written={recovery.WrittenActors}, " +
            $"paths=[{string.Join(",", recovery.AffectedPaths)}], " +
            $"phase={recovery.FailurePhase ?? "HealthTransition"}, " +
            $"detail={recovery.Detail}, " +
            $"evidence=[{string.Join(",", recovery.RecoveryEvidencePaths)}]",
            4096);

    private AutoSaveHealthWriteResult PublishRecovery(
        SnapshotJob job,
        string phase,
        string detail) =>
        PublishHealth(AutoSaveHealthRecord.Create(
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
            if (!_exitCompleted || _writerRunning)
                return;

            _exitReserved = false;
            _exitCompleted = false;
            _cleanOnExit = false;
            _finalCaptureStarted = false;
            _finalCapture = default;
            _hasFinalCapture = false;
            _exitCompletedWhileDisabled = false;
            _workerFailure = null;
            _lastTerminalResult = AutoSaveTerminalResult.PendingResult;
            _nextDueUtc = null;
            LastSaveUtc = null;
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
                reopenedAfterCompletedExit = _exitCompleted && !_writerRunning;

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
        SnapshotJob? cancelled = null;
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
            cancelled = _pendingPeriodic;
            _pendingPeriodic = null;
        }

        if (cancelled is { } cancelledJob)
        {
            var cancelledHealth = PublishHealth(AutoSaveHealthRecord.Create(
                cancelledJob.OperationId,
                cancelledJob.Reason,
                AutoSaveHealthStatus.Cancelled,
                cancelledJob.NowUtc,
                DateTime.UtcNow,
                intendedActors: cancelledJob.Captured.Count,
                detail: "Periodic autosave was coalesced by final reservation.",
                failurePhase: "Admission"),
                cancelledJob.HealthGeneration,
                retainFailure: true);
            if (!cancelledHealth.Succeeded)
            {
                lock (_queueGate)
                    _workerFailure ??= HealthFailureDetail(cancelledJob, cancelledHealth.Detail);
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
            _lastTerminalResult = AutoSaveTerminalResult.PendingResult;
        }

        // Clean-on-exit has no final pose reservation, but direct callers still
        // receive the historical synchronous cleanup behavior. The lifecycle
        // port calls CompleteForExit again, which is idempotent.
        if (!settings.Enabled || _cleanOnExit)
            CompleteForExit();

        return result;
    }

    /// <summary>
    /// Returns the number of actors CAPTURED, not the number of files that
    /// landed: the writes outlive this call. Zero therefore also covers
    /// "nothing had authored edits" and "a periodic item was coalesced into the
    /// bounded pending slot", both of which may produce zero.
    /// </summary>
    public int SaveNow(string reason) =>
        CaptureAndDispatch(reason, isFinal: false).CapturedActors;

    private AutoSaveCaptureResult CaptureAndDispatch(string reason, bool isFinal)
    {
        lock (_queueGate)
        {
            if (_disposed || _startupHealthFailure is not null || (_exitReserved && !isFinal))
                return AutoSaveCaptureResult.NotCaptured(
                    _startupHealthFailure ?? "Auto-save admission is closed.");
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

            var job = new SnapshotJob(
                Guid.NewGuid().ToString("N"), reason, nowUtc, keep, captured, isFinal, 0);

            // A pending periodic item is canceled before the replacement's
            // Queued record is admitted.  This keeps the single health file's
            // current record ordered with the bounded queue and ensures a
            // failed cancellation transition remains actionable instead of
            // being hidden by the newer admission.
            SnapshotJob? displaced = null;
            string? admissionFailure = null;
            if (!isFinal)
            {
                lock (_queueGate)
                {
                    if (_disposed || _startupHealthFailure is not null || _exitReserved)
                        admissionFailure = _startupHealthFailure ?? "Auto-save admission is closed.";
                    else
                    {
                        displaced = _pendingPeriodic;
                        _pendingPeriodic = null;
                    }
                }

                if (admissionFailure is not null)
                    return AutoSaveCaptureResult.Failure(
                        $"Auto-save ({reason}) was not admitted: {admissionFailure}",
                        captured.Count);

                if (displaced is { } displacedJob)
                {
                    var cancelled = PublishHealth(AutoSaveHealthRecord.Create(
                        displacedJob.OperationId,
                        displacedJob.Reason,
                        AutoSaveHealthStatus.Cancelled,
                        displacedJob.NowUtc,
                        DateTime.UtcNow,
                        intendedActors: displacedJob.Captured.Count,
                        detail: "Periodic autosave was coalesced by a newer periodic capture.",
                        failurePhase: "Admission"),
                        displacedJob.HealthGeneration,
                        retainFailure: true);
                    if (!cancelled.Succeeded)
                    {
                        lock (_queueGate)
                            _workerFailure ??= HealthFailureDetail(displacedJob, cancelled.Detail);
                        return AutoSaveCaptureResult.Failure(
                            $"Auto-save ({reason}) coalescing evidence failed: {cancelled.Detail}",
                            captured.Count);
                    }
                }
            }

            var admission = PublishAdmissionHealth(AutoSaveHealthRecord.Create(
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
                if (_disposed || _startupHealthFailure is not null || (_exitReserved && !isFinal))
                    admissionFailure = _startupHealthFailure ?? "Auto-save admission is closed.";

                if (admissionFailure is null && isFinal)
                    _finalJob = job;
                else if (admissionFailure is null)
                    _pendingPeriodic = job;
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
                dispatchAccepted = EnsureWriterLocked();
            }

            if (!dispatchAccepted)
            {
                lock (_queueGate)
                {
                    if (isFinal)
                        _finalJob = null;
                    else if (_pendingPeriodic.Equals(job))
                        _pendingPeriodic = null;
                    _workerFailure ??= $"Auto-save ({reason}) dispatch was not accepted.";
                }
                PublishHealth(AutoSaveHealthRecord.Create(
                    job.OperationId,
                    reason,
                    AutoSaveHealthStatus.RecoveryRequired,
                    nowUtc,
                    DateTime.UtcNow,
                    intendedActors: captured.Count,
                    detail: "Auto-save worker dispatch was not accepted.",
                    failurePhase: "Dispatch"),
                    job.HealthGeneration,
                    retainFailure: true);
                return AutoSaveCaptureResult.Captured(
                    captured.Count,
                    $"Auto-save ({reason}) dispatch was not accepted.");
            }

            if (dispatchAccepted)
            {
                LastSaveUtc = nowUtc;
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

            if (captureFailure != null)
            {
                return AutoSaveCaptureResult.Failure(
                    $"Auto-save ({reason}) captured {captured.Count} actor(s), " +
                    $"but another actor failed: {captureFailure}",
                    captured.Count);
            }

            return AutoSaveCaptureResult.Captured(captured.Count);
        }
        catch (Exception ex)
        {
            _error($"Auto-save ({reason}) failed: {ex}");
            return AutoSaveCaptureResult.Failure(
                $"Auto-save ({reason}) failed: {ex.Message}");
        }
    }

    private bool EnsureWriterLocked()
    {
        if (_writerRunning)
        {
            _lastTerminalResult = AutoSaveTerminalResult.PendingResult;
            return true;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _writerRunning = true;
        try
        {
            var accepted = _dispatch(() => WorkerDrain(completion));
            if (!accepted)
            {
                _writerRunning = false;
                completion.TrySetResult(false);
                _error("Auto-save worker dispatch was not accepted.");
                return false;
            }

            // The task is retained even when the test dispatcher invokes the
            // callback synchronously; unload always owns the join boundary.
            _writerTask = completion.Task;
            _lastTerminalResult = AutoSaveTerminalResult.PendingResult;
            return true;
        }
        catch (Exception ex)
        {
            _writerRunning = false;
            completion.TrySetException(ex);
            _error($"Auto-save worker dispatch failed: {ex.Message}");
            return false;
        }
    }

    private void WorkerDrain(TaskCompletionSource<bool> completion)
    {
        var success = true;
        try
        {
            while (true)
            {
                SnapshotJob? job;
                lock (_queueGate)
                {
                    job = _pendingPeriodic ?? _finalJob;
                    if (job is null)
                    {
                        _writerRunning = false;
                        _lastTerminalResult = success
                            ? AutoSaveTerminalResult.Written()
                            : AutoSaveTerminalResult.RecoveryRequired(
                                _workerFailure ?? "Auto-save worker failed.");
                        completion.TrySetResult(success);
                        return;
                    }

                    if (job.Value.IsFinal)
                        _finalJob = null;
                    else
                        _pendingPeriodic = null;
                }

                var result = WriteSnapshot(
                    job.Value.OperationId,
                    job.Value.Reason,
                    job.Value.NowUtc,
                    job.Value.Keep,
                    job.Value.Captured,
                    job.Value.HealthGeneration);
                if (!result.Success)
                {
                    success = false;
                    lock (_queueGate)
                        _workerFailure ??= result.Detail;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_queueGate)
            {
                success = false;
                _workerFailure ??= ex.Message;
                _writerRunning = false;
                _lastTerminalResult = AutoSaveTerminalResult.RecoveryRequired(ex.Message);
            }
            completion.TrySetResult(false);
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
        SnapshotJob? cancelled = null;
        lock (_queueGate)
        {
            if (_exitCompleted)
                return _lastTerminalResult;

            _exitReserved = true;
            cancelled = _pendingPeriodic;
            _pendingPeriodic = null;
            clean = _cleanOnExit;
            writer = _writerTask;
        }

        if (cancelled is { } cancelledJob)
        {
            var cancelledHealth = PublishHealth(AutoSaveHealthRecord.Create(
                cancelledJob.OperationId,
                cancelledJob.Reason,
                AutoSaveHealthStatus.Cancelled,
                cancelledJob.NowUtc,
                DateTime.UtcNow,
                intendedActors: cancelledJob.Captured.Count,
                detail: "Periodic autosave was cancelled during exit drain.",
                failurePhase: "Shutdown"),
                cancelledJob.HealthGeneration,
                retainFailure: true);
            if (!cancelledHealth.Succeeded)
            {
                lock (_queueGate)
                    _workerFailure ??= HealthFailureDetail(cancelledJob, cancelledHealth.Detail);
            }
        }

        try
        {
            writer?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            lock (_queueGate)
                _workerFailure ??= ex.Message;
        }

        AutoSaveTerminalResult result;
        lock (_queueGate)
        {
            if (_writerRunning || _finalJob is not null || _pendingPeriodic is not null)
            {
                // A callback can only reach here if a custom dispatcher violated
                // its ownership contract. Keep the service in recovery rather
                // than claiming that unload is safe.
                _workerFailure ??= "Auto-save worker did not reach a terminal state.";
            }

            result = _workerFailure is not null
                ? AutoSaveTerminalResult.RecoveryRequired(_workerFailure)
                : _startupHealthFailure is not null
                    ? AutoSaveTerminalResult.RecoveryRequired(_startupHealthFailure)
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

        AutoSaveHealthRecord? pendingRecovery;
        AutoSaveHealthRecord? healthRecord;
        long healthGeneration;
        lock (_healthGate)
        {
            pendingRecovery = _pendingHealthRecovery;
            healthRecord = _lastHealthRecord ?? _health.Read();
            healthGeneration = _currentHealthGeneration;
        }

        if (pendingRecovery is not null)
        {
            var pendingDetail =
                $"Outstanding health recovery: {DescribeHealthRecovery(pendingRecovery)}";
            result = AutoSaveTerminalResult.RecoveryRequired(
                result.Detail is null
                    ? pendingDetail
                    : $"{result.Detail}; {pendingDetail}");
        }

        if (healthRecord is not null)
        {
            var mergedDetail = healthRecord.Detail ?? result.Detail;
            var mergedEvidence = healthRecord.RecoveryEvidencePaths;
            var mergedPaths = healthRecord.AffectedPaths;
            var mergedRecoveryEntries = healthRecord.RecoveryEntries;
            var mergedRecoveryOverflow = healthRecord.RecoveryOverflowCount;
            var failurePhase = healthRecord.FailurePhase;
            if (pendingRecovery is not null)
            {
                mergedDetail = healthRecord.Detail is null
                    ? result.Detail
                    : $"{healthRecord.Detail}; {result.Detail}";
                mergedEvidence = healthRecord.RecoveryEvidencePaths
                    .Concat(pendingRecovery.RecoveryEvidencePaths)
                    .Distinct(StringComparer.Ordinal)
                    .Take(256)
                    .ToArray();
                mergedPaths = healthRecord.AffectedPaths
                    .Concat(pendingRecovery.AffectedPaths)
                    .Distinct(StringComparer.Ordinal)
                    .Take(256)
                    .ToArray();
                var mergedRecovery = MergeRecoveryEntries(
                    healthRecord.RecoveryEntries,
                    healthRecord.RecoveryOverflowCount,
                    pendingRecovery.RecoveryEntries,
                    pendingRecovery.RecoveryOverflowCount);
                mergedRecoveryEntries = mergedRecovery.Entries;
                mergedRecoveryOverflow = mergedRecovery.OverflowCount;
                failurePhase = pendingRecovery.FailurePhase ?? "HealthTransition";
            }
            var healthStatus = result.Status switch
            {
                AutoSaveTerminalStatus.Written => AutoSaveHealthStatus.Written,
                AutoSaveTerminalStatus.Cleaned => AutoSaveHealthStatus.Cleaned,
                AutoSaveTerminalStatus.RecoveryRequired => AutoSaveHealthStatus.RecoveryRequired,
                _ => healthRecord.Status,
            };
            var healthUpdate = PublishHealth(AutoSaveHealthRecord.Create(
                healthRecord.OperationId,
                healthRecord.Reason,
                healthStatus,
                healthRecord.CreatedUtc,
                DateTime.UtcNow,
                healthRecord.IntendedActors,
                healthRecord.WrittenActors,
                mergedPaths,
                result.Status == AutoSaveTerminalStatus.RecoveryRequired
                    ? clean
                        ? "Cleanup"
                        : failurePhase ?? "CompleteForExit"
                    : failurePhase,
                clean
                    ? result.Detail ?? mergedDetail
                    : mergedDetail,
                mergedEvidence,
                mergedRecoveryEntries,
                mergedRecoveryOverflow),
                healthGeneration,
                retainFailure: true);
            if (!healthUpdate.Succeeded)
                result = AutoSaveTerminalResult.RecoveryRequired($"Autosave health update failed: {healthUpdate.Detail}");
            else if (pendingRecovery is not null)
            {
                // Clear only the exact recovery set acknowledged by the
                // current terminal publication. A failed or stale-suppressed
                // update must remain actionable for the next exit.
                lock (_healthGate)
                {
                    if (healthGeneration == _currentHealthGeneration &&
                        ReferenceEquals(_pendingHealthRecovery, pendingRecovery))
                        _pendingHealthRecovery = null;
                }
            }
        }

        lock (_queueGate)
        {
            _lastTerminalResult = result;
            _exitCompleted = true;
            return result;
        }
    }

    internal bool WaitForIdle(TimeSpan timeout)
    {
        Task? writer;
        lock (_queueGate)
            writer = _writerTask;
        return writer is null || writer.Wait(timeout);
    }

    /// <summary>
    /// Worker half: serialization, folder creation, the writes and retention.
    /// Touches nothing but the captured data and the disk. Failure semantics
    /// are recorded in the operation health receipt and logged; one bad actor
    /// never aborts the rest of the snapshot.
    /// </summary>
    private WorkerResult WriteSnapshot(
        string operationId,
        string reason,
        DateTime nowUtc,
        int keep,
        IReadOnlyList<CapturedPose> captured,
        long healthGeneration)
    {
        var written = _store.Write(reason, nowUtc, keep, captured);
        var success = written.Success;
        var failure = written.Detail;
        var failurePhase = written.FailurePhase;
        var saved = written.Written;
        var affectedPaths = written.Paths;
        var recoveryEvidence = written.RecoveryEvidence;
        var health = PublishHealth(AutoSaveHealthRecord.Create(
            operationId,
            reason,
            success ? AutoSaveHealthStatus.Written : AutoSaveHealthStatus.RecoveryRequired,
            nowUtc,
            DateTime.UtcNow,
            intendedActors: captured.Count,
            writtenActors: saved,
            affectedPaths: affectedPaths,
            failurePhase: success ? null : failurePhase,
            detail: failure,
            recoveryEvidencePaths: recoveryEvidence),
            healthGeneration,
            retainFailure: !success);
        if (!health.Succeeded)
        {
            success = false;
            failurePhase = "HealthTransition";
            failure ??= $"health update failed: {health.Detail}";
        }
        return new WorkerResult(success, failure);
    }

    /// <summary>Close admission and join the owned worker before disposal.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_queueGate)
        {
            _disposed = true;
            _exitReserved = true;
            _pendingPeriodic = null;
        }

        CompleteForExit();
        GC.SuppressFinalize(this);
    }
}
