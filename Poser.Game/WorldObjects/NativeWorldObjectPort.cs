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

namespace Poser.Game.WorldObjects;

/// <summary>
/// Reads and writes BG objects through the game's world-object graph. The
/// graph uses child and sibling links, including circular sibling rings, so
/// traversal tracks visited addresses and limits processing to
/// <see cref="MaxNodes"/>.
/// Native fields are accessed through FFXIVClientStructs; writes refresh the
/// render and culling state that depends on placement.
/// </summary>
public sealed unsafe class NativeWorldObjectPort : IWorldObjectPort, IDisposable
{
    /// <summary>The hard stop on one walk. A zone's graph is thousands of
    /// nodes, not hundreds of thousands; a count this high can only mean the
    /// walk is following something that is not the graph it was told about.
    /// </summary>
    private const int MaxNodes = 100_000;

    private readonly IPluginLog _log;
    private readonly List<WorldObjectRow> _rows = new();
    private readonly List<nint> _lights = new();
    private readonly HashSet<nint> _visited = new();
    private readonly Stack<nint> _pending = new();

    // ── the VFX arm ──────────────────────────────────────────────────
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
    private readonly object _handledLock = new();
    private readonly VfxPathClaimOwner _vfxClaims = new();
    private readonly VfxOwnedAllocationLedger _vfxOwnership = new();
    private readonly Dictionary<nint, (nint Resource, long Generation)>
        _incarnations = new();
    private long _nextGeneration;
    private bool _disposed;
    private bool _resourceHookDisposed;
    private readonly bool _vfxReady;


    public NativeWorldObjectPort(
        ISigScanner sigScanner,
        IGameInteropProvider gameInterop,
        IPluginLog log)
    {
        _log = log;
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

    /// <summary>Whether the path names a VFX rather than a model — the one
    /// dispatch fact the whole arm turns on.</summary>
    public static bool IsVfxPath(string path) =>
        path.EndsWith(".avfx", StringComparison.OrdinalIgnoreCase);

    public bool IsAvailable => CSWorld.Instance() != null;

    public IReadOnlyList<WorldObjectRow> Enumerate()
    {
        Walk(wantLights: false);
        return _rows.Count == 0
            ? Array.Empty<WorldObjectRow>()
            : _rows.ToArray();
    }

    public IReadOnlyList<nint> EnumerateLights()
    {
        Walk(wantLights: true);
        return _lights.Count == 0
            ? Array.Empty<nint>()
            : _lights.ToArray();
    }

    /// <summary>Walks the graph once and collects either BG objects or lights.
    /// Keeping one traversal avoids a second read of the graph.</summary>
    private void Walk(bool wantLights)
    {
        _rows.Clear();
        _lights.Clear();
        _visited.Clear();
        _pending.Clear();
        try
        {
            var world = CSWorld.Instance();
            if (world == null)
                return;

            // The world root is a container, not a listing row.
            var root = &world->Object;
            _visited.Add((nint)root);
            PushRing(root->ChildObject);
            PushRing(root->NextSiblingObject);

            while (_pending.Count > 0 && _visited.Count < MaxNodes)
            {
                var address = _pending.Pop();
                var node = (CSObject*)address;
                if (node == null)
                    continue;
                PushRing(node->ChildObject);
                var type = node->GetObjectType();
                if (wantLights)
                {
                    if (type == ObjectType.Light)
                        _lights.Add(address);
                    continue;
                }
                if (type == ObjectType.BgObject)
                    _rows.Add(ReadRow(address, (BgObject*)node));
                else if (type == ObjectType.VfxObject)
                    // World EFFECTS list beside the map's objects: a zone
                    // bonfire's flame or a fountain's splash adopts by
                    // reference exactly like a BG object (ruled
                    // 2026-09-01), and every handle verb already
                    // dispatches on the node type.
                    _rows.Add(ReadVfxRow(address, (CSVfx*)node));
            }

            if (_visited.Count >= MaxNodes)
                _log.Warning(
                    "NativeWorldObjectPort: the world walk hit its node cap; "
                    + "the listing is truncated.");
        }
        catch (Exception ex)
        {
            // A graph that cannot be walked is an empty listing, never a
            // throw into the overlay's draw.
            _log.Error($"NativeWorldObjectPort: walking the world failed: {ex.Message}");
            _rows.Clear();
            _lights.Clear();
        }
    }

    public bool IsAlive(nint address) => Resolve(address) != null;

    public bool TryReadIncarnation(
        nint address, out WorldObjectIncarnation incarnation)
    {
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
            incarnation = _vfxOwnership.Observe(address, resource);
            return true;
        }
        lock (_handledLock)
        {
            if (!_incarnations.TryGetValue(address, out var prior)
                || prior.Resource != resource)
                prior = (resource, ++_nextGeneration);
            _incarnations[address] = prior;
            incarnation = new WorldObjectIncarnation(
                address, prior.Generation, resource, isVfx);
            }
        return true;
    }

