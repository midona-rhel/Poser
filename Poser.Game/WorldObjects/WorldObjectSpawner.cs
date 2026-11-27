using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Domain;
using Poser.Domain.Transforms;
using Poser.Application.Events;
using Poser.Game.Core;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Spawns world objects, and replaces a spawned object's model without
/// changing the object in the scene. A replacement stays hidden until it has
/// loaded and carries the object's settings; only then is the old model torn
/// down and the object pointed at the new one. Any object a failed spawn or
/// replacement leaves behind is kept here and torn down on a later frame.
/// </summary>
internal sealed class WorldObjectSpawner
{
    private readonly IWorldObjectAccess _port;
    private readonly IEventBus _events;
    private readonly IPluginLog _log;
    private readonly Func<AdoptedWorldObject, bool> _isCurrent;
    private readonly Action<AdoptedWorldObject> _dropPendingStain;
    private readonly List<WorldObjectIncarnation> _pendingTeardowns = new();
    private readonly Dictionary<AdoptedWorldObject, PendingRespawn> _respawns = new();

    private sealed record PendingRespawn(
        WorldObjectIncarnation Fresh, string Path, DateTime Deadline,
        TaskCompletionSource<Outcome> Completion, Func<bool>? StillCurrent,
        System.Threading.CancellationToken Cancellation);

    public WorldObjectSpawner(
        IWorldObjectAccess port,
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

    /// <summary>
    /// Spawns an object and reads back which object the game made. Spawning
    /// a new object and replacing a model both start here. The identity comes
    /// from the spawn itself, so when anything after it fails, the object that
    /// was made is torn down by that identity, or queued for teardown, and
    /// never by its address alone.
    /// </summary>
    public WorldObjectIncarnation? SpawnIdentified(string path, in Transform placement, out string? detail)
    {
        detail = null;
        var address = _port.Spawn(path, placement, out var allocated);
        if (address == nint.Zero)
        {
            if (allocated.Address != nint.Zero) TryCleanupFresh(allocated);
            detail = $"'{WorldObjectService.DisplayName(path)}' could not be spawned — the game did not take it.";
            return null;
        }
        if (!_port.TryReadIncarnation(address, out var identity)
            || !allocated.SameAllocation(identity))
        {
            detail = TryCleanupFresh(allocated)
                ? "The new object could not be identified."
                : "The new object could not be identified, and its cleanup is still waiting.";
            return null;
        }
        return identity;
    }

    /// <summary>Starts a replacement for an admitted spawned handle. The
    /// caller has already checked that the service and the handle accept it.</summary>
    public Task<Outcome> Respawn(AdoptedWorldObject handle, string path,
        Func<bool>? stillCurrent = null, System.Threading.CancellationToken cancellation = default)
    {
        static Task<Outcome> Refused(string detail) =>
            Task.FromResult(new Outcome(false, detail));
        if (cancellation.IsCancellationRequested || stillCurrent?.Invoke() == false)
            return Refused("Replacement cancelled because its operation is no longer current.");
        if (_respawns.ContainsKey(handle))
            return Refused("This object already has a replacement loading.");
        if (string.IsNullOrWhiteSpace(path))
            return Refused("The path names nothing.");
        path = path.Trim();
        if (SpawnIdentified(path, handle.Transform, out var spawnDetail) is not { } freshIdentity)
            return Refused(spawnDetail!);
        var fresh = freshIdentity.Address;
        var pending = new PendingRespawn(freshIdentity, path,
            DateTime.UtcNow.AddSeconds(15),
            new(TaskCreationOptions.RunContinuationsAsynchronously), stillCurrent, cancellation);
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
        if (CancelIfStale(handle, pending)) return;
        var freshIdentity = pending.Fresh;
        if (!_isCurrent(handle)
            || !_port.TryReadIncarnation(handle.Address, out var oldIdentity)
            || !oldIdentity.SameAllocation(handle.Identity)
            || !_port.TryReadIncarnation(freshIdentity.Address, out var currentFresh)
            || !currentFresh.SameAllocation(freshIdentity))
        {
            Finish(handle, pending, false, "The object or its replacement changed in the game while replacing the model.");
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
        // The last point where the replacement can still be called off. Any
        // later, the old model would already be gone.
        if (CancelIfStale(handle, pending)) return;
        bool oldDestroyed = TryDestroyIncarnation(handle.Identity);
        if (!oldDestroyed)
        {
            Finish(handle, pending, false, "The old model could not be torn down.");
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

    private bool CancelIfStale(AdoptedWorldObject handle, PendingRespawn pending)
    {
        if (!pending.Cancellation.IsCancellationRequested && pending.StillCurrent?.Invoke() != false)
            return false;
        Finish(handle, pending, false, "Replacement cancelled because its operation is no longer current.");
        return true;
    }

    private void Finish(AdoptedWorldObject handle, PendingRespawn pending,
        bool succeeded, string? detail)
    {
        // Only the replacement that is still running is finished, so a late
        // call can't tear down a model the object already uses.
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
            // Every setting is written before the old model is torn down.
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

    /// <summary>Destroys exactly the object Poser owns. A model address that
    /// now holds a different object is not Poser's any more.</summary>
    public bool TryDestroyIncarnation(WorldObjectIncarnation identity)
    {
        try
        {
            if (identity.IsVfx) return _port.TryDestroyVfx(identity);
            if (!_port.IsAlive(identity.Address)) return true;
            if (!_port.TryReadIncarnation(identity.Address, out var current)) return false;
            // A different object at the same address is not Poser's to destroy.
            if (!current.SameAllocation(identity)) return true;
            return _port.TryDestroy(identity.Address);
        }
        catch (Exception ex)
        {
            _log.Warning($"WorldObjectService: respawn teardown remains pending: {ex.Message}");
            return false;
        }
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
