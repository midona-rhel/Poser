using Poser.Domain.Integration;

namespace Poser.Documents.Mcdf;

public sealed partial class McdfFileBoundary
{
    public Task<IntegrationValue<McdfPackage>> CopyPackage(McdfPackage package,
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
}
