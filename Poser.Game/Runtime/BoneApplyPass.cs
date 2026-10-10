using System;
using System.Collections.Generic;
using System.Numerics;
using System.Linq;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;
using Poser.Core;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Identity;
using Poser.Services;
using Poser.Application.Lifecycle;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// The native apply pass: the UpdateBonePhysics hook and everything it runs —
/// each scheduled slot skeleton's authored stacks, held IK, FABRIK controls
/// and transitive actions applied to the Havok pose in Brio's order, with the
/// transform caches refreshed after apply and after partial reparenting.
/// Also owns which skeletons the next pass visits.
/// </summary>
internal sealed unsafe class BoneApplyPass : IDisposable
{
    private readonly IPluginLog _log;
    private readonly IGPoseService _gPoseService;
    private readonly ISkeletonService _skeletonService;
    private readonly IActorManager _actorManager;
    private readonly IKService _ikService;
    private readonly Poser.Game.Posing.GazePoseFrames _gazeFrames;
    private readonly PoseStackStore _stacks;
    private readonly IkChainRegistry _ik;
    private readonly TransitiveActionScheduler _transitive;
    private readonly Action<SkeletonKey> _purge;

    // Hook for intercepting bone physics updates
    private delegate nint UpdateBonePhysicsDelegate(nint a1);
    private Hook<UpdateBonePhysicsDelegate>? _updateBonePhysicsHook;

    // Track which slot skeletons need updating this frame (have modifications)
    private readonly HashSet<SkeletonKey> _skeletonsToUpdate = new();

    /// <summary>One-frame apply-pass leases taken by
    /// <see cref="RequestRawRefresh"/>; cleared by every rebuild, so a caller
    /// that still needs live raw asks again next tick.</summary>
    private readonly HashSet<SkeletonKey> _rawRefreshRequests = new();

    /// <summary>Reused snapshot buffer for the apply pass, which must iterate
    /// a set it may mutate. Single-threaded (physics detour) and never
    /// nested, so one instance keeps the steady state free of per-frame
    /// arrays.</summary>
    private readonly List<SkeletonKey> _updatePassBuffer = new();

    private bool _isUpdating = false;

    // One-shot fault flag: the detour runs every frame, so a repeating
    // fault must not turn the log into a firehose.
    private bool _physicsDetourFaultLogged;

    /// <summary>Which (build, partial) pairs already logged a native-map
    /// fallback.</summary>
    private readonly HashSet<(long, int)> _mapFallbackLogged = new();

    /// <param name="purge">Purges one slot's whole pose state when its actor
    /// is gone mid-pass.</param>
    public BoneApplyPass(
        IPluginLog log,
        IGPoseService gPoseService,
        ISkeletonService skeletonService,
        IActorManager actorManager,
        IKService ikService,
        Poser.Game.Posing.GazePoseFrames gazeFrames,
        PoseStackStore stacks,
        IkChainRegistry ik,
        TransitiveActionScheduler transitive,
        Action<SkeletonKey> purge)
    {
        _log = log;
        _gPoseService = gPoseService;
        _skeletonService = skeletonService;
        _actorManager = actorManager;
        _ikService = ikService;
        _gazeFrames = gazeFrames;
        _stacks = stacks;
        _ik = ik;
        _transitive = transitive;
        _purge = purge;
    }

    /// <summary>Installs and enables the UpdateBonePhysics hook. Called once
    /// the owner has built everything the detour reads.</summary>
    public void InstallHook(IGameInteropProvider hooking, ISigScanner scanner)
    {
        // Hook UpdateBonePhysics - this is called during skeleton updates
        try
        {
            var updateBonePhysicsAddress = scanner.ScanText("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ?? 45 33 E4");
            _updateBonePhysicsHook = hooking.HookFromAddress<UpdateBonePhysicsDelegate>(updateBonePhysicsAddress, UpdateBonePhysicsDetour);
            _updateBonePhysicsHook.Enable();
            _log.Debug("BonePosingService: UpdateBonePhysics hook initialized");
        }
        catch (Exception ex)
        {
            _log.Warning($"BonePosingService: Failed to hook UpdateBonePhysics: {ex.Message}");
        }
    }

