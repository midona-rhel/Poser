using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.Quaternion;
using FFXIVClientStructs.Havok.Common.Base.Math.Vector;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Service for manipulating bone transforms using game hooks.
/// Hooks into the skeleton update pipeline to apply bone pose modifications.
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

    // Pose info per skeleton (keyed by actor address)
    private readonly Dictionary<nint, SkeletonPoseInfo> _poseInfos = new();

    // Track which skeletons need updating this frame (have modifications)
    private readonly HashSet<nint> _skeletonsToUpdate = new();

    // Track which skeletons need cache updates (visible overlays, active gizmo, etc.)
    private readonly HashSet<nint> _skeletonsToUpdateCache = new();

    private bool _isUpdating = false;

    public event Action<IBone>? OnBoneTransformChanged;

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
        // Signature from Brio - MUST use the complete signature including "45 33 E4" (xor r12d, r12d) to match the correct function
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
        // Signature from Brio: "40 53 55 57 41 55 48 83 EC 68"
        try
        {
            var finalizeSkeletonsAddress = scanner.ScanText("40 53 55 57 41 55 48 83 EC 68");
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
        _eventBus.Subscribe<PosingModeChangedEvent>(OnPosingModeChanged);

        _log.Debug("BonePosingService initialized");
    }

    private void OnPosingModeChanged(PosingModeChangedEvent e)
    {
        if (e.IsPosingMode)
        {
            // Snapshot all actor skeletons when entering posing mode
            foreach (var actor in _actorManager.Actors)
            {
                var skeleton = _skeletonService.GetSkeleton(actor);
                if (skeleton != null)
                {
                    SnapshotSkeleton(skeleton);
                }
            }
        }
    }

    private nint UpdateBonePhysicsDetour(nint a1)
    {
        // Call original first
        var result = _updateBonePhysicsHook!.Original(a1);

        if (!_gPoseService.IsGPosing || _isUpdating)
            return result;

        _isUpdating = true;
        try
        {
            // Apply our bone transforms after the game's physics update
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
        // Mark all actors with pose modifications for update
        _skeletonsToUpdate.Clear();
        foreach (var (actorAddress, poseInfo) in _poseInfos)
        {
            if (poseInfo.IsOverridden)
            {
                _skeletonsToUpdate.Add(actorAddress);
            }
        }

        // Clear cache update registrations (they're re-registered each frame by the overlay)
        _skeletonsToUpdateCache.Clear();
    }

    public void RegisterSkeletonForCacheUpdate(ISkeleton skeleton)
    {
        _skeletonsToUpdateCache.Add(skeleton.Actor.Address);
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (!e.IsGPosing)
        {
            // Clear all pose modifications when exiting GPose
            _poseInfos.Clear();
            _skeletonsToUpdate.Clear();
        }
    }

    private void ApplyAllBoneTransforms()
    {
        _debugLogCounter++;

        foreach (var actorAddress in _skeletonsToUpdate)
        {
            if (!_poseInfos.TryGetValue(actorAddress, out var poseInfo))
                continue;

            // Find the actor and skeleton
            IActor? actor = null;
            foreach (var a in _actorManager.Actors)
            {
                if (a.Address == actorAddress)
                {
                    actor = a;
                    break;
                }
            }

            if (actor == null)
                continue;

            var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
            if (skeleton == null || !skeleton.IsValid)
                continue;

            ApplySkeletonTransforms(actor, skeleton, poseInfo);
        }
    }

    private void ApplySkeletonTransforms(IActor actor, Skeleton skeleton, SkeletonPoseInfo poseInfo)
    {
        var character = (Character*)actor.Address;
        if (character == null)
            return;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null)
            return;

        if (drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;

        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return;

        var gameSkeleton = charaBase->Skeleton;
        var partialCount = gameSkeleton->PartialSkeletonCount;

        // First pass: Apply all bone transform modifications
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

                var bonePoseInfo = poseInfo.GetPoseInfo(boneName, partialIdx);
                if (!bonePoseInfo.HasStacks)
                    continue;

                // Get the bone reference for IK
                var bone = skeleton.GetBoneByName(boneName, partialIdx);

                // Apply all stacks for this bone
                foreach (var stack in bonePoseInfo.Stacks)
                {
                    ApplyBoneTransform(pose, boneIdx, stack, bone);
                }

                // Update LastTransform AFTER applying modifications (like Brio)
                // This ensures the gizmo sees our modified position, not the game's original
                if (bone != null)
                {
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

        // Second pass: Reparent partial skeleton roots to follow their parent bones
        // This makes face bones follow head movement (face is in a different partial skeleton)
        ReparentPartials(skeleton, gameSkeleton);

        // Third pass: Update cached transforms for reparented bones
        UpdateCachedTransformsAfterReparent(skeleton, gameSkeleton);
    }

    /// <summary>
    /// Updates LastTransform for all bones after reparenting partials.
    /// This ensures face bones have correct cached transforms after following head.
    /// </summary>
    private void UpdateCachedTransformsAfterReparent(Skeleton skeleton, GameSkeleton* gameSkeleton)
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

    // Debug logging - only log once per bone per few frames to avoid spam
    private int _debugLogCounter = 0;
    private const int DebugLogInterval = 60; // Log every 60 frames

    /// <summary>
    /// Reparent partial skeleton roots to follow their parent bones.
    /// This is critical for face bones to follow head movement - face skeleton is separate from body skeleton.
    /// </summary>
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

                // Partial roots (not skeleton root) should follow their parent bone
                if (bone.IsPartialRoot && !bone.IsSkeletonRoot && bone.ParentBone != null)
                {
                    var modelSpace = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.Propagate);
                    var parent = bone.ParentBone.LastTransform;

                    var pos = parent.Position;
                    var rot = parent.Rotation;
                    var scale = parent.Scale;

                    modelSpace->Translation = *(hkVector4f*)(&pos);
                    modelSpace->Rotation = *(hkQuaternionf*)(&rot);
                    modelSpace->Scale = *(hkVector4f*)(&scale);

                    if (_debugLogCounter % DebugLogInterval == 0)
                    {
                        _log.Debug($"[ReparentPartials] Bone={boneName} (partial {partialIdx}) → Parent={bone.ParentBone.BoneName}");
                        _log.Debug($"  Copied transform: Pos={pos}, Rot={rot}, Scale={scale}");
                    }
                }
            }
        }
    }

    private void ApplyBoneTransform(hkaPose* pose, int boneIdx, BonePoseTransformInfo info, IBone? bone)
    {
        // Position - match Brio's ApplySnapshot exactly
        var prop = info.PropagateComponents.HasFlag(TransformComponents.Position);
        var modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforePos = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z);
        var tempPos = beforePos + info.Transform.Position;

        // Apply IK for position if enabled
        if (info.IKInfo.Enabled && bone != null)
        {
            // Solve IK to reach target position
            _ikService.SolveIK(pose, info.IKInfo, bone, tempPos);

            // If not enforcing constraints, override with exact position after IK
            if (!info.IKInfo.EnforceConstraints)
            {
                modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
                modelSpace->Translation = *(hkVector4f*)(&tempPos);
            }
        }
        else
        {
            // No IK - direct position write
            modelSpace->Translation = *(hkVector4f*)(&tempPos);
        }

        // Rotation - match Brio's ApplySnapshot exactly (NO normalize!)
        prop = info.PropagateComponents.HasFlag(TransformComponents.Rotation);
        modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforeRot = new Quaternion(modelSpace->Rotation.X, modelSpace->Rotation.Y, modelSpace->Rotation.Z, modelSpace->Rotation.W);
        var tempRot = beforeRot * info.Transform.Rotation;  // Quaternion multiply, no normalize
        modelSpace->Rotation = *(hkQuaternionf*)(&tempRot);

        // Scale - match Brio's ApplySnapshot exactly
        prop = info.PropagateComponents.HasFlag(TransformComponents.Scale);
        modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforeScale = new Vector3(modelSpace->Scale.X, modelSpace->Scale.Y, modelSpace->Scale.Z);
        var tempScale = beforeScale + info.Transform.Scale;
        modelSpace->Scale = *(hkVector4f*)(&tempScale);

        // Debug logging
        if (_debugLogCounter % DebugLogInterval == 0)
        {
            _log.Debug($"[ApplyBoneTransform] boneIdx={boneIdx}, IK={info.IKInfo.Enabled}");
            _log.Debug($"  Delta: Pos={info.Transform.Position}, Rot={info.Transform.Rotation}, Scale={info.Transform.Scale}");
            _log.Debug($"  Before: Pos={beforePos}, Rot={beforeRot}, Scale={beforeScale}");
            _log.Debug($"  After: Pos={tempPos}, Rot={tempRot}, Scale={tempScale}");
            _log.Debug($"  Propagate: {info.PropagateComponents}");
        }
    }

    public SkeletonPoseInfo GetPoseInfo(ISkeleton skeleton)
    {
        var actorAddress = skeleton.Actor.Address;
        if (!_poseInfos.TryGetValue(actorAddress, out var poseInfo))
        {
            poseInfo = new SkeletonPoseInfo();
            _poseInfos[actorAddress] = poseInfo;
        }
        return poseInfo;
    }

    public void ApplyRotation(IBone bone, Quaternion rotationDelta, bool propagate = true)
    {
        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        var components = propagate ? TransformComponents.Rotation : TransformComponents.None;
        bonePoseInfo.Apply(
            new Transform { Position = Vector3.Zero, Rotation = rotationDelta, Scale = Vector3.Zero },
            Transform.Identity,
            components);

        OnBoneTransformChanged?.Invoke(bone);
    }

    public void ApplyPosition(IBone bone, Vector3 positionDelta, bool propagate = true)
    {
        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        var components = propagate ? TransformComponents.Position : TransformComponents.None;
        bonePoseInfo.Apply(
            new Transform { Position = positionDelta, Rotation = Quaternion.Identity, Scale = Vector3.Zero },
            Transform.Identity,
            components);

        OnBoneTransformChanged?.Invoke(bone);
    }

    public void ApplyTransform(IBone bone, Transform transform, Transform? originalTransform = null, TransformComponents propagate = TransformComponents.Position | TransformComponents.Rotation, bool? accumulate = null)
    {
        _log.Debug($"[ApplyTransform] Bone={bone.BoneName}");
        _log.Debug($"  New Transform: Pos={transform.Position}, Rot={transform.Rotation}, Scale={transform.Scale}");
        _log.Debug($"  Original: {(originalTransform.HasValue ? $"Pos={originalTransform.Value.Position}, Rot={originalTransform.Value.Rotation}, Scale={originalTransform.Value.Scale}" : "NULL")}");
        _log.Debug($"  Propagate: {propagate}, Accumulate: {accumulate}");

        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        var result = bonePoseInfo.Apply(transform, originalTransform, propagate, accumulate);
        _log.Debug($"  Result: {(result.HasValue ? $"Pos={result.Value.Position}, Rot={result.Value.Rotation}, Scale={result.Value.Scale}" : "NULL")}");

        OnBoneTransformChanged?.Invoke(bone);
    }

    public void ResetBone(IBone bone)
    {
        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        bonePoseInfo.ClearStacks();

        OnBoneTransformChanged?.Invoke(bone);
    }

    public void ResetSkeleton(ISkeleton skeleton)
    {
        var actorAddress = skeleton.Actor.Address;
        if (_poseInfos.TryGetValue(actorAddress, out var poseInfo))
        {
            poseInfo.Clear();
        }
    }

    public bool HasModifications(IBone bone)
    {
        if (!_poseInfos.TryGetValue(bone.Skeleton.Actor.Address, out var poseInfo))
            return false;

        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        return bonePoseInfo.HasStacks;
    }

    public Transform? GetModification(IBone bone)
    {
        if (!_poseInfos.TryGetValue(bone.Skeleton.Actor.Address, out var poseInfo))
            return null;

        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        if (!bonePoseInfo.HasStacks)
            return null;

        // Combine all stacks into a single transform
        var combined = Transform.Identity;
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

    private void FinalizeSkeletonsDetour(nint a1)
    {
        _finalizeSkeletonsHook!.Original(a1);  // Let game finalize first

        if (!_gPoseService.IsGPosing)
            return;

        // DON'T re-apply transforms here - transforms are applied ONCE in UpdateBonePhysicsDetour
        // Re-applying would double the delta (Brio only applies once and caches here)

        // Take final snapshot of all skeletons that need cache updates
        // This includes: skeletons with modifications + skeletons with visible overlays/gizmos
        // Combine both sets to ensure all relevant skeletons are updated
        foreach (var actorAddress in _skeletonsToUpdate)
        {
            UpdateSkeletonCache(actorAddress);
        }

        // Also update skeletons registered for cache updates (even without modifications)
        foreach (var actorAddress in _skeletonsToUpdateCache)
        {
            if (!_skeletonsToUpdate.Contains(actorAddress))
            {
                UpdateSkeletonCache(actorAddress);
            }
        }
    }

    private void UpdateSkeletonCache(nint actorAddress)
    {
        IActor? actor = null;
        foreach (var a in _actorManager.Actors)
        {
            if (a.Address == actorAddress)
            {
                actor = a;
                break;
            }
        }

        if (actor == null)
            return;

        var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
        if (skeleton == null || !skeleton.IsValid)
            return;

        var character = (Character*)actor.Address;
        if (character == null)
            return;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null)
            return;

        if (drawObject->Object.GetObjectType() != ObjectType.CharacterBase)
            return;

        var charaBase = (CharacterBase*)drawObject;
        if (charaBase->Skeleton == null)
            return;

        var gameSkeleton = charaBase->Skeleton;

        // Update LastTransform for ALL bones in modified skeletons (like Brio's UpdateCachedTransforms)
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

                // Read with DontPropagate - get the CURRENT state, don't cascade
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
        // Just mark skeleton for update - the freeze already stops animations
        // We don't need to apply any transforms until the user actually modifies a bone
        // This matches Brio's approach: freezing + zero deltas = preserved state
        _skeletonsToUpdate.Add(skeleton.Actor.Address);
    }

    public void Dispose()
    {
        _updateBonePhysicsHook?.Dispose();
        _finalizeSkeletonsHook?.Dispose();
        _framework.Update -= OnFrameworkUpdate;
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _eventBus.Unsubscribe<PosingModeChangedEvent>(OnPosingModeChanged);
        _poseInfos.Clear();
        GC.SuppressFinalize(this);
    }
}
