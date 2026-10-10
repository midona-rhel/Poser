using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Transforms;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Poser.Core;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Game.WorldObjects;
using Poser.Services;
using PoserTransform = Poser.Domain.Transforms.Transform;

using Poser.Application.Viewport;
using Poser.Domain.Posing;
using Poser.Application.Events;
using Poser.Application.Lifecycle;

namespace Poser.Game.Lighting;

/// <summary>
/// Spawns and owns plugin-created scene lights through the game's own light
/// factory, and adopts the two kinds of light the plugin does not own: the
/// GPose camera lights (delisted, never destroyed) and overworld lights
/// (an owned editable replacement suppresses the original, as in Ktisis).
/// GPose-scoped: leaving GPose destroys every spawned light and releases
/// every adopted one.
/// </summary>
public sealed unsafe class LightingService : ILightingService
{
    // Light.Create. Wildcarded exactly as Brio upstream wildcards it: the
    // strict prologue stopped matching after a game patch, and the loosened
    // form is what still finds the factory.
    private const string CreateLightSignature =
        "48 ?? ?? ?? ?? 57 48 83 EC 20 49 8B D8 8B F9 ??";

    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPose;
    private readonly ICameraProjection _camera;
    private readonly IEventBus _events;
    private readonly LightGoboController _goboControl;
    private readonly WorldLightCapture _world;
    private readonly GPoseCameraLights _cameraLights;

    /// <summary>Light.Create — the game allocates and returns the object;
    /// the plugin never allocates one itself.</summary>
    private readonly delegate* unmanaged<uint, nint, void*, GameLight*> _createGameLight;

    private readonly List<Light> _lights = new();

    private bool _disposed;

    public LightingService(
        ISigScanner sigScanner,
        IFramework framework,
        IPluginLog log,
        IGPoseService gPose,
        ICameraProjection camera,
        IEventBus events,
        IObjectTable objects,
        IGameInteropProvider hooks,
        IWorldGraphPort worldGraph)
    {
        _framework = framework;
        _log = log;
        _gPose = gPose;
        _camera = camera;
        _events = events;

        var createAddress = TryScan(
            sigScanner, log, "Light.Create", CreateLightSignature);
        if (createAddress is { } create)
        {
            _createGameLight =
                (delegate* unmanaged<uint, nint, void*, GameLight*>)create;
            IsAvailable = true;
        }

        // Scanned and hooked in the service's original order: the gobo pair,
        // then Light.ctor, then the GPose light toggle.
        _goboControl = new LightGoboController(sigScanner, framework, log);
        _world = new WorldLightCapture(
            sigScanner, framework, log, camera, objects, hooks, worldGraph,
            () => _disposed, OnNativeLightDied);
        _cameraLights = new GPoseCameraLights(
            sigScanner, hooks, log, gPose, events, _lights, _world, () => _disposed);

        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    public bool IsAvailable { get; }

    /// <summary>False when either gobo signature failed or the embedded
    /// library is empty; the light service itself stays usable.</summary>
    public bool AreGobosAvailable => _goboControl.AreGobosAvailable;

    public IReadOnlyList<ILight> Lights => _lights;

    public IReadOnlyList<GoboEntry> Gobos => _goboControl.Gobos;

    internal static nint? TryScan(ISigScanner scanner, IPluginLog log, string name, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                return scanner.ScanText(pattern);
            }
            catch (Exception)
            {
                // Fall through to the next pattern; only a total failure is
                // worth a log line.
            }
        }

