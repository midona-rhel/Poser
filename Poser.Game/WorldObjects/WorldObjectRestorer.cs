using System;
using Dalamud.Plugin.Services;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Ends one claim natively: a spawned object is destroyed, a borrowed one gets
/// the placement, flags and dressing captured at adoption written back. A
/// claim whose native incarnation is gone is dropped without any write.
/// </summary>
internal sealed class WorldObjectRestorer
{
    private readonly IWorldObjectPort _port;
    private readonly VfxLifecycleOwner _vfx;
    private readonly WorldObjectRespawner _respawner;
    private readonly IPluginLog _log;
    private readonly Func<AdoptedWorldObject, bool> _isCurrent;

    public WorldObjectRestorer(
        IWorldObjectPort port,
        VfxLifecycleOwner vfx,
        WorldObjectRespawner respawner,
        IPluginLog log,
        Func<AdoptedWorldObject, bool> isCurrent)
    {
        _port = port;
        _vfx = vfx;
        _respawner = respawner;
        _log = log;
        _isCurrent = isCurrent;
    }

    /// <summary>The one place a captured pair is written back. An address that
    /// has stopped being a BG object is left alone: restoring onto whatever
    /// took its place is the single way this contract could do harm.</summary>
    public bool Restore(AdoptedWorldObject handle)
    {
        // A SPAWNED object has nothing to restore: it is Poser's own, and
        // its end is destruction.
        if (handle.Spawned)
        {
            if (!_isCurrent(handle))
            {
                if (handle.IsVfx
                    && !_port.TryReleaseVfxClaim(handle.Identity))
                {
                    _log.Warning(
                        $"WorldObjectService: releasing stale VFX {handle.Address:X} claim remains outstanding.");
                    return false;
                }
                handle.MarkReleased(handle.InitialPlacement);
                return true;
            }
            if (!_respawner.TryDestroyIncarnation(handle.Identity))
            {
                _log.Warning(
                    $"WorldObjectService: destroying spawned {handle.Address:X} remains pending.");
                return false;
            }
            handle.MarkReleased(handle.InitialPlacement);
            return true;
        }
        if (!_isCurrent(handle))
        {
            // The address was replaced by the game. Refuse all native writes,
            // then drop our stale claim without touching the replacement.
            handle.MarkReleased(handle.InitialPlacement);
            return true;
        }
        try
        {
            _port.Write(handle.Address, handle.InitialPlacement);
            _port.WriteFlags(handle.Address, handle.InitialFlags);
            if (handle.InitialNightState is { } dressing)
                _port.WriteBgNightState(handle.Address, dressing);
            if (handle.AnimationPaused)
                _port.WriteBgAnimationSpeed(handle.Address, 1f);
            if (handle.InitialVfxSnapshot is { } snapshot
                && !_vfx.Restore(handle.Identity, snapshot))
                return false;
            // Written BESIDE the flags rather than left to them: whether
            // the drawn bit lives inside that byte is the game's business.
            // VFX visibility is Color.W/alpha, which the snapshot restore
            // already put back exactly; WriteVisible would quantize a
            // fractional alpha to 0 or 1. BG keeps the legacy flag write.
            if (handle.InitialVfxSnapshot is null)
            {
                _port.WriteOpacity(handle.Address, handle.InitialOpacity);
                _port.WriteVisible(handle.Address, handle.InitialVisible);
            }
            handle.MarkReleased(handle.InitialPlacement);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"WorldObjectService: restoring a world object failed: {ex.Message}");
            return false;
        }
    }
}
