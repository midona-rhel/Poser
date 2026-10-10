using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Serial owner of the single autosave health record: admission generations,
/// stale-evidence suppression, retained recovery obligations and the terminal
/// exit publication.
/// </summary>
internal sealed class AutoSaveHealthLedger
{
    private readonly AutoSaveHealthStore _store;
    private readonly Action<string> _error;

    // Health transitions have their own serial owner.  Admission obtains a
    // monotonically increasing generation before the job becomes visible to
    // the queue; older worker terminal evidence can therefore never replace a
    // newer admitted operation.  No queue lock is held while this gate does
    // filesystem I/O, so the worker can always reach its next queue item.
    private readonly object _gate = new();
    private AutoSaveHealthRecord? _lastRecord;
    private AutoSaveHealthRecord? _pendingRecovery;
    private long _nextGeneration;
    private long _currentGeneration;

    private readonly record struct RecoveryEntryIdentity(
        string OperationId,
        AutoSaveHealthStatus Status,
        string? FailurePhase,
        DateTime CreatedUtc,
        DateTime UpdatedUtc,
        string? Detail,
        string AffectedPaths,
        string EvidencePaths);

    public AutoSaveHealthLedger(AutoSaveHealthStore store, Action<string> error)
    {
        _store = store;
        _error = error;
        var stale = _store.RecoverStale();
        // A terminal record (or no record) is a successful observation: only
        // an attempted promotion whose write failed closes new admissions.
        if (!stale.Succeeded)
        {
            StartupFailure = stale.Write?.Detail ??
                "Autosave stale health recovery could not be persisted.";
            _lastRecord = stale.Record?.With(
                status: AutoSaveHealthStatus.RecoveryRequired,
                updatedUtc: DateTime.UtcNow,
                failurePhase: "HealthTransition",
                detail: StartupFailure,
                recoveryEvidencePaths: stale.Write?.RecoveryEvidencePaths);
            _error($"Auto-save: {StartupFailure}");
        }
        else if (stale.Record is not null)
        {
            _lastRecord = stale.Record;
        }
    }

    /// <summary>Set once at construction when stale recovery could not be persisted; closes admission.</summary>
    public string? StartupFailure { get; }

    public AutoSaveHealthRecord? LastRecord
    {
        get
        {
            lock (_gate)
                return _lastRecord;
        }
    }

    public (AutoSaveHealthWriteResult Result, long Generation) PublishAdmission(
        AutoSaveHealthRecord record)
    {
        AutoSaveHealthWriteResult result;
        long generation;
        lock (_gate)
        {
            generation = ++_nextGeneration;
            result = WriteLocked(record);
            if (result.Succeeded)
                _currentGeneration = generation;
        }
        LogFailure(result);
        return (result, generation);
    }

    public AutoSaveHealthWriteResult Publish(
        AutoSaveHealthRecord record,
        long healthGeneration = 0,
        bool retainFailure = false)
    {
        AutoSaveHealthWriteResult result;
        lock (_gate)
        {
            // Once a newer operation has been admitted, an older worker may
            // still finish its disk work, but its terminal record is evidence
            // for that operation only and cannot become the current record.
            if (healthGeneration > 0 && healthGeneration < _currentGeneration)
            {
                if (retainFailure || record.Status == AutoSaveHealthStatus.RecoveryRequired)
                {
                    RetainRecoveryLocked(record);
                }
                result = AutoSaveHealthWriteResult.Success();
            }
            else
            {
                result = WriteLocked(record);
            }
        }
        LogFailure(result);
        return result;
    }

    /// <summary>
    /// Folds the exit result into the current health record together with any
    /// retained recovery obligations, and returns the possibly downgraded result.
    /// </summary>
    public AutoSaveTerminalResult PublishExit(AutoSaveTerminalResult result, bool clean)
    {
        AutoSaveHealthRecord? pendingRecovery;
        AutoSaveHealthRecord? healthRecord;
        long healthGeneration;
        lock (_gate)
        {
            pendingRecovery = _pendingRecovery;
            healthRecord = _lastRecord ?? _store.Read();
            healthGeneration = _currentGeneration;
        }

        if (pendingRecovery is not null)
        {
            var pendingDetail =
                $"Outstanding health recovery: {DescribeRecovery(pendingRecovery)}";
            result = AutoSaveTerminalResult.RecoveryRequired(
                result.Detail is null
                    ? pendingDetail
                    : $"{result.Detail}; {pendingDetail}");
        }

        if (healthRecord is null)
            return result;

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
        var healthUpdate = Publish(AutoSaveHealthRecord.Create(
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
            return AutoSaveTerminalResult.RecoveryRequired($"Autosave health update failed: {healthUpdate.Detail}");

        if (pendingRecovery is not null)
        {
            // Clear only the exact recovery set acknowledged by the
            // current terminal publication. A failed or stale-suppressed
            // update must remain actionable for the next exit.
            lock (_gate)
            {
                if (healthGeneration == _currentGeneration &&
                    ReferenceEquals(_pendingRecovery, pendingRecovery))
                    _pendingRecovery = null;
            }
        }
        return result;
    }

    private AutoSaveHealthWriteResult WriteLocked(AutoSaveHealthRecord record)
    {
        AutoSaveHealthWriteResult result;
        try
        {
            result = _store.Write(record);
        }
        catch (Exception ex)
        {
            result = AutoSaveHealthWriteResult.Failed(
                $"Autosave health transition threw: {ex.Message}");
        }

        if (result.Succeeded)
        {
            _lastRecord = record;
        }
        else
        {
            var recovery = record.With(
                status: AutoSaveHealthStatus.RecoveryRequired,
                updatedUtc: DateTime.UtcNow,
                failurePhase: "HealthTransition",
                detail: result.Detail,
                recoveryEvidencePaths: result.RecoveryEvidencePaths);
            _lastRecord = recovery;
            RetainRecoveryLocked(recovery);
        }

        return result;
    }

    private void LogFailure(AutoSaveHealthWriteResult result)
    {
        if (!result.Succeeded)
            _error($"Auto-save health transition failed: {result.Detail}");
    }

    private void RetainRecoveryLocked(AutoSaveHealthRecord recovery)
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
        var prior = _pendingRecovery;
        var merged = MergeRecoveryEntries(
            prior?.RecoveryEntries ?? Array.Empty<AutoSaveHealthRecoveryEntry>(),
            prior?.RecoveryOverflowCount ?? 0,
            new[] { entry }, 0);
        _pendingRecovery = (prior ?? recovery).With(
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

    private static string DescribeRecovery(AutoSaveHealthRecord recovery)
    {
        var text =
            $"operation {recovery.OperationId} ({recovery.Reason}) " +
            $"status={recovery.Status}, intended={recovery.IntendedActors}, " +
            $"written={recovery.WrittenActors}, " +
            $"paths=[{string.Join(",", recovery.AffectedPaths)}], " +
            $"phase={recovery.FailurePhase ?? "HealthTransition"}, " +
            $"detail={recovery.Detail}, " +
            $"evidence=[{string.Join(",", recovery.RecoveryEvidencePaths)}]";
        return text.Length <= 4096 ? text : text[..4096];
    }
}
