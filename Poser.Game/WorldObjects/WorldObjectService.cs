using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Services;

namespace Poser.Game.WorldObjects;

/// <summary>
/// One BG object the user has taken into the scene: a name, the address it was
/// adopted at, the placement and flags it stood with BEFORE anything was
/// written to it, and a live write-through transform.
///
/// <para>The handle owns nothing native. It is a claim on an object the GAME
/// owns, and the whole of that claim is the pair of captured values below —
/// <see cref="InitialPlacement"/> and <see cref="InitialFlags"/> — which are
/// what releasing it puts back. A released handle reads its last values and
/// writes nothing, exactly as a destroyed prop's handle does.</para>
/// </summary>
public sealed class AdoptedWorldObject
{
    private readonly WorldObjectService _owner;
    private Transform _placement;
    private bool _released;

    internal AdoptedWorldObject(
        WorldObjectService owner,
        int id,
        string name,
        string path,
        nint address,
        Transform initialPlacement,
        byte initialFlags,
        bool initialVisible)
    {
        _owner = owner;
        Id = id;
        Name = name;
        Path = path;
        Address = address;
        InitialPlacement = initialPlacement;
        InitialFlags = initialFlags;
        InitialVisible = initialVisible;
        _placement = initialPlacement;
    }

    public int Id { get; }

    /// <summary>What the sidebar calls it — the model file's own name, which
    /// is the only human-readable thing a BG object carries.</summary>
    public string Name { get; }

    /// <summary>The model resource path, or the adoption address when the
    /// object had no loaded model. It is the half of the scene-file identity
    /// that survives a session (Ktisis names the same value <c>Path</c>,
    /// <c>Ktisis/Structs/Objects/WorldObject.cs:32-40</c>).</summary>
    public string Path { get; }

    /// <summary>The native address the object was adopted at. Valid for this
    /// GPose session only.</summary>
    public nint Address { get; }

    /// <summary>Where the object stood at the moment of adoption. THE spine of
    /// this feature: every release, every session end and every unload writes
    /// this value back.</summary>
    public Transform InitialPlacement { get; }

    /// <summary>The draw flags the object stood with at adoption, restored
    /// beside the placement — Ktisis restores the same byte
    /// (<c>Scene/Entities/World/ObjectEntity.cs:42-49</c>), which is what puts
    /// back a visibility the user toggled.</summary>
    public byte InitialFlags { get; }

    /// <summary>Whether the object was drawn at adoption. Held beside the
    /// flags byte because it is the value a scene file and an undo entry
    /// state — neither of them may carry a raw game flag word.</summary>
    public bool InitialVisible { get; }

    /// <summary>Whether the claim is still live. False after a release, after
    /// GPose ends, and after unload.</summary>
    public bool IsValid => !_released && _owner.IsLive(this);

    /// <summary>Whether the object is drawn. Toggling it writes the draw
    /// flags' low bit and nothing else, so a release still puts the WHOLE
    /// captured byte back.</summary>
    public bool Visible
    {
        get => !_released && _owner.ReadVisible(this, InitialVisible);
        set
        {
            if (_released)
                return;
            _owner.WriteVisible(this, value);
        }
    }

    /// <summary>The live placement. Reading answers what the object actually
    /// stands at; assigning writes it through and re-states the render caches.
    /// </summary>
    public Transform Transform
    {
        get => _released ? _placement : _owner.ReadPlacement(this, _placement);
        set
        {
            if (_released)
                return;
            _placement = value;
            _owner.WritePlacement(this, value);
        }
    }

    internal void MarkReleased(Transform lastPlacement)
    {
        _placement = lastPlacement;
        _released = true;
    }
}

