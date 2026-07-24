using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Service for controlling actor gaze (where they look). Based on Brio's
/// ActorLookAtService. One entry per actor, keyed by the native GameObjectId —
/// there is no second wrapper-keyed map to desync, so an ordinary actor-list
/// refresh cannot orphan state. The Entity target is a GameObjectId written
/// natively (LookMode.Target through the Brio/Ktisis-verified id/position
/// union); no captured address is ever dereferenced.
/// </summary>
public unsafe class GazeService : IGazeService, IDisposable
{
    // LookAt controller indices for _updateLookAt function
    private const uint LookAtIndex_Body = 0;
    private const uint LookAtIndex_Head = 1;
    private const uint LookAtIndex_Eyes = 2;

    private readonly IGPoseService _gPoseService;
    private readonly ICameraService _cameraService;
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _eventBus;
    private readonly IPluginLog _log;

    private delegate* unmanaged<CharacterLookAtController*, LookAtTarget*, uint, nint, void> _updateLookAt;

    private delegate nint ActorLookAtLoopDelegate(ContainerInterface* args);
    private Hook<ActorLookAtLoopDelegate> _actorLookAtLoop = null!;

    /// <summary>
    /// One managed+native entry per actor. Mutated from the UI thread and read
    /// from the hooked game loop, so every access goes through the sync lock.
    /// </summary>
    private sealed class GazeEntry
    {
        public GazeTargetMode Mode;
        public GazeTargetType Parts = GazeTargetType.All;
        public ulong TargetId;              // Entity-mode target GameObjectId; 0 = unset
        public LookAtSource Target;         // per-part native write source
        public bool EyesLocked;
        public bool HeadLocked;
        public bool BodyLocked;
    }

    private readonly object _sync = new();
    private readonly Dictionary<ulong, GazeEntry> _entries = new();

