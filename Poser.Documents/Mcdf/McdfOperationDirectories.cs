using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Poser.Domain.Integration;

namespace Poser.Documents.Mcdf;

/// <summary>
/// Owned extraction directories: fenced allocation with an owner marker,
/// ownership proofs, payload copies between owned directories, and
/// ownership-checked deletion.
/// </summary>
internal static class McdfOperationDirectories
{
    public static IntegrationValue<McdfOperationDirectory> Create()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "Poser");
            Directory.CreateDirectory(root);
            string? lastAllocationFailure = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                string allocationStep = "claiming the staging name";
                string id = Guid.NewGuid().ToString("N");
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
                    string finalPath = McdfPlatformFileOwnership.GetRequiredFinalPath(directoryHandle);
                    if (!McdfPlatformFileOwnership.PathsEqual(finalPath, directory))
                        throw new IOException(
                            $"The MCDF operation directory rename could not be verified ({finalPath} != {directory}).");
                    string? identity = McdfPlatformFileOwnership.TryGetIdentity(directoryHandle);
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

    public static IntegrationResult Delete(McdfOperationDirectory operationDirectory)
    {
        try
        {
            if (!Directory.Exists(operationDirectory.Path))
                return IntegrationResult.Ok();
            using var root =
                McdfPlatformFileOwnership.OpenFencedDirectory(operationDirectory.Path);
            if (!OwnedDirectoryMatches(operationDirectory, root))
                return IntegrationResult.Fail(
                    "The extraction directory ownership changed; cleanup was refused.");
            using var marker = ReopenAndVerifyOwnerMarker(
                Path.Combine(operationDirectory.Path, ".owner"),
                operationDirectory.MarkerIdentity,
                operationDirectory.OwnerToken);
            if (marker == null)
                return IntegrationResult.Fail(
                    "The extraction directory owner marker changed; cleanup was refused.");
            DeleteOwnedChildren(operationDirectory, root, marker);
            McdfPlatformFileOwnership.MarkDeleteOnClose(marker.SafeFileHandle);
            marker.Dispose();
            McdfPlatformFileOwnership.MarkDeleteOnClose(root);
            return IntegrationResult.Ok();
        }
        catch (Exception ex)
        {
            // A file still held open (an antivirus scan, the game itself)
            // is a REPORTED failure: the caller keeps ownership of the
            // directory and retries instead of releasing deleted-in-name
            // payloads.
            return IntegrationResult.Fail(
                $"The extracted files could not be deleted: {ex.Message}");
        }
    }

    public static Task<IntegrationValue<McdfPackage>> Copy(McdfPackage package,
        McdfOperationDirectory source, McdfOperationDirectory destination, CancellationToken cancellation) =>
        Task.Run(async () =>
        {
            try
            {
                using var sourceRoot = McdfPlatformFileOwnership.OpenFencedDirectory(source.Path);
                using var destinationRoot = McdfPlatformFileOwnership.OpenFencedDirectory(destination.Path);
                if (!OwnedDirectoryMatches(source, sourceRoot) || !OwnedDirectoryMatches(destination, destinationRoot)
                    || !string.Equals(Path.GetFullPath(package.OperationDirectory), Path.GetFullPath(source.Path),
                        StringComparison.OrdinalIgnoreCase))
                    return IntegrationValue<McdfPackage>.Fail("Character-file directory ownership changed.");
                var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
                var copied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                long total = 0;
                for (int index = 0; index < package.FileCount; index++)
                {
                    string payload = Path.Combine(source.Path, $"p{index:D4}.dat");
                    cancellation.ThrowIfCancellationRequested();
                    if (!copied.TryGetValue(payload, out var outputPath))
                    {
                        using var input = new FileStream(payload, FileMode.Open, FileAccess.Read, FileShare.Read);
                        // Check the opened file, not just its supplied name: no link may redirect a retained payload.
                        var actual = McdfPlatformFileOwnership.GetRequiredFinalPath(input.SafeFileHandle);
                        if (!string.Equals(Path.GetDirectoryName(actual), Path.GetFullPath(source.Path),
                                StringComparison.OrdinalIgnoreCase))
                            return IntegrationValue<McdfPackage>.Fail("A retained payload escaped its owned directory.");
                        total = checked(total + input.Length);
                        if (total > package.TotalBytes)
                            return IntegrationValue<McdfPackage>.Fail("The retained character-file payload changed.");
                        outputPath = Path.Combine(destination.Path, $"p{copied.Count:D4}.dat");
                        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                        await input.CopyToAsync(output, cancellation);
                        copied.Add(payload, outputPath);
                    }
                }
                if (total != package.TotalBytes || copied.Count != package.FileCount)
                    return IntegrationValue<McdfPackage>.Fail("The retained character-file payload is incomplete.");
                foreach (var (gamePath, payload) in package.ReplacedGamePaths)
                {
                    if (!copied.TryGetValue(payload, out var copiedPath))
                        return IntegrationValue<McdfPackage>.Fail("A retained resource has no owned payload.");
                    mapped.Add(gamePath, copiedPath);
                }
                return IntegrationValue<McdfPackage>.Ok(package with
                {
                    ReplacedGamePaths = mapped,
                    SwappedGamePaths = new Dictionary<string, string>(package.SwappedGamePaths, StringComparer.Ordinal),
                    OperationDirectory = destination.Path,
                });
            }
            catch (Exception ex) { return IntegrationValue<McdfPackage>.Fail($"Restoring character-file payloads failed: {ex.Message}"); }
        }, cancellation);

    public static bool OwnedDirectoryMatches(
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
            if (!McdfPlatformFileOwnership.PathsEqual(finalPath, ownership.Path)
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

    private static void DeleteOwnedChildren(
        McdfOperationDirectory ownership,
        SafeFileHandle rootHandle,
        FileStream markerHandle)
    {
        string marker = Path.Combine(ownership.Path, ".owner");
        foreach (string child in Directory.EnumerateFileSystemEntries(ownership.Path)
                     .Where(child => !McdfPlatformFileOwnership.PathsEqual(child, marker)))
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
        if (!McdfPlatformFileOwnership.PathsEqual(markerPath, marker))
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
}
