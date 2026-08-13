using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poser.Files;

public enum AutoSaveHealthStatus
{
    Pending,
    Queued,
    DispatchAccepted,
    Written,
    Cleaned,
    RecoveryRequired,
    Cancelled,
}

/// <summary>One independently attributable recovery obligation.</summary>
public sealed class AutoSaveHealthRecoveryEntry
{
    [JsonConstructor]
    internal AutoSaveHealthRecoveryEntry(
        string operationId,
        string reason,
        AutoSaveHealthStatus status,
        DateTime createdUtc,
        DateTime updatedUtc,
        int intendedActors,
        int writtenActors,
        IReadOnlyList<string>? affectedPaths,
        string? failurePhase,
        string? detail,
        IReadOnlyList<string>? recoveryEvidencePaths)
    {
        OperationId = Limit(operationId, 128);
        Reason = Limit(reason, 128);
        Status = status;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        IntendedActors = Math.Clamp(intendedActors, 0, 8192);
        WrittenActors = Math.Clamp(writtenActors, 0, IntendedActors);
        AffectedPaths = Freeze(affectedPaths);
        FailurePhase = failurePhase is null ? null : Limit(failurePhase, 128);
        Detail = detail is null ? null : Limit(detail, 4096);
        RecoveryEvidencePaths = Freeze(recoveryEvidencePaths);
    }

    public string OperationId { get; }
    public string Reason { get; }
    public AutoSaveHealthStatus Status { get; }
    public DateTime CreatedUtc { get; }
    public DateTime UpdatedUtc { get; }
    public int IntendedActors { get; }
    public int WrittenActors { get; }
    public IReadOnlyList<string> AffectedPaths { get; }
    public string? FailurePhase { get; }
    public string? Detail { get; }
    public IReadOnlyList<string> RecoveryEvidencePaths { get; }

    internal static AutoSaveHealthRecoveryEntry Create(
        string operationId,
        string reason,
        AutoSaveHealthStatus status,
        DateTime createdUtc,
        DateTime updatedUtc,
        int intendedActors = 0,
        int writtenActors = 0,
        IEnumerable<string>? affectedPaths = null,
        string? failurePhase = null,
        string? detail = null,
        IEnumerable<string>? recoveryEvidencePaths = null) =>
        new(operationId, reason, status, createdUtc, updatedUtc, intendedActors,
            writtenActors, affectedPaths?.ToArray(), failurePhase, detail,
            recoveryEvidencePaths?.ToArray());

    private static string Limit(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static IReadOnlyList<string> Freeze(IEnumerable<string>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(256)
            .Select(static value => Limit(value, 1024))
            .ToArray());
}

/// <summary>Immutable bounded observation of one autosave operation.</summary>
public sealed class AutoSaveHealthRecord
{
    internal const int MaxRecoveryEntries = 4;

    [JsonConstructor]
    internal AutoSaveHealthRecord(
        string operationId,
        string reason,
        AutoSaveHealthStatus status,
        DateTime createdUtc,
        DateTime updatedUtc,
        int intendedActors,
        int writtenActors,
        IReadOnlyList<string>? affectedPaths,
        string? failurePhase,
        string? detail,
        IReadOnlyList<string>? recoveryEvidencePaths,
        IReadOnlyList<AutoSaveHealthRecoveryEntry>? recoveryEntries,
        int recoveryOverflowCount)
    {
        OperationId = Limit(operationId, 128);
        Reason = Limit(reason, 128);
        Status = status;
        CreatedUtc = createdUtc;
        UpdatedUtc = updatedUtc;
        IntendedActors = Math.Clamp(intendedActors, 0, 8192);
        WrittenActors = Math.Clamp(writtenActors, 0, IntendedActors);
        AffectedPaths = Freeze(affectedPaths);
        FailurePhase = failurePhase is null ? null : Limit(failurePhase, 128);
        Detail = detail is null ? null : Limit(detail, 4096);
        RecoveryEvidencePaths = Freeze(recoveryEvidencePaths);
        var incomingRecoveryEntries = recoveryEntries?.ToArray() ?? Array.Empty<AutoSaveHealthRecoveryEntry>();
        RecoveryEntries = FreezeEntries(incomingRecoveryEntries);
        var discardedRecoveryEntries = Math.Max(
            0, incomingRecoveryEntries.Length - MaxRecoveryEntries);
        var overflow = Math.Max(0L, (long)recoveryOverflowCount) +
            Math.Max(0L, (long)discardedRecoveryEntries);
        RecoveryOverflowCount = (int)Math.Min((long)int.MaxValue, overflow);
    }

