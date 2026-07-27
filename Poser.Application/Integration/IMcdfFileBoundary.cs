using Poser.Domain.Integration;

namespace Poser.Application.Integration;

/// <summary>
/// The MCDF v1 file boundary: package reading with validation/extraction,
/// and atomic package writing. Pure file work — no IPC, no actor state —
/// runs off-thread and reports progress cooperatively.
/// </summary>
public interface IMcdfFileBoundary
{
    /// <summary>
    /// Reads, validates, and extracts a complete package into a unique
    /// Poser operation directory. Any validation failure, limit breach, or
    /// cancellation deletes the operation directory before returning.
    /// </summary>
    Task<IntegrationValue<McdfPackage>> ReadPackage(
        string path,
        McdfLimits limits,
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
