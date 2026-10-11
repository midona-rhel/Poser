using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Domain;
using Poser.Application.Events;
using Poser.Game.Core;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Replaces a spawned world object's native body without changing its scene
/// identity, and owns every native incarnation such a replacement leaves
/// behind until it is torn down. The fresh body stays hidden until it is
/// loaded and carries the handle's settings; only then is the old one
/// destroyed and the handle re-pointed.
/// </summary>
internal sealed class WorldObjectRespawner
{
    private readonly IWorldObjectPort _port;
    private readonly IEventBus _events;
    private readonly IPluginLog _log;
    private readonly Func<AdoptedWorldObject, bool> _isCurrent;
    private readonly Action<AdoptedWorldObject> _dropPendingStain;
    private readonly List<WorldObjectIncarnation> _pendingTeardowns = new();
    private readonly Dictionary<AdoptedWorldObject, PendingRespawn> _respawns = new();

    private sealed record PendingRespawn(
        WorldObjectIncarnation Fresh, string Path, DateTime Deadline,
        TaskCompletionSource<Outcome> Completion);

    public WorldObjectRespawner(
        IWorldObjectPort port,
        IEventBus events,
        IPluginLog log,
        Func<AdoptedWorldObject, bool> isCurrent,
        Action<AdoptedWorldObject> dropPendingStain)
    {
        _port = port;
        _events = events;
        _log = log;
        _isCurrent = isCurrent;
        _dropPendingStain = dropPendingStain;
    }

    public bool HasPendingTeardowns => _pendingTeardowns.Count > 0;

    /// <summary>Starts a replacement for an admitted spawned handle. The
    /// caller has already checked that the service and the handle accept it.</summary>
    public Task<Outcome> Respawn(AdoptedWorldObject handle, string path)
    {
        static Task<Outcome> Refused(string detail) =>
            Task.FromResult(new Outcome(false, detail));
        if (_respawns.ContainsKey(handle))
            return Refused("This object already has a replacement loading.");
        if (string.IsNullOrWhiteSpace(path))
            return Refused("The path names nothing.");
        path = path.Trim();
        var placement = handle.Transform;
        var fresh = _port.Spawn(path, placement, out var allocated);
        if (fresh == nint.Zero)
        {
            if (allocated.Address != nint.Zero) TryCleanupFresh(allocated);
            return Refused($"'{WorldObjectService.DisplayName(path)}' could not be spawned — the game did not take it.");
        }
        if (!_port.TryReadIncarnation(fresh, out var freshIdentity)
            || !allocated.SameAllocation(freshIdentity)
            || (NativeWorldObjectPort.IsVfxPath(path.Trim())
                && (!freshIdentity.IsVfx
                    || freshIdentity.ResourceIdentity == nint.Zero)))
        {
            bool cleaned = allocated.Address != nint.Zero
                ? TryCleanupFresh(allocated)
                : TryCleanupUnidentified(path, fresh);
            return Refused(cleaned
                ? "The new native incarnation could not be identified."
                : "Respawn cleanup remains outstanding.");
        }
        var pending = new PendingRespawn(freshIdentity, path,
            DateTime.UtcNow.AddSeconds(15),
            new(TaskCreationOptions.RunContinuationsAsynchronously));
        _respawns.Add(handle, pending);
        try
        {
            // A streaming replacement is not a second scene object. Keep it
            // hidden until the renderer and its authored settings are ready.
            _port.WriteVisible(fresh, false);
            Advance(handle, pending, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Finish(handle, pending, false, $"The replacement failed: {ex.Message}");
        }
        return pending.Completion.Task;
    }

    public void Pump(DateTime now)
    {
        foreach (var (handle, pending) in new Dictionary<AdoptedWorldObject, PendingRespawn>(_respawns))
        {
            try { Advance(handle, pending, now); }
            catch (Exception ex)
            {
                Finish(handle, pending, false, $"The replacement failed: {ex.Message}");
            }
        }
    }

    /// <summary>Cancels the handle's replacement, if one is loading.</summary>
    public void Cancel(AdoptedWorldObject handle, string detail)
    {
        if (_respawns.TryGetValue(handle, out var pending))
            Finish(handle, pending, false, detail);
    }

    public void CancelAll(string detail)
    {
        foreach (var (handle, pending) in new Dictionary<AdoptedWorldObject, PendingRespawn>(_respawns))
            Finish(handle, pending, false, detail);
    }

    private void Advance(AdoptedWorldObject handle, PendingRespawn pending, DateTime now)
    {
        var freshIdentity = pending.Fresh;
        if (!_isCurrent(handle)
            || !_port.TryReadIncarnation(handle.Address, out var oldIdentity)
            || !oldIdentity.SameAllocation(handle.Identity)
            || !_port.TryReadIncarnation(freshIdentity.Address, out var currentFresh)
            || !currentFresh.SameAllocation(freshIdentity))
        {
            Finish(handle, pending, false, "A native incarnation changed while replacing the object.");
            return;
        }
        if (now >= pending.Deadline)
        {
            Finish(handle, pending, false, "The replacement did not finish loading; the original was kept.");
            return;
        }
        if (!freshIdentity.IsVfx)
        {
            if (!_port.IsBgReady(freshIdentity.Address)) return;
            if (pending.Path.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase))
            {
                if (!_port.WriteFurnitureColor(freshIdentity.Address, handle.Stain, handle.Tint)) return;
                if (pending.Path == handle.Path)
                    _port.WriteFurnitureLights(freshIdentity.Address, handle.FurnitureLights);
            }
            // Undyeable models have no stain buffer and must not wait for one.
            if (handle.Tint is not null && _port.CanDyeBg(freshIdentity.Address) != false
                && !_port.WriteBgTint(freshIdentity.Address, handle.Tint)) return;
            _port.WriteBgNightState(freshIdentity.Address, handle.NightState);
            // Raw spawned scenery deliberately has no animation data (see
            // NativeWorldObjectPort.Spawn); there is no animation load to await.
        }
        bool visible = handle.Visible;
        _port.Write(freshIdentity.Address, handle.Transform);
        if (!Prepare(handle, freshIdentity, visible, out var detail))
        {
            Finish(handle, pending, false, detail);
            return;
        }
        bool oldDestroyed = TryDestroyIncarnation(handle.Identity);
        if (!oldDestroyed)
        {
            Finish(handle, pending, false, "The old native incarnation could not be torn down.");
            return;
        }
        handle.Address = freshIdentity.Address;
        handle.Identity = freshIdentity;
        handle.Path = pending.Path;
        handle._isVfx = freshIdentity.IsVfx;
        _dropPendingStain(handle);
        handle.NightStatePending = false;
        handle.AnimationPauseRetries = 0;
        handle.AnimationAnchor = new SceneryAnimationState.Watching();
        handle.VfxPlayback = handle.IsVfx
            ? handle.VfxPaused ? VfxPlaybackState.Paused : VfxPlaybackState.Playing
            : VfxPlaybackState.Unavailable;
        if (handle.IsVfx)
        {
            if (handle.VfxPaused)
            {
                handle.NextVfxRefresh = DateTime.MaxValue;
            }
            else
            {
                handle.NextVfxRefresh = DateTime.UtcNow + VfxPlaybackControl.RefreshInterval;
            }
        }
        Finish(handle, pending, true, null);
        _events.Publish(new WorldObjectListChangedEvent());
    }

