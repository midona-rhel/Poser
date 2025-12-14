using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Core;
using Poser.Entities;
using Poser.Game.Structs;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Service for managing virtual camera presets.
/// Captures and applies camera state to/from the game camera.
/// Uses memory hooks to apply position offset each frame.
/// </summary>
public unsafe class VirtualCameraService : IVirtualCameraService
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;

    private readonly List<VirtualCameraEntity> _cameras = new();
    private VirtualCameraEntity? _currentCamera;

    // Store original zoom limits for DelimitCamera feature
    private Vector2? _originalZoomLimits;

    // Camera update hook for applying position offset
    private delegate nint CameraUpdateDelegate(BrioCamera* camera);
    private readonly Hook<CameraUpdateDelegate>? _cameraUpdateHook;

    public bool IsAvailable { get; }
    public VirtualCameraEntity? CurrentCamera => _currentCamera;
    public IReadOnlyList<VirtualCameraEntity> Cameras => _cameras;

    public VirtualCameraService(
        IPluginLog log,
        IFramework framework,
        ISigScanner sigScanner,
        IGameInteropProvider hooking,
        IGPoseService gPoseService,
        IEventBus eventBus)
    {
        _log = log;
        _framework = framework;
        _gPoseService = gPoseService;
        _eventBus = eventBus;

        // Set up camera update hook for position offset
        try
        {
            // Signature from Brio's CameraService
            var cameraUpdateAddr = sigScanner.ScanText("40 55 53 57 48 8D 6C 24 A0 48 81 EC ?? ?? ?? ?? 48 8B 1D");
            _cameraUpdateHook = hooking.HookFromAddress<CameraUpdateDelegate>(cameraUpdateAddr, CameraUpdateDetour);
            _cameraUpdateHook.Enable();
            IsAvailable = true;
            _log.Info("VirtualCameraService: Camera update hook installed");
        }
        catch (Exception ex)
        {
            _log.Warning($"VirtualCameraService: Failed to install camera hook: {ex.Message}");
            IsAvailable = false;
        }

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    /// <summary>
    /// Camera update detour - applies position offset each frame.
    /// </summary>
    private nint CameraUpdateDetour(BrioCamera* camera)
    {
        var result = _cameraUpdateHook!.Original(camera);

        if (_gPoseService.IsGPosing && _currentCamera != null)
        {
            // Apply position offset only (like Brio)
            // OrbitTarget is display-only - actual orbit is controlled by GPose target
            var offset = _currentCamera.PositionOffset;
            if (offset != Vector3.Zero)
            {
                Vector3 currentPos = camera->Camera.CameraBase.SceneCamera.Object.Position;
                var newPos = offset + currentPos;
                camera->Camera.CameraBase.SceneCamera.Object.Position = newPos;

                // Adjust LookAt to maintain view direction
                Vector3 currentLookAt = camera->Camera.CameraBase.SceneCamera.LookAtVector;
                camera->Camera.CameraBase.SceneCamera.LookAtVector = currentLookAt + (newPos - currentPos);
            }
        }

        return result;
    }

    public VirtualCameraEntity CreateCamera(string? name = null)
    {
        var camera = new VirtualCameraEntity(name);

        // Capture current game camera state
        CaptureFromGame(camera);

        _cameras.Add(camera);
        _log.Info($"VirtualCameraService: Created camera '{camera.Name}'");
        _eventBus.Publish(new CamerasChangedEvent());

        return camera;
    }

    public void DeleteCamera(VirtualCameraEntity camera)
    {
        if (!_cameras.Contains(camera))
            return;

        // If deleting current camera, switch to none
        if (_currentCamera == camera)
        {
            SelectCamera(null);
        }

        _cameras.Remove(camera);
        camera.Dispose();

        _log.Info($"VirtualCameraService: Deleted camera '{camera.Name}'");
        _eventBus.Publish(new CamerasChangedEvent());
    }

    public void DeleteAllCameras()
    {
        SelectCamera(null);

        foreach (var camera in _cameras.ToArray())
        {
            _cameras.Remove(camera);
            camera.Dispose();
        }

        _log.Info("VirtualCameraService: Deleted all cameras");
        _eventBus.Publish(new CamerasChangedEvent());
    }

    public VirtualCameraEntity CloneCamera(VirtualCameraEntity source)
    {
        var clone = source.Clone();
        _cameras.Add(clone);

        _log.Info($"VirtualCameraService: Cloned camera '{source.Name}' as '{clone.Name}'");
        _eventBus.Publish(new CamerasChangedEvent());

        return clone;
    }

    public void SelectCamera(VirtualCameraEntity? camera)
    {
        // Deactivate current camera
        if (_currentCamera != null)
        {
            // Save current game camera state BEFORE deactivating
            CaptureFromGame(_currentCamera);

            _currentCamera.IsActive = false;

            // Restore zoom limits if DelimitCamera was active
            RestoreZoomLimits();
        }

        _currentCamera = camera;

        // Activate new camera
        if (_currentCamera != null)
        {
            _currentCamera.IsActive = true;
            ApplyToGame(_currentCamera);
        }

        _eventBus.Publish(new CamerasChangedEvent());
    }

    public void CaptureFromGame(VirtualCameraEntity camera)
    {
        var brioCamera = GetBrioCamera();
        if (brioCamera == null)
            return;

        camera.Angle = brioCamera->Angle;
        camera.Distance = brioCamera->Distance;
        camera.FoV = brioCamera->FoV;
        camera.Roll = brioCamera->Roll;
        camera.Pan = brioCamera->Pan;
        // Note: Position offset is applied incrementally, so we don't capture it directly
    }

    public void ApplyToGame(VirtualCameraEntity camera)
    {
        var brioCamera = GetBrioCamera();
        if (brioCamera == null)
            return;

        brioCamera->Angle = camera.Angle;
        brioCamera->Distance = camera.Distance;
        brioCamera->FoV = camera.FoV;
        brioCamera->Roll = camera.Roll;
        brioCamera->Pan = camera.Pan;

        // Handle DelimitCamera (extended zoom limits)
        if (camera.DelimitCamera)
        {
            ApplyDelimitCamera(brioCamera);
        }
        else
        {
            RestoreZoomLimits();
        }
    }

    public bool IsVirtualCamera(IEntity entity)
    {
        return entity is VirtualCameraEntity cam && _cameras.Contains(cam);
    }

    private BrioCamera* GetBrioCamera()
    {
        var cameraManager = CameraManager.Instance();
        if (cameraManager == null)
            return null;

        var gameCamera = cameraManager->GetActiveCamera();
        if (gameCamera == null)
            return null;

        // Cast to BrioCamera to access extended fields
        return (BrioCamera*)gameCamera;
    }

    private void ApplyDelimitCamera(BrioCamera* camera)
    {
        // Store original limits if not already stored
        if (!_originalZoomLimits.HasValue)
        {
            _originalZoomLimits = new Vector2(camera->MinDistance, camera->MaxDistance);
        }

        // Extend zoom limits
        camera->MinDistance = 0f;
        camera->MaxDistance = 500f;
    }

    private void RestoreZoomLimits()
    {
        if (!_originalZoomLimits.HasValue)
            return;

        var brioCamera = GetBrioCamera();
        if (brioCamera == null)
            return;

        brioCamera->MinDistance = _originalZoomLimits.Value.X;
        brioCamera->MaxDistance = _originalZoomLimits.Value.Y;
        _originalZoomLimits = null;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_gPoseService.IsGPosing || _currentCamera == null)
            return;

        // Continuously apply certain camera settings that might be reset by the game
        // (This is a simplified version - full implementation would use hooks)
        var brioCamera = GetBrioCamera();
        if (brioCamera == null)
            return;

        // Re-apply roll and FoV as these can be reset
        if (Math.Abs(brioCamera->Roll - _currentCamera.Roll) > 0.001f)
        {
            brioCamera->Roll = _currentCamera.Roll;
        }

        // Maintain DelimitCamera if active
        if (_currentCamera.DelimitCamera && brioCamera->MaxDistance < 100f)
        {
            ApplyDelimitCamera(brioCamera);
        }
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (e.IsGPosing)
        {
            // Create default camera and capture current state
            // But DON'T apply - let game camera work normally
            var defaultCamera = CreateCamera("Default Camera");
            _currentCamera = defaultCamera;
            _currentCamera.IsActive = true;
            _eventBus.Publish(new CamerasChangedEvent());
        }
        else
        {
            // Reset on GPose exit
            SelectCamera(null);
            DeleteAllCameras();
        }
    }

    public void Dispose()
    {
        _cameraUpdateHook?.Dispose();
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update -= OnFrameworkUpdate;
        RestoreZoomLimits();
        DeleteAllCameras();
        GC.SuppressFinalize(this);
    }
}
