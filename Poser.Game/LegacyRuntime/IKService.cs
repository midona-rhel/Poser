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
using Poser.Domain.Posing;
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
    /// Solves the endpoint's chain toward the request. The havok pose is
    /// resolved from the endpoint's own slot skeleton, keeping raw pointers
    /// out of the public API.
    /// </summary>
    public void Solve(IBone endpoint, in IkSolveRequest request)
    {
        if (!_initialized || !request.Config.Enabled)
            return;

        var pose = GetHavokPose(endpoint);
        if (pose == null)
        {
            _log.Debug($"IKService: No havok pose for bone {endpoint.BoneName} (partial {endpoint.PartialId})");
            return;
        }

        if (request.Config.Solver == IkSolver.Ccd)
            SolveCcd(pose, endpoint, request);
        else if (request.Chain.TwoJointAvailable)
            SolveTwoJoint(pose, request);
    }

    /// <summary>
    /// Resolves the havok pose that owns the given bone through its OWN slot
    /// skeleton (slot-exact — an armed weapon bone solves against the weapon
    /// skeleton, never the Character skeleton). Pose 0 matches
    /// BonePosingService's usage.
    /// </summary>
    private static hkaPose* GetHavokPose(IBone bone)
    {
        var charaBase = SlotCharacterBases.Resolve(
            bone.Skeleton.Actor.Address,
            bone.Skeleton.Slot);
        if (charaBase == null)
            return null;

        var gameSkeleton = charaBase->Skeleton;
        if (gameSkeleton == null || bone.PartialId >= gameSkeleton->PartialSkeletonCount)
            return null;

        return gameSkeleton->PartialSkeletons[bone.PartialId].GetHavokPose(0);
    }

    private void SolveCcd(hkaPose* pose, IBone endpoint, in IkSolveRequest request)
    {
        // CCD walks same-slot parents from the endpoint; depth clamps to
        // the available chain.
        var boneList = GetBonesToDepth(endpoint, request.Config.CcdDepth, true);
        if (boneList.Count <= 1)
            return;

        var startBone = (short)boneList[^1].BoneIndex;
        var endBone = (short)boneList[0].BoneIndex;

        // The solver receives the CONFIGURED gain; the constraint buffer is
        // fully rewritten so nothing leaks from a previous solve.
        hkaCCDSolver* ccdSolver = (hkaCCDSolver*)_solverAddr.Aligned;
        _ccdSolverCtr(ccdSolver, request.Config.CcdIterations, request.Config.CcdGain);

        CCDIKConstraint* constraint = (CCDIKConstraint*)_ccdConstraintAddr.Aligned;
        *constraint = default;
        constraint->StartBone = startBone;
        constraint->EndBone = endBone;
        constraint->Target.X = request.Target.X;
        constraint->Target.Y = request.Target.Y;
        constraint->Target.Z = request.Target.Z;
        constraint->Target.W = 0f;

        var constraints = new hkArray<CCDIKConstraint>
        {
            Length = 1,
            CapacityAndFlags = 1,
            Data = constraint
        };

        byte notSure = 0;
        _ccdSolverSolve(ccdSolver, &notSure, &constraints, pose);
    }

    private void SolveTwoJoint(hkaPose* pose, in IkSolveRequest request)
    {
        var config = request.Config;
        var chain = request.Chain;

        // EVERY field is re-initialized per solve (indices, twists, gains,
        // hinge cosines, axis, targets, enforcement) — the shared buffer can
        // never carry a previous chain's values.
        TwoJointIKSetup* setup = (TwoJointIKSetup*)_twoJointSetupAddr.Aligned;
        *setup = new TwoJointIKSetup();
        setup->FirstJointIdx = chain.FirstJoint;
        setup->SecondJointIdx = chain.SecondJoint;
        setup->EndBoneIdx = chain.EndBone;
        setup->FirstJointTwistIdx = chain.FirstTwist;
        setup->SecondJointTwistIdx = chain.SecondTwist;
        setup->FirstJointIkGain = config.FirstJointGain;
        setup->SecondJointIkGain = config.SecondJointGain;
        setup->EndJointIkGain = config.EndJointGain;
        // Havok stores hinge limits as cosines: cos(min°) and cos(max°) —
        // the 0..180° defaults produce the native 1/-1 defaults exactly.
        setup->CosineMinHingeAngle = MathF.Cos(config.HingeMinDegrees * MathF.PI / 180f);
        setup->CosineMaxHingeAngle = MathF.Cos(config.HingeMaxDegrees * MathF.PI / 180f);
        setup->HingeAxisLS = new Vector4(config.HingeAxis, 0);
        setup->EndTargetMS = new Vector4(request.Target, 0);
        setup->EndTargetRotationMS = request.TargetRotation;
        setup->EnforceEndPosition = true;
        setup->EnforceEndRotation = config.EnforceEndRotation;

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
