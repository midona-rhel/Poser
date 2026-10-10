using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Poser.Application.Viewport;
using Poser.Domain.Scene;
using Poser.Application.Lifecycle;
using static Poser.Game.GazeEntryStore;

namespace Poser.Game;

/// <summary>
/// The native half of gaze: the hooked actor look-at loop and the
/// <c>_updateLookAt</c> writes it makes for every entry, plus the GPose-range
/// gate every native gaze write passes. Runs on the game's thread; it reads
/// the entries only under the store's lock and makes its native calls outside
/// it.
/// </summary>
internal sealed unsafe class GazeNativeDriver
{
    // LookAt controller indices for _updateLookAt function
    private const uint LookAtIndex_Body = 0;
    private const uint LookAtIndex_Head = 1;
    private const uint LookAtIndex_Eyes = 2;

    private readonly IGPoseService _gPoseService;
    private readonly ICameraProjection _cameraService;
    private readonly IObjectTable _objectTable;
    private readonly IPluginLog _log;
    private readonly GazeEntryStore _store;
    private readonly Poser.Game.Posing.GazePoseFrames _gazeFrames;
    private readonly IGazeNativeFactory _nativeFactory;
    private readonly Func<bool> _isAvailable;
    private readonly Func<bool> _onOwnerThread;
    private delegate* unmanaged<CharacterLookAtController*, LookAtTarget*, uint, nint, void> _updateLookAt;
    private IGazeHook? _actorLookAtLoop;
    private bool _detourFaultLogged;

    public GazeNativeDriver(
        IGPoseService gPoseService,
        ICameraProjection cameraService,
        IObjectTable objectTable,
        IPluginLog log,
        GazeEntryStore store,
        Poser.Game.Posing.GazePoseFrames gazeFrames,
        IGazeNativeFactory nativeFactory,
        Func<bool> isAvailable,
        Func<bool> onOwnerThread)
    {
        _gPoseService = gPoseService;
        _cameraService = cameraService;
        _objectTable = objectTable;
        _log = log;
        _store = store;
        _gazeFrames = gazeFrames;
        _nativeFactory = nativeFactory;
        _isAvailable = isAvailable;
        _onOwnerThread = onOwnerThread;
    }

    /// <summary>Sets the scanned native <c>_updateLookAt</c> call.</summary>
    public void SetUpdateLookAt(nint address) =>
        _updateLookAt = (delegate* unmanaged<CharacterLookAtController*, LookAtTarget*, uint, nint, void>)address;

    /// <summary>The installed look-at loop hook; the owner publishes it here
    /// before enabling it, because the detour reads it at once.</summary>
    public IGazeHook? LoopHook
    {
        get => _actorLookAtLoop;
        set => _actorLookAtLoop = value;
    }

    /// <summary>
    /// Whether this object may receive a native gaze write at all. The GPose
    /// index range IS the gate: a GPose clone SHARES its GameObjectId with the
    /// overworld original, so an object outside 201..439 named by an id is the
    /// wrong body and writing to it lands on the real actor.
    /// </summary>
    internal static bool CanWriteCharacter([NotNullWhen(true)] IGameObject? character) =>
        character is { Address: not 0 }
        && character.IsValid()
        && character.ObjectIndex is >= 201 and <= 439;

    /// <summary>
    /// Computes the character-target-id write this transition owes, and books
    /// it as applied. Null when the native id already matches, when the caller
    /// is off the owner thread, or when the character is not writable — in each
    /// case nothing is booked, so a later transition still sees
    /// desired != applied and retries. Callers hold the store lock; the
    /// write itself happens outside it.
    /// </summary>
    public ulong? PendingTargetWrite(GazeEntry entry, bool writable)
    {
        if (!writable || !_onOwnerThread())
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
    public void WriteCharacterTarget(IGameObject? character, ulong? pending)
    {
        if (pending is not { } targetId || !CanWriteCharacter(character))
            return;
        _nativeFactory.SetCharacterTargetId(character.Address, targetId);
    }

    internal nint ActorLookAtDetour(ContainerInterface* args)
    {
        // Never throw into the game's look-at loop: a fault skips this pass's
        // writes, is logged once, and the game's own loop still runs.
        try
        {
            if (_isAvailable())
                DriveLookAt(args);
        }
        catch (Exception ex)
        {
            if (!_detourFaultLogged)
            {
                _detourFaultLogged = true;
                _log.Error($"GazeService: look-at detour faulted (logged once): {ex}");
            }
        }
        // This advances native gaze inputs. Pose evaluation happens later,
        // before BonePosingService applies the authored transforms.
        return _actorLookAtLoop!.Original(args);
    }

    /// <summary>The detour's own writes, before the game's loop runs.</summary>
    private void DriveLookAt(ContainerInterface* args)
    {
        if (_gPoseService.IsGPosing)
        {
            bool any;
            lock (_store.Sync)
            {
                any = _store.Count > 0;
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
                    bool poseAware = false;
                    bool eyesLocked = false, headLocked = false, bodyLocked = false;
                    lock (_store.Sync)
                    {
                        if (_store.TryGetByAddress(targetActor.Address, out var entry))
                        {
                            known = true;
                            mode = EffectiveMode(entry);
                            poseAware = entry.PoseAware;
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
                        return;

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
                        lock (_store.Sync)
                        {
                            // Only what this pass delivered is settled; a debt
                            // booked meanwhile is still owed.
                            if (_store.TryGetByAddress(targetActor.Address, out var entry))
                                entry.PendingRelease &= ~pendingRelease;
                        }
                    }

                    bool compensate = poseAware && mode is GazeTargetMode.Camera or GazeTargetMode.Position;
                    _gazeFrames.Request(targetActor.Address, compensate);
                    // Off performs no further write: every channel has been
                    // handed back and the game's own update owns them again.
                    if (mode == GazeTargetMode.None)
                        return;

                    if (mode == GazeTargetMode.Detached)
                    {
                        // The game's loop re-aims at the camera each frame;
                        // "no target" written on every part right before it
                        // is what keeps the parts on the animation.
                        var none = new LookAtTarget { LookMode = LookMode.None };
                        _updateLookAt(lookAtController, &none, LookAtIndex_Body, 0);
                        _updateLookAt(lookAtController, &none, LookAtIndex_Head, 0);
                        _updateLookAt(lookAtController, &none, LookAtIndex_Eyes, 0);
                        return;
                    }

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

                    if (compensate)
                    {
                        lookAt.Body.LookAtTarget.Position = _gazeFrames.Convert(targetActor.Address,
                            GazeTargetType.Body, lookAt.Body.LookAtTarget.Position);
                        lookAt.Head.LookAtTarget.Position = _gazeFrames.Convert(targetActor.Address,
                            GazeTargetType.Head, lookAt.Head.LookAtTarget.Position);
                        lookAt.Eyes.LookAtTarget.Position = _gazeFrames.Convert(targetActor.Address,
                            GazeTargetType.Eyes, lookAt.Eyes.LookAtTarget.Position);
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
    }

    public void DisposeHook()
    {
        _actorLookAtLoop?.Dispose();
        _actorLookAtLoop = null;
    }
}
