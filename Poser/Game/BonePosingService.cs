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
        IGameInteropProvider hooking,
        ISigScanner scanner)
    {
        _log = log;
        _framework = framework;
        _gPoseService = gPoseService;
        _skeletonService = skeletonService;
        _actorManager = actorManager;
        _eventBus = eventBus;

        // Hook UpdateBonePhysics - this is called during skeleton updates
        // Signature from Brio: "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ??"
        try
        {
            var updateBonePhysicsAddress = scanner.ScanText("48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 57 41 54 41 56 48 83 EC ?? 48 8B 59 ??");
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

                // Apply all stacks for this bone
                foreach (var stack in bonePoseInfo.Stacks)
                {
                    ApplyBoneTransform(pose, boneIdx, stack);
                }

                // Update LastTransform AFTER applying modifications (like Brio)
                // This ensures the gizmo sees our modified position, not the game's original
                var bone = skeleton.GetBoneByName(boneName, partialIdx);
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
    }

    private void ApplyBoneTransform(hkaPose* pose, int boneIdx, BonePoseTransformInfo info)
    {
        // Apply position
        if (info.Transform.Position != Vector3.Zero)
        {
            var propagate = info.PropagateComponents.HasFlag(TransformComponents.Position);
            var modelSpace = pose->AccessBoneModelSpace(boneIdx, propagate ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
            if (modelSpace != null)
            {
                var newPos = new Vector3(
                    modelSpace->Translation.X + info.Transform.Position.X,
                    modelSpace->Translation.Y + info.Transform.Position.Y,
                    modelSpace->Translation.Z + info.Transform.Position.Z);
                modelSpace->Translation = *(hkVector4f*)(&newPos);
            }
        }

        // Apply rotation (post-multiply: newRot = currentRot * delta for local-space deltas, like Brio)
        if (info.Transform.Rotation != Quaternion.Identity)
        {
            var propagate = info.PropagateComponents.HasFlag(TransformComponents.Rotation);
            var modelSpace = pose->AccessBoneModelSpace(boneIdx, propagate ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
            if (modelSpace != null)
            {
                var currentRot = new Quaternion(
                    modelSpace->Rotation.X,
                    modelSpace->Rotation.Y,
                    modelSpace->Rotation.Z,
                    modelSpace->Rotation.W);
                var newRot = Quaternion.Normalize(currentRot * info.Transform.Rotation);
                modelSpace->Rotation = *(hkQuaternionf*)(&newRot);
            }
        }

        // Apply scale
        if (info.Transform.Scale != Vector3.Zero)
        {
            var propagate = info.PropagateComponents.HasFlag(TransformComponents.Scale);
            var modelSpace = pose->AccessBoneModelSpace(boneIdx, propagate ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
            if (modelSpace != null)
            {
                var newScale = new Vector3(
                    modelSpace->Scale.X + info.Transform.Scale.X,
                    modelSpace->Scale.Y + info.Transform.Scale.Y,
                    modelSpace->Scale.Z + info.Transform.Scale.Z);
                modelSpace->Scale = *(hkVector4f*)(&newScale);
            }
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

    public void ApplyTransform(IBone bone, Transform transform, Transform? originalTransform = null, TransformComponents propagate = TransformComponents.Position | TransformComponents.Rotation)
    {
        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        bonePoseInfo.Apply(transform, originalTransform, propagate);

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
        var poseInfo = GetPoseInfo(skeleton);

        // Mark skeleton for update so it starts being controlled by our hook
        _skeletonsToUpdate.Add(skeleton.Actor.Address);

        // Read current bone transforms from game and apply them as modifications
        // This "claims" the bones and prevents game systems (like LookAt) from controlling them
        var character = (Character*)skeleton.Actor.Address;
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
                if (bonePoseInfo.HasStacks)
                    continue; // Already has modifications, don't overwrite

                // Read current transform from game in local space (reference pose)
                var localSpace = pose->AccessBoneLocalSpace(boneIdx);
                if (localSpace == null)
                    continue;

                // Store the current rotation as a "modification" that maintains this pose
                // Using identity as the delta means we're snapshotting current state
                var currentRotation = new Quaternion(
                    localSpace->Rotation.X,
                    localSpace->Rotation.Y,
                    localSpace->Rotation.Z,
                    localSpace->Rotation.W);

                // Apply identity delta with the bone marked as having a stack
                // This tells ApplyBoneTransform to maintain current pose
                bonePoseInfo.Apply(
                    new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.Zero },
                    new Transform { Position = Vector3.Zero, Rotation = currentRotation, Scale = Vector3.One },
                    TransformComponents.Rotation);
            }
        }
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
