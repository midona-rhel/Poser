namespace Poser.Documents.Mcdf;

/// <summary>
/// Hard validation limits for reading an MCDF package. Exceeding any limit
/// is an explicit failure before actor mutation, never a silent trim.
/// </summary>
public sealed record McdfLimits(
    long MaxTotalBytes,
    long MaxFileBytes,
    int MaxFileCount,
    int MaxGamePathCount)
{
    /// <summary>Conservative defaults: 2 GiB expanded total, 512 MiB for a
    /// single file, 1024 file entries, 4096 game paths.</summary>
    public static McdfLimits Default => new(
        MaxTotalBytes: 2L * 1024 * 1024 * 1024,
        MaxFileBytes: 512L * 1024 * 1024,
        MaxFileCount: 1024,
        MaxGamePathCount: 4096);
}
