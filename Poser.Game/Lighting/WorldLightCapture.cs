using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using Poser.Application.Viewport;
using Poser.Game.WorldObjects;
using Poser.Game.Services;

namespace Poser.Game.Lighting;

/// <summary>
/// The list of world lights Poser can take over: every light the game has made
/// and not yet freed, apart from Poser's own lights and the GPose camera
/// lights. Each light gets a generation number when it is listed, so a new
/// light the game makes at the address of a freed one is never mistaken for
/// the light that was taken over. A hook on the light constructor adds lights,
/// a hook on the light destructor removes them, and a walk of the scene finds
/// the lights that were made before the hooks.
/// </summary>
internal sealed unsafe class WorldLightCapture : IDisposable
{
    // Every light the game makes passes through its constructor.
    private const string LightCtorSignature =
        Poser.Game.Natives.NativeSignatures.Light_ctor;

    private delegate GameLight* LightCtorDelegate(GameLight* light);
    private delegate nint LightDtorDelegate(GameLight* light, bool free);

    private readonly IFramework _framework;
    private readonly IPluginLog _log;
    private readonly ICameraProjection _camera;
    private readonly IObjectTable _objects;
    private readonly IGameInteropProvider _hooks;
    private readonly IWorldGraphPort _worldGraph;
    private readonly Func<bool> _disposed;
    private readonly Action<nint, long> _nativeLightDied;

    private readonly Hook<LightCtorDelegate>? _lightCtorHook;
    private Hook<LightDtorDelegate>? _lightDtorHook;
    private nint _destructorAddress;

    /// <summary>The listed lights. The game can make and free lights on other
    /// threads than the frame update, so every use takes the lock.</summary>
    private readonly HashSet<nint> _worldLights = new();
    private readonly Dictionary<nint, long> _worldLightGenerations = new();
    private long _nextWorldLightGeneration;
    private readonly object _worldGate = new();

    /// <summary>True once the scene has been walked for lights in this GPose
    /// session, so an empty list doesn't cause a walk every frame.</summary>
    private bool _worldLightsSeeded;

    /// <param name="nativeLightDied">Called on the next frame when the game
    /// frees a listed light, with its address and generation.</param>
    public WorldLightCapture(
        ISigScanner sigScanner,
        IFramework framework,
        IPluginLog log,
        ICameraProjection camera,
        IObjectTable objects,
        IGameInteropProvider hooks,
        IWorldGraphPort worldGraph,
        Func<bool> disposed,
        Action<nint, long> nativeLightDied)
    {
        _framework = framework;
        _log = log;
        _camera = camera;
        _objects = objects;
        _hooks = hooks;
        _worldGraph = worldGraph;
        _disposed = disposed;
        _nativeLightDied = nativeLightDied;

        _lightCtorHook = LightingService.TryHook<LightCtorDelegate>(_hooks, log,
            LightingService.TryScan(sigScanner, log, "Light.ctor", LightCtorSignature),
            LightCtorDetour, "could not hook Light.ctor, world lights can't be taken over");
    }

    public bool HasConstructorHook => _lightCtorHook != null;

    /// <summary>Without the destructor hook a freed light would stay listed,
    /// so nothing is listed at all.</summary>
    public bool HasDestructorHook => _lightDtorHook != null;

    /// <summary>The next listing walks the scene again.</summary>
    public void Unseed() => _worldLightsSeeded = false;

    /// <summary>Takes a light that isn't a world light, such as one Poser
    /// made or a GPose camera light, off the list.</summary>
    public void Forget(nint handle)
    {
        lock (_worldGate)
            ForgetWorldLight(handle);
    }

    /// <summary>Takes a light and its generation off the list. The caller
    /// holds the lock.</summary>
    private bool ForgetWorldLight(nint handle)
    {
        _worldLightGenerations.Remove(handle);
        return _worldLights.Remove(handle);
    }

    public bool IsCurrentWorldLight(nint handle, long generation)
    {
        lock (_worldGate)
            return _worldLights.Contains(handle)
                && _worldLightGenerations.GetValueOrDefault(handle) == generation;
    }

