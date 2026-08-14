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

    /// <summary>
    /// Writes the CHARACTER's own game target id (Brio ActorLookAtService
    /// SetActorTarget, `actor.Native()-&gt;SetTargetId(targetActorID)`). Behind
    /// the factory because it is a native member call on a live character.
    /// </summary>
    void SetCharacterTargetId(nint characterAddress, ulong targetId);
}

internal sealed class GazeNativeFactory : IGazeNativeFactory
{
    public unsafe void SetCharacterTargetId(nint characterAddress, ulong targetId) =>
        ((Character*)characterAddress)->SetTargetId(targetId);

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
    private readonly IFramework? _framework;
    private readonly IGazeNativeFactory _nativeFactory;

    /// <summary>Spawn/discovery-standard thread refusal (ActorSpawnService
    /// shape) for the members that write natively outside the hooked loop.</summary>
    private bool OnOwnerThread => _framework is null || _framework.IsInFrameworkUpdateThread;

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
        /// <summary>The CONFIGURED mode. Remembered across a full untoggle:
        /// Brio's SetTargetType only rewrites the participation mask and never
        /// touches TargetMode, so re-adding a part resumes the same mode.</summary>
        public GazeTargetMode Mode;
        public GazeTargetType Parts = GazeTargetType.All;

        /// <summary>The remembered Entity target GameObjectId; 0 = never
        /// chosen. Surviving a full untoggle is the point — it is cleared only
        /// by <see cref="ResetGaze"/>, which is Brio's RemoveObjectFromLook.</summary>
        public ulong TargetId;

        /// <summary>The remembered target is no longer in the object table.
        /// Exact identity, never an address: the id stays so the refusal can
        /// name it, and reapplying it is refused rather than followed.</summary>
        public bool TargetStale;