    private void Finish(AdoptedWorldObject handle, PendingRespawn pending,
        bool succeeded, string? detail)
    {
        // A post-commit subscriber failure must not roll back an already
        // published native replacement or destroy the handle's new body.
        if (!_respawns.TryGetValue(handle, out var active) || active != pending) return;
        _respawns.Remove(handle);
        if (!succeeded && !TryCleanupFresh(pending.Fresh))
            detail += " Replacement cleanup remains outstanding.";
        pending.Completion.TrySetResult(new(succeeded, detail));
    }

    private bool Prepare(AdoptedWorldObject handle,
        WorldObjectIncarnation fresh, bool visible, out string? detail)
    {
        detail = null;
        try
        {
            // Every native property write precedes old-object teardown.
            _port.WriteVisible(fresh.Address, visible);
            if (fresh.IsVfx)
            {
                if (Math.Abs(handle.VfxSpeed - 1f) > 0.001f
                    && !_port.TrySetVfxSpeed(fresh.Address, handle.VfxSpeed))
                {
                    detail = "The replacement could not take the playback speed.";
                    return false;
                }
                if (handle.Tint is { } tint) _port.WriteVfxTint(fresh.Address, tint);
                if (Math.Abs(handle.VfxIntensity - 1f) > 0.001f)
                    _port.SetVfxIntensity(fresh.Address, handle.VfxIntensity);
                if (handle.VfxPaused && !_port.TryPauseVfx(fresh.Address))
                {
                    detail = "The replacement could not be paused.";
                    return false;
                }
            }
            if (handle.Opacity < 1f && visible)
                _port.WriteOpacity(fresh.Address, handle.Opacity);
            return true;
        }
        catch (Exception ex)
        {
            detail = $"The replacement settings could not be applied: {ex.Message}";
            return false;
        }
    }

    private bool TryCleanupFresh(WorldObjectIncarnation identity)
    {
        bool cleaned = TryDestroyIncarnation(identity);
        if (!cleaned && !_pendingTeardowns.Contains(identity))
            _pendingTeardowns.Add(identity);
        return cleaned;
    }

    /// <summary>Destroys one exact incarnation Poser owns. A BG address that
    /// already holds a different allocation is not ours any more.</summary>
    public bool TryDestroyIncarnation(WorldObjectIncarnation identity)
    {
        try
        {
            if (identity.IsVfx) return _port.TryDestroyVfx(identity);
            if (!_port.IsAlive(identity.Address)) return true;
            if (!_port.TryReadIncarnation(identity.Address, out var current)) return false;
            // A queued BG cleanup owns this incarnation, not a reusable slot.
            if (!current.SameAllocation(identity)) return true;
            return _port.TryDestroy(identity.Address);
        }
        catch (Exception ex)
        {
            _log.Warning($"WorldObjectService: respawn teardown remains pending: {ex.Message}");
            return false;
        }
    }

    public bool TryCleanupUnidentified(string path, nint address)
    {
        if (NativeWorldObjectPort.IsVfxPath(path.Trim()))
        {
            // A failed identity read cannot safely authorize a raw-address
            // VFX destroy: that address may already hold a replacement.
            return false;
        }
        return _port.TryDestroy(address);
    }

    public void RetryPendingTeardowns()
    {
        for (int i = _pendingTeardowns.Count - 1; i >= 0; i--)
        {
            var identity = _pendingTeardowns[i];
            bool cleaned = TryDestroyIncarnation(identity);
            if (cleaned)
                _pendingTeardowns.RemoveAt(i);
        }
    }
}
