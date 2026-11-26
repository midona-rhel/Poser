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
using Poser.Domain.Transforms;
using CSObject = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.Object;
using CSVfx = FFXIVClientStructs.FFXIV.Client.Graphics.Scene.VfxObject;
using Poser.Game.Natives;

namespace Poser.Game.WorldObjects;

/// <summary>
/// The game functions for effects in the world: play, pause and speed, which
/// come from Brio; spawning an effect; tearing down exactly the effect Poser
/// owns; and a hook on effect-file loading that makes Poser's own effects
/// play and loop on their own. Effects share the world-object port with map
/// models, as in Brio. An effect is told apart by its .avfx file when it is
/// spawned and by the game object's type after that.
/// </summary>
internal sealed unsafe class VfxObjectNative
{
    private unsafe delegate* unmanaged<nint, float, uint, nint> _vfxPlayStatic;
    private unsafe delegate* unmanaged<nint, void> _vfxPause;
    private unsafe delegate* unmanaged<nint, float, void> _vfxSetSpeed;
    private unsafe delegate* unmanaged<nint, bool> _vfxIsActive;

    private delegate nint VfxResourceLoadDelegate(
        void* job, nint unk1, byte* filePath, byte* avfxData, uint dataSize,
        ResourceHandle* resourceHandle, uint unk2);

    private Hook<VfxResourceLoadDelegate>? _vfxResourceLoad;
    private readonly VfxPathClaimOwner _vfxClaims = new();
    private readonly VfxOwnedAllocationLedger _vfxOwnership = new();
    private readonly IPluginLog _log;
    private readonly WorldNodeResolver _resolver;
    private readonly object _handledLock;
    private readonly Func<nint, WorldObjectIncarnation?, bool> _destroyNative;
    private readonly bool _vfxReady;

    /// <param name="handledLock">The world-object port's lock. The file-load
    /// hook can run on a loading thread, so it reads Poser's effect paths
    /// under this lock.</param>
    /// <param name="destroyNative">The port's teardown for models and
    /// effects, which also forgets the torn-down object's identity.</param>
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
        // When a function isn't found, effects can't be spawned; map models
        // are not affected.
        try
        {
            _vfxPlayStatic = (delegate* unmanaged<nint, float, uint, nint>)
                sigScanner.ScanText(Poser.Game.Natives.NativeSignatures.VfxObject_Update);
            _vfxPause = (delegate* unmanaged<nint, void>)sigScanner.ScanText(
                Poser.Game.Natives.NativeSignatures.VfxResourceInstance_TogglePause);
            _vfxSetSpeed = (delegate* unmanaged<nint, float, void>)
                sigScanner.ScanText(
                    Poser.Game.Natives.NativeSignatures.VfxResourceInstance_SetSpeed);
            _vfxIsActive = (delegate* unmanaged<nint, bool>)
                sigScanner.ScanText(Poser.Game.Natives.NativeSignatures.VfxObject_IsActive);
            _vfxResourceLoad = gameInterop.HookFromAddress<
                VfxResourceLoadDelegate>(
                sigScanner.ScanText(
                    Poser.Game.Natives.NativeSignatures.ApricotCore_CreateAvfxResource),
                VfxResourceLoadDetour);
            _vfxResourceLoad.Enable();
            _vfxReady = true;
        }
        catch (Exception ex)
        {
            // A hook that was made but would not turn on is removed again.
            _vfxResourceLoad?.Dispose();
            _vfxResourceLoad = null;
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
        // Both are needed: a stopped effect with speed zero can't be told
        // apart from a paused one, so that case is refused.
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

    /// <summary>An effect is shown or hidden through its alpha, as in Brio;
    /// the draw flag does nothing for an effect.</summary>
    internal const int VfxAlphaOffset = NativeFields.VfxObject_ColorAlpha;

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

    /// <summary>The effect's brightness, three values on the effect's
    /// resource. Poser writes one value to all three, as Brio does, and then
    /// tells the game the effect moved so the change shows.</summary>
    private const int VfxIntensityOffset = NativeFields.VfxResourceInstance_Scale;

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

    /// <summary>Pauses the effect and sets its speed to zero, as Brio does.</summary>
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

    /// <summary>Plays the effect again at <paramref name="speed"/>, as Brio does.</summary>
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

    /// <summary>The colour, brightness and speed Poser can change on an
    /// effect. Read when Poser takes over a map's effect, so giving it back
    /// puts them back; otherwise Poser's values would stay on the map's effect
    /// until the area reloads.</summary>
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
        speed = *(float*)(instance + NativeFields.VfxResourceInstance_Speed);
        return true;
    }

