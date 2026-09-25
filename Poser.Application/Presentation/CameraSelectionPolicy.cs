using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;

namespace Poser.Application.Presentation;

/// <summary>Look-through-on-selection policy; no panel must be drawn to advance it.</summary>
public sealed class CameraSelectionPolicy(
    SelectionSession selection, ConfigurationService configuration, ICameraControl cameras)
{
    private SelectionId? _applied;

    public void Tick()
    {
        var primary = selection.Primary;
        if (primary is not { Kind: SceneEntityKind.Camera, Camera: { } camera })
        {
            _applied = null;
            return;
        }
        if (_applied == primary || !configuration.Config.Camera.LookThroughSelectedCamera) return;
        _applied = primary;
        if (cameras.Read(camera) is { IsLive: false }) cameras.SetLive(camera, true);
    }
}

