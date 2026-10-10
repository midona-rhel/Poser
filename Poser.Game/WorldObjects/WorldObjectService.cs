using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain;
using Poser.Services;

using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Application.Events;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Tracks adopted map objects. Adoption captures placement and draw state;
/// every release path restores that state and drops the claim. A missing or
/// replaced native address is ignored rather than written blindly.
/// </summary>
public sealed class WorldObjectService : IDisposable
{
    private readonly IWorldObjectPort _port;
    private readonly IEventBus _events;
    private readonly IPluginLog _log;
    private readonly Dalamud.Plugin.Services.IFramework? _framework;
    private readonly VfxLifecycleOwner _vfx;
    private readonly VfxPlaybackControl _vfxPlayback;
    private readonly SceneryAnimationHold _animation;
    private readonly WorldObjectRespawner _respawner;
    private readonly FurnitureLoadTracker _loadingFurniture;
    private readonly WorldObjectRestorer _restorer;
    private readonly List<AdoptedWorldObject> _adopted = new();

    private int _nextId;
    private bool _disposed;
    private bool _teardownOnly;

    public WorldObjectService(
        IWorldObjectPort port,
        IEventBus events,
        IPluginLog log,
        Dalamud.Plugin.Services.IFramework? framework = null)
    {
        _port = port;
        _events = events;
        _log = log;
        _framework = framework;
        _vfx = new VfxLifecycleOwner(port);
        _vfxPlayback = new VfxPlaybackControl(port, log);
        _animation = new SceneryAnimationHold(port);
        _respawner = new WorldObjectRespawner(port, events, log,
            IsHandleCurrent, handle => _pendingStains.Remove(handle));
        _loadingFurniture = new FurnitureLoadTracker(port, log,
            _adopted.Contains, Release);
        _restorer = new WorldObjectRestorer(port, _vfx, _respawner, log, IsHandleCurrent);
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        if (_framework != null)
            _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>The loop refresh: each looping spawned VFX past its
    /// interval is recreated in place. One per frame at most — churning
    /// several effects in one frame stutters for nothing.</summary>
    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework frame)
    {
        if (_disposed)
            return;
        _respawner.RetryPendingTeardowns();
        _port.Pump();
        _loadingFurniture.Pump(DateTime.UtcNow);
        PumpRespawns(DateTime.UtcNow);
        if (_teardownOnly)
        {
            ReleaseAll();
            _respawner.RetryPendingTeardowns();
            return;
        }
        if (_adopted.Count == 0)
            return;
        // Stain writes that beat their model's load land here, once the
        // stain buffer exists.
        if (_pendingStains.Count > 0)
            _pendingStains.RemoveWhere(pending =>
                !_adopted.Contains(pending)
                || !_port.IsAlive(pending.Address)
                // A model that CANNOT take dye retires its retry — the
                // buffer will never appear (undyeable models have none).
                || _port.CanDyeBg(pending.Address) == false
                || _port.WriteBgTint(pending.Address, pending.Tint));
        var now = DateTime.UtcNow;
        foreach (var handle in _adopted)
        {
            if (handle.NightStatePending && _port.IsBgReady(handle.Address))
            {
                handle.NightStatePending = false;
                _port.WriteBgNightState(handle.Address, handle.NightState);
            }
            _animation.RetryPendingSpeed(handle);
            // An adopted object's held dressing: the zone's layout keeps
            // re-writing its own instances, so the user's choice is
            // re-asserted whenever the game takes it back.
            if (handle.NightStateHeld
                && _port.ReadBgNightState(handle.Address) is { } current
                && current != handle.NightState)
                _port.WriteBgNightState(handle.Address, handle.NightState);
            if (!handle.Spawned || !handle.IsVfx || !handle.LoopVfx
                || handle.VfxPlayback != VfxPlaybackState.Playing
                || !IsHandleCurrent(handle))
                continue;
            _vfxPlayback.ReplayIfEnded(handle, now);
        }
    }

    /// <summary>Replaces a spawned object's native body without changing its
    /// scene identity. Completion includes renderer readiness and property replay.</summary>
    internal Task<Outcome> Respawn(
        AdoptedWorldObject handle, string path)
    {
        if (_disposed || _teardownOnly || !handle.Spawned
            || !_adopted.Contains(handle))
            return Task.FromResult(new Outcome(false, "Only a spawned object can respawn."));
        return _respawner.Respawn(handle, path);
    }

    internal void PumpRespawns(DateTime now) => _respawner.Pump(now);