    private GameLight* LightCtorDetour(GameLight* light)
    {
        var result = _lightCtorHook!.Original(light);
        if (_disposed())
            return result;

        // A light is only listed while the destructor hook runs, because only
        // then is it taken off the list when the game frees it. Lights made
        // before that hook existed are found by walking the scene again.
        if (_lightDtorHook != null)
        {
            lock (_worldGate)
            {
                _worldLights.Add((nint)light);
                _worldLightGenerations[(nint)light] = ++_nextWorldLightGeneration;
            }
        }

        // The destructor can't be found by a signature; its address is read
        // from the first light's function table. The hook is made on the next
        // frame instead of inside this detour.
        if (_destructorAddress == nint.Zero && light->VirtualTable != null)
        {
            _destructorAddress = (nint)light->VirtualTable->Destructor;
            var address = _destructorAddress;
            _framework.RunOnTick(() => HookDestructor(address));
        }
        return result;
    }

    private void HookDestructor(nint address)
    {
        if (_disposed() || _lightDtorHook != null)
            return;
        _lightDtorHook = LightingService.TryHook<LightDtorDelegate>(_hooks, _log, address,
            LightDtorDetour, "could not hook the light destructor, world lights can't be taken over");
        // Lights made before this point were never listed; the next listing
        // walks the scene for them.
        if (_lightDtorHook != null)
            _worldLightsSeeded = false;
    }

    private nint LightDtorDetour(GameLight* light, bool free)
    {
        var handle = (nint)light;
        bool known;
        long generation;
        lock (_worldGate)
        {
            generation = _worldLightGenerations.GetValueOrDefault(handle);
            known = ForgetWorldLight(handle);
        }
        if (known && !_disposed())
            _framework.RunOnTick(() => _nativeLightDied(handle, generation));
        return _lightDtorHook!.Original(light, free);
    }

    /// <summary>
    /// Lists the lights in the scene that were made before the constructor
    /// hook existed.
    /// </summary>
    public void SeedWorldLights()
    {
        if (_disposed() || !_framework.IsInFrameworkUpdateThread)
            return;
        if (!_worldGraph.IsAvailable)
            return;

        _worldLightsSeeded = true;

        IReadOnlyList<nint> found;
        try
        {
            found = _worldGraph.EnumerateLights();
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: seeding the world lights failed: {ex}");
            return;
        }

        // A light may only be listed while the destructor hook runs. When no
        // light has been made since Poser loaded, the hook doesn't exist yet,
        // so it is made from the first light found here. Nothing is listed
        // when it can't be made.
        if (_lightDtorHook == null && _destructorAddress == nint.Zero)
        {
            foreach (var handle in found)
            {
                var native = (GameLight*)handle;
                if (native->VirtualTable == null)
                    continue;
                _destructorAddress = (nint)native->VirtualTable->Destructor;
                HookDestructor(_destructorAddress);
                break;
            }
            // Making the hook asks for another walk; this walk is that one.
            _worldLightsSeeded = true;
        }
        if (_lightDtorHook == null)
            return;

        var added = 0;
        lock (_worldGate)
        {
            foreach (var handle in found)
            {
                if (handle != nint.Zero && _worldLights.Add(handle))
                {
                    _worldLightGenerations[handle] = ++_nextWorldLightGeneration;
                    added++;
                }
            }
        }

        if (added > 0)
            _log.Debug(
                $"LightingService: seeded {added} pre-existing world light(s) from the scene graph.");
    }

    /// <summary>The listed lights that aren't taken over yet, nearest the
    /// player first. The caller has checked that lights work and that this
    /// runs on the frame update.</summary>
    public IReadOnlyList<WorldLightCandidate> Candidates(Func<nint, bool> isCaptured)
    {
        // Walks the scene when Poser was loaded while already in GPose, which
        // sends no "GPose started" event, and after the destructor hook was made.
        if (!_worldLightsSeeded)
            SeedWorldLights();
        if (_lightDtorHook == null)
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
            if (isCaptured(handle))
                continue;
            var native = (GameLight*)handle;
            if (native->LightRenderObject == null)
                continue;
            Vector3 position = native->Transform.Position;
            long generation;
            lock (_worldGate) generation = _worldLightGenerations.GetValueOrDefault(handle);
            candidates.Add(new WorldLightCandidate(
                handle, Vector3.Distance(position, origin), position, generation));
        }

        candidates.Sort(static (left, right) =>
            left.DistanceFromPlayer.CompareTo(right.DistanceFromPlayer));
        return candidates;
    }

    public void Dispose()
    {
        _lightDtorHook?.Dispose();
        _lightCtorHook?.Dispose();
    }
}
