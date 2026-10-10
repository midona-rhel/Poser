using System;
using Poser.Services;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using CSObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSVfx = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;
using CSWorld = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.World;

using Poser.Domain.Scene;
using Poser.Application.World;
using Poser.Domain.Transforms;

namespace Poser.Game.WorldObjects;

/// <summary>
/// Reads and writes BG objects through the game's world-object graph.
/// Native fields are accessed through FFXIVClientStructs; writes refresh the
/// render and culling state that depends on placement. The graph walk, the
/// BG-only state and the VFX natives live in <see cref="WorldGraphWalker"/>,
/// <see cref="BgObjectNative"/> and <see cref="VfxObjectNative"/>; this port
/// owns node identity, the shared per-node reads and writes, spawn and
/// teardown.
/// </summary>
public sealed unsafe class NativeWorldObjectPort : IWorldObjectPort, IDisposable
{
    private readonly IPluginLog _log;
    private readonly FurnitureLayoutDriver _furniture;
    private readonly WorldNodeResolver _resolver;
    private readonly WorldGraphWalker _graph;
    private readonly BgObjectNative _bg;
    private readonly VfxObjectNative _vfx;
    private readonly object _handledLock = new();
    private readonly Dictionary<nint, (nint Resource, long Generation)>
        _incarnations = new();
    // BG objects this port created; their identity survives a walk that
    // does not reach them.
    private readonly HashSet<nint> _spawned = new();
    private long _nextGeneration;
    private bool _disposed;

    /// <summary>A vfx's drawn state is its ALPHA (Brio's rule); the draw
    /// flag says nothing for an effect.</summary>
    private const int VfxAlphaOffset = VfxObjectNative.VfxAlphaOffset;

    public NativeWorldObjectPort(
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop,
        IPluginLog log,
        IDataManager data)
    {
        _log = log;
        _furniture = new FurnitureLayoutDriver(sigScanner, data, log);
        _resolver = new WorldNodeResolver(_furniture, log);
        _graph = new WorldGraphWalker(log, _furniture, RetainPresentIdentities);
        _bg = new BgObjectNative(_furniture, _resolver);
        // The VFX natives and their resource-load hook are installed here,
        // after the furniture driver, exactly where they always were.
        _vfx = new VfxObjectNative(
            sigScanner, gameInterop, log, _resolver, _handledLock, TryDestroyNative);
    }