    public bool TryRead(nint address, out Transform placement)
    {
        placement = Transform.Identity;
        var node = Resolve(address);
        if (node == null)
            return false;
        placement = new Transform(node->Position, node->Rotation, node->Scale);
        return true;
    }

    public void Write(nint address, in Transform placement)
    {
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
        if (!RenderReady(bg))
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

    /// <summary>Whether the BG object's model has fully streamed in —
    /// LoadState 7, the only state the render refreshes are safe in.
    /// </summary>
    private static bool RenderReady(BgObject* bg)
    {
        var resource = bg->ModelResourceHandle;
        return resource != null && (byte)resource->LoadState == 7;
    }

    public bool TryReadFlags(nint address, out byte flags)
    {
        flags = 0;
        var node = Resolve(address);
        if (node == null)
            return false;
        flags = ((DrawObject*)node)->Flags;
        return true;
    }

    public void WriteFlags(nint address, byte flags)
    {
        var node = Resolve(address);
        if (node == null)
            return;
        ((DrawObject*)node)->Flags = flags;
    }

    /// <summary>A vfx's drawn state is its ALPHA (Brio's rule); the draw
    /// flag says nothing for an effect.</summary>
    private const int VfxAlphaOffset = 0x26C;

    public bool TryReadVisible(nint address, out bool visible)
    {
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
        var node = Resolve(address);
        if (node == null)
            return;
        if (node->GetObjectType() == ObjectType.VfxObject)
            *(float*)((byte*)node + VfxAlphaOffset) = visible ? 1f : 0f;
        else
            ((DrawObject*)node)->IsVisible = visible;
    }

    /// <summary>Dyes a BG object through the game's stain buffer —
    /// Stagehand's LiveBgObject mechanism whole. Returns FALSE while the
    /// buffer does not exist yet (it appears only after the model loads),
    /// so the service retries on its framework tick. Null clears to
    /// white, the game's own leave-it-alone dye. The colour squares into
    /// the game's linear space via sqrt-sRGB bytes (their conversion;
    /// their alpha-from-blue slip is not copied — alpha states full).
    /// </summary>
    public bool WriteBgTint(nint address, System.Numerics.Vector3? tint)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return true;
        var bg = (BgObject*)node;
        if (bg->StainBuffer == null)
            return false;
        var stated = tint ?? System.Numerics.Vector3.One;
        var color = new FFXIVClientStructs.FFXIV.Client.Graphics.ByteColor
        {
            R = (byte)(Math.Sqrt(Math.Clamp(stated.X, 0f, 1f)) * 255f),
            G = (byte)(Math.Sqrt(Math.Clamp(stated.Y, 0f, 1f)) * 255f),
            B = (byte)(Math.Sqrt(Math.Clamp(stated.Z, 0f, 1f)) * 255f),
            A = byte.MaxValue,
        };
        return bg->TrySetStainColor(color);
    }

    /// <summary>Whether a BG object's model has fully streamed in — the
    /// moment its bytes are worth dumping.</summary>
    public bool IsBgReady(nint address)
    {
        var node = Resolve(address);
        return node != null
            && node->GetObjectType() != ObjectType.VfxObject
            && RenderReady((BgObject*)node);
    }

