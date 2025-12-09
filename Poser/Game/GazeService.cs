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
/// Service for controlling actor gaze (where they look).
/// Based on Brio's ActorLookAtService implementation.
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

    private readonly Dictionary<IActor, GazeState> _gazeStates = new();
    private readonly Dictionary<ulong, LookAtDataHolder> _lookAtHandles = new();

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
    }

    private nint ActorLookAtDetour(ContainerInterface* args)
    {
        if (_gPoseService.IsGPosing)
        {
            var targetActor = _objectTable.CreateObjectReference((nint)args->OwnerObject);
            if (targetActor is not null && targetActor.IsValid()
                && _lookAtHandles.TryGetValue(targetActor.GameObjectId, out var lookAtDataHolder))
            {
                // Skip processing if gaze mode is None - let game handle normally
                if (lookAtDataHolder.TargetMode == GazeTargetMode.None)
                {
                    return _actorLookAtLoop.Original(args);
                }

                // Copy to local variable (like Brio does) to avoid issues with managed memory
                LookAtSource lookAt = lookAtDataHolder.Target;

                // Update target positions based on mode (only for unlocked parts)
                if (lookAtDataHolder.TargetMode == GazeTargetMode.Camera)
                {
                    var cameraPos = _cameraService.GetCameraPosition();
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Eyes) && !lookAtDataHolder.EyesLocked)
                        lookAt.Eyes.LookAtTarget.Position = cameraPos;
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Head) && !lookAtDataHolder.HeadLocked)
                        lookAt.Head.LookAtTarget.Position = cameraPos;
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Body) && !lookAtDataHolder.BodyLocked)
                        lookAt.Body.LookAtTarget.Position = cameraPos;
                }
                else if (lookAtDataHolder.TargetMode == GazeTargetMode.Entity && lookAtDataHolder.TargetEntityAddress != nint.Zero)
                {
                    var targetPos = GetEntityPosition(lookAtDataHolder.TargetEntityAddress);
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Eyes) && !lookAtDataHolder.EyesLocked)
                        lookAt.Eyes.LookAtTarget.Position = targetPos;
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Head) && !lookAtDataHolder.HeadLocked)
                        lookAt.Head.LookAtTarget.Position = targetPos;
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Body) && !lookAtDataHolder.BodyLocked)
                        lookAt.Body.LookAtTarget.Position = targetPos;
                }
                else if (lookAtDataHolder.TargetMode == GazeTargetMode.Forward)
                {
                    // Forward mode - calculate a position in front of the character
                    var nativeObj = (GameObject*)targetActor.Address;
                    var position = new Vector3(nativeObj->Position.X, nativeObj->Position.Y, nativeObj->Position.Z);
                    var rotation = nativeObj->Rotation;

                    var forwardDir = new Vector3(
                        MathF.Sin(rotation),
                        0f,
                        MathF.Cos(rotation)
                    );

                    var forwardPos = position + forwardDir * 10f + new Vector3(0, 1.5f, 0);
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Eyes) && !lookAtDataHolder.EyesLocked)
                        lookAt.Eyes.LookAtTarget.Position = forwardPos;
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Head) && !lookAtDataHolder.HeadLocked)
                        lookAt.Head.LookAtTarget.Position = forwardPos;
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Body) && !lookAtDataHolder.BodyLocked)
                        lookAt.Body.LookAtTarget.Position = forwardPos;
                }

                // Apply the look-at updates for parts that are enabled in TargetType
                var lookAtController = &((Character*)targetActor.Address)->LookAt.Controller;

                if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Body))
                    _updateLookAt(lookAtController, &lookAt.Body.LookAtTarget, LookAtIndex_Body, 0);
                if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Head))
                    _updateLookAt(lookAtController, &lookAt.Head.LookAtTarget, LookAtIndex_Head, 0);
                if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Eyes))
                    _updateLookAt(lookAtController, &lookAt.Eyes.LookAtTarget, LookAtIndex_Eyes, 0);
            }
        }

        // Call original - this runs gaze IK and modifies bones
        // Note: In Brio, gaze and bone posing are mutually exclusive for gaze bones
        // We don't re-apply bone transforms here - if user wants to pose gaze bones,
        // they should disable gaze first
        return _actorLookAtLoop.Original(args);
    }

    private Vector3 GetEntityPosition(nint address)
    {
        if (address == nint.Zero)
            return Vector3.Zero;

        var gameObject = (GameObject*)address;
        return gameObject->Position;
    }

    public GazeState GetGazeState(IActor actor)
    {
        if (!_gazeStates.TryGetValue(actor, out var state))
        {
            state = new GazeState();
            _gazeStates[actor] = state;
        }
        return state;
    }

    public void SetGazeMode(IActor actor, GazeTargetMode mode)
    {
        var state = GetGazeState(actor);
        state.Mode = mode;
        ApplyGaze(actor, state);
    }

    public void SetGazeTargetType(IActor actor, GazeTargetType targetType)
    {
        var state = GetGazeState(actor);
        state.TargetType = targetType;
        ApplyGaze(actor, state);
    }

    public void SetGazeTarget(IActor actor, IActor target)
    {
        var state = GetGazeState(actor);
        state.TargetEntity = target;
        state.Mode = GazeTargetMode.Entity;
        ApplyGaze(actor, state);
    }

    public void ResetGaze(IActor actor)
    {
        _gazeStates.Remove(actor);
        if (actor.Address != nint.Zero)
        {
            var gameObject = _objectTable.CreateObjectReference(actor.Address);
            if (gameObject != null)
            {
                _lookAtHandles.Remove(gameObject.GameObjectId);
            }
        }
    }

    public void SetGazeState(IActor actor, GazeState state)
    {
        // Clone the state so we own it
        var clonedState = state.Clone();
        _gazeStates[actor] = clonedState;
        ApplyGaze(actor, clonedState);
    }

    private void ApplyGaze(IActor actor, GazeState state)
    {
        if (actor.Address == nint.Zero)
            return;

        var gameObject = _objectTable.CreateObjectReference(actor.Address);
        if (gameObject == null)
            return;

        if (!_lookAtHandles.TryGetValue(gameObject.GameObjectId, out var holder))
        {
            holder = new LookAtDataHolder();
            _lookAtHandles[gameObject.GameObjectId] = holder;
        }

        holder.TargetMode = state.Mode;
        holder.TargetType = state.TargetType;
        holder.TargetEntityAddress = state.TargetEntity?.Address ?? nint.Zero;

        // Initialize look-at targets
        var cameraPos = _cameraService.GetCameraPosition();
        holder.Target = new LookAtSource
        {
            Body = new LookAtType { LookAtTarget = new LookAtTarget { LookMode = LookMode.Position, Position = cameraPos } },
            Head = new LookAtType { LookAtTarget = new LookAtTarget { LookMode = LookMode.Position, Position = cameraPos } },
            Eyes = new LookAtType { LookAtTarget = new LookAtTarget { LookMode = LookMode.Position, Position = cameraPos } },
        };
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            _lookAtHandles.Clear();
            _gazeStates.Clear();
        }
    }

    public void LockGaze(IActor actor, GazeTargetType targetType = GazeTargetType.All)
    {
        if (actor.Address == nint.Zero)
            return;

        var gameObject = _objectTable.CreateObjectReference(actor.Address);
        if (gameObject == null)
            return;

        if (!_lookAtHandles.TryGetValue(gameObject.GameObjectId, out var holder))
        {
            holder = new LookAtDataHolder();
            _lookAtHandles[gameObject.GameObjectId] = holder;
        }

        // Get current camera position to freeze at
        var cameraPos = _cameraService.GetCameraPosition();

        // Set lock flags and initialize positions to camera
        if (targetType.HasFlag(GazeTargetType.Eyes))
        {
            holder.EyesLocked = true;
            holder.Target.Eyes.LookAtTarget.Position = cameraPos;
            holder.Target.Eyes.LookAtTarget.LookMode = LookMode.Position;
        }
        if (targetType.HasFlag(GazeTargetType.Head))
        {
            holder.HeadLocked = true;
            holder.Target.Head.LookAtTarget.Position = cameraPos;
            holder.Target.Head.LookAtTarget.LookMode = LookMode.Position;
        }
        if (targetType.HasFlag(GazeTargetType.Body))
        {
            holder.BodyLocked = true;
            holder.Target.Body.LookAtTarget.Position = cameraPos;
            holder.Target.Body.LookAtTarget.LookMode = LookMode.Position;
        }

        // Set target mode to Camera so the detour processes it (applies our locked positions)
        holder.TargetMode = GazeTargetMode.Camera;
        holder.TargetType = targetType;

        _log.Debug($"GazeService: Locked gaze for actor {gameObject.GameObjectId}, type={targetType}");
        _eventBus.Publish(new GazeLockChangedEvent(actor, true));
    }

    /// <summary>
    /// Disables gaze control for an actor, letting bones move naturally with their parents.
    /// Use this for posing mode where gaze should not override bone positions.
    /// </summary>
    public void DisableGaze(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return;

        var gameObject = _objectTable.CreateObjectReference(actor.Address);
        if (gameObject == null)
            return;

        if (!_lookAtHandles.TryGetValue(gameObject.GameObjectId, out var holder))
        {
            holder = new LookAtDataHolder();
            _lookAtHandles[gameObject.GameObjectId] = holder;
        }

        // Set to None mode - this makes the detour skip our processing entirely
        // The original hook is still called, but since we have no handle with None mode,
        // the game's default behavior (bones follow animation) is preserved
        holder.TargetMode = GazeTargetMode.None;
        holder.TargetType = GazeTargetType.None;
        holder.EyesLocked = false;
        holder.HeadLocked = false;
        holder.BodyLocked = false;

        // Set LookMode to None to disable gaze tracking entirely
        holder.Target.Eyes.LookAtTarget.LookMode = LookMode.None;
        holder.Target.Head.LookAtTarget.LookMode = LookMode.None;
        holder.Target.Body.LookAtTarget.LookMode = LookMode.None;

        _log.Debug($"GazeService: Disabled gaze for actor {gameObject.GameObjectId}");
        _eventBus.Publish(new GazeLockChangedEvent(actor, false));
    }

    public void UnlockGaze(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return;

        var gameObject = _objectTable.CreateObjectReference(actor.Address);
        if (gameObject == null)
            return;

        if (_lookAtHandles.TryGetValue(gameObject.GameObjectId, out var holder))
        {
            holder.EyesLocked = false;
            holder.HeadLocked = false;
            holder.BodyLocked = false;

            // Reset to position tracking mode
            holder.Target.Eyes.LookAtTarget.LookMode = LookMode.Position;
            holder.Target.Head.LookAtTarget.LookMode = LookMode.Position;
            holder.Target.Body.LookAtTarget.LookMode = LookMode.Position;

            _log.Debug($"GazeService: Unlocked gaze for actor {gameObject.GameObjectId}");
        }

        _eventBus.Publish(new GazeLockChangedEvent(actor, false));
    }

    /// <summary>
    /// Lock or unlock a specific gaze target type at a position.
    /// Matches Brio's SetTargetLock API.
    /// </summary>
    public void SetTargetLock(IActor actor, bool doLock, GazeTargetType targetType, Vector3 position)
    {
        if (actor.Address == nint.Zero)
            return;

        var gameObject = _objectTable.CreateObjectReference(actor.Address);
        if (gameObject == null)
            return;

        if (!_lookAtHandles.TryGetValue(gameObject.GameObjectId, out var holder))
        {
            holder = new LookAtDataHolder();
            _lookAtHandles[gameObject.GameObjectId] = holder;
        }

        // Ensure gaze is active (Camera mode)
        if (holder.TargetMode == GazeTargetMode.None)
        {
            holder.TargetMode = GazeTargetMode.Camera;
            holder.TargetType = GazeTargetType.All;
        }

        // Set/unset lock for specific part
        if (targetType.HasFlag(GazeTargetType.Eyes))
        {
            holder.EyesLocked = doLock;
            if (doLock)
            {
                holder.Target.Eyes.LookAtTarget.Position = position;
                holder.Target.Eyes.LookAtTarget.LookMode = LookMode.Position;
            }
        }
        if (targetType.HasFlag(GazeTargetType.Head))
        {
            holder.HeadLocked = doLock;
            if (doLock)
            {
                holder.Target.Head.LookAtTarget.Position = position;
                holder.Target.Head.LookAtTarget.LookMode = LookMode.Position;
            }
        }
        if (targetType.HasFlag(GazeTargetType.Body))
        {
            holder.BodyLocked = doLock;
            if (doLock)
            {
                holder.Target.Body.LookAtTarget.Position = position;
                holder.Target.Body.LookAtTarget.LookMode = LookMode.Position;
            }
        }

        _log.Debug($"GazeService: SetTargetLock for actor {gameObject.GameObjectId}, doLock={doLock}, type={targetType}");
    }

    public bool IsGazeLocked(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        var gameObject = _objectTable.CreateObjectReference(actor.Address);
        if (gameObject == null)
            return false;

        if (_lookAtHandles.TryGetValue(gameObject.GameObjectId, out var holder))
        {
            return holder.EyesLocked || holder.HeadLocked || holder.BodyLocked;
        }

        return false;
    }

    public bool IsPartLocked(IActor actor, GazeTargetType targetType)
    {
        if (actor.Address == nint.Zero)
            return false;

        var gameObject = _objectTable.CreateObjectReference(actor.Address);
        if (gameObject == null)
            return false;

        if (_lookAtHandles.TryGetValue(gameObject.GameObjectId, out var holder))
        {
            if (targetType.HasFlag(GazeTargetType.Eyes) && holder.EyesLocked)
                return true;
            if (targetType.HasFlag(GazeTargetType.Head) && holder.HeadLocked)
                return true;
            if (targetType.HasFlag(GazeTargetType.Body) && holder.BodyLocked)
                return true;
        }

        return false;
    }

    public bool IsGazeEnabled(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return false;

        var gameObject = _objectTable.CreateObjectReference(actor.Address);
        if (gameObject == null)
            return false;

        if (_lookAtHandles.TryGetValue(gameObject.GameObjectId, out var holder))
        {
            return holder.TargetMode != GazeTargetMode.None;
        }

        return false;
    }

    public void EnableGaze(IActor actor)
    {
        if (actor.Address == nint.Zero)
            return;

        var gameObject = _objectTable.CreateObjectReference(actor.Address);
        if (gameObject == null)
            return;

        if (!_lookAtHandles.TryGetValue(gameObject.GameObjectId, out var holder))
        {
            holder = new LookAtDataHolder();
            _lookAtHandles[gameObject.GameObjectId] = holder;
        }

        // Initialize with Camera mode like Brio
        var cameraPos = _cameraService.GetCameraPosition();
        holder.TargetMode = GazeTargetMode.Camera;
        holder.TargetType = GazeTargetType.All;
        holder.EyesLocked = false;
        holder.HeadLocked = false;
        holder.BodyLocked = false;

        holder.Target = new LookAtSource
        {
            Body = new LookAtType { LookAtTarget = new LookAtTarget { LookMode = LookMode.Position, Position = cameraPos } },
            Head = new LookAtType { LookAtTarget = new LookAtTarget { LookMode = LookMode.Position, Position = cameraPos } },
            Eyes = new LookAtType { LookAtTarget = new LookAtTarget { LookMode = LookMode.Position, Position = cameraPos } },
        };

        _log.Debug($"GazeService: Enabled gaze for actor {gameObject.GameObjectId}");
    }

    public void Dispose()
    {
        _actorLookAtLoop.Dispose();
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
    }
}

internal class LookAtDataHolder
{
    public GazeTargetMode TargetMode;
    public GazeTargetType TargetType;
    public nint TargetEntityAddress;
    public LookAtSource Target;

    // Lock state - when locked, gaze is frozen at current position
    public bool EyesLocked;
    public bool HeadLocked;
    public bool BodyLocked;
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
    [FieldOffset(0x10)] public Vector3 Position;
}

internal enum LookMode
{
    None = 0,
    Frozen = 1,
    Pivot = 2,
    Position = 3,
}
