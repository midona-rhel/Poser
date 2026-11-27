using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Domain.Operations;
using Poser.Domain.Scene;
using Poser.Application.Library;

namespace Poser.Game.Scene;

/// <summary>What happens around a scene save or load: the Library is scanned
/// again after a save, and every result is written to the log, one line per
/// entity.</summary>
internal sealed class SceneWorkflowObserver(IPluginLog log, IPoseLibraryService library)
{
    public void Saved() => library.RequestScan();

    public void Completed(Guid operationId, SceneProgress progress)
    {
        var outcome = progress.Outcome!;
        var kind = progress.Kind;
        var state = outcome.State;
        var detail = outcome.Detail;
        var entities = outcome.Entities;
        var notes = outcome.Notes;
        var evidence = outcome.RecoveryEvidencePaths;
        string prefix = $"Scene {kind} {operationId:D}";
        string terminal =
            $"{prefix}: {state}: {progress.FileName}: {detail}";
        if (state == OperationReceiptState.Applied)
            log.Information(terminal);
        // A scene that loaded with some parts left out (objects from another map,
        // a missing file) is still kept, so it is a warning, not an error. Each
        // part left out is logged below.
        else if (state is OperationReceiptState.Cancelled
                 or OperationReceiptState.RolledBack
                 || entities.Any(entity => entity.Restored))
            log.Warning(terminal);
        else
            log.Error(terminal);

        foreach (var entity in entities)
        {
            string line = $"{prefix}: {entity.Kind.Label()} '{entity.Name}': " +
                (entity.Restored ? "restored" : "refused");
            if (!string.IsNullOrWhiteSpace(entity.Detail))
                line += $": {entity.Detail}";
            if (!entity.Restored && !string.IsNullOrWhiteSpace(entity.Remedy))
                line += $" Next: {entity.Remedy}";
            if (entity.Degraded)
                log.Information(line);
            else if (entity.Restored)
                log.Debug(line);
            else
                log.Warning(line);
        }

        foreach (var note in notes)
            log.Information($"{prefix}: {note}");
        foreach (var path in evidence)
            log.Warning($"{prefix}: recovery file: {path}");
    }
}
