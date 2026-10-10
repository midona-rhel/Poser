using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using Poser.Application.World;
using Poser.Domain.Scene;
using Poser.Services;
using CSObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSVfx = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;
using CSWorld = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.World;

namespace Poser.Game.WorldObjects;

/// <summary>
/// The world-VFX natives: Brio's play/pause/speed signatures, the
/// resource-load hook that unbinds AVFX timeline items for Poser's own
/// paths, spawn, and exact-incarnation teardown through the allocation
/// ledger.
/// </summary>
internal sealed unsafe class VfxObjectNative
{
    // A world VFX rides the SAME port as a BG object — Brio's own shape
    // (StaticVfxObject IS a WorldObject there) — dispatched by the path's
    // .avfx extension at spawn and by the node's object type everywhere
    // else. The natives come from Brio (Brio/Game/Core/VFXService.cs):
    // play/pause/speed signatures, plus the resource-load hook that
    // unbinds AVFX timeline items for OUR paths so a standalone world
    // effect actually plays and loops.

    private unsafe delegate* unmanaged<nint, float, uint, nint> _vfxPlayStatic;
    private unsafe delegate* unmanaged<nint, void> _vfxPause;
    private unsafe delegate* unmanaged<nint, float, void> _vfxSetSpeed;
    private unsafe delegate* unmanaged<nint, bool> _vfxIsActive;

    private delegate nint VfxResourceLoadDelegate(
        void* job, nint unk1, byte* filePath, byte* avfxData, uint dataSize,
        ResourceHandle* resourceHandle, uint unk2);

    private readonly Hook<VfxResourceLoadDelegate>? _vfxResourceLoad;
    private readonly VfxPathClaimOwner _vfxClaims = new();
    private readonly VfxOwnedAllocationLedger _vfxOwnership = new();
    private readonly IPluginLog _log;
    private readonly WorldNodeResolver _resolver;
    private readonly object _handledLock;
    private readonly Func<nint, WorldObjectIncarnation?, bool> _destroyNative;
    private bool _resourceHookDisposed;
    private readonly bool _vfxReady;

    /// <param name="handledLock">The port's identity lock; the resource
    /// hook reads the path claims under it.</param>
    /// <param name="destroyNative">The port's shared BG/VFX destructor
    /// sequence, which also retires the destroyed incarnation.</param>
    public VfxObjectNative(
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop,
        IPluginLog log,
        WorldNodeResolver resolver,
        object handledLock,
        Func<nint, WorldObjectIncarnation?, bool> destroyNative)
    {
        _log = log;
        _resolver = resolver;
        _handledLock = handledLock;
        _destroyNative = destroyNative;
        // Each signature is guarded on its own: a patch that breaks one
        // takes VFX away and leaves BG objects standing.
        try
        {
            _vfxPlayStatic = (delegate* unmanaged<nint, float, uint, nint>)
                sigScanner.ScanText("E8 ?? ?? ?? ?? B0 02 EB 02");
            _vfxPause = (delegate* unmanaged<nint, void>)sigScanner.ScanText(
                "E8 ?? ?? ?? ?? 48 8B CB E8 ?? ?? ?? ?? 0F 2E C7 7A ?? 74 ?? "
                + "0F 28 CF 48 8B CB E8");
            _vfxSetSpeed = (delegate* unmanaged<nint, float, void>)
                sigScanner.ScanText(
                    "48 89 5C 24 08 57 48 83 EC 30 48 8B 59 60");
            _vfxIsActive = (delegate* unmanaged<nint, bool>)
                sigScanner.ScanText(
                    "E8 ?? ?? ?? ?? 84 C0 75 ?? 48 8B 4B 28 48 8B 01 FF "
                    + "50 68 48 8B C8 0F 57 C9 E8");
            _vfxResourceLoad = gameInterop.HookFromAddress<
                VfxResourceLoadDelegate>(
                sigScanner.ScanText(
                    "E8 ?? ?? ?? ?? 48 8B 5C 24 ?? 48 85 C0 48 8B 6C 24"),
                VfxResourceLoadDetour);
            _vfxResourceLoad.Enable();
            _vfxReady = true;
        }
        catch (Exception ex)
        {
            _vfxReady = false;
            _log.Warning(
                $"NativeWorldObjectPort: the VFX natives are unavailable, "
                + $"so VFX spawns will refuse: {ex.Message}");
        }
    }

