namespace Poser.Documents.Mcdf;

/// <summary>Holds a declared MCDF header to the import limits and the game-path rules
/// before anything is extracted.</summary>
internal static class McdfPackageValidator
{
    /// <summary>Returns the refusal reason, or null when the declaration is acceptable.</summary>
    public static string? Validate(McdfWireData data, McdfLimits limits, out long totalBytes)
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
            if (entry.Hash.Length != 0 &&
                (entry.Hash.Length is not (40 or 64) || !entry.Hash.All(Uri.IsHexDigit)))
                return "A payload declares an unsupported hash; expected SHA-1 or BLAKE3 hexadecimal data.";
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
}
