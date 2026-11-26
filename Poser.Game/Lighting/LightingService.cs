using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Domain.Transforms;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Domain.Scene;
using Poser.Game.WorldObjects;
using PoserTransform = Poser.Domain.Transforms.Transform;

using Poser.Application.Viewport;
using Poser.Domain.Posing;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Game.Core;
using Poser.Game.Entities;
using Poser.Game.Services;

namespace Poser.Game.Lighting;

/// <summary>
/// The light list. Poser makes its own lights with the game's light function
/// and frees them again. It also lists two kinds of light that belong to the
/// game: the GPose camera lights, which are only listed and never freed, and
/// world lights the player takes over, which are hidden while Poser shows an
/// editable copy in their place, as in Ktisis. Leaving GPose frees Poser's
/// lights and gives every game light back.
/// </summary>
public sealed unsafe class LightingService : ILightingService
{
    private const string CreateLightSignature =
        Poser.Game.Natives.NativeSignatures.Light_Create;

    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPose;
    private readonly ICameraProjection _camera;
    private readonly IEventBus _events;
    private readonly LightGoboController _goboControl;
    private readonly WorldLightCapture _world;
    private readonly GPoseCameraLights _cameraLights;

    /// <summary>The game's function that makes a light. Poser never allocates
    /// a light itself.</summary>
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

        _goboControl = new LightGoboController(sigScanner, framework, log);
        _world = new WorldLightCapture(
            sigScanner, framework, log, camera, objects, hooks, worldGraph,
            () => _disposed);
        _cameraLights = new GPoseCameraLights(
            sigScanner, hooks, log, gPose, events, _lights, _world, () => _disposed);

        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    public bool IsAvailable { get; }

    /// <summary>False when the game's texture functions were not found or
    /// Poser's gobo list is empty; lights still work.</summary>
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
                // Try the next pattern; only logged when none matches.
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

        // A game light is given back, never freed.
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

    /// <summary>Frees Poser's lights and gives the game's back, without the
    /// thread check: leaving GPose and unloading Poser must always clean up.</summary>
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

    private static void DestroyNative(Light light)
    {
        var native = light.NativePtr;
        if (native != null)
            native->Destroy();
        light.Invalidate();
    }

    /// <summary>Gives a light back to the game. For a world light, the game's
    /// light is shown again (only while it is still the same light, checked by
    /// its generation) and Poser's replacement is freed. A GPose camera light
    /// is only taken off the list.</summary>
    private void ReleaseInternal(Light light)
    {
        if (light.WorldState is { } state &&
            _world.IsCurrentWorldLight(light.WorldAddress, light.WorldGeneration))
            state.Restore((GameLight*)light.WorldAddress);
        light.WorldState?.Dispose();
        light.WorldState = null;
        if (light.Ownership == LightOwnership.World)
            DestroyNative(light);
        else
            light.Invalidate();
    }

    /// <summary>The game's light functions are called directly, so they must
    /// run on the frame update. Leaving GPose and unloading Poser skip this
    /// check, because refusing there would leave Poser's lights behind.</summary>
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
        if (!_framework.IsInFrameworkUpdateThread)
        {
            _log.Warning("LightingService: light spawn must run on the framework thread");
            return false;
        }
        return true;
    }

    private ILight? SpawnInternal(LightKind kind, Light? source)
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

    /// <summary>Has the game make one light, gives it Poser's starting values
    /// and adds it to the list. Sends no list-changed event; the caller does
    /// that once it has finished setting the light up.</summary>
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

        // The constructor hook listed Poser's own light as a world light.
        _world.Forget((nint)native);

        // The position is set before the render object is pointed at it.
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

        if (!native->IsVisible)
            native->IsVisible = true;

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

    /// <summary>The camera's rotation, worked out from the view matrix.</summary>
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

    public IReadOnlyList<WorldLightCandidate> GetWorldLightCandidates()
    {
        if (!IsAvailable || !_world.HasConstructorHook || !_gPose.IsGPosing)
            return Array.Empty<WorldLightCandidate>();
        if (!_framework.IsInFrameworkUpdateThread)
            return Array.Empty<WorldLightCandidate>();
        return _world.Candidates(IsListed);
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
        if (IsListed(candidate.Handle))
        {
            _log.Warning("LightingService: that light is already in the light list");
            return null;
        }

        var original = (GameLight*)candidate.Handle;
        if (original->LightRenderObject == null)
            return null;

        BorrowedLightState? state = null;
        Light? light = null;
        try
        {
            // As in Ktisis, the game keeps updating its own light, so Poser
            // makes a copy to edit and only hides the game's light.
            var name = Poser.Domain.Scene.EntityNames.Next("World Light", _lights.Select(x => x.Name));
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

    /// <summary>Whether a game light is already in Poser's light list: taken
    /// over, made by Poser or a GPose camera light. The world-light list can
    /// hold all three after a second scene walk, so these are left out when
    /// lights are offered to take over.</summary>
    private bool IsListed(nint handle)
    {
        foreach (var light in _lights)
        {
            if (!light.IsValid)
                continue;
            if (light.Ownership == LightOwnership.World ? light.WorldAddress == handle : (nint)light.NativePtr == handle)
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
            // Seed first, then let camera-light refresh remove those slots from
            // the world-light candidates.
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
    }
}
