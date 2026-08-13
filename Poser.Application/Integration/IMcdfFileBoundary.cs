using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// The MCDF v1 file boundary: package reading with validation/extraction,
/// and atomic package writing. Pure file work — no IPC, no actor state —
/// runs off-thread and reports progress cooperatively.
/// </summary>
public interface IMcdfFileBoundary
{
    /// <summary>Returns the display name for a user-selected MCDF path.</summary>
    string GetFileName(string path);

    /// <summary>Allocates and creates one unique caller-owned extraction
    /// directory. The caller registers the returned path before reading.</summary>
    IntegrationValue<string> CreateOperationDirectory();

    /// <summary>Inspects Penumbra's raw resource map. All filesystem policy
    /// (canonicalization, readability, metadata, and real-root containment)
    /// is completed before observations cross into the application.</summary>
    IntegrationValue<McdfExportInspection> InspectExportCandidates(
        string modRoot,
        IReadOnlyDictionary<string, IReadOnlyList<string>> resources);

    /// <summary>
    /// Reads, validates, and extracts a complete package into the
    /// CALLER-OWNED operation directory. The boundary never deletes it —
    /// the caller registered the directory before this call, so a failed
    /// read leaves a visible, retryable cleanup obligation instead of a
    /// silently orphaned directory.
    /// </summary>
    Task<IntegrationValue<McdfPackage>> ReadPackage(
        string path,
        McdfLimits limits,
        string operationDirectory,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation);

    /// <summary>
    /// Writes the complete package to <c>destination + ".tmp"</c>, flushes
    /// and closes the LZ4 stream, then atomically replaces the destination.
    /// Failure or cancellation removes the temporary output and leaves an
    /// existing destination untouched.
    /// </summary>
    Task<IntegrationValue<McdfWriteStats>> WritePackage(
        string destination,
        McdfExportContent content,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation);

    /// <summary>Deletes one extraction directory. Idempotent for
    /// already-deleted directories; a failure is RETURNED so the caller
    /// keeps directory ownership and can retry — extracted payloads must
    /// never be released while something might still reference them.</summary>
    IntegrationPortResult DeleteOperationDirectory(string operationDirectory);
}
