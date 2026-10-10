using System;
using System.Collections.Generic;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Poser.Core;
using Poser.Domain.Scene;
using Poser.Services;

namespace Poser.Game.Lighting;

/// <summary>
/// The three GPose camera-light slots, mirrored into the light list. The
/// toggle hook follows the player switching a slot; the poll backfills the
/// lights the game already had when GPose opened. A slot the game empties is
/// delisted, never destroyed — these lights are the game's.
/// </summary>
internal sealed unsafe class GPoseCameraLights : IDisposable
{
    private const string ToggleGPoseLightSignature =
        "48 83 EC 28 4C 8B C1 83 FA 03";

    private static readonly TimeSpan GPosePollInterval = TimeSpan.FromSeconds(1);

    private delegate bool ToggleGPoseLightDelegate(GPoseLightController* state, uint index);

    private readonly IPluginLog _log;
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
        _log = log;
        _gPose = gPose;
        _events = events;
        _lights = lights;
        _world = world;
        _disposed = disposed;

        var toggleAddress = LightingService.TryScan(
            sigScanner, log, "GPose light toggle", ToggleGPoseLightSignature);
        if (toggleAddress is { } toggle)
        {
            try
            {
                _toggleGPoseLightHook =
                    hooks.HookFromAddress<ToggleGPoseLightDelegate>(
                        toggle, ToggleGPoseLightDetour);
                _toggleGPoseLightHook.Enable();
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"LightingService: could not hook the GPose light toggle, camera lights will not be tracked: {ex.Message}");
            }
        }
    }

    private static GPoseLightController* GetGPoseController()
    {
        var framework = EventFramework.Instance();
        if (framework == null)
            return null;
        return (GPoseLightController*)
            &framework->EventSceneModule.EventGPoseController;
    }

    private bool ToggleGPoseLightDetour(GPoseLightController* state, uint index)
    {
        var result = _toggleGPoseLightHook!.Original(state, index);
        try
        {
            if (!_disposed() && _gPose.IsGPosing)
                RefreshGPoseLights();
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to track a GPose light toggle: {ex}");
        }
        return result;
    }

    /// <summary>Reconciles the three camera-light slots against the list. A
    /// slot the game emptied is delisted, never destroyed.</summary>
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

            // The game constructed it, so it is sitting in the overworld set;
            // a camera light is not a capture candidate.
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

    /// <summary>Backfill, Ktisis' RefreshLightEntities: the toggle hook covers
    /// the player toggling a camera light, but not the lights the game already
    /// had when GPose opened.</summary>
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

    /// <summary>A GPose edge restarts the backfill poll immediately.</summary>
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