    /// <summary>Restores colour, intensity, and speed. The caller explicitly
    /// chooses whether to restart playback through the resume argument.</summary>
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
                // Stop it, then write the saved speed back. A stopped effect
                // with a speed above zero reads as finished again. One with
                // speed zero can't be told apart from paused.
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

    /// <summary>Whether the effect is still playing, read with Brio's game
    /// function. When that function wasn't found, the effect resource's own
    /// flags are read instead.</summary>
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
        return (*(uint*)(instance + NativeFields.VfxResourceInstance_Flags) & 1) != 0
            || *(ulong*)(instance + NativeFields.VfxResourceInstance_Handle) != 0;
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
        // The effect is gone, so Poser's record of it can go too.
        return _vfxOwnership.Release(identity);
    }

    /// <summary>Spawns an effect the way Brio does: create it, clear the flag
    /// that makes it wait before playing, update it once, place it, play it.
    /// The path is recorded first, so the file-load hook edits the effect
    /// file when it loads.</summary>
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

            // The effect's resource exists after the steps above. Its identity
            // is recorded now, while Poser knows this is the effect it made.
            var resource = ReadVfxResourceIdentity((nint)vfx);
            if (!_vfxOwnership.TryPromote(lease, resource, out identity))
                throw new InvalidOperationException(
                    "the created VFX allocation was not ready to claim");
            committed = true;
            return (nint)vfx;
        }
        catch
        {
            // A step after creating the effect failed: tear the effect down
            // before forgetting it, or it would stay in the game.
            try
            {
                if (vfx != null)
                {
                    OwnedSceneObjectLifetime.Destroy((CSObject*)vfx);
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
                // Creating the effect itself failed, so there is nothing to
                // tear down; only the recorded path is dropped.
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

    /// <summary>Runs when the game loads an effect file. For an effect Poser
    /// spawned, the file's bytes are edited so the effect plays on its own
    /// instead of waiting for a character to cast it, as in Brio.</summary>
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
            if (any && filePath != null && avfxData != null)
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

    // An .avfx file is a tree of chunks. Each chunk is a 4-byte tag, a 4-byte
    // length and then that many bytes, padded to a multiple of 4; a chunk's
    // bytes can hold more chunks. Like Brio, Poser sets the binder number
    // (BdNo) of every item (Item) in every timeline (TmLn) of the effect
    // (AVFX) to "none", so the effect doesn't wait for a character to play it.

    private static readonly uint EffectTag = Tag("AVFX");
    private static readonly uint TimelineTag = Tag("TmLn");
    private static readonly uint ItemTag = Tag("Item");
    private static readonly uint BinderTag = Tag("BdNo");

    private static uint Tag(string s) =>
        (uint)(((byte)s[0] << 24) | ((byte)s[1] << 16)
            | ((byte)s[2] << 8) | (byte)s[3]);

    /// <summary>The chunks that lie directly between <paramref name="start"/>
    /// and <paramref name="end"/>. Stops at the first chunk whose length
    /// reaches past <paramref name="end"/>, so nothing outside the data the
    /// game passed in is read or written.</summary>
    private static List<(uint Tag, int Start, int Length)> Chunks(byte* data, int start, int end)
    {
        var chunks = new List<(uint, int, int)>();
        int at = start;
        while (at + 8 <= end)
        {
            uint tag = *(uint*)(data + at);
            long length = *(uint*)(data + at + 4);
            int payload = at + 8;
            if (payload + length > end)
                break;
            chunks.Add((tag, payload, (int)length));
            at = payload + (int)((length + 3) & ~3L);
        }
        return chunks;
    }

    private static List<(uint Tag, int Start, int Length)> Children(
        byte* data, (uint Tag, int Start, int Length) chunk, uint tag) =>
        Chunks(data, chunk.Start, chunk.Start + chunk.Length).FindAll(c => c.Tag == tag);

    private static void UnbindAllTimelineItems(byte* data, int length)
    {
        foreach (var effect in Chunks(data, 0, length).FindAll(c => c.Tag == EffectTag))
            foreach (var timeline in Children(data, effect, TimelineTag))
                foreach (var item in Children(data, timeline, ItemTag))
                    // Only the first binder number of each item, as in Brio.
                    foreach (var binder in Children(data, item, BinderTag))
                    {
                        if (binder.Length >= 4)
                            *(uint*)(data + binder.Start) = 0xFFFFFFFF;
                        break;
                    }
    }

    public void DisposeHook()
    {
        _vfxResourceLoad?.Dispose();
        _vfxResourceLoad = null;
    }
}
