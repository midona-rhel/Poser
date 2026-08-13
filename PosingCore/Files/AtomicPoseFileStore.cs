using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Poser.Files;

public enum PoseFileStoreFailureKind
{
    Read,
    SizeLimit,
    Json,
    Validation,
    Serialization,
    TemporaryCreate,
    TemporaryWrite,
    TemporaryFlush,
    TemporaryReopen,
    Replace,
    Move,
}

public sealed record PoseFileStoreFailure(
    PoseFileStoreFailureKind Kind,
    string Detail,
    string? Path = null);

public readonly record struct PoseFileReadOutcome(
    PoseFile? Pose,
    PoseFileStoreFailure? Failure)
{
    public bool Succeeded => Pose is not null && Failure is null;
}

public readonly record struct PoseFileWriteOutcome(
    bool Succeeded,
    PoseFileStoreFailure? Failure,
    string? RecoveryEvidencePath = null);

internal enum PoseFileStorePhase
{
    Serialize,
    CreateTemporary,
    WriteTemporary,
    FlushTemporary,
    ReopenTemporary,
    ReplaceDestination,
    MoveDestination,
    CleanupTemporary,
}

/// <summary>
/// Typed ordinary-pose codec and same-directory atomic store. The optional
/// phase observer is an internal test seam; production uses <see cref="Default"/>.
/// </summary>
public sealed class AtomicPoseFileStore
{
    public static AtomicPoseFileStore Default { get; } = new();

    private readonly Action<PoseFileStorePhase, string?>? _beforePhase;

    public AtomicPoseFileStore()
    {
    }

    internal AtomicPoseFileStore(
        Action<PoseFileStorePhase, string?> beforePhase)
    {
        _beforePhase = beforePhase;
    }

