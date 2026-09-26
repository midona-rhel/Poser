using Dalamud.Plugin.Services;
using Poser.Application.AutoSave;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.AutoSave;

/// <summary>Owns framework subscriptions, preserving their host startup order.</summary>
public sealed class AutoSaveRuntime : IDisposable
{
    private readonly IFramework _framework;
    private readonly IGPoseService _gpose;
    private readonly AutoSaveService _poses;
    private SceneAutoSaveService? _scenes;

    public AutoSaveRuntime(IFramework framework, IGPoseService gpose, AutoSaveService poses)
    {
        _framework = framework;
        _gpose = gpose;
        _poses = poses;
        _framework.Update += TickPoses;
    }

    public void StartSceneSnapshots(SceneAutoSaveService scenes)
    {
        if (_scenes is not null)
            throw new InvalidOperationException("Scene autosave is already started.");
        _scenes = scenes;
        _framework.Update += TickScenes;
    }

    private void TickPoses(IFramework _) => _poses.Tick(DateTime.UtcNow, _gpose.IsGPosing);
    private void TickScenes(IFramework _) => _scenes?.Tick(DateTime.UtcNow, _gpose.IsGPosing);

    public void Dispose()
    {
        _framework.Update -= TickPoses;
        _framework.Update -= TickScenes;
    }
}
