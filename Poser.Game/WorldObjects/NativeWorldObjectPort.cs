using System;
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
public sealed unsafe class NativeWorldObjectPort : IWorldObjectPort
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

    private delegate nint VfxResourceLoadDelegate(
        void* job, nint unk1, byte* filePath, byte* avfxData, uint dataSize,
        ResourceHandle* resourceHandle, uint unk2);

    private readonly Hook<VfxResourceLoadDelegate>? _vfxResourceLoad;
    private readonly object _handledLock = new();
    private readonly HashSet<string> _handledVfxPaths =
        new(StringComparer.OrdinalIgnoreCase);
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
    public byte? ReadBgTailByte(nint address, int offset)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject
            || offset < 0xC0 || offset >= 0xE0)
            return null;
        return *((byte*)node + offset);
    }

    public void WriteBgTailByte(nint address, int offset, byte value)
    {
        var node = Resolve(address);
        if (node == null || node->GetObjectType() == ObjectType.VfxObject
            || offset < 0xC0 || offset >= 0xE0)
            return;
        *((byte*)node + offset) = value;
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

    public void Destroy(nint address)
    {
        var node = Resolve(address);
        if (node == null)
            return;
        try
        {
            // Brio's teardown order (BGOObject.Destroy): render cleanup
            // first, then the freeing destructor. VfxObject shares the
            // same virtual seats, so one call site serves both types. A
            // destroyed vfx's path stays HANDLED for the session — the
            // unbind patch is idempotent, and forgetting it would unpatch
            // a second live copy of the same effect (Brio's caveat).
            var bg = (BgObject*)node;
            bg->CleanupRender();
            bg->Dtor(1);
        }
        catch (Exception ex)
        {
            _log.Error(
                $"NativeWorldObjectPort: destroying {address:X} failed: {ex.Message}");
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
        lock (_handledLock)
        {
            _handledVfxPaths.Add(path);
        }
        var vfx = CSVfx.Create(path, string.Empty);
        if (vfx == null)
            return nint.Zero;
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
        return (nint)vfx;
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
                any = _handledVfxPaths.Count > 0;
            }
            if (any && filePath != null && avfxData != null && dataSize > 0)
            {
                var span = MemoryMarshal
                    .CreateReadOnlySpanFromNullTerminated(filePath);
                var path = Encoding.UTF8.GetString(span);
                bool handled;
                lock (_handledLock)
                {
                    handled = _handledVfxPaths.Contains(path);
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
}
