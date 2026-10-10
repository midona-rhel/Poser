using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Poser.Core;
using Poser.Domain;
using Poser.Domain.Scene;
using Poser.Services;
using Poser.Domain.Transforms;

namespace Poser.Game.WorldObjects;

/// <summary>
/// A user-adopted world object and the state needed to restore it. The handle
/// borrows the game object; it does not own or destroy the native object.
/// </summary>
public sealed class AdoptedWorldObject : IWorldObject
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
        WorldObjectIncarnation identity,
        Transform initialPlacement,
        byte initialFlags,
        bool initialVisible,
        bool spawned = false,
        bool isVfx = false,
        float initialOpacity = 1f)
    {
        _owner = owner;
        Id = id;
        Name = name;
        Path = path;
        Address = address;
        Identity = identity;
        InitialPlacement = initialPlacement;
        InitialFlags = initialFlags;
        InitialVisible = initialVisible;
        InitialOpacity = _opacity = initialOpacity;
        Spawned = spawned;
        _isVfx = isVfx;
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

    /// <summary>Identity of the native incarnation currently behind the
    /// handle. Address reuse never authorizes a stale handle to write.</summary>
    internal WorldObjectIncarnation Identity { get; set; }

    /// <summary>Whether this is a world VFX rather than a model. Borrowed
    /// handles carry the graph's observed kind even when no resource filename
    /// is readable; spawned handles classify at the requested-path boundary.
    /// </summary>
    public bool IsVfx => _isVfx;
    public bool IsFurniture => Path.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase);
    private byte _stain;
    public byte Stain
    {
        get => _stain;
        set
        {
            _stain = value;
            if (!_released && IsFurniture) _owner.WriteTint(this);
        }
    }
    internal bool _isVfx;

    public IReadOnlyList<FurnitureLightState> FurnitureLights
    {
        get => _owner.ReadFurnitureLights(this);
        set => _owner.WriteFurnitureLights(this, value);
    }

    internal VfxPlaybackState VfxPlayback { get; set; } =
        VfxPlaybackState.Playing;

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
            float stated = Math.Clamp(value, 0f, 5f);
            if (!_released && _owner.TryWriteVfxSpeed(this, stated))
                _vfxSpeed = stated;
        }
    }

    internal float _vfxSpeed = 1f;

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
            if (!_released && _owner.TryWriteVfxPaused(this, value))
                _vfxPaused = value;
        }
    }

    internal bool _vfxPaused;

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
    public float InitialOpacity { get; }

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

    internal VfxStateSnapshot? InitialVfxSnapshot;

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

    internal SceneryAnimationState AnimationAnchor = new SceneryAnimationState.Watching();

    /// <summary>Sets the desired placement WITHOUT writing — the unpause
    /// hand-off: where you froze it becomes where it stands.</summary>
    internal void SeedPlacement(Transform value) => _placement = value;

    /// <summary>The user's placement, for the anchor pump.</summary>
    internal Transform DesiredPlacement => _placement;

    /// <summary>Respawns this SPAWNED object from the stated path — the
    /// model field's apply. The old incarnation is destroyed only after
    /// the new one took, so a bad path costs nothing.</summary>
    public Task<Outcome> Respawn(string path) =>
        _owner.Respawn(this, path);

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
            : AnimationAnchor switch
            {
                SceneryAnimationState.Anchored or SceneryAnimationState.ReanchorPending => _placement,
                SceneryAnimationState.Paused paused => paused.Placement,
                _ => _owner.ReadPlacement(this, _placement),
            };
        set
        {
            if (_released)
                return;
            // Moving an ANIMATED object pauses it first (ruled
            // 2026-09-01): a drag against a running animation is two
            // writers on one value.
            if (!IsVfx && !_animationPaused
                && AnimationAnchor is SceneryAnimationState.Anchored or SceneryAnimationState.ReanchorPending)
                AnimationPaused = true;
            // A failed/stale native write must not make the handle claim a
            // placement it never reached. Commit the desired value only
            // after the lifecycle owner accepts the current incarnation.
            if (!_owner.WritePlacementTracked(this, value))
                return;
            _placement = value;
            // A paused object still goes where the user drags it: the
            // hold re-writes THIS value from then on.
            if (AnimationAnchor is SceneryAnimationState.Paused paused)
                paused.Placement = value;
        }
    }

    internal void MarkReleased(Transform lastPlacement)
    {
        _placement = lastPlacement;
        _released = true;
    }
}