    internal bool TryWriteVfxSpeed(AdoptedWorldObject handle, float speed)
    {
        if (_disposed || (_teardownOnly && handle.IsVfx)
            || !handle.IsVfx || !IsHandleCurrent(handle))
        {
            _vfxPlayback.LogRefusal("speed");
            return false;
        }
        return _vfxPlayback.TrySetSpeed(handle, speed);
    }

    internal void WriteVfxIntensity(AdoptedWorldObject handle)
    {
        if (_disposed || (_teardownOnly && handle.IsVfx)
            || !IsHandleCurrent(handle) || !handle.IsVfx)
            return;
        _vfxPlayback.WriteIntensity(handle);
    }

    internal bool TryWriteVfxPaused(
        AdoptedWorldObject handle, bool paused)
    {
        if (_disposed || (_teardownOnly && handle.IsVfx)
            || !IsHandleCurrent(handle) || !handle.IsVfx)
        {
            _vfxPlayback.LogRefusal("pause/resume");
            return false;
        }
        return _vfxPlayback.TrySetPaused(handle, paused);
    }

    /// <summary>BG objects whose stain write is waiting for the model's
    /// stain buffer to exist; retried on the framework tick, exactly
    /// Stagehand's poll.</summary>
    private readonly HashSet<AdoptedWorldObject> _pendingStains = new();

    internal bool? CanDye(AdoptedWorldObject handle) =>
        _disposed || !IsHandleCurrent(handle)
            ? false
            : handle.IsVfx
                ? true
                : _port.CanDyeBg(handle.Address);

    internal void WriteAnimationPaused(AdoptedWorldObject handle)
    {
        if (_disposed || !_port.IsAlive(handle.Address) || handle.IsVfx)
            return;
        _animation.WritePaused(handle);
    }

    /// <summary>Run by the Game frame-phase owner after native animation.
    /// Paused objects hold their transform and clock; anchored objects replay
    /// native motion relative to the user's placement.</summary>
    public void HoldPausedAnimations()
    {
        if (_disposed)
            return;
        foreach (var handle in _adopted)
        {
            if (handle.IsVfx || !IsHandleCurrent(handle))
                continue;
            _animation.Hold(handle);
        }
    }

    internal void WriteNightState(AdoptedWorldObject handle)
    {
        if (_disposed || !IsHandleCurrent(handle) || handle.IsVfx)
            return;
        if (_port.IsBgReady(handle.Address))
            _port.WriteBgNightState(handle.Address, handle.NightState);
        else
            handle.NightStatePending = true;
        if (!handle.Spawned)
            handle.NightStateHeld = true;
    }

    internal void WriteTint(AdoptedWorldObject handle)
    {
        if (_disposed || (_teardownOnly && handle.IsVfx)
            || !IsHandleCurrent(handle))
            return;
        if (handle.IsVfx)
        {
            if (handle.Tint is { } tint)
                _port.WriteVfxTint(handle.Address, tint);
            return;
        }
        if (handle.IsFurniture)
        {
            _port.WriteFurnitureColor(handle.Address, handle.Stain, handle.Tint);
            return;
        }
        if (!_port.WriteBgTint(handle.Address, handle.Tint))
            _pendingStains.Add(handle);
    }

    /// <summary>Restates the drawn opacity from the handle's own facts —
    /// hidden writes zero, shown writes the stated opacity.</summary>
    internal void WriteOpacity(AdoptedWorldObject handle)
    {
        if (_disposed || (_teardownOnly && handle.IsVfx)
            || !IsHandleCurrent(handle))
            return;
        _port.WriteOpacity(
            handle.Address, handle.IsFurniture || handle.Visible ? handle.Opacity : 0f);
    }

    /// <summary>The live claims. It is the service's own list, so a caller
    /// that releases while reading must work off a snapshot.</summary>
    public IReadOnlyList<AdoptedWorldObject> Adopted => _adopted;

    internal bool TryObserve(nint address, out WorldObjectIncarnation identity) =>
        _port.TryReadIncarnation(address, out identity);

#if DEBUG
    /// <summary>Debug: whether the object's model reports loaded.</summary>
    public bool IsReadyProbe(AdoptedWorldObject handle) =>
        !_disposed && IsHandleCurrent(handle) && _port.IsBgReady(handle.Address);
#endif

    public int Count => _adopted.Count;

    /// <summary>Whether the world's graph can be reached at all right now.
    /// </summary>
    public bool IsAvailable => !_disposed && _port.IsAvailable;

