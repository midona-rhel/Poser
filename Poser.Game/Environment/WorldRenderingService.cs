using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Domain;
using Poser.Game.Animation;
using Poser.Application.Events;

namespace Poser.Game.Environment;

/// <summary>
/// Freezes the water and the physics of hair and clothes. The water freeze is
/// a hook that skips the game's water update: while the hook is on the water
/// stands still, and turning it off gives the water back to the game, so there
/// is nothing to save or restore. The physics freeze is a change to the game's
/// code that affects every actor at once; <see cref="PhysicsFreezePatcher"/>
/// makes and undoes it.
/// </summary>
public sealed class WorldRenderingService : IDisposable
{
    private readonly IEventBus _events;
    private readonly Action<GPoseStateChangedEvent> _onGPoseStateChanged;

    private delegate nint UpdateWaterRendererDelegate(nint a1);
    private readonly Hook<UpdateWaterRendererDelegate>? _waterHook;

    // When the patcher can't find the game code to change, SetPhysicsFrozen
    // returns a failure that says so.
    private readonly PhysicsFreezePatcher _physics;

    public bool ResetWaterOnGPoseExit { get; set; } = true;

    /// <summary>False when the water hook was not found; the water can't be frozen.</summary>
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
            var address = sigScanner.ScanText(Poser.Game.Natives.NativeSignatures.WaterRenderer_Update);
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

    /// <summary>Only the animation session calls this, and it remembers whether
    /// the scene asked for the freeze.</summary>
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
        // Undoes the physics freeze if it is still on.
        _physics.Dispose();
    }
}
