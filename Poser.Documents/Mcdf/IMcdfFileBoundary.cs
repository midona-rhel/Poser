using Poser.Domain.Integration;

namespace Poser.Documents.Mcdf;

/// <summary>
/// Reads, unpacks and writes MCDF files. It only works with files: it does
/// not talk to other plugins or touch actors, so it runs in the background
/// and reports its progress as it goes.
/// </summary>
public interface IMcdfFileBoundary
{
    /// <summary>The name to show for a chosen MCDF file.</summary>
    string GetFileName(string path);

    /// <summary>Creates a new, empty folder to unpack one MCDF into. The
    /// caller records the folder before unpacking, so it can still delete it
    /// if unpacking fails.</summary>
    IntegrationValue<McdfOperationDirectory> CreateOperationDirectory();

    /// <summary>Checks the files Penumbra says an actor is using before they
    /// go into an MCDF: each file must be readable and lie inside Penumbra's
    /// mod folder. All file checks happen here, and the caller only gets the
    /// results.</summary>
    IntegrationValue<McdfExportInspection> InspectExportCandidates(
        string modRoot,
        IReadOnlyDictionary<string, IReadOnlyList<string>> resources,
        CancellationToken cancellation);

    /// <summary>
    /// Reads only the start of the file, which says what the MCDF holds, and
    /// stops before the packed mod files. It creates no folder and writes
    /// nothing, so it is safe to call for a file that is only highlighted in
    /// a list. <see cref="ReadPackage"/> is not.
    /// </summary>
    IntegrationValue<McdfSummary> ReadSummary(string path);

    /// <summary>The look a package holds, read without unpacking its mod files.</summary>
    IntegrationValue<McdfLook> ReadLook(string path) => McdfReader.ReadLook(path);

    /// <summary>
    /// Reads, checks and unpacks a whole MCDF into the given folder. This
    /// never deletes the folder: the caller recorded it beforehand, so after
    /// a failed read the caller still knows to delete it.
    /// </summary>
    Task<IntegrationValue<McdfPackage>> ReadPackage(
        string path,
        McdfLimits limits,
        McdfOperationDirectory operationDirectory,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation);

    /// <summary>Copies an MCDF that was already unpacked and checked into a new folder.
    /// Undo and redo use this to apply the same MCDF again without reading the file.</summary>
    Task<IntegrationValue<McdfPackage>> CopyPackage(McdfPackage package,
        McdfOperationDirectory source, McdfOperationDirectory destination, CancellationToken cancellation);

    /// <summary>
    /// Writes the whole MCDF to a temporary file in the destination's folder,
    /// then swaps it into place in one step. If writing fails or is
    /// cancelled, only the temporary file is removed, and a file already at
    /// the destination stays as it was.
    /// </summary>
    Task<IntegrationValue<McdfWriteStats>> WritePackage(
        string destination,
        McdfExportContent content,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation);

    /// <summary>Deletes one unpack folder. A folder that is already gone counts
    /// as deleted. A failure is returned, so the caller keeps the folder on its
    /// list and can try again later.</summary>
    IntegrationResult DeleteOperationDirectory(McdfOperationDirectory operationDirectory);
}
