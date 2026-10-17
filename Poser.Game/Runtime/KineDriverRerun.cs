using System;
using System.Collections.Generic;
using System.Threading;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.Havok.Animation.Rig;
using Poser.Domain.Posing;
using Poser.Game.Natives;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Re-runs the game's KineDriver (.kdb bone drivers) for one posed skeleton, so
/// driven bones follow Poser's pose instead of the animation (#529).
/// KineDriver runs in frame task 23; Poser applies its pose in task 25, after it.
/// This repeats the game's own per-skeleton job once more, after the pose: lock,
/// sync model space, connect partials, update the module with dt 0 (no fade
/// advance), unlock. It also reports which bones the drivers wrote, so the
/// caller can put the user's own edits on those bones back on top.
/// </summary>
internal sealed unsafe class KineDriverRerun
{
    // Node types that write their bone (ApplyDriver), each one channel of its
    // local transform: 6 TargetTranslate (its rescale branch also writes scale),
    // 12 PosConstraint: position; 8 TargetRotate, 9 BendRoll, 10 BendSTRoll,
    // 11 Expmap, 13 OrientationConstraint, 14 DirectionConstraint: rotation;
    // 7 TargetScale and its child list: scale. RBF, Expr and the link nodes
    // write none.
    private const byte FirstWriter = 6, TargetTranslate = 6, TargetScale = 7, PosConstraint = 12, LastWriter = 14;
    private const int PartialSlots = 5;
    // BindKdb holds at most four drivers inline in the 0x48-byte instance.
    private const int MaxDrivers = 4;

    private delegate void SyncAllModelSpaceFn(GameSkeleton* skeleton);
    private delegate void ConnectPartialsFromFn(GameSkeleton* skeleton, int partial);
    private delegate void UpdateModuleFn(nint module, float deltaTime);

    private readonly SyncAllModelSpaceFn? _syncAllModelSpace;
    private readonly ConnectPartialsFromFn? _connectPartialsFrom;
    private readonly UpdateModuleFn? _updateModule;
    private readonly Dictionary<(int Partial, int Bone), TransformComponents> _driven = new();

    public KineDriverRerun(IPluginLog log, ISigScanner scanner)
    {
        if (scanner.TryScanText(NativeSignatures.Skeleton_SyncAllModelSpace, out var sync)
            && scanner.TryScanText(NativeSignatures.Skeleton_ConnectPartialsFrom, out var connect)
            && scanner.TryScanText(NativeSignatures.KineDriver_UpdateModule, out var update))
        {
            _syncAllModelSpace = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<SyncAllModelSpaceFn>(sync);
            _connectPartialsFrom = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<ConnectPartialsFromFn>(connect);
            _updateModule = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<UpdateModuleFn>(update);
        }
        else
        {
            log.Warning("KineDriver re-run unavailable: a signature was not found; driven bones keep following the animation.");
        }
    }

    public bool IsAvailable => _updateModule != null;

    /// <summary>Runs the skeleton's drivers once against the current pose, and
    /// lists the bones they wrote this frame, (partial, bone index), with the
    /// channels written. False when they didn't run: no module, a module for
    /// another skeleton, or a busy lock skip this frame rather than wait.</summary>
    public bool Run(GameSkeleton* skeleton, out IReadOnlyDictionary<(int Partial, int Bone), TransformComponents> driven)
    {
        _driven.Clear();
        driven = _driven;
        if (_updateModule == null || skeleton == null)
            return false;
        var owner = skeleton->Owner;
        if (owner == null)
            return false;
        var module = *(nint*)((byte*)owner + NativeFields.CharacterBase_KineDriverModule);
        if (module == 0 || *(GameSkeleton**)(module + NativeFields.BoneKineDriverModule_Skeleton) != skeleton)
            return false;

        ref var spinLock = ref *(int*)((byte*)skeleton + NativeFields.Skeleton_PoseLock);
        if (Interlocked.CompareExchange(ref spinLock, 1, 0) != 0)
            return false;
        try
        {
            _syncAllModelSpace!(skeleton);
            _connectPartialsFrom!(skeleton, 0);
            _updateModule(module, 0f);
            // After the update: the node weights are the ones this run used.
            CollectDriven(skeleton, module);
        }
        finally
        {
            Volatile.Write(ref spinLock, 0);
        }
        return true;
    }