    public string OperationId { get; }
    public string Reason { get; }
    public AutoSaveHealthStatus Status { get; }
    public DateTime CreatedUtc { get; }
    public DateTime UpdatedUtc { get; }
    public int IntendedActors { get; }
    public int WrittenActors { get; }
    public IReadOnlyList<string> AffectedPaths { get; }
    public string? FailurePhase { get; }
    public string? Detail { get; }
    public IReadOnlyList<string> RecoveryEvidencePaths { get; }
    public IReadOnlyList<AutoSaveHealthRecoveryEntry> RecoveryEntries { get; }
    public int RecoveryOverflowCount { get; }

    internal static AutoSaveHealthRecord Create(
        string operationId,
        string reason,
        AutoSaveHealthStatus status,
        DateTime createdUtc,
        DateTime updatedUtc,
        int intendedActors = 0,
        int writtenActors = 0,
        IEnumerable<string>? affectedPaths = null,
        string? failurePhase = null,
        string? detail = null,
        IEnumerable<string>? recoveryEvidencePaths = null,
        IEnumerable<AutoSaveHealthRecoveryEntry>? recoveryEntries = null,
        int recoveryOverflowCount = 0) =>
        new(
            operationId,
            reason,
            status,
            createdUtc,
            updatedUtc,
            intendedActors,
            writtenActors,
            affectedPaths?.ToArray(),
            failurePhase,
            detail,
            recoveryEvidencePaths?.ToArray(),
            recoveryEntries?.ToArray(),
            recoveryOverflowCount);

    internal AutoSaveHealthRecord With(
        AutoSaveHealthStatus? status = null,
        DateTime? updatedUtc = null,
        int? writtenActors = null,
        IEnumerable<string>? affectedPaths = null,
        string? failurePhase = null,
        string? detail = null,
        IEnumerable<string>? recoveryEvidencePaths = null,
        IEnumerable<AutoSaveHealthRecoveryEntry>? recoveryEntries = null,
        int? recoveryOverflowCount = null) =>
        Create(
            OperationId,
            Reason,
            status ?? Status,
            CreatedUtc,
            updatedUtc ?? UpdatedUtc,
            IntendedActors,
            writtenActors ?? WrittenActors,
            affectedPaths ?? AffectedPaths,
            failurePhase ?? FailurePhase,
            detail ?? Detail,
            recoveryEvidencePaths ?? RecoveryEvidencePaths,
            recoveryEntries ?? RecoveryEntries,
            recoveryOverflowCount ?? RecoveryOverflowCount);

    private static string Limit(string? value, int max) =>
        string.IsNullOrEmpty(value) ? string.Empty : value.Length <= max ? value : value[..max];

    private static IReadOnlyList<string> Freeze(IEnumerable<string>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Take(256)
            .Select(static value => Limit(value, 1024))
            .ToArray());

    private static IReadOnlyList<AutoSaveHealthRecoveryEntry> FreezeEntries(
        IEnumerable<AutoSaveHealthRecoveryEntry>? values) =>
        Array.AsReadOnly((values ?? Array.Empty<AutoSaveHealthRecoveryEntry>())
            .Take(MaxRecoveryEntries)
            .ToArray());
}

public sealed class AutoSaveHealthWriteResult
{
    private AutoSaveHealthWriteResult(
        bool succeeded,
        string? detail,
        IReadOnlyList<string> recoveryEvidencePaths)
    {
        Succeeded = succeeded;
        Detail = detail;
        RecoveryEvidencePaths = recoveryEvidencePaths;
    }

