using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Container.Array;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
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

    // Per-solve chain scratch; see GetBonesToDepth for the reuse contract.
    private readonly List<IBone> _chainBuffer = new();

    public IKService(ISigScanner scanner, IPluginLog log)
    {
        _log = log;

        try
        {
            // Scan for native Havok IK solver functions. All three patterns are
            // Brio's verbatim (Brio/Game/Posing/IKService.cs:26-28): the CCD
            // solver constructor, its solve entry, and the two-joint solve.
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
        else if (request.Config.Solver == IkSolver.Fabrik)
            SolveFabrik(pose, endpoint, request);
        else if (request.Chain.TwoJointAvailable)
            SolveTwoJoint(pose, request);
        else
            return;
        if (MathF.Abs(request.Config.SwivelDegrees) > 0.01f)
            ApplySwivel(pose, endpoint, request);
    }

    // ── model-space access shared by the managed passes ─────────────────

    private static bool ReadModelSpace(hkaPose* pose, int boneIndex, out Vector3 position, out Quaternion rotation)
    {
        var entry = pose->AccessBoneModelSpace(boneIndex, hkaPose.PropagateOrNot.DontPropagate);
        if (entry == null)
        {
            position = default;
            rotation = Quaternion.Identity;
            return false;
        }
        position = new Vector3(entry->Translation.X, entry->Translation.Y, entry->Translation.Z);
        rotation = new Quaternion(entry->Rotation.X, entry->Rotation.Y, entry->Rotation.Z, entry->Rotation.W);
        return true;
    }

    /// <summary>Writes one bone's place and rotation, root to tip order:
    /// each write propagates, so what hangs off a chain bone but is not
    /// itself in the chain (a twist, the fingers past the hand) follows
    /// from its own local pose, and the next chain bone's own write then
    /// overrides what that propagation guessed for it.</summary>
    private static void WriteModelSpace(hkaPose* pose, int boneIndex, Vector3 position, Quaternion rotation)
    {
        var entry = pose->AccessBoneModelSpace(boneIndex, hkaPose.PropagateOrNot.Propagate);
        if (entry == null)
            return;
        entry->Translation = *(hkVector4f*)(&position);
        entry->Rotation = *(hkQuaternionf*)(&rotation);
    }

    private static Quaternion FromTo(Vector3 from, Vector3 to)
    {
        float fl = from.Length(), tl = to.Length();
        if (fl < 1e-6f || tl < 1e-6f)
            return Quaternion.Identity;
        from /= fl;
        to /= tl;
        float dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot > 0.99999f)
            return Quaternion.Identity;
        if (dot < -0.99999f)
        {
            var any = MathF.Abs(from.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var perpendicular = Vector3.Normalize(Vector3.Cross(from, any));
            return Quaternion.CreateFromAxisAngle(perpendicular, MathF.PI);
        }
        var axis = Vector3.Normalize(Vector3.Cross(from, to));
        return Quaternion.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }

    // ── FABRIK ──────────────────────────────────────────────────────────

    /// <summary>Forward-and-backward reaching over the chain the depth
    /// names: link lengths are kept, the tip is dragged to the target and
    /// the root pinned back, alternating until the tip lands or the
    /// iterations run out. Each bone then takes the rotation that turns
    /// its old link direction into its new one.</summary>
    private void SolveFabrik(hkaPose* pose, IBone endpoint, in IkSolveRequest request)
    {
        var bones = GetBonesToDepth(endpoint, request.Config.CcdDepth, true);
        int count = bones.Count;
        if (count <= 1)
            return;
        // Root first.
        var indices = new int[count];
        var positions = new Vector3[count];
        var rotations = new Quaternion[count];
        for (int i = 0; i < count; i++)
        {
            var bone = bones[count - 1 - i];
            indices[i] = bone.BoneIndex;
            if (!ReadModelSpace(pose, indices[i], out positions[i], out rotations[i]))
                return;
        }
        var original = (Vector3[])positions.Clone();
        var lengths = new float[count - 1];
        float reach = 0f;
        for (int i = 0; i < count - 1; i++)
        {
            lengths[i] = Vector3.Distance(positions[i + 1], positions[i]);
            reach += lengths[i];
        }
        var root = positions[0];
        var target = request.Target;
        if (Vector3.Distance(target, root) >= reach)
        {
            // Out of reach: the chain points straight at the target.
            var direction = target - root;
            if (direction.LengthSquared() < 1e-12f)
                return;
            direction = Vector3.Normalize(direction);
            for (int i = 0; i < count - 1; i++)
                positions[i + 1] = positions[i] + direction * lengths[i];
        }
        else
        {
            const float tolerance = 0.0001f;
            for (int pass = 0; pass < request.Config.CcdIterations; pass++)
            {
                if (Vector3.DistanceSquared(positions[count - 1], target) < tolerance * tolerance)
                    break;
                positions[count - 1] = target;
                for (int i = count - 2; i >= 0; i--)
                {
                    var toward = positions[i] - positions[i + 1];
                    if (toward.LengthSquared() < 1e-12f)
                        continue;
                    positions[i] = positions[i + 1] + Vector3.Normalize(toward) * lengths[i];
                }
                positions[0] = root;
                for (int i = 0; i < count - 1; i++)
                {
                    var toward = positions[i + 1] - positions[i];
                    if (toward.LengthSquared() < 1e-12f)
                        continue;
                    positions[i + 1] = positions[i] + Vector3.Normalize(toward) * lengths[i];
                }
            }
        }
        for (int i = 0; i < count - 1; i++)
            rotations[i] = Quaternion.Normalize(
                FromTo(original[i + 1] - original[i], positions[i + 1] - positions[i]) * rotations[i]);
        if (request.Config.EnforceEndRotation)
            rotations[count - 1] = request.TargetRotation;
        for (int i = 0; i < count; i++)
            WriteModelSpace(pose, indices[i], positions[i], rotations[i]);
    }

    // ── the swivel ──────────────────────────────────────────────────────

    /// <summary>Spins the solved chain about the line from its first joint
    /// to its tip: the tip stays put, the bones between swing round it —
    /// the pole angle of an elbow or knee, or of any chain. The tip keeps
    /// the rotation the solver enforced, else it turns with the rest.</summary>
    private void ApplySwivel(hkaPose* pose, IBone endpoint, in IkSolveRequest request)
    {
        List<IBone> bones;
        if (request.Config.Solver == IkSolver.TwoJoint)
        {
            bones = _chainBuffer;
            bones.Clear();
            var current = endpoint;
            while (current != null && bones.Count < 8)
            {
                bones.Add(current);
                if (current.BoneIndex == request.Chain.FirstJoint)
                    break;
                current = current.ParentBone;
            }
            if (bones.Count < 2 || bones[^1].BoneIndex != request.Chain.FirstJoint)
                return;
        }
        else
        {
            bones = GetBonesToDepth(endpoint, request.Config.CcdDepth, true);
            if (bones.Count < 2)
                return;
        }
        int count = bones.Count;
        int startIndex = bones[count - 1].BoneIndex;
        int endIndex = bones[0].BoneIndex;
        if (!ReadModelSpace(pose, startIndex, out var start, out _)
            || !ReadModelSpace(pose, endIndex, out var end, out _))
            return;
        var axis = end - start;
        if (axis.LengthSquared() < 1e-10f)
            return;
        var spin = Quaternion.CreateFromAxisAngle(
            Vector3.Normalize(axis), request.Config.SwivelDegrees * MathF.PI / 180f);
        bool keepEndRotation = request.Config.Solver == IkSolver.TwoJoint && request.Config.EnforceEndRotation;
        // The whole chain is read BEFORE anything is written: a write
        // propagates, so a child read afterwards comes back already spun
        // by its parent and would be spun again (compounded down the
        // chain, 2026-09-02).
        var indices = new int[count];
        var positions = new Vector3[count];
        var rotations = new Quaternion[count];
        for (int i = 0; i < count; i++)
        {
            indices[i] = bones[i].BoneIndex;
            if (!ReadModelSpace(pose, indices[i], out positions[i], out rotations[i]))
                return;
        }
        // Root to tip.
        for (int i = count - 1; i >= 0; i--)
        {
            var swung = i == count - 1
                ? positions[i]
                : start + Vector3.Transform(positions[i] - start, spin);
            var turned = i == 0 && keepEndRotation
                ? rotations[i]
                : Quaternion.Normalize(spin * rotations[i]);
            WriteModelSpace(pose, indices[i], swung, turned);
        }
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
    private List<IBone> GetBonesToDepth(IBone bone, int depth, bool includeSelf)
    {
        // Reused buffer, not a fresh list: Solve runs per armed chain per
        // physics tick. Safe because the single caller reads two indices out
        // of the result and never retains it, and the physics pass is
        // single-threaded.
        var result = _chainBuffer;
        result.Clear();
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
        // Refuse before freeing. Solve gates on nothing but _initialized, so a
        // physics-detour tick arriving after the frees — container teardown
        // order is a convention, not a guarantee — would write through
        // released aligned memory. Clearing the flag first makes that
        // structural instead of ordering-dependent.
        var wasInitialized = _initialized;
        _initialized = false;

        if (wasInitialized)
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
