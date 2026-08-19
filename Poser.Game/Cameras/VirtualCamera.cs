using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;

namespace Poser.Game.Cameras;

/// <summary>
/// One virtual camera over the game's single orbit camera — Brio's model.
/// While LIVE every orbit property routes straight to the native camera;
/// while parked the same property is retained state, written back in one
/// save/load exchange when the live camera switches. The service owns the
/// native pointer and the delimit bookkeeping, because there is exactly one
/// native camera however many virtual ones stand over it.
/// </summary>
internal sealed unsafe class VirtualCamera : IVirtualCamera
{
    private readonly VirtualCameraService _service;

    // Parked state. While live these fields are STALE by design — the native
    // camera is the truth, and SaveState refreshes them at deactivation.
    private Vector2 _angle;
    private Vector2 _pan;
    private float _roll;
    private float _zoom = 2.5f;
    private float _fov;
    private Vector2 _zoomLimits = new(1.5f, 20f);
    private Vector3 _lastWorldPosition;

    internal VirtualCamera(
        VirtualCameraService service, CameraKind kind, bool isDefault)
    {
        _service = service;
        Kind = kind;
        IsDefault = isDefault;
    }

    public bool IsValid { get; internal set; } = true;

    public string Name { get; set; } = "Camera";

    public CameraKind Kind { get; }

    public bool IsLive { get; internal set; }

    public bool IsDefault { get; }

    public bool IsLocked { get; set; }

    private NativeCamera* Live => IsLive ? _service.Native : null;

    // ── orbit state ──────────────────────────────────────────────────────

    // UI writes land during draw — AFTER the game's camera update already
    // ran this frame, where the update can normalize or re-derive them away
    // before they ever render. Each live write is therefore also queued and
    // re-asserted once inside the camera-update detour (the phase Brio's
    // position writes render from), so a drag reads back what it wrote.
    internal Vector2? PendingAngle;
    internal Vector2? PendingPan;
    internal float? PendingRoll;
    internal float? PendingZoom;
    internal float? PendingFoV;

    public Vector2 Angle
    {
        get { var native = Live; return native != null ? native->Angle : _angle; }
        set
        {
            var native = Live;
            if (native != null)
            {
                native->Angle = value;
                PendingAngle = value;
            }
            _angle = value;
        }
    }

    public Vector2 Pan
    {
        get { var native = Live; return native != null ? native->Pan : _pan; }
        set
        {
            var native = Live;
            if (native != null)
            {
                native->Pan = value;
                PendingPan = value;
            }
            _pan = value;
        }
    }

    public float Roll
    {
        get { var native = Live; return native != null ? native->Roll : _roll; }
        set
        {
            var native = Live;
            if (native != null)
            {
                native->Roll = value;
                PendingRoll = value;
            }
            _roll = value;
        }
    }

    public float Zoom
    {
        get { var native = Live; return native != null ? native->Distance : _zoom; }
        set
        {
            var native = Live;
            if (native != null)
            {
                native->Distance = value;
                PendingZoom = value;
            }
            _zoom = value;
        }
    }

    public Vector2 ZoomLimits
    {
        get
        {
            var native = Live;
            return native != null
                ? new Vector2(native->MinDistance, native->MaxDistance)
                : _zoomLimits;
        }
    }

    public float FoV
    {
        get { var native = Live; return native != null ? native->Zoom : _fov; }
        set
        {
            var native = Live;
            if (native != null)
            {
                native->Zoom = value;
                PendingFoV = value;
            }
            _fov = value;
        }
    }

    public Vector3 PositionOffset { get; set; }

    public Vector3 TargetOffset { get; set; }

    public string TargetActorName { get; set; } = string.Empty;

    /// <summary>The native reference retained for per-frame follow updates.
    /// Exact target operations require this reference and its id to agree.</summary>
    public IActor? TargetActor { get; internal set; }

    public ActorId? TargetActorId { get; set; }

    public bool IsTargetLocked { get; set; }

    public Vector3 WorldPosition
    {
        get
        {
            if (Kind == CameraKind.Free)
                return Position;
            var native = Live;
            if (native != null)
                return native->GetPosition();
            // Parked and pinned: the pin is where this camera WILL be, which
            // is the only honest answer while the native camera is somebody
            // else's. The offset rides on top exactly as the detour applies it.
            if (FixedPosition is { } pinned)
                return pinned + PositionOffset + TargetOffset;
            return _lastWorldPosition;
        }
    }

    public Vector3? FixedPosition { get; set; }

    public bool DisableCollision { get; set; }

    private bool _delimit;

    public bool DelimitCamera
    {
        get => _delimit;
        set
        {
            _delimit = value;
            if (IsLive)
                _service.ApplyDelimit(value);
        }
    }

