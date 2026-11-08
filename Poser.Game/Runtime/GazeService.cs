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
using Poser.Domain;
using Poser.Domain.Identity;

using Poser.Domain.Scene;

using Poser.Application.Viewport;
using Poser.Application.Events;
using Poser.Application.Lifecycle;
using Poser.Game.Core;
using Poser.Game.Entities;
using Poser.Game.Services;
using static Poser.Game.GazeEntryStore;
using static Poser.Game.GazeNativeDriver;

namespace Poser.Game;

/// <summary>
/// Controls where an actor looks. Settings use Poser ActorId because multiple GPose
/// copies can share a game object ID. The native hook finds settings by the current
/// body address; reconciliation updates that index after binding changes.
/// </summary>
public unsafe class GazeService : IDisposable
{
    private readonly ICameraProjection _cameraService;
    private readonly IObjectTable _objectTable;
    private readonly IEventBus _eventBus;
    private readonly IPluginLog _log;
    private readonly IFramework? _framework;
    private readonly Poser.Game.Posing.GazePoseFrames _gazeFrames;

    /// <summary>
    /// Native target commands require the framework thread when a framework is supplied.
    /// </summary>
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
    /// Parts configured for active target writes on the next hook pass.
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
    /// Parts waiting for a disable write on the next hook pass.
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

    /// <summary>
    /// Asks the user to replace an unavailable remembered target.
    /// </summary>
    private static Outcome StaleRefusal(GazeEntry entry) => Outcome.Fail(
        $"The remembered gaze target ({entry.TargetId:X}) has left the scene. Choose another actor.");

    /// <summary>
    /// Finds the Poser actor ID and a game-object wrapper for its current address.
    /// Native writes check the wrapper separately with CanWriteCharacter.
    /// </summary>
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
    /// Changes the gaze mode. Enabling a mode with no participating parts enables all
    /// three. Entity mode waits for a target; selecting a stale remembered target fails.
    /// Off retains the actor target but clears locks and rebuilds the stored part targets.
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
                // Off clears locks; BookRelease queues the native disable writes.
                ClearPartLock(entry, GazeTargetType.All);
            }
            else if (entry.Parts == GazeTargetType.None)
            {
                entry.Parts = GazeTargetType.All;
            }
            // Start a new Position target between actor and camera. Reselecting
            // Position preserves its current anchor.
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
    /// Changes which parts follow the configured target. Removed parts receive a disable
    /// write on the next hook pass. The mode and actor target survive an empty mask.
    /// Adding parts in Entity mode fails if the remembered target is stale.
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
            // Removed parts lose their locks and receive a queued disable write.
            ClearPartLock(entry, entry.Parts & ~parts);
            entry.Parts = parts;
            // Keep the mode and actor target for when a part is enabled again.
            ReseedUnlockedParts(entry);
            // Queue the native disable writes for removed parts.
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
        // Report the same GPose restriction used by the native write.
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
            // Explicitly selecting a resolved target clears its stale flag.
            entry.TargetStale = false;
            entry.Mode = GazeTargetMode.Entity;
            if (entry.Parts == GazeTargetType.None)
                entry.Parts = GazeTargetType.All;
            ReseedUnlockedParts(entry);
            BookRelease(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
            pendingTarget = _driver.PendingTargetWrite(entry, writable);
        }
        // Set the native target ID used by the game's object tracking.
        _driver.WriteCharacterTarget(gameObject, pendingTarget);
        // Notify only when the effective mode changed, outside the lock.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
        return Outcome.Ok();
    }

    /// <summary>
    /// In Position mode, moves the shared anchor and rebuilds every unlocked part target.
    /// Disabled parts are updated too, ready for later use. Other modes are unchanged.
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
            // Leave locked targets at their stored points.
            ReseedUnlockedParts(entry);
        }
    }

    /// <summary>
    /// Changes the specified Position-mode part targets, including locked parts.
    /// Leaves their lock flags and the shared anchor unchanged.
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
    /// Copies the current camera position into the specified Position-mode targets.
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
    /// Freezes an enabled part at a point derived from its current mode, or resumes
    /// following that mode. For Entity mode the point is the target actor position;
    /// if unavailable, it falls back to the camera position.
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
                // Choose the fixed point for this mode.
                var freezePos = mode switch
                {
                    GazeTargetMode.Camera => _cameraService.GetCameraPosition(),
                    GazeTargetMode.Forward => ForwardPoint(actor),
                    // Keep this part's stored point when the shared anchor moves.
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

    /// <summary>
    /// Clears stored settings and queues native part releases. Keeps the entry until
    /// the hook can send those disable writes; clears the character target separately.
    /// </summary>
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
            // Keep the entry so the hook can read its pending disable writes.
            // Unlike toggling gaze off, Reset also forgets the actor target.
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
        // Notify only if the effective mode changed.
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
    /// Rebuilds every unlocked part target from the configured mode, including disabled
    /// parts. This method does not change locked targets.
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
        // Use the configured mode even when every part is disabled.
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
                // Stored world point; the hook may compensate for the authored pose.
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
    /// Starts a Position-mode target halfway between actor and camera, matching Ktisis.
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