    public PoseFileReadOutcome Read(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            if (stream.Length > PoseFileLimits.MaxFileBytes)
            {
                return ReadFailure(
                    PoseFileStoreFailureKind.SizeLimit,
                    $"The pose file is {stream.Length} bytes " +
                    $"(limit {PoseFileLimits.MaxFileBytes}).",
                    path);
            }

            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                return ReadFailure(
                    PoseFileStoreFailureKind.SizeLimit,
                    "The pose file changed while it was being read.",
                    path);
            }
            return Decode(bytes, path);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Read,
                $"Reading the pose file failed: {ex.Message}",
                path);
        }
    }

    public PoseFileReadOutcome Parse(string json)
    {
        if (json is null)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Json,
                "The pose JSON is null.");
        }

        try
        {
            var byteCount = Encoding.UTF8.GetByteCount(json);
            if (byteCount > PoseFileLimits.MaxFileBytes)
            {
                return ReadFailure(
                    PoseFileStoreFailureKind.SizeLimit,
                    $"The pose JSON is {byteCount} bytes " +
                    $"(limit {PoseFileLimits.MaxFileBytes}).");
            }
            return Decode(Encoding.UTF8.GetBytes(json), path: null);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Json,
                $"Parsing the pose JSON failed: {ex.Message}");
        }
    }

    public PoseFileWriteOutcome Write(PoseFile pose, string destination)
    {
        var validation = PoseFileValidation.Validate(pose);
        if (!validation.Succeeded)
        {
            return WriteFailure(
                PoseFileStoreFailureKind.Validation,
                validation.Failure!.Detail,
                destination);
        }

        byte[] bytes;
        try
        {
            Before(PoseFileStorePhase.Serialize, destination);
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                pose,
                PoseFile.SerializerOptions);
        }
        catch (Exception ex)
        {
            return WriteFailure(
                PoseFileStoreFailureKind.Serialization,
                $"Serializing the pose failed: {ex.Message}",
                destination);
        }

        if (bytes.LongLength > PoseFileLimits.MaxFileBytes)
        {
            return WriteFailure(
                PoseFileStoreFailureKind.SizeLimit,
                $"The serialized pose is {bytes.LongLength} bytes " +
                $"(limit {PoseFileLimits.MaxFileBytes}).",
                destination);
        }

        var encoded = Decode(bytes, destination);
        if (!encoded.Succeeded)
        {
            return WriteFailure(
                PoseFileStoreFailureKind.Serialization,
                $"The serialized pose did not validate: {encoded.Failure!.Detail}",
                destination);
        }

        string fullDestination;
        string temporary;
        try
        {
            fullDestination = Path.GetFullPath(destination);
            var directory = Path.GetDirectoryName(fullDestination)
                ?? throw new IOException("The destination has no parent directory.");
            temporary = Path.Combine(
                directory,
                $".{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");
        }
        catch (Exception ex)
        {
            return WriteFailure(
                PoseFileStoreFailureKind.TemporaryCreate,
                $"Preparing the atomic pose path failed: {ex.Message}",
                destination);
        }

        PoseFileStoreFailure? failure = null;
        var committed = false;
        var failureKind = PoseFileStoreFailureKind.TemporaryCreate;
        try
        {
            failureKind = PoseFileStoreFailureKind.TemporaryCreate;
            Before(PoseFileStorePhase.CreateTemporary, temporary);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                failureKind = PoseFileStoreFailureKind.TemporaryWrite;
                Before(PoseFileStorePhase.WriteTemporary, temporary);
                stream.Write(bytes);

                failureKind = PoseFileStoreFailureKind.TemporaryFlush;
                Before(PoseFileStorePhase.FlushTemporary, temporary);
                stream.Flush(flushToDisk: true);
            }

            failureKind = PoseFileStoreFailureKind.TemporaryReopen;
            Before(PoseFileStorePhase.ReopenTemporary, temporary);
            var reopened = Read(temporary);
            if (!reopened.Succeeded)
            {
                failure = new PoseFileStoreFailure(
                    PoseFileStoreFailureKind.TemporaryReopen,
                    $"Reopening the atomic pose temp failed: {reopened.Failure!.Detail}",
                    temporary);
            }
            else if (File.Exists(fullDestination))
            {
                failureKind = PoseFileStoreFailureKind.Replace;
                Before(PoseFileStorePhase.ReplaceDestination, fullDestination);
                File.Replace(temporary, fullDestination, destinationBackupFileName: null);
                committed = true;
            }
            else
            {
                failureKind = PoseFileStoreFailureKind.Move;
                Before(PoseFileStorePhase.MoveDestination, fullDestination);
                File.Move(temporary, fullDestination);
                committed = true;
            }
        }
        catch (Exception ex)
        {
            failure = new PoseFileStoreFailure(
                failureKind,
                $"Atomic pose write failed during {failureKind}: {ex.Message}",
                failureKind is PoseFileStoreFailureKind.Replace or PoseFileStoreFailureKind.Move
                    ? fullDestination
                    : temporary);
        }

        if (committed)
            return new PoseFileWriteOutcome(true, null);

        failure ??= new PoseFileStoreFailure(
            PoseFileStoreFailureKind.TemporaryReopen,
            "The atomic pose temp was not committed.",
            temporary);

        string? recoveryEvidence = null;
        if (File.Exists(temporary))
        {
            try
            {
                Before(PoseFileStorePhase.CleanupTemporary, temporary);
                File.Delete(temporary);
            }
            catch (Exception cleanup)
            {
                recoveryEvidence = temporary;
                failure = failure with
                {
                    Detail = failure.Detail +
                        $" The temp could not be deleted: {cleanup.Message}",
                };
            }
        }

        return new PoseFileWriteOutcome(false, failure, recoveryEvidence);
    }

    private PoseFileReadOutcome Decode(ReadOnlySpan<byte> bytes, string? path)
    {
        try
        {
            var pose = JsonSerializer.Deserialize<PoseFile>(
                bytes,
                PoseFile.SerializerOptions);
            var validation = PoseFileValidation.Validate(pose);
            if (!validation.Succeeded)
            {
                return ReadFailure(
                    PoseFileStoreFailureKind.Validation,
                    validation.Failure!.Detail,
                    path);
            }
            return new PoseFileReadOutcome(pose, null);
        }
        catch (JsonException ex)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Json,
                $"The pose JSON is invalid: {ex.Message}",
                path);
        }
        catch (Exception ex)
        {
            return ReadFailure(
                PoseFileStoreFailureKind.Json,
                $"The pose JSON could not be decoded: {ex.Message}",
                path);
        }
    }

    private void Before(PoseFileStorePhase phase, string? path) =>
        _beforePhase?.Invoke(phase, path);

    private static PoseFileReadOutcome ReadFailure(
        PoseFileStoreFailureKind kind,
        string detail,
        string? path = null) =>
        new(null, new PoseFileStoreFailure(kind, detail, path));

    private static PoseFileWriteOutcome WriteFailure(
        PoseFileStoreFailureKind kind,
        string detail,
        string? path = null) =>
        new(false, new PoseFileStoreFailure(kind, detail, path));
}
