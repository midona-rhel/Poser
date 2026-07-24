using System;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Services;

namespace Poser.Game;

public class GPoseService : IGPoseService
{
    private readonly IClientState _clientState;
    private readonly IFramework _framework;
    private readonly IEventBus _eventBus;

    private bool _lastGPoseState = false;

    public bool IsGPosing => _clientState.IsGPosing;

    public GPoseService(IClientState clientState, IFramework framework, IEventBus eventBus)
    {
        _clientState = clientState;
        _framework = framework;
        _eventBus = eventBus;

        _framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var currentState = _clientState.IsGPosing;

        if (currentState != _lastGPoseState)
        {
            _lastGPoseState = currentState;
            _eventBus.Publish(new GPoseStateChangedEvent(currentState));
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        GC.SuppressFinalize(this);
    }
}
