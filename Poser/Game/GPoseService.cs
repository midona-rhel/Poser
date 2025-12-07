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

    [Obsolete("Use IEventBus.Subscribe<GPoseStateChangedEvent> instead")]
    public event Action<bool>? OnGPoseStateChanged;

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

            // Publish via EventBus (preferred)
            _eventBus.Publish(new GPoseStateChangedEvent(currentState));

            // Legacy event for backwards compatibility
#pragma warning disable CS0618
            OnGPoseStateChanged?.Invoke(currentState);
#pragma warning restore CS0618
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        GC.SuppressFinalize(this);
    }
}
