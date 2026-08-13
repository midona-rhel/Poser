using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Poser.Files;

/// <summary>Terminal/read-model states persisted for one autosave operation.</summary>
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

/// <summary>Bounded immutable health evidence for the most recent operation.</summary>
public sealed class AutoSaveHealthRecord
{
    public string OperationId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public AutoSaveHealthStatus Status { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public int IntendedActors { get; init; }
    public int WrittenActors { get; init; }
    public IReadOnlyList<string> AffectedPaths { get; init; } = Array.Empty<string>();
    public string? FailurePhase { get; init; }
    public string? Detail { get; init; }
    public IReadOnlyList<string> RecoveryEvidencePaths { get; init; } = Array.Empty<string>();

    internal AutoSaveHealthRecord Bounded()
    {
        static IReadOnlyList<string> Bound(IEnumerable<string> values) =>
            values.Where(static value => !string.IsNullOrWhiteSpace(value))
                .Take(256)
                .Select(static value => value.Length > 1024 ? value[..1024] : value)
                .ToArray();

        return new AutoSaveHealthRecord
        {
            OperationId = OperationId.Length > 128 ? OperationId[..128] : OperationId,
            Reason = Reason.Length > 128 ? Reason[..128] : Reason,
            Status = Status,
            CreatedUtc = CreatedUtc,
            UpdatedUtc = UpdatedUtc,
            IntendedActors = Math.Clamp(IntendedActors, 0, 8192),
            WrittenActors = Math.Clamp(WrittenActors, 0, 8192),
            AffectedPaths = Bound(AffectedPaths),
            FailurePhase = FailurePhase is null ? null : (FailurePhase.Length > 128 ? FailurePhase[..128] : FailurePhase),
            Detail = Detail is null ? null : (Detail.Length > 4096 ? Detail[..4096] : Detail),
            RecoveryEvidencePaths = Bound(RecoveryEvidencePaths),
        };
    }
}

public sealed class AutoSaveHealthWriteResult
{
    private AutoSaveHealthWriteResult(bool succeeded, string? detail, string? evidencePath)
    {
        Succeeded = succeeded;
        Detail = detail;
        RecoveryEvidencePath = evidencePath;
    }

    public bool Succeeded { get; }
    public string? Detail { get; }
    public string? RecoveryEvidencePath { get; }

    internal static AutoSaveHealthWriteResult Success() => new(true, null, null);
    internal static AutoSaveHealthWriteResult Failed(string detail, string? evidence = null) => new(false, detail, evidence);
}

/// <summary>
/// Root-level atomic autosave health record. It deliberately owns only the
/// health schema and replacement mechanics; pose codecs remain in
/// <see cref="AtomicPoseFileStore"/>.
/// </summary>
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

    public AutoSaveHealthStore(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        HealthPath = Path.Combine(rootDirectory, FileName);
    }

    public string RootDirectory { get; }
    public string HealthPath { get; }

    public AutoSaveHealthRecord? Read()
    {
        try
        {
            if (!File.Exists(HealthPath))
                return null;
            var info = new FileInfo(HealthPath);
            if (info.Length <= 0 || info.Length > MaxBytes)
                return null;
            using var stream = new FileStream(HealthPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var record = JsonSerializer.Deserialize<AutoSaveHealthRecord>(stream, JsonOptions);
            return record?.Bounded();
        }
        catch
        {
            return null;
        }
    }

    public AutoSaveHealthWriteResult Write(AutoSaveHealthRecord record)
    {
        var bounded = record.Bounded();
        string? temp = null;
        try
        {
            Directory.CreateDirectory(RootDirectory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(bounded, JsonOptions);
            if (bytes.Length == 0 || bytes.Length > MaxBytes)
                return AutoSaveHealthWriteResult.Failed("Autosave health record exceeded its size limit.");

            temp = Path.Combine(RootDirectory, $".{FileName}.{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            using (var verify = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var roundTrip = JsonSerializer.Deserialize<AutoSaveHealthRecord>(verify, JsonOptions);
                if (roundTrip is null || roundTrip.OperationId != bounded.OperationId || roundTrip.Status != bounded.Status)
                    return AutoSaveHealthWriteResult.Failed("Autosave health validation failed.", temp);
            }

            if (File.Exists(HealthPath))
                File.Replace(temp, HealthPath, null);
            else
                File.Move(temp, HealthPath);
            temp = null;
            return AutoSaveHealthWriteResult.Success();
        }
        catch (Exception ex)
        {
            return AutoSaveHealthWriteResult.Failed(ex.Message, temp);
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch { /* returned as evidence above */ }
            }
        }
    }

    /// <summary>Promotes stale nonterminal work after startup.</summary>
    public AutoSaveHealthRecord? RecoverStale()
    {
        var current = Read();
        if (current is null || current.Status is not (AutoSaveHealthStatus.Pending or AutoSaveHealthStatus.Queued or AutoSaveHealthStatus.DispatchAccepted))
            return current;

        var recovered = new AutoSaveHealthRecord
        {
            OperationId = current.OperationId,
            Reason = current.Reason,
            Status = AutoSaveHealthStatus.RecoveryRequired,
            CreatedUtc = current.CreatedUtc,
            UpdatedUtc = DateTime.UtcNow,
            IntendedActors = current.IntendedActors,
            WrittenActors = current.WrittenActors,
            AffectedPaths = current.AffectedPaths,
            FailurePhase = "Interrupted",
            Detail = "Autosave operation was interrupted before a terminal health record was written.",
            RecoveryEvidencePaths = current.RecoveryEvidencePaths,
        };
        return Write(recovered).Succeeded ? recovered : recovered;
    }
}
