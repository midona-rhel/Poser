using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using Poser.Domain;

using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Application.Events;
using Poser.Game.Core;
using Poser.Game.Services;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Keeps track of the map objects Poser borrowed and the objects it spawned.
/// Releasing a borrowed object puts back the state it had when borrowed;
/// releasing a spawned one destroys it. If cleanup fails, the object stays in
/// the list so cleanup can be tried again. Poser never writes to an object that
/// is no longer the one it holds.
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

    /// <summary>Each frame, moves on loading, model replacement, cleanup and
    /// value writes that had to wait, then restarts looping effects that ended.</summary>
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
        // A stain set before the model finished loading is written here, once
        // the model's dye data exists.
        if (_pendingStains.Count > 0)
            _pendingStains.RemoveWhere(pending =>
                !_adopted.Contains(pending)
                || !_port.IsAlive(pending.Address)
                // A model that cannot be dyed stops retrying: it never gets
                // dye data.
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
            // A borrowed object's day or night state: the map keeps setting
            // its own objects back, so the chosen state is written again
            // whenever the game changes it.
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

    /// <summary>Replaces a spawned object's model in the game without changing
    /// the object in the scene. It is done once the new model is drawn and the
    /// object's values are applied to it again.</summary>
    internal Task<Outcome> Respawn(
        AdoptedWorldObject handle, string path, Func<bool>? stillCurrent = null,
        System.Threading.CancellationToken cancellation = default)
    {
        if (_disposed || _teardownOnly || !handle.Spawned
            || !_adopted.Contains(handle))
            return Task.FromResult(new Outcome(false, "Only a spawned object can respawn."));
        return _respawner.Respawn(handle, path, stillCurrent, cancellation);
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

    /// <summary>Map models whose stain is waiting for the model's dye data to
    /// exist. Tried again every frame, the same way Stagehand does it.</summary>
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

    /// <summary>Called each frame after the game has animated its objects. A
    /// paused object keeps its position and animation time. A moved object
    /// plays the game's motion relative to where it was placed.</summary>
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

    /// <summary>Writes the drawn opacity from the object's own values: zero when
    /// it is hidden, its opacity when it is shown.</summary>
    internal void WriteOpacity(AdoptedWorldObject handle)
    {
        if (_disposed || (_teardownOnly && handle.IsVfx)
            || !IsHandleCurrent(handle))
            return;
        _port.WriteOpacity(
            handle.Address, handle.IsFurniture || handle.Visible ? handle.Opacity : 0f);
    }

    /// <summary>The objects Poser holds. This is the service's own list, so a
    /// caller that releases objects while going through it must copy it first.</summary>
    public IReadOnlyList<AdoptedWorldObject> Adopted => _adopted;

    internal bool TryObserve(nint address, out WorldObjectIncarnation identity) =>
        _port.TryReadIncarnation(address, out identity);

#if DEBUG
    /// <summary>Debug: whether the object's model reports loaded.</summary>
    public bool IsReadyProbe(AdoptedWorldObject handle) =>
        !_disposed && IsHandleCurrent(handle) && _port.IsBgReady(handle.Address);
#endif

    public int Count => _adopted.Count;

    /// <summary>Whether the game's list of map objects can be read at all right
    /// now.</summary>
    public bool IsAvailable => !_disposed && _port.IsAvailable;

    /// <summary>Map objects Poser does not hold yet, in the order the game lists
    /// them. The overlay filters them by distance and sorts them.</summary>
    public IReadOnlyList<WorldObjectCandidate> GetCandidates() =>
        Candidates(effects: false);

    /// <summary>Effects Poser does not hold yet, listed apart from map models.
    /// Effects are listed whether or not they are playing.</summary>
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

    /// <summary>Reads an object's outline for the hover highlight. It takes any
    /// object, because hovering does not need Poser to hold the object.</summary>
    public bool TryReadOutline(nint address, out byte outline)
    {
        if (_disposed)
        {
            outline = WorldObjectOutline.None;
            return false;
        }
        return _port.TryReadOutline(address, out outline);
    }

    /// <summary>Writes an object's hover outline while it is hovered. The caller
    /// keeps the old value and puts it back.</summary>
    public void WriteOutline(nint address, byte outline)
    {
        if (_disposed)
            return;
        _port.WriteOutline(address, outline);
    }

    /// <summary>Whether Poser already holds the object at this address.</summary>
    public bool IsAdopted(nint address)
    {
        foreach (var adopted in _adopted)
            if (adopted.Address == address)
                return true;
        return false;
    }

    /// <summary>The object Poser holds at this address, or null when it holds
    /// none there.</summary>
    public AdoptedWorldObject? Find(nint address)
    {
        foreach (var adopted in _adopted)
            if (adopted.Address == address)
                return adopted;
        return null;
    }

    /// <summary>
    /// Spawns a model, furniture piece or effect from a game file path.
    /// Releasing it destroys it. Returns null and a reason when spawning fails.
    /// </summary>
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
            // A newly spawned map model starts with its lights on. The
            // default is day, which is written once the model has loaded.
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

    /// <summary>Whether a spawned furniture piece is still loading its
    /// model.</summary>
    internal bool IsLoading(AdoptedWorldObject handle) =>
        _loadingFurniture.IsLoading(handle);

    /// <summary>Keeps a furniture piece whose model has not loaded, instead of
    /// removing it when its load time runs out.</summary>
    internal void KeepUnloaded(AdoptedWorldObject handle) =>
        _loadingFurniture.Keep(handle);

    /// <summary>How far from its saved place a map object is still taken for the saved one.
    /// Some map objects move by themselves, such as the pieces that circle an aetheryte.</summary>
    private const float MovingObjectRange = 5f;

    /// <summary>An object within this distance of its saved place has not moved.</summary>
    private const float ExactPlace = 0.01f;

    /// <summary>The borrowed map object a saved entry means: the one with the same model
    /// nearest to the saved place. Null when none is within <see cref="MovingObjectRange"/>.</summary>
    public AdoptedWorldObject? FindBorrowed(string path, Vector3 mapPosition) =>
        Nearest(_adopted.Where(item => !item.Spawned && item.IsValid),
            item => item.Path, item => item.InitialPlacement.Position, path, mapPosition).Item;

    private static (T? Item, float Distance) Nearest<T>(IEnumerable<T> items, Func<T, string> pathOf,
        Func<T, Vector3> positionOf, string path, Vector3 mapPosition)
    {
        (T? Item, float Distance) best = (default, MovingObjectRange);
        foreach (var item in items)
        {
            if (!string.Equals(pathOf(item), path, StringComparison.OrdinalIgnoreCase))
                continue;
            var distance = Vector3.Distance(positionOf(item), mapPosition);
            if (distance <= best.Distance)
                best = (item, distance);
        }
        return best;
    }

    public AdoptedWorldObject? RestoreBorrowed(string path, Vector3 mapPosition, Transform placement,
        bool visible, out string? detail)
    {
        // Compare against where the object was first found, also when an earlier load moved
        // it. A borrowed object standing exactly at the saved place is the one. Otherwise an
        // object not borrowed yet comes first, so that two saved objects with the same model
        // do not both take the same one.
        var borrowed = Nearest(_adopted.Where(item => !item.Spawned && item.IsValid),
            item => item.Path, item => item.InitialPlacement.Position, path, mapPosition);
        var onMap = borrowed.Item != null && borrowed.Distance <= ExactPlace ? default
            : Nearest(_port.Enumerate().Where(row => !IsAdopted(row.Address)).Select(row => (WorldObjectRow?)row),
                row => row!.Value.Path, row => row!.Value.Placement.Position, path, mapPosition);
        var handle = onMap.Item is { } unborrowed ? Adopt(unborrowed.Address) : borrowed.Item;
        detail = handle == null ? "The original map object could not be found near its saved position." : null;
        if (handle != null)
        {
            handle.Transform = placement;
            handle.Visible = visible;
        }
        return handle;
    }

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
        // Trust the object type the game reports, even when the file name
        // could not be read. A VFX must never be put back the way a map model
        // is, just because no path was found for it.
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
        // The object's own day or night state, put back on release. The entry
        // starts from the same value, so the buttons show the real state.
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

    /// <summary>Checks that a released map object can be borrowed again: first
    /// that the game still lists its address, and only then that the object
    /// there is the same one. The saved address is never read before that.</summary>
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

        // Reading the address is only safe because the game lists it. The
        // check is best effort: a map model the game rebuilt at the same
        // address from the same file cannot be told apart here.
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

    /// <summary>Borrows a released map object again, once the game still lists
    /// its address and the object there is the same one.</summary>
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


    /// <summary>
    /// Releases an object Poser holds, after cancelling any model replacement
    /// in progress. Returns false for an object Poser does not hold, or when
    /// cleanup in the game fails; then the object stays in the list so cleanup
    /// can be tried again.
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

    /// <summary>Gives every object back. Used when the scene is cleared, when
    /// GPose ends and when Poser unloads.</summary>
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

    // ── Reading and writing one object in the game ───────────────────────

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
        // A dimmed object shows again at its own opacity, not at full. This
        // is the one place that knows both values, so they are combined here.
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
    /// The object's name from the asset catalog, including names a person has
    /// set. An empty path gets the name "World object".
    /// </summary>
    public static string DisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "World object";
        // The catalog's name, such as "Rock [r2f0_rok01a]", so the scene
        // list, the hover label and the saved name all use the words the
        // pickers showed.
        return WorldAssetCatalog.LabelFor(path);
    }

    /// <summary>
    /// The name, with a number added only when the name is already used. A map
    /// has dozens of copies of one model, so without this, borrowing three of
    /// them would show three rows with the same name, and nobody could tell
    /// which is which.
    ///
    /// <para>The first one has no number. Lights are different: their names are
    /// all generic, so they are numbered from one. A world object's name already
    /// says what it is, so a number on a single one adds nothing. The number is
    /// the lowest free one, not a count, so releasing the middle of three and
    /// borrowing again fills the gap instead of repeating a name.</para>
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
        // Try cleanup before marking the service disposed. Objects that fail
        // to release stay in the list for another try.
        ReleaseAll();
        _respawner.RetryPendingTeardowns();
        // If cleanup in the game failed, the service stays usable. The object
        // stays in the list so the caller can try again; marking the service
        // disposed would quietly leave the object behind.
        _disposed = _adopted.Count == 0 && !_respawner.HasPendingTeardowns;
    }
}
