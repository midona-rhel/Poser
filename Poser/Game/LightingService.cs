using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Plugin.Services;
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
    private readonly IEventBus _eventBus;

    // Native function pointers
    private readonly delegate* unmanaged<GameLight*, void> _spawnGameLight;
    private readonly delegate* unmanaged<GameLight*, void> _spawnGameLightCreate;
    private readonly delegate* unmanaged<GameLight*, void> _spawnGameLightFinalize;
    private readonly delegate* unmanaged<GameLight*, void> _updateGameLightCulling;
    private readonly delegate* unmanaged<GameLight*, void> _updateGameLightMaterial;

    private readonly List<LightEntity> _spawnedLights = new();
    private int _nextLightId = 1;

    public bool IsAvailable { get; }
    public IReadOnlyList<LightEntity> SpawnedLights => _spawnedLights;

    public event Action? OnLightsChanged;

    public LightingService(
        IPluginLog log,
        IFramework framework,
        ISigScanner sigScanner,
        IGPoseService gPoseService,
        ICameraService cameraService,
        IEventBus eventBus)
    {
        _log = log;
        _framework = framework;
        _gPoseService = gPoseService;
        _cameraService = cameraService;
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

    public LightEntity? SpawnLight(LightType type)
    {
        if (!IsAvailable)
        {
            _log.Warning("LightingService: Cannot spawn light, service unavailable");
            return null;
        }

        if (!_gPoseService.IsGPosing)
        {
            _log.Warning("LightingService: Cannot spawn light outside of GPose");
            return null;
        }

        try
        {
            // Allocate native memory
            var lightPtr = (GameLight*)NativeMemory.AlignedAlloc((nuint)sizeof(GameLight), 8);

            // Initialize the light
            _spawnGameLight(lightPtr);
            _spawnGameLightCreate(lightPtr);
            _spawnGameLightFinalize(lightPtr);

            // Set initial position to camera position
            lightPtr->Transform.Position = _cameraService.GetCameraPosition();
            lightPtr->Transform.Rotation = Quaternion.Identity;
            lightPtr->Transform.Scale = Vector3.One;

            // Configure render object
            if (lightPtr->LightRenderObject != null)
            {
                lightPtr->LightRenderObject->EmissionType = type;
                lightPtr->LightRenderObject->Transform = &lightPtr->Transform;
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

            // Enable light
            lightPtr->LightFlags = 79;

            // Update the light
            UpdateNativeLight(lightPtr);

            // Create entity
            var lightId = _nextLightId++;
            var entityId = new EntityId($"light_{lightId}");
            var lightName = $"{GetLightTypeName(type)} {lightId}";
            var entity = new LightEntity(entityId, lightName, type, lightPtr);

            _spawnedLights.Add(entity);

            _log.Info($"LightingService: Spawned {type} light (ID: {lightId})");
            OnLightsChanged?.Invoke();

            return entity;
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: Failed to spawn light: {ex}");
            return null;
        }
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

    public LightEntity? CloneLight(LightEntity source)
    {
        if (!source.IsValidLight)
            return null;

        var newLight = SpawnLight(source.LightType);
        if (newLight == null)
            return null;

        // Copy transform
        newLight.Transform = source.Transform;

        // Copy properties
        newLight.Color = source.Color;
        newLight.Intensity = source.Intensity;
        newLight.Range = source.Range;
        newLight.Falloff = source.Falloff;
        newLight.FalloffType = source.FalloffType;
        newLight.SpotAngle = source.SpotAngle;
        newLight.FalloffAngle = source.FalloffAngle;
        newLight.HasReflection = source.HasReflection;
        newLight.CastsCharacterShadow = source.CastsCharacterShadow;
        newLight.CharacterShadowRange = source.CharacterShadowRange;

        return newLight;
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