    /// <summary>The skeletons the next pass visits; the finalize snapshot
    /// reads it as well.</summary>
    public HashSet<SkeletonKey> UpdateSet => _skeletonsToUpdate;

    /// <summary>Puts a slot in the pass still ahead of us this frame.</summary>
    public void Schedule(SkeletonKey key) => _skeletonsToUpdate.Add(key);

    /// <summary>One frame's membership in the apply pass, for a caller that
    /// needs <see cref="IBone.LastRawTransform"/> refreshed on a skeleton
    /// that carries nothing the pass would otherwise select it for.</summary>
    public void RequestRawRefresh(SkeletonKey key) => _rawRefreshRequests.Add(key);

    public void Unschedule(SkeletonKey key)
    {
        _skeletonsToUpdate.Remove(key);
        _rawRefreshRequests.Remove(key);
    }

    public void ClearUpdateSet() => _skeletonsToUpdate.Clear();

    /// <summary>Whether a native-map fallback for this (build, partial) is
    /// reported for the first time; both passes log a fallback once.</summary>
    public bool FirstFallback((long Revision, int Partial) key) => _mapFallbackLogged.Add(key);

    /// <summary>The per-frame rebuild of the pass's membership: every slot
    /// with stacks, an enabled chain, a registered batch, or a raw-refresh
    /// lease; every other slot drops its animated baselines.</summary>
    public void Rebuild()
    {
        _skeletonsToUpdate.Clear();

        foreach (var (slotKey, poseInfo) in _stacks)
        {
            // A registered batch qualifies the skeleton on its own. Brio gets
            // this for free — its pass takes every skeleton that has a posing
            // capability (SkeletonService.cs:227-231) — while Poser's pass is
            // opt-in per stack/chain, and a bake registers its actions exactly
            // when it has just cleared both.
            if (poseInfo.IsOverridden || _ik.HasEnabledChains(slotKey) ||
                _transitive.Contains(slotKey) ||
                _rawRefreshRequests.Contains(slotKey))
            {
                _skeletonsToUpdate.Add(slotKey);
                continue;
            }

            _stacks.RemoveAnimatedBaselines(slotKey);
        }

        // The lease is one rebuild long. A settling bake re-requests on every
        // tick it waits; when it stops, the skeleton leaves the pass again.
        _rawRefreshRequests.Clear();
    }

    private nint UpdateBonePhysicsDetour(nint a1)
    {
        var result = _updateBonePhysicsHook!.Original(a1);

        if (!_gPoseService.IsGPosing || _isUpdating)
            return result;

        _isUpdating = true;
        try
        {
            ApplyAllBoneTransforms();
        }
        catch (Exception ex)
        {
            // Never fault the native physics update (CharacterFinalizeDetour
            // standard): a managed fault here would unwind into the game's
            // render graph.
            if (!_physicsDetourFaultLogged)
            {
                _physicsDetourFaultLogged = true;
                _log.Error($"BonePosingService: apply pass faulted (logged once): {ex}");
            }
        }
        finally
        {
            _isUpdating = false;
        }

        return result;
    }

