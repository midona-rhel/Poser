namespace Poser.Application.Scene;

/// <summary>Outward notifications; neither library indexing nor logging owns the transaction.</summary>
public interface ISceneWorkflowObserver
{
    void Saved();
    void Completed(Guid operationId, SceneProgress progress);
}
