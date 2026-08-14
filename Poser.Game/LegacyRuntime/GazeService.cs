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

        /// <summary>Channels Poser currently claims — the set the detour is
        /// enforcing. Only a claimed channel can be owed a hand-back.</summary>
        public GazeTargetType ClaimedParts;

        /// <summary>Channels owed ONE disable write. Booked by the transition
        /// that dropped them and delivered by the detour on the native
        /// thread, which is the only place _updateLookAt may be called.</summary>
        public GazeTargetType PendingRelease;
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
    /// writes nothing at all — a channel outside the mask gets no
    /// _updateLookAt call, exactly as in Brio, where the original loop then
    /// runs unconditionally.
    /// </summary>
    private static GazeTargetMode EffectiveMode(GazeEntry entry) =>
        entry.Parts == GazeTargetType.None
            ? GazeTargetMode.None
            : SeedMode(entry);

    /// <summary>The channels the detour will enforce on its next pass.</summary>
    private static GazeTargetType EnforcedParts(GazeEntry entry) =>
        EffectiveMode(entry) == GazeTargetMode.None
            ? GazeTargetType.None
            : entry.Parts;

    /// <summary>
    /// The channels the detour will enforce for this actor on its next pass.
    /// Everything absent is handed back to the game. This is the observable
    /// form of the release contract, so it is what the tests assert.
    /// </summary>
    internal GazeTargetType WrittenParts(ulong gameObjectId)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(gameObjectId, out var entry)
                ? EnforcedParts(entry)
                : GazeTargetType.None;
        }
    }

    /// <summary>
    /// The channels owed a one-shot hand-back on the detour's next pass. The
    /// other half of the release contract, and likewise what the tests assert.
    /// </summary>
    internal GazeTargetType PendingRelease(ulong gameObjectId)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(gameObjectId, out var entry)
                ? entry.PendingRelease
                : GazeTargetType.None;
        }
    }

    /// <summary>
    /// Books the hand-back this transition owes. Ceasing to write a channel is
    /// NOT a release: _updateLookAt copies into the controller's persistent
    /// per-channel slot (Ktisis names the same native call
    /// <c>ActorLookAt(ActorGaze* writeTo, Gaze* readFrom, GazeControl part)</c>
    /// — Scene/Modules/Actors/ActorModule.cs:231), so a channel Poser stops
    /// writing keeps aiming at the last target it was given. Each dropped
    /// channel is therefore owed exactly one INACTIVE write; Brio spells that
    /// released value out in StopLookAt as LookMode.None on every part
    /// (Brio/Game/Actor/ActorLookAtService.cs:101-108) and Ktisis calls the
    /// same value GazeMode.Disabled (Ktisis/Structs/Actors/ActorGaze.cs:75).
    /// A channel that comes straight back cancels its debt, because the active
    /// write supersedes the disable. Callers hold <see cref="_sync"/>.
    /// </summary>
    private static void BookRelease(GazeEntry entry)
    {
        var enforced = EnforcedParts(entry);
        entry.PendingRelease =
            (entry.PendingRelease | (entry.ClaimedParts & ~enforced)) & ~enforced;
        entry.ClaimedParts = enforced;
    }

    /// <summary>
    /// Whether this object may receive a native gaze write at all. The GPose
    /// index range IS the gate: a GPose clone SHARES its GameObjectId with the
    /// overworld original, so an object outside 201..439 named by an id is the
    /// wrong body and writing to it lands on the real actor.
    /// </summary>
    private static bool CanWriteCharacter([NotNullWhen(true)] IGameObject? character) =>
        character is { Address: not 0 }
        && character.IsValid()
        && character.ObjectIndex is >= 201 and <= 439;

    /// <summary>
    /// The GPose clones carrying <paramref name="wanted"/>, found in ONE walk
    /// of the GPose range rather than a walk per id.
    /// <c>IObjectTable.SearchById</c> scans from index 0 and therefore answers
    /// with the overworld original for any actor that exists in both places —
    /// correct for an existence probe, wrong for a write address, so this walk
    /// is the only sound way to resolve a writable body by id.
    /// </summary>
    private Dictionary<ulong, (IGameObject Clone, bool Writable)> ResolveGPoseClones(
        HashSet<ulong> wanted)
    {
        var found = new Dictionary<ulong, (IGameObject, bool)>(wanted.Count);
        for (int index = 201; index <= 439 && found.Count < wanted.Count; index++)
            if (_objectTable[index] is { } candidate
                && wanted.Contains(candidate.GameObjectId)
                && !found.ContainsKey(candidate.GameObjectId))
                found[candidate.GameObjectId] = (candidate, CanWriteCharacter(candidate));
        return found;
    }

    /// <summary>
    /// Computes the character-target-id write this transition owes, and books
    /// it as applied. Null when the native id already matches, when the caller
    /// is off the owner thread, or when the character is not writable — in each
    /// case nothing is booked, so a later transition still sees
    /// desired != applied and retries. Callers hold <see cref="_sync"/>; the
    /// write itself happens outside it.
    /// </summary>
    private ulong? PendingTargetWrite(GazeEntry entry, bool writable)
    {
        if (!writable || !OnOwnerThread)
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
    ///
    /// This is the ONE gate site for the native call. Every caller funnels
    /// through here precisely so the GPose-index gate cannot be skipped by
    /// adding another one.
    /// </summary>
    private void WriteCharacterTarget(IGameObject? character, ulong? pending)
    {
        if (pending is not { } targetId || !CanWriteCharacter(character))
            return;
        _nativeFactory.SetCharacterTargetId(character.Address, targetId);
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
                // Same predicate as every other native gaze write, so the gate
                // is spelled exactly once in this file (Brio ActorTableHelpers
                // 201..439).
                if (CanWriteCharacter(targetActor))
                {
                    GazeTargetMode mode = GazeTargetMode.None;
                    GazeTargetType parts = GazeTargetType.None;
                    GazeTargetType pendingRelease = GazeTargetType.None;
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
                            pendingRelease = entry.PendingRelease;
                            // Copy to locals (like Brio) — the native calls
                            // below run outside the lock.
                            lookAt = entry.Target;
                            eyesLocked = entry.EyesLocked;
                            headLocked = entry.HeadLocked;
                            bodyLocked = entry.BodyLocked;
                        }
                    }

                    // An actor Poser has never touched is the game's alone.
                    if (!known)
                        return _actorLookAtLoop!.Original(args);

                    var lookAtController =
                        &((Character*)targetActor.Address)->LookAt.Controller;

                    // The hand-back: one INACTIVE write per released channel,
                    // on the native thread, before this pass's own writes.
                    // Without it the controller keeps the last target Poser
                    // gave the channel and the actor stays frozen mid-gaze.
                    if (pendingRelease != GazeTargetType.None)
                    {
                        var release = new LookAtTarget { LookMode = LookMode.None };
                        if (pendingRelease.HasFlag(GazeTargetType.Body))
                            _updateLookAt(lookAtController, &release, LookAtIndex_Body, 0);
                        if (pendingRelease.HasFlag(GazeTargetType.Head))
                            _updateLookAt(lookAtController, &release, LookAtIndex_Head, 0);
                        if (pendingRelease.HasFlag(GazeTargetType.Eyes))
                            _updateLookAt(lookAtController, &release, LookAtIndex_Eyes, 0);
                        lock (_sync)
                        {
                            // Only what this pass delivered is settled; a debt
                            // booked meanwhile is still owed.
                            if (_entries.TryGetValue(targetActor.GameObjectId, out var entry))
                                entry.PendingRelease &= ~pendingRelease;
                        }
                    }

                    // Off performs no further write: every channel has been
                    // handed back and the game's own update owns them again.
                    if (mode == GazeTargetMode.None)
                        return _actorLookAtLoop!.Original(args);

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
        // Resolved before the lock: the gate reads Dalamud wrapper properties,
        // and the detour contends on _sync from the native thread.
        bool writable = CanWriteCharacter(gameObject);
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
            BookRelease(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
            pendingTarget = PendingTargetWrite(entry, writable);
        }
        // Leaving Entity clears the character's imposed target id, so the
        // game's own look-at stops pointing at the actor Poser chose.
        WriteCharacterTarget(gameObject, pendingTarget);
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
        bool writable = CanWriteCharacter(gameObject);
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
            // The untoggled channel is owed its hand-back here: the remembered
            // mode and target survive, but the controller must stop aiming the
            // channel Poser just gave up.
            BookRelease(entry);
            modeChanged = EffectiveMode(entry) != beforeMode;
            pendingTarget = PendingTargetWrite(entry, writable);
        }
        // All-off drops the character's imposed target id; the first part back
        // reapplies it, which is what makes retoggling resume tracking.
        WriteCharacterTarget(gameObject, pendingTarget);
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
        // The same predicate the write funnel enforces, spelled once, so this
        // refusal can never drift from what WriteCharacterTarget will accept.
        // It is stated here as well only to name the reason for the user.
        if (!CanWriteCharacter(gameObject))
            return GazeResult.Refused("Only a GPose actor can be given a gaze target.");
        if (gameObject.GameObjectId == targetObject.GameObjectId)
        {
            _log.Warning("GazeService: an actor cannot gaze at itself.");
            return GazeResult.Refused("An actor cannot gaze at itself.");
        }
        bool modeChanged;
        ulong? pendingTarget;
        bool writable = CanWriteCharacter(gameObject);
        lock (_sync)
        {
            var entry = GetOrCreateEntry(gameObject.GameObjectId);
            var beforeMode = EffectiveMode(entry);
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
            pendingTarget = PendingTargetWrite(entry, writable);
        }
        // Brio parity (SetActorTarget): the character's own target id backs
        // the game's id-based look tracking. Written through the RESOLVED
        // wrapper's address — the raw IActor address is only a claim.
        WriteCharacterTarget(gameObject, pendingTarget);
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
            // Brio's RemoveObjectFromLook — the ONE path that forgets the
            // remembered target, as opposed to the toggles, which keep it. The
            // entry itself is cleared in place rather than dropped: it is the
            // ledger the detour reads to deliver the hand-back, and dropping it
            // would strand every claimed channel at its last gaze.
            if (!_entries.TryGetValue(gameObject.GameObjectId, out var entry))
                return;
            modeChanged = EffectiveMode(entry) != GazeTargetMode.None;
            if (entry.AppliedTargetId != 0 && OnOwnerThread)
            {
                pendingTarget = 0;
                entry.AppliedTargetId = 0;
            }
            entry.Mode = GazeTargetMode.None;
            entry.Parts = GazeTargetType.All;
            entry.TargetId = 0;
            entry.TargetStale = false;
            entry.Position = default;
            ClearPartLock(entry, GazeTargetType.All);
            ReseedUnlockedParts(entry);
            BookRelease(entry);
        }
        WriteCharacterTarget(gameObject, pendingTarget);
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
        List<(ulong Id, ulong TargetId)> snapshot;
        lock (_sync)
        {
            if (_entries.Count == 0)
                return;
            snapshot = new List<(ulong, ulong)>(_entries.Count);
            foreach (var (id, entry) in _entries)
                snapshot.Add((id, entry.TargetId));
        }

        // Every object-table read happens OUTSIDE _sync: the clone scan walks
        // the GPose range, and the detour contends on _sync from the native
        // thread on every frame.

        // The SOURCES must resolve to their own GPose clones — a clone is the
        // only body Poser may write, and SearchById would answer with the
        // overworld original that shares the id. One walk serves the whole
        // snapshot.
        var wanted = new HashSet<ulong>();
        foreach (var (id, _) in snapshot)
            wanted.Add(id);
        var clones = ResolveGPoseClones(wanted);

        // The TARGET question is existence, not an address: the game resolves
        // an imposed id against the whole table, so SearchById is the right
        // probe here and its answer is never written to.
        var liveTargets = new HashSet<ulong>();
        foreach (var (_, targetId) in snapshot)
            if (targetId != 0 && _objectTable.SearchById(targetId) != null)
                liveTargets.Add(targetId);

        bool modeChanged = false;
        List<(IGameObject Character, ulong TargetId)>? targetWrites = null;
        lock (_sync)
        {
            foreach (var (id, probedTargetId) in snapshot)
            {
                if (!_entries.TryGetValue(id, out var entry))
                    continue;
                if (!clones.TryGetValue(id, out var clone))
                {
                    // No GPose clone carries this id any more: the body the
                    // entry described is gone, so the entry goes with it.
                    _entries.Remove(id);
                    continue;
                }
                // The liveness probe answered about the target this entry held
                // when the snapshot was taken. An entry retargeted since is
                // left alone rather than judged on the wrong id — the retarget
                // proved its own target live and cleared the mark itself.
                if (entry.TargetId != probedTargetId)
                    continue;
                bool wasStale = entry.TargetStale;
                // Exact identity, and STICKY: once a remembered target has left
                // the scene the mark stays until a live target is chosen. An id
                // reappearing is not treated as consent to resume imposing it.
                entry.TargetStale = wasStale ||
                    (entry.TargetId != 0 && !liveTargets.Contains(entry.TargetId));
                if (entry.TargetStale == wasStale)
                    continue;
                // Entity-only from here: a stale target is meaningless to a
                // Point/Camera/Forward entry, and must not touch its locks.
                if (entry.Mode != GazeTargetMode.Entity)
                    continue;
                ClearPartLock(entry, GazeTargetType.All);
                // A stale target stops enforcement, so every claimed channel
                // is owed its hand-back — the same debt an untoggle books.
                BookRelease(entry);
                _log.Debug(
                    $"GazeService: gaze target of {id} despawned — remembered as stale.");
                modeChanged = true;
                if (PendingTargetWrite(entry, clone.Writable) is { } pending)
                    (targetWrites ??= new()).Add((clone.Clone, pending));
            }
        }
        // Outside the lock: a despawned target leaves the character's imposed
        // target id pointing at nothing, so it is cleared here too — on the
        // clone, through the same gated funnel as every other write.
        if (targetWrites != null)
            foreach (var (character, targetId) in targetWrites)
                WriteCharacterTarget(character, targetId);
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
