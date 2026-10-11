using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4.Legacy;
using Microsoft.Win32.SafeHandles;
using Poser.Domain.Integration;

namespace Poser.Documents.Mcdf;

/// <summary>
/// The export side: inspects Penumbra's resolved sources into observations,
/// then writes a package to an owned temporary and commits it over the
/// destination only while every source and the destination still match
/// what was observed.
/// </summary>
internal static class McdfWriter
{
    public static IntegrationValue<McdfExportInspection> InspectCandidates(
        string modRoot,
        IReadOnlyDictionary<string, IReadOnlyList<string>> resources,
        CancellationToken cancellation)
    {
        string realRoot;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            string fullRoot = Path.GetFullPath(modRoot);
            if (!Directory.Exists(fullRoot))
                return IntegrationValue<McdfExportInspection>.Fail(
                    "Penumbra's mod directory is missing or inaccessible.");
            using var entries = Directory.EnumerateFileSystemEntries(fullRoot).GetEnumerator();
            // Advance once so an ACL failure is observed by the boundary.
            _ = entries.MoveNext();
            using var rootHandle =
                McdfPlatformFileOwnership.OpenDirectoryForInspection(fullRoot);
            realRoot =
                McdfPlatformFileOwnership.GetRequiredFinalPath(rootHandle);
            if (realRoot.Length == 0 || !Directory.Exists(realRoot))
                return IntegrationValue<McdfExportInspection>.Fail(
                    "Penumbra's mod directory could not be resolved to a real path.");
        }
        catch (OperationCanceledException)
        {
            return IntegrationValue<McdfExportInspection>.Fail(
                "The export inspection was cancelled.");
        }
        catch (Exception)
        {
            return IntegrationValue<McdfExportInspection>.Fail(
                "Penumbra's mod directory is missing or inaccessible.");
        }

        var candidates = new List<McdfExportCandidate>();
        var skipped = new List<string>();
        foreach (var (actualRaw, gamePathsRaw) in resources)
        {
            if (cancellation.IsCancellationRequested)
                return IntegrationValue<McdfExportInspection>.Fail(
                    "The export inspection was cancelled.");
            if (actualRaw.Length > 1 && actualRaw[1] == ':')
            {
                string fullPath;
                try
                {
                    fullPath = Path.GetFullPath(actualRaw);
                }
                catch
                {
                    skipped.Add($"{actualRaw} (not a usable path)");
                    continue;
                }

                try
                {
                    using var stream = new FileStream(
                        fullPath, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    string finalPath = McdfPlatformFileOwnership.GetRequiredFinalPath(stream.SafeFileHandle);
                    if (EscapesRoot(Path.GetRelativePath(realRoot, finalPath)))
                    {
                        skipped.Add($"{actualRaw} (changed or outside the Penumbra mod directory)");
                        continue;
                    }
                    long length = stream.Length;
                    string hash = HashStream(
                        stream, HashAlgorithmName.SHA256, cancellation);
                    string? identity = McdfPlatformFileOwnership.TryGetIdentity(stream.SafeFileHandle);
                    var source = new McdfExportSourceObservation(
                        finalPath, realRoot, length, hash, identity);
                    candidates.Add(new McdfExportCandidate(
                        actualRaw, gamePathsRaw.ToArray(),
                        McdfExportCandidateKind.LocalFile, finalPath, length, source));
                }
                catch (OperationCanceledException)
                {
                    return IntegrationValue<McdfExportInspection>.Fail(
                        "The export inspection was cancelled.");
                }
                catch (UnauthorizedAccessException)
                {
                    skipped.Add($"{actualRaw} (not readable)");
                }
                catch (FileNotFoundException)
                {
                    skipped.Add($"{actualRaw} (missing on disk)");
                }
                catch (DirectoryNotFoundException)
                {
                    skipped.Add($"{actualRaw} (missing on disk)");
                }
                catch (Win32Exception)
                {
                    return IntegrationValue<McdfExportInspection>.Fail(
                        "A source file's final handle path or identity could not be verified.");
                }
                catch (IOException)
                {
                    skipped.Add($"{actualRaw} (metadata could not be read)");
                }
                catch
                {
                    skipped.Add($"{actualRaw} (could not resolve the real path)");
                }
            }
            else
            {
                candidates.Add(new McdfExportCandidate(
                    actualRaw, gamePathsRaw.ToArray(),
                    McdfExportCandidateKind.GamePath, null, 0));
            }
        }

        return IntegrationValue<McdfExportInspection>.Ok(
            new McdfExportInspection(candidates, skipped));
    }