    internal VfxOwnedAllocationLedger Ownership => _vfxOwnership;

    private CSObject* Resolve(nint address) => _resolver.Resolve(address);

    public bool TryReadVfxPlayback(
        nint address, out VfxPlaybackState playback)
    {
        playback = VfxPlaybackState.Unavailable;
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject)
            return false;
        var instance = (VfxResourceInstance*)((CSVfx*)node)->VfxResourceInstance;
        if (instance == null)
            return false;
        bool active = IsVfxActive(address);
        // Active and speed are deliberately considered together: inactive +
        // zero has no distinguishable native signal and must be refused.
        if (active && instance->Speed > 0.0001f)
            playback = VfxPlaybackState.Playing;
        else if (active)
            playback = VfxPlaybackState.Paused;
        else if (instance->Speed > 0.0001f)
            playback = VfxPlaybackState.Inactive;
        else
            return false;
        return true;
    }

    /// <summary>A vfx's drawn state is its ALPHA (Brio's rule); the draw
    /// flag says nothing for an effect.</summary>
    internal const int VfxAlphaOffset = 0x26C;

    public void WriteVfxTint(nint address, System.Numerics.Vector3 tint)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject)
            return;
        var vfx = (CSVfx*)node;
        var current = vfx->Color;
        vfx->Color = new System.Numerics.Vector4(
            tint.X, tint.Y, tint.Z, current.W);
    }

    /// <summary>The effect's brightness triple at 0x90 on the resource
    /// instance (Brio's SetIntensity): one uniform value drives all three
    /// components, then the transform/culling nudge makes it take.
    /// </summary>
    private const int VfxIntensityOffset = 0x90;

    public void SetVfxIntensity(nint address, float intensity)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject)
            return;
        var vfx = (CSVfx*)node;
        var instance = (nint)vfx->VfxResourceInstance;
        if (instance == nint.Zero)
            return;
        float clamped = Math.Clamp(intensity, 0f, 4f);
        *(System.Numerics.Vector3*)(instance + VfxIntensityOffset) =
            new System.Numerics.Vector3(clamped);
        vfx->NotifyTransformChanged();
        vfx->UpdateCulling();
    }

    /// <summary>Brio's pause pair: the pause native plus speed zero.
    /// </summary>
    public void PauseVfx(nint address)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject)
            return;
        var instance = (nint)((CSVfx*)node)->VfxResourceInstance;
        if (instance == nint.Zero)
            return;
        if (_vfxPause != null)
            _vfxPause(instance);
        if (_vfxSetSpeed != null)
            _vfxSetSpeed(instance, 0f);
    }

    /// <summary>Brio's resume pair: play the static effect again and put
    /// the stated speed back.</summary>
    public void ResumeVfx(nint address, float speed)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject)
            return;
        var vfx = (CSVfx*)node;
        if (_vfxReady && _vfxPlayStatic != null)
            _vfxPlayStatic((nint)vfx, 0f, 0xFFFFFFFF);
        var instance = (nint)vfx->VfxResourceInstance;
        if (instance != nint.Zero && _vfxSetSpeed != null)
            _vfxSetSpeed(instance, speed);
    }

    public bool TrySetVfxSpeed(nint address, float speed)
    {
        if (!TryReadVfxState(address, out _, out _, out _)
            || _vfxSetSpeed == null)
            return false;
        SetVfxSpeed(address, speed);
        return TryReadVfxState(address, out _, out _, out var actual)
            && Math.Abs(actual - speed) <= 0.0001f;
    }

    public bool TryPauseVfx(nint address)
    {
        if (!TryReadVfxState(address, out _, out _, out _)
            || _vfxPause == null || _vfxSetSpeed == null)
            return false;
        PauseVfx(address);
        return TryReadVfxPlayback(address, out var playback)
            && playback == VfxPlaybackState.Paused;
    }

    public bool TryResumeVfx(nint address, float speed)
    {
        if (!_vfxReady
            || !TryReadVfxState(address, out _, out _, out _)
            || _vfxPlayStatic == null || _vfxSetSpeed == null)
            return false;
        ResumeVfx(address, speed);
        return TryReadVfxPlayback(address, out var playback)
            && playback == VfxPlaybackState.Playing
            && TryReadVfxState(address, out _, out _, out var actual)
            && Math.Abs(actual - speed) <= 0.0001f;
    }

    /// <summary>The effect state an adoption may edit — captured at
    /// adopt so the release can hand the ZONE's effect back exactly as
    /// found. Tint, intensity, speed and pause otherwise stick on the
    /// zone's own effect until a zone reload.</summary>
    public bool TryReadVfxState(
        nint address,
        out System.Numerics.Vector4 color,
        out System.Numerics.Vector3 intensity,
        out float speed)
    {
        color = System.Numerics.Vector4.One;
        intensity = System.Numerics.Vector3.One;
        speed = 1f;
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject)
            return false;
        var vfx = (CSVfx*)node;
        color = vfx->Color;
        var instance = (nint)vfx->VfxResourceInstance;
        if (instance == nint.Zero)
            return false;
        intensity =
            *(System.Numerics.Vector3*)(instance + VfxIntensityOffset);
        speed = *(float*)(instance + 0x70);
        return true;
    }

    /// <summary>Puts a captured effect state back — the adopted release's
    /// other half. Resume replays only when the adoption paused it.</summary>
    public void RestoreVfxState(
        nint address,
        System.Numerics.Vector4 color,
        System.Numerics.Vector3 intensity,
        float speed,
        bool resume)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject)
            return;
        var vfx = (CSVfx*)node;
        vfx->Color = color;
        var instance = (nint)vfx->VfxResourceInstance;
        if (instance != nint.Zero)
            *(System.Numerics.Vector3*)(instance + VfxIntensityOffset) =
                intensity;
        if (resume && _vfxReady && _vfxPlayStatic != null)
            _vfxPlayStatic((nint)vfx, 0f, 0xFFFFFFFF);
        if (instance != nint.Zero && _vfxSetSpeed != null)
            _vfxSetSpeed(instance, speed);
        vfx->NotifyTransformChanged();
        vfx->UpdateCulling();
    }

    public bool TryRestoreVfxState(
        nint address, VfxStateSnapshot snapshot)
    {
        bool canPause = _vfxPause != null && _vfxSetSpeed != null;
        bool canPlay = _vfxReady && _vfxPlayStatic != null
            && _vfxSetSpeed != null;
        if (snapshot.Playback == VfxPlaybackState.Unavailable
            || (snapshot.Playback == VfxPlaybackState.Playing
                ? !canPlay
                : !canPause))
            return false;
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject)
            return false;
        var vfx = (CSVfx*)node;
        vfx->Color = snapshot.Color;
        var instance = (VfxResourceInstance*)vfx->VfxResourceInstance;
        if (instance == null)
            return false;
        *(System.Numerics.Vector3*)((byte*)instance + VfxIntensityOffset) =
            snapshot.Intensity;
        switch (snapshot.Playback)
        {
            case VfxPlaybackState.Playing:
                ResumeVfx(address, snapshot.Speed);
                break;
            case VfxPlaybackState.Paused:
                PauseVfx(address);
                SetVfxSpeed(address, snapshot.Speed);
                break;
            case VfxPlaybackState.Inactive:
                // Stop first, then restore authored speed as data. A retired
                // effect with positive speed re-observes as inactive without
                // replay; zero-speed inactive is intentionally ambiguous.
                PauseVfx(address);
                SetVfxSpeed(address, snapshot.Speed);
                break;
            default:
                return false;
        }
        vfx->NotifyTransformChanged();
        vfx->UpdateCulling();
        return TryReadVfxState(
                address, out var color, out var intensity, out var actualSpeed)
            && TryReadVfxPlayback(address, out var terminal)
            && terminal == snapshot.Playback
            && NearlyEqual(color, snapshot.Color)
            && NearlyEqual(intensity, snapshot.Intensity)
            && Math.Abs(actualSpeed - snapshot.Speed) <= 0.0001f;
    }

    private static bool NearlyEqual(
        System.Numerics.Vector4 left, System.Numerics.Vector4 right) =>
        Math.Abs(left.X - right.X) <= 0.0001f
        && Math.Abs(left.Y - right.Y) <= 0.0001f
        && Math.Abs(left.Z - right.Z) <= 0.0001f
        && Math.Abs(left.W - right.W) <= 0.0001f;

    private static bool NearlyEqual(
        System.Numerics.Vector3 left, System.Numerics.Vector3 right) =>
        Math.Abs(left.X - right.X) <= 0.0001f
        && Math.Abs(left.Y - right.Y) <= 0.0001f
        && Math.Abs(left.Z - right.Z) <= 0.0001f;

    /// <summary>Whether the effect is still playing — Brio's
    /// IsActiveStatic native, with the resource instance's own flags
    /// (Brio's struct: ActiveFlag bit 0, or a live job) as the fallback
    /// when the signature is gone. A looping effect that reports
    /// inactive is REPLAYED in place, never respawned — the respawn
    /// loop visibly blinked the effect off and on.</summary>
    public bool IsVfxActive(nint address)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject)
            return false;
        var vfx = (CSVfx*)node;
        if (_vfxIsActive != null)
            return _vfxIsActive((nint)vfx);
        var instance = (nint)vfx->VfxResourceInstance;
        if (instance == nint.Zero)
            return false;
        return (*(uint*)(instance + 0xC4) & 1) != 0
            || *(ulong*)(instance + 0x60) != 0;
    }

    public void SetVfxSpeed(nint address, float speed)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() != ObjectType.VfxObject
            || _vfxSetSpeed == null)
            return;
        var instance = (nint)((CSVfx*)node)->VfxResourceInstance;
        if (instance != nint.Zero)
            _vfxSetSpeed(instance, speed);
    }

    public bool TryDestroyCurrentVfx(WorldObjectIncarnation identity)
    {
        if (!_destroyNative(identity.Address, identity))
            return false;
        // The exact destructor has completed above; release this exact lease
        // directly. The public stale-release seam intentionally refuses an
        // ambiguous live native, but this path has already proved teardown.
        return _vfxOwnership.Release(identity);
    }

    /// <summary>Brio's create sequence (StaticVfxObject.Create): create,
    /// clear the auto-play-gate flag, prime one update, place, then play.
    /// The path is marked HANDLED first so the resource hook unbinds its
    /// timeline items when the avfx streams in.</summary>
    public nint SpawnVfx(string path, in Transform placement, out WorldObjectIncarnation identity)
    {
        identity = default;
        if (!_vfxReady)
            return nint.Zero;
        string claimedPath = path.Trim();
        var claim = _vfxClaims.Acquire(claimedPath);
        CSVfx* vfx = null;
        VfxAllocationLease lease = default;
        bool leased = false;
        bool committed = false;
        bool cleaned = false;
        try
        {
            vfx = CSVfx.Create(path, string.Empty);
            if (vfx == null)
            {
                claim.Dispose();
                return nint.Zero;
            }
            lease = _vfxOwnership.Reserve((nint)vfx, claim);
            leased = true;
            vfx->SomeFlags &= 0xF7;
            vfx->Update(0f);
            var node = (CSObject*)vfx;
            node->Position = placement.Position;
            node->Rotation = placement.Rotation;
            node->Scale = placement.Scale;
            vfx->NotifyTransformChanged();
            vfx->UpdateCulling();
            PlayVfx(vfx);
            *(float*)((byte*)vfx + VfxAlphaOffset) = 1f;

            // The resource instance is attached by the create/update/play
            // sequence. Only this synchronous allocation may promote its
            // reserved zero-resource identity to a live claim.
            var resource = ReadVfxResourceIdentity((nint)vfx);
            if (!_vfxOwnership.TryPromote(lease, resource, out identity))
                throw new InvalidOperationException(
                    "the created VFX allocation was not ready to claim");
            committed = true;
            return (nint)vfx;
        }
        catch
        {
            // Creation can fail after allocation (update, placement, or
            // playback). Tear down that exact allocation before dropping the
            // pending claim; otherwise a failed spawn leaks native state.
            try
            {
                if (vfx != null)
                {
                    vfx->CleanupRender();
                    vfx->Dtor(1);
                }
                cleaned = true;
            }
            catch (Exception ex)
            {
                _log.Error(
                    $"NativeWorldObjectPort: failed VFX cleanup for '{claimedPath}': {ex.Message}");
            }
            if (!leased)
            {
                // CSVfx.Create can throw before returning an allocation;
                // rollback the path token even though no native cleanup is
                // possible.
                claim.Dispose();
            }
            else if (cleaned && vfx != null)
            {
                _vfxOwnership.Release(committed ? identity : lease.Identity);
            }
            throw;
        }
    }

    public bool TryReleaseVfxClaim(WorldObjectIncarnation incarnation)
    {
        var current = ReadCurrentVfx(incarnation.Address);
        return _vfxOwnership.TryReleaseIfVanishedOrReplaced(
            incarnation, current);
    }

    public bool TryDestroyVfx(WorldObjectIncarnation incarnation)
    {
        var match = _vfxOwnership.Match(
            incarnation, ReadCurrentVfx(incarnation.Address));
        if (match is VfxAllocationMatch.Vanished
            or VfxAllocationMatch.Replaced)
            return _vfxOwnership.Release(incarnation);
        if (match != VfxAllocationMatch.Exact)
            return false;
        return TryDestroyCurrentVfx(incarnation);
    }

    public bool TryDestroyPendingVfx(nint address)
    {
        bool allClean = true;
        foreach (var lease in _vfxOwnership.PendingLeases)
        {
            if (lease.Identity.Address != address)
                continue;
            var match = _vfxOwnership.Match(
                lease.Identity, ReadCurrentVfx(address));
            if (match is VfxAllocationMatch.Vanished
                or VfxAllocationMatch.Replaced)
            {
                _vfxOwnership.Release(lease.Identity);
                continue;
            }
            if (match != VfxAllocationMatch.Exact
                || !TryDestroyCurrentVfx(lease.Identity))
                allClean = false;
        }
        return allClean;
    }

    private nint ReadVfxResourceIdentity(nint address)
    {
        return ReadCurrentVfx(address).ResourceIdentity;
    }

    private VfxCurrentObservation ReadCurrentVfx(nint address)
    {
        var node = Resolve(address);
        if (node == null)
            return new VfxCurrentObservation(false, false, nint.Zero);
        if (node->GetObjectType() != ObjectType.VfxObject)
            return new VfxCurrentObservation(true, false, nint.Zero);
        return new VfxCurrentObservation(
            true, true, (nint)((CSVfx*)node)->VfxResourceInstance);
    }

    private void PlayVfx(CSVfx* vfx)
    {
        if (_vfxReady && _vfxPlayStatic != null)
            _vfxPlayStatic((nint)vfx, 0f, 0xFFFFFFFF);
    }

    /// <summary>The resource-load seam: for paths POSER spawned, every
    /// timeline item's binder id is nulled in the streamed avfx bytes, so
    /// the effect plays standalone instead of waiting on a caster it will
    /// never have. Brio's mechanism, ported whole.</summary>
    private nint VfxResourceLoadDetour(
        void* job, nint unk1, byte* filePath, byte* avfxData, uint dataSize,
        ResourceHandle* resourceHandle, uint unk2)
    {
        try
        {
            bool any;
            lock (_handledLock)
            {
                any = _vfxClaims.HasClaims;
            }
            if (any && filePath != null && avfxData != null && dataSize > 0)
            {
                var span = MemoryMarshal
                    .CreateReadOnlySpanFromNullTerminated(filePath);
                var path = Encoding.UTF8.GetString(span);
                bool handled;
                lock (_handledLock)
                {
                    handled = _vfxClaims.Contains(path);
                }
                if (handled)
                    UnbindAllTimelineItems(avfxData, (int)dataSize);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(
                $"NativeWorldObjectPort: avfx unbind failed: {ex.Message}");
        }
        return _vfxResourceLoad!.Original(
            job, unk1, filePath, avfxData, dataSize, resourceHandle, unk2);
    }

    // The AVFX chunk walk (Brio VFXService.cs): null every BdNo in every
    // Item of every TmLn, so nothing in the file binds to a timeline.

    private static uint Tag(string s) =>
        (uint)(((byte)s[0] << 24) | ((byte)s[1] << 16)
            | ((byte)s[2] << 8) | (byte)s[3]);

    private static int FindChunk(byte* data, int start, int len, uint tag)
    {
        int consumed = 0;
        while (consumed + 8 <= len)
        {
            uint t = *(uint*)(data + start + consumed);
            uint pl = *(uint*)(data + start + consumed + 4);
            if (t == tag)
                return start + consumed + 8;
            consumed += 8 + (int)((pl + 3u) & ~3u);
        }
        return -1;
    }

    private static void UnbindAllTimelineItems(byte* data, int len)
    {
        int avfx = FindChunk(data, 0, len, Tag("AVFX"));
        if (avfx < 0)
            return;
        int avfxLen = (int)*(uint*)(data + avfx - 4);
        int consumed = 0;
        while (consumed + 8 <= avfxLen)
        {
            int childStart = avfx + consumed;
            uint tag = *(uint*)(data + childStart);
            uint payLen = *(uint*)(data + childStart + 4);
            int payStart = childStart + 8;
            if (payStart + (int)payLen > avfx + avfxLen)
                break;
            if (tag == Tag("TmLn"))
                UnbindTimeline(data, payStart, (int)payLen, len);
            consumed += 8 + (int)((payLen + 3u) & ~3u);
        }
    }

    private static void UnbindTimeline(
        byte* data, int tmlnStart, int tmlnLen, int totalLen)
    {
        int consumed = 0;
        while (consumed + 8 <= tmlnLen)
        {
            int childStart = tmlnStart + consumed;
            uint tag = *(uint*)(data + childStart);
            uint payLen = *(uint*)(data + childStart + 4);
            int payStart = childStart + 8;
            if (payStart + (int)payLen > tmlnStart + tmlnLen)
                break;
            if (tag == Tag("Item"))
            {
                int bd = FindChunk(data, payStart, (int)payLen, Tag("BdNo"));
                if (bd >= 0 && bd + 4 <= totalLen)
                    *(uint*)(data + bd) = 0xFFFFFFFF;
            }
            consumed += 8 + (int)((payLen + 3u) & ~3u);
        }
    }

    public void DisposeHook()
    {
        if (!_resourceHookDisposed)
        {
            _vfxResourceLoad?.Dispose();
            _resourceHookDisposed = true;
        }
    }
}