    /// <summary>Whether the path names a VFX rather than a model — the one
    /// dispatch fact the whole arm turns on.</summary>
    public static bool IsVfxPath(string path) =>
        path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase);

    public bool IsAvailable => CSWorld.Instance() != null;

    public IReadOnlyList<WorldObjectRow> Enumerate() => _graph.Enumerate();

    public IReadOnlyList<nint> EnumerateLights() => _graph.EnumerateLights();

    /// <summary>After a complete walk, forgets the identity of every address
    /// the graph no longer holds unless this port owns it, so the identity
    /// maps stay bounded by the live world rather than by every object ever
    /// listed. A still-present address keeps its generation, which is what
    /// candidate and adoption checks compare.</summary>
    private void RetainPresentIdentities(HashSet<nint> visited)
    {
        _vfx.Ownership.RetainObserved(visited);
        lock (_handledLock)
        {
            foreach (var address in _incarnations.Keys)
                if (!visited.Contains(address) && !_spawned.Contains(address))
                    _incarnations.Remove(address);
        }
    }

    public void Pump() => _furniture.Pump();

    public IReadOnlyList<FurnitureLightState> ReadFurnitureLights(nint address) => _furniture.ReadLights(address);
    public void WriteFurnitureLights(nint address, IReadOnlyList<FurnitureLightState> lights) =>
        _furniture.SetLights(address, lights);

    public bool WriteFurnitureColor(nint address, byte stain, System.Numerics.Vector3? tint) =>
        !_furniture.Contains(address) || _furniture.SetColor(address, stain, tint);

    public bool IsAlive(nint address) => _furniture.Contains(address) || Resolve(address) != null;

    public bool TryReadIncarnation(
        nint address, out WorldObjectIncarnation incarnation)
    {
        if (_furniture.TryIdentity(address, out incarnation)) return true;
        incarnation = default;
        var node = Resolve(address);
        if (node == null)
            return false;
        nint resource = node->GetObjectType() == ObjectType.VfxObject
            ? (nint)((CSVfx*)node)->VfxResourceInstance
            : (nint)((BgObject*)node)->ModelResourceHandle;
        bool isVfx = node->GetObjectType() == ObjectType.VfxObject;
        if (isVfx)
        {
            incarnation = _vfx.Ownership.Observe(address, resource);
            return true;
        }
        lock (_handledLock)
        {
            if (!_incarnations.TryGetValue(address, out var prior)
                || (prior.Resource != nint.Zero && prior.Resource != resource))
                prior = (resource, ++_nextGeneration);
            // Attaching the first model resource is streaming, not allocation.
            prior.Resource = resource;
            _incarnations[address] = prior;
            incarnation = new WorldObjectIncarnation(
                address, prior.Generation, resource, isVfx);
            }
        return true;
    }

    public bool TryRead(nint address, out Transform placement)
    {
        if (_furniture.Contains(address)) { placement = _furniture.Read(address); return true; }
        placement = Transform.Identity;
        var node = Resolve(address);
        if (node == null)
            return false;
        placement = new Transform(node->Position, node->Rotation, node->Scale);
        return true;
    }

    public void Write(nint address, in Transform placement)
    {
        if (_furniture.Contains(address)) { _furniture.Write(address, placement); return; }
        var node = Resolve(address);
        if (node == null)
            return;
        node->Position = placement.Position;
        node->Rotation = placement.Rotation;
        node->Scale = placement.Scale;
        if (node->GetObjectType() == ObjectType.VfxObject)
        {
            // Brio's StaticVfxObject.SetTransform: notify and re-cull,
            // NOTHING else — replaying here restarted the effect on every
            // drag tick (and would un-pause a paused one). Brio resumes
            // only behind its own ShouldResume flag, which we don't carry.
            var moved = (CSVfx*)node;
            moved->NotifyTransformChanged();
            moved->UpdateCulling();
            return;
        }
        // Placement alone does not update the dependent render and culling
        // state — but the refreshes are GATED on the model being fully
        // loaded, Brio's BgObjectEx gate (LoadState 7), and use Brio's own
        // pair (culling + transforms; it never calls UpdateRender on a BG
        // object). Refreshing a still-streaming object crashed the
        // renderer on scene-load spawns (2026-08-31). An early write still
        // lands: the game derives its initial state from the fields when
        // the load completes.
        var bg = (BgObject*)node;
        if (!BgObjectNative.RenderReady(bg))
            return;
        bg->UpdateCulling();
        bg->UpdateTransforms(false);
    }

    public void WriteVfxTransform(nint address, in Transform placement)
    {
        Write(address, placement);
    }

    public bool TryWriteVfxTransform(nint address, in Transform placement)
    {
        if (!TryReadVfxState(address, out _, out _, out _))
            return false;
        WriteVfxTransform(address, placement);
        // Placement itself is synchronous; the playback readback confirms
        // that playback remains observable after the write.
        return TryReadVfxPlayback(address, out var playback)
            && playback != VfxPlaybackState.Unavailable;
    }

    public bool TryReadVfxPlayback(
        nint address, out VfxPlaybackState playback) =>
        _vfx.TryReadVfxPlayback(address, out playback);

    public bool TryReadFlags(nint address, out byte flags)
    {
        if (_furniture.Contains(address)) { flags = _furniture.Visible(address) ? (byte)1 : (byte)0; return true; }
        flags = 0;
        var node = Resolve(address);
        if (node == null)
            return false;
        flags = ((DrawObject*)node)->Flags;
        return true;
    }

    public void WriteFlags(nint address, byte flags)
    {
        if (_furniture.Contains(address)) { _furniture.SetVisible(address, flags != 0); return; }
        var node = Resolve(address);
        if (node == null)
            return;
        ((DrawObject*)node)->Flags = flags;
    }

    public bool TryReadVisible(nint address, out bool visible)
    {
        if (_furniture.Contains(address)) { visible = _furniture.Visible(address); return true; }
        visible = false;
        var node = Resolve(address);
        if (node == null)
            return false;
        visible = node->GetObjectType() == ObjectType.VfxObject
            ? *(float*)((byte*)node + VfxAlphaOffset) > 0f
            : ((DrawObject*)node)->IsVisible;
        return true;
    }

    public void WriteVisible(nint address, bool visible)
    {
        if (_furniture.Contains(address)) { _furniture.SetVisible(address, visible); return; }
        var node = Resolve(address);
        if (node == null)
            return;
        if (node->GetObjectType() == ObjectType.VfxObject)
            *(float*)((byte*)node + VfxAlphaOffset) = visible ? 1f : 0f;
        else
            ((DrawObject*)node)->IsVisible = visible;
    }

    public bool TryReadOpacity(nint address, out float opacity)
    {
        if (_furniture.Contains(address)) { opacity = _furniture.Opacity(address); return true; }
        var node = Resolve(address);
        opacity = 1f;
        if (node == null)
            return false;
        // BG dither runs in the opposite direction to VFX alpha.
        opacity = node->GetObjectType() == ObjectType.VfxObject
            ? ((CSVfx*)node)->Color.W
            : 1f - ((BgObject*)node)->GetTransparency();
        return float.IsFinite(opacity) && opacity is >= 0f and <= 1f;
    }

    public void WriteOpacity(nint address, float opacity)
    {
        if (_furniture.Contains(address)) { _furniture.SetOpacity(address, opacity); return; }
        var node = Resolve(address);
        if (node == null)
            return;
        float clamped = Math.Clamp(opacity, 0f, 1f);
        if (node->GetObjectType() == ObjectType.VfxObject)
            *(float*)((byte*)node + VfxAlphaOffset) = clamped;
        else
            // The vtable's dither: 0 fully drawn, 1 gone — the opposite
            // sense of the stated opacity.
            ((BgObject*)node)->SetTransparency(1f - clamped);
    }

    public bool TryReadOutline(nint address, out byte outline)
    {
        outline = WorldObjectOutline.None;
        var node = Resolve(address);
        if (node == null)
            return false;
        outline = ((DrawObject*)node)->OutlineFlags;
        return true;
    }

    public void WriteOutline(nint address, byte outline)
    {
        var node = Resolve(address);
        if (node == null)
            return;
        // Outline is an independent field; changing it does not require a
        // placement refresh.
        ((DrawObject*)node)->OutlineFlags = outline;
    }

    public nint Spawn(string path, in Transform placement) => Spawn(path, placement, out _);

    public nint Spawn(string path, in Transform placement, out WorldObjectIncarnation identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(path))
            return nint.Zero;
        try
        {
            if (IsVfxPath(path))
                return _vfx.SpawnVfx(path, placement, out identity);
            if (path.EndsWith(".sgb", StringComparison.OrdinalIgnoreCase))
                return _furniture.Spawn(path, placement, ++_nextGeneration, out identity);
            if (!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) return 0;
            // The second argument is an unused debug string (Brio's own
            // note); empty is what the game expects.
            var bg = BgObject.Create(path, string.Empty);
            if (bg == null)
                return nint.Zero;
            var address = (nint)bg;
            lock (_handledLock)
            {
                var generation = ++_nextGeneration;
                var resource = (nint)bg->ModelResourceHandle;
                _incarnations[address] = (resource, generation);
                _spawned.Add(address);
                identity = new(address, generation, resource);
            }
            // LoadAnimationData is deliberately NOT called: it kicked off
            // the model's async .sklb/.pap loads on a raw spawn and the
            // game's deferred task crashed seconds later — the completion
            // expects layout context a bare Create never has (2026-09-01).
            // Spawned copies of animated scenery stand still, by ruling.
            // The placement write restates render and culling exactly as
            // any placement write does.
            Write(address, placement);
            return address;
        }
        catch (Exception ex)
        {
            _log.Error(
                $"NativeWorldObjectPort: spawning '{path}' failed: {ex.Message}");
            return nint.Zero;
        }
    }

    public void Destroy(nint address) => TryDestroy(address);

    public bool TryDestroy(nint address)
    {
        if (_furniture.Contains(address)) { _furniture.Destroy(address); return true; }
        var node = Resolve(address);
        if (node == null)
        {
            // A native object that already disappeared is fully torn down;
            // retire only the claim we can associate with this address.
            // No address-only claim is retired here; only exact VFX leases
            // may be released.
            return true;
        }
        if (node->GetObjectType() == ObjectType.VfxObject)
        {
            if (!TryReadIncarnation(address, out var identity))
                return false;
            return _vfx.TryDestroyCurrentVfx(identity);
        }
        return TryDestroyNative(address, null);
    }

    /// <summary>Teardown receives an exact VFX identity when one exists. It
    /// never finds a claim by address, because a recycled address may have
    /// both an old failed teardown and a new live generation.</summary>
    private bool TryDestroyNative(
        nint address, WorldObjectIncarnation? expected)
    {
        var node = Resolve(address);
        if (node == null)
        {
            if (expected is { } vanished)
            {
                return _vfx.Ownership.Release(vanished);
            }
            lock (_handledLock)
            {
                _incarnations.Remove(address);
                _spawned.Remove(address);
            }
            return true;
        }
        if (expected is { } exact
            && (!TryReadIncarnation(address, out var current)
                || current != exact))
            return _vfx.Ownership.Release(exact);
        WorldObjectIncarnation? currentIdentity = null;
        if (expected is null
            && TryReadIncarnation(address, out var currentObserved))
            currentIdentity = currentObserved;
        try
        {
            // Brio's teardown order (BGOObject.Destroy): render cleanup
            // first, then the freeing destructor. VfxObject shares the
            // same virtual seats, so one call site serves both types.
            var bg = (BgObject*)node;
            bg->CleanupRender();
            bg->Dtor(1);
            lock (_handledLock)
                _spawned.Remove(address);
            if (expected is { } destroyed)
                RetireIncarnation(destroyed);
            else if (currentIdentity is { } observedIdentity)
                RetireIncarnation(observedIdentity);
            return true;
        }
        catch (Exception ex)
        {
            _log.Error(
                $"NativeWorldObjectPort: destroying {address:X} failed: {ex.Message}");
            return false;
        }
    }

    private void RetireIncarnation(WorldObjectIncarnation identity)
    {
        lock (_handledLock)
        {
            if (_incarnations.TryGetValue(identity.Address, out var current)
                && current.Generation == identity.Generation
                && current.Resource == identity.ResourceIdentity)
                _incarnations.Remove(identity.Address);
        }
    }

    private CSObject* Resolve(nint address) => _resolver.Resolve(address);

    // ── BG-only state ────────────────────────────────────────────────

    public bool WriteBgTint(nint address, System.Numerics.Vector3? tint) => _bg.WriteBgTint(address, tint);
    public bool IsBgReady(nint address) => _bg.IsBgReady(address);
    public bool? CanDyeBg(nint address) => _bg.CanDyeBg(address);
    public bool WriteBgAnimationSpeed(nint address, float speed) => _bg.WriteBgAnimationSpeed(address, speed);
    public bool TryReadBgTail(nint address, byte[] into) => _bg.TryReadBgTail(address, into);
    public void WriteBgTailHeld(nint address, byte[] values) => _bg.WriteBgTailHeld(address, values);
    public bool? ReadBgNightState(nint address) => _bg.ReadBgNightState(address);
    public void WriteBgNightState(nint address, bool night) => _bg.WriteBgNightState(address, night);

    // ── VFX-only state ───────────────────────────────────────────────

    public void WriteVfxTint(nint address, System.Numerics.Vector3 tint) => _vfx.WriteVfxTint(address, tint);
    public void SetVfxIntensity(nint address, float intensity) => _vfx.SetVfxIntensity(address, intensity);
    public void PauseVfx(nint address) => _vfx.PauseVfx(address);
    public void ResumeVfx(nint address, float speed) => _vfx.ResumeVfx(address, speed);
    public bool TrySetVfxSpeed(nint address, float speed) => _vfx.TrySetVfxSpeed(address, speed);
    public bool TryPauseVfx(nint address) => _vfx.TryPauseVfx(address);
    public bool TryResumeVfx(nint address, float speed) => _vfx.TryResumeVfx(address, speed);
    public bool IsVfxActive(nint address) => _vfx.IsVfxActive(address);
    public void SetVfxSpeed(nint address, float speed) => _vfx.SetVfxSpeed(address, speed);
    public bool TryReleaseVfxClaim(WorldObjectIncarnation incarnation) => _vfx.TryReleaseVfxClaim(incarnation);
    public bool TryDestroyVfx(WorldObjectIncarnation incarnation) => _vfx.TryDestroyVfx(incarnation);

    public bool TryReadVfxState(
        nint address,
        out System.Numerics.Vector4 color,
        out System.Numerics.Vector3 intensity,
        out float speed) =>
        _vfx.TryReadVfxState(address, out color, out intensity, out speed);

    public void RestoreVfxState(
        nint address,
        System.Numerics.Vector4 color,
        System.Numerics.Vector3 intensity,
        float speed,
        bool resume) =>
        _vfx.RestoreVfxState(address, color, intensity, speed, resume);

    public bool TryRestoreVfxState(
        nint address, VfxStateSnapshot snapshot) =>
        _vfx.TryRestoreVfxState(address, snapshot);

    public void Dispose()
    {
        if (_disposed)
            return;
        _furniture.Dispose();
        var claims = _vfx.Ownership.LiveIdentities;
        var pending = _vfx.Ownership.PendingLeases;
        foreach (var identity in claims)
        {
            if (!TryDestroyVfx(identity))
                _log.Warning(
                    $"NativeWorldObjectPort: VFX {identity.Address:X} teardown remains outstanding during unload.");
        }
        foreach (var lease in pending)
        {
            if (!_vfx.TryDestroyPendingVfx(lease.Identity.Address))
                _log.Warning(
                    $"NativeWorldObjectPort: pending VFX {lease.Identity.Address:X} teardown remains outstanding during unload.");
        }
        _vfx.DisposeHook();
        lock (_handledLock)
        {
            if (!_vfx.Ownership.HasClaims)
            {
                _incarnations.Clear();
                _spawned.Clear();
            }
            _disposed = !_vfx.Ownership.HasClaims;
        }
        GC.SuppressFinalize(this);
    }
}
