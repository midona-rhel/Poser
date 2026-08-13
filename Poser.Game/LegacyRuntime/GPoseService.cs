using System;
using Dalamud.Plugin.Services;
using Poser.Application.Lifecycle;
using Poser.Core;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Observes GPose edges from <see cref="IFramework.Update"/> only. The
/// framework's <see cref="IFramework.IsInFrameworkUpdateThread"/> contract is
/// enforced before the synchronous capture-before-publish boundary.
/// </summary>
public class GPoseService : IGPoseService
{
    private readonly IClientState _clientState;
    private readonly IFramework _framework;
    private readonly IEventBus _eventBus;
    private readonly IPluginLog _log;
    private readonly ISessionLifecycleCoordinator _lifecycle;
    private readonly object _stateGate = new();

    private bool _lastGPoseState = false;

    public bool IsGPosing => _clientState.IsGPosing;

    public GPoseService(
        IClientState clientState,
        IFramework framework,
        IEventBus eventBus,
        IPluginLog log,
        ISessionLifecycleCoordinator lifecycle)
    {
        _clientState = clientState;
        _framework = framework;
        _eventBus = eventBus;
        _log = log;
        _lifecycle = lifecycle;

        _framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        // The capture reads live scene state synchronously. Do not schedule or
        // run it from another thread; the framework contract is the affinity
        // boundary for this edge and its legacy event publication.
        if (!framework.IsInFrameworkUpdateThread)
        {
            _log.Error(
                "GPose state observation skipped: callback was not on the framework update thread.");
            return;
        }

        lock (_stateGate)
        {
            var currentState = _clientState.IsGPosing;

            if (currentState == _lastGPoseState)
                return;

            _lastGPoseState = currentState;
            if (currentState)
            {
                _lifecycle.OnGposeEntered();
            }
            else
            {
                var exit = _lifecycle.OnGposeExit();
                if (!exit.AlreadyHandled &&
                    exit.Capture.Status == FinalCaptureStatus.Failure)
                {
                    _log.Error(
                        $"GPose exit final capture failed: {exit.Capture.Detail ?? "unknown failure"}");
                }
            }

            _eventBus.Publish(new GPoseStateChangedEvent(currentState));
        }
    }

    public void Dispose()
    {
        _framework.Update -= OnFrameworkUpdate;
        GC.SuppressFinalize(this);
    }
}
