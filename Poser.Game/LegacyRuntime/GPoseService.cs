using System;
using Dalamud.Plugin.Services;
using Poser.Application.Lifecycle;
using Poser.Domain.Operations;
using Poser.Core;
using Poser.Game.Posing;
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
    private readonly Func<IPoseImportLifecycleControl>? _importControl;
    private readonly object _stateGate = new();

    private bool _lastGPoseState = false;
    private bool _sessionActive;
    private bool _unloadExitHandled;
    private bool _closing;

    public bool IsGPosing => _clientState.IsGPosing;

    public GPoseService(
        IClientState clientState,
        IFramework framework,
        IEventBus eventBus,
        IPluginLog log,
        ISessionLifecycleCoordinator lifecycle,
        Func<IPoseImportLifecycleControl>? importControl = null)
    {
        _clientState = clientState;
        _framework = framework;
        _eventBus = eventBus;
        _log = log;
        _lifecycle = lifecycle;
        _importControl = importControl;

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
            if (_closing)
                return;

            var currentState = _clientState.IsGPosing;

            if (currentState == _lastGPoseState)
                return;

            _lastGPoseState = currentState;
            if (currentState)
            {
                var generation = _lifecycle.OnGposeEntered();
                if (!generation.HasValue)
                    return;

                _sessionActive = true;
                _unloadExitHandled = false;
                _eventBus.Publish(new GPoseStateChangedEvent(true));
            }
            else
            {
                if (ProcessExitEdge())
                    _eventBus.Publish(new GPoseStateChangedEvent(false));
            }
        }
    }

    /// <summary>
    /// Host unload entry point. It must run before the service provider starts
    /// disposing graph/session collaborators, and it deliberately shares the
    /// normal edge so unload cannot duplicate the false GPose notification.
    /// </summary>
    public void ExitForUnload()
    {
        if (!_framework.IsInFrameworkUpdateThread)
        {
            _log.Error("Plugin unload GPose exit was not requested on the framework thread.");
            return;
        }

        lock (_stateGate)
        {
            _closing = true;
            if (_unloadExitHandled)
                return;
            if (!_sessionActive && !_clientState.IsGPosing)
                return;

            _unloadExitHandled = true;
            _lastGPoseState = false;
            if (!_sessionActive)
            {
                if (!_lifecycle.OnGposeEntered().HasValue)
                    return;
                _sessionActive = true;
            }

            if (ProcessExitEdge())
                _eventBus.Publish(new GPoseStateChangedEvent(false));
        }
    }

    private bool ProcessExitEdge()
    {
        if (!_sessionActive)
            return false;

        var imports = _importControl?.Invoke();
        if (imports?.IsPending == true)
        {
            var drained = imports.CancelActive("GPose session exited.");
            if (drained.OperationReceipt is not
                { State: OperationReceiptState.Cancelled })
            {
                _log.Error(
                    $"GPose exit pose-import drain failed: {drained.Detail ?? "unknown failure"}");
            }
        }

        var exit = _lifecycle.OnGposeExit();
        _sessionActive = false;
        if (!exit.AlreadyHandled &&
            (exit.Capture.Status == FinalCaptureStatus.Failure ||
             exit.Capture.Persistence == FinalPersistenceStatus.RecoveryRequired))
        {
            _log.Error(
                $"GPose exit final capture failed: {exit.Capture.Detail ?? exit.Capture.PersistenceDetail ?? "unknown failure"}");
        }

        return true;
    }

    public void Dispose()
    {
        lock (_stateGate)
            _closing = true;
        _framework.Update -= OnFrameworkUpdate;
        _lifecycle.InvalidateForUnload();
        GC.SuppressFinalize(this);
    }
}
