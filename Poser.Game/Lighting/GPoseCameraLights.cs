using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Poser.Domain.Scene;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Game.Core;

namespace Poser.Game.Lighting;

/// <summary>
/// Lists the game's three GPose camera lights in Poser's light list. A hook on
/// the game's light switch notices when the player turns one on or off; a poll
/// once a second finds the lights that were already on when GPose opened.
/// </summary>
internal sealed unsafe class GPoseCameraLights : IDisposable
{
    private const string ToggleGPoseLightSignature =
        Poser.Game.Natives.NativeSignatures.EventGPoseController_ToggleCameraLight;

    private static readonly TimeSpan GPosePollInterval = TimeSpan.FromSeconds(1);

    private delegate bool ToggleGPoseLightDelegate(GPoseLightController* state, uint index);

    private readonly IGPoseService _gPose;
    private readonly IEventBus _events;
    private readonly List<Light> _lights;
    private readonly WorldLightCapture _world;
    private readonly Func<bool> _disposed;
    private readonly Hook<ToggleGPoseLightDelegate>? _toggleGPoseLightHook;
    private DateTime _nextGPosePollUtc = DateTime.MinValue;

    /// <param name="lights">The light service's own list; camera lights are
    /// added to and delisted from it here.</param>
    public GPoseCameraLights(
        ISigScanner sigScanner,
        IGameInteropProvider hooks,
        IPluginLog log,
        IGPoseService gPose,
        IEventBus events,
        List<Light> lights,
        WorldLightCapture world,
        Func<bool> disposed)
    {
        _gPose = gPose;
        _events = events;
        _lights = lights;
        _world = world;
        _disposed = disposed;

        // Without the hook, the poll below still finds a toggled light within
        // a second.
        _toggleGPoseLightHook = LightingService.TryHook<ToggleGPoseLightDelegate>(hooks, log,
            LightingService.TryScan(sigScanner, log, "GPose light toggle", ToggleGPoseLightSignature),
            ToggleGPoseLightDetour, "could not hook the GPose light toggle");
    }

    private static GPoseLightController* GetGPoseController() =>
        (GPoseLightController*)GPoseObjectTable.ControllerAddress();

    private bool ToggleGPoseLightDetour(GPoseLightController* state, uint index)
    {
        var result = _toggleGPoseLightHook!.Original(state, index);
        if (!_disposed() && _gPose.IsGPosing)
            RefreshGPoseLights();
        return result;
    }

    /// <summary>Brings the light list in line with the three camera-light
    /// slots. A slot the game emptied is taken off the list; the light is the
    /// game's, so Poser never frees it.</summary>
    public void RefreshGPoseLights()
    {
        var controller = GetGPoseController();
        if (controller == null)
            return;

        var changed = false;
        for (var slot = 0u; slot < GPoseLightController.LightCount; slot++)
        {
            var native = controller->GetLight(slot);
            var tracked = FindGPoseLight((int)slot);

            if (native == null)
            {
                if (tracked == null)
                    continue;
                _lights.Remove(tracked);
                tracked.Invalidate();
                changed = true;
                continue;
            }

            if (tracked != null)
            {
                if (tracked.NativePtr == native)
                    continue;
                _lights.Remove(tracked);
                tracked.Invalidate();
            }

            // The constructor hook listed it as a world light; a camera light
            // can't be taken over.
            _world.Forget((nint)native);

            _lights.Add(new Light(
                native, $"Camera Light {slot + 1}", LightOwnership.GPose)
            {
                GPoseSlot = (int)slot,
            });
            changed = true;
        }

        if (changed)
            _events.Publish(new LightListChangedEvent());
    }

    /// <summary>Finds camera lights the toggle hook didn't see, such as the
    /// ones already on when GPose opened. Ktisis polls the same way.</summary>
    public void PollGPoseLights()
    {
        if (!_gPose.IsGPosing)
            return;
        var now = DateTime.UtcNow;
        if (now < _nextGPosePollUtc)
            return;
        _nextGPosePollUtc = now + GPosePollInterval;
        RefreshGPoseLights();
    }

    /// <summary>Makes the next frame poll, used when GPose starts or ends.</summary>
    public void ResetPoll() => _nextGPosePollUtc = DateTime.MinValue;

    private Light? FindGPoseLight(int slot)
    {
        foreach (var light in _lights)
        {
            if (light.Ownership == LightOwnership.GPose && light.GPoseSlot == slot)
                return light;
        }
        return null;
    }

    public void Dispose() => _toggleGPoseLightHook?.Dispose();
}
