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
using Poser.Game.Core;
using static Poser.Game.GazeEntryStore;

namespace Poser.Game;

/// <summary>
/// Applies stored gaze settings before the native look-at loop. Copies entries under
/// the store lock, then performs native calls after releasing it.
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
    /// Accepts live objects in the GPose actor slots. GPose copies can share GameObjectId
    /// with the original actor, so that ID alone cannot restrict writes to a GPose copy.
    /// </summary>
    internal static bool CanWriteCharacter([NotNullWhen(true)] IGameObject? character) =>
        character is { Address: not 0 }
        && character.IsValid()
        && GPoseObjectTable.IsActorIndex(character.ObjectIndex);

    /// <summary>
    /// Selects a changed target ID and records it before the native write. Returns null
    /// when the source cannot be written or the caller is off the framework thread.
    /// Callers hold the store lock; WriteCharacterTarget performs the later write.
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
    /// Writes or clears the character target ID after checking the live GPose object.
    /// A cleared ID stops the native controller following the previously selected actor.
    /// </summary>
    public void WriteCharacterTarget(IGameObject? character, ulong? pending)
    {
        if (pending is not { } targetId || !CanWriteCharacter(character))
            return;
        _nativeFactory.SetCharacterTargetId(character.Address, targetId);
    }

    internal nint ActorLookAtDetour(ContainerInterface* args)
    {
        // Catch managed failures in our update and log the first one. Writes already
        // made are not rolled back; the original loop runs afterwards.
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
                // Restrict this update to live GPose actor objects.
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
                            // Native calls below use this snapshot outside the lock.
                            lookAt = entry.Target;
                            eyesLocked = entry.EyesLocked;
                            headLocked = entry.HeadLocked;
                            bodyLocked = entry.BodyLocked;
                        }
                    }

                    // Leave actors without a Poser gaze entry unchanged.
                    if (!known)
                        return;

                    var lookAtController =
                        &((Character*)targetActor.Address)->LookAt.Controller;

                    // Disable released parts before applying active targets. Otherwise the
                    // native controller retains the last target written for each part.
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
                            // Clear the part bits written by this pass.
                            if (_store.TryGetByAddress(targetActor.Address, out var entry))
                                entry.PendingRelease &= ~pendingRelease;
                        }
                    }

                    bool compensate = poseAware && mode is GazeTargetMode.Camera or GazeTargetMode.Position;
                    _gazeFrames.Request(targetActor.Address, compensate);
                    // Disabled mode has no active target writes.
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

                    // Refresh moving position targets. Entity uses a native object ID;
                    // Position uses the stored point, with optional pose compensation.
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
