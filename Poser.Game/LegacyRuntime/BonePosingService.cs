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
using Poser.Entities;
using Poser.Domain.Identity;
using Poser.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Service for manipulating bone transforms using game hooks.
/// Simple delta-based system like Brio - bones rotate around themselves.
/// </summary>
public unsafe class BonePosingService : IBonePosingService
{
    private readonly IPluginLog _log;
    private readonly IFramework _framework;
    private readonly IGPoseService _gPoseService;
    private readonly ISkeletonService _skeletonService;
    private readonly IActorManager _actorManager;
    private readonly IEventBus _eventBus;
    private readonly IIKService _ikService;

    // Hook for intercepting bone physics updates
    private delegate nint UpdateBonePhysicsDelegate(nint a1);
    private readonly Hook<UpdateBonePhysicsDelegate>? _updateBonePhysicsHook;

    // Hook for finalizing skeletons before rendering (takes final snapshot)
    private delegate void FinalizeSkeletonsDelegate(nint a1);
    private readonly Hook<FinalizeSkeletonsDelegate>? _finalizeSkeletonsHook;

    /// <summary>
    /// EXACT runtime skeleton identity: actor address, slot, and the built
    /// skeleton instance's unique id. Replacing a slot creates a new
    /// skeleton instance, so the replacement can never inherit the old
    /// skeleton's pose state through a reused (address, slot) pair.
    /// </summary>
    private readonly record struct SkeletonKey(
        nint Actor,
        PoseSlot Slot,
        string Skeleton)
    {
        public static SkeletonKey Of(ISkeleton skeleton) => new(
            skeleton.Actor.Address,
            skeleton.Slot,
            skeleton.Id.Unique);
    }

    // Pose info per exact slot-skeleton instance.
    private readonly Dictionary<SkeletonKey, SkeletonPoseInfo> _poseInfos = new();

    // Track which slot skeletons need updating this frame (have modifications)
    private readonly HashSet<SkeletonKey> _skeletonsToUpdate = new();

    // Track which slot skeletons need cache updates (visible overlays, active gizmo, etc.)
    private readonly HashSet<SkeletonKey> _skeletonsToUpdateCache = new();

    /// <summary>Session IK state per exact endpoint: validated chain
    /// configuration, resolved native chain, and the Fixed-mode capture.
    /// Keyed by the exact skeleton instance, so a replacement never
    /// inherits configuration or targets.</summary>
    private sealed class IkChainState
    {
        public required Poser.Domain.Posing.IkChainConfig Config;
        public Poser.Domain.Posing.IkResolvedChain Chain;
        public (Vector3 Target, Vector3 Translation)? FixedCapture;
    }

    private readonly Dictionary<(SkeletonKey Skeleton, int Partial, int Bone), IkChainState>
        _ikChains = new();

    // Native-boundary observations used by the live acceptance harness.
    private readonly Dictionary<(SkeletonKey Skeleton, int Partial, int Bone), BoneEvaluationObservation>
        _evaluationObservations = new();
    private long _evaluationSequence;

    private bool _isUpdating = false;

    public BonePosingService(
        IPluginLog log,
        IFramework framework,
        IGPoseService gPoseService,
        ISkeletonService skeletonService,
        IActorManager actorManager,
        IEventBus eventBus,
        IIKService ikService,
        IGameInteropProvider hooking,
        ISigScanner scanner)
    {
        _log = log;
        _framework = framework;
        _gPoseService = gPoseService;
        _skeletonService = skeletonService;
        _actorManager = actorManager;
        _eventBus = eventBus;
        _ikService = ikService;

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

        // Hook FinalizeSkeletons - called before rendering, takes final snapshot
        try
        {
            var finalizeSkeletonsAddress = scanner.ScanText("40 53 57 41 54 41 55 48 83 EC ?? ?? 48 ?? ?? ?? ?? ?? ?? ?? 4C") /* Brio 0.8 sig; JMP in Framework.TaskRenderGraphicsRender */;
            _finalizeSkeletonsHook = hooking.HookFromAddress<FinalizeSkeletonsDelegate>(finalizeSkeletonsAddress, FinalizeSkeletonsDetour);
            _finalizeSkeletonsHook.Enable();
            _log.Debug("BonePosingService: FinalizeSkeletons hook initialized");
        }
        catch (Exception ex)
        {
            _log.Warning($"BonePosingService: Failed to hook FinalizeSkeletons: {ex.Message}");
        }

        _framework.Update += OnFrameworkUpdate;
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);

