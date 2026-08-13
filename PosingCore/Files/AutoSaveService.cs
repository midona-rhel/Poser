using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Config;
using Poser.Entities;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Timed pose auto-save (GAP 4). Exports every actor with Poser-authored edits
/// into
/// <c>&lt;pluginConfigDir&gt;/AutoSaves/&lt;local day&gt;/&lt;HH-mm-ss&gt; &lt;actor&gt;.pose</c>
/// while in GPose, and requests one final capture attempt on GPose exit.
///
/// <para>SPLIT ACROSS TWO THREADS. <see cref="SaveNow"/> runs on the framework
/// tick and does only what needs live game state: the authored-edit scan and
/// <see cref="IPoseFileService.CreatePoseFile"/>. A <see cref="PoseFile"/> is
/// plain data the moment it is built, so JSON, folder creation, the writes and
/// retention all run on a worker (<see cref="WriteSnapshot"/>) instead of
/// hitching a frame once per interval. <see cref="IPoseFileService.ExportPose"/>
/// is not used because it fuses the capture and the write.</para>
///
/// Deliberate deviations from the references:
/// <list type="bullet">
/// <item>ONE FOLDER PER DAY, files prefixed with the save time (user call,
/// 2026-08-08: both references write a folder per save, which at a one-minute
/// interval buries a session under dozens of sibling folders). Local time,
/// because the layout exists to be browsed and "that evening's folder" is a
/// local-calendar notion; the 24-hour prefix keeps name order == time order
/// within a day, and the same-second suffix in
/// <see cref="SnapshotFilePath"/> also covers the DST fold's replayed hour.</item>
/// <item>Retention is computed from what is on disk, not from an in-memory
/// queue, so it still holds after a plugin restart (Ktisis' does not). It
/// counts SAVE EVENTS — a time-prefix group of files, or one whole folder of
/// the old one-folder-per-save layout, which is how pre-existing snapshots
/// age out with no migration.</item>
/// <item>One actor failing to export never aborts the rest of the snapshot
/// (Brio aborts the whole save on a single bad filename).</item>
/// <item>Nothing is written when no actor has authored edits, so no empty
/// folders accumulate (Ktisis leaves them).</item>
/// </list>
///
/// <para>The application lifecycle coordinator requests exactly one final
/// capture attempt before publishing the legacy GPose exit event. The scene
/// services remain factories so the capture reads their current state without
/// making composition depend on an event-subscriber order.</para>
/// </summary>
public class AutoSaveService : IAutoSaveService
{
    private const string AutoSaveFolderName = "AutoSaves";
    private const string DayFolderFormat = "yyyy-MM-dd";
    private const string TimePrefixFormat = "HH-mm-ss";
    /// <summary>Rendered length of <see cref="TimePrefixFormat"/>.</summary>
    private const int TimePrefixLength = 8;

    private readonly IPluginLog _log;
    private readonly IFramework? _framework;
    private readonly IGPoseService _gpose;
    private readonly Func<IActorManager> _actors;
    private readonly Func<ISkeletonService> _skeletons;
    private readonly Func<IBonePosingService> _bonePosing;
    private readonly Func<IPoseFileService> _poseFiles;
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

    public AutoSaveService(
        IPluginLog log,
        IFramework framework,
        IGPoseService gpose,
        Func<IActorManager> actors,
        Func<ISkeletonService> skeletons,
        Func<IBonePosingService> bonePosing,
        Func<IPoseFileService> poseFiles,
        ConfigurationService configuration,
        IDalamudPluginInterface pluginInterface)
        : this(
            log,
            framework,
            gpose,
            actors,
            skeletons,
            bonePosing,
            poseFiles,
            configuration,
            Path.Combine(pluginInterface.GetPluginConfigDirectory(), AutoSaveFolderName))
    {
    }