    /// <summary>Returns unadopted world objects in the port's traversal order.
    /// Range filtering and ordering belong to the overlay.</summary>
    public IReadOnlyList<WorldObjectCandidate> GetCandidates() =>
        Candidates(effects: false);

    /// <summary>The world's playing EFFECTS, listed apart — effects are
    /// their own class everywhere (portal tab, sidebar mark, footer
    /// glyph), never filed under the map's objects.</summary>
    public IReadOnlyList<WorldObjectCandidate> GetEffectCandidates() =>
        Candidates(effects: true);

    private IReadOnlyList<WorldObjectCandidate> Candidates(bool effects)
    {
        if (_disposed)
            return Array.Empty<WorldObjectCandidate>();
        var rows = _port.Enumerate();
        if (rows.Count == 0)
            return Array.Empty<WorldObjectCandidate>();

        var candidates = new List<WorldObjectCandidate>(rows.Count);
        foreach (var row in rows)
        {
            if (row.IsEffect != effects || IsAdopted(row.Address))
                continue;
            candidates.Add(new WorldObjectCandidate(
                row.Address,
                row.Path,
                DisplayName(row.Path),
                row.Placement.Position));
        }
        return candidates;
    }

    /// <summary>Reads an object's outline for hover feedback. This accepts a
    /// candidate address because hovering does not require adoption.</summary>
    public bool TryReadOutline(nint address, out byte outline)
    {
        if (_disposed)
        {
            outline = WorldObjectOutline.None;
            return false;
        }
        return _port.TryReadOutline(address, out outline);
    }

    /// <summary>Writes an object's transient hover outline. The caller owns
    /// the captured value used to restore it.</summary>
    public void WriteOutline(nint address, byte outline)
    {
        if (_disposed)
            return;
        _port.WriteOutline(address, outline);
    }

    /// <summary>Whether this address is already claimed.</summary>
    public bool IsAdopted(nint address)
    {
        foreach (var adopted in _adopted)
            if (adopted.Address == address)
                return true;
        return false;
    }

    /// <summary>The claim on one address, or null when it is not claimed.
    /// </summary>
    public AdoptedWorldObject? Find(nint address)
    {
        foreach (var adopted in _adopted)
            if (adopted.Address == address)
                return adopted;
        return null;
    }

    /// <summary>
    /// Takes one BG object into the scene. The object is NOT written: adoption
    /// reads its placement and flags and records them, and that is the whole
    /// act. Null when the address is not an addressable BG object, and the
    /// existing claim when it is already adopted — adopting twice is one claim,
    /// never two captures of a value the first one may already have changed.
    /// </summary>
    /// <summary>Creates a NEW BG object from a model path — Poser's own,
    /// destroyed on release rather than restored. This is how a saved
    /// world object comes back in another zone: by path, standing where
    /// the caller says. Null with a stated <paramref name="detail"/> on
    /// every refusal.</summary>
    public AdoptedWorldObject? Spawn(
        string path, Transform placement, bool visible, out string? detail)
    {
        detail = null;
        if (_disposed || _teardownOnly || !_port.IsAvailable)
        {
            detail = "The world cannot be reached right now.";
            return null;
        }
        var address = _port.Spawn(path, placement);
        if (address == nint.Zero)
        {
            detail = $"'{DisplayName(path)}' could not be spawned — the "
                + "game did not take the model.";
            return null;
        }
        if (!_port.TryReadIncarnation(address, out var identity)
            || (NativeWorldObjectPort.IsVfxPath(path.Trim())
                && (!identity.IsVfx
                    || identity.ResourceIdentity == nint.Zero)))
        {
            bool cleaned = _respawner.TryCleanupUnidentified(path, address);
            detail = cleaned
                ? "The spawned native object could not be identified."
                : "Spawn cleanup remains outstanding.";
            return null;
        }
        path = path.Trim();
        bool isVfx = NativeWorldObjectPort.IsVfxPath(path);
        var handle = new AdoptedWorldObject(
            this,
            ++_nextId,
            Poser.Domain.Scene.EntityNames.Next(DisplayName(path), _adopted.Select(x => x.Name)),
            path,
            address,
            identity,
            placement,
            0,
            true,
            spawned: true,
            isVfx: isVfx);
        if (!visible)
            handle.Visible = false;
        if (handle.IsVfx)
        {
            handle.VfxPlayback = VfxPlaybackState.Playing;
            handle.NextVfxRefresh = DateTime.UtcNow + VfxPlaybackControl.RefreshInterval;
        }
        else
            // The raw native object ships lit; the default dressing is
            // day, written once the model streams in.
            handle.NightStatePending = true;
        _adopted.Add(handle);
        if (handle.IsFurniture)
            _loadingFurniture.Track(handle, DateTime.UtcNow.AddSeconds(15));
        _events.Publish(new WorldObjectListChangedEvent());
        return handle;
    }

