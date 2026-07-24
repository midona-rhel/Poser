using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Game;

/// <summary>
/// Service for solving inverse kinematics using native Havok solvers.
/// Based on Brio's IKService implementation.
/// </summary>
public unsafe class IKService : IIKService
{
    private readonly IPluginLog _log;

    // Native function pointers
    private delegate* unmanaged<hkaCCDSolver*, int, float, void> _ccdSolverCtr;
    private delegate* unmanaged<hkaCCDSolver*, byte*, hkArray<CCDIKConstraint>*, hkaPose*, byte*> _ccdSolverSolve;
    private delegate* unmanaged<byte*, TwoJointIKSetup*, hkaPose*, byte*> _twoJointSolverSolve;

    // Allocated memory for solver structs
    private (nint Aligned, nint Unaligned) _solverAddr;
    private (nint Aligned, nint Unaligned) _ccdConstraintAddr;
    private (nint Aligned, nint Unaligned) _twoJointSetupAddr;

    private bool _initialized = false;

    public IKService(ISigScanner scanner, IPluginLog log)
    {
        _log = log;

        try
        {
            // Scan for native Havok IK solver functions
            _ccdSolverCtr = (delegate* unmanaged<hkaCCDSolver*, int, float, void>)
                scanner.ScanText("E8 ?? ?? ?? ?? 48 8D 43 ?? 48 C7 43");
            _ccdSolverSolve = (delegate* unmanaged<hkaCCDSolver*, byte*, hkArray<CCDIKConstraint>*, hkaPose*, byte*>)
                scanner.ScanText("E8 ?? ?? ?? ?? 8B 44 24 ?? 48 8B 5C 24 ?? 48 3B 5C 24");
            _twoJointSolverSolve = (delegate* unmanaged<byte*, TwoJointIKSetup*, hkaPose*, byte*>)
                scanner.ScanText("E8 ?? ?? ?? ?? 0F 28 55 ?? 41 0F 28 D8");

            // Allocate aligned memory for solver structs (16-byte aligned for SIMD)
            _solverAddr = NativeHelpers.AllocateAlignedMemory(sizeof(hkaCCDSolver), 16);
            _ccdConstraintAddr = NativeHelpers.AllocateAlignedMemory(sizeof(CCDIKConstraint), 16);
            _twoJointSetupAddr = NativeHelpers.AllocateAlignedMemory(sizeof(TwoJointIKSetup), 16);

            // Initialize TwoJoint setup with defaults
            TwoJointIKSetup* setup = (TwoJointIKSetup*)_twoJointSetupAddr.Aligned;
            *setup = new TwoJointIKSetup();

            _initialized = true;
            _log.Debug("IKService: Initialized with native Havok solvers");
        }
        catch (Exception ex)
        {
            _log.Warning($"IKService: Failed to initialize - IK will be disabled. Error: {ex.Message}");
            _initialized = false;
        }
    }

    /// <summary>
    /// Solves IK for a bone chain to reach a target position.
    /// The havok pose is resolved from the bone's skeleton, keeping raw pointers out of the public API.
    /// </summary>
    public void SolveIK(IBone bone, Vector3 target, BoneIKInfo ikInfo)
    {
        if (!_initialized || !ikInfo.Enabled)
            return;

        var pose = GetHavokPose(bone);
        if (pose == null)
        {
            _log.Debug($"IKService: No havok pose for bone {bone.BoneName} (partial {bone.PartialId})");
            return;
        }

        if (ikInfo.SolverType == IKSolverType.CCD)
        {
            SolveCCD(pose, ikInfo.CCD, bone, target);
        }
        else if (ikInfo.SolverType == IKSolverType.TwoJoint)
        {
            SolveTwoJoint(pose, ikInfo.TwoJoint, bone, target);
        }
    }

    /// <summary>
    /// Resolves the havok pose that owns the given bone, via its skeleton's actor draw object.
    /// Same resolution path as Skeleton.GetGameSkeleton; pose 0 matches BonePosingService's usage.
    /// </summary>
    private static hkaPose* GetHavokPose(IBone bone)
    {
        var address = bone.Skeleton.Actor.Address;
        if (address == nint.Zero)
            return null;

        var character = (Character*)address;
        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null)
            return null;

        if (drawObject->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            return null;

        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)drawObject;
        var gameSkeleton = charaBase->Skeleton;
        if (gameSkeleton == null || bone.PartialId >= gameSkeleton->PartialSkeletonCount)
            return null;

