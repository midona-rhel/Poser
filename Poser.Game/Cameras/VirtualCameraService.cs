using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;

using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;

namespace Poser.Game.Cameras;

/// <summary>
/// Brio's virtual-camera overlay, whole: N virtual cameras save/restore state
/// onto the game's one orbit camera, a position/target offset is re-applied
/// inside the native camera update, collision and zoom limits are lifted per
/// camera, and a free camera replaces the view matrix outright, fed by the
/// game's own input handler. Ktisis's two exclusives are grafted on the same
/// live camera: orthographic projection (render-camera fields) and bone
/// tracking (the look-position hook steering the pivot at averaged bone
/// positions). GPose-scoped: entering mints the default camera, leaving
/// restores the native state and destroys everything.
/// </summary>
public sealed unsafe class VirtualCameraService : IVirtualCameraService
{
    // One home for the fly speed's numbers: the wheel's curve owns them and
    // the camera's default is that curve's unit. These are the FLOOR the
    // configured defaults fall back to, not the defaults themselves — see
    // CameraSettings.
    internal const float DefaultMovementSpeed = FreeCameraSpeed.Default;
    internal const float DefaultMouseSensitivity = 0.1f;

    /// <summary>
    /// The user's camera settings, or shipped defaults when no configuration
    /// service exists yet — the test constructor stands this service up
    /// without one, and the input detour is a game callback that must not
    /// depend on plugin start order. Read per use rather than cached: a
    /// settings save takes effect on the next frame, not the next GPose
    /// session.
    /// </summary>
    internal static Config.CameraConfiguration CameraSettings =>
        Config.ConfigurationService.Instance is { } service
            ? service.Config.Camera
            : FallbackCameraSettings;

    private static readonly Config.CameraConfiguration
        FallbackCameraSettings = new();

    // Brio signatures, verbatim from main (2026-08).
    private const string CameraUpdateSignature =
        "40 55 53 57 48 8D 6C 24 A0 48 81 EC ?? ?? ?? ?? 48 8B 1D";
    private const string CameraCollisionSignature =
        "E8 ?? ?? ?? ?? 4C 8D 44 24 40 89 83 14 ?? ?? ??";
    private const string CameraSceneUpdateSignature =
        "48 ?? ?? ?? ?? ?? 48 81 EC ?? ?? ?? ?? F6 81 F0 ?? ?? ?? ?? 48 8B ??";
    private const string CameraMatrixLoadSignature =
        "E8 ?? ?? ?? ?? 48 8B 93 90 02 ?? ?? 48 8D 4C 24 40";
    private const string HandleInputSignature =
        "E8 ?? ?? ?? ?? ?? 8B ?? ?? ?? ?? 8B 87 ?? ?? ?? ?? 89 45";

    // Ktisis signature, verbatim from main (2026-08): the function that
    // derives the orbit look-at, hooked for bone tracking.
    private const string CalculateLookPositionSignature =
        "E8 ?? ?? ?? ?? F3 0F 10 64 24 ?? F3 0F 10 0D ?? ?? ?? ??";

    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGPoseService _gPose;
    private readonly IEventBus _events;

    private delegate nint CameraUpdateDelegate(NativeCamera* camera);
    private delegate nint CameraCollisionDelegate(
        NativeCamera* camera, Vector3* a2, Vector3* a3, float a4, nint a5, float a6);
    private delegate nint CameraSceneUpdateDelegate(SceneCamera* camera);
    private delegate void CameraMatrixLoadDelegate(RenderCamera* camera, nint matrix);
    private delegate void HandleInputDelegate(
        nint a1, nint a2, nint a3, MouseFrame* mouse, KeyboardFrame* keyboard);
    private delegate float* CalculateLookPositionDelegate(
        NativeCamera* camera, float* lookAt, float* position, byte mode);

    private readonly Hook<CameraUpdateDelegate>? _cameraUpdateHook;
    private readonly Hook<CameraCollisionDelegate>? _cameraCollisionHook;
    private readonly Hook<CameraSceneUpdateDelegate>? _cameraSceneUpdateHook;
    private readonly Hook<HandleInputDelegate>? _handleInputHook;
    private readonly Hook<CalculateLookPositionDelegate>? _lookPositionHook;
    private readonly CameraMatrixLoadDelegate? _cameraMatrixLoad;

    private readonly List<VirtualCamera> _cameras = new();
    private VirtualCamera? _live;

    /// <summary>The game's own limits before a delimit lifted them; one set,
    /// because there is one native camera.</summary>
    private (Vector2 Distance, float YMin, float YMax)? _originalLimits;

    // Free-cam frame inputs, written by the input detour, consumed by the
    // scene-update detour (Brio's _forward/_lastMousePosition pair).
    private Vector3 _freeForward;
    /// <summary>Where the tracked pivot was last frame: a free camera in
    /// Follow moves by the pivot's motion, not to the pivot.</summary>
    private Vector3? _freeFollowPivot;
    private Vector2 _freeMouseDelta;
    private float _freeMoveSpeed = DefaultMovementSpeed;

    /// <summary>Set per UI frame: while typing or an active ImGui item
    /// owns the keyboard, flight keys stand down (the modifier contract's
    /// focus rule). Written from the draw side, read on the camera's
    /// update — a one-frame lag is invisible.</summary>
    public bool SuppressFlightKeys { get; set; }

    // The last fly-speed change the wheel made, for the overlay's readout.
    // Two scalar fields rather than one notice struct because the writer is
    // the game's input handler and the reader is the draw pass: each field is
    // atomically read on its own, so the worst a reader can see is a speed one
    // notch behind its timestamp. A struct pair could tear. A zero stamp is
    // 'no notch yet'.
    private float _speedNoticeValue = DefaultMovementSpeed;
    private long _speedNoticeAtMs;

