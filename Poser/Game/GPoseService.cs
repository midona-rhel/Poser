using System;
using Dalamud.Plugin.Services;
using Poser.Services;

namespace Poser.Game;

public class GPoseService : IGPoseService
{
    private readonly IClientState _clientState;
    private readonly IFramework _framework;

    private bool _lastGPoseState = false;

    public bool IsGPosing => _clientState.IsGPosing;

    public event Action<bool>? OnGPoseStateChanged;

    public GPoseService(IClientState clientState, IFramework framework)
    {
        _clientState = clientState;
        _framework = framework;

        _framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var currentState = _clientState.IsGPosing;

        if (currentState != _lastGPoseState)
        {
            _lastGPoseState = currentState;
            OnGPoseStateChanged?.Invoke(currentState);
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        GC.SuppressFinalize(this);
    }
}
