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

    private readonly IVfxObjectPort _port;
    private readonly IPluginLog _log;

    public VfxPlaybackControl(IVfxObjectPort port, IPluginLog log)
    {
        _port = port;
        _log = log;
    }

    public bool TrySetSpeed(AdoptedWorldObject handle, float speed)
    {
        try
        {
            if (_port.TrySetVfxSpeed(handle.Address, speed))
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
        _port.SetVfxIntensity(handle.Address, handle.VfxIntensity);

    public bool TrySetPaused(AdoptedWorldObject handle, bool paused)
    {
        try
        {
            if (!_port.TryReadVfxPlayback(
                    handle.Address, out var currentPlayback)
                || currentPlayback == VfxPlaybackState.Unavailable)
            {
                LogRefusal("pause/resume");
                return false;
            }
            if (paused)
            {
                if (!_port.TryPauseVfx(handle.Address))
                {
                    LogRefusal("pause");
                    return false;
                }
                handle.VfxPlayback = VfxPlaybackState.Paused;
                handle.NextVfxRefresh = DateTime.MaxValue;
            }
            else
            {
                if (!_port.TryResumeVfx(handle.Address, handle.VfxSpeed))
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
        if (!_port.IsVfxActive(handle.Address))
        {
            _port.ResumeVfx(handle.Address, handle.VfxSpeed);
            handle.VfxPlayback = VfxPlaybackState.Playing;
        }
    }

    public void LogRefusal(string operation) =>
        _log.Warning($"WorldObjectService: VFX {operation} command refused.");
}
