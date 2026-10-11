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
/// The overworld light set: every scene light the game has constructed and
/// not yet destroyed, minus this plugin's own, each with a generation so a
/// reused address is never mistaken for the light a capture borrowed. The
/// Light.ctor hook adds, the destructor hook (installed from the first light's
/// virtual table) prunes, and a graph walk seeds the lights that predate the
/// hooks.
/// </summary>
internal sealed unsafe class WorldLightCapture : IDisposable
{
    // Light.ctor — every scene light in the process passes through here,
    // which is how overworld lights become capture candidates.
    private const string LightCtorSignature =
        "E8 ?? ?? ?? ?? 48 89 84 ?? ?? ?? ?? ?? 48 85 C0 0F ?? ?? ?? ?? ?? 48 8B C8";

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

    /// <summary>Every scene light the game has constructed and not yet
    /// destroyed, minus this plugin's own. Written from the ctor/dtor detours
    /// as well as the framework thread, hence the gate.</summary>
    private readonly HashSet<nint> _worldLights = new();
    private readonly Dictionary<nint, long> _worldLightGenerations = new();
    private long _nextWorldLightGeneration;
    private readonly object _worldGate = new();

    /// <summary>Whether the world's existing lights have been walked for this
    /// GPose session. The ctor hook only ever sees lights constructed AFTER it
    /// was installed, so a zone that was already loaded — every dev hot-reload,
    /// and every session entered without a territory change — contributes
    /// nothing through it. The seed is what makes those lights exist to the
    /// listing; this flag is what stops an empty listing re-walking the graph
    /// on every frame.</summary>
    private bool _worldLightsSeeded;

    /// <param name="nativeLightDied">Run on the next framework tick when a
    /// tracked native light is destroyed, with its handle and generation.</param>
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

        var ctorAddress = LightingService.TryScan(sigScanner, log, "Light.ctor", LightCtorSignature);
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
    }

    public bool HasConstructorHook => _lightCtorHook != null;

    /// <summary>Without the destructor hook nothing is pruned, so nothing is
    /// tracked or trusted.</summary>
    public bool HasDestructorHook => _lightDtorHook != null;

    /// <summary>The next listing walks the graph again.</summary>
    public void Unseed() => _worldLightsSeeded = false;

    /// <summary>Drops a handle the plugin itself owns or delists (its own
    /// spawn, a camera light) from the overworld set.</summary>
    public void Forget(nint handle)
    {
        lock (_worldGate)
            ForgetWorldLight(handle);
    }

    /// <summary>Drops a handle from the overworld set together with its
    /// generation, so neither outlives the native. Caller holds _worldGate.</summary>
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
        try
        {
            if (_disposed() || light == null)
                return result;

            // A handle is tracked only while the destructor hook can prune
            // it; one constructed before that hook exists is found by the
            // re-walk the hook's installation schedules.
            if (_lightDtorHook != null)
            {
                lock (_worldGate)
                {
                    _worldLights.Add((nint)light);
                    _worldLightGenerations[(nint)light] = ++_nextWorldLightGeneration;
                }
            }

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
        if (_disposed() || _lightDtorHook != null || address == nint.Zero)
            return;
        try
        {
            _lightDtorHook =
                _hooks.HookFromAddress<LightDtorDelegate>(address, LightDtorDetour);
            _lightDtorHook.Enable();
            // Lights constructed before this point were never tracked; the
            // next listing walks the graph for them.
            _worldLightsSeeded = false;
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
            long generation;
            lock (_worldGate)
            {
                generation = _worldLightGenerations.GetValueOrDefault(handle);
                known = ForgetWorldLight(handle);
            }
            if (known && !_disposed())
                _framework.RunOnTick(() => _nativeLightDied(handle, generation));
        }
        catch (Exception ex)
        {
            _log.Error($"LightingService: light destructor tracking failed: {ex}");
        }
        return _lightDtorHook!.Original(light, free);
    }

    /// <summary>
    /// Walks the world's scene graph for the lights that already exist and adds
    /// them to the tracked set.
    ///
    /// <para>WHY THIS EXISTS: <see cref="LightCtorDetour"/> is the only other
    /// writer, and a hook sees nothing that was constructed before it was
    /// installed. A light belonging to an already-loaded zone therefore never
    /// entered the set, which made the world-light listing permanently empty
    /// for exactly the case it is tested in. The hook is the LIVENESS half and
    /// stays; this is the SEED half.</para>
    ///
    /// <para>Ktisis reaches the same lights the same way and no other way:
    /// one recursion of <c>World.Instance()</c>'s object graph, partitioned by
    /// <c>ObjectType.Light</c> (<c>Ktisis/Services/Game/WorldService.cs:39-42</c>),
    /// rebuilt on its own GPose-enter event (<c>:27-30</c>), with the node's
    /// address used as the light pointer directly
    /// (<c>Scene/Entities/World/LightEntity.cs:114</c>). Overlap with the hook's
    /// additions costs nothing — the set is a hash set.</para>
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

        // Every stored handle must be one the destructor hook will prune.
        // These were walked this frame, so their virtual tables are live:
        // install the hook from the first before storing anything, and store
        // nothing when it cannot be installed.
        if (_lightDtorHook == null && _destructorAddress == nint.Zero)
        {
            foreach (var handle in found)
            {
                var native = (GameLight*)handle;
                if (native == null || native->VirtualTable == null)
                    continue;
                _destructorAddress = (nint)native->VirtualTable->Destructor;
                HookDestructor(_destructorAddress);
                break;
            }
            // A successful install asked for a re-walk; this walk is it.
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

    /// <summary>The tracked lights not already captured, nearest the player
    /// first. The caller has checked availability and the framework thread.</summary>
    public IReadOnlyList<WorldLightCandidate> Candidates(Func<nint, bool> isCaptured)
    {
        // The lazy half of the seed, for the session this service was
        // constructed INSIDE of — a hot reload raises no GPose-enter event —
        // and for the lights that predate the destructor hook.
        if (!_worldLightsSeeded)
            SeedWorldLights();
        // Without the destructor hook nothing is pruned, so nothing is
        // tracked or trusted.
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
            if (native == null || native->LightRenderObject == null)
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

    /// <summary>Disposes the destructor hook, then the constructor hook — the
    /// order the service always tore them down in.</summary>
    public void Dispose()
    {
        _lightDtorHook?.Dispose();
        _lightCtorHook?.Dispose();
    }
}