    public bool Succeeded { get; }
    public string? Detail { get; }
    public IReadOnlyList<string> RecoveryEvidencePaths { get; }

    internal static AutoSaveHealthWriteResult Success() =>
        new(true, null, Array.Empty<string>());

    internal static AutoSaveHealthWriteResult Failed(
        string detail,
        IEnumerable<string>? evidence = null) =>
        new(false, detail, Array.AsReadOnly((evidence ?? Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray()));
}

public sealed class AutoSaveHealthRecoveryResult
{
    internal AutoSaveHealthRecoveryResult(
        AutoSaveHealthRecord? record,
        AutoSaveHealthWriteResult? write)
    {
        Record = record;
        Write = write;
    }

    public AutoSaveHealthRecord? Record { get; }
    public AutoSaveHealthWriteResult? Write { get; }

    /// <summary>True when startup found a non-terminal record and attempted promotion.</summary>
    public bool PromotionAttempted => Write is not null;

    /// <summary>
    /// True for a successful observation, including a record that needed no
    /// promotion. A failed promotion remains distinguishable through
    /// <see cref="PromotionAttempted"/> and <see cref="Write"/>.
    /// </summary>
    public bool Succeeded => !PromotionAttempted || Write!.Succeeded;
}

internal interface IAutoSaveHealthFileSystem
{
    Stream OpenRead(string path);
    Stream CreateNew(string path);
    void FlushToDisk(Stream stream);
    bool Exists(string path);
    void Replace(string source, string destination, string backup);
    void Move(string source, string destination);
    void Delete(string path);
}

internal sealed class SystemAutoSaveHealthFileSystem : IAutoSaveHealthFileSystem
{
    public Stream OpenRead(string path) => new FileStream(
        path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);

    public Stream CreateNew(string path) => new FileStream(
        path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.SequentialScan);

    public void FlushToDisk(Stream stream) => ((FileStream)stream).Flush(flushToDisk: true);
    public bool Exists(string path) => File.Exists(path);
    public void Replace(string source, string destination, string backup) => File.Replace(source, destination, backup);
    public void Move(string source, string destination) => File.Move(source, destination);
    public void Delete(string path) => File.Delete(path);
}

/// <summary>Bounded atomic root-level autosave health storage.</summary>
public sealed class AutoSaveHealthStore
{
    public const string FileName = ".autosave-health.json";
    private const long MaxBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        MaxDepth = 32,
        PropertyNamingPolicy = null,
    };

    private readonly IAutoSaveHealthFileSystem _fileSystem;

    public AutoSaveHealthStore(string rootDirectory)
        : this(rootDirectory, new SystemAutoSaveHealthFileSystem())
    {
    }

    internal AutoSaveHealthStore(string rootDirectory, IAutoSaveHealthFileSystem fileSystem)
    {
        RootDirectory = rootDirectory;
        HealthPath = Path.Combine(rootDirectory, FileName);
        _fileSystem = fileSystem;
    }

    public string RootDirectory { get; }
    public string HealthPath { get; }

