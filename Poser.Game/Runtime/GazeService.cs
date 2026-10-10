using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
using Poser.Domain;
using Poser.Domain.Identity;
using Poser.Entities;
using Poser.Services;

using Poser.Domain.Scene;

using Poser.Application.Viewport;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using static Poser.Game.GazeEntryStore;
using static Poser.Game.GazeNativeDriver;

namespace Poser.Game;

/// <summary>
/// Service for controlling actor gaze (where they look). Based on Brio's
/// ActorLookAtService. One entry per actor, keyed by the binding registry's
/// <see cref="ActorId"/>: a GPose clone SHARES its source's GameObjectId, so
/// that id cannot tell two player-seeded actors apart. The detour finds an
/// entry by the body's address through an index the reconciliation pass
/// keeps current. The Entity target is remembered by ActorId too; its
/// GameObjectId is only the value written natively (LookMode.Target through
/// the Brio/Ktisis-verified id/position union). Position mode holds a shared
/// world anchor plus per-part positions the detour writes unchanged.
/// </summary>
public unsafe class GazeService : IDisposable
{
    private readonly ICameraProjection _cameraService;
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _eventBus;
    private readonly IPluginLog _log;
    private readonly IFramework? _framework;
    private readonly Poser.Game.Posing.GazePoseFrames _gazeFrames;

    /// <summary>Spawn/discovery-standard thread refusal (ActorSpawnService
    /// shape) for the members that write natively outside the hooked loop.</summary>
    private bool OnOwnerThread => _framework is null || _framework.IsInFrameworkUpdateThread;

    private bool _isAvailable;
    private bool _disposed;
    private bool _subscribed;

    /// <summary>Whether the native gaze capability initialized successfully.</summary>
    public bool IsAvailable => _isAvailable && !_disposed;

    /// <summary>
    /// Stable detail for an unavailable native capability; null when available.
    /// </summary>
    public string? UnavailableDetail { get; private set; }

    private readonly IEntityBindings _bindings;
    private readonly GazeEntryStore _store = new();
    private readonly GazeNativeDriver _driver;
    private readonly GazeReconciler _reconciler;

    public GazeService(
        IGPoseService gPoseService,
        ICameraProjection cameraService,
        IObjectTable objectTable,
        IEventBus eventBus,
        ISigScanner sigScanner,
        IGameInteropProvider hooks,
        IPluginLog log,
        IFramework framework,
        IEntityBindings bindings,
        Poser.Game.Posing.GazePoseFrames gazeFrames)
        : this(
            gPoseService,
            cameraService,
            objectTable,
            eventBus,
            sigScanner,
            hooks,
            log,
            framework,
            new GazeNativeFactory(), bindings, gazeFrames)
    {
    }

