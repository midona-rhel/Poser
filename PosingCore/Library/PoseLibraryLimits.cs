namespace Poser.Library;

/// <summary>
/// Hard bounds for one library traversal. A pass that reaches any bound is
/// abandoned and its previous coherent snapshot remains published.
/// </summary>
public static class PoseLibraryLimits
{
    /// <summary>Maximum directory nesting below a configured root.</summary>
    public const int MaxDepth = 32;

    /// <summary>Maximum flattened folders in one published snapshot.</summary>
    public const int MaxFolders = 4_096;

    /// <summary>Maximum library files in one published snapshot.</summary>
    public const int MaxFiles = 32_768;

    /// <summary>Cancellation is cooperative and aborts the whole pass.</summary>
    public const bool CancellationAbortsPass = true;

    // Descriptive aliases keep call sites explicit about what is bounded.
    public const int MaxTraversalDepth = MaxDepth;
    public const int MaxTraversalFolders = MaxFolders;
    public const int MaxTraversalFiles = MaxFiles;
}