    private void ApplyAllBoneTransforms()
    {
        _gazeFrames.BeginPass();
        // The pass can purge (and therefore mutate _skeletonsToUpdate) while it
        // runs, so it iterates a snapshot — a REUSED buffer, because this runs
        // in the physics detour every frame and the old ToArray() charged the
        // steady state one array per frame.
        _updatePassBuffer.Clear();
        foreach (var key in _skeletonsToUpdate)
            _updatePassBuffer.Add(key);

        for (var i = 0; i < _updatePassBuffer.Count; i++)
        {
            var slotKey = _updatePassBuffer[i];
            if (!_stacks.TryGet(slotKey, out var poseInfo))
                continue;

            var actor = FindActor(_actorManager, slotKey.Actor);

            if (actor == null)
            {
                _purge(slotKey);
                continue;
            }

            // A REPLACED skeleton is not a reason to drop the pose — it is
            // exactly where the pose belongs. The store is keyed by actor and
            // slot, so the apply pass lands the same authored stacks on
            // whatever instance the slot currently holds.
            //
            // A MISSING skeleton is not a reason either: every redraw passes
            // through frames where the actor has no character base, and
            // purging there threw the pose away right before the rebuilt
            // skeleton arrived to receive it. While the ACTOR exists the pose
            // waits; only actor teardown purges.
            var skeleton = _skeletonService.GetSkeleton(actor, slotKey.Slot) as Skeleton;
            if (skeleton == null || !skeleton.IsValid)
                continue;

            ApplySkeletonTransforms(slotKey, skeleton, poseInfo);
        }
    }

    /// <summary>
    /// Apply skeleton transforms following Brio's exact pattern:
    /// 1. Apply transforms with per-bone LastTransform update
    /// 2. Full cache update after apply
    /// 3. Reparent partials
    /// 4. Full cache update after reparent
    /// </summary>
    private void ApplySkeletonTransforms(SkeletonKey slotKey, Skeleton skeleton, SkeletonPoseInfo poseInfo)
    {
        // The slot skeleton resolves its OWN native pointer; a weapon or
        // ornament stack is applied through that slot's skeleton only.
        var gameSkeleton = skeleton.GetGameSkeletonPointer();
        if (gameSkeleton == null)
            return;

        var gazeFrame = _gazeFrames.Before(skeleton);
#if DEBUG
        if (slotKey.Slot == PoseSlot.Character)
            Diagnostics.GazeEvaluationProbe.Capture(skeleton.Actor.Address, "pose-before");
#endif
        // STEP 1: Apply transforms AND update LastTransform per-bone (like Brio ApplyBrioTransforms)
        _transitive.TryGet(slotKey, out var actions);
        ApplyTransformsWithPerBoneUpdate(
            slotKey,
            skeleton,
            gameSkeleton,
            poseInfo,
            actions);
        _ik.ApplyFabrikControls(slotKey, skeleton);
        // Brio's pass has no such flag: every skeleton it registers is
        // visited every frame, so a registered action always runs. Poser
        // records the fact so a dropped batch is distinguishable from an
        // executed one at interval end.
        if (actions != null)
            actions.Executed = true;

        // STEP 2: Full cache update after apply (like Brio line 242)
        UpdateAllLastTransforms(skeleton, gameSkeleton);

        // STEP 3: Reparent partials (like Brio line 243)
        ReparentPartials(skeleton, gameSkeleton);

        // STEP 4: Full cache update after reparent (like Brio line 244)
        UpdateAllLastTransforms(skeleton, gameSkeleton);
        if (gazeFrame is { } captured) _gazeFrames.After(captured);
#if DEBUG
        if (slotKey.Slot == PoseSlot.Character)
            Diagnostics.GazeEvaluationProbe.Capture(skeleton.Actor.Address, "pose-after");
#endif
    }