    internal IReadOnlyList<FurnitureLightState> ReadFurnitureLights(AdoptedWorldObject handle) =>
        !_disposed && handle.IsFurniture && IsHandleCurrent(handle) ? _port.ReadFurnitureLights(handle.Address) : [];

    internal void WriteFurnitureLights(AdoptedWorldObject handle, IReadOnlyList<FurnitureLightState> lights)
    {
        if (!_disposed && handle.IsFurniture && IsHandleCurrent(handle))
            _port.WriteFurnitureLights(handle.Address, lights);
    }

    /// <summary>Whether a spawned furniture piece is still streaming its
    /// model.</summary>
    internal bool IsLoading(AdoptedWorldObject handle) =>
        _loadingFurniture.IsLoading(handle);

    /// <summary>Keeps a piece that has not loaded; see
    /// <see cref="FurnitureLoadTracker.Keep"/>.</summary>
    internal void KeepUnloaded(AdoptedWorldObject handle) =>
        _loadingFurniture.Keep(handle);

    public AdoptedWorldObject? Adopt(nint address)
        => Adopt(address, null, out _);

    private AdoptedWorldObject? Adopt(
        nint address, WorldObjectIncarnation? expected, out string detail)
    {
        detail = string.Empty;
        if (expected is { } expectedIdentity
            && !CanReclaimBorrow(address, expectedIdentity, out detail))
            return null;
        if (_disposed || _teardownOnly)
        {
            detail = "Unable to undo: the world is unavailable.";
            return null;
        }
        if (Find(address) is { } existing)
        {
            if (expected is not null)
            {
                detail = "Unable to undo: this world object is already borrowed by a newer action.";
                return null;
            }
            return existing;
        }
        if (!_port.TryRead(address, out var placement))
        {
            _log.Warning(
                "WorldObjectService: that world object no longer exists.");
            detail = "Unable to undo: the current world object could not be read.";
            return null;
        }
        if (!_port.TryReadFlags(address, out var flags))
            flags = 0;
        if (!_port.TryReadVisible(address, out bool visible))
            visible = true;

        var observed = RowOf(address);
        string path = observed?.Path
            ?? address.ToString("X", CultureInfo.InvariantCulture);
        WorldObjectIncarnation identity;
        VfxStateSnapshot snapshot = default;
        if (!_port.TryReadIncarnation(address, out identity))
        {
            detail = "Unable to undo: the current world object identity could not be read.";
            return null;
        }
        // The graph's object type is authoritative even when its resource
        // filename was unreadable; never route an observed VFX through BG
        // restore merely because RowOf could not supply a path.
        bool isVfx = observed?.IsEffect == true || identity.IsVfx;
        if (isVfx && !_vfx.TryCapture(
                address, out identity, out snapshot))
        {
            _log.Warning(
                $"WorldObjectService: refusing VFX {address:X}; its playback state is unavailable.");
            detail = "Unable to undo: the current VFX state could not be read.";
            return null;
        }
        if (expected is { } expectedAfterCapture
            && !MatchesBorrowedIdentity(expectedAfterCapture, identity))
        {
            detail = "Unable to undo: the world object at this address no longer matches the captured identity.";
            return null;
        }
        float opacity = snapshot.Color.W;
        if (!isVfx && !_port.TryReadOpacity(address, out opacity))
        {
            detail = "Unable to undo: the current world object opacity could not be read.";
            return null;
        }
        var handle = new AdoptedWorldObject(
            this,
            ++_nextId,
            UniqueName(DisplayName(path)),
            path,
            address,
            identity,
            placement,
            flags,
            visible,
            isVfx: isVfx,
            initialOpacity: opacity);
        // The original's own dressing, put back on release; the handle
        // starts from the same value so the buttons read true.
        handle.InitialNightState = _port.ReadBgNightState(address);
        if (isVfx)
        {
            handle.InitialVfxSnapshot = snapshot;
            handle.VfxPlayback = snapshot.Playback;
            handle._vfxSpeed = snapshot.Speed;
            handle._vfxPaused = snapshot.Playback == VfxPlaybackState.Paused;
        }
        if (handle.InitialNightState is { } adoptedState)
            handle.SeedNightState(adoptedState);
        _adopted.Add(handle);
        _events.Publish(new WorldObjectListChangedEvent());
        detail = string.Empty;
        return handle;
    }

