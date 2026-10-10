using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Poser.Documents.Files;

/// <summary>Steps of one atomic write, in order; also the failure phase.</summary>
internal enum AtomicWritePhase
{
    CreateTemporary,
    WriteTemporary,
    FlushTemporary,
    ReopenTemporary,
    ReplaceDestination,
    MoveDestination,
    CleanupTemporary,
    CleanupBackup,
}

internal sealed class AtomicWriteOptions
{
    /// <summary>Noun used in failure text, e.g. "pose".</summary>
    public required string Subject { get; init; }

    /// <summary>False refuses an existing destination instead of replacing it.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Validates the flushed temp; returns the failure detail or null.</summary>
    public Func<string, string?>? VerifyTemporary { get; init; }

    /// <summary>True keeps the hidden temp (the new bytes) when the commit
    /// fails, as recovery evidence. Only set it where the caller reports
    /// <see cref="AtomicWriteResult.RecoveryEvidencePaths"/>; otherwise the
    /// temp is deleted so failed saves do not pile up hidden files.</summary>
    public bool KeepTemporaryOnFailure { get; init; }

    /// <summary>Test seam invoked before each step with the path it touches.</summary>
    public Action<AtomicWritePhase, string>? BeforePhase { get; init; }
}

/// <summary>Thrown by a write callback to fail the write with exactly this detail.</summary>
internal sealed class AtomicWriteRefusedException(string detail) : IOException(detail);

internal sealed class AtomicWriteResult
{
    private AtomicWriteResult(
        AtomicWritePhase? phase,
        string? detail,
        string? path,
        Exception? exception,
        IReadOnlyList<string> recoveryEvidencePaths)
    {
        Phase = phase;
        Detail = detail;
        Path = path;
        Exception = exception;
        RecoveryEvidencePaths = recoveryEvidencePaths;
    }

    public bool Succeeded => Phase is null;

    /// <summary>The failing step. A cleanup phase means the new bytes were committed.</summary>
    public AtomicWritePhase? Phase { get; }
    public string? Detail { get; }
    public string? Path { get; }
    public Exception? Exception { get; }

    /// <summary>Temp/backup files that still exist after the failure.</summary>
    public IReadOnlyList<string> RecoveryEvidencePaths { get; }

    /// <summary>True when the new bytes are at the destination, even if a
    /// leftover temp/backup could not be removed.</summary>
    public bool Committed =>
        Phase is null or AtomicWritePhase.CleanupTemporary or AtomicWritePhase.CleanupBackup;

    /// <summary>Throws unless the new bytes were committed.</summary>
    public void ThrowIfFailed()
    {
        if (!Committed)
            throw new IOException(Detail, Exception);
    }

    internal static AtomicWriteResult Success { get; } =
        new(null, null, null, null, Array.Empty<string>());

    internal static AtomicWriteResult Failed(
        AtomicWritePhase phase,
        string detail,
        string? path,
        Exception? exception = null,
        IReadOnlyList<string>? evidence = null) =>
        new(phase, detail, path, exception, evidence ?? Array.Empty<string>());

    internal AtomicWriteResult WithDetail(string detail) =>
        new(Phase, detail, Path, Exception, RecoveryEvidencePaths);

    internal AtomicWriteResult WithEvidence(IReadOnlyList<string> evidence) =>
        new(Phase, Detail, Path, Exception, evidence);
}

/// <summary>
/// The one same-directory atomic write: a unique hidden temp beside the
/// destination, written and flushed to disk, optionally validated, then
/// <c>Replace</c> with a unique backup (existing destination) or <c>Move</c>.
/// The committed bytes are confirmed by length and SHA-256 before the backup
/// is deleted; whatever temp/backup survives a failure is reported as
/// recovery evidence. The original destination is never opened for writing.
/// </summary>
internal static class AtomicFile
{
    public static AtomicWriteResult Write(
        IAtomicFileSystem fileSystem,
        string destination,
        byte[] bytes,
        AtomicWriteOptions options) =>
        Write(fileSystem, destination, stream => stream.Write(bytes), options);