    // Tracking: the averaged bone world position is derived once per tick on
    // the framework thread (skeleton caches are refreshed there, exactly like
    // the light attach), and the hook only consumes the number.
    private Vector3? _trackedPivot;
    private readonly HashSet<Skeleton> _trackRefreshed = new();

    // Set when GPose was entered before the native camera manager was ready;
    // the per-tick handler retries the default-camera mint until it lands or
    // GPose ends. One pointer read per frame, no scans, no new subscriptions.
    private bool _defaultCameraPending;

    // Test seam: replaces the CameraManager singleton read so the retry
    // policy is drivable without the game. Null in production.
    private readonly Func<nint>? _nativeCameraOverride;

    // Null only in the test ctor (no native actors exist there); every
    // actor-address deref resolves through it first — a raw IActor address
    // is a claim, not a proof (WorldActorDiscovery standard).
    private readonly Dalamud.Plugin.Services.IObjectTable? _objectTable;

    private bool _disposed;

    public VirtualCameraService(
        ISigScanner sigScanner,
        IGameInteropProvider hooks,
        IFramework framework,
        IPluginLog log,
        IGPoseService gPose,
        IEventBus events,
        Dalamud.Plugin.Services.IObjectTable objectTable)
    {
        _log = log;
        _framework = framework;
        _gPose = gPose;
        _events = events;
        _objectTable = objectTable;

        Hook<T>? TryHook<T>(string name, string signature, T detour)
            where T : Delegate
        {
            try
            {
                var hook = hooks.HookFromAddress<T>(
                    sigScanner.ScanText(signature), detour);
                hook.Enable();
                return hook;
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"VirtualCameraService: '{name}' unavailable: {ex.Message}");
                return null;
            }
        }

        _cameraUpdateHook = TryHook<CameraUpdateDelegate>(
            "camera update", CameraUpdateSignature, CameraUpdateDetour);
        IsAvailable = _cameraUpdateHook != null;

        _cameraCollisionHook = TryHook<CameraCollisionDelegate>(
            "camera collision", CameraCollisionSignature, CameraCollisionDetour);
        _cameraSceneUpdateHook = TryHook<CameraSceneUpdateDelegate>(
            "scene update", CameraSceneUpdateSignature, CameraSceneUpdateDetour);
        _handleInputHook = TryHook<HandleInputDelegate>(
            "input handler", HandleInputSignature, HandleInputDetour);
        _lookPositionHook = TryHook<CalculateLookPositionDelegate>(
            "look position", CalculateLookPositionSignature,
            CalculateLookPositionDetour);

