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

internal unsafe delegate nint GazeLoopDelegate(ContainerInterface* args);

internal unsafe interface IGazeHook : IDisposable
{
    void Enable();
    nint Original(ContainerInterface* args);
}

internal interface IGazeNativeFactory
{
    nint ScanUpdateLookAt(ISigScanner scanner);
    nint ScanActorLookAtLoop(ISigScanner scanner);
    IGazeHook CreateActorLookAtHook(
        IGameInteropProvider hooks,
        nint address,
        GazeLoopDelegate detour);
}

internal sealed class GazeNativeFactory : IGazeNativeFactory
{
    public nint ScanUpdateLookAt(ISigScanner scanner) => scanner.ScanText(
        "E8 ?? ?? ?? ?? 8B D7 48 8B CB E8 ?? ?? ?? ?? 41 ?? ?? 8B D7 48 ?? ?? 48 ?? ?? ?? ?? 48 83 ?? ?? 5F");

    public nint ScanActorLookAtLoop(ISigScanner scanner) => scanner.ScanText(
        "E8 ?? ?? ?? ?? 48 83 C3 08 48 83 EF 01 75 CF 48 ?? ?? ?? ?? 48");

    public IGazeHook CreateActorLookAtHook(
        IGameInteropProvider hooks,
        nint address,
        GazeLoopDelegate detour) =>
        new DalamudGazeHook(hooks.HookFromAddress<GazeLoopDelegate>(address, detour));

    private sealed class DalamudGazeHook(Hook<GazeLoopDelegate> hook) : IGazeHook
    {
        public void Enable() => hook.Enable();

        public unsafe nint Original(ContainerInterface* args) => hook.Original(args);

        public void Dispose() => hook.Dispose();
    }
}

