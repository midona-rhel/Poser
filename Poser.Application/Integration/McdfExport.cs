using Poser.Application.Lifecycle;
using Poser.Documents.Mcdf;
using Poser.Domain.Identity;
using Poser.Domain.Integration;
using Poser.Domain.Operations;

namespace Poser.Application.Integration;

/// <summary>Every vendor read an export needs, frozen on the framework
/// thread before the operation is admitted.</summary>
internal sealed record McdfExportCapture(
    string GlamourerState,
    string CustomizeData,
    string ManipulationData,
    string Root,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Tree);

/// <summary>
/// The read-only MCDF export: the selected actor's supported external state
/// captured synchronously — export never changes the actor — then inspected,
/// filtered and written off-thread.
/// </summary>
internal sealed class McdfExport(
    IPenumbraPort penumbra,
    IGlamourerPort glamourer,
    IMcdfFileBoundary files,
    IntegrationOwnership ownership,
    SingleFlightOwner<McdfOperation, McdfProgress> flight)
{
    /// <summary>
    /// Captures the exact actor's supported external state. Refuses while an
    /// MCDF is active on the actor (no repackaging) and when a provider is
    /// unavailable or its state cannot be read.
    /// </summary>
    internal (IntegrationResult? Refusal, McdfExportCapture? Captured) Capture(ActorId actor)
    {
        var current = ownership.OverridesFor(actor);
        if (current.Mcdf != null)
            return (IntegrationResult.Fail(
                "This actor is wearing an imported character file; exporting would repackage it. Reset MCDF first."), null);
        if (!penumbra.Penumbra.Available)
            return (IntegrationResult.Fail(penumbra.Penumbra.Detail), null);
        if (!glamourer.Glamourer.Available)
            return (IntegrationResult.Fail(glamourer.Glamourer.Detail), null);

        var look = glamourer.CaptureGlamourerState(actor);
        if (!look.Success || look.Value is not { } glamourerState)
            return (IntegrationResult.Fail(
                look.Detail ?? "The Glamourer state could not be captured."), null);
        var manipulations = penumbra.GetActorMetaManipulations(actor);
        if (!manipulations.Success || manipulations.Value is not { } manipulationData)
            return (IntegrationResult.Fail(
                manipulations.Detail ?? "The meta manipulations could not be captured."), null);
        var resources = penumbra.GetActorResourcePaths(actor);
        if (!resources.Success || resources.Value is not { } tree)
            return (IntegrationResult.Fail(
                resources.Detail ?? "The actor's resources could not be captured."), null);
        var modRoot = penumbra.GetModDirectory();
        if (!modRoot.Success || modRoot.Value is not { } root)
            return (IntegrationResult.Fail(
                modRoot.Detail ?? "Penumbra's mod directory could not be read."), null);

        // The C+ active-profile query can omit temporary profiles. Copy,
        // history and export must use the same retained-profile precedence.
        var body = ownership.CaptureBodyProfile(actor);
        if (!body.Success)
            return (IntegrationResult.Fail(body.Detail ?? "The Customize+ profile could not be captured."), null);
        string customizeData = body.Value is { } profileJson
            ? Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(profileJson))
            : string.Empty;
        return (null, new McdfExportCapture(glamourerState, customizeData, manipulationData, root, tree));
    }

    internal async Task Run(
        McdfOperation operation,
        string path,
        string description,
        McdfExportCapture captured,
        CancellationToken cancellation)
    {
        var (glamourerState, customizeData, manipulationData, root, tree) = captured;
        var actor = operation.Target;
        string fileName = operation.FileName;
        int filesTotal = 0;
        long bytesTotal = 0;
        var skipped = new List<string>();

        void FinishFailure(string detail, int filesDone = 0, long bytesDone = 0)
        {
            bool cancelled = cancellation.IsCancellationRequested;
            var progress = new McdfProgress(
                actor, fileName, McdfOperationKind.Export,
                cancelled ? McdfPhase.Cancelled : McdfPhase.Failed,
                filesDone, filesTotal, bytesDone, bytesTotal, false,
                new McdfOutcome(false, cancelled, detail, 0, 0, skipped));
            var receipt = cancelled
                ? OperationReceipt.Cancelled(
                    operation.OperationId, operation.Epoch, operation.Session, actor, detail)
                : OperationReceipt.Failed(
                    operation.OperationId, operation.Epoch, operation.Session, actor, detail);
            flight.PublishTerminal(operation, progress, receipt);
        }

        try
        {
            var inspected = files.InspectExportCandidates(root, tree, cancellation);
            if (!inspected.Success || inspected.Value is not { } observation)
            {
                FinishFailure(inspected.Detail
                    ?? "The export resources could not be inspected.");
                return;
            }
            cancellation.ThrowIfCancellationRequested();
            var (content, contentSkipped, contentError) = BuildExportContent(
                description, glamourerState, customizeData, manipulationData, observation);
            skipped = contentSkipped;
            if (contentError != null || content == null)
            {
                FinishFailure(contentError ?? "The export content could not be built.");
                return;
            }
            filesTotal = content.Files.Count;
            var written = await files.WritePackage(path, content, step =>
            {
                bytesTotal = step.BytesTotal;
                flight.PublishStep(operation, new McdfProgress(
                    actor, fileName, McdfOperationKind.Export,
                    step.Phase, step.FilesDone, step.FilesTotal,
                    step.BytesDone, step.BytesTotal, true, null));
            }, cancellation);
            if (!written.Success || written.Value is not { } stats)
            {
                FinishFailure(written.Detail ?? "The package could not be written.");
                return;
            }
            string detail = skipped.Count == 0
                ? $"Exported {stats.Files} files ({stats.UncompressedBytes:N0} bytes)."
                : $"Exported {stats.Files} files ({stats.UncompressedBytes:N0} bytes); {skipped.Count} resources skipped.";
            flight.PublishTerminal(
                operation,
                new McdfProgress(actor, fileName, McdfOperationKind.Export,
                    McdfPhase.Completed, stats.Files, stats.Files,
                    stats.UncompressedBytes, stats.UncompressedBytes, false,
                    new McdfOutcome(true, false, detail,
                        stats.Files, stats.UncompressedBytes, skipped)),
                OperationReceipt.Applied(
                    operation.OperationId, operation.Epoch, operation.Session, actor, detail));
        }
        catch (OperationCanceledException)
        {
            FinishFailure("The export was cancelled.");
        }
        catch (Exception ex)
        {
            FinishFailure($"The export failed unexpectedly: {ex.Message}");
        }
    }

    /// <summary>
    /// Turns Penumbra's actual-path → game-paths tree into MCDF content:
    /// only real replacements, swap targets validated as game paths and
    /// kept game-path to game-path, allowed extensions only, local files
    /// only from under the CANONICAL Penumbra mod root, Brio's
    /// compatibility filter applied, every skipped or missing resource
    /// reported by name, and conflicting duplicate game-path mappings
    /// rejected outright — a package that lies about a path is worse than
    /// no package.
    /// </summary>
    private static (McdfExportContent? Content, List<string> Skipped, string? Error)
        BuildExportContent(
            string description,
            string glamourerState,
            string customizeData,
            string manipulationData,
            McdfExportInspection observation)
    {
        var skipped = observation.Skipped.ToList();
        var files = new List<McdfExportFile>();
        var swaps = new Dictionary<string, string>(StringComparer.Ordinal);
        // Which exported source already serves each game path; identical
        // duplicates are ignored, conflicting ones fail the export.
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var candidate in observation.Candidates)
        {
            // A local filesystem path is a file replacement; anything else
            // is a game path — identical means unmodified, different means
            // a swap.
            bool isLocalFile = candidate.Kind == McdfExportCandidateKind.LocalFile;
            string actualRaw = candidate.ActualPath;
            var gamePathsRaw = candidate.GamePaths;

            string sourceKey;
            string? localFull = candidate.LocalPath;
            string swapTarget = string.Empty;
            if (isLocalFile)
            {
                if (localFull is null)
                    continue;
                sourceKey = localFull;
            }
            else
            {
                swapTarget = McdfFormat.NormalizeGamePath(actualRaw);
                if (McdfFormat.ValidateGamePath(swapTarget) != null)
                {
                    skipped.Add($"{actualRaw} (unsupported swap target)");
                    continue;
                }
                sourceKey = swapTarget;
            }

            var replaced = new List<string>();
            foreach (var rawGamePath in gamePathsRaw)
            {
                string gamePath = McdfFormat.NormalizeGamePath(rawGamePath);
                if (!isLocalFile && gamePath == swapTarget)
                    continue; // Unmodified resource, not a replacement.
                if (McdfFormat.ValidateGamePath(gamePath) != null)
                {
                    skipped.Add($"{gamePath} (unsupported resource path)");
                    continue;
                }
                if (!McdfFormat.ExportFilterAllows(gamePath))
                {
                    skipped.Add($"{gamePath} (omitted for MCDF compatibility)");
                    continue;
                }
                if (sources.TryGetValue(gamePath, out var previous))
                {
                    if (string.Equals(previous, sourceKey, StringComparison.OrdinalIgnoreCase))
                        continue; // Identical duplicate mapping.
                    return (null, skipped,
                        $"Penumbra reported conflicting replacements for {gamePath}.");
                }
                sources[gamePath] = sourceKey;
                replaced.Add(gamePath);
            }
            if (replaced.Count == 0)
                continue;

            if (isLocalFile)
                files.Add(new McdfExportFile(replaced, localFull!, candidate.Source));
            else
                foreach (var gamePath in replaced)
                    swaps[gamePath] = swapTarget;
        }

        return (new McdfExportContent(
            description, glamourerState, customizeData, manipulationData, files, swaps),
            skipped, null);
    }
}