    public static AtomicWriteResult Write(
        IAtomicFileSystem fileSystem,
        string destination,
        Action<Stream> write,
        AtomicWriteOptions options)
    {
        var subject = options.Subject;
        string fullDestination;
        string temporary;
        string backup;
        try
        {
            fullDestination = Path.GetFullPath(destination);
            var directory = Path.GetDirectoryName(fullDestination)
                ?? throw new IOException("The destination has no parent directory.");
            var fileName = Path.GetFileName(fullDestination);
            temporary = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.tmp");
            backup = Path.Combine(directory, $".{fileName}.{Guid.NewGuid():N}.bak");
        }
        catch (Exception ex)
        {
            return AtomicWriteResult.Failed(
                AtomicWritePhase.CreateTemporary,
                $"Preparing the atomic {subject} paths failed: {ex.Message}",
                destination,
                ex);
        }

        var phase = AtomicWritePhase.CreateTemporary;
        AtomicWriteResult? failure = null;
        FileStamp stamp = default;
        var existing = false;
        try
        {
            Before(options, phase, temporary);
            using (var stream = fileSystem.CreateNew(temporary))
            {
                phase = AtomicWritePhase.WriteTemporary;
                Before(options, phase, temporary);
                write(stream);

                phase = AtomicWritePhase.FlushTemporary;
                Before(options, phase, temporary);
                stream.Flush();
                fileSystem.FlushToDisk(stream);
            }

            phase = AtomicWritePhase.ReopenTemporary;
            Before(options, phase, temporary);
            if (options.VerifyTemporary?.Invoke(temporary) is { } invalid)
            {
                failure = AtomicWriteResult.Failed(phase, invalid, temporary);
            }
            else if (StampOf(fileSystem, temporary) is not { } observed)
            {
                failure = AtomicWriteResult.Failed(
                    phase, $"The atomic {subject} temp could not be checksummed.", temporary);
            }
            else
            {
                stamp = observed;
                existing = options.Overwrite && fileSystem.Exists(fullDestination);
            }
        }
        catch (AtomicWriteRefusedException ex)
        {
            failure = AtomicWriteResult.Failed(phase, ex.Message, temporary, ex);
        }
        catch (Exception ex)
        {
            failure = AtomicWriteResult.Failed(
                phase,
                $"Atomic {subject} write failed during {LegacyName(phase)}: {ex.Message}",
                temporary,
                ex);
        }

        if (failure is not null)
            return CleanupPrecommitFailure(fileSystem, options, failure, temporary);

        return existing
            ? CommitExisting(fileSystem, options, stamp, temporary, fullDestination, backup)
            : CommitNew(fileSystem, options, stamp, temporary, fullDestination);
    }

    private static AtomicWriteResult CommitExisting(
        IAtomicFileSystem fileSystem,
        AtomicWriteOptions options,
        FileStamp stamp,
        string temporary,
        string destination,
        string backup)
    {
        const AtomicWritePhase phase = AtomicWritePhase.ReplaceDestination;
        try
        {
            Before(options, phase, destination);
            fileSystem.Replace(temporary, destination, backup);
            if (!Matches(fileSystem, destination, stamp))
            {
                return FailCommit(fileSystem, options, AtomicWriteResult.Failed(
                    phase,
                    "Replace returned without the validated bytes at the destination.",
                    destination), temporary, backup);
            }
        }
        catch (Exception ex)
        {
            // Replace can report failure after the swap landed; trust the bytes.
            if (!Matches(fileSystem, destination, stamp))
            {
                return FailCommit(fileSystem, options, AtomicWriteResult.Failed(
                    phase,
                    $"Atomic {options.Subject} replace failed: {ex.Message}",
                    destination,
                    ex), temporary, backup);
            }
        }
        return CleanupConfirmedCommit(fileSystem, options, stamp, destination, temporary, backup);
    }

    private static AtomicWriteResult CommitNew(
        IAtomicFileSystem fileSystem,
        AtomicWriteOptions options,
        FileStamp stamp,
        string temporary,
        string destination)
    {
        const AtomicWritePhase phase = AtomicWritePhase.MoveDestination;
        try
        {
            Before(options, phase, destination);
            fileSystem.Move(temporary, destination);
            if (!Matches(fileSystem, destination, stamp))
            {
                return FailCommit(fileSystem, options, AtomicWriteResult.Failed(
                    phase,
                    "Move returned without the validated bytes at the destination.",
                    destination), temporary, null);
            }
        }
        catch (Exception ex)
        {
            // Without overwrite the move is refused onto an existing file (even
            // an identical one); the temp is ours and nothing to recover.
            if (!options.Overwrite)
            {
                return CleanupPrecommitFailure(fileSystem, options, AtomicWriteResult.Failed(
                    phase,
                    $"Atomic {options.Subject} move failed: {ex.Message}",
                    destination,
                    ex), temporary);
            }
            if (!Matches(fileSystem, destination, stamp))
            {
                return FailCommit(fileSystem, options, AtomicWriteResult.Failed(
                    phase,
                    $"Atomic {options.Subject} move failed: {ex.Message}",
                    destination,
                    ex), temporary, null);
            }
        }
        return CleanupConfirmedCommit(fileSystem, options, stamp, destination, temporary, null);
    }

