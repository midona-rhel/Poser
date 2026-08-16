using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Services;

namespace Poser.Game.WorldObjects;

/// <summary>
/// A user-adopted world object and the state needed to restore it. The handle
/// borrows the game object; it does not own or destroy the native object.
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

    /// <summary>The model resource path, or the adoption address when no model
    /// is loaded.</summary>
    public string Path { get; }

    /// <summary>The native address the object was adopted at. Valid for this
    /// GPose session only.</summary>
    public nint Address { get; }

    /// <summary>Placement captured when the object was adopted and restored on
    /// release.</summary>
    public Transform InitialPlacement { get; }

    /// <summary>Draw flags captured when the object was adopted and restored
    /// beside its placement.</summary>
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
/// Tracks adopted map objects. Adoption captures placement and draw state;
/// every release path restores that state and drops the claim. A missing or
/// replaced native address is ignored rather than written blindly.
/// </summary>
public sealed class WorldObjectService : IDisposable
{
    private readonly IWorldObjectPort _port;
    private readonly IEventBus _events;
    private readonly IPluginLog _log;
    private readonly List<AdoptedWorldObject> _adopted = new();

    private int _nextId;
    private bool _disposed;

    public WorldObjectService(
        IWorldObjectPort port,
        IEventBus events,
        IPluginLog log)
    {
        _port = port;
        _events = events;
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

    /// <summary>Returns unadopted world objects in the port's traversal order.
    /// Range filtering and ordering belong to the overlay.</summary>
    public IReadOnlyList<WorldObjectCandidate> GetCandidates()
    {
        if (_disposed)
            return Array.Empty<WorldObjectCandidate>();
        var rows = _port.Enumerate();
        if (rows.Count == 0)
            return Array.Empty<WorldObjectCandidate>();

        var candidates = new List<WorldObjectCandidate>(rows.Count);
        foreach (var row in rows)
        {
            if (IsAdopted(row.Address))
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
            UniqueName(DisplayName(path)),
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

    /// <summary>How far a saved map position may sit from a live one and still
    /// be the same object. It absorbs the codec — a float that has been through
    /// a decimal string is not the float that went in — and nothing more: two
    /// BG objects of one model standing five centimetres apart is not a map
    /// anyone builds, while a rounding error of that size is every one of
    /// them.</summary>
    public const float IdentityToleranceYalms = 0.05f;

    /// <summary>
    /// Finds the object a saved scene entry names and adopts it at the
    /// placement that entry recorded.
    ///
    /// <para>Identity is the pair the MAP owns: the model path, and the point
    /// the map stands the object at. The address is deliberately not part of it
    /// — a saved address belongs to the run that saved it. The nearest live
    /// candidate within <see cref="IdentityToleranceYalms"/> of the saved point
    /// wins, and an entry that matches nothing here is refused BY NAME rather
    /// than applied to whatever else shares its path.</para>
    ///
    /// <para>Null with a stated <paramref name="detail"/> on every refusal.
    /// </para>
    /// </summary>
    public AdoptedWorldObject? AdoptByIdentity(
        string path,
        Vector3 mapPosition,
        Transform placement,
        bool visible,
        out string? detail)
    {
        detail = null;
        if (_disposed)
        {
            detail = "The world-object service is not running.";
            return null;
        }
        if (string.IsNullOrEmpty(path))
        {
            detail = "The entry names no model, so nothing could be matched.";
            return null;
        }

        // The live listing is checked SECOND, because a claim this session
        // already holds has been moved by the user: the object no longer stands
        // at the point that names it, and only the claim still remembers that
        // point. Re-adopting it would capture the user's own placement as the
        // map's, and the release would then owe the map the wrong value.
        foreach (var claim in _adopted)
        {
            if (!string.Equals(claim.Path, path, StringComparison.Ordinal))
                continue;
            if (Vector3.Distance(claim.InitialPlacement.Position, mapPosition)
                > IdentityToleranceYalms)
                continue;
            detail = $"'{DisplayName(path)}' is already borrowed by this scene.";
            return null;
        }

        nint best = nint.Zero;
        float bestDistance = float.PositiveInfinity;
        foreach (var row in _port.Enumerate())
        {
            if (!string.Equals(row.Path, path, StringComparison.Ordinal))
                continue;
            if (IsAdopted(row.Address))
                continue;
            float distance = Vector3.Distance(row.Placement.Position, mapPosition);
            if (distance > IdentityToleranceYalms || distance >= bestDistance)
                continue;
            best = row.Address;
            bestDistance = distance;
        }

        if (best == nint.Zero)
        {
            detail = $"'{DisplayName(path)}' is not standing where this scene " +
                "recorded it, so it was not borrowed.";
            return null;
        }

        detail = null;
        var handle = AdoptAt(best, placement, visible);
        if (handle == null)
            detail = $"'{DisplayName(path)}' could not be borrowed.";
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
                // Written BESIDE the flags rather than left to them: whether
                // the drawn bit lives inside that byte is the game's business,
                // and this contract may not rest on the answer.
                _port.WriteVisible(handle.Address, handle.InitialVisible);
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
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
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