    /// <summary>
    /// Apply transforms with per-bone LastTransform update - exactly like Brio's ApplyBrioTransforms.
    /// Updates LastTransform IMMEDIATELY after applying each bone's stacks.
    /// </summary>
    private void ApplyTransformsWithPerBoneUpdate(
        SkeletonKey slotKey,
        Skeleton skeleton,
        GameSkeleton* gameSkeleton,
        SkeletonPoseInfo poseInfo,
        TransitiveActionSet? actions)
    {
        var partialCount = gameSkeleton->PartialSkeletonCount;

        for (int partialIdx = 0; partialIdx < partialCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null)
                continue;

            var boneMap = skeleton.GetNativeBoneMap(partialIdx, pose);
            var boneCount = pose->Skeleton->Bones.Length;
            // Capture the attachment map before this partial's edits run.
            // Its parent partial has already been posed in this pass.
            for (int rootIndex = 0; rootIndex < boneCount; rootIndex++)
            {
                var root = ResolveNativeBone(skeleton, boneMap, pose, partialIdx, rootIndex);
                if (root is not { IsPartialRoot: true, IsSkeletonRoot: false, ParentBone: { } parent })
                    continue;
                var frameKey = (slotKey, partialIdx, rootIndex);
                _stacks.ForgetPartialFrame(frameKey);
                var rootSpace = pose->AccessBoneModelSpace(rootIndex, hkaPose.PropagateOrNot.DontPropagate);
                var parentPose = gameSkeleton->PartialSkeletons[parent.PartialId].GetHavokPose(0);
                var parentSpace = parentPose == null ? null : parentPose->AccessBoneModelSpace(
                    parent.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);
                if (rootSpace == null || parentSpace == null)
                    continue;
                var attached = ReadTransform(parentSpace);
                if (root.PartialRootScale is { } scale)
                    attached.Scale = scale;
                var frame = new Poser.Game.Posing.PartialPoseFrame(ReadTransform(rootSpace), attached);
                if (frame.IsInvertible)
                    _stacks.SetPartialFrame(frameKey, frame);
            }
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var bone = ResolveNativeBone(skeleton, boneMap, pose, partialIdx, boneIdx);
                if (bone == null)
                    continue;

                // The resolved bone's name IS the native name this index would
                // have marshaled (it was resolved BY that name on both paths),
                // so the pose store is keyed identically without the marshal.
                var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, partialIdx);
                _ik.TryGetChain(
                    (slotKey, partialIdx, boneIdx), out var chainState);
                if (chainState?.Config is { Solver: IkSolver.Fabrik or IkSolver.Rope })
                    chainState = null; // Authored spans solve once around the selected handle, after pose layers.
                // Import deltas must be measured against the ordinary pose,
                // before IK moves parents underneath the remaining file bones.
                if (_ik.IsImportSuppressed(slotKey.Actor))
                    chainState = null;
                bool fixedHold = chainState is
                {
                    Config.Enabled: true,
                    HeldCapture: not null,
                };
                // Brio visits every bone unconditionally (SkeletonService.cs:98-127)
                // because a transitive action may append a stack to a bone
                // that has none. With no batch registered the pass keeps
                // Poser's cheap skip; with one, every bone is visited.
                if (!bonePoseInfo.HasStacks && !fixedHold && actions == null)
                    continue;

                var baselineSpace = pose->AccessBoneModelSpace(
                    boneIdx,
                    hkaPose.PropagateOrNot.DontPropagate);
                if (baselineSpace == null)
                    continue;
                var animatedBaseline = ReadTransform(baselineSpace);

                // Brio SkeletonService.cs:108 — the stack count taken BEFORE
                // the existing stacks are applied. Everything past it is what
                // the transitive actions appended, and only those are applied
                // a second time below.
                var snapshotCount = bonePoseInfo.Stacks.Count;

                if (bonePoseInfo.HasStacks)
                {
                    // Apply ALL stacks for this bone (like Brio lines 108-112)
                    foreach (var stack in bonePoseInfo.Stacks)
                    {
                        ApplyBoneTransform(pose, boneIdx,
                            HeldPoseStack(stack, fixedHold), bone, fixedHold ? null : chainState);
                    }
                }
                if (fixedHold)
                {
                    // An armed Fixed chain with no authored stack still holds
                    // its captured target against the running animation.
                    ApplyFixedHold(pose, boneIdx, bone, chainState!, bonePoseInfo.IkModification());
                }

