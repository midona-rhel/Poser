namespace Poser.Documents.Library;

/// <summary>
/// How much the library lists. A source folder that goes past a limit is
/// shown as failed and lists nothing; the sources after it can still use
/// what is left.
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
}
