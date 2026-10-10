using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain;
using Poser.Domain.Transforms;
using Dalamud.Game;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using Poser.Domain.Identity;
using Poser.Domain.Scene;

using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;

using Poser.Domain.Cameras;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Game.Core;
using Poser.Game.Entities;
using Poser.Game.Services;

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
    internal Documents.Config.CameraConfiguration CameraSettings =>
        _configuration is { } service
            ? service.Config.Camera
            : FallbackCameraSettings;

    private static readonly Documents.Config.CameraConfiguration
        FallbackCameraSettings = new();

    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGPoseService _gPose;
    private readonly IEventBus _events;

    private readonly Runtime.SceneFramePhaseService? _framePhases;

    /// <summary>Null in the test ctor: no signature scans, no hooks.</summary>
    private readonly NativeCameraHooks? _native;
    private readonly CameraTargeting _targeting;

    /// <summary>The free camera's flight inputs and view matrix.</summary>
    internal FreeCameraController FreeCamera { get; }

    private readonly List<VirtualCamera> _cameras = new();
    private VirtualCamera? _live;

    /// <summary>The game's own limits before a delimit lifted them; one set,
    /// because there is one native camera.</summary>
    private (Vector2 Distance, float YMin, float YMax)? _originalLimits;

    public bool SuppressFlightKeys { get => FreeCamera.SuppressFlightKeys; set => FreeCamera.SuppressFlightKeys = value; }
    public bool FlightActive => FreeCamera.FlightActive;

    // Tracking: the averaged bone world position is derived once per tick on
    // the framework thread (skeleton caches are refreshed there, exactly like
    // the light attach), and the hook only consumes the number.
    private Vector3? _trackedPivot;
    private readonly HashSet<Skeleton> _trackRefreshed = new();

    // Set when GPose was entered before the native camera manager was ready;
    // the per-tick handler retries the default-camera mint until it lands or
    // GPose ends. One pointer read per frame, no scans, no new subscriptions.
    // The GPose entry event may precede service activation during a reload.
    // Reconcile once on the framework thread even without a new entry edge.
    private bool _defaultCameraPending = true;

    // Test seam: replaces the CameraManager singleton read so the retry
    // policy is drivable without the game. Null in production.
    private readonly Func<nint>? _nativeCameraOverride;

    private bool _disposed;

    private readonly global::Poser.Application.Settings.ConfigurationService ? _configuration;

    public VirtualCameraService(
        global::Poser.Application.Settings.ConfigurationService configuration,
        ISigScanner sigScanner,
        IGameInteropProvider hooks,
        IFramework framework,
        IPluginLog log,
        IGPoseService gPose,
        IEventBus events,
        Dalamud.Plugin.Services.IObjectTable objectTable,
        IKeyState keyState,
        Runtime.SceneFramePhaseService framePhases,
        Application.Input.CameraInputState input)
    {
        _configuration = configuration;
        _log = log;
        _framework = framework;
        _gPose = gPose;
        _events = events;
        _framePhases = framePhases;
        FreeCamera = new FreeCameraController(this, input, keyState);
        _targeting = new CameraTargeting(this, gPose, objectTable);

        using var startup = new global::Poser.Application.Lifecycle.StartupCleanup(
            error => log.Error(error, "Camera activation cleanup failed"));

        var native = new NativeCameraHooks(
            this, sigScanner, hooks, log, gPose, objectTable, startup);
        _native = native;
        IsAvailable = native.UpdateHooked;

        startup.OnFailure(() => _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged));
        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        startup.OnFailure(() => _framework.Update -= OnFrameworkUpdate);
        _framework.Update += OnFrameworkUpdate;
        startup.OnFailure(() => framePhases.CameraUpdate -= native.UpdateSceneCamera);
        framePhases.CameraUpdate += native.UpdateSceneCamera;
        startup.Complete();
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
        bool isAvailable,
        IKeyState? keyState = null)
    {
        _log = log;
        _framework = framework;
        _gPose = gPose;
        _events = events;
        _nativeCameraOverride = nativeCameraOverride;
        IsAvailable = isAvailable;
        FreeCamera = new FreeCameraController(this, new Application.Input.CameraInputState(), keyState);
        _targeting = new CameraTargeting(this, gPose, null);

        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    public bool IsAvailable { get; }

    public IReadOnlyList<IVirtualCamera> Cameras => _cameras;

    public IVirtualCamera? LiveCamera => _live;

    public FreeCameraSpeedNotice? SpeedNotice =>
        _live is { Kind: CameraKind.Free }
            ? FreeCamera.SpeedNotice
            : null;

    /// <summary>The live camera, for the hooks and the targeting commands.</summary>
    internal VirtualCamera? Live => _live;

    /// <summary>The averaged world position of the live camera's tracked
    /// bones this tick, or null when nothing is tracked.</summary>
    internal Vector3? TrackedPivot => _trackedPivot;

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

    public IVirtualCamera? CreateCamera(CameraKind kind, bool makeLive = true)
    {
        if (!IsAvailable || !_gPose.IsGPosing || Native == null)
            return null;
        // A free camera without the matrix-load call would freeze the view.
        if (kind == CameraKind.Free &&
            (_native?.MatrixLoadAvailable != true || _framePhases?.RenderHookAvailable != true))
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
        if (makeLive)
            SetLive(camera);
        else
            Publish();
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
            Name = EntityNames.Next(original.Name, _cameras.Select(x => x.Name)),
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
        FreeCamera.ClearSpeedNotice();
        Publish();
    }

    public bool SetTargetActor(
        IVirtualCamera camera, IActor actor, ActorId actorId,
        string displayName) =>
        _targeting.SetTargetActor(camera, actor, actorId, displayName);

    public void ClearTargetActor(IVirtualCamera camera) =>
        _targeting.ClearTargetActor(camera);

    /// <summary>See <see cref="CameraTargeting.CenterOnActor"/>.</summary>
    public Outcome CenterOnActor(IActor actor) => _targeting.CenterOnActor(actor);

    /// <summary>See <see cref="CameraTargeting.CenterOnBone"/>.</summary>
    public Outcome CenterOnBone(IBone bone) => _targeting.CenterOnBone(bone);

    /// <summary>The spawned camera's default name. Bare number, no "#": every
    /// other numbered entity in the scene (lights, props) is named
    /// "{stem} {n}", and one family wearing a hash read as a different sort of
    /// thing (user 2026-08-14). Restored scene documents keep their authored
    /// names; fresh creation and duplication use the shared series rule.</summary>
    private string NextName(CameraKind kind)
    {
        string stem = kind == CameraKind.Free ? "Free camera" : "Camera";
        return EntityNames.Next(stem, _cameras.Select(x => x.Name));
    }

    private void Publish() =>
        _events.Publish(new CameraListChangedEvent());

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
            if (!IsAvailable || !_gPose.IsGPosing)
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
            if (BoneWorld.Of(bone) is not { } world)
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
        _native?.DisposeUpdateHooks();
        if (_framePhases != null && _native != null)
            _framePhases.CameraUpdate -= _native.UpdateSceneCamera;
        _native?.DisposeInputHooks();
        GC.SuppressFinalize(this);
    }
}
