using Dalamud.Plugin.Services;
using Poser.Application.Scene;

namespace Poser.Game.Scene;

/// <summary>The one frame owner for pending creation; no window needs to be open.</summary>
public sealed class SceneCreationRuntime : IDisposable
{
    private readonly IFramework _framework;
    private readonly PendingSceneCreation _pending;
    private readonly SceneDuplication _duplication;

    public SceneCreationRuntime(IFramework framework, PendingSceneCreation pending, SceneDuplication duplication)
    {
        _framework = framework;
        _pending = pending;
        _duplication = duplication;
        _framework.Update += Tick;
    }

    private void Tick(IFramework _)
    {
        _pending.Tick();
        _duplication.Tick();
    }

    public void Dispose() => _framework.Update -= Tick;
}
