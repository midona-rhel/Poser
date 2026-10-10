using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4.Legacy;
using Poser.Domain.Integration;

namespace Poser.Documents.Mcdf;

/// <summary>Reads MCDF v1 packages: the header-only summary, and the validated
/// extraction into a caller-owned operation directory.</summary>
internal static class McdfReader
{
    public static IntegrationValue<McdfPackage> ReadPackage(
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
            if (!McdfOperationDirectories.OwnedDirectoryMatches(operationDirectory, operationRoot))
                return IntegrationValue<McdfPackage>.Fail(
                    "The extraction directory ownership changed; extraction was refused.");
            progress(new McdfProgressStep(McdfPhase.Reading, 0, 0, 0, 0));
            using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var lz4 = LZ4Legacy.Decode(file, leaveOpen: true);
            using var reader = new BinaryReader(lz4, Encoding.UTF8, leaveOpen: true);

            var header = ReadHeader(lz4, reader, out string? headerFailure);
            if (header == null)
                return IntegrationValue<McdfPackage>.Fail(headerFailure!);
            McdfWireData data = header;

            progress(new McdfProgressStep(McdfPhase.Validating, 0, data.Files.Count, 0, 0));
            var validation = McdfPackageValidator.Validate(data, limits, out long totalBytes);
            if (validation != null)
                return IntegrationValue<McdfPackage>.Fail(validation);

            // Extraction: generated file names inside a unique operation
            // directory; archive-declared names are never used on disk.
            if (!McdfOperationDirectories.OwnedDirectoryMatches(operationDirectory, operationRoot))
                return IntegrationValue<McdfPackage>.Fail(
                    "The extraction directory ownership changed; extraction was refused.");
            var replaced = new Dictionary<string, string>(StringComparer.Ordinal);
            long bytesDone = 0;
            var chunk = new byte[McdfWire.ChunkSize];
            for (int i = 0; i < data.Files.Count; i++)
            {
                if (cancellation.IsCancellationRequested)
                    return IntegrationValue<McdfPackage>.Fail("The import was cancelled.");
                var entry = data.Files[i];
                if (!McdfOperationDirectories.OwnedDirectoryMatches(operationDirectory, operationRoot))
                    return IntegrationValue<McdfPackage>.Fail(
                        "The extraction directory ownership changed; extraction was refused.");
                string extracted = Path.Combine(
                    operationDirectory.Path, $"p{i:D4}.dat");
                using var hash = new McdfPayloadHash(legacy: entry.Hash.Length == 40);
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
                        hash.Append(chunk.AsSpan(0, got));
                        remaining -= got;
                        bytesDone += got;
                        progress(new McdfProgressStep(
                            McdfPhase.Extracting, i, data.Files.Count, bytesDone, totalBytes));
                    }
                }

                if (entry.Hash.Length > 0)
                {
                    string computed = hash.Finish();
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

    public static IntegrationValue<McdfSummary> ReadSummary(string path)
    {
        try
        {
            using var file = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var lz4 = LZ4Legacy.Decode(file, leaveOpen: true);
            using var reader = new BinaryReader(lz4, Encoding.UTF8, leaveOpen: true);
            var data = ReadHeader(lz4, reader, out string? failure);
            if (data == null)
                return IntegrationValue<McdfSummary>.Fail(failure!);

            // DECLARED bytes, and named so: nothing past the header is read,
            // so this is what the package says its payloads weigh and not
            // what they weigh. The import's own validation is what holds the
            // declaration to the limits.
            long declared = 0;
            foreach (var entry in data.Files)
                declared += Math.Max(0, entry.Length);
            return IntegrationValue<McdfSummary>.Ok(new McdfSummary(
                Path.GetFileName(path),
                data.Description,
                data.Files.Count,
                declared,
                data.FileSwaps.Count,
                data.GlamourerData.Length > 0,
                data.CustomizePlusData.Length > 0,
                data.ManipulationData.Length > 0));
        }
        catch (EndOfStreamException)
        {
            return IntegrationValue<McdfSummary>.Fail("The package is truncated.");
        }
        catch (Exception ex)
        {
            return IntegrationValue<McdfSummary>.Fail(
                $"Reading the package header failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The package's opening: magic, version, and the declaration JSON. Shared
    /// by the extracting read and the header-only summary so there is ONE
    /// account of what a valid MCDF opens with — and so a file the summary
    /// accepted can never be refused for its header by the import that
    /// follows. Leaves the stream positioned on the first payload byte.
    /// </summary>
    private static McdfWireData? ReadHeader(
        Stream lz4, BinaryReader reader, out string? failure)
    {
        failure = null;
        var magic = reader.ReadBytes(4);
        if (magic.Length != 4 || magic[0] != (byte)'M' || magic[1] != (byte)'C'
            || magic[2] != (byte)'D' || magic[3] != (byte)'F')
        {
            failure = "This is not an MCDF character file.";
            return null;
        }
        byte version = reader.ReadByte();
        if (version != McdfFormat.Version)
        {
            failure =
                $"MCDF version {version} is not supported (expected {McdfFormat.Version}).";
            return null;
        }

        int jsonLength = reader.ReadInt32();
        if (jsonLength <= 0 || jsonLength > McdfWire.MaxJsonBytes)
        {
            failure = $"The package declares an invalid header length ({jsonLength}).";
            return null;
        }
        var jsonBytes = new byte[jsonLength];
        ReadExact(lz4, jsonBytes, "package header");

        McdfWireData? data;
        try
        {
            data = JsonSerializer.Deserialize<McdfWireData>(jsonBytes, McdfWire.JsonOptions);
        }
        catch (JsonException ex)
        {
            failure = $"The package header is not valid JSON: {ex.Message}";
            return null;
        }
        if (data != null)
            return data;
        failure = "The package header is empty.";
        return null;
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