    public AutoSaveHealthRecord? Read()
    {
        try
        {
            if (!_fileSystem.Exists(HealthPath))
                return null;
            using var stream = _fileSystem.OpenRead(HealthPath);
            if (stream.Length <= 0 || stream.Length > MaxBytes)
                return null;
            return JsonSerializer.Deserialize<AutoSaveHealthRecord>(stream, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public AutoSaveHealthWriteResult Write(AutoSaveHealthRecord record)
    {
        string? temp = null;
        string? backup = null;
        var replaceAttempted = false;
        var moveAttempted = false;
        var tempCleanupAttempted = false;
        try
        {
            byte[] bytes;
            try
            {
                bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            }
            catch (Exception ex)
            {
                return AutoSaveHealthWriteResult.Failed(
                    $"Autosave health serialization failed: {ex.Message}");
            }

            if (bytes.Length == 0 || bytes.Length > MaxBytes)
                return AutoSaveHealthWriteResult.Failed("Autosave health record exceeded its size limit.");

            Directory.CreateDirectory(RootDirectory);
            temp = Path.Combine(RootDirectory, $".{FileName}.{Guid.NewGuid():N}.tmp");
            using (var stream = _fileSystem.CreateNew(temp))
            {
                stream.Write(bytes, 0, bytes.Length);
                _fileSystem.FlushToDisk(stream);
            }

            using (var verify = _fileSystem.OpenRead(temp))
            {
                var roundTrip = JsonSerializer.Deserialize<AutoSaveHealthRecord>(verify, JsonOptions);
                if (!RecordsEqual(record, roundTrip))
                    return CleanupPrecommitFailure(
                        "Autosave health validation failed.", ref temp, ref tempCleanupAttempted);
            }

            if (_fileSystem.Exists(HealthPath))
            {
                backup = Path.Combine(RootDirectory, $".{FileName}.{Guid.NewGuid():N}.bak");
                replaceAttempted = true;
                _fileSystem.Replace(temp, HealthPath, backup);
            }
            else
            {
                moveAttempted = true;
                _fileSystem.Move(temp, HealthPath);
            }

            try
            {
                if (!TryReadMatchingRecord(HealthPath, record))
                    return AutoSaveHealthWriteResult.Failed(
                        "Autosave health commit could not confirm its destination.",
                        Observe(HealthPath).Concat(Observe(backup)).ToArray());
            }
            catch (Exception ex)
            {
                return AutoSaveHealthWriteResult.Failed(
                    $"Autosave health commit confirmation failed: {ex.Message}",
                    Observe(HealthPath).Concat(Observe(backup)).ToArray());
            }

            temp = null;
            if (backup is not null)
            {
                try
                {
                    _fileSystem.Delete(backup);
                }
                catch (Exception ex)
                {
                    return AutoSaveHealthWriteResult.Failed(
                        $"Autosave health backup cleanup failed: {ex.Message}",
                        Observe(backup));
                }
            }
            return AutoSaveHealthWriteResult.Success();
        }
        catch (Exception ex)
        {
            var evidence = new List<string>();
            if (replaceAttempted)
            {
                evidence.AddRange(Observe(HealthPath));
                evidence.AddRange(Observe(temp));
                evidence.AddRange(Observe(backup));
            }
            else
            {
                evidence.AddRange(Observe(temp));
                evidence.AddRange(Observe(backup));
            }

            // A pre-commit temp is not recovery evidence once cleanup succeeds.
            // Remove it before constructing the result so the returned paths
            // describe only files that still exist.
            if (temp is not null && !replaceAttempted && !moveAttempted)
            {
                tempCleanupAttempted = true;
                try
                {
                    _fileSystem.Delete(temp);
                    evidence.RemoveAll(path => string.Equals(path, temp, StringComparison.Ordinal));
                    temp = null;
                }
                catch (Exception cleanup)
                {
                    evidence.Add(temp!);
                    ex = new IOException($"{ex.Message}; temp cleanup failed: {cleanup.Message}", ex);
                }
            }
            return AutoSaveHealthWriteResult.Failed(ex.Message, evidence);
        }
        finally
        {
            if (temp is not null && !tempCleanupAttempted && !replaceAttempted && !moveAttempted)
            {
                try { _fileSystem.Delete(temp); }
                catch { /* temp remains recoverable through the failure evidence */ }
            }
        }
    }

    public AutoSaveHealthRecoveryResult RecoverStale()
    {
        var current = Read();
        if (current is null || current.Status is not
            (AutoSaveHealthStatus.Pending or AutoSaveHealthStatus.Queued or AutoSaveHealthStatus.DispatchAccepted))
            return new AutoSaveHealthRecoveryResult(current, null);

        var recovered = AutoSaveHealthRecord.Create(
            current.OperationId,
            current.Reason,
            AutoSaveHealthStatus.RecoveryRequired,
            current.CreatedUtc,
            DateTime.UtcNow,
            current.IntendedActors,
            current.WrittenActors,
            current.AffectedPaths,
            "Interrupted",
            "Autosave operation was interrupted before a terminal health record was written.",
            current.RecoveryEvidencePaths,
            current.RecoveryEntries,
            current.RecoveryOverflowCount);
        var write = Write(recovered);
        return new AutoSaveHealthRecoveryResult(recovered, write);
    }

    private AutoSaveHealthWriteResult CleanupPrecommitFailure(
        string detail,
        ref string? temp,
        ref bool cleanupAttempted)
    {
        if (temp is null)
            return AutoSaveHealthWriteResult.Failed(detail);
        var path = temp;
        cleanupAttempted = true;
        try
        {
            _fileSystem.Delete(path);
            temp = null;
            return AutoSaveHealthWriteResult.Failed(detail);
        }
        catch (Exception cleanup)
        {
            return AutoSaveHealthWriteResult.Failed(
                $"{detail}; temp cleanup failed: {cleanup.Message}", Observe(path));
        }
    }

    private bool TryReadMatchingRecord(string path, AutoSaveHealthRecord expected)
    {
        if (!_fileSystem.Exists(path))
            return false;
        using var stream = _fileSystem.OpenRead(path);
        if (stream.Length <= 0 || stream.Length > MaxBytes)
            return false;
        return RecordsEqual(expected,
            JsonSerializer.Deserialize<AutoSaveHealthRecord>(stream, JsonOptions));
    }

    private static bool RecordsEqual(AutoSaveHealthRecord expected, AutoSaveHealthRecord? actual) =>
        actual is not null &&
        expected.OperationId == actual.OperationId &&
        expected.Reason == actual.Reason &&
        expected.Status == actual.Status &&
        expected.CreatedUtc == actual.CreatedUtc &&
        expected.UpdatedUtc == actual.UpdatedUtc &&
        expected.IntendedActors == actual.IntendedActors &&
        expected.WrittenActors == actual.WrittenActors &&
        ListsEqual(expected.AffectedPaths, actual.AffectedPaths) &&
        expected.FailurePhase == actual.FailurePhase &&
        expected.Detail == actual.Detail &&
        ListsEqual(expected.RecoveryEvidencePaths, actual.RecoveryEvidencePaths) &&
        expected.RecoveryOverflowCount == actual.RecoveryOverflowCount &&
        expected.RecoveryEntries.Count == actual.RecoveryEntries.Count &&
        expected.RecoveryEntries.Zip(actual.RecoveryEntries).All(pair =>
            pair.First.OperationId == pair.Second.OperationId &&
            pair.First.Reason == pair.Second.Reason &&
            pair.First.Status == pair.Second.Status &&
            pair.First.CreatedUtc == pair.Second.CreatedUtc &&
            pair.First.UpdatedUtc == pair.Second.UpdatedUtc &&
            pair.First.IntendedActors == pair.Second.IntendedActors &&
            pair.First.WrittenActors == pair.Second.WrittenActors &&
            ListsEqual(pair.First.AffectedPaths, pair.Second.AffectedPaths) &&
            pair.First.FailurePhase == pair.Second.FailurePhase &&
            pair.First.Detail == pair.Second.Detail &&
            ListsEqual(pair.First.RecoveryEvidencePaths, pair.Second.RecoveryEvidencePaths));

    private static bool ListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.Count == right.Count && left.SequenceEqual(right, StringComparer.Ordinal);

    private IReadOnlyList<string> Observe(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Array.Empty<string>();
        try
        {
            return _fileSystem.Exists(path) ? new[] { path } : Array.Empty<string>();
        }
        catch
        {
            return new[] { path };
        }
    }
}