        try
        {
            _cameraMatrixLoad = System.Runtime.InteropServices.Marshal
                .GetDelegateForFunctionPointer<CameraMatrixLoadDelegate>(
                    sigScanner.ScanText(CameraMatrixLoadSignature));
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"VirtualCameraService: matrix load unavailable, free cameras disabled: {ex.Message}");
        }

        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>Test ctor: no signature scans, no hooks; availability and the
    /// native camera presence are supplied so the default-camera retry policy
    /// runs its production path without the game.</summary>
    internal VirtualCameraService(
        IFramework framework,
        IPluginLog log,
        IGPoseService gPose,
        IEventBus events,
        Func<nint> nativeCameraOverride,
        bool isAvailable)
    {
        _log = log;
        _framework = framework;
        _gPose = gPose;
        _events = events;
        _nativeCameraOverride = nativeCameraOverride;
        IsAvailable = isAvailable;

        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    public bool IsAvailable { get; }

    public IReadOnlyList<IVirtualCamera> Cameras => _cameras;

    public IVirtualCamera? LiveCamera => _live;

    public FreeCameraSpeedNotice? SpeedNotice =>
        _live is { Kind: CameraKind.Free } && _speedNoticeAtMs != 0L
            ? new FreeCameraSpeedNotice(_speedNoticeValue, _speedNoticeAtMs)
            : null;

    /// <summary>The native orbit camera; null when the manager is not up.</summary>
    internal NativeCamera* Native
    {
        get
        {
            if (_nativeCameraOverride is { } custom)
                return (NativeCamera*)custom();
            var manager = CameraManager.Instance();
            if (manager == null)
                return null;
            return (NativeCamera*)manager->GetActiveCamera();
        }
    }

    // ── camera management ────────────────────────────────────────────────

    public IVirtualCamera? CreateCamera(CameraKind kind)
    {
        if (!IsAvailable || !_gPose.IsGPosing || Native == null)
            return null;
        // A free camera without the matrix-load call would freeze the view.
        if (kind == CameraKind.Free &&
            (_cameraMatrixLoad == null || _cameraSceneUpdateHook == null))
            return null;

        var camera = new VirtualCamera(this, kind, isDefault: false)
        {
            Name = NextName(kind),
        };
        // Seed from the current view so switching to the new camera does not
        // jump: orbit cameras copy the live state, free cameras take the real
        // position and the current look rotation.
        _live?.SaveState();
        camera.SaveState();
        if (kind == CameraKind.Free)
            camera.SeedFreeCam();
        camera.CaptureOwnedDefaults();

        _cameras.Add(camera);
        SetLive(camera);
        return camera;
    }

    public IVirtualCamera? CloneCamera(IVirtualCamera source)
    {
        if (source is not VirtualCamera original ||
            !_cameras.Contains(original) || !_gPose.IsGPosing)
            return null;

        // The live original's parked fields are stale by design; refresh them
        // so the clone copies what is on screen.
        if (original.IsLive)
            original.SaveState();

        var clone = new VirtualCamera(this, original.Kind, isDefault: false)
        {
            Name = NextName(original.Kind),
            PositionOffset = original.PositionOffset,
            TargetOffset = original.TargetOffset,
            TargetActorName = original.TargetActorName,
            TargetActor = original.TargetActor,
            TargetActorId = original.TargetActorId,
            IsTargetLocked = original.IsTargetLocked,
            DisableCollision = original.DisableCollision,
            Position = original.Position,
            SpawnPosition = original.SpawnPosition,
            Rotation = original.Rotation,
            MovementEnabled = original.MovementEnabled,
            Move2D = original.Move2D,
            MovementSpeed = original.MovementSpeed,
            MouseSensitivity = original.MouseSensitivity,
            DelimitAngle = original.DelimitAngle,
            OrthographicZoom = original.OrthographicZoom,
        };
        clone.Angle = original.Angle;
        clone.Pan = original.Pan;
        clone.Roll = original.Roll;
        clone.Zoom = original.Zoom;
        clone.FoV = original.FoV;
        clone.DelimitCamera = original.DelimitCamera;
        clone.Orthographic = original.Orthographic;
        clone.IsLocked = original.IsLocked;
        clone.IsTracking = original.IsTracking;
        clone.TrackingMode = original.TrackingMode;
        foreach (var bone in original.TrackedBones)
            clone.TrackedBones.Add(bone);
        clone.CaptureOwnedDefaults();

        _cameras.Add(clone);
        SetLive(clone);
        return clone;
    }

    public void DestroyCamera(IVirtualCamera camera)
    {
        if (camera is not VirtualCamera target ||
            target.IsDefault ||
            !_cameras.Remove(target))
            return;

        if (_live == target)
        {
            _live = null;
            target.IsLive = false;
            var fallback = _cameras.Find(candidate => candidate.IsDefault);
            if (fallback != null)
                SetLive(fallback);
            else
                RestoreNativeOverrides();
        }

        target.IsValid = false;
        Publish();
    }

    public void DestroyAllCameras()
    {
        foreach (var camera in _cameras.ToArray())
        {
            if (!camera.IsDefault)
                DestroyCamera(camera);
        }
    }

    public void SetLive(IVirtualCamera camera)
    {
        if (camera is not VirtualCamera target ||
            !_cameras.Contains(target) ||
            _live == target ||
            Native == null)
            return;

        if (_live is { } outgoing)
        {
            outgoing.SaveState();
            outgoing.IsLive = false;
        }

        _live = target;
        target.IsLive = true;
        target.LoadState();
        // The readout belongs to the camera whose wheel made it; a switch
        // retires it rather than letting the incoming camera inherit a speed
        // it was never set to.
        _speedNoticeAtMs = 0L;
        Publish();
    }

    public bool SetTargetActor(
        IVirtualCamera camera, IActor actor, ActorId actorId,
        string displayName)
    {
        if (camera is not VirtualCamera target || actor.Address == nint.Zero)
            return false;
        // Deref-time revalidation: the stored actor address is only a claim;
        // the deref goes through the object-table-resolved wrapper.
        var resolved = _objectTable?.CreateObjectReference(actor.Address);
        if (resolved == null || !resolved.IsValid())
            return false;
        var gameObject = (GameObject*)resolved.Address;
        var drawObject = gameObject->DrawObject;
        if (drawObject == null)
            return false;
        Vector3 drawPosition = drawObject->Object.Position;
        Vector3 objectPosition = resolved.Position;
        target.TargetOffset = drawPosition - objectPosition;
        target.TargetActorName = displayName;
        target.TargetActor = actor;
        target.TargetActorId = actorId;
        return true;
    }

    public void ClearTargetActor(IVirtualCamera camera)
    {
        if (camera is not VirtualCamera target)
            return;
        target.TargetOffset = Vector3.Zero;
        target.TargetActorName = string.Empty;
        target.TargetActor = null;
        target.TargetActorId = null;
        target.IsTargetLocked = false;
    }

    /// <summary>Centers the current live orbit camera on the actor's drawn
    /// mid-body pivot while retaining its orientation. Validation completes
    /// before the first camera setter: stale, hidden, or undrawn actors leave
    /// the shot untouched.</summary>
    public CameraCenterResult CenterOnActor(IActor actor)
    {
        if (!IsAvailable)
            return CameraCenterResult.Refused("Center: the camera is unavailable.");
        if (actor.Address == nint.Zero ||
            _objectTable?.CreateObjectReference(actor.Address) is not { } resolved ||
            !resolved.IsValid())
            return CameraCenterResult.Refused("Center: that actor is no longer available.");

        var gameObject = (GameObject*)resolved.Address;
        var drawObject = gameObject->DrawObject;
        if (!gameObject->IsReadyToDraw() ||
            drawObject == null || !drawObject->IsVisible)
            return CameraCenterResult.Refused("Center: that actor is not drawn yet.");

        var native = Native;
        if (!_gPose.IsGPosing || native == null ||
            _live is not { IsLive: true } camera)
            return CameraCenterResult.Refused("Center: the game camera is not ready.");
        if (camera.Kind == CameraKind.Free)
            return CameraCenterResult.Refused("Center: switch from the free camera first.");
        if (camera.IsLocked)
            return CameraCenterResult.Refused("Center: unlock the camera first.");
        if (camera.FixedPosition != null)
            return CameraCenterResult.Refused("Center: clear the camera position pin first.");

        Vector3 drawOrigin = drawObject->Object.Position;
        float reportedHeight = MathF.Abs(gameObject->CameraOffset.Y);
        // CameraOffset is the client's character-aware framing measure. Some
        // non-character draw objects report zero, so use a conservative human
        // height rather than aiming at their feet or producing zero zoom.
        float height = reportedHeight is >= 0.5f and <= 5f
            ? reportedHeight
            : 1.7f;
        Vector3 pivot = drawOrigin + Vector3.UnitY * (height * 0.5f);
        var scene = &native->Camera.CameraBase.SceneCamera;
        Vector3 baseLookAt = scene->LookAtVector;
        Vector2 zoomLimits = camera.ZoomLimits;
        if (!IsFinite(pivot) || !IsFinite(baseLookAt) ||
            !float.IsFinite(zoomLimits.X) || !float.IsFinite(zoomLimits.Y) ||
            zoomLimits.X > zoomLimits.Y)
            return CameraCenterResult.Refused("Center: no usable actor or camera pivot.");

        // The UI runs after the camera-update detour, so LookAtVector already
        // includes the current position/target offsets. Add only the delta
        // from that effective pivot; TargetOffset stays untouched and the
        // existing follow relationship remains exactly as it was.
        camera.PositionOffset += pivot - baseLookAt;
        camera.Zoom = Math.Clamp(height * 2f, zoomLimits.X, zoomLimits.Y);
        return CameraCenterResult.Centered();
    }

    /// <summary>Centers on the selected bone's live model-space transform.
    /// The skeleton cache and object-table draw checks happen before either
    /// camera setter so a replaced or undrawn identity cannot move the shot.
    /// </summary>
    public CameraCenterResult CenterOnBone(IBone bone)
    {
        if (!IsAvailable)
            return CameraCenterResult.Refused("Center: the camera is unavailable.");
        if (bone.Skeleton is not Skeleton skeleton || !skeleton.IsValid)
            return CameraCenterResult.Refused("Center: that bone is no longer available.");

        var actor = skeleton.Actor;
        if (actor.Address == nint.Zero ||
            _objectTable?.CreateObjectReference(actor.Address) is not { } resolved ||
            !resolved.IsValid())
            return CameraCenterResult.Refused("Center: that actor is no longer available.");
        var gameObject = (GameObject*)resolved.Address;
        var drawObject = gameObject->DrawObject;
        if (!gameObject->IsReadyToDraw() || drawObject == null ||
            !drawObject->IsVisible)
            return CameraCenterResult.Refused("Center: that bone is not drawn yet.");

        var native = Native;
        if (!_gPose.IsGPosing || native == null ||
            _live is not { IsLive: true } camera)
            return CameraCenterResult.Refused("Center: the game camera is not ready.");
        if (camera.Kind == CameraKind.Free)
            return CameraCenterResult.Refused("Center: switch from the free camera first.");
        if (camera.IsLocked)
            return CameraCenterResult.Refused("Center: unlock the camera first.");
        if (camera.FixedPosition != null)
            return CameraCenterResult.Refused("Center: clear the camera position pin first.");

        skeleton.UpdateBoneTransforms(BoneCacheTypes.LastTransform);
        var world = Poser.Transform.FromMatrix(
            bone.LastTransform.ToMatrix() * skeleton.GetModelMatrix());
        float reportedHeight = MathF.Abs(gameObject->CameraOffset.Y);
        float actorHeight = reportedHeight is >= 0.5f and <= 5f
            ? reportedHeight
            : 1.7f;
        // A bone is a point rather than a body; a quarter body height gives a
        // useful Ktisis-like close framing without changing the view angles.
        float framingHeight = Math.Clamp(actorHeight * 0.25f, 0.5f, 1.5f);
        var pivot = world.Position;
        var scene = &native->Camera.CameraBase.SceneCamera;
        Vector3 baseLookAt = scene->LookAtVector;
        Vector2 zoomLimits = camera.ZoomLimits;
        if (!IsFinite(pivot) || !IsFinite(baseLookAt) ||
            !float.IsFinite(zoomLimits.X) || !float.IsFinite(zoomLimits.Y) ||
            zoomLimits.X > zoomLimits.Y)
            return CameraCenterResult.Refused("Center: no usable bone or camera pivot.");

        camera.PositionOffset += pivot - baseLookAt;
        camera.Zoom = Math.Clamp(framingHeight * 2f, zoomLimits.X, zoomLimits.Y);
        return CameraCenterResult.Centered();
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);

    /// <summary>The spawned camera's default name. Bare number, no "#": every
    /// other numbered entity in the scene (lights, props) is named
    /// "{stem} {n}", and one family wearing a hash read as a different sort of
    /// thing (user 2026-08-14). Nothing parses the number back out and scene
    /// documents store the display name verbatim, so older saves keep their
    /// hashed names and load unchanged.</summary>
    private string NextName(CameraKind kind)
    {
        string stem = kind == CameraKind.Free ? "Free camera" : "Camera";
        for (int i = 1; i <= 100; i++)
        {
            string name = $"{stem} {i}";
            if (!_cameras.Exists(camera =>
                    string.Equals(camera.Name, name, StringComparison.Ordinal)))
                return name;
        }
        return stem;
    }

    private void Publish() =>
        _events.Publish(new CameraListChangedEvent(Cameras));

    // ── native override plumbing ─────────────────────────────────────────

    /// <summary>Brio's delimit (distance 0–500) plus Ktisis's vertical-clamp
    /// loosening, over ONE saved original set.</summary>
    internal void ApplyDelimit(bool delimit)
    {
        var native = Native;
        if (native == null)
            return;

        if (delimit)
        {
            _originalLimits ??= (
                new Vector2(native->MinDistance, native->MaxDistance),
                native->YMin,
                native->YMax);
            native->MinDistance = 0f;
            native->MaxDistance = 500f;
            native->YMin = 1.5f;
            native->YMax = -1.5f;
            return;
        }

        if (_originalLimits is not { } original)
            return;
        native->MinDistance = original.Distance.X;
        native->MaxDistance = original.Distance.Y;
        native->YMin = original.YMin;
        native->YMax = original.YMax;
        if (native->Distance < native->MinDistance)
            native->Distance = native->MinDistance;
        _originalLimits = null;
    }

    /// <summary>Ktisis's orthographic switch on the render camera.</summary>
    internal void ApplyOrthographic(bool enabled, float zoom)
    {
        var native = Native;
        if (native == null)
            return;
        var render =
            (RenderCameraEx*)native->Camera.CameraBase.SceneCamera.RenderCamera;
        if (render == null)
            return;
        render->OrthographicEnabled = enabled;
        render->OrthographicZoom = enabled ? zoom : 10f;
    }

    /// <summary>Puts the native camera back to its own state — the last live
    /// camera's overrides all cleared.</summary>
    private void RestoreNativeOverrides()
    {
        ApplyDelimit(false);
        ApplyOrthographic(false, 10f);
    }

    // ── hooks ────────────────────────────────────────────────────────────

    /// <summary>Brio's CameraUpdateDetour: the position/target offset is
    /// added after the game has computed the frame's camera, and the look-at
    /// moves with it so the view direction survives.</summary>
    private nint CameraUpdateDetour(NativeCamera* camera)
    {
        var result = _cameraUpdateHook!.Original(camera);
        try
        {
            if (!_gPose.IsGPosing || _live is not { } live)
                return result;

            // UI-written orbit values, re-asserted AFTER the game's update:
            // a draw-time write lands after this frame's update already ran,
            // where the update's own normalization can eat it before it ever
            // renders (the horizontal angle especially). Each write applies
            // once — the mouse orbit is never fought.
            if (live.PendingAngle is { } pendingAngle)
            {
                camera->Angle = pendingAngle;
                live.PendingAngle = null;
            }
            if (live.PendingPan is { } pendingPan)
            {
                camera->Pan = pendingPan;
                live.PendingPan = null;
            }
            if (live.PendingRoll is { } pendingRoll)
            {
                camera->Roll = pendingRoll;
                live.PendingRoll = null;
            }
            if (live.PendingZoom is { } pendingZoom)
            {
                camera->Distance = pendingZoom;
                live.PendingZoom = null;
            }
            if (live.PendingFoV is { } pendingFoV)
            {
                camera->Zoom = pendingFoV;
                live.PendingFoV = null;
            }

            if (live.Kind != CameraKind.Free)
            {
                var offset = live.PositionOffset + live.TargetOffset;
                // Ktisis's WritePosition: a pinned camera measures its offset
                // from the PIN instead of from wherever the game's update
                // left it, which is what stops the shot drifting when the
                // subject walks. Unpinned, this is the offset-only path it
                // has always been — and offset-free AND unpinned still costs
                // nothing.
                if (offset != Vector3.Zero || live.FixedPosition != null)
                {
                    var scene = &camera->Camera.CameraBase.SceneCamera;
                    Vector3 current = scene->Position;
                    var moved = (live.FixedPosition ?? current) + offset;
                    if (moved != current)
                    {
                        Vector3 lookAt = scene->LookAtVector;
                        scene->Position = moved;
                        scene->LookAtVector = lookAt + (moved - current);
                    }
                }
            }

            // Ktisis re-asserts the ortho zoom every write; the game resets
            // it when the render camera rebuilds.
            if (live.Orthographic)
                ApplyOrthographic(true, live.OrthographicZoom);
        }
        catch (Exception ex)
        {
            _log.Error($"VirtualCameraService: camera update failed: {ex}");
        }
        return result;
    }

    /// <summary>Brio's collision detour: with collision disabled the collide
    /// distance is pushed to the zoom ceiling and the game's probe skipped.
    /// </summary>
    private nint CameraCollisionDetour(
        NativeCamera* camera, Vector3* a2, Vector3* a3, float a4, nint a5, float a6)
    {
        if (_gPose.IsGPosing &&
            _live is { DisableCollision: true, Kind: not CameraKind.Free })
        {
            camera->Collide = new Vector2(camera->MaxDistance);
            return 0;
        }
        return _cameraCollisionHook!.Original(camera, a2, a3, a4, a5, a6);
    }

    /// <summary>Whether the scene-update detour is live — the frame slot
    /// between the game's world update and the render, which other
    /// systems (the world-object animation anchor) borrow through
    /// <see cref="AfterSceneUpdate"/>.</summary>
    public bool SceneUpdateHookLive => _cameraSceneUpdateHook != null;

    /// <summary>Runs every frame inside the scene-update detour, after
    /// the game's own pass: writes made here land before the render
    /// consumes them — the one slot where a per-frame transform write
    /// neither flickers nor lags a frame.</summary>
    public Action? AfterSceneUpdate;

    /// <summary>Brio's scene-update detour: while a free camera is live the
    /// frame's view matrix is replaced with the fly-cam's and loaded into the
    /// render camera.</summary>
    private nint CameraSceneUpdateDetour(SceneCamera* camera)
    {
        var result = _cameraSceneUpdateHook!.Original(camera);
        try
        {
            AfterSceneUpdate?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error(
                $"VirtualCameraService: an AfterSceneUpdate borrower failed: {ex}");
        }
        try
        {
            if (!_gPose.IsGPosing ||
                _live is not { Kind: CameraKind.Free } live ||
                _cameraMatrixLoad == null)
                return result;

            camera->ViewMatrix = UpdateFreeCamera(live);
            var native = Native;
            if (native != null)
                _cameraMatrixLoad(
                    native->Camera.CameraBase.SceneCamera.RenderCamera,
                    (nint)(&camera->ViewMatrix));
        }
        catch (Exception ex)
        {
            _log.Error($"VirtualCameraService: free camera update failed: {ex}");
        }
        return result;
    }

    /// <summary>Brio's input detour, whole: a live free camera eats the
    /// movement keys and the right-drag look, and a locked live camera of
    /// ANY kind eats the game's camera input outright — the orbit drag, the
    /// scroll zoom, and the movement keys — so nothing the game reads can
    /// move the shot.</summary>
    private void HandleInputDetour(
        nint a1, nint a2, nint a3, MouseFrame* mouse, KeyboardFrame* keyboard)
    {
        _handleInputHook!.Original(a1, a2, a3, mouse, keyboard);
        try
        {
            if (!_gPose.IsGPosing || _live is not { } live)
                return;

            // Null-checked BEFORE the deref: a null singleton here is an
            // AccessViolationException, which .NET never delivers to the
            // catch below — it would be a process crash inside the game's
            // input handler. Every other singleton read in this file goes
            // through the null-checking Native property.
            var atk = RaptureAtkModule.Instance();
            if (atk == null || atk->AtkModule.IsTextInputActive())
                return;

            // Brio's EnableConsumeAllInput, opt-in: the whole frame of keys
            // goes, except Escape and Return. Those two are how a user leaves
            // a game dialog, and a plugin that swallows them strands them.
            if (keyboard != null && CameraSettings.ConsumeAllGameInput)
            {
                for (int i = 0; i < KeyboardFrame.KeyStateLength; i++)
                {
                    if (i == (int)VirtualKey.ESCAPE || i == (int)VirtualKey.RETURN)
                        continue;
                    keyboard->KeyState[i] = 0;
                }
            }

            if (live.Kind == CameraKind.Free)
                HandleFreeCameraInput(live, mouse, keyboard);

            // Brio's full lock (GameInputService): the locked camera's frame
            // of input is consumed after any freecam bookkeeping, whatever
            // the camera kind.
            if (live.IsLocked)
            {
                if (mouse != null)
                {
                    mouse->HandleDelta();
                    mouse->ScrollValue = 0;
                }
                if (keyboard != null)
                {
                    keyboard->HandleKey(VirtualKey.W);
                    keyboard->HandleKey(VirtualKey.A);
                    keyboard->HandleKey(VirtualKey.S);
                    keyboard->HandleKey(VirtualKey.D);
                    keyboard->HandleKey(VirtualKey.Q);
                    keyboard->HandleKey(VirtualKey.E);
                    keyboard->HandleKey(VirtualKey.SPACE);
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"VirtualCameraService: input handling failed: {ex}");
        }
    }

    private void HandleFreeCameraInput(
        VirtualCamera live, MouseFrame* mouse, KeyboardFrame* keyboard)
    {
        // A locked camera holds its shot: the look-drag stops accumulating
        // (the lock block below eats the delta itself).
        if (!live.IsLocked &&
            mouse != null && mouse->IsButtonDown(MouseState.Right))
        {
            _freeMouseDelta += mouse->Delta;
            mouse->HandleDelta();
        }

        // The wheel is the fly speed, and the game never gets to see it. A
        // free camera replaces the view matrix and leaves the game orbiting
        // its own camera underneath, so an unconsumed scroll zooms a camera
        // nobody is looking through and desyncs the orbit state every reader
        // of the native camera still trusts. Consumed for a live free camera
        // whatever the wheel then does — locked, or movement switched off, it
        // is eaten and dropped.
        if (mouse != null)
        {
            if (!live.IsLocked && live.MovementEnabled)
            {
                int notches = FreeCameraSpeed.Notches(mouse->ScrollValue);
                if (notches != 0)
                {
                    live.MovementSpeed =
                        FreeCameraSpeed.Step(live.MovementSpeed, notches);
                    _speedNoticeValue = live.MovementSpeed;
                    // Fully qualified: Poser.Game.Environment is a namespace
                    // of this assembly and wins the plain name.
                    _speedNoticeAtMs = System.Environment.TickCount64;
                }
            }
            mouse->ScrollValue = 0;
        }

        // The UI owns the keyboard while anything is typing or an ImGui
        // item is active: flight keys stand down entirely — pressed keys
        // are neither moved on nor consumed (the modifier contract's
        // focus rule).
        if (keyboard == null || !live.MovementEnabled || SuppressFlightKeys)
            return;

        int forwardBack = 0;
        if (keyboard->KeyDown(VirtualKey.W)) forwardBack -= 1;
        if (keyboard->KeyDown(VirtualKey.S)) forwardBack += 1;

        int leftRight = 0;
        if (keyboard->KeyDown(VirtualKey.A)) leftRight -= 1;
        if (keyboard->KeyDown(VirtualKey.D)) leftRight += 1;

        // The modifier contract's vertical map: Q or Space rises, E or C
        // falls. Shift-descend (Brio's map) is DEAD — Shift is a speed
        // modifier now, and a modifier must never be a motion key. The
        // axis travels with the input vector below, so it is the camera's
        // up rather than the world's — pitched down, rising also carries
        // you forward. Move2D is the switch that pins it to world vertical.
        int upDown = 0;
        if (keyboard->KeyDown(VirtualKey.Q) ||
            keyboard->KeyDown(VirtualKey.SPACE))
            upDown += 1;
        if (keyboard->KeyDown(VirtualKey.E) ||
            keyboard->KeyDown(VirtualKey.C))
            upDown -= 1;

        // Shift = faster, Ctrl = slower — increase and decrease, in that
        // order. Alt carries NO camera role: it is the visibility peek,
        // everywhere and exclusively.
        var settings = CameraSettings;
        _freeMoveSpeed = live.MovementSpeed;
        if (keyboard->KeyDown(VirtualKey.SHIFT))
            _freeMoveSpeed = live.MovementSpeed * settings.FastMultiplier;
        else if (keyboard->KeyDown(VirtualKey.CONTROL))
            _freeMoveSpeed = live.MovementSpeed * settings.SlowMultiplier;

        keyboard->HandleKey(VirtualKey.W);
        keyboard->HandleKey(VirtualKey.A);
        keyboard->HandleKey(VirtualKey.S);
        keyboard->HandleKey(VirtualKey.D);
        keyboard->HandleKey(VirtualKey.Q);
        keyboard->HandleKey(VirtualKey.E);
        keyboard->HandleKey(VirtualKey.C);
        // The modifiers this path reads are consumed with the letters
        // (Brio's EnableKeyHandlingOnKeyMod block, on by default): Shift
        // and Ctrl are the speed modifiers, so leaving them in the frame
        // hands the game a held modifier for every second the camera
        // flies fast. Turning the setting off gives the game those back —
        // Space included, since Space is half the rise/fall pair. Alt is
        // NOT consumed: the camera no longer reads it.
        if (settings.ConsumeModifiersWhileFlying)
        {
            keyboard->HandleKey(VirtualKey.SPACE);
            keyboard->HandleKey(VirtualKey.SHIFT);
            keyboard->HandleKey(VirtualKey.CONTROL);
        }

        if (live.IsLocked)
        {
            _freeForward = Vector3.Zero;
            return;
        }

        // Brio's FlipKeyBindsPastNinety, applied to the same two axes it
        // does: rolled past a quarter turn the screen's left is the world's
        // right, so the key that moved you screen-left keeps doing so.
        if (settings.FlipBindsPastNinety &&
            MathF.Abs(live.Roll) > MathF.PI / 2f)
        {
            leftRight = -leftRight;
            upDown = -upDown;
        }

        var input = new Vector3(leftRight, upDown, forwardBack);
        if (live.IsPortraitMode)
            input = Vector3.Transform(
                input,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));

        // Brio's frame vector: the input rotated by the camera's yaw (and
        // pitch, unless lateral movement pins travel to the horizontal
        // plane).
        _freeForward = Vector3.Transform(
            input,
            Quaternion.CreateFromYawPitchRoll(
                live.Rotation.X,
                live.Move2D ? 0f : -live.Rotation.Y,
                live.Rotation.Z));
    }

    /// <summary>Brio's UpdateMatrix, whole: integrates the frame inputs into
    /// position/rotation and builds the fly-cam view matrix, roll applied as
    /// a Z-axis transform at the end.</summary>
    private Matrix4x4 UpdateFreeCamera(VirtualCamera live)
    {
        var mouse = _freeMouseDelta * live.MouseSensitivity * (MathF.PI / 180f);
        if (live.IsPortraitMode)
            mouse = new Vector2(-mouse.Y, mouse.X);

        var position = live.Position + _freeForward * _freeMoveSpeed;
        var rotation = live.Rotation;
        rotation.X -= mouse.X;
        rotation.Y = live.DelimitAngle
            ? rotation.Y + mouse.Y
            : Math.Clamp(rotation.Y + mouse.Y, -1.5f, 1.5f);
        // Tracking on a free camera: Follow carries the camera with the
        // pivot's motion, Pan turns it onto the pivot, both do both.
        if (live.IsTracking && _trackedPivot is { } pivot)
        {
            var mode = live.TrackingMode;
            if (mode is CameraTrackingMode.Follow or CameraTrackingMode.FollowAndPan
                && _freeFollowPivot is { } last)
                position += pivot - last;
            if (mode is CameraTrackingMode.Pan or CameraTrackingMode.FollowAndPan)
            {
                var toPivot = pivot - position;
                if (toPivot.LengthSquared() > 1e-6f)
                {
                    float flat = MathF.Sqrt(
                        toPivot.X * toPivot.X + toPivot.Z * toPivot.Z);
                    rotation.X = MathF.Atan2(toPivot.X, toPivot.Z);
                    rotation.Y = MathF.Atan2(toPivot.Y, flat);
                }
            }
            _freeFollowPivot = pivot;
        }
        else
            _freeFollowPivot = null;
        live.Position = position;
        live.Rotation = rotation;

        _freeMouseDelta = Vector2.Zero;
        _freeForward = Vector3.Zero;
        _freeMoveSpeed = live.MovementSpeed;

        var look = Vector3.Normalize(new Vector3(
            MathF.Sin(rotation.X) * MathF.Cos(rotation.Y),
            MathF.Sin(rotation.Y),
            MathF.Cos(rotation.X) * MathF.Cos(rotation.Y)));
        var right = Vector3.Normalize(Vector3.Cross(Vector3.UnitY, look));
        var up = Vector3.Cross(look, right);

        var matrix = new Matrix4x4(
            right.X, up.X, look.X, 0f,
            right.Y, up.Y, look.Y, 0f,
            right.Z, up.Z, look.Z, 0f,
            -position.X * right.X - position.Y * right.Y - position.Z * right.Z,
            -position.X * up.X - position.Y * up.Y - position.Z * up.Z,
            -position.X * look.X - position.Y * look.Y - position.Z * look.Z,
            1f);

        return Matrix4x4.Transform(
            matrix,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, live.Roll));
    }

    /// <summary>Ktisis's look-position detour: while tracking, the orbit
    /// pivot follows the averaged bone position per the mode — Follow moves
    /// the camera rig with the bones, Pan swings the look-at onto them,
    /// FollowAndPan blends both with Ktisis's easing factor.</summary>
    private float* CalculateLookPositionDetour(
        NativeCamera* camera, float* lookAt, float* position, byte mode)
    {
        try
        {
            if (_gPose.IsGPosing &&
                _live is { IsTracking: true } live &&
                live.Kind != CameraKind.Free &&
                _trackedPivot is { } pivot &&
                live.TrackedBones.Count > 0 &&
                live.TrackedBones[0].Skeleton?.Actor is { } actor &&
                actor.Address != nint.Zero &&
                // Deref-time revalidation between the per-tick validity
                // scans: the tracked bone's stored actor address is only a
                // claim; unresolved is refusal for this frame.
                _objectTable?.CreateObjectReference(actor.Address) is { } resolved &&
                resolved.IsValid())
            {
                var gameObject = (GameObject*)resolved.Address;
                Vector3 actorPosition = resolved.Position;
                float cameraOffsetY = gameObject->CameraOffset.Y;

                switch (live.TrackingMode)
                {
                    case CameraTrackingMode.Follow:
                    {
                        var offset = pivot - actorPosition;
                        offset.Y = pivot.Y - actorPosition.Y - cameraOffsetY;
                        live.TargetOffset = offset;
                        break;
                    }
                    case CameraTrackingMode.Pan:
                        lookAt[0] = pivot.X;
                        lookAt[1] = pivot.Y;
                        lookAt[2] = pivot.Z;
                        live.TargetOffset = Vector3.Zero;
                        break;
                    case CameraTrackingMode.FollowAndPan:
                    {
                        // Ktisis's easing: normalize both positions and take
                        // the per-component hypotenuse's first lane over √2 —
                        // ~0 near the start pose, approaching one half.
                        var from = Vector3.Normalize(actorPosition with
                        {
                            Y = actorPosition.Y + cameraOffsetY,
                        });
                        var to = Vector3.Normalize(pivot);
                        float factor =
                            MathF.Sqrt(from.X * from.X + to.X * to.X) /
                            MathF.Sqrt(2f);
                        var lerp = Vector3.Lerp(actorPosition, pivot, factor);
                        var offset = lerp - actorPosition;
                        offset.Y = 0f;
                        live.TargetOffset = offset;
                        lookAt[0] = pivot.X - offset.X;
                        lookAt[1] = pivot.Y;
                        lookAt[2] = pivot.Z - offset.Z;
                        break;
                    }
                    case CameraTrackingMode.None:
                        live.TargetOffset = Vector3.Zero;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"VirtualCameraService: tracking failed: {ex}");
        }
        return _lookPositionHook!.Original(camera, lookAt, position, mode);
    }

    // ── per-tick upkeep ──────────────────────────────────────────────────

    /// <summary>Mints the "Main Camera" over the game's orbit camera. False
    /// when the native manager is not up yet — the caller decides whether
    /// that means "retry" (GPose entry) or nothing.</summary>
    private bool TryMintDefaultCamera()
    {
        if (_cameras.Exists(camera => camera.IsDefault))
            return true;
        if (Native == null)
            return false;
        var defaultCamera =
            new VirtualCamera(this, CameraKind.Game, isDefault: true)
            {
                Name = "Main Camera",
            };
        defaultCamera.SaveState();
        _cameras.Add(defaultCamera);
        _live = defaultCamera;
        defaultCamera.IsLive = true;
        Publish();
        return true;
    }

    /// <summary>Derives the tracked pivot for the frame and drops bones whose
    /// skeletons died — the same per-tick shape the light attach uses.</summary>
    private void OnFrameworkUpdate(IFramework framework)
    {
        if (_defaultCameraPending)
        {
            if (!_gPose.IsGPosing)
                _defaultCameraPending = false;
            else if (TryMintDefaultCamera())
                _defaultCameraPending = false;
        }

        if (!_gPose.IsGPosing || _live is not { IsTracking: true } live ||
            live.TrackedBones.Count == 0)
        {
            _trackedPivot = null;
            return;
        }

        _trackRefreshed.Clear();
        Vector3 sum = Vector3.Zero;
        int count = 0;
        for (int i = live.TrackedBones.Count - 1; i >= 0; i--)
        {
            var bone = live.TrackedBones[i];
            if (bone.Skeleton is not Skeleton skeleton || !skeleton.IsValid)
            {
                live.TrackedBones.RemoveAt(i);
                continue;
            }
            if (_trackRefreshed.Add(skeleton))
                skeleton.UpdateBoneTransforms(BoneCacheTypes.LastTransform);
            var world = Poser.Transform.FromMatrix(
                bone.LastTransform.ToMatrix() * skeleton.GetModelMatrix());
            if (!float.IsFinite(world.Position.X) ||
                !float.IsFinite(world.Position.Y) ||
                !float.IsFinite(world.Position.Z))
                continue;
            sum += world.Position;
            count++;
        }
        _trackedPivot = count > 0 ? sum / count : null;
    }

    // ── GPose lifecycle ──────────────────────────────────────────────────

    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        if (evt.IsGPosing)
        {
            if (!IsAvailable)
                return;
            // The native camera manager can lag GPose entry. A miss here is
            // retried per framework tick instead of freezing the capability
            // for the whole session — Brio's DrawWhenReady/RunUntilSatisfied
            // treat native readiness the same tick-gated way.
            if (!TryMintDefaultCamera())
                _defaultCameraPending = true;
            return;
        }

        // Leaving GPose: the native camera goes back to the game untouched.
        _defaultCameraPending = false;
        RestoreNativeOverrides();
        foreach (var camera in _cameras)
        {
            camera.IsLive = false;
            camera.IsValid = false;
        }
        _cameras.Clear();
        _live = null;
        _trackedPivot = null;
        Publish();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _framework.Update -= OnFrameworkUpdate;
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        try
        {
            RestoreNativeOverrides();
        }
        catch
        {
            // The game may already be tearing down.
        }
        _cameraUpdateHook?.Dispose();
        _cameraCollisionHook?.Dispose();
        _cameraSceneUpdateHook?.Dispose();
        _handleInputHook?.Dispose();
        _lookPositionHook?.Dispose();
        GC.SuppressFinalize(this);
    }
}