    internal GazeService(
        IGPoseService gPoseService,
        ICameraProjection cameraService,
        IObjectTable objectTable,
        IEventBus eventBus,
        ISigScanner sigScanner,
        IGameInteropProvider hooks,
        IPluginLog log,
        IFramework? framework,
        IGazeNativeFactory nativeFactory,
        IEntityBindings bindings,
        Poser.Game.Posing.GazePoseFrames? gazeFrames = null)
    {
        _bindings = bindings;
        _cameraService = cameraService;
        _objectTable = objectTable;
        _eventBus = eventBus;
        _log = log;
        _framework = framework;
        _gazeFrames = gazeFrames ?? new();
        _driver = new GazeNativeDriver(
            gPoseService, cameraService, objectTable, log, _store, _gazeFrames, nativeFactory,
            () => IsAvailable, () => OnOwnerThread);
        _reconciler = new GazeReconciler(
            _store, _driver, bindings, objectTable, eventBus, log, _gazeFrames,
            () => IsAvailable, () => OnOwnerThread);

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
            _driver.SetUpdateLookAt(updateLookAtAddress);
            hook = nativeFactory.CreateActorLookAtHook(
                hooks,
                actorLookAtLoopAddress,
                _driver.ActorLookAtDetour);
            // Published before enabling: the detour reads it and can run at once.
            _driver.LoopHook = hook;
            hook.Enable();
            _isAvailable = true;

            _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
            _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);
            _subscribed = true;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_driver.LoopHook, hook))
                _driver.LoopHook = null;
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

    /// <summary>
    /// The channels the detour will enforce for this actor on its next pass.
    /// Everything absent is handed back to the game. This is the observable
    /// form of the release contract, so it is what the tests assert.
    /// </summary>
    internal GazeTargetType WrittenParts(ActorId actor)
    {
        lock (_store.Sync)
        {
            return _store.TryGet(actor, out var entry)
                ? EnforcedParts(entry)
                : GazeTargetType.None;
        }
    }

    /// <summary>
    /// The channels owed a one-shot hand-back on the detour's next pass. The
    /// other half of the release contract, and likewise what the tests assert.
    /// </summary>
    internal GazeTargetType PendingRelease(ActorId actor)
    {
        lock (_store.Sync)
        {
            return _store.TryGet(actor, out var entry)
                ? entry.PendingRelease
                : GazeTargetType.None;
        }
    }

    private Outcome Unavailable() =>
        Outcome.Fail(UnavailableDetail ?? "Gaze capability unavailable.");

    /// <summary>The remembered target is gone, so reapplying it is refused by
    /// name instead of quietly following nothing or a reused address.</summary>
    private static Outcome StaleRefusal(GazeEntry entry) => Outcome.Fail(
        $"The remembered gaze target ({entry.TargetId:X}) has left the scene. Choose another actor.");

    /// <summary>The actor's stable id and live body. An actor the registry
    /// has not bound has no gaze identity: keying it by anything else is how
    /// two clones of one player came to share an entry.</summary>
    private bool Resolve(IActor actor, out ActorId id, [NotNullWhen(true)] out IGameObject? body)
    {
        id = default;
        body = null;
        if (actor.Address == nint.Zero || _bindings.GetActorId(actor) is not { } bound)
            return false;
        id = bound;
        body = _objectTable.CreateObjectReference(actor.Address);
        return body != null;
    }

    /// <summary>Snapshot of the actor's managed gaze state.</summary>
    public GazeState GetGazeState(IActor actor)
    {
        if (!IsAvailable)
            return new GazeState();
        if (!Resolve(actor, out var id, out _))
            return new GazeState();
        lock (_store.Sync)
        {
            return _store.TryGet(id, out var entry)
                ? new GazeState
                {
                    PoseAware = entry.PoseAware,
                    Mode = entry.Mode,
                    Active = EffectiveMode(entry) != GazeTargetMode.None,
                    TargetStale = entry.TargetStale,
                    TargetType = entry.Parts,
                    TargetActor = entry.TargetActor,
                    TargetId = entry.TargetId,
                    Position = entry.Position,
                    EyesPosition = entry.Target.Eyes.LookAtTarget.Position,
                    HeadPosition = entry.Target.Head.LookAtTarget.Position,
                    BodyPosition = entry.Target.Body.LookAtTarget.Position,
                }
                : new GazeState();
        }
    }

    public Outcome RestoreSettings(IActor actor, Poser.Application.Gaze.GazeSettings settings)
    {
        if (!IsAvailable) return Unavailable();
        if (!Resolve(actor, out var id, out var gameObject))
            return Outcome.Fail("This actor is no longer resolvable.");
        bool writable = CanWriteCharacter(gameObject);
        ulong? pendingTarget;
        lock (_store.Sync)
        {
            var entry = _store.Bind(id, gameObject.Address);
            if (settings.Mode == GazeTargetMode.Entity && entry.TargetActor != null && entry.TargetStale)
                return StaleRefusal(entry);
            entry.Mode = settings.Mode;
            entry.PoseAware = settings.PoseAware;
            entry.Parts = settings.TargetType;
            entry.Position = settings.Position;
            ClearPartLock(entry, GazeTargetType.All);
            ReseedUnlockedParts(entry);
            foreach (var part in new[] { GazeTargetType.Eyes, GazeTargetType.Head, GazeTargetType.Body })
            {
                // Restoring a lock restores its frozen point, not today's camera/actor target.
                if (settings.IsPartLocked(part)) ApplyPartLock(entry, part, settings.PartPosition(part));
                else if (settings.Mode is GazeTargetMode.Position or GazeTargetMode.None or GazeTargetMode.Detached)
                    WritePart(entry, part, new LookAtTarget {
                        LookMode = settings.Mode == GazeTargetMode.Position ? LookMode.Position : LookMode.None,
                        Position = settings.PartPosition(part),
                    });
            }
            BookRelease(entry);
            pendingTarget = _driver.PendingTargetWrite(entry, writable);
        }
        _driver.WriteCharacterTarget(gameObject, pendingTarget);
        _eventBus.Publish(new GazeStateChangedEvent());
        return Outcome.Ok();
    }

    public Outcome SetPoseAware(IActor actor, bool enabled)
    {
        if (!IsAvailable) return Unavailable();
        if (!Resolve(actor, out var id, out var gameObject))
            return Outcome.Fail("This actor is no longer resolvable.");
        lock (_store.Sync) _store.Bind(id, gameObject.Address).PoseAware = enabled;
        if (!enabled) _gazeFrames.Request(actor.Address, false);
        _eventBus.Publish(new GazeStateChangedEvent());
        return Outcome.Ok();
    }

    /// <summary>
    /// One mode transition. Entering a non-Off mode with no participating
    /// parts enables all three. Entity mode without a chosen target performs
    /// no native override until a target is set. Off keeps the remembered
    /// target and per-part points — only <see cref="ResetGaze"/> forgets them.
    /// Re-entering Entity on a stale remembered target is refused.
    /// </summary>
    public Outcome SetGazeMode(IActor actor, GazeTargetMode mode)
    {
        if (!IsAvailable)
            return Unavailable();
        if (!Resolve(actor, out var id, out var gameObject))
            return Outcome.Fail("This actor is no longer resolvable.");
        bool modeChanged;
        ulong? pendingTarget;
        // Resolved before the lock: the gate reads Dalamud wrapper properties,
        // and the detour contends on the store lock from the native thread.
        bool writable = CanWriteCharacter(gameObject);
        lock (_store.Sync)
        {
            var entry = _store.Bind(id, gameObject.Address);
            // Re-selecting Actor mode is a reapply of the remembered target, so
            // a stale one is refused here rather than silently doing nothing.
            if (mode == GazeTargetMode.Entity && entry.TargetActor != null && entry.TargetStale)
                return StaleRefusal(entry);
            var beforeMode = EffectiveMode(entry);
            var previousMode = entry.Mode;
            entry.Mode = mode;
            if (mode == GazeTargetMode.None)
            {
                // Off stops every write and clears locks; the game's own
                // update re-takes the released slots. The remembered target and
                // the stored per-part points deliberately survive — this is the
                // toggle the user expects to be able to undo.
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
            BookRelease(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
            pendingTarget = _driver.PendingTargetWrite(entry, writable);
        }
        // Leaving Entity clears the character's imposed target id, so the
        // game's own look-at stops pointing at the actor Poser chose.
        _driver.WriteCharacterTarget(gameObject, pendingTarget);
        // Published outside the lock — the detour contends on the store lock from the
        // native thread, so the bus is never invoked while holding it.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
        return Outcome.Ok();
    }

    /// <summary>
    /// Changes part participation only, exactly as Brio's SetTargetType does:
    /// a part removed from the mask is simply no longer written, so the game's
    /// own look-at resumes owning it. The mode and target survive an empty
    /// mask, so re-adding a part resumes what was configured. Re-adding a part
    /// on a stale remembered target is refused; removing one never is.
    /// </summary>
    public Outcome SetGazeParts(IActor actor, GazeTargetType parts)
    {
        if (!IsAvailable)
            return Unavailable();
        if (!Resolve(actor, out var id, out var gameObject))
            return Outcome.Fail("This actor is no longer resolvable.");
        bool modeChanged;
        ulong? pendingTarget;
        bool writable = CanWriteCharacter(gameObject);
        lock (_store.Sync)
        {
            var entry = _store.Bind(id, gameObject.Address);
            // Adding a part back is a reapply of the remembered configuration.
            // Relinquishing one never is, so only additions can be refused.
            if ((parts & ~entry.Parts) != GazeTargetType.None
                && entry.Mode == GazeTargetMode.Entity
                && entry.TargetActor != null
                && entry.TargetStale)
                return StaleRefusal(entry);
            var beforeMode = EffectiveMode(entry);
            // Removing a part relinquishes it immediately — the detour just
            // stops writing it — and a locked part being disabled unlocks.
            ClearPartLock(entry, entry.Parts & ~parts);
            entry.Parts = parts;
            // The mode is NOT cleared when the last part goes off. Brio's
            // SetTargetType rewrites the participation mask and nothing else,
            // so the mode and the chosen target are still there to resume from
            // the moment a part comes back.
            ReseedUnlockedParts(entry);
            // The untoggled channel is owed its hand-back here: the remembered
            // mode and target survive, but the controller must stop aiming the
            // channel Poser just gave up.
            BookRelease(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
            pendingTarget = _driver.PendingTargetWrite(entry, writable);
        }
        // All-off drops the character's imposed target id; the first part back
        // reapplies it, which is what makes retoggling resume tracking.
        _driver.WriteCharacterTarget(gameObject, pendingTarget);
        // Crossing between "some part enforced" and "none" is the transition;
        // part edits that leave that alone stay silent. Published outside lock.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
        return Outcome.Ok();
    }

    /// <summary>
    /// Chooses the Entity-mode target and switches to Entity mode. The
    /// source actor itself is rejected.
    /// </summary>
    public Outcome SetGazeTarget(IActor actor, IActor target)
    {
        if (!IsAvailable)
            return Unavailable();
        if (!OnOwnerThread)
            return Outcome.Fail("Gaze targets can only be set on the game thread.");
        if (!Resolve(actor, out var id, out var gameObject)
            || !Resolve(target, out var targetKey, out var targetObject))
            return Outcome.Fail("This actor is no longer resolvable.");
        // The same predicate the write funnel enforces, spelled once, so this
        // refusal can never drift from what WriteCharacterTarget will accept.
        // It is stated here as well only to name the reason for the user.
        if (!CanWriteCharacter(gameObject))
            return Outcome.Fail("Only a GPose actor can be given a gaze target.");
        // Stable identity, not GameObjectId: two clones of one player share
        // that id and are still two actors that may look at each other.
        if (id == targetKey)
        {
            _log.Warning("GazeService: an actor cannot gaze at itself.");
            return Outcome.Fail("An actor cannot gaze at itself.");
        }
        bool modeChanged;
        ulong? pendingTarget;
        bool writable = CanWriteCharacter(gameObject);
        lock (_store.Sync)
        {
            var entry = _store.Bind(id, gameObject.Address);
            var beforeMode = EffectiveMode(entry);
            entry.TargetActor = targetKey;
            entry.TargetId = targetObject.GameObjectId;
            // A freshly chosen target is live by construction, and the stale
            // mark is sticky everywhere else, so this is the ONE place it is
            // lifted — an id reappearing does not resume anything by itself.
            entry.TargetStale = false;
            entry.Mode = GazeTargetMode.Entity;
            if (entry.Parts == GazeTargetType.None)
                entry.Parts = GazeTargetType.All;
            ReseedUnlockedParts(entry);
            BookRelease(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
            pendingTarget = _driver.PendingTargetWrite(entry, writable);
        }
        // Brio parity (SetActorTarget): the character's own target id backs
        // the game's id-based look tracking. Written through the RESOLVED
        // wrapper's address — the raw IActor address is only a claim.
        _driver.WriteCharacterTarget(gameObject, pendingTarget);
        // Retargeting within Entity mode is not a mode transition; only the
        // move INTO Entity publishes. Published outside the lock.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
        return Outcome.Ok();
    }

    /// <summary>
    /// Position mode only: moves the shared anchor and every enabled,
    /// unlocked part to <paramref name="position"/>. No-op in any other mode
    /// or when no entry exists.
    /// </summary>
    public void SetGazePosition(IActor actor, Vector3 position)
    {
        if (!IsAvailable)
            return;
        if (!Resolve(actor, out var id, out _))
            return;
        lock (_store.Sync)
        {
            if (!_store.TryGet(id, out var entry) ||
                SeedMode(entry) != GazeTargetMode.Position)
                return; // the anchor exists only in Position mode
            entry.Position = position;
            // Locked parts keep their frozen positions — existing guarantee.
            ReseedUnlockedParts(entry);
        }
    }

    /// <summary>
    /// Position mode only: writes one part's target position explicitly.
    /// Works on locked parts too — an explicit user edit outranks a lock, and
    /// the lock flag itself is untouched. Does not move the anchor.
    /// </summary>
    public void SetPartPosition(IActor actor, GazeTargetType part, Vector3 position)
    {
        if (!IsAvailable)
            return;
        if (!Resolve(actor, out var id, out _))
            return;
        lock (_store.Sync)
        {
            if (!_store.TryGet(id, out var entry) ||
                SeedMode(entry) != GazeTargetMode.Position)
                return;
            // An explicit user edit outranks a lock, so locked parts move too;
            // the lock flag and the shared anchor are both left alone.
            WritePart(entry, part, new LookAtTarget { LookMode = LookMode.Position, Position = position });
        }
    }

    /// <summary>
    /// Brio's "set to camera value": <see cref="SetPartPosition"/> with the
    /// current camera position.
    /// </summary>
    public void SnapPartToCamera(IActor actor, GazeTargetType part)
    {
        if (!IsAvailable)
            return;
        if (!Resolve(actor, out var id, out _))
            return;
        lock (_store.Sync)
        {
            if (!_store.TryGet(id, out var entry) ||
                SeedMode(entry) != GazeTargetMode.Position)
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

    /// <summary>
    /// Freezes/unfreezes one participating part at its actual current
    /// target. Does not change the mode, the participation mask, or other
    /// parts.
    /// </summary>
    public void SetPartLock(IActor actor, GazeTargetType part, bool locked)
    {
        if (!IsAvailable)
            return;
        if (!Resolve(actor, out var id, out _))
            return;
        lock (_store.Sync)
        {
            if (!_store.TryGet(id, out var entry))
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
                    _ => TargetPosition(entry.TargetActor) ?? _cameraService.GetCameraPosition(),
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

    /// <summary>Whether the given part is frozen.</summary>
    public bool IsPartLocked(IActor actor, GazeTargetType part)
    {
        if (!IsAvailable)
            return false;
        if (!Resolve(actor, out var id, out _))
            return false;
        lock (_store.Sync)
        {
            if (!_store.TryGet(id, out var entry))
                return false;
            if (part.HasFlag(GazeTargetType.Eyes) && entry.EyesLocked) return true;
            if (part.HasFlag(GazeTargetType.Head) && entry.HeadLocked) return true;
            if (part.HasFlag(GazeTargetType.Body) && entry.BodyLocked) return true;
            return false;
        }
    }

    /// <summary>Removes state and the native handle — full game default.</summary>
    public void ResetGaze(IActor actor)
    {
        if (!IsAvailable)
            return;
        if (!Resolve(actor, out var id, out var gameObject))
            return;
        bool modeChanged;
        ulong? pendingTarget = null;
        lock (_store.Sync)
        {
            // Brio's RemoveObjectFromLook — the ONE path that forgets the
            // remembered target, as opposed to the toggles, which keep it. The
            // entry itself is cleared in place rather than dropped: it is the
            // ledger the detour reads to deliver the hand-back, and dropping it
            // would strand every claimed channel at its last gaze.
            if (!_store.TryGet(id, out var entry))
                return;
            modeChanged = EffectiveMode(entry) != GazeTargetMode.None;
            if (entry.AppliedTargetId != 0 && OnOwnerThread)
            {
                pendingTarget = 0;
                entry.AppliedTargetId = 0;
            }
            entry.Mode = GazeTargetMode.None;
            entry.PoseAware = false;
            entry.Parts = GazeTargetType.All;
            entry.TargetActor = null;
            entry.TargetId = 0;
            entry.TargetStale = false;
            entry.Position = default;
            ClearPartLock(entry, GazeTargetType.All);
            ReseedUnlockedParts(entry);
            BookRelease(entry);
        }
        _driver.WriteCharacterTarget(gameObject, pendingTarget);
        // A dropped entry that was already effectively Off changed nothing.
        // Published outside the lock.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
    }

    // ── entry seeding (all callers hold the store lock) ──────────────────

    /// <summary>The remembered target's live position, resolved through the
    /// registry; null when it is gone.</summary>
    private Vector3? TargetPosition(ActorId? target) =>
        target is { } id
        && _bindings.Resolve(id) is { Success: true, Value: { } live }
        && _objectTable.CreateObjectReference(live.Address) is { } body
            ? body.Position
            : null;

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
        // Seeded from the CONFIGURED mode, so untoggling every part leaves the
        // stored per-part sources intact for the retoggle to resume from.
        switch (SeedMode(entry))
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
        _gazeFrames.Clear();
        if (!e.IsGPosing)
        {
            lock (_store.Sync)
            {
                _store.Clear();
            }
        }
    }

    private void OnActorListChanged(ActorListChangedEvent _) => _gazeFrames.Clear();

    /// <summary>See <see cref="GazeReconciler.Reconcile"/>.</summary>
    public void Reconcile() => _reconciler.Reconcile();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _isAvailable = false;
        _driver.DisposeHook();
        if (_subscribed)
        {
            _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
            _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
            _subscribed = false;
        }
    }
}