    // A failed commit keeps any backup (it may hold the original); the temp
    // only holds the rejected new bytes and is kept only when asked for.
    private static AtomicWriteResult FailCommit(
        IAtomicFileSystem fileSystem,
        AtomicWriteOptions options,
        AtomicWriteResult failure,
        string temporary,
        string? backup)
    {
        if (!options.KeepTemporaryOnFailure)
        {
            try
            {
                fileSystem.Delete(temporary);
            }
            catch (Exception cleanup)
            {
                failure = failure.WithDetail(
                    failure.Detail + $" The temp could not be deleted: {cleanup.Message}");
            }
        }
        return failure.WithEvidence(backup is null
            ? Surviving(fileSystem, temporary)
            : Surviving(fileSystem, temporary, backup));
    }

    private static AtomicWriteResult CleanupConfirmedCommit(
        IAtomicFileSystem fileSystem,
        AtomicWriteOptions options,
        FileStamp stamp,
        string destination,
        string temporary,
        string? backup)
    {
        var errors = new List<string>();
        AtomicWritePhase? failed = null;
        try
        {
            Before(options, AtomicWritePhase.CleanupTemporary, temporary);
            fileSystem.Delete(temporary);
        }
        catch (Exception ex)
        {
            failed = AtomicWritePhase.CleanupTemporary;
            errors.Add($"{temporary}: {ex.Message}");
        }

        if (backup is not null)
        {
            if (!Matches(fileSystem, destination, stamp))
            {
                failed ??= AtomicWritePhase.CleanupBackup;
                errors.Add($"{backup}: destination postcondition changed before backup cleanup");
            }
            else
            {
                try
                {
                    Before(options, AtomicWritePhase.CleanupBackup, backup);
                    fileSystem.Delete(backup);
                }
                catch (Exception ex)
                {
                    failed ??= AtomicWritePhase.CleanupBackup;
                    errors.Add($"{backup}: {ex.Message}");
                }
            }
        }

        if (failed is null)
            return AtomicWriteResult.Success;

        return AtomicWriteResult.Failed(
            failed.Value,
            $"The {options.Subject} was committed, but recovery-file cleanup failed: " +
            string.Join("; ", errors),
            null,
            evidence: backup is null
                ? Surviving(fileSystem, temporary)
                : Surviving(fileSystem, temporary, backup));
    }

    private static AtomicWriteResult CleanupPrecommitFailure(
        IAtomicFileSystem fileSystem,
        AtomicWriteOptions options,
        AtomicWriteResult failure,
        string temporary)
    {
        try
        {
            Before(options, AtomicWritePhase.CleanupTemporary, temporary);
            fileSystem.Delete(temporary);
        }
        catch (Exception cleanup)
        {
            failure = failure.WithDetail(
                failure.Detail + $" The temp could not be deleted: {cleanup.Message}");
        }
        return failure.WithEvidence(Surviving(fileSystem, temporary));
    }

    private static void Before(AtomicWriteOptions options, AtomicWritePhase phase, string path) =>
        options.BeforePhase?.Invoke(phase, path);

    // Failure text predates this primitive; keep the names stores reported.
    private static string LegacyName(AtomicWritePhase phase) => phase switch
    {
        AtomicWritePhase.CreateTemporary => "TemporaryCreate",
        AtomicWritePhase.WriteTemporary => "TemporaryWrite",
        AtomicWritePhase.FlushTemporary => "TemporaryFlush",
        AtomicWritePhase.ReopenTemporary => "TemporaryReopen",
        _ => phase.ToString(),
    };

    private readonly record struct FileStamp(long Length, string Digest);

    // Streamed length + SHA-256 so multi-hundred-MB scenes are never held in memory.
    private static FileStamp? StampOf(IAtomicFileSystem fileSystem, string path)
    {
        try
        {
            using var stream = fileSystem.OpenRead(path);
            var length = stream.Length;
            var digest = SHA256.HashData(stream);
            return new FileStamp(length, Convert.ToHexString(digest));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool Matches(IAtomicFileSystem fileSystem, string path, FileStamp expected) =>
        StampOf(fileSystem, path) is { } actual && actual == expected;

    private static IReadOnlyList<string> Surviving(
        IAtomicFileSystem fileSystem,
        params string[] candidates) =>
        Array.AsReadOnly(candidates
            .Distinct(StringComparer.Ordinal)
            .Where(candidate => !IsMissing(fileSystem, candidate))
            .ToArray());

    // Anything other than a definite "not found" counts as surviving.
    private static bool IsMissing(IAtomicFileSystem fileSystem, string path)
    {
        try
        {
            using var stream = fileSystem.OpenRead(path);
            return false;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }
}
