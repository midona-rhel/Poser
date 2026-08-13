using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    private readonly Action<string>? _beforeCommit;
    private readonly Func<SafeFileHandle, string> _getFinalPath;
    private readonly Func<SafeFileHandle, string?> _getIdentity;
    private readonly Func<SafeFileHandle, string> _getOperationFinalPath;
    private readonly Func<SafeFileHandle, string?> _getOperationIdentity;
    private readonly Action<SafeFileHandle> _markDeleteOnClose;
    private readonly Action<string>? _beforeDestinationCommit;
    private readonly Func<SafeFileHandle, string?> _getDestinationIdentity;

    public McdfFileBoundary() : this(Guid.NewGuid, null, null)
    {
    }

    internal McdfFileBoundary(
        Func<Guid>? newGuid = null,
        Action? inspectChunk = null,
        Action<string>? beforeCommit = null,
        Func<SafeFileHandle, string>? getFinalPath = null,
        Func<SafeFileHandle, string?>? getIdentity = null,
        Func<SafeFileHandle, string>? getOperationFinalPath = null,
        Func<SafeFileHandle, string?>? getOperationIdentity = null,
        Action<SafeFileHandle>? markDeleteOnClose = null,
        Action<string>? beforeDestinationCommit = null,
        Func<SafeFileHandle, string?>? getDestinationIdentity = null)
    {
        _newGuid = newGuid ?? Guid.NewGuid;
        _inspectionChunk = inspectChunk;
        _beforeCommit = beforeCommit;
        _getFinalPath =
            getFinalPath ?? McdfPlatformFileOwnership.GetRequiredFinalPath;
        _getIdentity =
            getIdentity ?? McdfPlatformFileOwnership.TryGetIdentity;
        _getOperationFinalPath =
            getOperationFinalPath ?? McdfPlatformFileOwnership.GetRequiredFinalPath;
        _getOperationIdentity =
            getOperationIdentity ?? McdfPlatformFileOwnership.TryGetIdentity;
        _markDeleteOnClose =
            markDeleteOnClose ?? McdfPlatformFileOwnership.MarkDeleteOnClose;
        _beforeDestinationCommit = beforeDestinationCommit;
        _getDestinationIdentity =
            getDestinationIdentity ?? McdfPlatformFileOwnership.TryGetIdentity;
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

    public IntegrationValue<McdfOperationDirectory> CreateOperationDirectory()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "Poser");
            Directory.CreateDirectory(root);
            string? lastAllocationFailure = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string allocationStep = "claiming the staging name";
                string id = _newGuid().ToString("N");
                string staging = Path.Combine(root, $".mcdf-staging-{id}");
                string directory = Path.Combine(root, $"mcdf-{id}");
                SafeFileHandle? directoryHandle = null;
                FileStream? markerStream = null;
                bool renamed = false;
                bool ownerVerified = false;
                bool markerAuthoritative = false;
                string? markerIdentity = null;
                string token = Convert.ToHexString(
                    RandomNumberGenerator.GetBytes(32));
                try
                {
                    if (!McdfPlatformFileOwnership.TryCreateDirectoryExclusive(staging))
                        continue;
                    allocationStep = "opening the fenced staging directory";
                    directoryHandle =
                        McdfPlatformFileOwnership.OpenFencedDirectory(staging);
                    allocationStep = "creating the owner marker";
                    markerStream =
                        McdfPlatformFileOwnership.CreateExclusiveOwnedMarker(
                            Path.Combine(staging, ".owner"));
                    markerAuthoritative = true;
                    markerStream.Write(Encoding.UTF8.GetBytes(token));
                    markerStream.Flush(flushToDisk: true);
                    markerIdentity =
                        McdfPlatformFileOwnership.TryGetIdentity(
                            markerStream.SafeFileHandle);
                    if (markerIdentity == null)
                        throw new IOException(
                            "The MCDF owner marker identity could not be verified.");
                    markerStream.Dispose();
                    markerStream = null;
                    markerAuthoritative = false;
                    allocationStep = "renaming the fenced directory";
                    McdfPlatformFileOwnership.CommitExactHandle(
                        directoryHandle, directory, replaceExisting: false);
                    renamed = true;
                    allocationStep = "reopening the owner marker";
                    markerStream = ReopenAndVerifyOwnerMarker(
                        Path.Combine(directory, ".owner"),
                        markerIdentity, token, throwOnFailure: true);
                    if (markerStream == null)
                        throw new IOException(
                            "The MCDF owner marker changed during allocation.");
                    ownerVerified = true;
                    markerAuthoritative = true;
                    allocationStep = "verifying the renamed directory";
                    string finalPath = _getOperationFinalPath(directoryHandle);
                    if (!PathsEqual(finalPath, directory))
                        throw new IOException(
                            $"The MCDF operation directory rename could not be verified ({finalPath} != {directory}).");
                    string? identity = _getOperationIdentity(directoryHandle);
                    if (identity == null)
                        throw new IOException(
                            "The MCDF operation directory identity could not be verified.");
                    markerStream.Dispose();
                    markerStream = null;
                    return IntegrationValue<McdfOperationDirectory>.Ok(
                        new McdfOperationDirectory(
                            finalPath, token, identity, markerIdentity));
                }
                catch (Exception ex) when (attempt < 7 && (!renamed || ownerVerified))
                {
                    lastAllocationFailure = $"{allocationStep}: {ex.Message}";
                    if (markerStream == null && directoryHandle != null)
                    {
                        try
                        {
                            markerStream = ReopenAndVerifyOwnerMarker(
                                Path.Combine(renamed ? directory : staging, ".owner"),
                                markerIdentity, token);
                            ownerVerified = markerStream != null;
                            markerAuthoritative = ownerVerified;
                        }
                        catch { }
                    }
                    if (markerAuthoritative)
                        DeleteAllocatedDirectoryWithHandles(
                            directoryHandle, markerStream);
                    else
                        markerStream?.Dispose();
                    markerStream = null;
                }
                catch (Exception ex)
                {
                    lastAllocationFailure = $"{allocationStep}: {ex.Message}";
                    if (markerStream == null && directoryHandle != null)
                    {
                        try
                        {
                            markerStream = ReopenAndVerifyOwnerMarker(
                                Path.Combine(renamed ? directory : staging, ".owner"),
                                markerIdentity, token);
                            ownerVerified = markerStream != null;
                            markerAuthoritative = ownerVerified;
                        }
                        catch { }
                    }
                    if (markerAuthoritative)
                        DeleteAllocatedDirectoryWithHandles(
                            directoryHandle, markerStream);
                    else
                        markerStream?.Dispose();
                    markerStream = null;
                    throw new IOException(
                        $"Operation directory allocation failed while {allocationStep}: {ex.Message}",
                        ex);
                }
                finally
                {
                    markerStream?.Dispose();
                    directoryHandle?.Dispose();
                }
            }
            return IntegrationValue<McdfOperationDirectory>.Fail(
                lastAllocationFailure == null
                    ? "The MCDF operation directory could not be allocated."
                    : $"The MCDF operation directory could not be allocated: {lastAllocationFailure}");
        }
        catch (Exception ex)
        {
            return IntegrationValue<McdfOperationDirectory>.Fail(
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
                    string finalPath = _getFinalPath(stream.SafeFileHandle);
                    if (EscapesRoot(Path.GetRelativePath(realRoot, finalPath)))
                    {
                        skipped.Add($"{actualRaw} (changed or outside the Penumbra mod directory)");
                        continue;
                    }
                    long length = stream.Length;
                    string hash = HashStream(
                        stream, HashAlgorithmName.SHA256, cancellation, _inspectionChunk);
                    string? identity = _getIdentity(stream.SafeFileHandle);
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
        McdfOperationDirectory operationDirectory,
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

    public IntegrationPortResult DeleteOperationDirectory(
        McdfOperationDirectory operationDirectory)
    {
        try
        {
            if (!Directory.Exists(operationDirectory.Path))
                return IntegrationPortResult.Ok();
            using var root =
                McdfPlatformFileOwnership.OpenFencedDirectory(operationDirectory.Path);
            if (!OwnedDirectoryMatches(operationDirectory, root))
                return IntegrationPortResult.Fail(
                    "The extraction directory ownership changed; cleanup was refused.");
            using var marker = ReopenAndVerifyOwnerMarker(
                Path.Combine(operationDirectory.Path, ".owner"),
                operationDirectory.MarkerIdentity,
                operationDirectory.OwnerToken);
            if (marker == null)
                return IntegrationPortResult.Fail(
                    "The extraction directory owner marker changed; cleanup was refused.");
            DeleteOwnedChildren(operationDirectory, root, marker);
            McdfPlatformFileOwnership.MarkDeleteOnClose(marker.SafeFileHandle);
            marker.Dispose();
            McdfPlatformFileOwnership.MarkDeleteOnClose(root);
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

    private static FileStream? ReopenAndVerifyOwnerMarker(
        string path,
        string? expectedIdentity,
        string expectedToken,
        bool throwOnFailure = false)
    {
        if (expectedIdentity == null)
        {
            if (throwOnFailure)
                throw new IOException("The expected MCDF owner marker identity was null.");
            return null;
        }
        FileStream? marker = null;
        try
        {
            marker = McdfPlatformFileOwnership.OpenOwnedMarker(path);
            string? currentIdentity =
                McdfPlatformFileOwnership.TryGetIdentity(marker.SafeFileHandle);
            if (currentIdentity == null
                || !string.Equals(
                    currentIdentity, expectedIdentity, StringComparison.Ordinal))
            {
                if (throwOnFailure)
                    throw new IOException($"The MCDF owner marker identity did not match (expected {expectedIdentity}, current {currentIdentity ?? "<null>"}).");
                return null;
            }
            using var reader = new StreamReader(
                marker, Encoding.UTF8, leaveOpen: true);
            if (!string.Equals(
                    reader.ReadToEnd(), expectedToken, StringComparison.Ordinal))
            {
                if (throwOnFailure)
                    throw new IOException("The MCDF owner marker token did not match.");
                return null;
            }
            marker.Position = 0;
            var verified = marker;
            marker = null;
            return verified;
        }
        catch (Exception ex) when (throwOnFailure && ex is not IOException)
        {
            throw new IOException($"The MCDF owner marker could not be reopened: {ex.Message}", ex);
        }
        finally
        {
            marker?.Dispose();
        }
    }

    private static bool OwnedDirectoryMatches(
        McdfOperationDirectory ownership,
        SafeFileHandle? existingHandle = null)
    {
        try
        {
            using var opened = existingHandle == null
                ? McdfPlatformFileOwnership.OpenFencedDirectory(ownership.Path)
                : null;
            var handle = existingHandle ?? opened!;
            string finalPath = McdfPlatformFileOwnership.GetRequiredFinalPath(handle);
            string? identity = McdfPlatformFileOwnership.TryGetIdentity(handle);
            if (!PathsEqual(finalPath, ownership.Path)
                || ownership.Identity == null
                || identity == null
                || !string.Equals(identity, ownership.Identity, StringComparison.Ordinal))
                return false;
            using var marker = ReopenAndVerifyOwnerMarker(
                Path.Combine(ownership.Path, ".owner"),
                ownership.MarkerIdentity,
                ownership.OwnerToken);
            return marker != null;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteOwnedChildren(
        McdfOperationDirectory ownership,
        SafeFileHandle rootHandle,
        FileStream markerHandle)
    {
        string marker = Path.Combine(ownership.Path, ".owner");
        foreach (string child in Directory.EnumerateFileSystemEntries(ownership.Path)
                     .Where(child => !PathsEqual(child, marker)))
        {
            if (!OwnedDirectoryMatches(ownership, rootHandle))
                throw new IOException(
                    "The extraction directory ownership changed during cleanup.");
            var attributes = File.GetAttributes(child);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(
                    "A reparse point appeared in the extraction directory; cleanup was refused.");
            if ((attributes & FileAttributes.Directory) != 0)
                Directory.Delete(child, recursive: false);
            else
                File.Delete(child);
        }
        if (!OwnedDirectoryMatches(ownership, rootHandle))
            throw new IOException(
                "The extraction directory ownership changed during cleanup.");
        string markerPath = McdfPlatformFileOwnership.GetRequiredFinalPath(
            markerHandle.SafeFileHandle);
        if (!PathsEqual(markerPath, marker))
            throw new IOException(
                "The extraction directory owner marker changed during cleanup.");
    }

    private static void DeleteAllocatedDirectoryWithHandles(
        SafeFileHandle? directoryHandle,
        FileStream? markerStream)
    {
        try
        {
            if (directoryHandle == null || directoryHandle.IsInvalid)
                return;
            if (markerStream != null)
            {
                McdfPlatformFileOwnership.MarkDeleteOnClose(
                    markerStream.SafeFileHandle);
                markerStream.Dispose();
            }
            McdfPlatformFileOwnership.MarkDeleteOnClose(directoryHandle);
        }
        catch
        {
        }
    }

    // ── Read ─────────────────────────────────────────────────────────────

    private IntegrationValue<McdfPackage> ReadCore(
        string path,
        McdfLimits limits,
        McdfOperationDirectory operationDirectory,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation)
    {
        // The caller owns the operation directory and registered it before
        // calling; a failure here leaves partial extraction for the
        // caller's visible, retryable cleanup.
        try
        {
            using var operationRoot =
                McdfPlatformFileOwnership.OpenFencedDirectory(operationDirectory.Path);
            if (!OwnedDirectoryMatches(operationDirectory, operationRoot))
                return IntegrationValue<McdfPackage>.Fail(
                    "The extraction directory ownership changed; extraction was refused.");
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
            if (!OwnedDirectoryMatches(operationDirectory, operationRoot))
                return IntegrationValue<McdfPackage>.Fail(
                    "The extraction directory ownership changed; extraction was refused.");
            var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
            long bytesDone = 0;
            var chunk = new byte[ChunkSize];
            for (int i = 0; i < data.Files.Count; i++)
            {
                if (cancellation.IsCancellationRequested)
                    return IntegrationValue<McdfPackage>.Fail("The import was cancelled.");
                var entry = data.Files[i];
                if (!OwnedDirectoryMatches(operationDirectory, operationRoot))
                    return IntegrationValue<McdfPackage>.Fail(
                        "The extraction directory ownership changed; extraction was refused.");
                string extracted = Path.Combine(
                    operationDirectory.Path, $"p{i:D4}.dat");
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
                operationDirectory.Path,
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
        FileStream? ownedOutput = null;
        SafeFileHandle? destinationHandle = null;
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
                destinationIdentity = _getDestinationIdentity(destinationHandle);
                if (destinationIdentity == null)
                    throw new WriteFailureException(
                        "The existing destination identity could not be verified.");
            }
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
                    throw new WriteFailureException("The export was cancelled.");
                var source = file.Source ?? CaptureSource(file.LocalPath, cancellation);
                using (var input = OpenValidatedSource(file.LocalPath, source, cancellation,
                    out string? sourceError))
                {
                    if (input == null)
                        throw new WriteFailureException(
                            sourceError ?? $"{file.LocalPath} changed while exporting.");
                    string localDigest = HashStream(
                        input, HashAlgorithmName.SHA256, cancellation, null,
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
                    string digest = HashStream(
                        input, HashAlgorithmName.SHA1, cancellation, null);
                    long length = input.Length;
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
                var chunk = new byte[ChunkSize];
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
                        input, HashAlgorithmName.SHA256, cancellation, null);
                    if (!string.Equals(localDigestBeforeCopy, source.ContentHash,
                            StringComparison.OrdinalIgnoreCase))
                        throw new WriteFailureException(
                            $"{byHash[digest].LocalPath} changed while exporting; its declared hash would be false.");
                    input.Position = 0;
                    string wireDigestBeforeCopy = HashStream(
                        input, HashAlgorithmName.SHA1, cancellation, null);
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
            _beforeCommit?.Invoke(temporary);
            _beforeDestinationCommit?.Invoke(fullDestination);
            if (destinationExisted)
            {
                if (!File.Exists(fullDestination)
                    || destinationHandle == null
                    || !string.Equals(
                        _getDestinationIdentity(destinationHandle),
                        destinationIdentity,
                        StringComparison.Ordinal))
                    throw new WriteFailureException(
                        "The existing destination changed before commit; the export was refused.");
            }
            else if (File.Exists(fullDestination))
            {
                throw new WriteFailureException(
                    "A destination appeared before commit; the export was refused.");
            }
            cancellation.ThrowIfCancellationRequested();
            McdfPlatformFileOwnership.CommitExactHandle(
                ownedOutput.SafeFileHandle, fullDestination, destinationExisted);
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
            if (!moved && ownedOutput != null)
            {
                try
                {
                    _markDeleteOnClose(ownedOutput.SafeFileHandle);
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

    private FileStream? OpenValidatedSource(
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
            string finalPath = _getFinalPath(input.SafeFileHandle);
            if (!PathsEqual(finalPath, expected.CanonicalPath))
            {
                input.Dispose();
                error = $"{localPath} changed its canonical handle path while exporting.";
                return null;
            }
            string? identity = _getIdentity(input.SafeFileHandle);
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

    private McdfExportSourceObservation CaptureSource(
        string localPath, CancellationToken cancellation)
    {
        string fullPath = Path.GetFullPath(localPath);
        string realPath = ResolveRealPath(fullPath)
            ?? throw new IOException($"{localPath} could not be resolved for export.");
        using var input = new FileStream(
            realPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string finalPath = _getFinalPath(input.SafeFileHandle);
        if (!PathsEqual(finalPath, realPath))
            throw new IOException($"{localPath} changed while being opened.");
        long length = input.Length;
        string hash = HashStream(
            input, HashAlgorithmName.SHA256, cancellation, null);
        return new McdfExportSourceObservation(
            finalPath, string.Empty, length, hash,
            _getIdentity(input.SafeFileHandle));
    }

    private static string HashStream(
        Stream stream,
        HashAlgorithmName algorithm,
        CancellationToken cancellation,
        Action? chunkHook,
        Action<int>? bytesRead = null)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
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