    /// <summary>
    /// Test seam: explicit root directory, an injectable UTC clock, and an
    /// optional framework (null means the caller drives <see cref="Tick"/>
    /// itself instead of the service subscribing to the game tick).
    /// </summary>
    internal AutoSaveService(
        IPluginLog log,
        IFramework? framework,
        IGPoseService gpose,
        Func<IActorManager> actors,
        Func<ISkeletonService> skeletons,
        Func<IBonePosingService> bonePosing,
        Func<IPoseFileService> poseFiles,
        ConfigurationService configuration,
        string rootDirectory,
        Func<DateTime>? utcClock = null,
        Func<Action, bool>? dispatch = null,
        AutoSaveHealthStore? healthStore = null)
    {
        _log = log;
        _framework = framework;
        _gpose = gpose;
        _actors = actors;
        _skeletons = skeletons;
        _bonePosing = bonePosing;
        _poseFiles = poseFiles;
        _configuration = configuration;
        _clock = utcClock ?? (() => DateTime.UtcNow);
        _dispatch = dispatch ?? (work =>
        {
            _ = Task.Run(work);
            return true;
        });
        RootDirectory = rootDirectory;
        _health = healthStore ?? new AutoSaveHealthStore(rootDirectory);
        var stale = _health.RecoverStale();
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
            _log.Error($"Auto-save: {_startupHealthFailure}");
        }
        else if (stale.Record is not null)
        {
            _lastHealthRecord = stale.Record;
        }

        try
        {
            Directory.CreateDirectory(RootDirectory);
        }
        catch (Exception ex)
        {
            // A missing root is not fatal at construction: each save retries the
            // per-snapshot create and logs there.
            _log.Error($"Auto-save: could not create '{RootDirectory}': {ex.Message}");
        }

