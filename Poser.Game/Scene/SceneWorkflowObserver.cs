using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Domain.Operations;
using Poser.Library;

namespace Poser.Game.Scene;

internal sealed class SceneWorkflowObserver(IPluginLog log, IPoseLibraryService library)
    : ISceneWorkflowObserver
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
        else if (state is OperationReceiptState.Cancelled
                 or OperationReceiptState.RolledBack)
            log.Warning(terminal);
        else
            log.Error(terminal);

        foreach (var entity in entities)
        {
            string line = $"{prefix}: {entity.Kind} '{entity.Name}': " +
                (entity.Restored ? "restored" : "refused");
            if (!string.IsNullOrWhiteSpace(entity.Detail))
                line += $": {entity.Detail}";
            if (!entity.Restored && !string.IsNullOrWhiteSpace(entity.Remedy))
                line += $" Next: {entity.Remedy}";
            if (entity.Restored)
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