/// <summary>
/// The scene's claim on the map: which BG objects the user has adopted, and
/// the one contract that makes adopting one safe.
///
/// <para>THE RESTORE CONTRACT, which is the whole feature. An adopted world
/// object is BORROWED, never owned: Poser does not create it, never destroys
/// it, and must never leave it displaced. Adoption captures the object's
/// placement and draw flags before anything is written; every exit from the
/// claim writes that pair back:</para>
/// <list type="number">
/// <item><description><see cref="Release"/> — the user removed it from the
/// scene.</description></item>
/// <item><description><see cref="ReleaseAll"/> — the scene was cleared, or a
/// scene load replaced it.</description></item>
/// <item><description>GPose ended: the session that made the claims is over,
/// so every claim ends with it.</description></item>
/// <item><description><see cref="Dispose"/> — the plugin unloaded. The last
/// line of defence, and it runs whether or not the events above
/// did.</description></item>
/// </list>
/// <para>Each is idempotent, each leaves the service holding nothing, and any
/// two of them in either order are still correct. An address that has stopped
/// being a BG object is dropped rather than restored onto — writing a captured
/// transform into whatever took its place would be the one way this contract
/// could do harm.</para>
///
/// <para>THE SAFETY RULE, which is NOT the actor band's rule: a BG object is
/// not a character, so the 201–439 GPose object-index gate has nothing to say
/// about it. The rule here is that the only objects ever written are the ones
/// the user adopted — <see cref="IWorldObjectPort.Enumerate"/> reads the whole
/// graph, but every write below goes through a handle that exists only because
/// a click created it.</para>
///
/// <para>Brio's world-object subsystem is NOT the reference for this: its BG
/// objects are SPAWNED (<c>BGOObject</c> calls <c>BgObject.Create</c> and
/// <c>Dtor</c>, <c>Brio/Game/WorldObjects/Objects/BGOObject.cs:56-104</c>), so
/// it owns and destroys them. Adopting one the map already placed is Ktisis'
/// feature alone.</para>
/// </summary>
public sealed class WorldObjectService : IDisposable
{
    private readonly IWorldObjectPort _port;
    private readonly IEventBus _events;
    private readonly IObjectTable _objects;
    private readonly IPluginLog _log;
    private readonly List<AdoptedWorldObject> _adopted = new();

    private int _nextId;
    private bool _disposed;

    public WorldObjectService(
        IWorldObjectPort port,
        IEventBus events,
        IObjectTable objects,
        IPluginLog log)
    {
        _port = port;
        _events = events;
        _objects = objects;
        _log = log;
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
    }

    /// <summary>The live claims. It is the service's own list, so a caller
    /// that releases while reading must work off a snapshot.</summary>
    public IReadOnlyList<AdoptedWorldObject> Adopted => _adopted;

    public int Count => _adopted.Count;

    /// <summary>Whether the world's graph can be reached at all right now.
    /// </summary>
    public bool IsAvailable => !_disposed && _port.IsAvailable;

    /// <summary>
    /// Everything the world holds that the scene has not taken, nearest first.
    /// Already-adopted objects are filtered out by address, which is what makes
    /// an adopted object's handle disappear from the viewport the moment it
    /// joins the scene (Ktisis does the same filter at draw time,
    /// <c>Ktisis/Interface/Overlay/SceneDraw.cs:201</c>).
    /// </summary>
    public IReadOnlyList<WorldObjectCandidate> GetCandidates()
    {
        if (_disposed)
            return Array.Empty<WorldObjectCandidate>();
        var rows = _port.Enumerate();
        if (rows.Count == 0)
            return Array.Empty<WorldObjectCandidate>();

        var origin = _objects.LocalPlayer?.Position ?? Vector3.Zero;
        var candidates = new List<WorldObjectCandidate>(rows.Count);
        foreach (var row in rows)
        {
            if (IsAdopted(row.Address))
                continue;
            candidates.Add(new WorldObjectCandidate(
                row.Address,
                row.Path,
                DisplayName(row.Path),
                Vector3.Distance(row.Placement.Position, origin),
                row.Placement.Position));
        }
        candidates.Sort(static (left, right) =>
            left.DistanceFromPlayer.CompareTo(right.DistanceFromPlayer));
        return candidates;
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
    public AdoptedWorldObject? Adopt(nint address)
    {
        if (_disposed)
            return null;
        if (Find(address) is { } existing)
            return existing;
        if (!_port.TryRead(address, out var placement))
        {
            _log.Warning(
                "WorldObjectService: that world object no longer exists.");
            return null;
        }
        if (!_port.TryReadFlags(address, out var flags))
            flags = 0;
        if (!_port.TryReadVisible(address, out bool visible))
            visible = true;

        string path = PathOf(address);
        var handle = new AdoptedWorldObject(
            this,
            ++_nextId,
            DisplayName(path),
            path,
            address,
            placement,
            flags,
            visible);
        _adopted.Add(handle);
        _events.Publish(new WorldObjectListChangedEvent());
        return handle;
    }

    /// <summary>
    /// Adopts one object and puts it back where a saved scene had it. The scene
    /// load path: the capture still records where the map STANDS it, so the
    /// release restores the map's placement and not the file's.
    /// </summary>
    public AdoptedWorldObject? AdoptAt(nint address, Transform placement, bool visible)
    {
        var handle = Adopt(address);
        if (handle == null)
            return null;
        handle.Transform = placement;
        handle.Visible = visible;
        return handle;
    }

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
        if (!_adopted.Remove(handle))
            return false;
        RestoreNative(handle);
        _events.Publish(new WorldObjectListChangedEvent());
        return true;
    }

