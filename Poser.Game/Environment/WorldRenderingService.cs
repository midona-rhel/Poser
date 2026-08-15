using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Services;

namespace Poser.Game.Environment;

/// <summary>
/// Brio's WorldRenderingService. The water freeze is the water renderer's
/// update hooked to return zero; the hook's enabled state IS the freeze, so
/// there is nothing to store and nothing to restore — releasing it hands the
/// surface straight back to the game.
/// </summary>
public sealed class WorldRenderingService : IWorldRenderingService, IDisposable
{
    private readonly IEventBus _events;
    private readonly Action<GPoseStateChangedEvent> _onGPoseStateChanged;

    private delegate nint UpdateWaterRendererDelegate(nint a1);
    private readonly Hook<UpdateWaterRendererDelegate>? _waterHook;

    public bool ResetWaterOnGPoseExit { get; set; } = true;

    public bool IsWaterFreezeAvailable => _waterHook != null;

    public WorldRenderingService(
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IPluginLog log,
        IEventBus events)
    {
        _events = events;

        try
        {
            var address = sigScanner.ScanText("48 8B C4 48 89 58 ?? 57 48 81 EC ?? ?? ?? ?? 0F B6 B9");
            _waterHook = hooking.HookFromAddress<UpdateWaterRendererDelegate>(
                address, UpdateWaterRendererDetour);
        }
        catch (Exception ex)
        {
            log.Warning($"World rendering: water freeze signature not found ({ex.Message}); the freeze is unavailable.");
        }

        _onGPoseStateChanged = OnGPoseStateChanged;
        _events.Subscribe(_onGPoseStateChanged);
    }

    public bool IsWaterFrozen
    {
        get => _waterHook?.IsEnabled == true;
        set
        {
            if (_waterHook == null || value == IsWaterFrozen)
                return;
            if (value)
                _waterHook.Enable();
            else
                _waterHook.Disable();
        }
    }

    private nint UpdateWaterRendererDetour(nint a1) => 0;

    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing && ResetWaterOnGPoseExit)
            IsWaterFrozen = false;
    }

    public void Dispose()
    {
        _events.Unsubscribe(_onGPoseStateChanged);
        _waterHook?.Dispose();
        GC.SuppressFinalize(this);
    }
}