        if (_framework != null)
            _framework.Update += OnFrameworkUpdate;
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
            _log.Error($"Auto-save health transition failed: {result.Detail}");
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
        var allEntries = (prior?.RecoveryEntries ?? Array.Empty<AutoSaveHealthRecoveryEntry>())
            .Concat(new[] { entry })
            .ToArray();
        var overflow = (prior?.RecoveryOverflowCount ?? 0) +
            Math.Max(0, allEntries.Length - AutoSaveHealthRecord.MaxRecoveryEntries);
        _pendingHealthRecovery = (prior ?? recovery).With(
            status: AutoSaveHealthStatus.RecoveryRequired,
            updatedUtc: DateTime.UtcNow,
            recoveryEntries: allEntries.Take(AutoSaveHealthRecord.MaxRecoveryEntries),
            recoveryOverflowCount: overflow);
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
            _hasFinalCapture = false;
            _workerFailure = null;
            _lastTerminalResult = AutoSaveTerminalResult.PendingResult;
        }
    }

    private void OnFrameworkUpdate(IFramework framework) => Tick(_clock());

    /// <summary>
    /// Interval logic. Idle (not enabled, or not in GPose) disarms the timer;
    /// the first tick after arming schedules one full interval out, so entering
    /// GPose never saves immediately (parity with both references).
    /// </summary>
    internal void Tick(DateTime nowUtc)
    {
        var settings = Settings;
        if (!settings.Enabled || !_gpose.IsGPosing)
        {
            _nextDueUtc = null;
            return;
        }

        ResetCompletedExitForNewSession();
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
    /// One captured actor. The <see cref="PoseFile"/> is already detached from
    /// game memory — <see cref="IPoseFileService.CreatePoseFile"/> copies bone
    /// transforms into plain dictionaries — which is exactly what lets the
    /// write half run off the framework thread.
    /// </summary>
    private readonly record struct CapturedPose(
        string ActorName,
        string FileName,
        PoseFile Pose);

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
            var captured = new List<CapturedPose>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? captureFailure = null;

            foreach (var actor in _actors().Actors)
            {
                try
                {
                    if (!HasAuthoredEdits(actor))
                        continue;

                    // Both halves of the capture read live game state, so both
                    // stay here; only the resulting PoseFile crosses over.
                    captured.Add(new CapturedPose(
                        actor.Name,
                        UniqueFileName(actor.Name, used) + ".pose",
                        _poseFiles().CreatePoseFile(_skeletons().GetSkeletons(actor))));
                }
                catch (Exception ex)
                {
                    captureFailure ??= ex.Message;
                    _log.Error(
                        $"Auto-save ({reason}): could not inspect actor '{actor.Name}': {ex.Message}");
                }
            }

            if (captured.Count == 0)
            {
                if (captureFailure != null)
                {
                    return AutoSaveCaptureResult.Failure(
                        $"Auto-save ({reason}) could not capture an actor: {captureFailure}");
                }

                _log.Debug($"Auto-save ({reason}): no actors with authored edits, skipping");
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
            _log.Error($"Auto-save ({reason}) failed: {ex}");
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
                _log.Error("Auto-save worker dispatch was not accepted.");
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
            _log.Error($"Auto-save worker dispatch failed: {ex.Message}");
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
            if (CleanAll())
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
                mergedRecoveryEntries = healthRecord.RecoveryEntries
                    .Concat(pendingRecovery.RecoveryEntries)
                    .Take(AutoSaveHealthRecord.MaxRecoveryEntries)
                    .ToArray();
                mergedRecoveryOverflow = healthRecord.RecoveryOverflowCount +
                    pendingRecovery.RecoveryOverflowCount +
                    Math.Max(0, healthRecord.RecoveryEntries.Count +
                        pendingRecovery.RecoveryEntries.Count -
                        AutoSaveHealthRecord.MaxRecoveryEntries);
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
        var success = true;
        string? failure = null;
        string? failurePhase = null;
        var affectedPaths = new List<string>();
        var recoveryEvidence = new List<string>();
        var saved = 0;
        try
        {
            var local = nowUtc.ToLocalTime();
            var dayFolder = Path.Combine(
                RootDirectory,
                local.ToString(DayFolderFormat, CultureInfo.InvariantCulture));
            var prefix = local.ToString(TimePrefixFormat, CultureInfo.InvariantCulture);
            var planned = new List<(CapturedPose Entry, string Path)>(captured.Count);
            foreach (var entry in captured)
            {
                var path = SnapshotFilePath(dayFolder, prefix, entry.FileName);
                affectedPaths.Add(path);
                planned.Add((entry, path));
            }

            Directory.CreateDirectory(dayFolder);
            foreach (var (entry, path) in planned)
            {
                try
                {
                    var write = AtomicPoseFileStore.Default.Write(entry.Pose, path);
                    if (write.Succeeded)
                    {
                        saved++;
                    }
                    else
                    {
                        success = false;
                        failurePhase ??= "ActorWrite";
                        failure ??= write.Failure?.Detail ?? $"export failed for actor '{entry.ActorName}'";
                        recoveryEvidence.AddRange(write.RecoveryEvidencePaths);
                        // The typed atomic store carries the filesystem
                        // evidence; this adds the auto-save actor context.
                        _log.Error(
                            $"Auto-save ({reason}): export failed for actor '{entry.ActorName}' -> {path}: {write.Failure?.Detail}");
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    failurePhase ??= "ActorWrite";
                    failure ??= ex.Message;
                    _log.Error(
                        $"Auto-save ({reason}): actor '{entry.ActorName}' -> {path} threw: {ex.Message}");
                }
            }

            _log.Info($"Auto-saved {saved}/{captured.Count} actor(s) to {dayFolder} ({reason})");
            if (!Prune(keep))
            {
                success = false;
                failurePhase ??= "Retention";
                failure ??= "retention pruning failed";
            }
        }
        catch (Exception ex)
        {
            success = false;
            failurePhase ??= "Worker";
            failure ??= ex.Message;
            _log.Error($"Auto-save ({reason}) failed: {ex}");
        }
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

    /// <summary>
    /// Mirrors <c>CleanPoseFacade.HasAuthoredEdits</c>: any bone of any present
    /// slot carrying an unnamed (user-authored, not service-owned) layer.
    /// </summary>
    private bool HasAuthoredEdits(IActor actor)
    {
        var bonePosing = _bonePosing();
        return _skeletons().GetSkeletons(actor).Any(skeleton =>
            bonePosing.GetPoseInfo(skeleton).AllPoses
                .Any(pose => pose.Stacks.Any(stack => stack.Layer == null)));
    }

    /// <summary>
    /// <c>"HH-mm-ss Actor.pose"</c> inside the day folder. A collision (an
    /// exit save landing in the same second as an interval save, or the DST
    /// fold replaying an hour) gets a " (2)" suffix before the extension, so
    /// nothing is ever overwritten.
    /// </summary>
    private static string SnapshotFilePath(string dayFolder, string prefix, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(dayFolder, $"{prefix} {stem}{extension}");
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(dayFolder, $"{prefix} {stem} ({suffix}){extension}");
        return candidate;
    }

    /// <summary>
    /// Ktisis <c>FormatService.StripInvalidChars</c> parity, plus in-snapshot
    /// de-duplication so two actors with the same name both survive.
    /// </summary>
    private static string UniqueFileName(string actorName, HashSet<string> used)
    {
        var name = Sanitize(actorName);
        if (used.Add(name))
            return name;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (used.Add(candidate))
                return candidate;
        }
    }

    private static string Sanitize(string actorName)
    {
        if (string.IsNullOrWhiteSpace(actorName))
            return "Actor";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = actorName.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "Actor" : sanitized;
    }

    /// <summary>
    /// Disk-based retention: the newest <c>MaxAutoSaves</c> SAVE EVENTS by date
    /// are kept, everything older is deleted. One event is what a single save
    /// wrote — the files sharing one time prefix inside a day folder, or one
    /// whole folder of the old one-folder-per-save layout, which is how
    /// pre-existing snapshots join the same ordering and age out without a
    /// migration. Reading the disk rather than a session queue is what makes
    /// retention hold across restarts.
    ///
    /// <para>Date, not name (Brio's semantic): a save is written once and never
    /// touched again, so its last-write time IS the save date, and a folder or
    /// file the user renamed keeps its true age instead of being sorted by
    /// whatever it is now called. Ties break on key, descending, so the order
    /// is total even at one-second stamp granularity. A day folder whose last
    /// event was pruned goes with it.</para>
    /// </summary>
    private bool Prune(int keep)
    {
        var events = new List<(DateTime AtUtc, string Key, string? LegacyDir, List<string>? Files)>();
        var dayFolders = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
            {
                var name = Path.GetFileName(dir) ?? string.Empty;
                if (!IsDayFolder(name))
                {
                    // Old layout: the folder is the save.
                    events.Add((Directory.GetLastWriteTimeUtc(dir), name, dir, null));
                    continue;
                }

                dayFolders.Add(dir);
                foreach (var group in Directory.EnumerateFiles(dir)
                             .GroupBy(file => EventKey(Path.GetFileName(file))))
                {
                    var files = group.ToList();
                    var newest = DateTime.MinValue;
                    foreach (var file in files)
                    {
                        var at = File.GetLastWriteTimeUtc(file);
                        if (at > newest)
                            newest = at;
                    }
                    events.Add((newest, $"{name}/{group.Key}", null, files));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Auto-save: could not enumerate '{RootDirectory}' to prune: {ex.Message}");
            return false;
        }

        var stale = events
            .OrderByDescending(entry => entry.AtUtc)
            .ThenByDescending(entry => entry.Key, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();

        var pruned = 0;
        var success = true;
        foreach (var (_, _, legacyDir, files) in stale)
        {
            try
            {
                if (legacyDir != null)
                    Directory.Delete(legacyDir, recursive: true);
                else
                    foreach (var file in files!)
                        File.Delete(file);
                pruned++;
            }
            catch (Exception ex)
            {
                success = false;
                _log.Error(
                    $"Auto-save: could not prune '{legacyDir ?? files![0]}': {ex.Message}");
            }
        }

        foreach (var dir in dayFolders)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                success = false;
                _log.Error(
                    $"Auto-save: could not remove empty day folder '{dir}': {ex.Message}");
            }
        }

        if (pruned > 0)
            _log.Debug($"Auto-save pruned {pruned} old save(s).");
        return success;
    }

    private static bool IsDayFolder(string name) =>
        DateTime.TryParseExact(
            name,
            DayFolderFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    /// <summary>Files sharing one valid <c>"HH-mm-ss "</c> prefix are one save;
    /// anything else (a user-renamed file) is its own event under its full
    /// name.</summary>
    private static string EventKey(string? fileName)
    {
        if (fileName != null &&
            fileName.Length > TimePrefixLength &&
            fileName[TimePrefixLength] == ' ' &&
            DateTime.TryParseExact(
                fileName[..TimePrefixLength],
                TimePrefixFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            return fileName[..TimePrefixLength];
        return fileName ?? string.Empty;
    }

    private bool CleanAll()
    {
        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(RootDirectory).ToList();
        }
        catch (Exception ex)
        {
            _log.Error($"Auto-save: could not enumerate '{RootDirectory}' to clean: {ex.Message}");
            return false;
        }

        var deleted = 0;
        var success = true;
        foreach (var dir in folders)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex)
            {
                success = false;
                _log.Error($"Auto-save: could not delete '{dir}': {ex.Message}");
            }
        }

        _log.Info($"Auto-save cleaned {deleted} snapshot folder(s) on leaving GPose.");
        try
        {
            return success && !Directory.EnumerateDirectories(RootDirectory).Any();
        }
        catch (Exception ex)
        {
            _log.Error(
                $"Auto-save: could not verify clean-on-exit root '{RootDirectory}': {ex.Message}");
            return false;
        }
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

        if (_framework != null)
            _framework.Update -= OnFrameworkUpdate;
        CompleteForExit();
        GC.SuppressFinalize(this);
    }
}