        return gameSkeleton->PartialSkeletons[bone.PartialId].GetHavokPose(0);
    }

    private void SolveCCD(hkaPose* pose, CCDOptions options, IBone bone, Vector3 target)
    {
        var boneList = GetBonesToDepth(bone, options.Depth, true);
        if (boneList.Count <= 1)
            return;

        var startBone = (short)boneList[^1].BoneIndex;
        var endBone = (short)boneList[0].BoneIndex;

        hkaCCDSolver* ccdSolver = (hkaCCDSolver*)_solverAddr.Aligned;
        _ccdSolverCtr(ccdSolver, options.Iterations, 1f);

        CCDIKConstraint* constraint = (CCDIKConstraint*)_ccdConstraintAddr.Aligned;
        constraint->StartBone = startBone;
        constraint->EndBone = endBone;
        constraint->Target.X = target.X;
        constraint->Target.Y = target.Y;
        constraint->Target.Z = target.Z;

        var constraints = new hkArray<CCDIKConstraint>
        {
            Length = 1,
            CapacityAndFlags = 1,
            Data = constraint
        };

        byte notSure = 0;
        _ccdSolverSolve(ccdSolver, &notSure, &constraints, pose);
    }

    private void SolveTwoJoint(hkaPose* pose, TwoJointOptions options, IBone bone, Vector3 target)
    {
        var boneList = GetBonesToDepth(bone, options.FirstBone, true);
        if (boneList.Count < options.FirstBone)
            return;

        TwoJointIKSetup* setup = (TwoJointIKSetup*)_twoJointSetupAddr.Aligned;
        setup->FirstJointIdx = (short)boneList[options.FirstBone].BoneIndex;
        setup->SecondJointIdx = (short)boneList[options.SecondBone].BoneIndex;
        setup->EndBoneIdx = (short)boneList[options.EndBone].BoneIndex;
        setup->EndTargetMS = new Vector4(target, 0);
        setup->HingeAxisLS = new Vector4(options.RotationAxis, 0);

        byte notSure = 0;
        _twoJointSolverSolve(&notSure, setup, pose);
    }

    /// <summary>
    /// Gets a list of bones going up the hierarchy from the given bone.
    /// </summary>
    private static List<IBone> GetBonesToDepth(IBone bone, int depth, bool includeSelf)
    {
        var result = new List<IBone>();
        if (includeSelf)
            result.Add(bone);

        var current = bone.ParentBone;
        while (current != null && result.Count < depth + 1)
        {
            result.Add(current);
            current = current.ParentBone;
        }
        return result;
    }

    public void Dispose()
    {
        if (_initialized)
        {
            NativeHelpers.FreeAlignedMemory(_solverAddr);
            NativeHelpers.FreeAlignedMemory(_ccdConstraintAddr);
            NativeHelpers.FreeAlignedMemory(_twoJointSetupAddr);
        }
        GC.SuppressFinalize(this);
    }

    // Native struct definitions

    [StructLayout(LayoutKind.Explicit, Size = 0x18)]
    private struct hkaCCDSolver
    {
        [FieldOffset(0x0)] public nint vtbl;
        [FieldOffset(0x10)] public uint Iterations;
        [FieldOffset(0x14)] public float Gain;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x20)]
    private struct CCDIKConstraint
    {
        [FieldOffset(0x0)] public short StartBone;
        [FieldOffset(0x2)] public short EndBone;
        [FieldOffset(0x10)] public hkVector4f Target;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x82)]
    private struct TwoJointIKSetup
    {
        [FieldOffset(0x00)] public short FirstJointIdx;
        [FieldOffset(0x02)] public short SecondJointIdx;
        [FieldOffset(0x04)] public short EndBoneIdx;
        [FieldOffset(0x06)] public short FirstJointTwistIdx;
        [FieldOffset(0x08)] public short SecondJointTwistIdx;
        [FieldOffset(0x10)] public Vector4 HingeAxisLS;
        [FieldOffset(0x20)] public float CosineMaxHingeAngle;
        [FieldOffset(0x24)] public float CosineMinHingeAngle;
        [FieldOffset(0x28)] public float FirstJointIkGain;
        [FieldOffset(0x2C)] public float SecondJointIkGain;
        [FieldOffset(0x30)] public float EndJointIkGain;
        [FieldOffset(0x40)] public Vector4 EndTargetMS;
        [FieldOffset(0x50)] public Quaternion EndTargetRotationMS;
        [FieldOffset(0x60)] public Vector4 EndBoneOffsetLS;
        [FieldOffset(0x70)] public Quaternion EndBoneRotationOffsetLS;
        [FieldOffset(0x80)] public bool EnforceEndPosition;
        [FieldOffset(0x81)] public bool EnforceEndRotation;

        public TwoJointIKSetup()
        {
            FirstJointIdx = -1;
            SecondJointIdx = -1;
            EndBoneIdx = -1;
            FirstJointTwistIdx = -1;
            SecondJointTwistIdx = -1;
            HingeAxisLS = Vector4.Zero;
            CosineMaxHingeAngle = -1f;
            CosineMinHingeAngle = 1f;
            FirstJointIkGain = 1f;
            SecondJointIkGain = 1f;
            EndJointIkGain = 1f;
            EndTargetMS = Vector4.Zero;
            EndTargetRotationMS = Quaternion.Identity;
            EndBoneOffsetLS = Vector4.Zero;
            EndBoneRotationOffsetLS = Quaternion.Identity;
            EnforceEndPosition = true;
            EnforceEndRotation = false;
        }
    }
}
