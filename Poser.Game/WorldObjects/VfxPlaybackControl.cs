using System;
using Dalamud.Plugin.Services;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Playback writes on an adopted world VFX: speed, intensity, pause/resume,
/// and the loop replay. Every call here is made for a handle the service has
/// already admitted as a live, current VFX incarnation.
/// </summary>
internal sealed class VfxPlaybackControl
{
    /// <summary>How often a looping effect is even CHECKED for having
    /// run out. The loop replays the same instance in place (Brio's
    /// active check + play) — the old recreate-on-interval visibly
    /// blinked the effect off and on.</summary>
    internal static readonly TimeSpan RefreshInterval =
        TimeSpan.FromSeconds(1);

    private readonly IWorldEffectAccess _world;
    private readonly IPluginLog _log;

    public VfxPlaybackControl(IWorldEffectAccess world, IPluginLog log)
    {
        _world = world;
        _log = log;
    }

    public bool TrySetSpeed(AdoptedWorldObject handle, float speed)
    {
        try
        {
            if (_world.TrySetVfxSpeed(handle.Address, speed))
                return true;
            LogRefusal("speed");
            return false;
        }
        catch (Exception ex)
        {
            _log.Warning($"WorldObjectService: VFX speed write failed: {ex.Message}");
            return false;
        }
    }

    public void WriteIntensity(AdoptedWorldObject handle) =>
        _world.SetVfxIntensity(handle.Address, handle.VfxIntensity);

    public bool TrySetPaused(AdoptedWorldObject handle, bool paused)
    {
        try
        {
            if (!_world.TryReadVfxPlayback(
                    handle.Address, out var currentPlayback)
                || currentPlayback == VfxPlaybackState.Unavailable)
            {
                LogRefusal("pause/resume");
                return false;
            }
            if (paused)
            {
                if (!_world.TryPauseVfx(handle.Address))
                {
                    LogRefusal("pause");
                    return false;
                }
                handle.VfxPlayback = VfxPlaybackState.Paused;
                handle.NextVfxRefresh = DateTime.MaxValue;
            }
            else
            {
                if (!_world.TryResumeVfx(handle.Address, handle.VfxSpeed))
                {
                    LogRefusal("resume");
                    return false;
                }
                handle.VfxPlayback = VfxPlaybackState.Playing;
                handle.NextVfxRefresh = DateTime.UtcNow;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"WorldObjectService: VFX playback write failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The loop refresh for one looping, playing, spawned effect:
    /// once per interval, the SAME instance is replayed if it ran out.</summary>
    public void ReplayIfEnded(AdoptedWorldObject handle, DateTime now)
    {
        if (now < handle.NextVfxRefresh)
            return;
        handle.NextVfxRefresh = now + RefreshInterval;
        // Replay the SAME instance only once it actually ran out —
        // no recreate, so nothing blinks.
        if (!_world.IsVfxActive(handle.Address))
        {
            _world.ResumeVfx(handle.Address, handle.VfxSpeed);
            handle.VfxPlayback = VfxPlaybackState.Playing;
        }
    }

    public void LogRefusal(string operation) =>
        _log.Warning($"WorldObjectService: VFX {operation} command refused.");
}
