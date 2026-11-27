using Dalamud.Plugin.Services;
using Poser.Application.AutoSave;
using Poser.Application.Lifecycle;

namespace Poser.Game.AutoSave;

/// <summary>Calls the scene autosave's Tick every game frame with whether
/// GPose is open. The autosave decides whether to write a file.</summary>
public sealed class AutoSaveTick : IDisposable
{
    private readonly IFramework _framework;
    private readonly IGPoseService _gpose;
    private SceneAutoSaveService? _scenes;

    public AutoSaveTick(IFramework framework, IGPoseService gpose)
    {
        _framework = framework;
        _gpose = gpose;
    }

    public void StartSceneSnapshots(SceneAutoSaveService scenes)
    {
        if (_scenes is not null)
            throw new InvalidOperationException("Scene autosave is already started.");
        _scenes = scenes;
        _framework.Update += TickScenes;
    }

    private void TickScenes(IFramework _) => _scenes?.Tick(DateTime.UtcNow, _gpose.IsGPosing);

    public void Dispose()
    {
        _framework.Update -= TickScenes;
    }
}
