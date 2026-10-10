using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Poser.Domain;
using Poser.Services;

namespace Poser.Files;

/// <summary>Immutable capture admitted to the autosave writer.</summary>
internal readonly record struct AutoSaveSnapshotJob(
    string OperationId,
    string Reason,
    DateTime NowUtc,
    int Keep,
    IReadOnlyList<NamedAutoSavePose> Captured,
    bool IsFinal,
    long HealthGeneration);

/// <summary>
/// The bounded autosave queue (one coalescing periodic slot plus one final
/// slot) and its single owned worker. Every member suffixed <c>Locked</c>, and
/// <see cref="Failure"/> / <see cref="TerminalResult"/>, require the caller to
/// hold the admission gate shared with <see cref="AutoSaveService"/>, so
/// admission checks and queue mutation stay one atomic step.
/// </summary>
internal sealed class AutoSaveWriter
{
    private readonly object _gate;
    private readonly PoseAutoSaveStore _store;
    private readonly AutoSaveHealthLedger _health;
    private readonly Func<Action, bool> _dispatch;
    private readonly Action<string> _error;

    private AutoSaveSnapshotJob? _pendingPeriodic;
    private AutoSaveSnapshotJob? _finalJob;
    private Task? _writerTask;
    private bool _running;

    public AutoSaveWriter(
        object gate,
        PoseAutoSaveStore store,
        AutoSaveHealthLedger health,
        Func<Action, bool> dispatch,
        Action<string> error)
    {
        _gate = gate;
        _store = store;
        _health = health;
        _dispatch = dispatch;
        _error = error;
    }

    /// <summary>First worker or admission failure since the last session reset.</summary>
    public string? Failure { get; set; }

    public AutoSaveTerminalResult TerminalResult { get; set; } =
        AutoSaveTerminalResult.PendingResult;

    public bool IsRunningLocked => _running;

    public bool HasOutstandingWorkLocked =>
        _running || _finalJob is not null || _pendingPeriodic is not null;

    public Task? WriterTaskLocked => _writerTask;

    public AutoSaveSnapshotJob? TakePeriodicLocked()
    {
        var job = _pendingPeriodic;
        _pendingPeriodic = null;
        return job;
    }

    public void AdmitLocked(AutoSaveSnapshotJob job)
    {
        if (job.IsFinal)
            _finalJob = job;
        else
            _pendingPeriodic = job;
    }

    public void WithdrawLocked(AutoSaveSnapshotJob job)
    {
        if (job.IsFinal)
            _finalJob = null;
        else if (_pendingPeriodic.Equals(job))
            _pendingPeriodic = null;
    }

    public bool EnsureRunningLocked()
    {
        if (_running)
        {
            TerminalResult = AutoSaveTerminalResult.PendingResult;
            return true;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _running = true;
        try
        {
            var accepted = _dispatch(() => Drain(completion));
            if (!accepted)
            {
                _running = false;
                completion.TrySetResult(false);
                _error("Auto-save worker dispatch was not accepted.");
                return false;
            }

            // The task is retained even when the test dispatcher invokes the
            // callback synchronously; unload always owns the join boundary.
            _writerTask = completion.Task;
            TerminalResult = AutoSaveTerminalResult.PendingResult;
            return true;
        }
        catch (Exception ex)
        {
            _running = false;
            completion.TrySetException(ex);
            _error($"Auto-save worker dispatch failed: {ex.Message}");
            return false;
        }
    }

    public bool WaitForIdle(TimeSpan timeout)
    {
        Task? writer;
        lock (_gate)
            writer = _writerTask;
        return writer is null || writer.Wait(timeout);
    }

    private void Drain(TaskCompletionSource<bool> completion)
    {
        var success = true;
        try
        {
            while (true)
            {
                AutoSaveSnapshotJob? job;
                lock (_gate)
                {
                    job = _pendingPeriodic ?? _finalJob;
                    if (job is null)
                    {
                        _running = false;
                        TerminalResult = success
                            ? AutoSaveTerminalResult.Written()
                            : AutoSaveTerminalResult.RecoveryRequired(
                                Failure ?? "Auto-save worker failed.");
                        completion.TrySetResult(success);
                        return;
                    }

                    if (job.Value.IsFinal)
                        _finalJob = null;
                    else
                        _pendingPeriodic = null;
                }

                var result = WriteSnapshot(job.Value);
                if (!result.Success)
                {
                    success = false;
                    lock (_gate)
                        Failure ??= result.Detail;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                success = false;
                Failure ??= ex.Message;
                _running = false;
                TerminalResult = AutoSaveTerminalResult.RecoveryRequired(ex.Message);
            }
            completion.TrySetResult(false);
        }
    }

    /// <summary>
    /// Worker half: serialization, folder creation, the writes and retention.
    /// Touches nothing but the captured data and the disk. Failure semantics
    /// are recorded in the operation health receipt and logged; one bad actor
    /// never aborts the rest of the snapshot.
    /// </summary>
    private Outcome WriteSnapshot(AutoSaveSnapshotJob job)
    {
        var written = _store.Write(job.Reason, job.NowUtc, job.Keep, job.Captured);
        var success = written.Success;
        var failure = written.Detail;
        var health = _health.Publish(AutoSaveHealthRecord.Create(
            job.OperationId,
            job.Reason,
            success ? AutoSaveHealthStatus.Written : AutoSaveHealthStatus.RecoveryRequired,
            job.NowUtc,
            DateTime.UtcNow,
            intendedActors: job.Captured.Count,
            writtenActors: written.Written,
            affectedPaths: written.Paths,
            failurePhase: success ? null : written.FailurePhase,
            detail: failure,
            recoveryEvidencePaths: written.RecoveryEvidence),
            job.HealthGeneration,
            retainFailure: !success);
        if (!health.Succeeded)
        {
            success = false;
            failure ??= $"health update failed: {health.Detail}";
        }
        return new Outcome(success, failure);
    }
}
