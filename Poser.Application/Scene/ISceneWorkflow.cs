using Poser.Domain;
using Poser.Domain.Operations;

namespace Poser.Application.Scene;

/// <summary>The whole-scene save and load transaction as a surface drives it.</summary>
public interface ISceneWorkflow
{
    long EstimatedAppearanceBytes { get; }
    SceneProgress? Progress { get; }
    OperationReceipt? Receipt { get; }
    bool Busy { get; }
    void Cancel();
    Outcome BeginSave(string path, string? description = null, SceneSaveOptions? options = null);
    Outcome BeginLoad(string path, SceneLoadOptions? options = null);
}
