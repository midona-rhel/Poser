using System;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using Poser.Domain.Scene;
using Poser.Services;
using Poser.Application.Lifecycle;

using SceneCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Camera;
using RenderCamera = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Camera;

namespace Poser.Game.Cameras;

/// <summary>
/// The native camera hooks the virtual-camera overlay runs inside: Brio's
/// camera update, collision and input detours, Ktisis's look-position detour
/// for bone tracking, and the matrix load a free camera's view needs. Each
/// detour reads the owner's live camera; a fault is logged once per detour
/// and the game's own call still runs.
/// </summary>
internal sealed unsafe class NativeCameraHooks
{
    // Brio signatures, verbatim from main (2026-08).
    private const string CameraUpdateSignature =
        "40 55 53 57 48 8D 6C 24 A0 48 81 EC ?? ?? ?? ?? 48 8B 1D";
    private const string CameraCollisionSignature =
        "E8 ?? ?? ?? ?? 4C 8D 44 24 40 89 83 14 ?? ?? ??";
    private const string CameraMatrixLoadSignature =
        "E8 ?? ?? ?? ?? 48 8B 93 90 02 ?? ?? 48 8D 4C 24 40";
    private const string HandleInputSignature =
        "E8 ?? ?? ?? ?? ?? 8B ?? ?? ?? ?? 8B 87 ?? ?? ?? ?? 89 45";

    // Ktisis signature, verbatim from main (2026-08): the function that
    // derives the orbit look-at, hooked for bone tracking.
    private const string CalculateLookPositionSignature =
        "E8 ?? ?? ?? ?? F3 0F 10 64 24 ?? F3 0F 10 0D ?? ?? ?? ??";

    private readonly VirtualCameraService _owner;
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPose;
    private readonly Dalamud.Plugin.Services.IObjectTable _objectTable;

    private delegate nint CameraUpdateDelegate(NativeCamera* camera);
    private delegate nint CameraCollisionDelegate(
        NativeCamera* camera, Vector3* a2, Vector3* a3, float a4, nint a5, float a6);
    private delegate void CameraMatrixLoadDelegate(RenderCamera* camera, nint matrix);
    private delegate void HandleInputDelegate(
        nint a1, nint a2, nint a3, MouseFrame* mouse, KeyboardFrame* keyboard);
    private delegate float* CalculateLookPositionDelegate(
        NativeCamera* camera, float* lookAt, float* position, byte mode);

    // Not readonly: TryHook publishes each before enabling it.
    private Hook<CameraUpdateDelegate>? _cameraUpdateHook;
    private Hook<CameraCollisionDelegate>? _cameraCollisionHook;
    private Hook<HandleInputDelegate>? _handleInputHook;
    private Hook<CalculateLookPositionDelegate>? _lookPositionHook;

    // A detour never throws into the game: a fault is logged once per
    // detour and the game's own call still runs.
    private bool _updateFaultLogged;
    private bool _collisionFaultLogged;
    private bool _inputFaultLogged;
    private bool _trackingFaultLogged;
    private readonly CameraMatrixLoadDelegate? _cameraMatrixLoad;

    /// <param name="startup">The owner's activation cleanup: every enabled
    /// hook is disposed if the owner's construction fails after it.</param>
    public NativeCameraHooks(
        VirtualCameraService owner,
        ISigScanner sigScanner,
        IGameInteropProvider hooks,
        IPluginLog log,
        IGPoseService gPose,
        Dalamud.Plugin.Services.IObjectTable objectTable,
        global::Poser.Application.Lifecycle.StartupCleanup startup)
    {
        _owner = owner;
        _log = log;
        _gPose = gPose;
        _objectTable = objectTable;

        // The detour reads the field `publish` assigns and can run the moment
        // the hook is enabled, so the field is set first.
        void TryHook<T>(string name, string signature, T detour, Action<Hook<T>?> publish)
            where T : Delegate
        {
            try
            {
                using var activating = new global::Poser.Application.Lifecycle.StartupCleanup(
                    error => log.Error(error, "Camera hook cleanup failed"));
                var hook = hooks.HookFromAddress<T>(
                    sigScanner.ScanText(signature), detour);
                activating.OnFailure(hook.Dispose);
                publish(hook);
                hook.Enable();
                startup.OnFailure(hook.Dispose);
                activating.Complete();
            }
            catch (Exception ex)
            {
                publish(null);
                _log.Warning(
                    $"VirtualCameraService: '{name}' unavailable: {ex.Message}");
            }
        }

        TryHook<CameraUpdateDelegate>(
            "camera update", CameraUpdateSignature, CameraUpdateDetour,
            hook => _cameraUpdateHook = hook);

        TryHook<CameraCollisionDelegate>(
            "camera collision", CameraCollisionSignature, CameraCollisionDetour,
            hook => _cameraCollisionHook = hook);
        TryHook<HandleInputDelegate>(
            "input handler", HandleInputSignature, HandleInputDetour,
            hook => _handleInputHook = hook);
        TryHook<CalculateLookPositionDelegate>(
            "look position", CalculateLookPositionSignature,
            CalculateLookPositionDetour,
            hook => _lookPositionHook = hook);

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
    }

