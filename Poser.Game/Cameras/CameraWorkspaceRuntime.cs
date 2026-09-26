using Dalamud.Plugin.Services;
using Poser.Application.Presentation;
using Poser.Application.Scene;

namespace Poser.Game.Cameras;

/// <summary>Framework owner for camera selection-follow and stale native tracking cleanup.</summary>
public sealed class CameraWorkspaceRuntime : IDisposable
{
    private readonly IFramework _framework;
    private readonly SceneSession _scene;
    private readonly CameraTargetControl _targets;
    private readonly CameraSelectionPolicy _selection;
    private readonly IPluginLog _log;

    public CameraWorkspaceRuntime(IFramework framework, SceneSession scene, CameraTargetControl targets,
        CameraSelectionPolicy selection, IPluginLog log)
    {
        _framework = framework;
        _scene = scene;
        _targets = targets;
        _selection = selection;
        _log = log;
        _framework.Update += Tick;
    }

    private void Tick(IFramework _)
    {
        foreach (var camera in _scene.Snapshot.Cameras)
        {
            var result = _targets.Reconcile(camera.Id);
            if (!result.Success && result.Detail is { } detail) _log.Debug("{Detail}", detail);
        }
        _selection.Tick();
    }

    public void Dispose() => _framework.Update -= Tick;
}