    /// <summary>Checks a released borrowed identity only after the current
    /// graph enumeration proves its address is live. The saved address itself
    /// is never resolved before that membership check.</summary>
    internal bool CanReclaimBorrow(
        nint address, WorldObjectIncarnation expected, out string detail)
    {
        detail = "Unable to undo: the world is unavailable.";
        if (_disposed || _teardownOnly || !_port.IsAvailable)
            return false;

        bool listed = _port.Enumerate().Any(row => row.Address == address);
        if (!listed)
        {
            detail = "Unable to undo: the original world object is no longer in the current world graph.";
            return false;
        }

        // This read is safe only after enumeration found the address in the
        // live graph. It still compares a best-effort observed incarnation;
        // same-address/same-resource BG reuse is not distinguishable here.
        if (!_port.TryReadIncarnation(address, out var current))
        {
            detail = "Unable to undo: the current world object identity could not be read.";
            return false;
        }
        bool matches = MatchesBorrowedIdentity(expected, current);
        if (!matches)
        {
            detail = "Unable to undo: the world object at this address no longer matches the captured identity.";
            return false;
        }

        detail = string.Empty;
        return true;
    }

    /// <summary>Re-adopts a released borrowed object after live graph
    /// membership and the captured incarnation both match.</summary>
    internal AdoptedWorldObject? ReclaimBorrow(
        nint address, WorldObjectIncarnation expected, out string detail)
    {
        return Adopt(address, expected, out detail);
    }

    private static bool MatchesBorrowedIdentity(
        WorldObjectIncarnation expected, WorldObjectIncarnation current) =>
        expected.IsVfx
            ? current == expected
            : current.SameAllocation(expected);

    /// <summary>How far a saved map position may sit from a live one and still
    /// be the same object. It absorbs the codec — a float that has been through
    /// a decimal string is not the float that went in — and nothing more: two
    /// BG objects of one model standing five centimetres apart is not a map
    /// anyone builds, while a rounding error of that size is every one of
    /// them.</summary>
    public const float IdentityToleranceYalms = 0.05f;


    /// <summary>
    /// Gives one object back to the map: its captured placement and flags are
    /// written back and the claim is forgotten. Returns false only when there
    /// was no such claim — a claim whose address has gone is still released
    /// (there is nothing left to restore onto), because leaving it in the list
    /// would leave the user holding a row that can never be given back.
    /// </summary>
    public bool Release(AdoptedWorldObject? handle)
    {
        if (handle == null)
            return false;
        if (!_adopted.Contains(handle))
            return false;
        _respawner.Cancel(handle, "Replacement cancelled because the object was released.");
        if (!_restorer.Restore(handle))
            return false;
        _adopted.Remove(handle);
        _events.Publish(new WorldObjectListChangedEvent());
        return true;
    }

    /// <summary>Gives every object back. The scene-clear edge, and the shared
    /// body of the GPose-exit and unload edges.</summary>
    public void ReleaseAll()
    {
        _respawner.CancelAll("Replacement cancelled because the scene was released.");
        _respawner.RetryPendingTeardowns();
        if (_adopted.Count == 0)
            return;
        foreach (var handle in _adopted.ToArray())
            if (_restorer.Restore(handle))
                _adopted.Remove(handle);
        _events.Publish(new WorldObjectListChangedEvent());
    }

    // ── the handle's write-through half ──────────────────────────────────

    internal bool IsLive(AdoptedWorldObject handle) =>
        !_disposed && _adopted.Contains(handle) && IsHandleCurrent(handle);

    private bool IsHandleCurrent(AdoptedWorldObject handle) =>
        handle.IsVfx
            ? _vfx.IsCurrent(handle.Identity)
            : _port.TryReadIncarnation(handle.Address, out var current)
                && current.SameAllocation(handle.Identity);

    internal Transform ReadPlacement(AdoptedWorldObject handle, Transform fallback) =>
        IsHandleCurrent(handle)
            && _port.TryRead(handle.Address, out var placement)
                ? placement
                : fallback;