    /// <summary>The instance's DAY/NIGHT state byte at 0xCD — found by
    /// the twin-diff hunt (2026-09-01): the zone's layout writes 0x00 on
    /// its own objects by day and a raw spawn ships 0xFF, which is why
    /// spawned lamps always glowed. No reference plugin knows this byte.
    /// </summary>
    private const int BgNightStateOffset = 0xCD;

    /// <summary>Whether this BG model can take dye at all. The stain
    /// buffer exists only on models BUILT for staining (housing-style
    /// dyeable parts) — on anything else TrySetStainColor can never land
    /// (both lamp twins carried a null buffer, 2026-09-01). Null while
    /// the model is still streaming.</summary>
    public bool? CanDyeBg(nint address)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return false;
        var bg = (BgObject*)node;
        if (!RenderReady(bg))
            return null;
        return bg->StainBuffer != null;
    }

    /// <summary>Sets every Havok animation control on the BG object's
    /// render skeleton to the stated speed — the same lever the actor
    /// animation port pulls, reached through LoadedAnimationData. False
    /// until the skeleton and its controls exist (they stream in after
    /// the model, and a model without animation never grows them).
    /// </summary>
    public bool WriteBgAnimationSpeed(nint address, float speed)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return false;
        var bg = (BgObject*)node;
        var animation = bg->LoadedAnimationData;
        if (animation == null || animation->RenderSkeleton == null)
            return false;
        var skeleton = animation->RenderSkeleton;
        bool touched = false;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var animated =
                skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
            {
                var control = animated->AnimationControls[c].Value;
                if (control == null)
                    continue;
                control->PlaybackSpeed = speed;
                touched = true;
            }
        }
        return touched;
    }

    /// <summary>Single tail-byte access (0xC0..0xE0), for the
    /// animation-gate hunt: transform-animated scenery is driven by
    /// something in the undocumented tail, found empirically like the
    /// night byte was.</summary>
    /// <summary>Offsets the byte diagnostics may touch: the DrawObject
    /// flag bytes (0x88..0x90, except 0x89 — its low nibble is the load
    /// state) and the undocumented tail.</summary>
    private static bool ByteOffsetAllowed(int offset) =>
        (offset >= 0x88 && offset < 0x90 && offset != 0x89)
        || (offset >= 0xC0 && offset < 0xE0);

    public byte? ReadBgTailByte(nint address, int offset)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject
            || !ByteOffsetAllowed(offset))
            return null;
        return *((byte*)node + offset);
    }

    public void WriteBgTailByte(nint address, int offset, byte value)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject
            || !ByteOffsetAllowed(offset))
            return;
        *((byte*)node + offset) = value;
    }

    /// <summary>The animation topology, for the pause investigation:
    /// whether animation data, a render skeleton and Havok controls
    /// exist at all on this instance.</summary>
    public string DescribeBgAnimation(nint address)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return "(not a BG object)";
        var bg = (BgObject*)node;
        var animation = bg->LoadedAnimationData;
        if (animation == null)
            return "no animation data";
        var parts = new System.Text.StringBuilder("animation data");
        if (animation->AsyncSkeletonResourceHandle != null)
            parts.Append(", sklb handle");
        if (animation->AsyncPapResourceHandle != null)
            parts.Append(", pap handle");
        var skeleton = animation->RenderSkeleton;
        if (skeleton == null)
            return parts.Append(", no render skeleton").ToString();
        parts.Append($", skeleton with {skeleton->PartialSkeletonCount} partials");
        int controls = 0;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var animated =
                skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if (animated == null)
                continue;
            for (int c = 0; c < animated->AnimationControls.Length; c++)
                if (animated->AnimationControls[c].Value != null)
                    controls++;
        }
        parts.Append($", {controls} controls");
        return parts.ToString();
    }

    /// <summary>The whole undocumented tail (0xC0..0xE0) in one read —
    /// the pause hold freezes it beside the transform, because part of
    /// it is the instance's own animation clock (it visibly counts up)
    /// and a held transform with a running clock JUMPS on unpause.
    /// </summary>
    public bool TryReadBgTail(nint address, byte[] into)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject
            || into.Length < 0x20)
            return false;
        for (int i = 0; i < 0x20; i++)
            into[i] = *((byte*)node + 0xC0 + i);
        return true;
    }

    /// <summary>Writes a captured tail back, skipping the DOCUMENTED
    /// bytes (night state, colour intensity, colour) so a held pause
    /// never overwrites a choice the user makes meanwhile.</summary>
    public void WriteBgTailHeld(nint address, byte[] values)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject
            || values.Length < 0x20)
            return;
        for (int i = 0; i < 0x20; i++)
        {
            int offset = 0xC0 + i;
            // Never held: the game's own words. 0xC0..0xC3 is the draw
            // state — a tail captured the frame a file's object was spawned
            // held it at 0 and the paused object never drew (Crystal group,
            // 2026-09-02). 0xC4..0xC7 is an index the cascade-shadow pass
            // dereferences and 0xCC its state byte — a tail captured before
            // the model loaded held 0xC4 at its 0xFFFF sentinel and the pass
            // crashed the client on it (dump 2026-09-02 02:34,
            // ffxiv_dx11+453EE1).
            if (offset is (>= 0xC0 and <= 0xC7) or 0xCC or 0xCD or 0xCE or (>= 0xD0 and <= 0xD3))
                continue;
            *((byte*)node + offset) = values[i];
        }
    }

    /// <summary>The base Object's 64-bit flag word at 0x38 — the widest
    /// undocumented lever the instance carries.</summary>
    public ulong? ReadBgObjectFlags(nint address)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return null;
        return node->ObjectFlags;
    }

    public void WriteBgObjectFlags(nint address, ulong flags)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return;
        node->ObjectFlags = flags;
    }

    public bool? ReadBgNightState(nint address)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return null;
        return *((byte*)node + BgNightStateOffset) != 0;
    }

    public void WriteBgNightState(nint address, bool night)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject)
            return;
        *((byte*)node + BgNightStateOffset) = night ? byte.MaxValue : (byte)0;
        var bg = (BgObject*)node;
        if (RenderReady(bg))
        {
            bg->UpdateCulling();
            bg->UpdateTransforms(false);
        }
    }

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

    public void WriteOpacity(nint address, float opacity)
    {
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

    public nint Spawn(string path, in Transform placement)
    {
        if (string.IsNullOrWhiteSpace(path))
            return nint.Zero;
        try
        {
            if (IsVfxPath(path))
                return SpawnVfx(path, placement);
            // The second argument is an unused debug string (Brio's own
            // note); empty is what the game expects.
            var bg = BgObject.Create(path, string.Empty);
            if (bg == null)
                return nint.Zero;
            var address = (nint)bg;
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
            return TryDestroyCurrentVfx(identity);
        }
        return TryDestroyNative(address, null);
    }

    private bool TryDestroyCurrentVfx(WorldObjectIncarnation identity)
    {
        if (!TryDestroyNative(identity.Address, identity))
            return false;
        // The exact destructor has completed above; release this exact lease
        // directly. The public stale-release seam intentionally refuses an
        // ambiguous live native, but this path has already proved teardown.
        return _vfxOwnership.Release(identity);
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
                return _vfxOwnership.Release(vanished);
            }
            lock (_handledLock)
                _incarnations.Remove(address);
            return true;
        }
        if (expected is { } exact
            && (!TryReadIncarnation(address, out var current)
                || current != exact))
            return _vfxOwnership.Release(exact);
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

    // ── the vfx arm's private half ───────────────────────────────────

    /// <summary>Brio's create sequence (StaticVfxObject.Create): create,
    /// clear the auto-play-gate flag, prime one update, place, then play.
    /// The path is marked HANDLED first so the resource hook unbinds its
    /// timeline items when the avfx streams in.</summary>
    private nint SpawnVfx(string path, in Transform placement)
    {
        if (!_vfxReady)
            return nint.Zero;
        string claimedPath = path.Trim();
        var claim = _vfxClaims.Acquire(claimedPath);
        CSVfx* vfx = null;
        VfxAllocationLease lease = default;
        bool leased = false;
        bool committed = false;
        WorldObjectIncarnation identity = default;
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

    private bool TryDestroyPendingVfx(nint address)
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

    /// <summary>The one address check every read and write goes through: a
    /// non-null pointer that still answers BgObject. An address that has
    /// stopped being one is inert rather than written blind.</summary>
    private CSObject* Resolve(nint address)
    {
        if (address == nint.Zero)
            return null;
        try
        {
            var node = (CSObject*)address;
            var type = node->GetObjectType();
            return type is ObjectType.BgObject or ObjectType.VfxObject
                ? node
                : null;
        }
        catch (Exception ex)
        {
            // Answering null is right — an address that cannot be read is not
            // written — but doing it silently made every read and write past
            // this point a no-op nobody could account for.
            _log.Warning(
                $"NativeWorldObjectPort: {address:X} could not be resolved: {ex.Message}");
            return null;
        }
    }

    /// <summary>Pushes one sibling ring, stopping at the first node already
    /// seen. The ring is circular, so "already seen" is its terminator.
    /// </summary>
    private void PushRing(CSObject* first)
    {
        var cursor = first;
        while (cursor != null && _visited.Add((nint)cursor))
        {
            _pending.Push((nint)cursor);
            cursor = cursor->NextSiblingObject;
        }
    }

    /// <summary>A world effect's row: the .avfx path read through the
    /// resource chain (instance → resource object → apricot handle), the
    /// address as ever when nothing is readable yet.</summary>
    private WorldObjectRow ReadVfxRow(nint address, CSVfx* vfx)
    {
        string path = address.ToString("X");
        try
        {
            var instance = vfx->VfxResourceInstance;
            if (instance != null
                && instance->VfxResourceObject != null
                && instance->VfxResourceObject->ApricotResourceHandle != null)
                path = instance->VfxResourceObject->ApricotResourceHandle
                    ->FileName.ToString();
        }
        catch (Exception ex)
        {
            _log.Debug(
                $"NativeWorldObjectPort: {address:X} has no readable effect name: {ex.Message}");
        }
        var node = (CSObject*)vfx;
        return new WorldObjectRow(
            address,
            path,
            new Transform(node->Position, node->Rotation, node->Scale),
            ((DrawObject*)node)->Flags,
            IsEffect: true);
    }

    private WorldObjectRow ReadRow(nint address, BgObject* bg)
    {
        // Use the address when no model resource can provide a path, so an
        // object without a loaded model still has an identifiable row.
        string path = address.ToString("X");
        try
        {
            var resource = bg->ModelResourceHandle;
            if (resource != null)
                path = resource->FileName.ToString();
        }
        catch (Exception ex)
        {
            // A half-loaded resource names itself by address; it is still a
            // legitimate row — but the fallback is stated rather than assumed,
            // so a listing full of hex names has a reason in the log.
            _log.Debug(
                $"NativeWorldObjectPort: {address:X} has no readable model name: {ex.Message}");
        }
        var node = (CSObject*)bg;
        return new WorldObjectRow(
            address,
            path,
            new Transform(node->Position, node->Rotation, node->Scale),
            ((DrawObject*)node)->Flags);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        var claims = _vfxOwnership.LiveIdentities;
        var pending = _vfxOwnership.PendingLeases;
        foreach (var identity in claims)
        {
            if (!TryDestroyVfx(identity))
                _log.Warning(
                    $"NativeWorldObjectPort: VFX {identity.Address:X} teardown remains outstanding during unload.");
        }
        foreach (var lease in pending)
        {
            if (!TryDestroyPendingVfx(lease.Identity.Address))
                _log.Warning(
                    $"NativeWorldObjectPort: pending VFX {lease.Identity.Address:X} teardown remains outstanding during unload.");
        }
        if (!_resourceHookDisposed)
        {
            _vfxResourceLoad?.Dispose();
            _resourceHookDisposed = true;
        }
        lock (_handledLock)
        {
            if (!_vfxOwnership.HasClaims)
                _incarnations.Clear();
            _disposed = !_vfxOwnership.HasClaims;
        }
        GC.SuppressFinalize(this);
    }
}
