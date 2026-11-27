using Dalamud.Plugin.Services;
using Poser.Application.Scene;
using Poser.Application.World;

namespace Poser.Game.Scene;

/// <summary>Each frame, moves on whatever is waiting for the game: adding an
/// entity, copying one, and taking over a world object. It runs whether or not
/// any Poser window is open.</summary>
public sealed class SceneCreationTick : IDisposable
{
    private readonly IFramework _framework;
    private readonly PendingSceneCreation _pending;
    private readonly SceneDuplication _duplication;
    private readonly WorldAcquisitionControl _acquisition;

    public SceneCreationTick(IFramework framework, PendingSceneCreation pending, SceneDuplication duplication, WorldAcquisitionControl acquisition)
    {
        _framework = framework;
        _pending = pending;
        _duplication = duplication;
        _acquisition = acquisition;
        _framework.Update += Tick;
    }

    private void Tick(IFramework _)
    {
        _pending.Tick();
        _duplication.Tick();
        _acquisition.Tick();
    }

    public void Dispose() => _framework.Update -= Tick;
}