                // Brio captures both caches immediately after applying each bone.
                var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
                if (modelSpace != null)
                {
                    var transform = ReadTransform(modelSpace);
                    bone.LastRawTransform = transform;
                    bone.LastTransform = transform;
                    if (bonePoseInfo.HasStacks || fixedHold)
                    {
                        _stacks.SetAnimatedBaseline(
                            (slotKey, partialIdx, boneIdx),
                            animatedBaseline);
                    }

                    // Brio SkeletonService.cs:119-127: the actions run against
                    // the caches this pass has just refreshed — the running,
                    // post-parent basis an absolute write must be diffed
                    // against — and whatever they appended is applied here,
                    // in this bone's turn, before the loop moves to its
                    // children.
                    if (actions != null)
                    {
                        TransitiveActionScheduler.ExecuteTransitiveActions(actions, bone, bonePoseInfo);
                        for (var i = snapshotCount; i < bonePoseInfo.Stacks.Count; i++)
                            ApplyBoneTransform(
                                pose, boneIdx, HeldPoseStack(bonePoseInfo.Stacks[i], fixedHold),
                                bone, fixedHold ? null : chainState);
                        if (fixedHold && bonePoseInfo.Stacks.Count != snapshotCount)
                            ApplyFixedHold(pose, boneIdx, bone, chainState!, bonePoseInfo.IkModification());
                    }
                }
            }
        }
    }

    /// <summary>
    /// Resolves the SAME managed bone the name path resolves for a native bone,
    /// without marshaling anything while the skeleton's prebuilt map still
    /// describes this pose. <paramref name="map"/> is handed out by
    /// <see cref="Skeleton.GetNativeBoneMap"/> only after the native
    /// <c>hkaSkeleton</c> pointer and bone count have been matched against the
    /// build, so an invalid handle — a rebound partial, a partial the build
    /// never saw, a resized bone array — falls back to marshaling the native
    /// name and asking <see cref="Skeleton.GetBoneByName"/>, exactly as before.
    /// </summary>
    internal static Bone? ResolveNativeBone(
        Skeleton skeleton,
        Skeleton.NativeBoneMap map,
        hkaPose* pose,
        int partialIdx,
        int boneIdx)
    {
        if (map.IsValid)
            return map[boneIdx];

        var rawBone = pose->Skeleton->Bones[boneIdx];
        var boneName = rawBone.Name.String ?? $"bone_{partialIdx}_{boneIdx}";
        return skeleton.GetBoneByName(boneName, partialIdx);
    }

    /// <summary>Refreshes both transform caches at the same two points as Brio:
    /// after applying stacks and after partial reparenting.</summary>
    private void UpdateAllLastTransforms(Skeleton skeleton, GameSkeleton* gameSkeleton)
    {
        var partialCount = gameSkeleton->PartialSkeletonCount;

        for (int partialIdx = 0; partialIdx < partialCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null)
                continue;

            var boneMap = skeleton.GetNativeBoneMap(partialIdx, pose);
            // The fallback path resolves EVERY bone BY NAME, allocating a
            // managed copy of the native name per bone per frame — the
            // third-of-a-core in the profile if it is what actually runs.
            // One line per build says which path this partial is on.
            if (!boneMap.IsValid && _mapFallbackLogged.Add(
                    (skeleton.BuildRevision, partialIdx)))
                _log.Debug(
                    $"Bone snapshot FALLBACK for {skeleton.Actor.Name} " +
                    $"{skeleton.Slot} partial {partialIdx} rev " +
                    $"{skeleton.BuildRevision}: the native map failed " +
                    "validation; resolving " +
                    $"{pose->Skeleton->Bones.Length} bones by name each frame.");
            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var bone = ResolveNativeBone(skeleton, boneMap, pose, partialIdx, boneIdx);
                if (bone == null)
                    continue;

                var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
                if (modelSpace != null)
                {
                    var transform = new Transform
                    {
                        Position = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z),
                        Rotation = new Quaternion(modelSpace->Rotation.X, modelSpace->Rotation.Y, modelSpace->Rotation.Z, modelSpace->Rotation.W),
                        Scale = new Vector3(modelSpace->Scale.X, modelSpace->Scale.Y, modelSpace->Scale.Z)
                    };
                    bone.LastRawTransform = transform;
                    bone.LastTransform = transform;
                }
            }
        }
    }

    private void ReparentPartials(Skeleton skeleton, GameSkeleton* gameSkeleton)
    {
        var partialCount = gameSkeleton->PartialSkeletonCount;

        for (int partialIdx = 0; partialIdx < partialCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null)
                continue;

            var boneMap = skeleton.GetNativeBoneMap(partialIdx, pose);
            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var bone = ResolveNativeBone(skeleton, boneMap, pose, partialIdx, boneIdx);
                if (bone == null)
                    continue;

                if (bone.IsPartialRoot && !bone.IsSkeletonRoot)
                {
                    // Brio performs this access for EVERY partial root and
                    // only afterwards checks whether a parent exists (Brio
                    // SkeletonService.cs:152-153): AccessBoneModelSpace with
                    // Propagate natively syncs the root's model-space entry
                    // and invalidates its descendants, a side effect that
                    // must happen even when no parent transform is written.
                    var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.Propagate);
                    // The Propagate side effect above is the point of the call
                    // and has already happened; the null check only gates the
                    // WRITE below, matching every other model-space deref in
                    // this file (check-then-use, never fail-open).
                    if (modelSpace == null)
                        continue;

                    var parentBone = bone.ParentBone;
                    if (parentBone == null)
                        continue;
                    var parentPartial = &gameSkeleton->PartialSkeletons[parentBone.PartialId];
                    var parentPose = parentPartial->GetHavokPose(0);

                    Vector3 pos;
                    Quaternion rot;
                    Vector3 scale;

                    // A pose that exists but cannot hand back the parent's
                    // model-space entry falls through to the cached transform
                    // arm rather than dereferencing null.
                    var parentModelSpace = parentPose == null
                        ? null
                        : parentPose->AccessBoneModelSpace(parentBone.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);

                    if (parentModelSpace != null)
                    {
                        pos = new Vector3(parentModelSpace->Translation.X, parentModelSpace->Translation.Y, parentModelSpace->Translation.Z);
                        rot = new Quaternion(parentModelSpace->Rotation.X, parentModelSpace->Rotation.Y, parentModelSpace->Rotation.Z, parentModelSpace->Rotation.W);
                        scale = new Vector3(parentModelSpace->Scale.X, parentModelSpace->Scale.Y, parentModelSpace->Scale.Z);
                    }
                    else
                    {
                        var parent = parentBone.LastTransform;
                        pos = parent.Position;
                        rot = parent.Rotation;
                        scale = parent.Scale;
                    }

                    // An owned root scale (a duplicate's captured head
                    // scaling) stands in for the parent's.
                    if (bone.PartialRootScale is { } owned)
                        scale = owned;
                    modelSpace->Translation = *(hkVector4f*)(&pos);
                    modelSpace->Rotation = *(hkQuaternionf*)(&rot);
                    modelSpace->Scale = *(hkVector4f*)(&scale);
                }
            }
        }
    }

    // A handle edit supplies the solver target, not a direct translation of
    // the tip beforehand. Directly moving it first changes the limb lengths
    // and can leave Two Joint solving an already-displaced endpoint.
    internal static BonePoseTransformInfo HeldPoseStack(BonePoseTransformInfo stack, bool held) =>
        held && stack.IkTransform == null && stack.Layer == null
            ? stack with { Transform = stack.Transform with { Position = Vector3.Zero } }
            : stack;

    /// <summary>Solve the held target after applying the pose-only stack values.</summary>
    private void ApplyFixedHold(hkaPose* pose, int boneIdx, IBone bone, IkChainState ik, Transform authored)
    {
        if (_ik.Held.ResolveHeld(ik, bone, authored.Position, authored.Rotation, pose) is not { } held)
            return;
        var target = held.Position;
        var rotSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
        var currentRotation = new Quaternion(
            rotSpace->Rotation.X, rotSpace->Rotation.Y,
            rotSpace->Rotation.Z, rotSpace->Rotation.W);
        bool holdRotation = ik.Config.HoldsEndRotation;
        _ikService.Solve(bone, new Poser.Domain.Posing.IkSolveRequest(
            target, holdRotation ? held.Rotation : currentRotation, ik.Config, ik.Chain));
        if (!ik.Config.EnforceConstraints)
        {
            var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.Propagate);
            modelSpace->Translation = *(hkVector4f*)(&target);
        }
        if (holdRotation)
        {
            // The tip keeps the held rotation too; its children ride along.
            var heldSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.Propagate);
            var heldRotation = held.Rotation;
            heldSpace->Rotation = *(hkQuaternionf*)(&heldRotation);
        }
    }

    private void ApplyBoneTransform(hkaPose* pose, int boneIdx, BonePoseTransformInfo info, IBone bone, IkChainState? ik)
    {
        // Delta mode: ADD to Havok state (like Brio)

        // Older action units use the partial root; Ktisis 847f3673 samples
        // parent-local deltas. Resolve that parent from THIS Havok pass, not
        // a last-frame cache that already contains the authored expression.
        bool relative = info.Frame != TransformFrame.BoneLocal;
        var headRotation = Quaternion.Identity;
        if (relative)
        {
            int frameIndex = info.Frame == TransformFrame.ParentRelative
                ? pose->Skeleton->ParentIndices[boneIdx] : 0;
            var rootSpace = frameIndex >= 0
                ? pose->AccessBoneModelSpace(frameIndex, hkaPose.PropagateOrNot.DontPropagate) : null;
            if (rootSpace != null)
                headRotation = new Quaternion(rootSpace->Rotation.X, rootSpace->Rotation.Y, rootSpace->Rotation.Z, rootSpace->Rotation.W);
        }
        var framedDelta = info.Frame == TransformFrame.ParentRelative
            ? PoseMath.ProjectExpressionDelta(info.Transform, headRotation) : info.Transform;

        // Position
        var prop = info.PropagateComponents.HasFlag(TransformComponents.Position);
        var modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforePos = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z);
        var positionDelta = info.Frame == TransformFrame.HeadRelative
            ? Vector3.Transform(info.Transform.Position, headRotation) : framedDelta.Position;
        var tempPos = beforePos + positionDelta;
        bool armed = ik is { Config.Enabled: true } && info.IkTransform == null;
        bool fixedMode = armed && ik!.HeldCapture != null;
        bool rotationEnforcedByIk = false;
        Quaternion? heldRotation = null;
        if (armed && (fixedMode || info.Transform.Position != Vector3.Zero))
        {
            // Brio-style live IK: the stored delta is the TARGET offset; the
            // chain is solved every frame, so undo/redo stay pure delta
            // operations. Fixed mode targets the captured model-space point
            // shifted by the authored translation moved since capture, so
            // mode changes never jump or double-apply an existing edit.
            var target = tempPos;

            // Requested end rotation, computed BEFORE the solve so optional
            // enforcement receives the value the direct apply would produce.
            var rotSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
            var rotBefore = new Quaternion(
                rotSpace->Rotation.X, rotSpace->Rotation.Y,
                rotSpace->Rotation.Z, rotSpace->Rotation.W);
            var requestedRotation = info.Frame == TransformFrame.HeadRelative
                ? Quaternion.Normalize(headRotation * info.Transform.Rotation * Quaternion.Inverse(headRotation) * rotBefore)
                : Quaternion.Normalize(rotBefore * info.Transform.Rotation);

            // A held target brings its own rotation when the chain holds
            // rotation: the solver aims at it and the write below keeps it.
            if (fixedMode
                && _ik.Held.ResolveHeld(ik!, bone, info.Transform.Position, info.Transform.Rotation, pose)
                    is { } held)
            {
                target = held.Position;
                if (ik.Config.HoldsEndRotation)
                {
                    requestedRotation = held.Rotation;
                    heldRotation = held.Rotation;
                }
            }

            _ikService.Solve(bone, new Poser.Domain.Posing.IkSolveRequest(
                target, requestedRotation, ik!.Config, ik.Chain));
            // When the solver enforces the end rotation, it is not applied a
            // second time below.
            rotationEnforcedByIk =
                ik.Config.Solver == Poser.Domain.Posing.IkSolver.TwoJoint &&
                ik.Config.EnforceEndRotation;
            if (!ik.Config.EnforceConstraints)
            {
                modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
                modelSpace->Translation = *(hkVector4f*)(&target);
            }
        }
        else
        {
            modelSpace->Translation = *(hkVector4f*)(&tempPos);
        }

        // Rotation (skipped when the Two Joint solver enforced it)
        if (!rotationEnforcedByIk)
        {
            prop = info.PropagateComponents.HasFlag(TransformComponents.Rotation);
            modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
            // A zero basis takes the delta as its whole rotation — the
            // same reading the delta was taken with (BonePoseInfo.UsableBasis).
            var beforeRot = BonePoseInfo.UsableBasis(new Quaternion(
                modelSpace->Rotation.X, modelSpace->Rotation.Y, modelSpace->Rotation.Z, modelSpace->Rotation.W));
            var tempRot = info.Frame == TransformFrame.HeadRelative
                ? Quaternion.Normalize(headRotation * info.Transform.Rotation * Quaternion.Inverse(headRotation) * beforeRot)
                : Quaternion.Normalize(beforeRot * info.Transform.Rotation);
            if (heldRotation is { } keep)
                tempRot = keep;
            modelSpace->Rotation = *(hkQuaternionf*)(&tempRot);
        }

        // Scale
        prop = info.PropagateComponents.HasFlag(TransformComponents.Scale);
        modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforeScale = new Vector3(modelSpace->Scale.X, modelSpace->Scale.Y, modelSpace->Scale.Z);
        var tempScale = info.Frame == TransformFrame.ParentRelative
            ? beforeScale * (Vector3.One + info.Transform.Scale)
            : beforeScale + info.Transform.Scale;
        modelSpace->Scale = *(hkVector4f*)(&tempScale);
    }

    /// <summary>Indexed scan, not foreach: <c>Actors</c> is an interface-typed
    /// list, so foreach boxes an enumerator on every call and these callers run
    /// per posed skeleton per frame inside the detours.</summary>
    internal static IActor? FindActor(IActorManager actorManager, string actorId)
    {
        var actors = actorManager.Actors;
        for (var i = 0; i < actors.Count; i++)
        {
            if (actors[i].Id.Unique == actorId)
                return actors[i];
        }
        // The CharaView preview body poses through the same apply pass; a miss
        // here purges its pose state on the very next frame.
        var auxiliary = actorManager.AuxiliaryActors;
        for (var i = 0; i < auxiliary.Count; i++)
        {
            if (auxiliary[i].Id.Unique == actorId)
                return auxiliary[i];
        }
        return null;
    }

    internal static Transform ReadTransform(hkQsTransformf* transform) =>
        new()
        {
            Position = new Vector3(
                transform->Translation.X,
                transform->Translation.Y,
                transform->Translation.Z),
            Rotation = new Quaternion(
                transform->Rotation.X,
                transform->Rotation.Y,
                transform->Rotation.Z,
                transform->Rotation.W),
            Scale = new Vector3(
                transform->Scale.X,
                transform->Scale.Y,
                transform->Scale.Z),
        };

    public void Dispose() => _updateBonePhysicsHook?.Dispose();
}
