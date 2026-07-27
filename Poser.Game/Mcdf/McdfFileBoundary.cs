using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using K4os.Compression.LZ4.Legacy;
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
        Action<McdfProgressStep> progress,
        CancellationToken cancellation) =>
        Task.Run(() => ReadCore(path, limits, progress, cancellation), CancellationToken.None);

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
        Action<McdfProgressStep> progress,
        CancellationToken cancellation)
    {
        string operationDirectory = Path.Combine(
            Path.GetTempPath(), "Poser", $"mcdf-{Guid.NewGuid():N}");
        bool keep = false;
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

            keep = true;
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
        finally
        {
            if (!keep)
                DeleteOperationDirectory(operationDirectory);
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
        string temporary = destination + ".tmp";
        bool moved = false;
        try
        {
            // Pass 1 — hash and measure every local file so the header can
            // precede the payloads, deduplicating identical content by
            // SHA-1 while every game path is preserved.
            var byHash = new Dictionary<string, (WireFile Entry, string LocalPath)>(
                StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            long totalBytes = 0;
            long hashedBytes = 0;
            long toHash = 0;
            foreach (var file in content.Files)
                toHash += new FileInfo(file.LocalPath).Length;

            foreach (var file in content.Files)
            {
                if (cancellation.IsCancellationRequested)
                    return IntegrationValue<McdfWriteStats>.Fail("The export was cancelled.");
                long length = 0;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                using (var input = new FileStream(
                    file.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var chunk = new byte[ChunkSize];
                    int got;
                    while ((got = input.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (cancellation.IsCancellationRequested)
                            return IntegrationValue<McdfWriteStats>.Fail("The export was cancelled.");
                        hash.AppendData(chunk, 0, got);
                        length += got;
                        hashedBytes += got;
                        progress(new McdfProgressStep(
                            McdfPhase.WritingPackage, 0, content.Files.Count, hashedBytes, toHash));
                    }
                }
                if (length > int.MaxValue)
                    return IntegrationValue<McdfWriteStats>.Fail(
                        $"{file.LocalPath} is too large for the MCDF format.");

                string digest = Convert.ToHexString(hash.GetHashAndReset());
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
                    }, file.LocalPath);
                    order.Add(digest);
                    totalBytes += length;
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

            // Pass 2 — write header + payloads to the temporary path, then
            // replace the destination atomically.
            using (var output = new FileStream(
                temporary, FileMode.Create, FileAccess.Write, FileShare.None))
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
                    using var input = new FileStream(
                        byHash[digest].LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var rehash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    int got;
                    long fileWritten = 0;
                    while ((got = input.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (cancellation.IsCancellationRequested)
                            return IntegrationValue<McdfWriteStats>.Fail("The export was cancelled.");
                        writer.Write(chunk, 0, got);
                        rehash.AppendData(chunk, 0, got);
                        written += got;
                        fileWritten += got;
                        progress(new McdfProgressStep(
                            McdfPhase.WritingPackage, done, order.Count, written, totalBytes));
                    }
                    // A file that changed since pass 1 — by size OR by a
                    // same-length modification — would make the declared
                    // SHA-1 false and corrupt payload alignment; fail
                    // before the destination is ever replaced.
                    if (fileWritten != byHash[digest].Entry.Length)
                        return IntegrationValue<McdfWriteStats>.Fail(
                            $"{byHash[digest].LocalPath} changed while exporting.");
                    if (!string.Equals(
                            Convert.ToHexString(rehash.GetHashAndReset()),
                            digest, StringComparison.OrdinalIgnoreCase))
                        return IntegrationValue<McdfWriteStats>.Fail(
                            $"{byHash[digest].LocalPath} changed while exporting; its declared hash would be false.");
                    done++;
                    progress(new McdfProgressStep(
                        McdfPhase.WritingPackage, done, order.Count, written, totalBytes));
                }

                writer.Flush();
            }

            if (File.Exists(destination))
                File.Replace(temporary, destination, destinationBackupFileName: null);
            else
                File.Move(temporary, destination);
            moved = true;
            return IntegrationValue<McdfWriteStats>.Ok(
                new McdfWriteStats(order.Count, totalBytes));
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
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch
                {
                    // Leaving a .tmp behind is preferable to masking the
                    // original failure; the destination itself is untouched.
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
}