        /// <summary>The character target id Poser last wrote natively; 0 when
        /// Poser has written none. Poser only ever clears what it set.</summary>
        public ulong AppliedTargetId;

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
        IPluginLog log,
        IFramework framework)
        : this(
            gPoseService,
            cameraService,
            objectTable,
            eventBus,
            sigScanner,
            hooks,
            log,
            framework,
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
        IFramework? framework,
        IGazeNativeFactory nativeFactory)
    {
        _gPoseService = gPoseService;
        _cameraService = cameraService;
        _objectTable = objectTable;
        _eventBus = eventBus;
        _log = log;
        _framework = framework;
        _nativeFactory = nativeFactory;

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

    /// <summary>
    /// The mode the entry's stored per-part sources are SEEDED from. Entity
    /// without a usable target seeds nothing; the participation mask is
    /// deliberately not consulted, so untoggling every part leaves the stored
    /// positions and target id exactly as they were (Brio's SetTargetType
    /// rewrites the mask and nothing else).
    /// </summary>
    private static GazeTargetMode SeedMode(GazeEntry entry) =>
        entry.Mode == GazeTargetMode.Entity && (entry.TargetId == 0 || entry.TargetStale)
            ? GazeTargetMode.None
            : entry.Mode;

    /// <summary>
    /// What the detour actually enforces. No participating part means Poser
    /// writes nothing at all and the game's own look-at loop owns every
    /// channel — release is cessation, exactly as in Brio, where a channel
    /// outside the mask gets no _updateLookAt call and the original loop runs
    /// unconditionally afterwards.
    /// </summary>
    private static GazeTargetMode EffectiveMode(GazeEntry entry) =>
        entry.Parts == GazeTargetType.None
            ? GazeTargetMode.None
            : SeedMode(entry);

    /// <summary>
    /// The channels the detour will enforce for this actor on its next pass.
    /// Everything absent is handed back to the game. This is the observable
    /// form of the release contract, so it is what the tests assert.
    /// </summary>
    internal GazeTargetType WrittenParts(ulong gameObjectId)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(gameObjectId, out var entry))
                return GazeTargetType.None;
            return EffectiveMode(entry) == GazeTargetMode.None
                ? GazeTargetType.None
                : entry.Parts;
        }
    }

    /// <summary>
    /// Computes the character-target-id write this transition owes, and books
    /// it as applied. Null when the native id already matches. Callers hold
    /// <see cref="_sync"/>; the write itself happens outside it.
    /// </summary>
    private ulong? PendingTargetWrite(GazeEntry entry)
    {
        // Off-thread transitions book nothing, so the next on-thread transition
        // still sees desired != applied and performs the write.
        if (!OnOwnerThread)
            return null;
        var desired = EffectiveMode(entry) == GazeTargetMode.Entity ? entry.TargetId : 0ul;
        if (desired == entry.AppliedTargetId)
            return null;
        entry.AppliedTargetId = desired;
        return desired;
    }

    /// <summary>
    /// Keeps the character's own game target id in step with the effective
    /// Entity target. Brio drives this BOTH ways — set when an actor is picked,
    /// and written back to 0 by its "Reset Selected Actor" path — and the clear
    /// is what actually hands the channel back: an imposed target left behind
    /// keeps the game's own look-at pointing at it.
    /// </summary>
    private void WriteCharacterTarget(nint characterAddress, ulong? pending)
    {
        if (pending is not { } targetId || characterAddress == nint.Zero)
            return;
        _nativeFactory.SetCharacterTargetId(characterAddress, targetId);
    }

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

    private GazeResult Unavailable() =>
        GazeResult.Refused(UnavailableDetail ?? "Gaze capability unavailable.");

    /// <summary>The remembered target is gone, so reapplying it is refused by
    /// name instead of quietly following nothing or a reused address.</summary>
    private static GazeResult StaleRefusal(GazeEntry entry) => GazeResult.Refused(
        $"The remembered gaze target ({entry.TargetId:X}) has left the scene. Choose another actor.");

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
                    Active = EffectiveMode(entry) != GazeTargetMode.None,
                    TargetStale = entry.TargetStale,
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

    public GazeResult SetGazeMode(IActor actor, GazeTargetMode mode)
    {
        if (!IsAvailable)
            return Unavailable();
        if (Resolve(actor) is not { } gameObject)
            return GazeResult.Refused("This actor is no longer resolvable.");
        bool modeChanged;
        ulong? pendingTarget;
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            // Re-selecting Actor mode is a reapply of the remembered target, so
            // a stale one is refused here rather than silently doing nothing.
            if (mode == GazeTargetMode.Entity && entry.TargetId != 0 && entry.TargetStale)
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
            modeChanged = EffectiveMode(entry) != beforeMode;
            pendingTarget = PendingTargetWrite(entry);
        }
        // Leaving Entity clears the character's imposed target id, so the
        // game's own look-at stops pointing at the actor Poser chose.
        WriteCharacterTarget(gameObject.Address, pendingTarget);
        // Published outside the lock — the detour contends on _sync from the
        // native thread, so the bus is never invoked while holding it.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
        return GazeResult.Ok();
    }

    public GazeResult SetGazeParts(IActor actor, GazeTargetType parts)
    {
        if (!IsAvailable)
            return Unavailable();
        if (Resolve(actor) is not { } gameObject)
            return GazeResult.Refused("This actor is no longer resolvable.");
        bool modeChanged;
        ulong? pendingTarget;
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            // Adding a part back is a reapply of the remembered configuration.
            // Relinquishing one never is, so only additions can be refused.
            if ((parts & ~entry.Parts) != GazeTargetType.None
                && entry.Mode == GazeTargetMode.Entity
                && entry.TargetId != 0
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
            modeChanged = EffectiveMode(entry) != beforeMode;
            pendingTarget = PendingTargetWrite(entry);
        }
        // All-off drops the character's imposed target id; the first part back
        // reapplies it, which is what makes retoggling resume tracking.
        WriteCharacterTarget(gameObject.Address, pendingTarget);
        // Crossing between "some part enforced" and "none" is the transition;
        // part edits that leave that alone stay silent. Published outside lock.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
        return GazeResult.Ok();
    }

    public GazeResult SetGazeTarget(IActor actor, IActor target)
    {
        if (!IsAvailable)
            return Unavailable();
        if (!OnOwnerThread)
            return GazeResult.Refused("Gaze targets can only be set on the game thread.");
        if (Resolve(actor) is not { } gameObject || Resolve(target) is not { } targetObject)
            return GazeResult.Refused("This actor is no longer resolvable.");
        // The GPose index gate is load-bearing here exactly as in the detour
        // (Brio ActorTableHelpers 201..439): a GPose clone SHARES its
        // GameObjectId with the overworld original, so a stale wrapper naming
        // an overworld body would write the target id onto the real actor.
        if (!gameObject.IsValid() || gameObject.ObjectIndex is not (>= 201 and <= 439))
            return GazeResult.Refused("Only a GPose actor can be given a gaze target.");
        if (gameObject.GameObjectId == targetObject.GameObjectId)
        {
            _log.Warning("GazeService: an actor cannot gaze at itself.");
            return GazeResult.Refused("An actor cannot gaze at itself.");
        }
        bool modeChanged;
        ulong? pendingTarget;
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            var beforeMode = EffectiveMode(entry);
            entry.TargetId = targetObject.GameObjectId;
            // A freshly chosen target is live by construction, so this is the
            // one place the stale mark is lifted.
            entry.TargetStale = false;
            entry.Mode = GazeTargetMode.Entity;
            if (entry.Parts == GazeTargetType.None)
                entry.Parts = GazeTargetType.All;
            ReseedUnlockedParts(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
            pendingTarget = PendingTargetWrite(entry);
        }
        // Brio parity (SetActorTarget): the character's own target id backs
        // the game's id-based look tracking. Written through the RESOLVED
        // wrapper's address — the raw IActor address is only a claim.
        WriteCharacterTarget(gameObject.Address, pendingTarget);
        // Retargeting within Entity mode is not a mode transition; only the
        // move INTO Entity publishes. Published outside the lock.
        if (modeChanged)
            _eventBus.Publish(new GazeStateChangedEvent());
        return GazeResult.Ok();
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
                SeedMode(entry) != GazeTargetMode.Position)
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
                SeedMode(entry) != GazeTargetMode.Position)
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
        ulong? pendingTarget = null;
        lock (_sync)
        {
            // Release is cessation: dropping the entry stops every write in
            // the same transition. This is Brio's RemoveObjectFromLook — the
            // ONE path that forgets the remembered target, as opposed to the
            // toggles, which keep it.
            bool known = _entries.TryGetValue(gameObject.GameObjectId, out var entry);
            modeChanged = known && EffectiveMode(entry!) != GazeTargetMode.None;
            if (known && entry!.AppliedTargetId != 0 && OnOwnerThread)
                pendingTarget = 0;
            _entries.Remove(gameObject.GameObjectId);
        }
        WriteCharacterTarget(gameObject.Address, pendingTarget);
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
        List<(nint Address, ulong TargetId)>? targetWrites = null;
        lock (_sync)
        {
            if (_entries.Count > 0)
            {
                List<ulong>? removed = null;
                foreach (var (id, entry) in _entries)
                {
                    var source = _objectTable.SearchById(id);
                    if (source == null)
                    {
                        (removed ??= new List<ulong>()).Add(id);
                        continue;
                    }
                    bool wasStale = entry.TargetStale;
                    // Exact identity: the remembered id is KEPT and marked
                    // stale, never zeroed and never re-resolved by address. A
                    // stale target enforces nothing, and reapplying it is
                    // refused by name rather than followed.
                    entry.TargetStale = entry.TargetId != 0 &&
                        _objectTable.SearchById(entry.TargetId) == null;
                    if (entry.TargetStale == wasStale)
                        continue;
                    if (entry.TargetStale)
                    {
                        ClearPartLock(entry, GazeTargetType.All);
                        _log.Debug(
                            $"GazeService: gaze target of {id} despawned — remembered as stale.");
                    }
                    if (entry.Mode != GazeTargetMode.Entity)
                        continue;
                    modeChanged = true;
                    if (PendingTargetWrite(entry) is { } pending)
                        (targetWrites ??= new()).Add((source.Address, pending));
                }
                if (removed != null)
                    foreach (var id in removed)
                        _entries.Remove(id);
            }
        }
        // Outside the lock: a despawned target leaves the character's imposed
        // target id pointing at nothing, so it is cleared here too.
        if (targetWrites != null)
            foreach (var (address, targetId) in targetWrites)
                WriteCharacterTarget(address, targetId);
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