    public bool IsPortraitMode { get; private set; }

    public void TogglePortraitMode()
    {
        IsPortraitMode = !IsPortraitMode;
        Roll += IsPortraitMode ? MathF.PI / 2f : -MathF.PI / 2f;
    }

    // ── free-cam state ───────────────────────────────────────────────────

    public Vector3 Position { get; set; }

    public Vector3 SpawnPosition { get; internal set; }

    public Vector3 Rotation { get; set; }

    public bool MovementEnabled { get; set; } = true;

    public bool Move2D { get; set; }

    public float MovementSpeed { get; set; } = DefaultSpeed;

    public float MouseSensitivity { get; set; } = DefaultSensitivity;

    /// <summary>The configured starting fly speed, clamped to the range the
    /// Speed row can show back — a hand-edited config must not create a
    /// camera whose speed its own slider cannot reach.</summary>
    private static float DefaultSpeed => Math.Clamp(
        VirtualCameraService.CameraSettings.DefaultMovementSpeed,
        FreeCameraSpeed.Minimum,
        FreeCameraSpeed.Maximum);

    /// <summary>The configured starting look sensitivity, clamped to the
    /// Sensitivity row's range for the same reason.</summary>
    private static float DefaultSensitivity => Math.Clamp(
        VirtualCameraService.CameraSettings.DefaultMouseSensitivity,
        0.001f,
        0.2f);

    public bool DelimitAngle { get; set; }

    // ── projection / tracking ────────────────────────────────────────────

    private bool _orthographic;

    public bool Orthographic
    {
        get => _orthographic;
        set
        {
            _orthographic = value;
            if (IsLive)
                _service.ApplyOrthographic(value, OrthographicZoom);
        }
    }

    public float OrthographicZoom { get; set; } = 10f;

    public bool IsTracking { get; set; }

    public CameraTrackingMode TrackingMode { get; set; } =
        CameraTrackingMode.None;

    public IList<IBone> TrackedBones { get; } = new List<IBone>();

    // ── live switch ──────────────────────────────────────────────────────

    /// <summary>Copies the native camera into the parked fields — the last
    /// step of being live.</summary>
    internal void SaveState()
    {
        var native = _service.Native;
        if (native == null)
            return;
        _angle = native->Angle;
        _pan = native->Pan;
        _roll = native->Roll;
        _zoom = native->Distance;
        _fov = native->Zoom;
        _zoomLimits = new Vector2(native->MinDistance, native->MaxDistance);
        _lastWorldPosition = native->GetPosition();
    }

    /// <summary>Writes the parked fields onto the native camera — the first
    /// step of becoming live. Delimit and orthographic are re-asserted from
    /// their flags because both live on the ONE native camera and the
    /// previous occupant may have left them differently. Every field is also
    /// queued for the detour re-assert: a live switch happens at draw time,
    /// the same wrong phase a UI write does.</summary>
    internal void LoadState()
    {
        var native = _service.Native;
        if (native == null)
            return;
        native->Angle = _angle;
        native->Pan = _pan;
        native->Roll = _roll;
        native->Distance = _zoom;
        native->Zoom = _fov;
        PendingAngle = _angle;
        PendingPan = _pan;
        PendingRoll = _roll;
        PendingZoom = _zoom;
        PendingFoV = _fov;
        _service.ApplyDelimit(_delimit);
        _service.ApplyOrthographic(_orthographic, OrthographicZoom);
    }

    /// <summary>Seeds a free camera from the current view: real position, and
    /// the orbit rotation so the first frame looks the same way (Brio's
    /// ToFreeCam + rotation carry-over). The position comes off the rendered
    /// view matrix, so seeding from a live FREE camera carries where the shot
    /// actually is instead of the orbit position it left behind.</summary>
    internal void SeedFreeCam()
    {
        var native = _service.Native;
        if (native == null)
            return;
        Position = native->GetPosition();
        SpawnPosition = Position;
        Rotation = native->RotationAsVector3;
    }

    public void ResetProperties()
    {
        PositionOffset = Vector3.Zero;
        FixedPosition = null;
        TargetOffset = Vector3.Zero;
        TargetActorName = string.Empty;
        TargetActor = null;
        TargetActorId = null;
        IsTargetLocked = false;
        DisableCollision = false;
        DelimitCamera = false;
        IsPortraitMode = false;
        Roll = 0f;
        Zoom = 2.5f;
        FoV = 0f;
        Angle = Vector2.Zero;
        Pan = Vector2.Zero;
        Orthographic = false;
        OrthographicZoom = 10f;
        MovementSpeed = DefaultSpeed;
        MouseSensitivity = DefaultSensitivity;
    }
}
