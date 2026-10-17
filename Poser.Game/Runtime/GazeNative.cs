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
using Poser.Game.Natives;

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
        Poser.Game.Natives.NativeSignatures.LookAtController_SetTarget);

    public nint ScanActorLookAtLoop(ISigScanner scanner) => scanner.ScanText(
        Poser.Game.Natives.NativeSignatures.LookAtContainer_UpdateLookAt);

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
    [FieldOffset(NativeFields.LookAtType_LookAtTarget)] public LookAtTarget LookAtTarget;
}

[StructLayout(LayoutKind.Explicit, Size = 0x28)]
internal struct LookAtTarget
{
    [FieldOffset(NativeFields.LookAtTarget_LookMode)] public LookMode LookMode;
    // Position and the actor-target id are a union at 0x10 — corroborated by
    // Brio ActorLookAtService.LookAtTarget and Ktisis ActorGaze.Gaze.
    [FieldOffset(NativeFields.LookAtTarget_Position)] public Vector3 Position;
    [FieldOffset(NativeFields.LookAtTarget_ActorTargetId)] public ulong ActorTargetId;
    // Trailing field of the native 0x28 CharacterLookAtTargetParam (Ktisis
    // Gaze.Unk5). The explicit size keeps captures and native reads
    // byte-complete instead of over-reading adjacent managed memory.
    [FieldOffset(NativeFields.LookAtTarget_Unknown20)] public uint Unknown20;
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
