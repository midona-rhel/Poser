using Poser.Domain.Integration;

namespace Poser.Documents.Mcdf;

/// <summary>
/// The <see cref="IMcdfFileBoundary"/> port over the MCDF v1 file work:
/// <see cref="McdfReader"/> (with <see cref="McdfPackageValidator"/>),
/// <see cref="McdfWriter"/> and <see cref="McdfOperationDirectories"/>.
/// Package reads and writes run off-thread.
/// </summary>
public sealed class McdfFileBoundary : IMcdfFileBoundary
{
    public string GetFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }

    public IntegrationValue<McdfOperationDirectory> CreateOperationDirectory() =>
        McdfOperationDirectories.Create();

    public IntegrationValue<McdfExportInspection> InspectExportCandidates(
        string modRoot,
        IReadOnlyDictionary<string, IReadOnlyList<string>> resources,
        CancellationToken cancellation) =>
        McdfWriter.InspectCandidates(modRoot, resources, cancellation);

    public IntegrationValue<McdfSummary> ReadSummary(string path) =>
        McdfReader.ReadSummary(path);

    public Task<IntegrationValue<McdfPackage>> ReadPackage(
        string path,
        McdfLimits limits,
        McdfOperationDirectory operationDirectory,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation) =>
        Task.Run(
            () => McdfReader.ReadPackage(path, limits, operationDirectory, progress, cancellation),
            CancellationToken.None);

    public Task<IntegrationValue<McdfPackage>> CopyPackage(McdfPackage package,
        McdfOperationDirectory source, McdfOperationDirectory destination, CancellationToken cancellation) =>
        McdfOperationDirectories.Copy(package, source, destination, cancellation);

    public Task<IntegrationValue<McdfWriteStats>> WritePackage(
        string destination,
        McdfExportContent content,
        Action<McdfProgressStep> progress,
        CancellationToken cancellation) =>
        Task.Run(() => McdfWriter.Write(destination, content, progress, cancellation), CancellationToken.None);

    public IntegrationResult DeleteOperationDirectory(
        McdfOperationDirectory operationDirectory) =>
        McdfOperationDirectories.Delete(operationDirectory);
}
