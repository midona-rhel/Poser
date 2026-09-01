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
        bool initialVisible,
        bool spawned = false)
    {
        _owner = owner;
        Id = id;
        Name = name;
        Path = path;
        Address = address;
        InitialPlacement = initialPlacement;
        InitialFlags = initialFlags;
        InitialVisible = initialVisible;
        Spawned = spawned;
        _placement = initialPlacement;
    }

    /// <summary>Whether POSER created this object. A spawned object is
    /// owned — destroyed on release — where a borrowed one is the map's
    /// and is restored. Everything else about the handle is identical.</summary>
    public bool Spawned { get; }

    public int Id { get; }

    /// <summary>What the sidebar calls it — the model file's own name until
    /// the user renames it. The name is Poser's, never written back to the
    /// map; a changed name moves the scene signature exactly as a prop's
    /// does.</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                _name = value.Trim();
        }
    }

    private string _name = string.Empty;

    /// <summary>The model resource path, or the adoption address when no model
    /// is loaded. A SPAWNED object's path can change — respawning from a
    /// stated path is how the model field edits.</summary>
    public string Path { get; internal set; }

    /// <summary>The native address the object was adopted at — or, for a
    /// spawned VFX, the CURRENT incarnation's address: the loop refresh
    /// recreates the effect and swaps this in place, so the handle and
    /// every id bound to it survive the churn.</summary>
    public nint Address { get; internal set; }

    /// <summary>Whether this is a world VFX rather than a model — spawned
    /// from an .avfx path, playing rather than standing.</summary>
    public bool IsVfx =>
        Path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the effect replays on the refresh interval. Most
    /// world effects are one-shots the game retires; looping them is the
    /// point of spawning one, so it starts on.</summary>
    public bool LoopVfx { get; set; } = true;

    /// <summary>The effect's playback speed. Written through immediately;
    /// re-applied after every loop refresh.</summary>
    public float VfxSpeed
    {
        get => _vfxSpeed;
        set
        {
            _vfxSpeed = Math.Clamp(value, 0f, 5f);
            if (!_released)
                _owner.WriteVfxSpeed(this, _vfxSpeed);
        }
    }

    private float _vfxSpeed = 1f;

    /// <summary>One uniform brightness on the effect's intensity triple,
    /// 1 as authored, up to Brio's 4.</summary>
    public float VfxIntensity
    {
        get => _vfxIntensity;
        set
        {
            _vfxIntensity = Math.Clamp(value, 0f, 4f);
            if (!_released)
                _owner.WriteVfxIntensity(this);
        }
    }

    private float _vfxIntensity = 1f;

    /// <summary>Whether the effect is frozen mid-frame. Paused also
    /// suspends the loop refresh — a recreate would restart the frames
    /// the pause is holding.</summary>
    public bool VfxPaused
    {
        get => _vfxPaused;
        set
        {
            _vfxPaused = value;
            if (!_released)
                _owner.WriteVfxPaused(this);
        }
    }

    private bool _vfxPaused;

    /// <summary>The drawn opacity, 1 fully drawn: a VFX's alpha, a BG
    /// object's dither. Composed with <see cref="Visible"/> — hiding
    /// writes zero, showing writes this.</summary>
    public float Opacity
    {
        get => _opacity;
        set
        {
            _opacity = Math.Clamp(value, 0f, 1f);
            if (!_released)
                _owner.WriteOpacity(this);
        }
    }

    private float _opacity = 1f;

    /// <summary>The colour, when the user tinted it: a VFX's colour
    /// multiplier, a BG object's stain dye. Null leaves the file's own
    /// colours alone (a BG object that WAS dyed clears back to white).
    /// </summary>
    public Vector3? Tint
    {
        get => _tint;
        set
        {
            bool hadTint = _tint is not null;
            _tint = value;
            if (!_released && (value is not null || hadTint))
                _owner.WriteTint(this);
        }
    }

    private Vector3? _tint;

    /// <summary>Whether the model can take the dye at all: effects always
    /// tint; a BG model only when it was built for staining. Null while
    /// the model streams.</summary>
    public bool? Dyeable =>
        _released ? false : _owner.CanDye(this);

    /// <summary>When the loop refresh next recreates this effect. Internal
    /// to the service's tick.</summary>
    internal DateTime NextVfxRefresh = DateTime.MaxValue;

    /// <summary>The model's DAY or NIGHT dressing — lamps glow at
    /// night. OFF (day) is the default everywhere a state is undefined
    /// (ruled 2026-09-01): a fresh spawn is dressed for day even though
    /// the raw native object ships lit, and a file without the field
    /// reads day.</summary>
    public bool NightState
    {
        get => _nightState;
        set
        {
            _nightState = value;
            if (!_released)
                _owner.WriteNightState(this);
        }
    }

    private bool _nightState;

    internal void SeedNightState(bool value) => _nightState = value;

    /// <summary>The adopted original's own state, put back on release.
    /// </summary>
    internal bool? InitialNightState;

    /// <summary>An adopted EFFECT's own colour, intensity and speed,
    /// put back on release — otherwise a tint or a pause sticks on the
    /// zone's effect until the zone reloads (the stuck-glow report).
    /// </summary>
    internal (Vector4 Color, Vector3 Intensity, float Speed)?
        InitialVfxState;

    /// <summary>Whether the state write is still owed, once the model
    /// streams in.</summary>
    internal bool NightStatePending;

    /// <summary>Whether the user explicitly dressed an ADOPTED object.
    /// The zone's layout keeps re-dressing its own instances, so a held
    /// state is re-asserted on the tick until release.</summary>
    internal bool NightStateHeld;

    /// <summary>Whether an animated model's motion is frozen — a windmill
    /// mid-turn, a banner mid-sway. A no-op on models with no skeleton.
    /// </summary>
    public bool AnimationPaused
    {
        get => _animationPaused;
        set
        {
            _animationPaused = value;
            if (!_released)
                _owner.WriteAnimationPaused(this);
        }
    }

    private bool _animationPaused;

    /// <summary>Ticks left to retry the speed write while the skeleton
    /// streams in. Bounded: a model with no animation never grows
    /// controls, and its retries must not run forever.</summary>
    internal int AnimationPauseRetries;

    /// <summary>The placement the pause froze; re-written every draw
    /// while the pause stands. A drag updates it through the transform
    /// setter, so a paused object still moves where the user says.</summary>
    internal Transform? HeldPause;

    /// <summary>The instance tail the pause froze — the animation clock
    /// lives in it, and a held transform over a running clock jumps on
    /// unpause.</summary>
    internal byte[]? HeldPauseTail;

    /// <summary>What the anchor pump last wrote, so the game's own write
    /// is recognisable: raw differing from this without us writing IS
    /// the animation.</summary>
    internal Transform? LastWritten;

    /// <summary>The game transform at anchor engagement. The animation's
    /// motion is measured against it and replayed on the USER'S placement
    /// — the object animates around wherever the user put it.</summary>
    internal Transform? AnimRef;

    /// <summary>Whether the anchor ever engaged on this object — an
    /// unpause then re-engages IMMEDIATELY instead of waiting to detect
    /// motion again. The detection wait left a frame window that
    /// rendered the game's raw value (the unpause blip) and let a quick
    /// re-pause capture the raw ORIGINAL place instead of the base.
    /// </summary>
    internal bool WasAnchored;

    /// <summary>Set on unpause of a previously anchored object: the next
    /// pump engages on the spot, writing the base that same frame.</summary>
    internal bool EngageNext;

    /// <summary>Sets the desired placement WITHOUT writing — the unpause
    /// hand-off: where you froze it becomes where it stands.</summary>
    internal void SeedPlacement(Transform value) => _placement = value;

    /// <summary>The user's placement, for the anchor pump.</summary>
    internal Transform DesiredPlacement => _placement;

    /// <summary>Live debug access to the base object's 64-bit flag word.
    /// </summary>
    public ulong? DebugObjectFlags
    {
        get => _released ? null : _owner.ReadObjectFlags(this);
        set
        {
            if (!_released && value is { } stated)
                _owner.WriteObjectFlags(this, stated);
        }
    }

    /// <summary>Live debug access to one instance byte (the port bounds
    /// the offsets).</summary>
    public byte? DebugByte(int offset) =>
        _released ? null : _owner.ReadDebugByte(this, offset);

    public void SetDebugByte(int offset, byte value)
    {
        if (!_released)
            _owner.WriteDebugByte(this, offset, value);
    }

    /// <summary>Respawns this SPAWNED object from the stated path — the
    /// model field's apply. The old incarnation is destroyed only after
    /// the new one took, so a bad path costs nothing.</summary>
    public bool Respawn(string path, out string? detail) =>
        _owner.Respawn(this, path, out detail);

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
        // An ANCHORED object's stated transform is the user's BASE, and a
        // paused one's is the frozen pose — never the live animated value,
        // which would spin the gizmo and save a random phase.
        get => _released
            ? _placement
            : AnimRef is not null
                ? _placement
                : HeldPause ?? _owner.ReadPlacement(this, _placement);
        set
        {
            if (_released)
                return;
            // Moving an ANIMATED object pauses it first (ruled
            // 2026-09-01): a drag against a running animation is two
            // writers on one value.
            if (!IsVfx && !_animationPaused
                && (AnimRef is not null || EngageNext))
                AnimationPaused = true;
            _placement = value;
            // A paused object still goes where the user drags it: the
            // hold re-writes THIS value from then on.
            if (HeldPause is not null)
                HeldPause = value;
            _owner.WritePlacementTracked(this, value);
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
    private readonly Dalamud.Plugin.Services.IFramework? _framework;
    private readonly List<AdoptedWorldObject> _adopted = new();

    private int _nextId;
    private bool _disposed;

    /// <summary>How often a looping effect is even CHECKED for having
    /// run out. The loop replays the same instance in place (Brio's
    /// active check + play) — the old recreate-on-interval visibly
    /// blinked the effect off and on.</summary>
    private static readonly TimeSpan VfxRefreshInterval =
        TimeSpan.FromSeconds(1);

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
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        if (_framework != null)
            _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>The loop refresh: each looping spawned VFX past its
    /// interval is recreated in place. One per frame at most — churning
    /// several effects in one frame stutters for nothing.</summary>
    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework frame)
    {
        if (_disposed || _adopted.Count == 0)
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
            if (handle.AnimationPauseRetries > 0)
            {
                handle.AnimationPauseRetries--;
                if (_port.WriteBgAnimationSpeed(
                        handle.Address,
                        handle.AnimationPaused ? 0f : 1f))
                    handle.AnimationPauseRetries = 0;
            }
            // An adopted object's held dressing: the zone's layout keeps
            // re-writing its own instances, so the user's choice is
            // re-asserted whenever the game takes it back.
            if (handle.NightStateHeld
                && _port.ReadBgNightState(handle.Address) is { } current
                && current != handle.NightState)
                _port.WriteBgNightState(handle.Address, handle.NightState);
            if (!handle.Spawned || !handle.IsVfx || !handle.LoopVfx
                || handle.VfxPaused)
                continue;
            if (now < handle.NextVfxRefresh)
                continue;
            handle.NextVfxRefresh = now + VfxRefreshInterval;
            // Replay the SAME instance only once it actually ran out —
            // no recreate, so nothing blinks.
            if (!_port.IsVfxActive(handle.Address))
                _port.ResumeVfx(handle.Address, handle.VfxSpeed);
        }
    }

    /// <summary>Recreates one SPAWNED object from the stated path, keeping
    /// the handle: same id, same name, same placement, same bindings — a
    /// new native incarnation under them. The loop refresh and the model
    /// field's apply are both this. The old native object is destroyed
    /// only after the new spawn took.</summary>
    internal bool Respawn(
        AdoptedWorldObject handle, string path, out string? detail)
    {
        detail = null;
        if (_disposed || !handle.Spawned || !_adopted.Contains(handle))
        {
            detail = "Only a spawned object can respawn.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            detail = "The path names nothing.";
            return false;
        }
        var placement = handle.Transform;
        bool visible = handle.Visible;
        var fresh = _port.Spawn(path, placement);
        if (fresh == nint.Zero)
        {
            detail = $"'{DisplayName(path)}' could not be spawned — the "
                + "game did not take it.";
            return false;
        }
        var old = handle.Address;
        handle.Address = fresh;
        handle.Path = path.Trim();
        try
        {
            _port.Destroy(old);
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"WorldObjectService: destroying the old incarnation failed: {ex.Message}");
        }
        if (!visible)
            handle.Visible = false;
        if (handle.IsVfx)
        {
            if (Math.Abs(handle.VfxSpeed - 1f) > 0.001f)
                _port.SetVfxSpeed(fresh, handle.VfxSpeed);
            if (handle.Tint is { } tint)
                _port.WriteVfxTint(fresh, tint);
            if (Math.Abs(handle.VfxIntensity - 1f) > 0.001f)
                _port.SetVfxIntensity(fresh, handle.VfxIntensity);
            if (handle.VfxPaused)
            {
                _port.PauseVfx(fresh);
                handle.NextVfxRefresh = DateTime.MaxValue;
            }
            else
            {
                handle.NextVfxRefresh = DateTime.UtcNow + VfxRefreshInterval;
            }
        }
        else
        {
            if (handle.Tint is not null)
                // The fresh incarnation's model is still loading, so the
                // dye rides the pending-stain retry.
                WriteTint(handle);
            // The fresh incarnation ships lit; restate the dressing.
            handle.NightStatePending = true;
            if (handle.AnimationPaused)
                handle.AnimationPauseRetries = AnimationPauseRetryTicks;
        }
        if (handle.Opacity < 1f && visible)
            _port.WriteOpacity(fresh, handle.Opacity);
        _events.Publish(new WorldObjectListChangedEvent());
        return true;
    }

    internal void WriteVfxSpeed(AdoptedWorldObject handle, float speed)
    {
        if (_disposed || !_port.IsAlive(handle.Address))
            return;
        _port.SetVfxSpeed(handle.Address, speed);
    }

    internal void WriteVfxIntensity(AdoptedWorldObject handle)
    {
        if (_disposed || !_port.IsAlive(handle.Address) || !handle.IsVfx)
            return;
        _port.SetVfxIntensity(handle.Address, handle.VfxIntensity);
    }

    internal void WriteVfxPaused(AdoptedWorldObject handle)
    {
        if (_disposed || !_port.IsAlive(handle.Address) || !handle.IsVfx)
            return;
        if (handle.VfxPaused)
        {
            _port.PauseVfx(handle.Address);
            handle.NextVfxRefresh = DateTime.MaxValue;
        }
        else
        {
            _port.ResumeVfx(handle.Address, handle.VfxSpeed);
            handle.NextVfxRefresh = DateTime.UtcNow;
        }
    }

    /// <summary>BG objects whose stain write is waiting for the model's
    /// stain buffer to exist; retried on the framework tick, exactly
    /// Stagehand's poll.</summary>
    private readonly HashSet<AdoptedWorldObject> _pendingStains = new();

    internal ulong? ReadObjectFlags(AdoptedWorldObject handle) =>
        _disposed || !_port.IsAlive(handle.Address)
            ? null
            : _port.ReadBgObjectFlags(handle.Address);

    internal void WriteObjectFlags(AdoptedWorldObject handle, ulong flags)
    {
        if (!_disposed && _port.IsAlive(handle.Address))
            _port.WriteBgObjectFlags(handle.Address, flags);
    }

    internal byte? ReadDebugByte(AdoptedWorldObject handle, int offset) =>
        _disposed || !_port.IsAlive(handle.Address)
            ? null
            : _port.ReadBgTailByte(handle.Address, offset);

    internal void WriteDebugByte(
        AdoptedWorldObject handle, int offset, byte value)
    {
        if (!_disposed && _port.IsAlive(handle.Address))
            _port.WriteBgTailByte(handle.Address, offset, value);
    }

    internal bool? CanDye(AdoptedWorldObject handle) =>
        _disposed || !_port.IsAlive(handle.Address)
            ? false
            : handle.IsVfx
                ? true
                : _port.CanDyeBg(handle.Address);

    /// <summary>How many framework ticks a pending animation-speed write
    /// keeps retrying — the skeleton streams in behind the model; a model
    /// without one lets the countdown lapse.</summary>
    private const int AnimationPauseRetryTicks = 600;

    internal void WriteAnimationPaused(AdoptedWorldObject handle)
    {
        if (_disposed || !_port.IsAlive(handle.Address) || handle.IsVfx)
            return;
        float speed = handle.AnimationPaused ? 0f : 1f;
        handle.AnimationPauseRetries =
            _port.WriteBgAnimationSpeed(handle.Address, speed)
                ? 0
                : AnimationPauseRetryTicks;
        // Skeleton speed covers skeleton-animated scenery only. Motion
        // driven through the TRANSFORM (a windmill's turning rotation)
        // has its gate somewhere in the undocumented tail: pausing runs
        // the automated hunt — flip a candidate byte, watch the rotation,
        // keep the byte that stops it (2026-09-01, user-directed).
        if (handle.AnimationPaused)
        {
            // THE MECHANISM (proved in game 2026-09-01): re-write the
            // captured transform AND the instance tail — the animation
            // clock lives in it — every draw. A draw-time write lands
            // after the game's animator and wins the frame; skeleton
            // speed, flag bits and an UpdateRender skip all did nothing.
            // A pause landing in the unpause hand-off window captures
            // the BASE — the live value is the game's raw original
            // place for a frame or two.
            // On an ANCHORED object a field read LIES: the game's writer
            // runs again after our seam write, so the fields end every
            // frame holding ITS value while the render shows ours (the
            // log proved it: held 127.36 vs base 125.93). The frozen pose
            // is what we last composed, never a raw read.
            handle.HeldPause = handle.EngageNext
                ? handle.DesiredPlacement
                : handle.AnimRef is not null
                    && handle.LastWritten is { } composed
                    ? composed
                    : _port.TryRead(handle.Address, out var frozen)
                        ? frozen
                        : handle.Transform;
            handle.EngageNext = false;
            var tail = new byte[0x20];
            handle.HeldPauseTail =
                _port.TryReadBgTail(handle.Address, tail)
                    ? tail
                    : null;
            _log.Debug(
                "[WorldObject] pause topology: "
                + _port.DescribeBgAnimation(handle.Address));
        }
        else
        {
            if (handle.HeldPause is { } frozen)
                handle.SeedPlacement(frozen);
            handle.HeldPause = null;
            handle.HeldPauseTail = null;
            handle.LastWritten = null;
            handle.AnimRef = null;
            if (handle.WasAnchored)
                handle.EngageNext = true;
        }
    }

    /// <summary>Whether the anchor is pumped from the render seam (the
    /// camera scene-update detour) — the overlay's draw-time pump then
    /// stands down. The render seam is strictly better: its writes land
    /// BEFORE the frame renders, so nothing flickers and unpause does
    /// not blip; the draw pump remains the fallback when the camera
    /// signature is gone.</summary>
    public bool AnchorPumpedFromRender { get; set; }

    /// <summary>The ANIMATION ANCHOR. Best seated in the render seam
    /// (see <see cref="AnchorPumpedFromRender"/>); otherwise the
    /// overlay's DRAW, where a write still wins the race but shows the
    /// game's value for one rendered frame (the flicker). Paused
    /// objects re-write their frozen transform and clock. Objects the
    /// game visibly animates get COMPOSED instead: the animation's
    /// motion, measured against the engagement reference, is replayed
    /// on the user's own placement — move the object and the animation
    /// moves with it; unpause continues from the frozen phase because
    /// the frozen pose became the base.</summary>
    public void HoldPausedAnimations()
    {
        if (_disposed)
            return;
        foreach (var handle in _adopted)
        {
            if (handle.IsVfx || !_port.IsAlive(handle.Address))
                continue;
            if (handle.AnimationPaused)
            {
                if (handle.HeldPause is not { } held)
                    continue;
                _port.Write(handle.Address, held);
                if (handle.HeldPauseTail is { } heldTail)
                    _port.WriteBgTailHeld(handle.Address, heldTail);
                handle.LastWritten = held;
                handle.AnimRef = null;
                continue;
            }
            if (!_port.TryRead(handle.Address, out var raw))
                continue;
            if (handle.EngageNext)
            {
                // The unpause hand-off: engage on the game's fresh value
                // and write the base THIS frame, so no raw frame renders.
                handle.EngageNext = false;
                handle.AnimRef = raw;
                var resumed = handle.DesiredPlacement;
                _port.Write(handle.Address, resumed);
                handle.LastWritten = resumed;
                continue;
            }
            if (handle.AnimRef is { } reference)
            {
                var user = handle.DesiredPlacement;
                var inverse = Quaternion.Inverse(reference.Rotation);
                var deltaRotation = Quaternion.Normalize(
                    inverse * raw.Rotation);
                var deltaPosition = Vector3.Transform(
                    raw.Position - reference.Position, inverse);
                var composed = new Transform(
                    user.Position
                        + Vector3.Transform(deltaPosition, user.Rotation),
                    Quaternion.Normalize(user.Rotation * deltaRotation),
                    user.Scale);
                _port.Write(handle.Address, composed);
                handle.LastWritten = composed;
                continue;
            }
            if (handle.LastWritten is { } prior
                && (Vector3.DistanceSquared(
                        raw.Position, prior.Position) > 0.000001f
                    || Math.Abs(Quaternion.Dot(
                        raw.Rotation, prior.Rotation)) < 0.999999f))
            {
                // The game moved it since we last wrote: the object IS
                // animated. Engage — from here its motion replays on the
                // user's placement.
                handle.AnimRef = raw;
                handle.WasAnchored = true;
                var user = handle.DesiredPlacement;
                _port.Write(handle.Address, user);
                handle.LastWritten = user;
                continue;
            }
            handle.LastWritten ??= raw;
        }
    }

    internal void WriteNightState(AdoptedWorldObject handle)
    {
        if (_disposed || !_port.IsAlive(handle.Address) || handle.IsVfx)
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
        if (_disposed || !_port.IsAlive(handle.Address))
            return;
        if (handle.IsVfx)
        {
            if (handle.Tint is { } tint)
                _port.WriteVfxTint(handle.Address, tint);
            return;
        }
        if (!_port.WriteBgTint(handle.Address, handle.Tint))
            _pendingStains.Add(handle);
    }

    /// <summary>Restates the drawn opacity from the handle's own facts —
    /// hidden writes zero, shown writes the stated opacity.</summary>
    internal void WriteOpacity(AdoptedWorldObject handle)
    {
        if (_disposed || !_port.IsAlive(handle.Address))
            return;
        _port.WriteOpacity(
            handle.Address, handle.Visible ? handle.Opacity : 0f);
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
        if (_disposed || !_port.IsAvailable)
        {
            detail = "The world cannot be reached right now.";
            return null;
        }
        // The spawn-vs-borrow investigation (2026-09-01): whether Create
        // handed back an address the map already stands, or a claim
        // already holds.
        bool preExisting = false;
        foreach (var row in _port.Enumerate())
            if (string.Equals(row.Path, path, StringComparison.Ordinal))
                preExisting = true;
        var address = _port.Spawn(path, placement);
        if (address == nint.Zero)
        {
            detail = $"'{DisplayName(path)}' could not be spawned — the "
                + "game did not take the model.";
            return null;
        }
        _port.TryRead(address, out var landed);
        _log.Debug(
            $"[WorldObject] spawn '{path}' -> {address:X} "
            + $"(path already in world: {preExisting}, "
            + $"already claimed: {Find(address) != null}, "
            + $"asked ({placement.Position.X:F1}, {placement.Position.Y:F1}, "
            + $"{placement.Position.Z:F1}) landed ({landed.Position.X:F1}, "
            + $"{landed.Position.Y:F1}, {landed.Position.Z:F1}))");
        var handle = new AdoptedWorldObject(
            this,
            ++_nextId,
            UniqueName(DisplayName(path)),
            path,
            address,
            placement,
            0,
            true,
            spawned: true);
        if (!visible)
            handle.Visible = false;
        if (handle.IsVfx)
            handle.NextVfxRefresh = DateTime.UtcNow + VfxRefreshInterval;
        else
            // The raw native object ships lit; the default dressing is
            // day, written once the model streams in.
            handle.NightStatePending = true;
        _adopted.Add(handle);
        _events.Publish(new WorldObjectListChangedEvent());
        return handle;
    }

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
        // The original's own dressing, put back on release; the handle
        // starts from the same value so the buttons read true.
        handle.InitialNightState = _port.ReadBgNightState(address);
        if (handle.IsVfx
            && _port.TryReadVfxState(
                address, out var vfxColor, out var vfxIntensity,
                out var vfxSpeed))
            handle.InitialVfxState = (vfxColor, vfxIntensity, vfxSpeed);
        if (handle.InitialNightState is { } adoptedState)
            handle.SeedNightState(adoptedState);
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

    internal void WritePlacementTracked(
        AdoptedWorldObject handle, in Transform placement)
    {
        handle.LastWritten = placement;
        WritePlacement(handle, placement);
    }

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
        // A dimmed object re-shows at ITS opacity, not full: the two facts
        // compose here, the one place both are known.
        if (visible && handle.Opacity < 1f)
            _port.WriteOpacity(handle.Address, handle.Opacity);
    }

    // ── restore ──────────────────────────────────────────────────────────

    /// <summary>The one place a captured pair is written back. An address that
    /// has stopped being a BG object is left alone: restoring onto whatever
    /// took its place is the single way this contract could do harm.</summary>
    private void RestoreNative(AdoptedWorldObject handle)
    {
        // A SPAWNED object has nothing to restore: it is Poser's own, and
        // its end is destruction.
        if (handle.Spawned)
        {
            try
            {
                _port.Destroy(handle.Address);
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"WorldObjectService: destroying a spawned object failed: {ex.Message}");
            }
            finally
            {
                handle.MarkReleased(handle.InitialPlacement);
            }
            return;
        }
        try
        {
            if (_port.IsAlive(handle.Address))
            {
                _port.Write(handle.Address, handle.InitialPlacement);
                _port.WriteFlags(handle.Address, handle.InitialFlags);
                if (handle.InitialNightState is { } dressing)
                    _port.WriteBgNightState(handle.Address, dressing);
                if (handle.AnimationPaused)
                    _port.WriteBgAnimationSpeed(handle.Address, 1f);
                if (handle.InitialVfxState is { } effect)
                    _port.RestoreVfxState(
                        handle.Address,
                        effect.Color,
                        effect.Intensity,
                        effect.Speed,
                        resume: handle.VfxPaused);
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
        if (!evt.IsGPosing)
            ReleaseAll();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (_framework != null)
            _framework.Update -= OnFrameworkUpdate;
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseChanged);
        // Released BEFORE the disposed flag goes up: the restore writes go
        // through the same guarded path every other release does, and a
        // service that has already said it is disposed refuses them.
        ReleaseAll();
        _disposed = true;
    }
}
