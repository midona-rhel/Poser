using Poser.Application.Transforms;
using Poser.Domain.Transforms;

namespace Poser.Application.Scene;

/// <summary>The most recent operation represented by a scene-load history
/// step. Redo creates new entities, so undo must use the new operation.</summary>
internal sealed class SceneLoadReplay(SceneOperation current)
{
    public SceneOperation Current = current;
    public IReadOnlyDictionary<(string Kind, Guid Key), SceneEntityHandle> Entities = current.HistoryEntities;
    public IReadOnlyDictionary<Guid, Guid> Groups = current.HistoryGroups;
}

/// <summary>
/// A committed load is one history step even when individual entities report
/// failures. Undo removes entities and relationships created by that load and
/// restores baselines it replaced. Redo loads the file additively and never
/// clears the session again. A clear performed before the original load is not
/// recorded and cannot be undone by this step.
/// </summary>
internal sealed class SceneLoadHistory(
    EditHistory history, ISceneStatePort sceneState, ISceneHistoryPort historyPort,
    SceneLoadRollback rollback,
    SceneWorkflow workflow)
{
    public void Append(SceneOperation operation, string path, SceneLoadOptions options)
    {
        if (operation.Replay is not null)
            return;
        // Redo creates new native entities. Keep the inverse attached to that
        // new operation rather than the first load's emptied rollback lists.
        var load = new SceneLoadReplay(operation);
        var replay = options with { ClearExistingScene = false };
        history.Append(new JournalStep(
            $"Load {operation.FileName}",
            () => Undo(load),
            () => workflow.BeginReplay(path, replay, load).Success)
        {
            RequiredAsset = path,
            // Busy is a scene operation still running, not a dead step:
            // pressing undo twice during a save must not discard the load.
            OnRefusal = () => workflow.Busy ? RefusalAction.Keep : RefusalAction.DropOnRepeat,
            CompleteReplay = (undo, _, _, completed) =>
            {
                if (undo)
                    completed(GestureResult.Ok());
                else
                    _ = CompleteRedo(load.Current, workflow.Drain, completed);
            },
        });
    }

    /// <summary>Reports a redo's outcome once the replayed load is
    /// terminal, on the framework thread history is confined to.</summary>
    private async Task CompleteRedo(
        SceneOperation operation, Task running, Action<GestureResult> completed)
    {
        // Never complete inside the redo call itself: history reads a
        // synchronous completion as the redo's own answer.
        await Task.Yield();
        try
        {
            await running;
        }
        catch (Exception)
        {
            // The terminal is published before the task ends either way.
        }
        var result = operation.Committed
            ? GestureResult.Ok()
            : GestureResult.Fail(
                operation.TerminalDetail ?? $"Loading {operation.FileName} again did not complete.");
        try
        {
            await sceneState.OnFramework(() =>
            {
                completed(result);
                return true;
            });
        }
        catch (Exception)
        {
            // The framework is gone (unload); history goes with it.
        }
    }

    private bool Undo(SceneLoadReplay load)
    {
        if (workflow.Disposed || workflow.Busy) return false;
        // Register before removal publishes missing bindings. Both transform
        // patches and group snapshots follow the same replacement on redo.
        foreach (var (key, token) in load.Entities)
            if (historyPort.ResolveHistoryEntity(token) is { } entity)
                history.RetainLifecycleEntity(entity, () =>
                    load.Entities.TryGetValue(key, out var current) ? historyPort.ResolveHistoryEntity(current) : null);
        return rollback.Run(load.Current) is null;
    }
}
