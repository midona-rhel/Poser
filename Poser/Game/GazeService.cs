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
    private readonly IPluginLog _log;

    private delegate* unmanaged<CharacterLookAtController*, LookAtTarget*, uint, nint, void> _updateLookAt;

    private delegate nint ActorLookAtLoopDelegate(ContainerInterface* args);
    private Hook<ActorLookAtLoopDelegate>? _actorLookAtLoop;

    private readonly Dictionary<IActor, GazeState> _gazeStates = new();
    private readonly Dictionary<ulong, LookAtDataHolder> _lookAtHandles = new();

    public GazeService(
        IGPoseService gPoseService,
        ICameraService cameraService,
        IObjectTable objectTable,
        ISigScanner sigScanner,
        IGameInteropProvider hooks,
        IPluginLog log)
    {
        _gPoseService = gPoseService;
        _cameraService = cameraService;
        _objectTable = objectTable;
        _log = log;

        try
        {
            var updateFaceTrackerAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 8B D7 48 8B CB E8 ?? ?? ?? ?? 41 ?? ?? 8B D7 48 ?? ?? 48 ?? ?? ?? ?? 48 83 ?? ?? 5F");
            _updateLookAt = (delegate* unmanaged<CharacterLookAtController*, LookAtTarget*, uint, nint, void>)updateFaceTrackerAddress;

            var actorLookAtLoopAddress = sigScanner.ScanText("E8 ?? ?? ?? ?? 48 83 C3 08 48 83 EF 01 75 CF 48 ?? ?? ?? ?? 48");
            _actorLookAtLoop = hooks.HookFromAddress<ActorLookAtLoopDelegate>(actorLookAtLoopAddress, ActorLookAtDetour);
            _actorLookAtLoop.Enable();
        }
        catch (Exception ex)
        {
            _log.Warning($"GazeService: Failed to initialize gaze hooks, gaze control will be disabled: {ex.Message}");
        }

        _gPoseService.OnGPoseStateChanged += OnGPoseStateChanged;
    }

    private nint ActorLookAtDetour(ContainerInterface* args)
    {
        if (_actorLookAtLoop == null)
            return 0;

        if (_gPoseService.IsGPosing)
        {
            var targetActor = _objectTable.CreateObjectReference((nint)args->OwnerObject);
            if (targetActor is not null && targetActor.IsValid()
                && _lookAtHandles.TryGetValue(targetActor.GameObjectId, out var lookAtDataHolder))
            {
                if (lookAtDataHolder.TargetMode == GazeTargetMode.None)
                {
                    return _actorLookAtLoop.Original(args);
                }

                // Update target positions based on mode
                if (lookAtDataHolder.TargetMode == GazeTargetMode.Camera)
                {
                    var cameraPos = _cameraService.GetCameraPosition();
                    SetLookAtTargetPosition(lookAtDataHolder, cameraPos);
                }
                else if (lookAtDataHolder.TargetMode == GazeTargetMode.Entity && lookAtDataHolder.TargetEntityAddress != nint.Zero)
                {
                    var targetPos = GetEntityPosition(lookAtDataHolder.TargetEntityAddress);
                    SetLookAtTargetPosition(lookAtDataHolder, targetPos);
                }
                else if (lookAtDataHolder.TargetMode == GazeTargetMode.Forward)
                {
                    // Forward mode - calculate a position in front of the character based on their facing direction
                    var nativeObj = (GameObject*)targetActor.Address;
                    var position = new Vector3(nativeObj->Position.X, nativeObj->Position.Y, nativeObj->Position.Z);
                    var rotation = nativeObj->Rotation;

                    // Calculate forward direction from rotation (Y rotation in radians)
                    // Add a point 10 units in front of the character at head height
                    var forwardDir = new Vector3(
                        MathF.Sin(rotation),
                        0f,
                        MathF.Cos(rotation)
                    );

                    // Position 10 units ahead, slightly elevated for head/eye level
                    var forwardPos = position + forwardDir * 10f + new Vector3(0, 1.5f, 0);
                    SetLookAtTargetPosition(lookAtDataHolder, forwardPos);
                }

                // Apply the look-at updates
                var lookAtController = &((Character*)targetActor.Address)->LookAt.Controller;

                fixed (LookAtTarget* bodyTarget = &lookAtDataHolder.Target.Body.LookAtTarget)
                fixed (LookAtTarget* headTarget = &lookAtDataHolder.Target.Head.LookAtTarget)
                fixed (LookAtTarget* eyesTarget = &lookAtDataHolder.Target.Eyes.LookAtTarget)
                {
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Body))
                        _updateLookAt(lookAtController, bodyTarget, LookAtIndex_Body, 0);
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Head))
                        _updateLookAt(lookAtController, headTarget, LookAtIndex_Head, 0);
                    if (lookAtDataHolder.TargetType.HasFlag(GazeTargetType.Eyes))
                        _updateLookAt(lookAtController, eyesTarget, LookAtIndex_Eyes, 0);
                }
            }
        }

        return _actorLookAtLoop.Original(args);
    }

    private Vector3 GetEntityPosition(nint address)
    {
        if (address == nint.Zero)
            return Vector3.Zero;

        var gameObject = (GameObject*)address;
        return gameObject->Position;
    }

    private static void SetLookAtTargetPosition(LookAtDataHolder holder, Vector3 position)
    {
        if (holder.TargetType.HasFlag(GazeTargetType.Eyes))
            holder.Target.Eyes.LookAtTarget.Position = position;
        if (holder.TargetType.HasFlag(GazeTargetType.Body))
            holder.Target.Body.LookAtTarget.Position = position;
        if (holder.TargetType.HasFlag(GazeTargetType.Head))
            holder.Target.Head.LookAtTarget.Position = position;
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

    private void OnGPoseStateChanged(bool isGPosing)
    {
        if (!isGPosing)
        {
            _lookAtHandles.Clear();
            _gazeStates.Clear();
        }
    }

    public void Dispose()
    {
        _actorLookAtLoop?.Dispose();
        _gPoseService.OnGPoseStateChanged -= OnGPoseStateChanged;
    }
}

internal class LookAtDataHolder
{
    public GazeTargetMode TargetMode;
    public GazeTargetType TargetType;
    public nint TargetEntityAddress;
    public LookAtSource Target;
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
