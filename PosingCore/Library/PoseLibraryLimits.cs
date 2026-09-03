namespace Poser.Library;

/// <summary>
/// Per-source traversal and aggregate publication bounds. A source that
/// exceeds a bound is reported as failed and contributes no entries; later
/// sources can still use the remaining publication capacity.
/// </summary>
public static class PoseLibraryLimits
{
    /// <summary>Maximum directory nesting below a configured root.</summary>
    public const int MaxDepth = 32;

    /// <summary>Maximum flattened folders in one published snapshot.</summary>
    public const int MaxFolders = 4_096;

    /// <summary>Maximum library files in one published snapshot.</summary>
    public const int MaxFiles = 32_768;

    /// <summary>Maximum sources observed and retained in a snapshot. Additional
    /// configured sources are reported by SkippedSourceCount.</summary>
    public const int MaxSources = 64;

    /// <summary>Cancellation is cooperative and aborts the whole pass.</summary>
    public const bool CancellationAbortsPass = true;

    // Descriptive aliases keep call sites explicit about what is bounded.
    public const int MaxTraversalDepth = MaxDepth;
    public const int MaxTraversalFolders = MaxFolders;
    public const int MaxTraversalFiles = MaxFiles;
}