    internal bool WritePlacementTracked(
        AdoptedWorldObject handle, in Transform placement)
    {
        if (!WritePlacement(handle, placement))
            return false;
        switch (handle.AnimationAnchor)
        {
            case SceneryAnimationState.Watching watching: watching.LastWritten = placement; break;
            case SceneryAnimationState.Anchored anchored: anchored.LastWritten = placement; break;
        }
        return true;
    }

    internal bool WritePlacement(AdoptedWorldObject handle, in Transform placement)
    {
        if (_disposed || (_teardownOnly && handle.IsVfx)
            || !IsHandleCurrent(handle))
            return false;
        if (handle.IsVfx)
        {
            if (_vfx.WriteTransform(
                    handle.Identity, placement, out var actualPlayback)
                != VfxTransformWriteResult.Written)
            {
                _vfxPlayback.LogRefusal("transform");
                return false;
            }
            handle.VfxPlayback = actualPlayback;
            return true;
        }
        _port.Write(handle.Address, placement);
        return true;
    }

    internal bool ReadVisible(AdoptedWorldObject handle, bool fallback) =>
        IsHandleCurrent(handle)
            && _port.TryReadVisible(handle.Address, out bool visible)
                ? visible
                : fallback;

    internal void WriteVisible(AdoptedWorldObject handle, bool visible)
    {
        if (_disposed || (_teardownOnly && handle.IsVfx)
            || !IsHandleCurrent(handle))
            return;
        _port.WriteVisible(handle.Address, visible);
        // A dimmed object re-shows at ITS opacity, not full: the two facts
        // compose here, the one place both are known.
        if (visible && handle.Opacity < 1f)
            _port.WriteOpacity(handle.Address, handle.Opacity);
    }

    private WorldObjectRow? RowOf(nint address)
    {
        foreach (var row in _port.Enumerate())
            if (row.Address == address)
                return row;
        return null;
    }

    /// <summary>
    /// The row label: the model file's own name without its folder or
    /// extension.
    ///
    /// <para>The path is an opaque asset code, so the display name removes
    /// only its folder and extension and leaves the stem unchanged.</para>
    ///
    /// <para>The stem is left unchanged because asset codes are opaque and
    /// users may search for the original path. The full path remains available
    /// in the object pane.</para>
    /// </summary>
    public static string DisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "World object";
        // The catalog's derived label — "Rock [r2f0_rok01a]" — so the
        // sidebar row, the viewport hover, and the entry name all speak
        // the same words the pickers found the thing under.
        return WorldAssetCatalog.LabelFor(path);
    }

    /// <summary>
    /// The label with a number when, and only when, it repeats. A map stands
    /// dozens of copies of one model, so borrowing three of them without this
    /// puts three identical rows in the tree and the user cannot tell which is
    /// which.
    ///
    /// <para>The FIRST is unnumbered — unlike a light, whose every name is
    /// generic and therefore numbered from one (<c>LightingService.UniqueName</c>).
    /// A world object's name is already distinctive; numbering a lone one is
    /// noise. The suffix is the lowest that is free, not a count, so releasing
    /// the middle of three and borrowing again reuses the gap rather than
    /// colliding.</para>
    /// </summary>
    private string UniqueName(string baseName)
    {
        if (!IsNameTaken(baseName))
            return baseName;
        for (int suffix = 2; ; suffix++)
        {
            string candidate = $"{baseName} {suffix}";
            if (!IsNameTaken(candidate))
                return candidate;
        }
    }

    private bool IsNameTaken(string name)
    {
        foreach (var adopted in _adopted)
            if (string.Equals(adopted.Name, name, StringComparison.Ordinal))
                return true;
        return false;
    }

    private void OnGPoseChanged(GPoseStateChangedEvent evt)
    {
        if (evt.IsGPosing)
        {
            if (_adopted.Count == 0 && !_respawner.HasPendingTeardowns)
                _teardownOnly = false;
            return;
        }
        _teardownOnly = true;
        ReleaseAll();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_framework != null)
            _framework.Update -= OnFrameworkUpdate;
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        _teardownOnly = true;
        // Released BEFORE the disposed flag goes up: the restore writes go
        // through the same guarded path every other release does, and a
        // service that has already said it is disposed refuses them.
        ReleaseAll();
        _respawner.RetryPendingTeardowns();
        // Keep the service recoverable when native teardown refused. The
        // outstanding claim remains in the list and can be retried by the
        // caller; declaring disposal complete would silently strand it.
        _disposed = _adopted.Count == 0 && !_respawner.HasPendingTeardowns;
    }
}