    /// <summary>Every bone a driver of this module writes this frame: enabled
    /// instance, enabled driver, enabled node of a writing type with a weight
    /// above 0. A merged slot's indices are the whole-skeleton view's and are
    /// mapped to their partial by name. Rebuilt every run: instances bind when
    /// their .kdb loads and rebind on a face, hair, top or headgear swap.</summary>
    private void CollectDriven(GameSkeleton* skeleton, nint module)
    {
        _driven.Clear();
        nint mergedSkeleton = *(nint*)(module + NativeFields.BoneKineDriverModule_MergedSkeleton);
        for (int p = 0; p < PartialSlots; p++)
        {
            nint slot = module + NativeFields.BoneKineDriverModule_Slots + p * NativeFields.KineDriverSlot_Size;
            nint instance = *(nint*)slot;
            if (instance == 0
                || *(byte*)(instance + NativeFields.KineDriverInstance_Ready) == 0
                || *(byte*)(instance + NativeFields.KineDriverInstance_Enabled) == 0)
                continue;
            // The merged view only with a merged skeleton; its pose must be that
            // skeleton's, or the game skips the whole slot (nothing written).
            bool merged = *(byte*)(slot + NativeFields.KineDriverSlot_Merged) != 0 && mergedSkeleton != 0;
            var mergedPose = merged ? *(hkaPose**)(module + NativeFields.BoneKineDriverModule_MergedPose) : null;
            if (merged && (mergedPose == null || (nint)mergedPose->Skeleton != mergedSkeleton))
                continue;
            uint drivers = Math.Min(*(uint*)(instance + NativeFields.KineDriverInstance_DriverCount), MaxDrivers);
            for (uint d = 0; d < drivers; d++)
            {
                // Inline: the instance holds the driver pointers themselves.
                nint driver = *(nint*)(instance + NativeFields.KineDriverInstance_Drivers + 8 * (nint)d);
                if (driver == 0 || *(byte*)(driver + NativeFields.KineDriverRuntime_Enabled) == 0)
                    continue;
                nint data = *(nint*)driver;
                if (data == 0)
                    continue;
                nint weights = driver + NativeFields.KineDriverRuntime_NodeWeights;
                weights += *(int*)weights;
                uint nodes = *(uint*)(data + NativeFields.KineDriverData_NodeCount);
                nint table = data + NativeFields.KineDriverData_Nodes;
                table += *(int*)table;
                for (uint k = 0; k < nodes && k < 4096; k++)
                {
                    nint entry = table + 4 * (nint)k;
                    nint node = entry + *(int*)entry;
                    byte type = *(byte*)(node + NativeFields.KineDriverNode_Type);
                    if (*(byte*)(node + NativeFields.KineDriverNode_Enabled) == 0
                        || *(float*)(weights + 4 * (nint)k) <= 0f
                        || type < FirstWriter || type > LastWriter)
                        continue;
                    var channels = type switch
                    {
                        TargetTranslate => TransformComponents.Position | TransformComponents.Scale,
                        PosConstraint => TransformComponents.Position,
                        TargetScale => TransformComponents.Scale,
                        _ => TransformComponents.Rotation,
                    };
                    Add(skeleton, p, mergedPose, *(short*)(node + NativeFields.KineDriverNode_Bone), channels);
                    if (type == TargetScale)
                    {
                        nint children = node + NativeFields.KineDriverTargetScale_Children;
                        children += *(int*)children;
                        uint count = *(uint*)(node + NativeFields.KineDriverTargetScale_ChildCount);
                        for (uint c = 0; c < count && c < 256; c++)
                            Add(skeleton, p, mergedPose, *(short*)(children + 2 * (nint)c), TransformComponents.Scale);
                    }
                }
            }
        }
    }

    private void Add(GameSkeleton* skeleton, int partial, hkaPose* mergedPose, short bone, TransformComponents channels)
    {
        if (bone < 0)
            return;
        if (mergedPose == null)
        {
            Mark((partial, bone), channels);
            return;
        }
        // A merged slot's index is into the whole-skeleton view: the bone by name.
        var merged = mergedPose->Skeleton;
        if (merged == null || bone >= merged->Bones.Length || merged->Bones[bone].Name.String is not { } name)
            return;
        for (int p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var pose = skeleton->PartialSkeletons[p].GetHavokPose(0);
            if (pose == null || pose->Skeleton == null)
                continue;
            for (int b = 0; b < pose->Skeleton->Bones.Length; b++)
                if (pose->Skeleton->Bones[b].Name.String == name)
                {
                    Mark((p, b), channels);
                    return;
                }
        }
    }

    private void Mark((int Partial, int Bone) key, TransformComponents channels) =>
        _driven[key] = _driven.GetValueOrDefault(key) | channels;
}
