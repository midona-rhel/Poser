using System;
using Poser.Domain.Library;

namespace Poser.Application.Library;

/// <summary>
/// Scans configured folders in the background and publishes a snapshot for the UI.
/// RequestScan starts or requests another scan; reading Snapshot does not scan the disk.
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