    /// <summary>Whether the camera update hook — the overlay itself — is in.</summary>
    public bool UpdateHooked => _cameraUpdateHook != null;

    /// <summary>A free camera without the matrix-load call would freeze the view.</summary>
    public bool MatrixLoadAvailable => _cameraMatrixLoad != null;

    private VirtualCamera? Live => _owner.Live;

    private NativeCamera* Native => _owner.Native;

    private Vector3? TrackedPivot => _owner.TrackedPivot;

    /// <summary>Brio's CameraUpdateDetour: the position/target offset is
    /// added after the game has computed the frame's camera, and the look-at
    /// moves with it so the view direction survives.</summary>
    private nint CameraUpdateDetour(NativeCamera* camera)
    {
        var result = _cameraUpdateHook!.Original(camera);
        try
        {
            if (!_gPose.IsGPosing || Live is not { } live)
                return result;

            // Pan, roll and zoom retain their one post-update reassertion.
            // Angle writes use the native one-update discontinuity flag;
            // reasserting yaw here would desynchronize it from the rendered view.
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
                _owner.ApplyOrthographic(true, live.OrthographicZoom);
        }
        catch (Exception ex)
        {
            if (!_updateFaultLogged)
            {
                _updateFaultLogged = true;
                _log.Error($"VirtualCameraService: camera update failed (logged once): {ex}");
            }
        }
        return result;
    }

    /// <summary>Brio's collision detour: with collision disabled the collide
    /// distance is pushed to the zoom ceiling and the game's probe skipped.
    /// </summary>
    private nint CameraCollisionDetour(
        NativeCamera* camera, Vector3* a2, Vector3* a3, float a4, nint a5, float a6)
    {
        try
        {
            if (_gPose.IsGPosing &&
                Live is { DisableCollision: true, Kind: not CameraKind.Free })
            {
                camera->Collide = new Vector2(camera->MaxDistance);
                return 0;
            }
        }
        catch (Exception ex)
        {
            if (!_collisionFaultLogged)
            {
                _collisionFaultLogged = true;
                _log.Error($"VirtualCameraService: camera collision failed (logged once): {ex}");
            }
        }
        return _cameraCollisionHook!.Original(camera, a2, a3, a4, a5, a6);
    }

    // Runs after the native scene update and scenery anchors, before rendering.
    public void UpdateSceneCamera(SceneCamera* camera)
    {
        if (!_gPose.IsGPosing ||
            Live is not { Kind: CameraKind.Free } live ||
            _cameraMatrixLoad == null)
            return;

        camera->ViewMatrix = _owner.FreeCamera.UpdateMatrix(live);
        var native = Native;
        if (native != null)
            _cameraMatrixLoad(
                native->Camera.CameraBase.SceneCamera.RenderCamera,
                (nint)(&camera->ViewMatrix));
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
            if (!_gPose.IsGPosing || Live is not { } live)
                return;

            // Null-checked BEFORE the deref: a null singleton here is an
            // AccessViolationException, which .NET never delivers to the
            // catch below — it would be a process crash inside the game's
            // input handler. Every other singleton read in this file goes
            // through the null-checking Native property.
            var atk = RaptureAtkModule.Instance();
            if (atk == null || atk->AtkModule.IsTextInputActive())
                return;


            _owner.FreeCamera.FlightActive = false;
            if (live.Kind == CameraKind.Free)
                _owner.FreeCamera.HandleInput(live, mouse, keyboard);

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
            if (!_inputFaultLogged)
            {
                _inputFaultLogged = true;
                _log.Error($"VirtualCameraService: input handling failed (logged once): {ex}");
            }
        }
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
                Live is { IsTracking: true } live &&
                live.Kind != CameraKind.Free &&
                TrackedPivot is { } pivot &&
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
            if (!_trackingFaultLogged)
            {
                _trackingFaultLogged = true;
                _log.Error($"VirtualCameraService: tracking failed (logged once): {ex}");
            }
        }
        return _lookPositionHook!.Original(camera, lookAt, position, mode);
    }

    /// <summary>The update and collision hooks, disposed first on unload.</summary>
    public void DisposeUpdateHooks()
    {
        _cameraUpdateHook?.Dispose();
        _cameraCollisionHook?.Dispose();
    }

    /// <summary>The input and look-position hooks, disposed after the scene
    /// phase is unsubscribed.</summary>
    public void DisposeInputHooks()
    {
        _handleInputHook?.Dispose();
        _lookPositionHook?.Dispose();
    }
}
