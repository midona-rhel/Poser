using System;

namespace Poser.Library;

/// <summary>
/// Scans the configured pose roots off-thread and publishes the result as ONE
/// immutable snapshot. Nothing is scanned until a caller asks: plugin load pays
/// nothing, and no frame may touch the file system.
/// </summary>
public interface IPoseLibraryService : IDisposable
{
    /// <summary>
    /// Latest published snapshot; never null, starts empty at revision 0.
    /// </summary>
    PoseLibrarySnapshot Snapshot { get; }

    bool IsScanning { get; }

    /// <summary>
    /// Kicks a background rescan. A call made while one is running coalesces
    /// into exactly one more run afterwards.
    /// </summary>
    void RequestScan();
}