    public static IntegrationValue<McdfWriteStats> Write(
        string destination,
        McdfExportContent content,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation)
    {
        string temporary = string.Empty;
        bool moved = false;
        FileStream? ownedOutput = null;
        SafeFileHandle? destinationHandle = null;
        string? destinationBackupPath = null;
        bool destinationBackedUp = false;
        bool destinationBackupDeleted = false;
        IntegrationValue<McdfWriteStats> result =
            IntegrationValue<McdfWriteStats>.Fail(
                "Writing the package failed unexpectedly.");
        string fullDestination = string.Empty;
        bool destinationExisted = false;
        try
        {
            fullDestination = Path.GetFullPath(destination);
            destinationExisted = File.Exists(fullDestination);
            string? destinationIdentity = null;
            if (destinationExisted)
            {
                destinationHandle =
                    McdfPlatformFileOwnership.OpenDestinationForCommit(fullDestination);
                destinationIdentity = McdfPlatformFileOwnership.TryGetIdentity(destinationHandle);
                if (destinationIdentity == null)
                    throw new WriteFailureException(
                        "The existing destination identity could not be verified.");
            }
            // Pass 1 — hash and measure every local file so the header can
            // precede the payloads, deduplicating identical content by
            // BLAKE3 (current Lightless/Brio) while every game path is preserved.
            var byHash = new Dictionary<string, (
                McdfWireFile Entry, string LocalPath, McdfExportSourceObservation Source)>(
                StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            long totalBytes = 0;
            long hashedBytes = 0;
            long toHash = 0;
            foreach (var file in content.Files)
            {
                if (file.Source is { } source)
                    toHash += source.Length;
                else
                    toHash += new FileInfo(file.LocalPath).Length;
            }

            foreach (var file in content.Files)
            {
                if (cancellation.IsCancellationRequested)
                    throw new WriteFailureException("The export was cancelled.");
                var source = file.Source ?? CaptureSource(file.LocalPath, cancellation);
                using (var input = OpenValidatedSource(file.LocalPath, source, cancellation,
                    out string? sourceError))
                {
                    if (input == null)
                        throw new WriteFailureException(
                            sourceError ?? $"{file.LocalPath} changed while exporting.");
                    string localDigest = HashStream(
                        input, HashAlgorithmName.SHA256, cancellation,
                        bytes =>
                        {
                            hashedBytes += bytes;
                            progress(new McdfProgressStep(
                                McdfPhase.WritingPackage, 0, content.Files.Count,
                                hashedBytes, toHash));
                        });
                    if (!string.Equals(localDigest, source.ContentHash, StringComparison.OrdinalIgnoreCase))
                        throw new WriteFailureException(
                            $"{file.LocalPath} changed while exporting; its declared hash would be false.");
                    input.Position = 0;
                    string digest = HashPayload(input, cancellation);
                    long length = input.Length;
                    // McdfWireFile.Length is the format's int32 length field.
                    if (length > int.MaxValue)
                        throw new WriteFailureException(
                            $"{file.LocalPath} is too large for the MCDF format.");

                    if (byHash.TryGetValue(digest, out var existing))
                    {
                        foreach (var gamePath in file.GamePaths)
                            if (!existing.Entry.GamePaths.Contains(gamePath))
                                existing.Entry.GamePaths.Add(gamePath);
                    }
                    else
                    {
                        byHash[digest] = (new McdfWireFile
                        {
                            GamePaths = file.GamePaths.ToList(),
                            Length = (int)length,
                            Hash = digest,
                        }, file.LocalPath, source);
                        order.Add(digest);
                        totalBytes += length;
                    }
                }
            }

            var swapEntries = content.Swaps
                .GroupBy(pair => pair.Value, StringComparer.Ordinal)
                .Select(group => new McdfWireSwap
                {
                    GamePaths = group.Select(pair => pair.Key).ToList(),
                    FileSwapPath = group.Key,
                })
                .ToList();

            var data = new McdfWireData
            {
                Description = content.Description,
                GlamourerData = content.GlamourerData,
                CustomizePlusData = content.CustomizePlusData,
                ManipulationData = content.ManipulationData,
                Files = order.Select(digest => byHash[digest].Entry).ToList(),
                FileSwaps = swapEntries,
            };
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data, McdfWire.JsonOptions);

            // Pass 2 — claim a unique same-directory temp path exclusively,
            // then write header + payloads before the atomic destination step.
            ownedOutput = CreateOwnedTemporary(
                Path.GetFullPath(destination), out temporary);
            using (var lz4 = LZ4Legacy.Encode(
                ownedOutput, highCompression: true, blockSize: 1024 * 1024, leaveOpen: true))
            using (var writer = new BinaryWriter(lz4, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write((byte)'M');
                writer.Write((byte)'C');
                writer.Write((byte)'D');
                writer.Write((byte)'F');
                writer.Write(McdfFormat.Version);
                writer.Write(jsonBytes.Length);
                writer.Write(jsonBytes);

                long written = 0;
                int done = 0;
                var chunk = new byte[McdfWire.ChunkSize];
                foreach (var digest in order)
                {
                    if (cancellation.IsCancellationRequested)
                        throw new WriteFailureException("The export was cancelled.");
                    var source = byHash[digest].Source;
                    using var input = OpenValidatedSource(
                        byHash[digest].LocalPath, source, cancellation, out string? sourceError);
                    if (input == null)
                        throw new WriteFailureException(
                            sourceError ?? $"{byHash[digest].LocalPath} changed while exporting.");
                    string localDigestBeforeCopy = HashStream(
                        input, HashAlgorithmName.SHA256, cancellation);
                    if (!string.Equals(localDigestBeforeCopy, source.ContentHash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new WriteFailureException(
                            $"{byHash[digest].LocalPath} changed while exporting; its declared hash would be false.");
                    input.Position = 0;
                    string wireDigestBeforeCopy = HashPayload(input, cancellation);
                    if (!string.Equals(wireDigestBeforeCopy, digest,
                            StringComparison.OrdinalIgnoreCase))
                        throw new WriteFailureException(
                            $"{byHash[digest].LocalPath} changed while exporting.");
                    input.Position = 0;
                    long fileWritten = 0;
                    int got;
                    while ((got = input.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (cancellation.IsCancellationRequested)
                            throw new WriteFailureException("The export was cancelled.");
                        writer.Write(chunk, 0, got);
                        written += got;
                        fileWritten += got;
                        progress(new McdfProgressStep(
                            McdfPhase.WritingPackage, done, order.Count, written, totalBytes));
                    }
                    if (fileWritten != byHash[digest].Entry.Length)
                        throw new WriteFailureException(
                            $"{byHash[digest].LocalPath} changed while exporting.");
                    done++;
                    progress(new McdfProgressStep(
                        McdfPhase.WritingPackage, done, order.Count, written, totalBytes));
                }

                writer.Flush();
            }

            ownedOutput.Flush(flushToDisk: true);
            if (destinationExisted)
            {
                if (!File.Exists(fullDestination)
                    || destinationHandle == null
                    || !string.Equals(
                        McdfPlatformFileOwnership.TryGetIdentity(destinationHandle),
                        destinationIdentity,
                        StringComparison.Ordinal))
                    throw new WriteFailureException(
                        "The existing destination changed before commit; the export was refused.");
                cancellation.ThrowIfCancellationRequested();
                destinationBackupPath = CreateDestinationBackupPath(fullDestination);
                try
                {
                    McdfPlatformFileOwnership.CommitExactHandle(
                        destinationHandle, destinationBackupPath, replaceExisting: false);
                }
                catch (Exception ex)
                {
                    throw new WriteFailureException(
                        $"The admitted destination could not be moved to its owned backup: {ex.Message}");
                }
                destinationBackedUp = true;
                if (File.Exists(fullDestination))
                    throw new WriteFailureException(
                        "A foreign destination appeared during the commit transaction; "
                        + "the export was refused.");
            }
            else if (File.Exists(fullDestination))
            {
                throw new WriteFailureException(
                    "A destination appeared before commit; the export was refused.");
            }
            cancellation.ThrowIfCancellationRequested();
            McdfPlatformFileOwnership.CommitExactHandle(
                ownedOutput.SafeFileHandle, fullDestination, replaceExisting: false);
            moved = true;
            ownedOutput.Dispose();
            ownedOutput = null;
            result = IntegrationValue<McdfWriteStats>.Ok(
                new McdfWriteStats(order.Count, totalBytes));
        }
        catch (OperationCanceledException)
        {
            result = IntegrationValue<McdfWriteStats>.Fail(
                "The export was cancelled.");
        }
        catch (WriteFailureException ex)
        {
            result = IntegrationValue<McdfWriteStats>.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            result = IntegrationValue<McdfWriteStats>.Fail(
                $"Writing the package failed: {ex.Message}");
        }
        finally
        {
            if (destinationBackedUp && moved && destinationHandle != null)
            {
                try
                {
                    McdfPlatformFileOwnership.MarkDeleteOnClose(destinationHandle);
                    destinationBackupDeleted = true;
                }
                catch (Exception cleanupError)
                {
                    string original = result.Detail
                        ?? "The export completed, but destination cleanup failed.";
                    result = IntegrationValue<McdfWriteStats>.Fail(
                        $"{original} Destination backup cleanup also failed: "
                        + $"{cleanupError.Message} The owned backup was retained at "
                        + $"{destinationBackupPath} for manual cleanup.");
                }
            }
            else if (destinationBackedUp && !destinationBackupDeleted)
            {
                string original = result.Detail
                    ?? "Writing the package failed.";
                result = IntegrationValue<McdfWriteStats>.Fail(
                    $"{original} The exact admitted destination was retained at "
                    + $"{destinationBackupPath} as recovery evidence.");
            }
            if (!moved && ownedOutput != null)
            {
                try
                {
                    McdfPlatformFileOwnership.MarkDeleteOnClose(ownedOutput.SafeFileHandle);
                }
                catch (Exception cleanupError)
                {
                    string original = result.Detail
                        ?? "Writing the package failed.";
                    result = IntegrationValue<McdfWriteStats>.Fail(
                        $"{original} Exact temporary cleanup also failed: "
                        + $"{cleanupError.Message} The owned temporary file "
                        + $"was retained at {temporary} for manual cleanup.");
                }
            }
            ownedOutput?.Dispose();
            destinationHandle?.Dispose();
        }
        return result;
    }

    private sealed class WriteFailureException(string message)
        : IOException(message);

    private static string CreateDestinationBackupPath(string destination)
    {
        string directory = Path.GetDirectoryName(destination)
            ?? throw new IOException("The destination directory could not be resolved.");
        string name = Path.GetFileName(destination);
        return Path.Combine(
            directory,
            $".{name}.mcdf-backup-{Guid.NewGuid():N}");
    }

    private static FileStream CreateOwnedTemporary(string destination, out string temporary)
    {
        string directory = Path.GetDirectoryName(destination)
            ?? throw new IOException("The destination directory could not be resolved.");
        string name = Path.GetFileName(destination);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            temporary = Path.Combine(
                directory, $".{name}.{Guid.NewGuid():N}.tmp");
            try
            {
                return McdfPlatformFileOwnership.CreateExclusiveTemporary(temporary);
            }
            catch (IOException) when (attempt < 7)
            {
                // CreateNew is the ownership proof. A stale or concurrent
                // same-name temp is never opened, overwritten, or deleted.
            }
        }

        temporary = string.Empty;
        throw new IOException("A unique temporary export file could not be allocated.");
    }

    private static FileStream? OpenValidatedSource(
        string localPath,
        McdfExportSourceObservation expected,
        CancellationToken cancellation,
        out string? error)
    {
        error = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            string fullPath = Path.GetFullPath(localPath);
            string? realPath = ResolveRealPath(fullPath);
            if (realPath == null || !McdfPlatformFileOwnership.PathsEqual(realPath, expected.CanonicalPath))
            {
                error = $"{localPath} changed its canonical path while exporting.";
                return null;
            }
            if (expected.CanonicalRoot.Length > 0
                && EscapesRoot(Path.GetRelativePath(expected.CanonicalRoot, realPath)))
            {
                error = $"{localPath} is outside the inspected mod directory.";
                return null;
            }

            var input = new FileStream(
                realPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string finalPath = McdfPlatformFileOwnership.GetRequiredFinalPath(input.SafeFileHandle);
            if (!McdfPlatformFileOwnership.PathsEqual(finalPath, expected.CanonicalPath))
            {
                input.Dispose();
                error = $"{localPath} changed its canonical handle path while exporting.";
                return null;
            }
            string? identity = McdfPlatformFileOwnership.TryGetIdentity(input.SafeFileHandle);
            if (expected.Identity != null
                && (identity == null
                    || !string.Equals(
                        expected.Identity, identity, StringComparison.Ordinal)))
            {
                input.Dispose();
                error = $"{localPath} changed its file identity while exporting.";
                return null;
            }
            if (input.Length != expected.Length)
            {
                input.Dispose();
                error = $"{localPath} changed while exporting.";
                return null;
            }
            return input;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = $"{localPath} could not be opened for export: {ex.Message}";
            return null;
        }
    }

    private static McdfExportSourceObservation CaptureSource(
        string localPath, CancellationToken cancellation)
    {
        string fullPath = Path.GetFullPath(localPath);
        string realPath = ResolveRealPath(fullPath)
            ?? throw new IOException($"{localPath} could not be resolved for export.");
        using var input = new FileStream(
            realPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string finalPath = McdfPlatformFileOwnership.GetRequiredFinalPath(input.SafeFileHandle);
        if (!McdfPlatformFileOwnership.PathsEqual(finalPath, realPath))
            throw new IOException($"{localPath} changed while being opened.");
        long length = input.Length;
        string hash = HashStream(
            input, HashAlgorithmName.SHA256, cancellation);
        return new McdfExportSourceObservation(
            finalPath, string.Empty, length, hash,
            McdfPlatformFileOwnership.TryGetIdentity(input.SafeFileHandle));
    }

    private static string HashPayload(Stream stream, CancellationToken cancellation)
    {
        using var hash = new McdfPayloadHash();
        var chunk = new byte[McdfWire.ChunkSize];
        int got;
        while ((got = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            hash.Append(chunk.AsSpan(0, got));
        }
        cancellation.ThrowIfCancellationRequested();
        return hash.Finish();
    }

    private static string HashStream(
        Stream stream,
        HashAlgorithmName algorithm,
        CancellationToken cancellation,
        Action<int>? bytesRead = null)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        var chunk = new byte[McdfWire.ChunkSize];
        int got;
        while ((got = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            hash.AppendData(chunk, 0, got);
            bytesRead?.Invoke(got);
        }
        cancellation.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>Resolves every reparse point in a path, including
    /// intermediate directories, and restarts from each final target.</summary>
    private static string? ResolveRealPath(string fullPath)
    {
        var separators = new[]
        {
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
        };
        string path = fullPath;
        for (int pass = 0; pass < 8; pass++)
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return null;
            string current = root;
            bool jumped = false;
            foreach (var segment in path[root.Length..]
                .Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : new FileInfo(current);
                if (info.LinkTarget == null)
                    continue;
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved == null)
                    return null;
                var remainder = path[current.Length..].TrimStart('\\', '/');
                path = remainder.Length == 0
                    ? resolved.FullName
                    : Path.Combine(resolved.FullName, remainder);
                jumped = true;
                break;
            }
            if (!jumped)
                return path;
        }
        return null;
    }

    private static bool EscapesRoot(string relative) =>
        Path.IsPathRooted(relative)
        || relative == ".."
        || relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal)
        || relative.StartsWith("../", StringComparison.Ordinal);
}