    public GazeService(
        IGPoseService gPoseService,
        ICameraService cameraService,
        IObjectTable objectTable,
        IEventBus eventBus,
        ISigScanner sigScanner,
        IGameInteropProvider hooks,
        IPluginLog log)
    {
        _gPoseService = gPoseService;
        _cameraService = cameraService;
        _objectTable = objectTable;
        _eventBus = eventBus;
        _log = log;

        // No try-catch - let plugin fail to load if sigs are invalid rather than run in broken state
        var updateFaceTrackerAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 8B D7 48 8B CB E8 ?? ?? ?? ?? 41 ?? ?? 8B D7 48 ?? ?? 48 ?? ?? ?? ?? 48 83 ?? ?? 5F");
        _updateLookAt = (delegate* unmanaged<CharacterLookAtController*, LookAtTarget*, uint, nint, void>)updateFaceTrackerAddress;

        var actorLookAtLoopAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 48 83 C3 08 48 83 EF 01 75 CF 48 ?? ?? ?? ?? 48");
        _actorLookAtLoop = hooks.HookFromAddress<ActorLookAtLoopDelegate>(actorLookAtLoopAddress, ActorLookAtDetour);
        _actorLookAtLoop.Enable();

        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);
    }

    /// <summary>Entity mode without a chosen target performs no override.</summary>
    private static GazeTargetMode EffectiveMode(GazeEntry entry) =>
        entry.Mode == GazeTargetMode.Entity && entry.TargetId == 0
            ? GazeTargetMode.None
            : entry.Mode;

    private nint ActorLookAtDetour(ContainerInterface* args)
    {
        if (_gPoseService.IsGPosing)
        {
            bool any;
            lock (_sync)
            {
                any = _entries.Count > 0;
            }
            if (any)
            {
                var targetActor = _objectTable.CreateObjectReference((nint)args->OwnerObject);
                if (targetActor is not null && targetActor.IsValid())
                {
                    GazeTargetMode mode = GazeTargetMode.None;
                    GazeTargetType parts = GazeTargetType.None;
                    LookAtSource lookAt = default;
                    bool eyesLocked = false, headLocked = false, bodyLocked = false;
                    lock (_sync)
                    {
                        if (_entries.TryGetValue(targetActor.GameObjectId, out var entry))
                        {
                            mode = EffectiveMode(entry);
                            parts = entry.Parts;
                            // Copy to a local (like Brio) — the native calls
                            // below run outside the lock.
                            lookAt = entry.Target;
                            eyesLocked = entry.EyesLocked;
                            headLocked = entry.HeadLocked;
                            bodyLocked = entry.BodyLocked;
                        }
                    }

                    // Off performs no Poser write at all.
                    if (mode == GazeTargetMode.None)
                        return _actorLookAtLoop.Original(args);

                    // Camera and Forward are position sources refreshed each
                    // loop for unlocked parts; Entity carries the target id in
                    // the union and needs no per-loop position poll.
                    if (mode == GazeTargetMode.Camera)
                    {
                        var cameraPos = _cameraService.GetCameraPosition();
                        if (parts.HasFlag(GazeTargetType.Eyes) && !eyesLocked)
                            lookAt.Eyes.LookAtTarget.Position = cameraPos;
                        if (parts.HasFlag(GazeTargetType.Head) && !headLocked)
                            lookAt.Head.LookAtTarget.Position = cameraPos;
                        if (parts.HasFlag(GazeTargetType.Body) && !bodyLocked)
                            lookAt.Body.LookAtTarget.Position = cameraPos;
                    }
                    else if (mode == GazeTargetMode.Forward)
                    {
                        var nativeObj = (GameObject*)targetActor.Address;
                        var position = new Vector3(nativeObj->Position.X, nativeObj->Position.Y, nativeObj->Position.Z);
                        var rotation = nativeObj->Rotation;
                        var forwardDir = new Vector3(MathF.Sin(rotation), 0f, MathF.Cos(rotation));
                        var forwardPos = position + forwardDir * 10f + new Vector3(0, 1.5f, 0);
                        if (parts.HasFlag(GazeTargetType.Eyes) && !eyesLocked)
                            lookAt.Eyes.LookAtTarget.Position = forwardPos;
                        if (parts.HasFlag(GazeTargetType.Head) && !headLocked)
                            lookAt.Head.LookAtTarget.Position = forwardPos;
                        if (parts.HasFlag(GazeTargetType.Body) && !bodyLocked)
                            lookAt.Body.LookAtTarget.Position = forwardPos;
                    }

                    var lookAtController = &((Character*)targetActor.Address)->LookAt.Controller;
                    if (parts.HasFlag(GazeTargetType.Body))
                        _updateLookAt(lookAtController, &lookAt.Body.LookAtTarget, LookAtIndex_Body, 0);
                    if (parts.HasFlag(GazeTargetType.Head))
                        _updateLookAt(lookAtController, &lookAt.Head.LookAtTarget, LookAtIndex_Head, 0);
                    if (parts.HasFlag(GazeTargetType.Eyes))
                        _updateLookAt(lookAtController, &lookAt.Eyes.LookAtTarget, LookAtIndex_Eyes, 0);
                }
            }
        }

        // Call original - this runs gaze IK and modifies bones
        return _actorLookAtLoop.Original(args);
    }

    private IGameObject? Resolve(IActor actor) =>
        actor.Address != nint.Zero ? _objectTable.CreateObjectReference(actor.Address) : null;

    public GazeState GetGazeState(IActor actor)
    {
        if (Resolve(actor) is not { } gameObject)
            return new GazeState();
        lock (_sync)
        {
            return _entries.TryGetValue(gameObject.GameObjectId, out var entry)
                ? new GazeState { Mode = entry.Mode, TargetType = entry.Parts, TargetId = entry.TargetId }
                : new GazeState();
        }
    }

    public void SetGazeMode(IActor actor, GazeTargetMode mode)
    {
        if (Resolve(actor) is not { } gameObject)
            return;
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            entry.Mode = mode;
            if (mode != GazeTargetMode.None && entry.Parts == GazeTargetType.None)
                entry.Parts = GazeTargetType.All;
            ReseedUnlockedParts(entry);
        }
    }

    public void SetGazeParts(IActor actor, GazeTargetType parts)
    {
        if (Resolve(actor) is not { } gameObject)
            return;
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            entry.Parts = parts;
            // Turning off the final active part returns the mode to Off in the
            // same transition; the handle is kept so re-enabling is symmetric.
            if (parts == GazeTargetType.None)
                entry.Mode = GazeTargetMode.None;
            ReseedUnlockedParts(entry);
        }
    }

    public void SetGazeTarget(IActor actor, IActor target)
    {
        if (Resolve(actor) is not { } gameObject || Resolve(target) is not { } targetObject)
            return;
        if (gameObject.GameObjectId == targetObject.GameObjectId)
        {
            _log.Warning("GazeService: an actor cannot gaze at itself.");
            return;
        }
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            entry.TargetId = targetObject.GameObjectId;
            entry.Mode = GazeTargetMode.Entity;
            if (entry.Parts == GazeTargetType.None)
                entry.Parts = GazeTargetType.All;
            ReseedUnlockedParts(entry);
        }
        // Brio parity (SetActorTarget): the character's own target id backs
        // the game's id-based look tracking.
        ((Character*)actor.Address)->SetTargetId(targetObject.GameObjectId);
    }

    public nint GetGazeTargetAddress(IActor actor)
    {
        ulong targetId;
        if (Resolve(actor) is not { } gameObject)
            return 0;
        lock (_sync)
        {
            if (!_entries.TryGetValue(gameObject.GameObjectId, out var entry) || entry.TargetId == 0)
                return 0;
            targetId = entry.TargetId;
        }
        return _objectTable.SearchById(targetId)?.Address ?? 0;
    }

    public void SetPartLock(IActor actor, GazeTargetType part, bool locked)
    {
        if (Resolve(actor) is not { } gameObject)
            return;
        lock (_sync)
        {
            if (!_entries.TryGetValue(gameObject.GameObjectId, out var entry))
                return;
            var mode = EffectiveMode(entry);
            if (mode == GazeTargetMode.None || !entry.Parts.HasFlag(part))
                return; // locks act only on participating parts of an active mode

            if (locked)
            {
                // Freeze the part at its ACTUAL current target.
                var freezePos = mode switch
                {
                    GazeTargetMode.Camera => _cameraService.GetCameraPosition(),
                    GazeTargetMode.Forward => ForwardPoint(actor),
                    _ => _objectTable.SearchById(entry.TargetId)?.Position ?? _cameraService.GetCameraPosition(),
                };
                ApplyPartLock(entry, part, freezePos);
            }
            else
            {
                ClearPartLock(entry, part);
                ReseedPart(entry, part);
            }
        }
    }

    public bool IsPartLocked(IActor actor, GazeTargetType part)
    {
        if (Resolve(actor) is not { } gameObject)
            return false;
        lock (_sync)
        {
            if (!_entries.TryGetValue(gameObject.GameObjectId, out var entry))
                return false;
            if (part.HasFlag(GazeTargetType.Eyes) && entry.EyesLocked) return true;
            if (part.HasFlag(GazeTargetType.Head) && entry.HeadLocked) return true;
            if (part.HasFlag(GazeTargetType.Body) && entry.BodyLocked) return true;
            return false;
        }
    }

    public bool IsGazeEnabled(IActor actor)
    {
        if (Resolve(actor) is not { } gameObject)
            return false;
        lock (_sync)
        {
            return _entries.TryGetValue(gameObject.GameObjectId, out var entry) &&
                EffectiveMode(entry) != GazeTargetMode.None;
        }
    }

    public void ResetGaze(IActor actor)
    {
        if (Resolve(actor) is not { } gameObject)
            return;
        lock (_sync)
        {
            _entries.Remove(gameObject.GameObjectId);
        }
    }

    // ── entry maintenance (all callers hold _sync) ───────────────────────

    private GazeEntry GetOrCreateEntry(ulong gameObjectId)
    {
        if (_entries.TryGetValue(gameObjectId, out var entry))
            return entry;
        return _entries[gameObjectId] = new GazeEntry();
    }

    /// <summary>
    /// Reseeds every UNLOCKED participating part for the entry's effective
    /// mode. Locked parts keep their frozen position and mode — a transition
    /// never silently moves a locked part.
    /// </summary>
    private void ReseedUnlockedParts(GazeEntry entry)
    {
        if (!entry.EyesLocked) ReseedPart(entry, GazeTargetType.Eyes);
        if (!entry.HeadLocked) ReseedPart(entry, GazeTargetType.Head);
        if (!entry.BodyLocked) ReseedPart(entry, GazeTargetType.Body);
    }

    private void ReseedPart(GazeEntry entry, GazeTargetType part)
    {
        var target = new LookAtTarget();
        switch (EffectiveMode(entry))
        {
            case GazeTargetMode.Camera:
            case GazeTargetMode.Forward:
                // Position source; the detour refreshes the position per loop.
                target.LookMode = LookMode.Position;
                target.Position = _cameraService.GetCameraPosition();
                break;
            case GazeTargetMode.Entity:
                // Id source through the union — the game follows the object.
                target.LookMode = LookMode.Target;
                target.ActorTargetId = entry.TargetId;
                break;
            default:
                target.LookMode = LookMode.None;
                break;
        }
        WritePart(entry, part, target);
    }

    private static void ApplyPartLock(GazeEntry entry, GazeTargetType part, Vector3 position)
    {
        var target = new LookAtTarget { LookMode = LookMode.Position, Position = position };
        if (part.HasFlag(GazeTargetType.Eyes)) entry.EyesLocked = true;
        if (part.HasFlag(GazeTargetType.Head)) entry.HeadLocked = true;
        if (part.HasFlag(GazeTargetType.Body)) entry.BodyLocked = true;
        WritePart(entry, part, target);
    }

    private static void ClearPartLock(GazeEntry entry, GazeTargetType part)
    {
        if (part.HasFlag(GazeTargetType.Eyes)) entry.EyesLocked = false;
        if (part.HasFlag(GazeTargetType.Head)) entry.HeadLocked = false;
        if (part.HasFlag(GazeTargetType.Body)) entry.BodyLocked = false;
    }

    private static void WritePart(GazeEntry entry, GazeTargetType part, LookAtTarget target)
    {
        if (part.HasFlag(GazeTargetType.Eyes)) entry.Target.Eyes.LookAtTarget = target;
        if (part.HasFlag(GazeTargetType.Head)) entry.Target.Head.LookAtTarget = target;
        if (part.HasFlag(GazeTargetType.Body)) entry.Target.Body.LookAtTarget = target;
    }

    private static Vector3 ForwardPoint(IActor actor)
    {
        var nativeObj = (GameObject*)actor.Address;
        var position = new Vector3(nativeObj->Position.X, nativeObj->Position.Y, nativeObj->Position.Z);
        var forwardDir = new Vector3(MathF.Sin(nativeObj->Rotation), 0f, MathF.Cos(nativeObj->Rotation));
        return position + forwardDir * 10f + new Vector3(0, 1.5f, 0);
    }

    // ── lifecycle reconciliation ─────────────────────────────────────────

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            lock (_sync)
            {
                _entries.Clear();
            }
        }
    }

    /// <summary>
    /// Actor-list reconciliation by stable id: a departed source drops its
    /// entry; a departed Entity target transitions its source to Off. Nothing
    /// ever follows a reused address.
    /// </summary>
    private void OnActorListChanged(ActorListChangedEvent _)
    {
        lock (_sync)
        {
            if (_entries.Count == 0)
                return;
            List<ulong>? removed = null;
            foreach (var (id, entry) in _entries)
            {
                if (_objectTable.SearchById(id) == null)
                {
                    (removed ??= new List<ulong>()).Add(id);
                    continue;
                }
                if (entry.Mode == GazeTargetMode.Entity && entry.TargetId != 0 &&
                    _objectTable.SearchById(entry.TargetId) == null)
                {
                    entry.TargetId = 0;
                    entry.Mode = GazeTargetMode.None;
                    _log.Debug($"GazeService: gaze target of {id} despawned — gaze off.");
                }
            }
            if (removed != null)
                foreach (var id in removed)
                    _entries.Remove(id);
        }
    }

    public void Dispose()
    {
        _actorLookAtLoop.Dispose();
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct LookAtSource
{
    public LookAtType Body;
    public LookAtType Head;
    public LookAtType Eyes;
    public LookAtType Unknown;
}

[StructLayout(LayoutKind.Explicit)]
internal struct LookAtType
{
    [FieldOffset(0x30)] public LookAtTarget LookAtTarget;
}

[StructLayout(LayoutKind.Explicit)]
internal struct LookAtTarget
{
    [FieldOffset(0x08)] public LookMode LookMode;
    // Position and the actor-target id are a union at 0x10 — corroborated by
    // Brio ActorLookAtService.LookAtTarget and Ktisis ActorGaze.Gaze.
    [FieldOffset(0x10)] public Vector3 Position;
    [FieldOffset(0x10)] public ulong ActorTargetId;
}

internal enum LookMode
{
    None = 0,
    // Value 1 is id-based object tracking (Brio LookMode.Target / Ktisis
    // GazeMode.Object) — previously mislabeled "Frozen".
    Target = 1,
    Pivot = 2,
    Position = 3,
}