/// <summary>
/// Service for controlling actor gaze (where they look). Based on Brio's
/// ActorLookAtService. One entry per actor, keyed by the native GameObjectId —
/// there is no second wrapper-keyed map to desync, so an ordinary actor-list
/// refresh cannot orphan state. The Entity target is a GameObjectId written
/// natively (LookMode.Target through the Brio/Ktisis-verified id/position
/// union); no captured address is ever dereferenced. Position mode holds a
/// shared world anchor plus per-part positions the detour writes unchanged.
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
    private IGazeHook? _actorLookAtLoop;
    private bool _isAvailable;
    private bool _disposed;
    private bool _subscribed;

    public bool IsAvailable => _isAvailable && !_disposed;

    public string? UnavailableDetail { get; private set; }

    /// <summary>
    /// One managed+native entry per actor. Mutated from the UI thread and read
    /// from the hooked game loop, so every access goes through the sync lock.
    /// </summary>
    private sealed class GazeEntry
    {
        public GazeTargetMode Mode;
        public GazeTargetType Parts = GazeTargetType.All;
        public ulong TargetId;              // Entity-mode target GameObjectId; 0 = unset
        public Vector3 Position;            // Position-mode shared world anchor
        public LookAtSource Target;         // per-part native write source
        public bool EyesLocked;
        public bool HeadLocked;
        public bool BodyLocked;

        // Release contract: NONE (Brio parity). Removing a part simply stops
        // the per-frame writes for it; the game's own loop re-takes the slot.
        // Both write-on-release variants were tried and pinned the part to a
        // stale target instead (user 2026-08-04/07).
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
        : this(
            gPoseService,
            cameraService,
            objectTable,
            eventBus,
            sigScanner,
            hooks,
            log,
            new GazeNativeFactory())
    {
    }

    internal GazeService(
        IGPoseService gPoseService,
        ICameraService cameraService,
        IObjectTable objectTable,
        IEventBus eventBus,
        ISigScanner sigScanner,
        IGameInteropProvider hooks,
        IPluginLog log,
        IGazeNativeFactory nativeFactory)
    {
        _gPoseService = gPoseService;
        _cameraService = cameraService;
        _objectTable = objectTable;
        _eventBus = eventBus;
        _log = log;

        nint updateLookAtAddress;
        try
        {
            updateLookAtAddress = nativeFactory.ScanUpdateLookAt(sigScanner);
            if (updateLookAtAddress == nint.Zero)
            {
                SetUnavailable("Required gaze update signature unavailable.");
                return;
            }
        }
        catch (Exception ex)
        {
            SetUnavailable("Required gaze update signature unavailable.", ex);
            return;
        }

        nint actorLookAtLoopAddress;
        try
        {
            actorLookAtLoopAddress = nativeFactory.ScanActorLookAtLoop(sigScanner);
            if (actorLookAtLoopAddress == nint.Zero)
            {
                SetUnavailable("Required gaze loop signature unavailable.");
                return;
            }
        }
        catch (Exception ex)
        {
            SetUnavailable("Required gaze loop signature unavailable.", ex);
            return;
        }

        IGazeHook? hook = null;
        try
        {
            _updateLookAt = (delegate* unmanaged<CharacterLookAtController*, LookAtTarget*, uint, nint, void>)updateLookAtAddress;
            hook = nativeFactory.CreateActorLookAtHook(
                hooks,
                actorLookAtLoopAddress,
                ActorLookAtDetour);
            hook.Enable();
            _actorLookAtLoop = hook;
            _isAvailable = true;

            _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
            _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);
            _subscribed = true;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_actorLookAtLoop, hook))
                _actorLookAtLoop = null;
            hook?.Dispose();
            SetUnavailable(
                hook is null
                    ? "Gaze hook creation failed."
                    : "Gaze hook enable failed.",
                ex);
        }
    }

    private void SetUnavailable(string detail, Exception? error = null)
    {
        _isAvailable = false;
        UnavailableDetail = detail;
        if (error is null)
            _log.Warning($"GazeService: {detail}");
        else
            _log.Warning($"GazeService: {detail} {error.Message}");
    }

    /// <summary>Entity mode without a chosen target performs no override.</summary>
    private static GazeTargetMode EffectiveMode(GazeEntry entry) =>
        entry.Mode == GazeTargetMode.Entity && entry.TargetId == 0
            ? GazeTargetMode.None
            : entry.Mode;

    private nint ActorLookAtDetour(ContainerInterface* args)
    {
        if (!IsAvailable)
            return _actorLookAtLoop!.Original(args);

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
                // The GPose index gate is load-bearing (Brio ActorTableHelpers
                // 201..439): a GPose clone SHARES its GameObjectId with the
                // overworld original, and without the gate every write lands
                // on both bodies.
                if (targetActor is not null && targetActor.IsValid()
                    && targetActor.ObjectIndex is >= 201 and <= 439)
                {
                    GazeTargetMode mode = GazeTargetMode.None;
                    GazeTargetType parts = GazeTargetType.None;
                    LookAtSource lookAt = default;
                    bool known = false;
                    bool eyesLocked = false, headLocked = false, bodyLocked = false;
                    lock (_sync)
                    {
                        if (_entries.TryGetValue(targetActor.GameObjectId, out var entry))
                        {
                            known = true;
                            mode = EffectiveMode(entry);
                            parts = entry.Parts;
                            // Copy to locals (like Brio) — the native calls
                            // below run outside the lock.
                            lookAt = entry.Target;
                            eyesLocked = entry.EyesLocked;
                            headLocked = entry.HeadLocked;
                            bodyLocked = entry.BodyLocked;
                        }
                    }

                    // Off performs no Poser write at all — release is pure
                    // cessation (Brio parity): the game's own update re-takes
                    // any slot Poser stops writing.
                    if (!known || mode == GazeTargetMode.None)
                        return _actorLookAtLoop!.Original(args);

                    var lookAtController =
                        &((Character*)targetActor.Address)->LookAt.Controller;

                    // Camera and Forward are position sources refreshed each
                    // loop for unlocked parts; Entity carries the target id in
                    // the union and needs no per-loop position poll; Position
                    // carries stored fixed world points, likewise needing no
                    // per-loop poll — they are written through as-is.
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
        return _actorLookAtLoop!.Original(args);
    }

    private IGameObject? Resolve(IActor actor) =>
        actor.Address != nint.Zero ? _objectTable.CreateObjectReference(actor.Address) : null;

    public GazeState GetGazeState(IActor actor)
    {
        if (!IsAvailable)
            return new GazeState();
        if (Resolve(actor) is not { } gameObject)
            return new GazeState();
        lock (_sync)
        {
            return _entries.TryGetValue(gameObject.GameObjectId, out var entry)
                ? new GazeState
                {
                    Mode = entry.Mode,
                    TargetType = entry.Parts,
                    TargetId = entry.TargetId,
                    Position = entry.Position,
                    EyesPosition = entry.Target.Eyes.LookAtTarget.Position,
                    HeadPosition = entry.Target.Head.LookAtTarget.Position,
                    BodyPosition = entry.Target.Body.LookAtTarget.Position,
                }
                : new GazeState();
        }
    }

    public void SetGazeMode(IActor actor, GazeTargetMode mode)
    {
        if (!IsAvailable)
            return;
        if (Resolve(actor) is not { } gameObject)
            return;
        bool modeChanged;
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            var beforeMode = EffectiveMode(entry);
            var previousMode = entry.Mode;
            entry.Mode = mode;
            if (mode == GazeTargetMode.None)
            {
                // Off stops every write and clears locks; the game's own
                // update re-takes the released slots.
                ClearPartLock(entry, GazeTargetType.All);
            }
            else if (entry.Parts == GazeTargetType.None)
            {
                entry.Parts = GazeTargetType.All;
            }
            // Entering Position seeds the anchor halfway between actor and
            // camera (Ktisis GetCameraLerpFor parity) so the gizmo appears in
            // view; re-selecting the mode it is already in never moves it.
            if (mode == GazeTargetMode.Position && previousMode != GazeTargetMode.Position)
                entry.Position = CameraLerpPoint(actor);
            ReseedUnlockedParts(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
        }
        // Published outside the lock — the detour contends on _sync from the
        // native thread, so the bus is never invoked while holding it.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
    }

    public void SetGazeParts(IActor actor, GazeTargetType parts)
    {
        if (!IsAvailable)
            return;
        if (Resolve(actor) is not { } gameObject)
            return;
        bool modeChanged;
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            var beforeMode = EffectiveMode(entry);
            // Removing a part relinquishes it immediately — the detour just
            // stops writing it — and a locked part being disabled unlocks.
            ClearPartLock(entry, entry.Parts & ~parts);
            entry.Parts = parts;
            // Turning off the final active part returns the mode to Off in the
            // same transition; the handle is kept so re-enabling is symmetric.
            if (parts == GazeTargetType.None)
                entry.Mode = GazeTargetMode.None;
            ReseedUnlockedParts(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
        }
        // Only the last-part-off auto-Off is a mode transition; part edits that
        // leave the mode alone stay silent. Published outside the lock.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
    }

    public void SetGazeTarget(IActor actor, IActor target)
    {
        if (!IsAvailable)
            return;
        if (Resolve(actor) is not { } gameObject || Resolve(target) is not { } targetObject)
            return;
        if (gameObject.GameObjectId == targetObject.GameObjectId)
        {
            _log.Warning("GazeService: an actor cannot gaze at itself.");
            return;
        }
        bool modeChanged;
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            var beforeMode = EffectiveMode(entry);
            entry.TargetId = targetObject.GameObjectId;
            entry.Mode = GazeTargetMode.Entity;
            if (entry.Parts == GazeTargetType.None)
                entry.Parts = GazeTargetType.All;
            ReseedUnlockedParts(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
        }
        // Brio parity (SetActorTarget): the character's own target id backs
        // the game's id-based look tracking.
        ((Character*)actor.Address)->SetTargetId(targetObject.GameObjectId);
        // Retargeting within Entity mode is not a mode transition; only the
        // move INTO Entity publishes. Published outside the lock.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
    }

    public nint GetGazeTargetAddress(IActor actor)
    {
        if (!IsAvailable)
            return 0;
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

    public void SetGazePosition(IActor actor, Vector3 position)
    {
        if (!IsAvailable)
            return;
        if (Resolve(actor) is not { } gameObject)
            return;
        lock (_sync)
        {
            if (!_entries.TryGetValue(gameObject.GameObjectId, out var entry) ||
                EffectiveMode(entry) != GazeTargetMode.Position)
                return; // the anchor exists only in Position mode
            entry.Position = position;
            // Locked parts keep their frozen positions — existing guarantee.
            ReseedUnlockedParts(entry);
        }
    }

    public void SetPartPosition(IActor actor, GazeTargetType part, Vector3 position)
    {
        if (!IsAvailable)
            return;
        if (Resolve(actor) is not { } gameObject)
            return;
        lock (_sync)
        {
            if (!_entries.TryGetValue(gameObject.GameObjectId, out var entry) ||
                EffectiveMode(entry) != GazeTargetMode.Position)
                return;
            // An explicit user edit outranks a lock, so locked parts move too;
            // the lock flag and the shared anchor are both left alone.
            WritePart(entry, part, new LookAtTarget { LookMode = LookMode.Position, Position = position });
        }
    }

    public void SnapPartToCamera(IActor actor, GazeTargetType part)
    {
        if (!IsAvailable)
            return;
        if (Resolve(actor) is not { } gameObject)
            return;
        lock (_sync)
        {
            if (!_entries.TryGetValue(gameObject.GameObjectId, out var entry) ||
                EffectiveMode(entry) != GazeTargetMode.Position)
                return;
            // Brio's "set to camera value": a one-shot capture, not a follow.
            var target = new LookAtTarget
            {
                LookMode = LookMode.Position,
                Position = _cameraService.GetCameraPosition(),
            };
            WritePart(entry, part, target);
        }
    }

    public void SetPartLock(IActor actor, GazeTargetType part, bool locked)
    {
        if (!IsAvailable)
            return;
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
                    // Position mode is already position-identity: freeze where
                    // the part already looks so a lock only stops it following
                    // later anchor moves.
                    GazeTargetMode.Position => PartPosition(entry, part) ?? entry.Position,
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
        if (!IsAvailable)
            return false;
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
        if (!IsAvailable)
            return false;
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
        if (!IsAvailable)
            return;
        if (Resolve(actor) is not { } gameObject)
            return;
        bool modeChanged;
        lock (_sync)
        {
            // Release is cessation: dropping the entry stops every write in
            // the same transition.
            modeChanged = _entries.TryGetValue(gameObject.GameObjectId, out var entry) &&
                EffectiveMode(entry) != GazeTargetMode.None;
            _entries.Remove(gameObject.GameObjectId);
        }
        // A dropped entry that was already effectively Off changed nothing.
        // Published outside the lock.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
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
            case GazeTargetMode.Position:
                // Fixed world point — the detour writes it unchanged each loop.
                target.LookMode = LookMode.Position;
                target.Position = entry.Position;
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

    /// <summary>
    /// The single-flag part's stored target position; null when the flag is
    /// not exactly one known part (so callers can fall back to the anchor).
    /// </summary>
    private static Vector3? PartPosition(GazeEntry entry, GazeTargetType part) => part switch
    {
        GazeTargetType.Eyes => entry.Target.Eyes.LookAtTarget.Position,
        GazeTargetType.Head => entry.Target.Head.LookAtTarget.Position,
        GazeTargetType.Body => entry.Target.Body.LookAtTarget.Position,
        _ => null,
    };

    /// <summary>
    /// Halfway between the actor and the camera — Ktisis GetCameraLerpFor,
    /// the seed for a freshly entered Position mode.
    /// </summary>
    private Vector3 CameraLerpPoint(IActor actor)
    {
        var nativeObj = (GameObject*)actor.Address;
        var actorPos = new Vector3(nativeObj->Position.X, nativeObj->Position.Y, nativeObj->Position.Z);
        return Vector3.Lerp(actorPos, _cameraService.GetCameraPosition(), 0.5f);
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
        bool modeChanged = false;
        lock (_sync)
        {
            if (_entries.Count > 0)
            {
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
                        ClearPartLock(entry, GazeTargetType.All);
                        // Entity with a live target was effectively Entity, so
                        // this branch is always a transition to Off.
                        modeChanged = true;
                        _log.Debug($"GazeService: gaze target of {id} despawned — gaze off.");
                    }
                }
                if (removed != null)
                    foreach (var id in removed)
                        _entries.Remove(id);
            }
        }
        // Published outside the lock, once for the whole reconciliation pass.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _isAvailable = false;
        _actorLookAtLoop?.Dispose();
        _actorLookAtLoop = null;
        if (_subscribed)
        {
            _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
            _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
            _subscribed = false;
        }
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

[StructLayout(LayoutKind.Explicit, Size = 0x28)]
internal struct LookAtTarget
{
    [FieldOffset(0x08)] public LookMode LookMode;
    // Position and the actor-target id are a union at 0x10 — corroborated by
    // Brio ActorLookAtService.LookAtTarget and Ktisis ActorGaze.Gaze.
    [FieldOffset(0x10)] public Vector3 Position;
    [FieldOffset(0x10)] public ulong ActorTargetId;
    // Trailing field of the native 0x28 CharacterLookAtTargetParam (Ktisis
    // Gaze.Unk5). The explicit size keeps captures and native reads
    // byte-complete instead of over-reading adjacent managed memory.
    [FieldOffset(0x20)] public uint Unknown20;
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