    /// <summary>Gives every object back. The scene-clear edge, and the shared
    /// body of the GPose-exit and unload edges.</summary>
    public void ReleaseAll()
    {
        if (_adopted.Count == 0)
            return;
        for (int i = 0; i < _adopted.Count; i++)
            RestoreNative(_adopted[i]);
        _adopted.Clear();
        _events.Publish(new WorldObjectListChangedEvent());
    }

    // ── the handle's write-through half ──────────────────────────────────

    internal bool IsLive(AdoptedWorldObject handle) =>
        !_disposed && _adopted.Contains(handle) && _port.IsAlive(handle.Address);

    internal Transform ReadPlacement(AdoptedWorldObject handle, Transform fallback) =>
        _port.TryRead(handle.Address, out var placement) ? placement : fallback;

    internal void WritePlacement(AdoptedWorldObject handle, in Transform placement)
    {
        if (_disposed || !_port.IsAlive(handle.Address))
            return;
        _port.Write(handle.Address, placement);
    }

    internal bool ReadVisible(AdoptedWorldObject handle, bool fallback) =>
        _port.TryReadVisible(handle.Address, out bool visible) ? visible : fallback;

    internal void WriteVisible(AdoptedWorldObject handle, bool visible)
    {
        if (_disposed || !_port.IsAlive(handle.Address))
            return;
        _port.WriteVisible(handle.Address, visible);
    }

    // ── restore ──────────────────────────────────────────────────────────

    /// <summary>The one place a captured pair is written back. An address that
    /// has stopped being a BG object is left alone: restoring onto whatever
    /// took its place is the single way this contract could do harm.</summary>
    private void RestoreNative(AdoptedWorldObject handle)
    {
        try
        {
            if (_port.IsAlive(handle.Address))
            {
                _port.Write(handle.Address, handle.InitialPlacement);
                _port.WriteFlags(handle.Address, handle.InitialFlags);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"WorldObjectService: restoring a world object failed: {ex.Message}");
        }
        finally
        {
            // The claim ends even when the write threw: a handle whose restore
            // half-ran must never be handed back to the port.
            handle.MarkReleased(handle.InitialPlacement);
        }
    }

    private string PathOf(nint address)
    {
        foreach (var row in _port.Enumerate())
            if (row.Address == address)
                return row.Path;
        return address.ToString("X", CultureInfo.InvariantCulture);
    }

    /// <summary>The row label: the model file's own name without its folder or
    /// extension, which is the only part of a BG object path a human reads.
    /// Brio names its BG objects the same way
    /// (<c>BGOObject.FriendlyPath</c>, <c>Brio/Game/WorldObjects/Objects/BGOObject.cs:26</c>).
    /// </summary>
    public static string DisplayName(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "World object";
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private void OnGPoseChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing)
            ReleaseAll();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        // Released BEFORE the disposed flag goes up: the restore writes go
        // through the same guarded path every other release does, and a
        // service that has already said it is disposed refuses them.
        ReleaseAll();
        _disposed = true;
    }
}
