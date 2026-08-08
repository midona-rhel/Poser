using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using Poser.Core;
using Poser.Domain.Scene;
using Poser.Entities;
using Poser.Services;
using PoserTransform = Poser.Transform;

namespace Poser.Game.Lighting;

/// <summary>
/// Spawns and owns plugin-created scene lights through the game's own light
/// factory, and adopts the two kinds of light the plugin does not own: the
/// GPose camera lights (delisted, never destroyed) and copies of overworld
/// lights (the original is suppressed for as long as the copy lives).
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

    // Light.ctor — every scene light in the process passes through here,
    // which is how overworld lights become capture candidates.
    private const string LightCtorSignature =
        "E8 ?? ?? ?? ?? 48 89 84 ?? ?? ?? ?? ?? 48 85 C0 0F ?? ?? ?? ?? ?? 48 8B C8";

    private const string ToggleGPoseLightSignature =
        "48 83 EC 28 4C 8B C1 83 FA 03";

    // Gobo pair, Ktisis pattern first then Brio's. The two projects signature
    // the same two functions off different anchors and neither is guaranteed
    // to survive a patch, so each is tried in turn before the feature is
    // declared unavailable.
    private const string SetLightTextureSignatureKtisis =
        "40 53 48 83 EC ?? 48 8B D9 C7 44 24 ?? ?? ?? ?? ?? 33 C9";

    private const string SetLightTextureSignatureBrio =
        "40 53 48 83 ?? ?? 48 ?? ?? ?? 44 24 58 ?? ?? ?? ?? 33 ?? 48";

    private const string ClassifyPathSignatureKtisis =
        "40 53 48 83 EC ?? ?? ?? ?? ?? 4C 8B CA 0F BE 42";

    private const string ClassifyPathSignatureBrio =
        "40 53 48 83 ?? ?? 44 0F BE 02 ?? ?? ?? ??";

    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPose;
    private readonly ICameraService _camera;
    private readonly IEventBus _events;
    private readonly IObjectTable _objects;
    private readonly IGameInteropProvider _hooks;

    /// <summary>Light.Create — the game allocates and returns the object;
    /// the plugin never allocates one itself.</summary>
    private readonly delegate* unmanaged<uint, nint, void*, GameLight*> _createGameLight;

    /// <summary>Assigns a resource path as the light's projected texture.</summary>
    private readonly delegate* unmanaged<GameLight*, uint*, byte*, byte> _setLightTexture;

    /// <summary>Classifies a resource path into its resource category, which
    /// the texture assignment takes as its second argument.</summary>
    private readonly delegate* unmanaged<uint*, byte*, uint*> _classifyPath;

    private delegate GameLight* LightCtorDelegate(GameLight* light);
    private delegate nint LightDtorDelegate(GameLight* light, bool free);
    private delegate bool ToggleGPoseLightDelegate(GPoseLightController* state, uint index);

    private readonly Hook<LightCtorDelegate>? _lightCtorHook;
    private readonly Hook<ToggleGPoseLightDelegate>? _toggleGPoseLightHook;
    private Hook<LightDtorDelegate>? _lightDtorHook;
    private nint _destructorAddress;

    private readonly List<Light> _lights = new();
    private readonly IReadOnlyList<GoboEntry> _gobos;

    /// <summary>Every scene light the game has constructed and not yet
    /// destroyed, minus this plugin's own. Written from the ctor/dtor detours
    /// as well as the framework thread, hence the gate.</summary>
    private readonly HashSet<nint> _worldLights = new();
    private readonly object _worldGate = new();

    private readonly HashSet<Skeleton> _attachRefreshed = new();

    private static readonly TimeSpan GPosePollInterval = TimeSpan.FromSeconds(1);
    private DateTime _nextGPosePollUtc = DateTime.MinValue;

    private bool _disposed;

    public LightingService(
        ISigScanner sigScanner,
        IFramework framework,
        IPluginLog log,
        IGPoseService gPose,
        ICameraService camera,
        IEventBus events,
        IObjectTable objects,
        IGameInteropProvider hooks)
    {
        _framework = framework;
        _log = log;
        _gPose = gPose;
        _camera = camera;
        _events = events;
        _objects = objects;
        _hooks = hooks;

        _gobos = GoboLibrary.Load();

        var createAddress = TryScan(
            sigScanner, "Light.Create", CreateLightSignature);
        if (createAddress is { } create)
        {
            _createGameLight =
                (delegate* unmanaged<uint, nint, void*, GameLight*>)create;
            IsAvailable = true;
        }

        // Gobo availability is tracked apart from IsAvailable on purpose: a
        // patch that only breaks the texture pair must cost gobos, not lights.
        var textureAddress = TryScan(
            sigScanner,
            "light set-texture",
            SetLightTextureSignatureKtisis,
            SetLightTextureSignatureBrio);
        var classifyAddress = TryScan(
            sigScanner,
            "resource-path classify",
            ClassifyPathSignatureKtisis,
            ClassifyPathSignatureBrio);
        if (textureAddress is { } texture && classifyAddress is { } classify)
        {
            _setLightTexture =
                (delegate* unmanaged<GameLight*, uint*, byte*, byte>)texture;
            _classifyPath = (delegate* unmanaged<uint*, byte*, uint*>)classify;
            AreGobosAvailable = _gobos.Count > 0;
        }

        var ctorAddress = TryScan(sigScanner, "Light.ctor", LightCtorSignature);
        if (ctorAddress is { } ctor)
        {
            try
            {
                _lightCtorHook = _hooks.HookFromAddress<LightCtorDelegate>(
                    ctor, LightCtorDetour);
                _lightCtorHook.Enable();
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"LightingService: could not hook Light.ctor, overworld capture unavailable: {ex.Message}");
            }
        }

        var toggleAddress = TryScan(
            sigScanner, "GPose light toggle", ToggleGPoseLightSignature);
        if (toggleAddress is { } toggle)
        {
            try
            {
                _toggleGPoseLightHook =
                    _hooks.HookFromAddress<ToggleGPoseLightDelegate>(
                        toggle, ToggleGPoseLightDetour);
                _toggleGPoseLightHook.Enable();
            }
            catch (Exception ex)
            {
                _log.Warning(
                    $"LightingService: could not hook the GPose light toggle, camera lights will not be tracked: {ex.Message}");
            }
        }

        _events.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update += OnFrameworkUpdate;
    }

    public bool IsAvailable { get; }

    /// <summary>False when either gobo signature failed or the embedded
    /// library is empty; the light service itself stays usable.</summary>
    public bool AreGobosAvailable { get; }

    public IReadOnlyList<ILight> Lights => _lights;

    public IReadOnlyList<GoboEntry> Gobos => _gobos;

    private nint? TryScan(ISigScanner scanner, string name, params string[] patterns)
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

        _log.Warning(
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

        // A borrowed native is never destructed: a GPose camera light belongs
        // to the game and an overworld light belongs to the world.
        if (typed.Ownership != LightOwnership.Spawned)
        {
            ReleaseLight(light);
            return;
        }

        if (!_lights.Remove(typed))
            return;

        DestroyNative(typed);
        _events.Publish(new LightListChangedEvent(Lights));
    }

    public void ReleaseLight(ILight light)
    {
        if (light is not Light typed || typed.Ownership == LightOwnership.Spawned)
            return;
        if (!_lights.Remove(typed))
            return;

        ReleaseInternal(typed);
        _events.Publish(new LightListChangedEvent(Lights));
    }

    public void DestroyAllLights()
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
        _events.Publish(new LightListChangedEvent(Lights));
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

    /// <summary>Hands a borrowed native back: a world copy restores and drops
    /// its original then destroys the copy the plugin owns, a GPose light is
    /// only delisted.</summary>
    private void ReleaseInternal(Light light)
    {
        if (light.Ownership == LightOwnership.World)
        {
            RestoreWorldOriginal(light);
            DestroyNative(light);
            return;
        }

        light.Invalidate();
    }

    private void RestoreWorldOriginal(Light light)
    {
        var handle = light.WorldOriginal;
        light.WorldOriginal = nint.Zero;
        if (handle == nint.Zero)
            return;

        bool alive;
        lock (_worldGate)
            alive = _worldLights.Contains(handle);
        if (!alive)
            return;

        try
        {
            var native = (GameLight*)handle;
            if (native != null)
                native->IsVisible = true;
        }
        catch (Exception ex)
        {
            _log.Error(
                $"LightingService: failed to restore a suppressed world light: {ex.Message}");
        }
    }

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
            var transform = source != null
                ? source.Transform
                : new PoserTransform(
                    _camera.GetCameraPosition(), CameraRotation(), Vector3.One);

            var light = SpawnNative(
                kind, transform, LightOwnership.Spawned, GenerateName(kind));
            if (light == null)
                return null;

            if (source != null)
            {
                CopyProperties(source, light);
                if (source.GoboPath is { } gobo)
                    ApplyGoboPath(light, gobo);
            }

            light.NativePtr->Update();

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
        lock (_worldGate)
            _worldLights.Remove((nint)native);

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

        var sameKind = 0;
        foreach (var light in _lights)
        {
            if (light.Kind == kind)
                sameKind++;
        }
        // Every light carries its number, the first one included: an unnumbered
        // "Spot Light" beside "Spot Light 2" reads as a different sort of thing
        // rather than as the first of a series.
        return $"{baseName} {sameKind + 1}";
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
        if (!AreGobosAvailable)
            return false;
        if (!_framework.IsInFrameworkUpdateThread)
        {
            _log.Warning("LightingService: gobos must be applied on the framework thread");
            return false;
        }
        if (!SupportsGobo(typed.Kind))
            return false;

        return ApplyGoboPath(typed, gobo.Path);
    }

    public void ClearGobo(ILight light)
    {
        if (light is not Light typed)
            return;
        ClearGoboNative(typed);
    }

    /// <summary>Only spot and area lights project a texture; the game ignores
    /// it on the other two kinds.</summary>
    private static bool SupportsGobo(LightKind kind) =>
        kind is LightKind.Spot or LightKind.Area;

    [SkipLocalsInit]
    private bool ApplyGoboPath(Light light, string path)
    {
        if (!AreGobosAvailable || string.IsNullOrEmpty(path))
            return false;

        var native = light.NativePtr;
        if (native == null)
            return false;

        // The native assignment early-returns when a texture handle is already
        // present, so any previous gobo has to be released first.
        ClearGoboNative(light);

        try
        {
            var byteCount = Encoding.UTF8.GetByteCount(path);
            Span<byte> buffer = byteCount > 511
                ? (Span<byte>)new byte[byteCount + 1]
                : stackalloc byte[512];
            Encoding.UTF8.GetBytes(path.AsSpan(), buffer);
            buffer[byteCount] = 0;

            fixed (byte* pathPtr = buffer)
            {
                var category = 0xFFFFFFFFu;
                _classifyPath(&category, pathPtr);
                var result = _setLightTexture(native, &category, pathPtr);

                native->UpdateRender();
                native->Update();

                if (result == 0)
                {
                    _log.Warning(
                        $"LightingService: the game refused gobo '{path}' for light '{light.Name}'");
                    return false;
                }
            }

            light.SetGoboPath(path);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to apply gobo '{path}': {ex.Message}");
            return false;
        }
    }

    private void ClearGoboNative(Light light)
    {
        var native = light.NativePtr;
        if (native == null)
        {
            light.SetGoboPath(null);
            return;
        }

        try
        {
            if (native->ProjectedCubemapTexture != null)
            {
                native->ProjectedCubemapTexture->DecRef();
                native->ProjectedCubemapTexture = null;
            }
            if (native->LightRenderObject != null)
                native->LightRenderObject->Texture = null;
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to clear a gobo: {ex.Message}");
        }

        light.SetGoboPath(null);
    }

    #endregion

    #region GPose camera lights

    private static GPoseLightController* GetGPoseController()
    {
        var framework = EventFramework.Instance();
        if (framework == null)
            return null;
        return (GPoseLightController*)
            &framework->EventSceneModule.EventGPoseController;
    }

    private bool ToggleGPoseLightDetour(GPoseLightController* state, uint index)
    {
        var result = _toggleGPoseLightHook!.Original(state, index);
        try
        {
            if (!_disposed && _gPose.IsGPosing)
                RefreshGPoseLights();
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to track a GPose light toggle: {ex}");
        }
        return result;
    }

    /// <summary>Reconciles the three camera-light slots against the list. A
    /// slot the game emptied is delisted, never destroyed.</summary>
    private void RefreshGPoseLights()
    {
        var controller = GetGPoseController();
        if (controller == null)
            return;

        var changed = false;
        for (var slot = 0u; slot < GPoseLightController.LightCount; slot++)
        {
            var native = controller->GetLight(slot);
            var tracked = FindGPoseLight((int)slot);

            if (native == null)
            {
                if (tracked == null)
                    continue;
                _lights.Remove(tracked);
                tracked.Invalidate();
                changed = true;
                continue;
            }

            if (tracked != null)
            {
                if (tracked.NativePtr == native)
                    continue;
                _lights.Remove(tracked);
                tracked.Invalidate();
            }

            // The game constructed it, so it is sitting in the overworld set;
            // a camera light is not a capture candidate.
            lock (_worldGate)
                _worldLights.Remove((nint)native);

            _lights.Add(new Light(
                native, $"Camera Light {slot + 1}", LightOwnership.GPose)
            {
                GPoseSlot = (int)slot,
            });
            changed = true;
        }

        if (changed)
            _events.Publish(new LightListChangedEvent(Lights));
    }

    /// <summary>Backfill, Ktisis' RefreshLightEntities: the toggle hook covers
    /// the player toggling a camera light, but not the lights the game already
    /// had when GPose opened.</summary>
    private void PollGPoseLights()
    {
        if (!_gPose.IsGPosing)
            return;
        var now = DateTime.UtcNow;
        if (now < _nextGPosePollUtc)
            return;
        _nextGPosePollUtc = now + GPosePollInterval;
        RefreshGPoseLights();
    }

    private Light? FindGPoseLight(int slot)
    {
        foreach (var light in _lights)
        {
            if (light.Ownership == LightOwnership.GPose && light.GPoseSlot == slot)
                return light;
        }
        return null;
    }

    #endregion

    #region Overworld capture

    private GameLight* LightCtorDetour(GameLight* light)
    {
        var result = _lightCtorHook!.Original(light);
        try
        {
            if (_disposed || light == null)
                return result;

            lock (_worldGate)
                _worldLights.Add((nint)light);

            // The destructor has no signature of its own; its address comes
            // out of the first constructed light's virtual table, and the hook
            // is installed off the detour rather than inside it.
            if (_destructorAddress == nint.Zero && light->VirtualTable != null)
            {
                _destructorAddress = (nint)light->VirtualTable->Destructor;
                var address = _destructorAddress;
                _framework.RunOnTick(() => HookDestructor(address));
            }
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: light constructor tracking failed: {ex}");
        }
        return result;
    }

    private void HookDestructor(nint address)
    {
        if (_disposed || _lightDtorHook != null || address == nint.Zero)
            return;
        try
        {
            _lightDtorHook =
                _hooks.HookFromAddress<LightDtorDelegate>(address, LightDtorDetour);
            _lightDtorHook.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"LightingService: could not hook the light destructor, stale capture candidates will not be pruned: {ex.Message}");
        }
    }

    private nint LightDtorDetour(GameLight* light, bool free)
    {
        try
        {
            var handle = (nint)light;
            var known = false;
            lock (_worldGate)
                known = _worldLights.Remove(handle);
            if (known && !_disposed)
                _framework.RunOnTick(() => OnNativeLightDied(handle));
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: light destructor tracking failed: {ex}");
        }
        return _lightDtorHook!.Original(light, free);
    }

    /// <summary>A native the plugin borrowed has gone away: drop anything
    /// bound to it without touching the freed memory.</summary>
    private void OnNativeLightDied(nint handle)
    {
        if (_disposed)
            return;

        var changed = false;
        foreach (var light in _lights.ToArray())
        {
            if (light.Ownership == LightOwnership.World &&
                light.WorldOriginal == handle)
            {
                // The original is already gone — clear it first so the release
                // never writes through the dead pointer.
                light.WorldOriginal = nint.Zero;
                _lights.Remove(light);
                DestroyNative(light);
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
            _events.Publish(new LightListChangedEvent(Lights));
    }

    public IReadOnlyList<WorldLightCandidate> GetWorldLightCandidates()
    {
        if (!IsAvailable || _lightCtorHook == null || !_gPose.IsGPosing)
            return Array.Empty<WorldLightCandidate>();
        if (!_framework.IsInFrameworkUpdateThread)
            return Array.Empty<WorldLightCandidate>();

        nint[] handles;
        lock (_worldGate)
        {
            if (_worldLights.Count == 0)
                return Array.Empty<WorldLightCandidate>();
            handles = new nint[_worldLights.Count];
            _worldLights.CopyTo(handles);
        }

        var origin = _objects.LocalPlayer?.Position ?? _camera.GetCameraPosition();
        var candidates = new List<WorldLightCandidate>(handles.Length);
        foreach (var handle in handles)
        {
            if (IsCaptured(handle))
                continue;
            var native = (GameLight*)handle;
            if (native == null || native->LightRenderObject == null)
                continue;
            Vector3 position = native->Transform.Position;
            candidates.Add(new WorldLightCandidate(
                handle, Vector3.Distance(position, origin)));
        }

        candidates.Sort(static (left, right) =>
            left.DistanceFromPlayer.CompareTo(right.DistanceFromPlayer));
        return candidates;
    }

    public ILight? CaptureWorldLight(WorldLightCandidate candidate)
    {
        if (!CanSpawn())
            return null;

        bool known;
        lock (_worldGate)
            known = _worldLights.Contains(candidate.Handle);
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

        try
        {
            var source = original->LightRenderObject;
            var kind = Light.ToKind(source->EmissionType);
            var transform = new PoserTransform(
                original->Transform.Position,
                original->Transform.Rotation,
                original->Transform.Scale);

            var light = SpawnNative(
                kind, transform, LightOwnership.World, UniqueName("World Light"));
            if (light == null)
                return null;

            var target = light.NativePtr->LightRenderObject;
            if (target != null)
            {
                target->LightFlags = source->LightFlags;
                target->EmissionType = source->EmissionType;
                target->ColorIntensity = source->ColorIntensity;
                target->ShadowPlaneNear = source->ShadowPlaneNear;
                target->ShadowPlaneFar = source->ShadowPlaneFar;
                target->FalloffType = source->FalloffType;
                target->AreaAngle = source->AreaAngle;
                target->Falloff = source->Falloff;
                target->LightAngle = source->LightAngle;
                target->FalloffAngle = source->FalloffAngle;
                target->Range = source->Range;
                target->CharacterShadowRange = source->CharacterShadowRange;
            }

            light.WorldOriginal = candidate.Handle;
            AdoptGobo(light, original);

            // Copy-and-suppress: the original stays alive but is held
            // invisible for as long as the copy exists, so the scene keeps
            // exactly one of the two lights lit.
            original->IsVisible = false;

            light.NativePtr->Update();

            _log.Debug(
                $"LightingService: captured world light {candidate.Handle:X} as '{light.Name}'");
            _events.Publish(new LightListChangedEvent(Lights));
            return light;
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: failed to capture a world light: {ex}");
            return null;
        }
    }

    /// <summary>Adopts the original's projected texture path when it has one,
    /// matched against the embedded library so the UI can name it.</summary>
    private void AdoptGobo(Light light, GameLight* original)
    {
        if (!AreGobosAvailable || original->ProjectedCubemapTexture == null)
            return;

        string path;
        try
        {
            path = original->ProjectedCubemapTexture->FileName.ToString();
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrEmpty(path) || !SupportsGobo(light.Kind))
            return;
        ApplyGoboPath(light, path);
    }

    private bool IsCaptured(nint handle)
    {
        foreach (var light in _lights)
        {
            if (light.Ownership == LightOwnership.World &&
                light.WorldOriginal == handle)
                return true;
        }
        return false;
    }

    #endregion

    #region Bone attachment

    /// <summary>Soft attach, Ktisis' model: the bone drives the light's world
    /// position and rotation every frame, scale is left to the user, and
    /// nothing about it is serialized.</summary>
    private bool ApplyBoneAttachment(Light light)
    {
        var bone = light.AttachedBone;
        if (bone == null)
            return true;

        var world = TryGetBoneWorldTransform(bone);
        if (world == null)
        {
            light.AttachedBone = null;
            _log.Debug(
                $"LightingService: '{light.Name}' detached — its bone is gone");
            return false;
        }

        var current = light.Transform;
        light.Transform = new PoserTransform(
            world.Value.Position, world.Value.Rotation, current.Scale);
        return true;
    }

    private PoserTransform? TryGetBoneWorldTransform(IBone bone)
    {
        if (bone.Skeleton is not Skeleton skeleton || !skeleton.IsValid)
            return null;

        // Update-phase refresh of the display cache only, and once per
        // skeleton per tick however many lights hang off it.
        if (_attachRefreshed.Add(skeleton))
            skeleton.UpdateBoneTransforms(BoneCacheTypes.LastTransform);

        var world = PoserTransform.FromMatrix(
            bone.LastTransform.ToMatrix() * skeleton.GetModelMatrix());
        if (!IsFinite(world.Position) || !IsFinite(world.Rotation))
            return null;
        return world;
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    #endregion

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (framework.IsFrameworkUnloading || _disposed)
            return;

        PollGPoseLights();

        if (_lights.Count == 0)
            return;

        _attachRefreshed.Clear();
        var detached = false;

        foreach (var light in _lights)
        {
            if (!light.IsValid)
                continue;

            if (light.AttachedBone != null && !ApplyBoneAttachment(light))
                detached = true;

            // A light switched away from spot or area cannot project, so the
            // texture goes with the switch rather than lingering unused.
            if (light.GoboPath != null && !SupportsGobo(light.Kind))
                ClearGoboNative(light);

            SuppressWorldOriginal(light);

            if (!light.IsOn)
                continue;

            var native = light.NativePtr;
            native->UpdateRender();
            native->Update();
        }

        if (detached)
            _events.Publish(new LightListChangedEvent(Lights));
    }

    private void SuppressWorldOriginal(Light light)
    {
        if (light.Ownership != LightOwnership.World ||
            light.WorldOriginal == nint.Zero)
            return;

        bool alive;
        lock (_worldGate)
            alive = _worldLights.Contains(light.WorldOriginal);
        if (!alive)
            return;

        var original = (GameLight*)light.WorldOriginal;
        if (original != null)
            original->IsVisible = false;
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent evt)
    {
        _nextGPosePollUtc = DateTime.MinValue;
        if (evt.IsGPosing)
            RefreshGPoseLights();
        else
            DestroyAllLights();
    }

    public void Dispose()
    {
        _disposed = true;
        _events.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _framework.Update -= OnFrameworkUpdate;
        DestroyAllLights();
        _toggleGPoseLightHook?.Dispose();
        _lightDtorHook?.Dispose();
        _lightCtorHook?.Dispose();
        GC.SuppressFinalize(this);
    }
}