        _log.Debug("BonePosingService initialized");
    }

    private nint UpdateBonePhysicsDetour(nint a1)
    {
        var result = _updateBonePhysicsHook!.Original(a1);

        if (!_gPoseService.IsGPosing || _isUpdating)
            return result;

        _isUpdating = true;
        try
        {
            _evaluationSequence++;
            ApplyAllBoneTransforms();
        }
        finally
        {
            _isUpdating = false;
        }

        return result;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        _skeletonsToUpdate.Clear();
        foreach (var (slotKey, poseInfo) in _poseInfos)
        {
            if (poseInfo.IsOverridden)
            {
                _skeletonsToUpdate.Add(slotKey);
                continue;
            }

            RemoveEvaluationObservations(slotKey);
        }

        _skeletonsToUpdateCache.Clear();
    }

    public void RegisterSkeletonForCacheUpdate(ISkeleton skeleton)
    {
        _skeletonsToUpdateCache.Add(SkeletonKey.Of(skeleton));
    }

    /// <summary>The frozen animated/reference baseline beneath the authored
    /// layers; a bone without applied layers has no observation, and its
    /// current transform IS its baseline.</summary>
    public Transform GetAnimatedBaseline(IBone bone) =>
        TryGetEvaluationObservation(bone, out var observation)
            ? observation.AnimatedBaseline
            : bone.LastTransform;

    public bool TryGetEvaluationObservation(
        IBone bone,
        out BoneEvaluationObservation observation)
    {
        if (bone is VirtualBone)
        {
            observation = default;
            return false;
        }

        return _evaluationObservations.TryGetValue(
            (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex),
            out observation);
    }

    /// <summary>Actor teardown: purge every runtime pose store belonging to
    /// an address that no longer hosts a live actor.</summary>
    private void OnActorListChanged(ActorListChangedEvent e)
    {
        var live = new HashSet<nint>();
        foreach (var actor in e.Actors)
            live.Add(actor.Address);
        foreach (var key in _poseInfos.Keys.Where(key => !live.Contains(key.Actor)).ToArray())
            PurgeSkeletonState(key);
    }

    /// <summary>Removes one exact skeleton instance's pose state, update
    /// registrations, observations, and IK chain state (a replacement never
    /// inherits configuration or fixed targets).</summary>
    private void PurgeSkeletonState(SkeletonKey key)
    {
        _poseInfos.Remove(key);
        _skeletonsToUpdate.Remove(key);
        _skeletonsToUpdateCache.Remove(key);
        RemoveEvaluationObservations(key);
        foreach (var chainKey in _ikChains.Keys
                     .Where(chainKey => chainKey.Skeleton == key)
                     .ToArray())
            _ikChains.Remove(chainKey);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            _poseInfos.Clear();
            _skeletonsToUpdate.Clear();
            _evaluationObservations.Clear();
            _ikChains.Clear();
        }
    }

    private void ApplyAllBoneTransforms()
    {
        foreach (var slotKey in _skeletonsToUpdate.ToArray())
        {
            if (!_poseInfos.TryGetValue(slotKey, out var poseInfo))
                continue;

            IActor? actor = null;
            foreach (var a in _actorManager.Actors)
            {
                if (a.Address == slotKey.Actor)
                {
                    actor = a;
                    break;
                }
            }

            if (actor == null)
            {
                PurgeSkeletonState(slotKey);
                continue;
            }

            var skeleton = _skeletonService.GetSkeleton(actor, slotKey.Slot) as Skeleton;
            if (skeleton == null || !skeleton.IsValid ||
                skeleton.Id.Unique != slotKey.Skeleton)
            {
                // The slot vanished or was REPLACED: the stored pose belongs
                // to the old skeleton instance and must never be applied to
                // its replacement.
                PurgeSkeletonState(slotKey);
                continue;
            }

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

        // STEP 1: Apply transforms AND update LastTransform per-bone (like Brio ApplyBrioTransforms)
        ApplyTransformsWithPerBoneUpdate(
            slotKey,
            skeleton,
            gameSkeleton,
            poseInfo);

        // STEP 2: Full cache update after apply (like Brio line 242)
        UpdateAllLastTransforms(skeleton, gameSkeleton);

        // STEP 3: Reparent partials (like Brio line 243)
        ReparentPartials(skeleton, gameSkeleton);

        // STEP 4: Full cache update after reparent (like Brio line 244)
        UpdateAllLastTransforms(skeleton, gameSkeleton);
    }

    /// <summary>
    /// Apply transforms with per-bone LastTransform update - exactly like Brio's ApplyBrioTransforms.
    /// Updates LastTransform IMMEDIATELY after applying each bone's stacks.
    /// </summary>
    private void ApplyTransformsWithPerBoneUpdate(
        SkeletonKey slotKey,
        Skeleton skeleton,
        GameSkeleton* gameSkeleton,
        SkeletonPoseInfo poseInfo)
    {
        var partialCount = gameSkeleton->PartialSkeletonCount;

        for (int partialIdx = 0; partialIdx < partialCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null)
                continue;

            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var rawBone = pose->Skeleton->Bones[boneIdx];
                var boneName = rawBone.Name.String ?? $"bone_{partialIdx}_{boneIdx}";

                var bone = skeleton.GetBoneByName(boneName, partialIdx);
                if (bone == null)
                    continue;

                var bonePoseInfo = poseInfo.GetPoseInfo(boneName, partialIdx);
                if (!bonePoseInfo.HasStacks)
                    continue;

                var baselineSpace = pose->AccessBoneModelSpace(
                    boneIdx,
                    hkaPose.PropagateOrNot.DontPropagate);
                if (baselineSpace == null)
                    continue;
                var animatedBaseline = ReadTransform(baselineSpace);

                // Apply ALL stacks for this bone (like Brio lines 108-112)
                foreach (var stack in bonePoseInfo.Stacks)
                {
                    ApplyBoneTransform(pose, boneIdx, stack, bone, bonePoseInfo.IK);
                }

                // Brio captures both caches immediately after applying each bone.
                var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
                if (modelSpace != null)
                {
                    var transform = ReadTransform(modelSpace);
                    bone.LastRawTransform = transform;
                    bone.LastTransform = transform;
                    _evaluationObservations[
                        (slotKey, partialIdx, boneIdx)] =
                        new BoneEvaluationObservation(
                            _evaluationSequence,
                            animatedBaseline,
                            transform,
                            Combine(bonePoseInfo.Stacks),
                            bonePoseInfo.Stacks.Count);
                }
            }
        }
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

            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var rawBone = pose->Skeleton->Bones[boneIdx];
                var boneName = rawBone.Name.String ?? $"bone_{partialIdx}_{boneIdx}";

                var bone = skeleton.GetBoneByName(boneName, partialIdx);
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

            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var rawBone = pose->Skeleton->Bones[boneIdx];
                var boneName = rawBone.Name.String ?? $"bone_{partialIdx}_{boneIdx}";

                var bone = skeleton.GetBoneByName(boneName, partialIdx);
                if (bone == null)
                    continue;

                if (bone.IsPartialRoot && !bone.IsSkeletonRoot && bone.ParentBone != null)
                {
                    var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.Propagate);

                    var parentBone = bone.ParentBone;
                    var parentPartial = &gameSkeleton->PartialSkeletons[parentBone.PartialId];
                    var parentPose = parentPartial->GetHavokPose(0);

                    Vector3 pos;
                    Quaternion rot;
                    Vector3 scale;

                    if (parentPose != null)
                    {
                        var parentModelSpace = parentPose->AccessBoneModelSpace(parentBone.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);
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

                    modelSpace->Translation = *(hkVector4f*)(&pos);
                    modelSpace->Rotation = *(hkQuaternionf*)(&rot);
                    modelSpace->Scale = *(hkVector4f*)(&scale);
                }
            }
        }
    }

    private void ApplyBoneTransform(hkaPose* pose, int boneIdx, BonePoseTransformInfo info, IBone bone, BoneIKInfo ik)
    {
        // Delta mode: ADD to Havok state (like Brio)

        // Ktisis v0.4 action-unit deltas are authored with their axes fixed in
        // the bone's partial-root ("head") frame, not the bone's own frame.
        // Rotation applies pre-multiplied conjugated by the head rotation and
        // the position delta rotates by the head rotation before the model
        // add. Applying them bone-locally is exactly the defect that made
        // Blink open the eyes and Pucker shove the mouth sideways.
        var headRotation = Quaternion.Identity;
        if (info.Frame == TransformFrame.HeadRelative)
        {
            var rootSpace = pose->AccessBoneModelSpace(0, hkaPose.PropagateOrNot.DontPropagate);
            if (rootSpace != null)
                headRotation = new Quaternion(rootSpace->Rotation.X, rootSpace->Rotation.Y, rootSpace->Rotation.Z, rootSpace->Rotation.W);
        }

        // Position
        var prop = info.PropagateComponents.HasFlag(TransformComponents.Position);
        var modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforePos = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z);
        var positionDelta = info.Frame == TransformFrame.HeadRelative
            ? Vector3.Transform(info.Transform.Position, headRotation)
            : info.Transform.Position;
        var tempPos = beforePos + positionDelta;
        if (ik.Enabled && info.Transform.Position != Vector3.Zero)
        {
            // Brio-style live IK: the stored delta is the TARGET offset; the chain is
            // solved every frame, so undo/redo stay pure delta operations.
            _ikService.SolveIK(bone, tempPos, ik);
            if (!ik.EnforceConstraints)
            {
                modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
                modelSpace->Translation = *(hkVector4f*)(&tempPos);
            }
        }
        else
        {
            modelSpace->Translation = *(hkVector4f*)(&tempPos);
        }

        // Rotation
        prop = info.PropagateComponents.HasFlag(TransformComponents.Rotation);
        modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforeRot = new Quaternion(modelSpace->Rotation.X, modelSpace->Rotation.Y, modelSpace->Rotation.Z, modelSpace->Rotation.W);
        var tempRot = info.Frame == TransformFrame.HeadRelative
            ? Quaternion.Normalize(
                headRotation * info.Transform.Rotation *
                Quaternion.Inverse(headRotation) * beforeRot)
            : beforeRot * info.Transform.Rotation;
        modelSpace->Rotation = *(hkQuaternionf*)(&tempRot);

        // Scale
        prop = info.PropagateComponents.HasFlag(TransformComponents.Scale);
        modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforeScale = new Vector3(modelSpace->Scale.X, modelSpace->Scale.Y, modelSpace->Scale.Z);
        var tempScale = beforeScale + info.Transform.Scale;
        modelSpace->Scale = *(hkVector4f*)(&tempScale);
    }

    public SkeletonPoseInfo GetPoseInfo(ISkeleton skeleton)
    {
        var slotKey = SkeletonKey.Of(skeleton);
        if (!_poseInfos.TryGetValue(slotKey, out var poseInfo))
        {
            // A replaced slot skeleton gets a FRESH store; any state still
            // keyed to the old instance of the same (actor, slot) is purged.
            foreach (var stale in _poseInfos.Keys
                         .Where(key => key.Actor == slotKey.Actor &&
                                       key.Slot == slotKey.Slot)
                         .ToArray())
                PurgeSkeletonState(stale);
            poseInfo = new SkeletonPoseInfo();
            _poseInfos[slotKey] = poseInfo;
        }
        return poseInfo;
    }

    public bool LinkedBonesEnabled { get; set; } = true;

    private bool _propagatingLinks;

    public void ApplyTransform(IBone bone, Transform newTransform, Transform originalTransform)
    {
        if (bone is VirtualBone)
            return;

        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        bonePoseInfo.Apply(newTransform, originalTransform);

        // Linked bones (Anamnesis parity): transfer the SAME delta to the rest
        // of the link set. Re-entrancy guard stops link chains from ping-ponging.
        if (LinkedBonesEnabled && !_propagatingLinks)
        {
            var links = LinkedBones.GetLinks(bone.BoneName);
            if (links.Count > 0)
            {
                var delta = BonePoseInfo.Diff(newTransform, originalTransform);
                _propagatingLinks = true;
                try
                {
                    foreach (var linkName in links)
                    {
                        var linked = bone.Skeleton.Bones.FirstOrDefault(
                            candidate => candidate.BoneName == linkName &&
                                         candidate.PartialId == bone.PartialId);
                        if (linked == null || linked == bone)
                            continue;

                        var linkedCurrent = linked.LastTransform;
                        var linkedNew = new Transform
                        {
                            Position = linkedCurrent.Position + delta.Position,
                            Rotation = System.Numerics.Quaternion.Normalize(linkedCurrent.Rotation * delta.Rotation),
                            Scale = linkedCurrent.Scale + delta.Scale,
                        };
                        ApplyTransform(linked, linkedNew, linkedCurrent);
                    }
                }
                finally
                {
                    _propagatingLinks = false;
                }
            }
        }

        _eventBus.Publish(new BoneTransformChangedEvent(bone));
    }

    public Poser.Domain.Posing.IkChainConfig? GetIkConfiguration(IBone bone)
    {
        if (bone is VirtualBone)
            return null;
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        if (definition == null)
            return null;
        var key = ChainKey(bone);
        return _ikChains.TryGetValue(key, out var state)
            ? state.Config
            : Poser.Domain.Posing.IkChainConfig.DefaultsFor(definition.IsArm);
    }

    public string? SetIkConfiguration(IBone bone, Poser.Domain.Posing.IkChainConfig config)
    {
        if (bone is VirtualBone)
            return "Virtual bones cannot use IK.";
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        if (definition == null)
            return $"{bone.BoneName} is not a supported IK endpoint.";
        if (config.Validate() is { } invalid)
            return invalid;
        var chain = ResolveChain(bone, definition);
        if (config.Solver == Poser.Domain.Posing.IkSolver.TwoJoint &&
            !chain.TwoJointAvailable)
            return "The Two Joint chain does not resolve on this skeleton.";

        var key = ChainKey(bone);
        _ikChains.TryGetValue(key, out var previous);
        var state = previous ?? new IkChainState { Config = config };
        state.Config = config.Normalized();
        state.Chain = chain;

        // Fixed-target lifecycle: capture on entering Fixed or enabling a
        // Fixed chain; disabling retains tuning but clears the capture.
        if (!config.Enabled)
        {
            state.FixedCapture = null;
        }
        else if (config.TargetMode == Poser.Domain.Posing.IkTargetMode.Fixed &&
                 (previous == null ||
                  previous.Config.TargetMode != Poser.Domain.Posing.IkTargetMode.Fixed ||
                  !previous.Config.Enabled ||
                  state.FixedCapture == null))
        {
            state.FixedCapture = (
                bone.LastTransform.Position,
                GetModification(bone)?.Position ?? Vector3.Zero);
        }
        else if (config.TargetMode == Poser.Domain.Posing.IkTargetMode.Relative)
        {
            state.FixedCapture = null;
        }

        _ikChains[key] = state;
        MirrorLegacyIk(bone, state.Config);
        return null;
    }

    public bool IsIkTwoJointAvailable(IBone bone)
    {
        if (bone is VirtualBone)
            return false;
        var definition = Poser.Domain.Posing.IkChains.ForEndpoint(bone.BoneName);
        return definition != null && ResolveChain(bone, definition).TwoJointAvailable;
    }

    public void ClearIkConfigurations(ISkeleton skeleton)
    {
        var key = SkeletonKey.Of(skeleton);
        foreach (var chainKey in _ikChains.Keys
                     .Where(chainKey => chainKey.Skeleton == key)
                     .ToArray())
            _ikChains.Remove(chainKey);

        // Clear the per-stack solve inputs too, including bones armed by
        // older paths. Idempotent, actor-local, no transform stacks touched.
        var poseInfo = GetPoseInfo(skeleton);
        foreach (var pose in poseInfo.AllPoses)
        {
            if (pose.IK.Enabled)
                pose.IK = BoneIKInfo.Disabled;
        }
    }

    private static (SkeletonKey Skeleton, int Partial, int Bone) ChainKey(IBone bone) =>
        (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex);

    /// <summary>Resolves the chain inside the endpoint's OWN skeleton and
    /// partial; missing optional twists resolve to native index -1 and a
    /// missing mandatory joint makes Two Joint unavailable.</summary>
    private static Poser.Domain.Posing.IkResolvedChain ResolveChain(
        IBone endpoint,
        Poser.Domain.Posing.IkChainDefinition definition)
    {
        short Index(string? name)
        {
            if (name == null)
                return -1;
            var resolved = (endpoint.Skeleton as Skeleton)?
                .GetBoneByName(name, endpoint.PartialId);
            return resolved == null ? (short)-1 : (short)resolved.BoneIndex;
        }

        return new Poser.Domain.Posing.IkResolvedChain(
            Index(definition.FirstJoint),
            Index(definition.FirstTwist),
            Index(definition.SecondJoint),
            Index(definition.SecondTwist),
            (short)endpoint.BoneIndex);
    }

    /// <summary>TRANSITIONAL (removed with the native mapping step): the
    /// per-stack solve still consumes the legacy BoneIKInfo, so the stored
    /// configuration mirrors its understood subset.</summary>
    private void MirrorLegacyIk(IBone bone, Poser.Domain.Posing.IkChainConfig config)
    {
        var legacy = BoneIKInfo.CalculateDefault(bone.BoneName);
        legacy.Enabled = config.Enabled;
        legacy.EnforceConstraints = config.EnforceConstraints;
        legacy.SolverType = config.Solver == Poser.Domain.Posing.IkSolver.Ccd
            ? IKSolverType.CCD
            : IKSolverType.TwoJoint;
        legacy.CCD.Depth = config.CcdDepth;
        legacy.CCD.Iterations = config.CcdIterations;
        var poseInfo = GetPoseInfo(bone.Skeleton);
        poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId).IK = legacy;
    }

    public int ResetRegion(ISkeleton skeleton, string region)
    {
        bool IsFace(string n) => n.StartsWith("j_f_") || n == "j_kao" || n.StartsWith("j_ago");
        bool IsHair(string n) => n.StartsWith("j_kami") || n.StartsWith("j_ex_h") || n.StartsWith("j_ex_met");

        Func<string, bool> match = region.ToLowerInvariant() switch
        {
            "face" => IsFace,
            "hair" => IsHair,
            "body" => n => !IsFace(n) && !IsHair(n),
            _ => _ => true,
        };

        int reset = 0;
        foreach (var bone in skeleton.Bones)
        {
            if (!match(bone.BoneName) || !HasModifications(bone))
                continue;
            ResetBone(bone);
            reset++;
        }
        return reset;
    }

    public bool HasEnabledIk(ISkeleton skeleton)
    {
        if (!_poseInfos.TryGetValue(SkeletonKey.Of(skeleton), out var poseInfo))
            return false;
        return poseInfo.AnyIkEnabled;
    }

    public void ResetBone(IBone bone)
    {
        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        bonePoseInfo.ClearStacks();
        _evaluationObservations.Remove(
            (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex));

        _eventBus.Publish(new BoneTransformChangedEvent(bone));
    }

    public void ResetSkeleton(ISkeleton skeleton)
    {
        var slotKey = SkeletonKey.Of(skeleton);
        if (_poseInfos.TryGetValue(slotKey, out var poseInfo))
        {
            poseInfo.Clear();
        }
        RemoveEvaluationObservations(slotKey);
    }

    public bool HasModifications(IBone bone)
    {
        if (!_poseInfos.TryGetValue(SkeletonKey.Of(bone.Skeleton), out var poseInfo))
            return false;

        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        return bonePoseInfo.HasStacks;
    }

    public Transform? GetModification(IBone bone)
    {
        if (!_poseInfos.TryGetValue(SkeletonKey.Of(bone.Skeleton), out var poseInfo))
            return null;

        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        if (!bonePoseInfo.HasStacks)
            return null;

        var combined = Transform.Zero;
        foreach (var stack in bonePoseInfo.Stacks)
        {
            combined = new Transform
            {
                Position = combined.Position + stack.Transform.Position,
                Rotation = Quaternion.Normalize(combined.Rotation * stack.Transform.Rotation),
                Scale = combined.Scale + stack.Transform.Scale
            };
        }
        return combined;
    }

    public IReadOnlyList<BonePoseTransformInfo> CapturePoseStacks(IBone bone)
    {
        if (!_poseInfos.TryGetValue(SkeletonKey.Of(bone.Skeleton), out var poseInfo))
            return Array.Empty<BonePoseTransformInfo>();

        return poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId).Stacks.ToArray();
    }

    public void RestorePoseStacks(IBone bone, IReadOnlyList<BonePoseTransformInfo> stacks)
    {
        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        if (bonePoseInfo.RestoreInteractiveStacks(stacks))
            _eventBus.Publish(new BoneTransformChangedEvent(bone));
    }

    /// <summary>
    /// FinalizeSkeletonsDetour - matches Brio's FinalizeSkeletonUpdate exactly.
    /// STEP 5: Final update for ALL modified skeletons after engine is done.
    /// </summary>
    private void FinalizeSkeletonsDetour(nint a1)
    {
        _finalizeSkeletonsHook!.Original(a1);

        if (!_gPoseService.IsGPosing)
            return;

        // STEP 5: Final update for ALL modified skeletons (like Brio line 263)
        // This takes a final snapshot now the engine is done touching skeletons.
        foreach (var slotKey in _skeletonsToUpdate)
        {
            UpdateSkeletonCache(slotKey);
        }

        // Also update overlay-only skeletons that don't have modifications
        foreach (var slotKey in _skeletonsToUpdateCache)
        {
            if (!_skeletonsToUpdate.Contains(slotKey))
            {
                UpdateSkeletonCache(slotKey);
            }
        }
    }

    private void UpdateSkeletonCache(SkeletonKey slotKey)
    {
        IActor? actor = null;
        foreach (var a in _actorManager.Actors)
        {
            if (a.Address == slotKey.Actor)
            {
                actor = a;
                break;
            }
        }

        if (actor == null)
            return;

        var skeleton = _skeletonService.GetSkeleton(actor, slotKey.Slot) as Skeleton;
        if (skeleton == null || !skeleton.IsValid ||
            skeleton.Id.Unique != slotKey.Skeleton)
            return;

        var gameSkeleton = skeleton.GetGameSkeletonPointer();
        if (gameSkeleton == null)
            return;

        for (int partialIdx = 0; partialIdx < gameSkeleton->PartialSkeletonCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null)
                continue;

            var boneCount = pose->Skeleton->Bones.Length;
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var rawBone = pose->Skeleton->Bones[boneIdx];
                var boneName = rawBone.Name.String ?? $"bone_{partialIdx}_{boneIdx}";

                var bone = skeleton.GetBoneByName(boneName, partialIdx);
                if (bone == null)
                    continue;

                var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
                if (modelSpace != null)
                {
                    bone.LastTransform = new Transform
                    {
                        Position = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z),
                        Rotation = new Quaternion(modelSpace->Rotation.X, modelSpace->Rotation.Y, modelSpace->Rotation.Z, modelSpace->Rotation.W),
                        Scale = new Vector3(modelSpace->Scale.X, modelSpace->Scale.Y, modelSpace->Scale.Z)
                    };
                }
            }
        }
    }

    public void SnapshotSkeleton(ISkeleton skeleton)
    {
        _skeletonsToUpdate.Add(SkeletonKey.Of(skeleton));
    }

    public void FlipBone(IBone bone)
    {
        if (bone is VirtualBone)
            return;

        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        // Get current rotation and convert to euler
        var currentRotation = bone.LastTransform.Rotation;
        var euler = QuaternionToEuler(currentRotation);

        // Flip: X = 180 - X, Y = -Y (matching Brio's approach)
        euler.X = 180f - euler.X;
        euler.Y = -euler.Y;

        var newRotation = EulerToQuaternion(euler);

        // Create new transform with flipped rotation
        var newTransform = new Transform
        {
            Position = bone.LastTransform.Position,
            Rotation = newRotation,
            Scale = bone.LastTransform.Scale
        };

        // Clear existing stacks and apply fresh - flip is a replacement, not an accumulation
        bonePoseInfo.ClearStacks();
        bonePoseInfo.Apply(newTransform, bone.LastRawTransform);

        _eventBus.Publish(new BoneTransformChangedEvent(bone));
    }

    public void MirrorPose(ISkeleton skeleton)
    {
        var poseInfo = GetPoseInfo(skeleton);
        var bones = new Dictionary<(string Name, int PartialId), IBone>();
        foreach (var bone in skeleton.Bones)
            bones[(bone.BoneName, bone.PartialId)] = bone;

        foreach (var bone in skeleton.Bones)
        {
            if (!bone.BoneName.EndsWith("_l", StringComparison.Ordinal))
                continue;

            var mirrorName = GetMirrorBoneName(bone.BoneName);
            if (mirrorName == null || !bones.TryGetValue((mirrorName, bone.PartialId), out var mirrorBone))
                continue;

            var leftInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
            var rightInfo = poseInfo.GetPoseInfo(mirrorBone.BoneName, mirrorBone.PartialId);
            var leftStacks = leftInfo.Stacks.ToArray();
            var rightStacks = rightInfo.Stacks.ToArray();

            leftInfo.ReplaceStacks(rightStacks.Select(MirrorStack));
            rightInfo.ReplaceStacks(leftStacks.Select(MirrorStack));

            _eventBus.Publish(new BoneTransformChangedEvent(bone));
            _eventBus.Publish(new BoneTransformChangedEvent(mirrorBone));
        }

        static BonePoseTransformInfo MirrorStack(BonePoseTransformInfo stack)
            => stack with { Transform = PoseMath.MirrorPoseDelta(stack.Transform) };
    }

    public string? GetMirrorBoneName(string boneName) => PoseMath.GetMirrorBoneName(boneName);

    private static Vector3 QuaternionToEuler(Quaternion r) => PoseMath.QuaternionToEuler(r);

    private static Quaternion EulerToQuaternion(Vector3 euler) => PoseMath.EulerToQuaternion(euler);

    private static Transform ReadTransform(hkQsTransformf* transform) =>
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

    private static Transform Combine(
        IReadOnlyList<BonePoseTransformInfo> stacks)
    {
        var combined = Transform.Zero;
        foreach (var stack in stacks)
        {
            combined = new Transform
            {
                Position = combined.Position + stack.Transform.Position,
                Rotation = Quaternion.Normalize(
                    combined.Rotation * stack.Transform.Rotation),
                Scale = combined.Scale + stack.Transform.Scale,
            };
        }
        return combined;
    }

    private void RemoveEvaluationObservations(SkeletonKey slotKey)
    {
        foreach (var key in _evaluationObservations.Keys
                     .Where(key => key.Skeleton == slotKey)
                     .ToArray())
        {
            _evaluationObservations.Remove(key);
        }
    }

    public void Dispose()
    {
        _updateBonePhysicsHook?.Dispose();
        _finalizeSkeletonsHook?.Dispose();
        _framework.Update -= OnFrameworkUpdate;
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
        _poseInfos.Clear();
        _evaluationObservations.Clear();
        GC.SuppressFinalize(this);
    }
}
