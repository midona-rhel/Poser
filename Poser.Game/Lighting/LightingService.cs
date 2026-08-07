using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game.Lighting;

/// <summary>
/// Spawns and owns plugin-created scene lights through the game's own light
/// factory. GPose-scoped: leaving GPose destroys every spawned light.
/// </summary>
public sealed unsafe class LightingService : ILightingService
{
    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPose;
    private readonly ICameraService _camera;
    private readonly IEventBus _events;

    /// <summary>Light.Create — the game allocates and returns the object;
    /// the plugin never allocates one itself.</summary>
    private readonly delegate* unmanaged<uint, nint, void*, GameLight*> _createGameLight;

    private readonly List<Light> _lights = new();

    public LightingService(
        ISigScanner sigScanner,
        IFramework framework,
        IPluginLog log,
        IGPoseService gPose,
        ICameraService camera,
        IEventBus events)
    {
        _framework = framework;
        _log = log;
        _gPose = gPose;
        _camera = camera;
        _events = events;

        try
        {
            var createAddress = sigScanner.ScanText(
                "48 89 5C 24 ?? 57 48 83 EC 20 49 8B D8 8B F9");
            _createGameLight =
                (delegate* unmanaged<uint, nint, void*, GameLight*>)createAddress;
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"LightingService: light factory signature not found, lighting unavailable: {ex.Message}");
            IsAvailable = false;
        }

        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    public bool IsAvailable { get; }

    public IReadOnlyList<ILight> Lights => _lights;

    public ILight? SpawnLight(LightKind kind)
    {
        if (!CanSpawn())
            return null;
        return SpawnInternal(kind, null);
    }

    public ILight? CloneLight(ILight source)
    {
        if (!CanSpawn())
            return null;
        if (source is not Light typed || !typed.IsValid)
        {
            _log.Warning("LightingService: cannot clone an invalid light");
            return null;
        }
        return SpawnInternal(typed.Kind, typed);
    }

    public void DestroyLight(ILight light)
    {
        if (light is not Light typed || !_lights.Remove(typed))
            return;

        try
        {
            var native = typed.NativePtr;
            if (native != null)
                native->Destroy();
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to destroy light: {ex.Message}");
        }

        typed.Invalidate();
        _events.Publish(new LightListChangedEvent(Lights));
    }

    public void DestroyAllLights()
    {
        if (_lights.Count == 0)
            return;

        foreach (var light in _lights.ToArray())
        {
            try
            {
                var native = light.NativePtr;
                if (native != null)
                    native->Destroy();
            }
            catch (Exception ex)
            {
                _log.Error($"LightingService: failed to destroy light: {ex.Message}");
            }
            light.Invalidate();
        }

        _lights.Clear();
        _events.Publish(new LightListChangedEvent(Lights));
    }

    public bool IsSpawnedLight(ILight light) =>
        light is Light typed && _lights.Contains(typed);

    private bool CanSpawn()
    {
        if (!IsAvailable)
            return false;
        if (!_gPose.IsGPosing)
        {
            _log.Warning("LightingService: lights can only be spawned in GPose");
            return false;
        }
        // UI commands arrive on the framework thread, so the native call runs
        // inline — queueing would defer the new light past the caller's return.
        if (!_framework.IsInFrameworkUpdateThread)
        {
            _log.Warning("LightingService: light spawn must run on the framework thread");
            return false;
        }
        return true;
    }