        log.Warning(
            $"LightingService: signature '{name}' not found ({patterns.Length} pattern(s) tried); the feature it backs is disabled.");
        return null;
    }

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
        if (light is not Light typed)
            return;
        if (!OnOwnerThread(nameof(DestroyLight)))
            return;

        // Release understands the original/replacement pair for world lights;
        // the original and GPose camera lights are never destroyed by Poser.
        if (typed.Ownership != LightOwnership.Spawned)
        {
            ReleaseLight(light);
            return;
        }

        if (!_lights.Remove(typed))
            return;

        DestroyNative(typed);
        _events.Publish(new LightListChangedEvent());
    }

    public void ReleaseLight(ILight light)
    {
        if (light is not Light typed || typed.Ownership == LightOwnership.Spawned)
            return;
        if (!OnOwnerThread(nameof(ReleaseLight)))
            return;
        if (!_lights.Remove(typed))
            return;

        ReleaseInternal(typed);
        _events.Publish(new LightListChangedEvent());
    }

    public void DestroyAllLights()
    {
        if (!OnOwnerThread(nameof(DestroyAllLights)))
            return;
        DestroyAllLightsCore();
    }

    /// <summary>The ungated body. Teardown (Dispose, GPose exit) must destroy
    /// its natives even when it does not run inside a framework update:
    /// refusing there would leak every spawned light instead of protecting
    /// anything, so the thread gate lives on the public entry only.</summary>
    private void DestroyAllLightsCore()
    {
        if (_lights.Count == 0)
            return;

        foreach (var light in _lights.ToArray())
        {
            if (light.Ownership == LightOwnership.Spawned)
                DestroyNative(light);
            else
                ReleaseInternal(light);
        }

        _lights.Clear();
        _events.Publish(new LightListChangedEvent());
    }

    public bool IsSpawnedLight(ILight light) =>
        light is Light typed &&
        typed.Ownership == LightOwnership.Spawned &&
        _lights.Contains(typed);

    private void DestroyNative(Light light)
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

    /// <summary>Restore source visibility and destroy only our replacement.
    /// GPose lights are only delisted.</summary>
    private void ReleaseInternal(Light light)
    {
        try
        {
            if (light.WorldState is { } state &&
                _world.IsCurrentWorldLight(light.WorldAddress, light.WorldGeneration))
                state.Restore((GameLight*)light.WorldAddress);
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to restore a borrowed world light: {ex.Message}");
        }
        finally
        {
            light.WorldState?.Dispose();
            light.WorldState = null;
            if (light.Ownership == LightOwnership.World)
                DestroyNative(light);
            else
                light.Invalidate();
        }
    }

    /// <summary>The native calls run inline on the caller's thread, so every
    /// public entry that reaches one refuses off-thread rather than racing the
    /// game. Teardown paths bypass this deliberately — see DestroyAllLights.</summary>
    private bool OnOwnerThread(string operation)
    {
        if (_framework.IsInFrameworkUpdateThread)
            return true;
        _log.Warning($"LightingService: {operation} must run on the framework thread");
        return false;
    }

    private bool CanSpawn()
    {
        if (!IsAvailable)
        {
            // Said out loud like the other two refusals below it. This branch
            // was the silent one, so a light that could never be made looked
            // exactly like a click that missed.
            _log.Warning(
                "LightingService: lights are unavailable — the game's light "
                + "factory was not found.");
            return false;
        }
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
            var forward = _camera.GetLookDirection();
            if (forward == Vector3.Zero)
                forward = Vector3.Transform(-Vector3.UnitZ, CameraRotation());
            var transform = source != null
                ? source.Transform
                : LightPlacement.FromCamera(
                    _camera.GetCameraPosition(), forward,
                    Vector3.One);

            var light = SpawnNative(
                kind, transform, LightOwnership.Spawned, source == null
                    ? GenerateName(kind)
                    : Poser.Domain.Scene.EntityNames.Next(source.Name, _lights.Select(x => x.Name)));
            if (light == null)
                return null;

            if (source != null)
            {
                CopyProperties(source, light);
                if (source.GoboPath is { } gobo)
                    _goboControl.ApplyGoboPath(light, gobo);
            }

            light.NativePtr->Update();

            _log.Debug($"LightingService: spawned {kind} light '{light.Name}'");
            _events.Publish(new LightListChangedEvent());
            return light;
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to spawn light: {ex}");
            return null;
        }
    }

    /// <summary>Allocates one native light through the game factory, writes
    /// the spawn defaults, and lists it. Publishes nothing — the caller owns
    /// the notification once it has finished configuring the light.</summary>
    private Light? SpawnNative(
        LightKind kind,
        PoserTransform transform,
        LightOwnership ownership,
        string name)
    {
        var nativeType = Light.ToNative(kind);
        var native = _createGameLight((uint)nativeType, nint.Zero, null);
        if (native == null)
        {
            _log.Error("LightingService: light factory returned null");
            return null;
        }

        // The factory runs the same constructor the ctor hook watches, so the
        // plugin's own light lands in the overworld set; take it back out.
        _world.Forget((nint)native);

        // The render object caches the address of the light's transform,
        // so the transform must hold its final values BEFORE the pointer
        // is published — a pointer written first latches stale data.
        native->Transform.Position = transform.Position;
        native->Transform.Rotation = transform.Rotation;
        native->Transform.Scale = transform.Scale;

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

        var light = new Light(native, name, ownership);
        _lights.Add(light);
        return light;
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

        return Poser.Domain.Scene.EntityNames.Next(baseName, _lights.Select(x => x.Name));
    }

    private string UniqueName(string baseName)
    {
        var taken = 0;
        foreach (var light in _lights)
        {
            if (light.Name.StartsWith(baseName, StringComparison.Ordinal))
                taken++;
        }
        return $"{baseName} {taken + 1}";
    }

    #region Gobos

    public bool ApplyGobo(ILight light, GoboEntry gobo)
    {
        if (light is not Light typed || !typed.IsValid)
            return false;
        return _goboControl.ApplyGobo(typed, gobo);
    }

    public void ClearGobo(ILight light)
    {
        if (light is not Light typed)
            return;
        _goboControl.ClearGoboNative(typed);
    }

    #endregion

    #region Overworld capture

    /// <summary>A native the plugin borrowed has gone away: drop anything
    /// bound to it without touching the freed memory.</summary>
    private void OnNativeLightDied(nint handle, long generation)
    {
        if (_disposed)
            return;

        var changed = false;
        foreach (var light in _lights.ToArray())
        {
            if (light.Ownership == LightOwnership.World &&
                light.WorldAddress == handle && light.WorldGeneration == generation)
            {
                _lights.Remove(light);
                // Release checks the original generation before restoring;
                // our separately allocated replacement is still ours to free.
                ReleaseInternal(light);
                changed = true;
                continue;
            }

            if (light.Ownership == LightOwnership.GPose &&
                (nint)light.NativePtr == handle)
            {
                _lights.Remove(light);
                light.Invalidate();
                changed = true;
            }
        }

        if (changed)
            _events.Publish(new LightListChangedEvent());
    }

    public IReadOnlyList<WorldLightCandidate> GetWorldLightCandidates()
    {
        if (!IsAvailable || !_world.HasConstructorHook || !_gPose.IsGPosing)
            return Array.Empty<WorldLightCandidate>();
        if (!_framework.IsInFrameworkUpdateThread)
            return Array.Empty<WorldLightCandidate>();
        return _world.Candidates(IsCaptured);
    }

    public WorldLightCandidate? GetWorldSource(ILight light) =>
        light is Light { Ownership: LightOwnership.World } native && native.IsValid
            ? new WorldLightCandidate(native.WorldAddress, 0, Generation: native.WorldGeneration)
            : null;

    public ILight? CaptureWorldLight(WorldLightCandidate candidate)
    {
        if (!CanSpawn() || !_world.HasDestructorHook)
            return null;

        bool known = _world.IsCurrentWorldLight(candidate.Handle, candidate.Generation);
        if (!known)
        {
            _log.Warning("LightingService: that world light no longer exists");
            return null;
        }
        if (IsCaptured(candidate.Handle))
        {
            _log.Warning("LightingService: that world light is already captured");
            return null;
        }

        var original = (GameLight*)candidate.Handle;
        if (original == null || original->LightRenderObject == null)
            return null;

        BorrowedLightState? state = null;
        Light? light = null;
        try
        {
            // Ktisis AddFromOverworld: game-authored lights keep updating, so
            // edit a separate allocation and suppress only the original's draw.
            var name = UniqueName("World Light");
            state = new BorrowedLightState(original);
            light = SpawnNative(Light.ToKind(original->LightRenderObject->EmissionType),
                new PoserTransform(original->Transform.Position, original->Transform.Rotation,
                    original->Transform.Scale), LightOwnership.World, name);
            if (light == null)
            {
                state.Dispose();
                return null;
            }
            light.WorldAddress = candidate.Handle;
            light.WorldGeneration = candidate.Generation;
            light.WorldState = state;
            state.CopyTo(light.NativePtr);
            _goboControl.AdoptGobo(light, original);
            light.NativePtr->UpdateRender();
            light.NativePtr->Update();
            state.Suppress(original);

            _log.Debug(
                $"LightingService: captured world light {candidate.Handle:X} as '{light.Name}'");
            _events.Publish(new LightListChangedEvent());
            return light;
        }
        catch (Exception ex)
        {
            if (light != null)
            {
                _lights.Remove(light);
                ReleaseInternal(light);
            }
            state?.Dispose();
            _log.Error($"LightingService: failed to capture a world light: {ex}");
            return null;
        }
    }

    private bool IsCaptured(nint handle)
    {
        foreach (var light in _lights)
        {
            if (light.Ownership == LightOwnership.World &&
                light.WorldAddress == handle && light.IsValid)
                return true;
        }
        return false;
    }

    #endregion

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (framework.IsFrameworkUnloading || _disposed)
            return;

        _cameraLights.PollGPoseLights();

        if (_lights.Count == 0)
            return;

        var detached = false;

        for (var i = _lights.Count - 1; i >= 0; i--)
        {
            var light = _lights[i];
            if (light.Ownership == LightOwnership.World)
            {
                if (!_world.IsCurrentWorldLight(light.WorldAddress, light.WorldGeneration))
                {
                    _lights.RemoveAt(i);
                    ReleaseInternal(light);
                    detached = true;
                    continue;
                }
                // Keep the source suppressed even while the editable copy is
                // switched off. The game's own updates may show it again.
                light.WorldState?.Suppress((GameLight*)light.WorldAddress);
            }
            if (!light.IsValid)
                continue;


            // A light switched away from spot or area cannot project, so the
            // texture goes with the switch rather than lingering unused.
            if (light.GoboPath != null && !LightGoboController.SupportsGobo(light.Kind))
                _goboControl.ClearGoboNative(light);

            if (!light.IsOn)
                continue;

            var native = light.NativePtr;
            native->UpdateRender();
            native->Update();
        }

        if (detached)
            _events.Publish(new LightListChangedEvent());
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        _cameraLights.ResetPoll();
        if (evt.IsGPosing)
        {
            // Ktisis rebuilds its world listing on this same edge
            // (WorldService.cs:27-30). The seed runs first so the camera
            // lights the refresh finds are already deduped against it.
            _world.Unseed();
            _world.SeedWorldLights();
            _cameraLights.RefreshGPoseLights();
        }
        else
        {
            _world.Unseed();
            DestroyAllLightsCore();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update -= OnFrameworkUpdate;
        DestroyAllLightsCore();
        _cameraLights.Dispose();
        _world.Dispose();
        GC.SuppressFinalize(this);
    }
}
