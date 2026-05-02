using System.Collections.Generic;
using Poser.Library;

namespace Poser.Config;

/// <summary>
/// Configuration for the pose library system.
/// </summary>
public class LibraryConfiguration
{
    /// <summary>
    /// Configured library sources.
    /// </summary>
    public List<LibrarySource> Sources { get; set; } = new()
    {
        LibrarySource.CreatePoserPoses(),
        LibrarySource.CreateBrioPoses(),
        LibrarySource.CreateAnamnesisPoses()
    };

    /// <summary>
    /// Set of favorited entry identifiers (paths).
    /// </summary>
    public HashSet<string> Favorites { get; set; } = new();

    /// <summary>
    /// Icon size in the library grid (pixels).
    /// </summary>
    public float IconSize { get; set; } = 100f;

    /// <summary>
    /// Whether to show subdirectories in the grid.
    /// </summary>
    public bool ShowDirectories { get; set; } = true;

    /// <summary>
    /// Last visited directory path.
    /// </summary>
    public string? LastPath { get; set; }
}
