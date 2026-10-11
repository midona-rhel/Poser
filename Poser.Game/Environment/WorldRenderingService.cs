using Poser.Application.World;
using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Domain;
using Poser.Game.Animation;
using Poser.Application.Events;

namespace Poser.Game.Environment;

/// <summary>
/// Brio's WorldRenderingService. The water freeze is the water renderer's
/// update hooked to return zero; the hook's enabled state IS the freeze, so
/// there is nothing to store and nothing to restore — releasing it hands the
/// surface straight back to the game. The physics freeze is a process-global
/// code patch owned by <see cref="PhysicsFreezePatcher"/>.
/// </summary>
public sealed class WorldRenderingService : IWorldRenderingRuntimePort, IDisposable
{
    private readonly IEventBus _events;
    private readonly Action<GPoseStateChangedEvent> _onGPoseStateChanged;

    private delegate nint UpdateWaterRendererDelegate(nint a1);
    private readonly Hook<UpdateWaterRendererDelegate>? _waterHook;

    // The patcher owns the site, capability state and restore; an unavailable
    // site degrades SetPhysicsFrozen to an explicit failure with its detail.
    private readonly PhysicsFreezePatcher _physics;

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

        _physics = new PhysicsFreezePatcher(sigScanner, log);

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

    public bool IsPhysicsFrozen => _physics.IsFrozen;

    public Outcome SetPhysicsFrozen(bool frozen) => _physics.SetFrozen(frozen);

    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing && ResetWaterOnGPoseExit)
            IsWaterFrozen = false;
    }

    public void Dispose()
    {
        _events.Unsubscribe(_onGPoseStateChanged);
        _waterHook?.Dispose();
        // The animation session releases the scene hold first; the patcher's
        // dispose restores a still-applied patch or reports the failure.
        _physics.Dispose();
        GC.SuppressFinalize(this);
    }
}
