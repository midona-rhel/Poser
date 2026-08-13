using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using K4os.Compression.LZ4.Legacy;
using Microsoft.Win32.SafeHandles;
using Poser.Application.Integration;
using Poser.Domain.Integration;

namespace Poser.Game.Mcdf;

/// <summary>
/// MCDF v1 wire I/O. The complete file is a legacy LZ4 stream whose
/// decompressed content is: ASCII <c>MCDF</c>, version byte 1, a
/// little-endian int32 JSON byte length, the UTF-8 JSON document, then the
/// raw file payloads immediately after the JSON in <c>Files</c> order.
/// Unknown JSON members are ignored; unknown versions fail explicitly.
/// </summary>
public sealed class McdfFileBoundary : IMcdfFileBoundary
{
    // A JSON document larger than this is not a plausible character file.
    private const int MaxJsonBytes = 64 * 1024 * 1024;
    private const int ChunkSize = 81920;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
    };

    private readonly Func<Guid> _newGuid;
    private readonly Action? _inspectionChunk;

    public McdfFileBoundary() : this(Guid.NewGuid, null)
    {
    }

    internal McdfFileBoundary(Func<Guid>? newGuid = null, Action? inspectChunk = null)
    {
        _newGuid = newGuid ?? Guid.NewGuid;
        _inspectionChunk = inspectChunk;
    }

    public string GetFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }

    public IntegrationValue<string> CreateOperationDirectory()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "Poser");
            Directory.CreateDirectory(root);
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string id = _newGuid().ToString("N");
                string staging = Path.Combine(root, $".mcdf-staging-{id}");
                string directory = Path.Combine(root, $"mcdf-{id}");
                string marker = Path.Combine(staging, ".owner");
                bool owned = false;
                try
                {
                    Directory.CreateDirectory(staging);
                    using (new FileStream(
                        marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        owned = true;
                    }
                    Directory.Move(staging, directory);
                    return IntegrationValue<string>.Ok(directory);
                }
                catch (IOException) when (attempt < 7)
                {
                    if (owned)
                    {
                        try { Directory.Delete(staging, recursive: true); }
                        catch { }
                    }
                    // Both the marker and the final directory are exclusive:
                    // a stale collision is never claimed or cleaned here.
                }
                catch
                {
                    if (owned)
                    {
                        try { Directory.Delete(staging, recursive: true); }
                        catch { }
                    }
                    throw;
                }
            }
            return IntegrationValue<string>.Fail(
                "The MCDF operation directory could not be allocated.");
        }
        catch (Exception ex)
        {
            return IntegrationValue<string>.Fail(
                $"The MCDF operation directory could not be allocated: {ex.Message}");
        }
    }

    public IntegrationValue<McdfExportInspection> InspectExportCandidates(
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
            realRoot = ResolveRealPath(fullRoot) ?? string.Empty;
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

                string? realFile;
                try
                {
                    realFile = ResolveRealPath(fullPath);
                    if (realFile == null)
                    {
                        skipped.Add($"{actualRaw} (could not resolve the real path)");
                        continue;
                    }
                    if (EscapesRoot(Path.GetRelativePath(realRoot, realFile)))
                    {
                        skipped.Add($"{actualRaw} (outside the Penumbra mod directory)");
                        continue;
                    }

                    using var stream = new FileStream(
                        realFile, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    long length = stream.Length;
                    string hash = HashStream(stream, cancellation, _inspectionChunk);
                    string? identity = TryGetFileIdentity(stream.SafeFileHandle);
                    var source = new McdfExportSourceObservation(
                        realFile, realRoot, length, hash, identity);
                    candidates.Add(new McdfExportCandidate(
                        actualRaw, gamePathsRaw.ToArray(),
                        McdfExportCandidateKind.LocalFile, realFile, length, source));
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

    private sealed class WireData
    {
        public string Description { get; set; } = string.Empty;
        public string GlamourerData { get; set; } = string.Empty;
        public string CustomizePlusData { get; set; } = string.Empty;
        public string ManipulationData { get; set; } = string.Empty;
        public List<WireFile> Files { get; set; } = new();
        public List<WireSwap> FileSwaps { get; set; } = new();
    }

    private sealed class WireFile
    {
        public List<string> GamePaths { get; set; } = new();
        public int Length { get; set; }
        public string Hash { get; set; } = string.Empty;
    }

    private sealed class WireSwap
    {
        public List<string> GamePaths { get; set; } = new();
        public string FileSwapPath { get; set; } = string.Empty;
    }

    public Task<IntegrationValue<McdfPackage>> ReadPackage(
        string path,
        McdfLimits limits,
        string operationDirectory,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation) =>
        Task.Run(
            () => ReadCore(path, limits, operationDirectory, progress, cancellation),
            CancellationToken.None);

    public Task<IntegrationValue<McdfWriteStats>> WritePackage(
        string destination,
        McdfExportContent content,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation) =>
        Task.Run(() => WriteCore(destination, content, progress, cancellation), CancellationToken.None);

    public IntegrationPortResult DeleteOperationDirectory(string operationDirectory)
    {
        try
        {
            if (Directory.Exists(operationDirectory))
                Directory.Delete(operationDirectory, recursive: true);
            return IntegrationPortResult.Ok();
        }
        catch (Exception ex)
        {
            // A file still held open (an antivirus scan, the game itself)
            // is a REPORTED failure: the caller keeps ownership of the
            // directory and retries instead of releasing deleted-in-name
            // payloads.
            return IntegrationPortResult.Fail(
                $"The extracted files could not be deleted: {ex.Message}");
        }
    }

    // ── Read ─────────────────────────────────────────────────────────────

    private IntegrationValue<McdfPackage> ReadCore(
        string path,
        McdfLimits limits,
        string operationDirectory,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation)
    {
        // The caller owns the operation directory and registered it before
        // calling; a failure here leaves partial extraction for the
        // caller's visible, retryable cleanup.
        try
        {
            progress(new McdfProgressStep(McdfPhase.Reading, 0, 0, 0, 0));
            using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var lz4 = LZ4Legacy.Decode(file, leaveOpen: true);
            using var reader = new BinaryReader(lz4, Encoding.UTF8, leaveOpen: true);

            var magic = reader.ReadBytes(4);
            if (magic.Length != 4 || magic[0] != (byte)'M' || magic[1] != (byte)'C'
                || magic[2] != (byte)'D' || magic[3] != (byte)'F')
                return IntegrationValue<McdfPackage>.Fail(
                    "This is not an MCDF character file.");
            byte version = reader.ReadByte();
            if (version != McdfFormat.Version)
                return IntegrationValue<McdfPackage>.Fail(
                    $"MCDF version {version} is not supported (expected {McdfFormat.Version}).");

            int jsonLength = reader.ReadInt32();
            if (jsonLength <= 0 || jsonLength > MaxJsonBytes)
                return IntegrationValue<McdfPackage>.Fail(
                    $"The package declares an invalid header length ({jsonLength}).");
            var jsonBytes = new byte[jsonLength];
            ReadExact(lz4, jsonBytes, "package header");

            WireData? data;
            try
            {
                data = JsonSerializer.Deserialize<WireData>(jsonBytes, JsonOptions);
            }
            catch (JsonException ex)
            {
                return IntegrationValue<McdfPackage>.Fail(
                    $"The package header is not valid JSON: {ex.Message}");
            }
            if (data == null)
                return IntegrationValue<McdfPackage>.Fail("The package header is empty.");

            progress(new McdfProgressStep(McdfPhase.Validating, 0, data.Files.Count, 0, 0));
            var validation = Validate(data, limits, out long totalBytes);
            if (validation != null)
                return IntegrationValue<McdfPackage>.Fail(validation);

            // Extraction: generated file names inside a unique operation
            // directory; archive-declared names are never used on disk.
            Directory.CreateDirectory(operationDirectory);
            var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
            long bytesDone = 0;
            var chunk = new byte[ChunkSize];
            for (int i = 0; i < data.Files.Count; i++)
            {
                if (cancellation.IsCancellationRequested)
                    return IntegrationValue<McdfPackage>.Fail("The import was cancelled.");
                var entry = data.Files[i];
                string extracted = Path.Combine(operationDirectory, $"p{i:D4}.dat");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using (var output = new FileStream(
                    extracted, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    int remaining = entry.Length;
                    while (remaining > 0)
                    {
                        if (cancellation.IsCancellationRequested)
                            return IntegrationValue<McdfPackage>.Fail("The import was cancelled.");
                        int wanted = Math.Min(remaining, chunk.Length);
                        int got = lz4.Read(chunk, 0, wanted);
                        if (got <= 0)
                            return IntegrationValue<McdfPackage>.Fail(
                                $"The package ends before file {i + 1} of {data.Files.Count} is complete.");
                        output.Write(chunk, 0, got);
                        hash.AppendData(chunk, 0, got);
                        remaining -= got;
                        bytesDone += got;
                        progress(new McdfProgressStep(
                            McdfPhase.Extracting, i, data.Files.Count, bytesDone, totalBytes));
                    }
                }

                if (entry.Hash.Length > 0)
                {
                    string computed = Convert.ToHexString(hash.GetHashAndReset());
                    if (!string.Equals(computed, entry.Hash, StringComparison.OrdinalIgnoreCase))
                        return IntegrationValue<McdfPackage>.Fail(
                            $"A payload does not match its declared hash ({entry.Hash}).");
                }

                foreach (var gamePath in entry.GamePaths)
                    replaced[McdfFormat.NormalizeGamePath(gamePath)] = extracted;
                progress(new McdfProgressStep(
                    McdfPhase.Extracting, i + 1, data.Files.Count, bytesDone, totalBytes));
            }

            if (lz4.Read(chunk, 0, 1) != 0)
                return IntegrationValue<McdfPackage>.Fail(
                    "The package contains trailing data after the declared payloads.");

            var swaps = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var swap in data.FileSwaps)
                foreach (var gamePath in swap.GamePaths)
                    swaps[McdfFormat.NormalizeGamePath(gamePath)] =
                        McdfFormat.NormalizeGamePath(swap.FileSwapPath);

            return IntegrationValue<McdfPackage>.Ok(new McdfPackage(
                Path.GetFileName(path),
                data.Description,
                data.GlamourerData,
                data.CustomizePlusData,
                data.ManipulationData,
                replaced,
                swaps,
                operationDirectory,
                data.Files.Count,
                totalBytes));
        }
        catch (EndOfStreamException)
        {
            return IntegrationValue<McdfPackage>.Fail("The package is truncated.");
        }
        catch (Exception ex)
        {
            return IntegrationValue<McdfPackage>.Fail(
                $"Reading the package failed: {ex.Message}");
        }
    }

    private static string? Validate(WireData data, McdfLimits limits, out long totalBytes)
    {
        totalBytes = 0;
        if (data.Files.Count > limits.MaxFileCount)
            return $"The package contains {data.Files.Count} files (limit {limits.MaxFileCount}).";

        int pathCount = data.Files.Sum(f => f.GamePaths.Count)
            + data.FileSwaps.Sum(s => s.GamePaths.Count);
        if (pathCount > limits.MaxGamePathCount)
            return $"The package contains {pathCount} game paths (limit {limits.MaxGamePathCount}).";

        // Duplicate game paths are rejected unless they are byte-identical
        // and intentional — i.e. they declare the same content hash.
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in data.Files)
        {
            if (entry.Length < 0)
                return "The package declares a negative file length.";
            if (entry.Length > limits.MaxFileBytes)
                return $"A single file is {entry.Length} bytes (limit {limits.MaxFileBytes}).";
            totalBytes += entry.Length;
            if (totalBytes > limits.MaxTotalBytes)
                return $"The package expands past the total limit ({limits.MaxTotalBytes} bytes).";
            if (entry.GamePaths.Count == 0)
                return "The package contains a file with no game path.";
            foreach (var rawPath in entry.GamePaths)
            {
                var gamePath = McdfFormat.NormalizeGamePath(rawPath);
                if (McdfFormat.ValidateGamePath(gamePath) is { } invalid)
                    return $"The package contains {invalid}.";
                if (seen.TryGetValue(gamePath, out var previousHash))
                {
                    if (entry.Hash.Length == 0
                        || !string.Equals(previousHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                        return $"The package maps {gamePath} to conflicting contents.";
                }
                else
                {
                    seen[gamePath] = entry.Hash;
                }
            }
        }

        foreach (var swap in data.FileSwaps)
        {
            if (swap.GamePaths.Count == 0)
                return "The package contains a file swap with no game path.";
            var target = McdfFormat.NormalizeGamePath(swap.FileSwapPath);
            // Swaps must be game-path to game-path; a swap that points at a
            // filesystem location is not a swap.
            if (McdfFormat.ValidateGamePath(target) is { } invalidTarget)
                return $"The package contains a file swap to {invalidTarget}.";
            foreach (var rawPath in swap.GamePaths)
            {
                var gamePath = McdfFormat.NormalizeGamePath(rawPath);
                if (McdfFormat.ValidateGamePath(gamePath) is { } invalid)
                    return $"The package contains {invalid}.";
                if (!seen.TryAdd(gamePath, target))
                    return $"The package maps {gamePath} to conflicting contents.";
            }
        }

        return null;
    }

    // ── Write ────────────────────────────────────────────────────────────

    private IntegrationValue<McdfWriteStats> WriteCore(
        string destination,
        McdfExportContent content,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation)
    {
        string temporary = string.Empty;
        bool moved = false;
        string fullDestination = string.Empty;
        bool destinationExisted = false;
        try
        {
            fullDestination = Path.GetFullPath(destination);
            destinationExisted = File.Exists(fullDestination);
            // Pass 1 — hash and measure every local file so the header can
            // precede the payloads, deduplicating identical content by
            // SHA-1 while every game path is preserved.
            var byHash = new Dictionary<string, (
                WireFile Entry, string LocalPath, McdfExportSourceObservation Source)>(
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
                    return IntegrationValue<McdfWriteStats>.Fail("The export was cancelled.");
                var source = file.Source ?? CaptureSource(file.LocalPath, cancellation);
                using (var input = OpenValidatedSource(file.LocalPath, source, cancellation,
                    out string? sourceError))
                {
                    if (input == null)
                        return IntegrationValue<McdfWriteStats>.Fail(
                            sourceError ?? $"{file.LocalPath} changed while exporting.");
                    string digest = HashStream(input, cancellation, null,
                        bytes =>
                        {
                            hashedBytes += bytes;
                            progress(new McdfProgressStep(
                                McdfPhase.WritingPackage, 0, content.Files.Count,
                                hashedBytes, toHash));
                        });
                    if (!string.Equals(digest, source.ContentHash, StringComparison.OrdinalIgnoreCase))
                        return IntegrationValue<McdfWriteStats>.Fail(
                            $"{file.LocalPath} changed while exporting; its declared hash would be false.");
                    long length = input.Length;
                    if (length > int.MaxValue)
                        return IntegrationValue<McdfWriteStats>.Fail(
                            $"{file.LocalPath} is too large for the MCDF format.");

                    if (byHash.TryGetValue(digest, out var existing))
                    {
                        foreach (var gamePath in file.GamePaths)
                            if (!existing.Entry.GamePaths.Contains(gamePath))
                                existing.Entry.GamePaths.Add(gamePath);
                    }
                    else
                    {
                        byHash[digest] = (new WireFile
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
                .Select(group => new WireSwap
                {
                    GamePaths = group.Select(pair => pair.Key).ToList(),
                    FileSwapPath = group.Key,
                })
                .ToList();

            var data = new WireData
            {
                Description = content.Description,
                GlamourerData = content.GlamourerData,
                CustomizePlusData = content.CustomizePlusData,
                ManipulationData = content.ManipulationData,
                Files = order.Select(digest => byHash[digest].Entry).ToList(),
                FileSwaps = swapEntries,
            };
            var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);

            // Pass 2 — claim a unique same-directory temp path exclusively,
            // then write header + payloads before the atomic destination step.
            using (var output = CreateOwnedTemporary(
                Path.GetFullPath(destination), out temporary))
            using (var lz4 = LZ4Legacy.Encode(
                output, highCompression: true, blockSize: 1024 * 1024, leaveOpen: true))
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
                var chunk = new byte[ChunkSize];
                foreach (var digest in order)
                {
                    if (cancellation.IsCancellationRequested)
                        return IntegrationValue<McdfWriteStats>.Fail("The export was cancelled.");
                    var source = byHash[digest].Source;
                    using var input = OpenValidatedSource(
                        byHash[digest].LocalPath, source, cancellation, out string? sourceError);
                    if (input == null)
                        return IntegrationValue<McdfWriteStats>.Fail(
                            sourceError ?? $"{byHash[digest].LocalPath} changed while exporting.");
                    string digestBeforeCopy = HashStream(input, cancellation, null);
                    if (!string.Equals(digestBeforeCopy, source.ContentHash,
                            StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(digestBeforeCopy, digest,
                            StringComparison.OrdinalIgnoreCase))
                        return IntegrationValue<McdfWriteStats>.Fail(
                            $"{byHash[digest].LocalPath} changed while exporting; its declared hash would be false.");
                    input.Position = 0;
                    long fileWritten = 0;
                    int got;
                    while ((got = input.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (cancellation.IsCancellationRequested)
                            return IntegrationValue<McdfWriteStats>.Fail("The export was cancelled.");
                        writer.Write(chunk, 0, got);
                        written += got;
                        fileWritten += got;
                        progress(new McdfProgressStep(
                            McdfPhase.WritingPackage, done, order.Count, written, totalBytes));
                    }
                    if (fileWritten != byHash[digest].Entry.Length)
                        return IntegrationValue<McdfWriteStats>.Fail(
                            $"{byHash[digest].LocalPath} changed while exporting.");
                    done++;
                    progress(new McdfProgressStep(
                        McdfPhase.WritingPackage, done, order.Count, written, totalBytes));
                }

                writer.Flush();
            }

            // Decide the replacement mode before any lengthy source work. If
            // an absent destination appears concurrently, Move fails without
            // overwriting it; if an existing destination disappears, Replace
            // fails without silently switching to create semantics.
            if (destinationExisted)
                File.Replace(temporary, fullDestination, destinationBackupFileName: null);
            else
                File.Move(temporary, fullDestination);
            moved = true;
            return IntegrationValue<McdfWriteStats>.Ok(
                new McdfWriteStats(order.Count, totalBytes));
        }
        catch (OperationCanceledException)
        {
            return IntegrationValue<McdfWriteStats>.Fail("The export was cancelled.");
        }
        catch (Exception ex)
        {
            return IntegrationValue<McdfWriteStats>.Fail(
                $"Writing the package failed: {ex.Message}");
        }
        finally
        {
            if (!moved)
            {
                try
                {
                    if (temporary.Length > 0)
                        File.Delete(temporary);
                }
                catch
                {
                    // The exact owned temp is best-effort cleanup; never
                    // mask the original failure or touch the destination.
                }
            }
        }
    }

    private static void ReadExact(Stream stream, byte[] buffer, string what)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int got = stream.Read(buffer, offset, buffer.Length - offset);
            if (got <= 0)
                throw new EndOfStreamException($"The {what} is truncated.");
            offset += got;
        }
    }

    private FileStream CreateOwnedTemporary(string destination, out string temporary)
    {
        string directory = Path.GetDirectoryName(destination)
            ?? throw new IOException("The destination directory could not be resolved.");
        string name = Path.GetFileName(destination);
        for (int attempt = 0; attempt < 8; attempt++)
        {
            temporary = Path.Combine(
                directory, $".{name}.{_newGuid():N}.tmp");
            try
            {
                return new FileStream(
                    temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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
            if (realPath == null || !PathsEqual(realPath, expected.CanonicalPath))
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
            string? finalPath = TryGetFinalPath(input.SafeFileHandle);
            if (finalPath != null && !PathsEqual(finalPath, expected.CanonicalPath))
            {
                input.Dispose();
                error = $"{localPath} changed its canonical handle path while exporting.";
                return null;
            }
            string? identity = TryGetFileIdentity(input.SafeFileHandle);
            if (expected.Identity != null && identity != null
                && !string.Equals(expected.Identity, identity, StringComparison.Ordinal))
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
        long length = input.Length;
        string hash = HashStream(input, cancellation, null);
        return new McdfExportSourceObservation(
            realPath, string.Empty, length, hash,
            TryGetFileIdentity(input.SafeFileHandle));
    }

    private static string HashStream(
        Stream stream,
        CancellationToken cancellation,
        Action? chunkHook,
        Action<int>? bytesRead = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var chunk = new byte[ChunkSize];
        int got;
        while ((got = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            hash.AppendData(chunk, 0, got);
            bytesRead?.Invoke(got);
            chunkHook?.Invoke();
        }
        cancellation.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static string? TryGetFinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid)
            return null;
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
            return null;
        string path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\" + path[7..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        return Path.GetFullPath(path);
    }

    private static string? TryGetFileIdentity(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid
            || !GetFileInformationByHandle(handle, out var info))
            return null;
        return $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
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