    private ILight? SpawnInternal(LightKind kind, Light? source)
    {
        try
        {
            var nativeType = Light.ToNative(kind);
            var native = _createGameLight((uint)nativeType, nint.Zero, null);
            if (native == null)
            {
                _log.Error("LightingService: light factory returned null");
                return null;
            }

            // The render object caches the address of the light's transform,
            // so the transform must hold its final values BEFORE the pointer
            // is published — a pointer written first latches stale data.
            if (source != null)
            {
                var sourceTransform = source.Transform;
                native->Transform.Position = sourceTransform.Position;
                native->Transform.Rotation = sourceTransform.Rotation;
                native->Transform.Scale = sourceTransform.Scale;
            }
            else
            {
                native->Transform.Position = _camera.GetCameraPosition();
                native->Transform.Rotation = CameraRotation();
                native->Transform.Scale = Vector3.One;
            }

            if (native->LightRenderObject != null)
            {
                var render = native->LightRenderObject;
                render->EmissionType = nativeType;
                render->Transform = &native->Transform;
                render->LightFlags = LightFlags.Reflection;

                render->Color = new Vector3(20f);
                render->Intensity = 1f;

                render->FalloffType = FalloffType.Quadratic;
                render->Falloff = 1f;
                render->LightAngle = 45f;
                render->FalloffAngle = 0.5f;
                render->Range = DefaultRange(nativeType);
                render->AreaAngle = Vector2.Zero;

                render->CharacterShadowRange = 110f;
                render->ShadowPlaneNear = 0.01f;
                render->ShadowPlaneFar = 17f;
            }

            if (native->VisibilityFlags == 0)
                native->VisibilityFlags = 79;

            var light = new Light(native, GenerateName(kind));
            _lights.Add(light);

            if (source != null)
                CopyProperties(source, light);

            native->Update();

            _log.Debug($"LightingService: spawned {kind} light '{light.Name}'");
            _events.Publish(new LightListChangedEvent(Lights));
            return light;
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to spawn light: {ex}");
            return null;
        }
    }

    private static void CopyProperties(Light source, Light target)
    {
        target.Kind = source.Kind;
        target.IsOn = source.IsOn;
        target.Color = source.Color;
        target.Intensity = source.Intensity;
        target.Range = source.Range;
        target.Falloff = source.Falloff;
        target.FalloffType = source.FalloffType;
        target.SpotAngle = source.SpotAngle;
        target.FalloffAngle = source.FalloffAngle;
        target.AreaAngle = source.AreaAngle;
        target.HasReflection = source.HasReflection;
        target.CastsDynamicShadows = source.CastsDynamicShadows;
        target.CastsCharacterShadow = source.CastsCharacterShadow;
        target.CastsObjectShadow = source.CastsObjectShadow;
        target.CharacterShadowRange = source.CharacterShadowRange;
        target.ShadowPlaneNear = source.ShadowPlaneNear;
        target.ShadowPlaneFar = source.ShadowPlaneFar;
    }

    private static float DefaultRange(LightType type) => type switch
    {
        LightType.SpotLight => 15f,
        LightType.FlatLight => 10f,
        LightType.PointLight => 8f,
        _ => 15f,
    };

    /// <summary>Camera look rotation, taken from the inverse of the view
    /// matrix — the camera service exposes no rotation of its own.</summary>
    private Quaternion CameraRotation()
    {
        var view = _camera.GetViewMatrix();
        if (!Matrix4x4.Invert(view, out var world))
            return Quaternion.Identity;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(world));
    }

    private string GenerateName(LightKind kind)
    {
        var baseName = kind switch
        {
            LightKind.Spot => "Spot Light",
            LightKind.Point => "Point Light",
            LightKind.Area => "Area Light",
            LightKind.Directional => "Directional Light",
            _ => "Light",
        };

        var sameKind = 0;
        foreach (var light in _lights)
        {
            if (light.Kind == kind)
                sameKind++;
        }
        return sameKind > 0 ? $"{baseName} {sameKind + 1}" : baseName;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (framework.IsFrameworkUnloading || _lights.Count == 0)
            return;

        foreach (var light in _lights)
        {
            if (!light.IsValid || !light.IsOn)
                continue;
            light.NativePtr->Update();
        }
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        if (!evt.IsGPosing)
            DestroyAllLights();
    }

    public void Dispose()
    {
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update -= OnFrameworkUpdate;
        DestroyAllLights();
        GC.SuppressFinalize(this);
    }
}
