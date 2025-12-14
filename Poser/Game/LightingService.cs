using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Poser.Core;
using Poser.Entities;
using Poser.Game.Structs;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Service for spawning and controlling lights in the scene.
/// Based on Brio's LightingService implementation.
/// </summary>
public unsafe class LightingService : ILightingService
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGPoseService _gPoseService;
    private readonly ICameraService _cameraService;
    private readonly ISelectionService _selectionService;
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;

    // Native function pointers
    private readonly delegate* unmanaged<GameLight*, void> _spawnGameLight;
    private readonly delegate* unmanaged<GameLight*, void> _spawnGameLightCreate;
    private readonly delegate* unmanaged<GameLight*, void> _spawnGameLightFinalize;
    private readonly delegate* unmanaged<GameLight*, void> _updateGameLightCulling;
    private readonly delegate* unmanaged<GameLight*, void> _updateGameLightMaterial;

    private readonly List<LightEntity> _spawnedLights = new();

    // Placement mode state
    private LightEntity? _placingLight;
    private float _placementDepth;
    private bool _placementStartedThisFrame;

    public bool IsAvailable { get; }
    public IReadOnlyList<LightEntity> SpawnedLights => _spawnedLights;
    public bool IsPlacing => _placingLight != null;
    public LightEntity? PlacingLight => _placingLight;

    public event Action? OnLightsChanged;

    public LightingService(
        IPluginLog log,
        IFramework framework,
        ISigScanner sigScanner,
        IGPoseService gPoseService,
        ICameraService cameraService,
        ISelectionService selectionService,
        IActorManager actorManager,
        IEventBus eventBus)
    {
        _log = log;
        _framework = framework;
        _gPoseService = gPoseService;
        _cameraService = cameraService;
        _selectionService = selectionService;
        _actorManager = actorManager;
        _eventBus = eventBus;

        try
        {
            // Scan for native functions
            var spawnAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 48 89 84 ?? ?? ?? ?? ?? 48 85 C0 0F ?? ?? ?? ?? ?? 48 8B C8");
            _spawnGameLight = (delegate* unmanaged<GameLight*, void>)spawnAddress;

            var createAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 48 8B 0D ?? ?? ?? ?? 48 8B D3 E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 48 8B ?? ?? ?? ?? ?? 40 0F");
            _spawnGameLightCreate = (delegate* unmanaged<GameLight*, void>)createAddress;

            var finalizeAddress = sigScanner.ScanText("F6 41 38 01 ?? ?? 48 8B ?? ?? ?? ?? ?? 48");
            _spawnGameLightFinalize = (delegate* unmanaged<GameLight*, void>)finalizeAddress;

            var cullingAddress = sigScanner.ScanText("48 89 5C 24 ?? 57 48 83 EC 40 48 8B B9 ?? ?? ?? ??");
            _updateGameLightCulling = (delegate* unmanaged<GameLight*, void>)cullingAddress;

            var materialAddress = sigScanner.ScanText("40 53 48 83 EC 20 0F B6 81 ?? ?? ?? ?? 48 8B D9 A8 04 75 45 0C 04 B2 05");
            _updateGameLightMaterial = (delegate* unmanaged<GameLight*, void>)materialAddress;

            IsAvailable = true;
            _log.Info("LightingService: All signatures found, service available");
        }
        catch (Exception ex)
        {
            _log.Warning($"LightingService: Failed to find signatures, service unavailable: {ex.Message}");
            IsAvailable = false;
        }

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    public void BeginPlacement(LightType type)
    {
        if (!IsAvailable || !_gPoseService.IsGPosing)
            return;

        // Cancel any existing placement
        if (_placingLight != null)
            CancelPlacement();

        _placementStartedThisFrame = true;

        // Get camera position and spawn INSIDE framework thread (like Brio does)
        // Reading game memory must happen on framework thread
        _framework.RunOnFrameworkThread(() =>
        {
            // Get position from BrioCamera ON FRAMEWORK THREAD
            Vector3 targetPos;
            var cameraManager = CameraManager.Instance();
            if (cameraManager != null)
            {
                var brioCamera = (BrioCamera*)cameraManager->GetActiveCamera();
                if (brioCamera != null)
                {
                    targetPos = brioCamera->Position;
                }
                else
                {
                    targetPos = _cameraService.GetCameraPosition();
                }
            }
            else
            {
                targetPos = _cameraService.GetCameraPosition();
            }

            _placementDepth = _cameraService.GetDepthToPosition(targetPos);

            // Spawn with the correctly-read position
            var entity = SpawnLightInternal(type, targetPos, null);
            if (entity != null)
            {
                _placingLight = entity;
            }
        });
    }

    public void ConfirmPlacement()
    {
        if (_placingLight == null)
            return;

        _log.Info($"LightingService: Confirmed placement of '{_placingLight.Name}'");
        _placingLight = null;
    }

    public void CancelPlacement()
    {
        if (_placingLight == null)
            return;

        DestroyLight(_placingLight);
        _placingLight = null;
    }

    public void SpawnLight(LightType type, Vector3 position)
    {
        if (!IsAvailable)
        {
            _log.Warning("LightingService: Cannot spawn light, service unavailable");
            return;
        }

        if (!_gPoseService.IsGPosing)
        {
            _log.Warning("LightingService: Cannot spawn light outside of GPose");
            return;
        }

        // Run on framework thread like Brio does - critical for native code
        _framework.RunOnFrameworkThread(() =>
        {
            var entity = SpawnLightInternal(type, position, null);
            if (entity != null && _placingLight == null)
            {
                _placingLight = entity;
            }
        });
    }

    /// <summary>
    /// Internal spawn method - MUST be called on framework thread.
    /// </summary>
    private LightEntity? SpawnLightInternal(LightType type, Vector3 position, LightEntity? copyFrom)
    {
        try
        {
            // Allocate native memory
            var lightPtr = (GameLight*)NativeMemory.AlignedAlloc((nuint)sizeof(GameLight), 8);

            // 1. Initialize the light via native functions
            _spawnGameLight(lightPtr);
            _spawnGameLightCreate(lightPtr);
            _spawnGameLightFinalize(lightPtr);

            // 2. SET POSITION FIRST (before Transform pointer!) - Brio's exact order
            lightPtr->Transform.Position = position;
            if (copyFrom != null)
            {
                lightPtr->Transform.Rotation = copyFrom.Transform.Rotation;
                lightPtr->Transform.Scale = copyFrom.Transform.Scale;
            }
            else
            {
                lightPtr->Transform.Rotation = Quaternion.Identity;
                lightPtr->Transform.Scale = Vector3.One;
            }

            // 3. THEN configure LightRenderObject (Transform pointer AFTER position is set!)
            if (lightPtr->LightRenderObject != null)
            {
                lightPtr->LightRenderObject->EmissionType = type;
                lightPtr->LightRenderObject->Transform = &lightPtr->Transform; // AFTER position!

                if (copyFrom != null)
                {
                    // Copy properties from source
                    lightPtr->LightRenderObject->LightFlags = copyFrom.HasReflection ? LightFlags.Reflection : 0;
                    if (copyFrom.CastsCharacterShadow)
                        lightPtr->LightRenderObject->LightFlags |= LightFlags.CharaShadow;
                    lightPtr->LightRenderObject->Color = copyFrom.Color;
                    lightPtr->LightRenderObject->Intensity = copyFrom.Intensity;
                    lightPtr->LightRenderObject->FalloffType = copyFrom.FalloffType;
                    lightPtr->LightRenderObject->Falloff = copyFrom.Falloff;
                    lightPtr->LightRenderObject->LightAngle = copyFrom.SpotAngle;
                    lightPtr->LightRenderObject->FalloffAngle = copyFrom.FalloffAngle;
                    lightPtr->LightRenderObject->Range = copyFrom.Range;
                    lightPtr->LightRenderObject->CharacterShadowRange = copyFrom.CharacterShadowRange;
                    lightPtr->LightRenderObject->ShadowPlaneNear = 0.01f;
                    lightPtr->LightRenderObject->ShadowPlaneFar = 17f;
                }
                else
                {
                    // Default properties
                    lightPtr->LightRenderObject->LightFlags = LightFlags.Reflection;
                    lightPtr->LightRenderObject->Color = new Vector3(20f, 20f, 20f);
                    lightPtr->LightRenderObject->Intensity = 1f;
                    lightPtr->LightRenderObject->FalloffType = FalloffType.Quadratic;
                    lightPtr->LightRenderObject->Falloff = 1f;
                    lightPtr->LightRenderObject->LightAngle = 45f;
                    lightPtr->LightRenderObject->FalloffAngle = 0.5f;
                    lightPtr->LightRenderObject->Range = 35f;
                    lightPtr->LightRenderObject->CharacterShadowRange = 110f;
                    lightPtr->LightRenderObject->ShadowPlaneNear = 0.01f;
                    lightPtr->LightRenderObject->ShadowPlaneFar = 17f;
                }
            }

            // 4. Enable light
            lightPtr->LightFlags = 79;

            // Update the light IMMEDIATELY (Brio does this right after)
            UpdateNativeLight(lightPtr);

            // Create entity with smart naming
            var lightName = GenerateLightName(type);
            var entityId = new EntityId($"light_{lightName.Replace(" ", "_").ToLowerInvariant()}_{Guid.NewGuid():N}");
            var entity = new LightEntity(entityId, lightName, type, lightPtr);

            _spawnedLights.Add(entity);

            _log.Info($"LightingService: Spawned {type} light '{lightName}' at {position}");
            OnLightsChanged?.Invoke();

            return entity;
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: Failed to spawn light: {ex}");
            return null;
        }
    }

    private string GenerateLightName(LightType type)
    {
        var baseName = GetLightTypeName(type);

        // Count existing lights of the same type
        var sameTypeCount = _spawnedLights.Count(l => l.LightType == type);

        // Only add number if there are already lights of this type
        return sameTypeCount > 0 ? $"{baseName} {sameTypeCount + 1}" : baseName;
    }

    public void DestroyLight(LightEntity light)
    {
        if (!_spawnedLights.Contains(light))
            return;

        _spawnedLights.Remove(light);

        try
        {
            var nativePtr = light.NativePtr;
            if (nativePtr != null)
            {
                nativePtr->Destroy();
                NativeMemory.AlignedFree(nativePtr);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: Failed to destroy light: {ex}");
        }

        light.Invalidate();
        light.Dispose();

        _log.Info($"LightingService: Destroyed light '{light.Name}'");
        OnLightsChanged?.Invoke();
    }

    public void DestroyAllLights()
    {
        foreach (var light in _spawnedLights.ToArray())
        {
            DestroyLight(light);
        }
    }

    public void CloneLight(LightEntity source)
    {
        if (!source.IsValidLight || !IsAvailable || !_gPoseService.IsGPosing)
            return;

        // Clone on framework thread - all properties copied via SpawnLightInternal
        _framework.RunOnFrameworkThread(() =>
        {
            SpawnLightInternal(source.LightType, source.Transform.Position, source);
        });
    }

    public bool IsSpawnedLight(IEntity entity)
    {
        return entity is LightEntity light && _spawnedLights.Contains(light);
    }

    private static string GetLightTypeName(LightType type) => type switch
    {
        LightType.SpotLight => "Spot Light",
        LightType.AreaLight => "Point Light",
        LightType.FlatLight => "Flat Light",
        LightType.WorldLight => "World Light",
        _ => "Light"
    };

    private void UpdateNativeLight(GameLight* light)
    {
        if (light == null || !IsAvailable)
            return;

        _updateGameLightCulling(light);
        _updateGameLightMaterial(light);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!_gPoseService.IsGPosing)
            return;

        // Handle placement mode
        if (_placingLight != null)
        {
            // Skip input handling on the frame placement started (menu click still active)
            if (_placementStartedThisFrame)
            {
                _placementStartedThisFrame = false;
            }
            else
            {
                // Check for Escape to cancel
                if (ImGui.IsKeyPressed(ImGuiKey.Escape))
                {
                    CancelPlacement();
                    return;
                }
                // Check for click to confirm (left mouse button, not over ImGui)
                else if (ImGui.IsMouseClicked(ImGuiMouseButton.Left) && !ImGui.GetIO().WantCaptureMouse)
                {
                    ConfirmPlacement();
                    return;
                }
            }

            // Update light position to follow cursor
            var mousePos = ImGui.GetMousePos();
            var worldPos = _cameraService.ScreenToWorld(mousePos, _placementDepth);

            // Only update if position is valid (not NaN)
            if (!float.IsNaN(worldPos.X) && !float.IsNaN(worldPos.Y) && !float.IsNaN(worldPos.Z))
            {
                var transform = _placingLight.Transform;
                transform.Position = worldPos;
                _placingLight.Transform = transform;
            }
        }

        // Update all valid lights
        foreach (var light in _spawnedLights)
        {
            if (light.IsValidLight && light.IsLightOn)
            {
                UpdateNativeLight(light.NativePtr);
            }
        }
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            DestroyAllLights();
        }
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update -= OnFrameworkUpdate;
        DestroyAllLights();
        GC.SuppressFinalize(this);
    }
}
