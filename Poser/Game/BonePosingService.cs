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
        foreach (var (actorAddress, poseInfo) in _poseInfos)
        {
            if (poseInfo.IsOverridden)
            {
                _skeletonsToUpdate.Add(actorAddress);
            }
        }

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

    /// <summary>
    /// Apply skeleton transforms following Brio's exact pattern:
    /// 1. Apply transforms with per-bone LastTransform update
    /// 2. Full cache update after apply
    /// 3. Reparent partials
    /// 4. Full cache update after reparent
    /// </summary>
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

        // STEP 1: Apply transforms AND update LastTransform per-bone (like Brio ApplyBrioTransforms)
        ApplyTransformsWithPerBoneUpdate(skeleton, gameSkeleton, poseInfo);

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
    private void ApplyTransformsWithPerBoneUpdate(Skeleton skeleton, GameSkeleton* gameSkeleton, SkeletonPoseInfo poseInfo)
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

                // Apply ALL stacks for this bone (like Brio lines 108-112)
                foreach (var stack in bonePoseInfo.Stacks)
                {
                    ApplyBoneTransform(pose, boneIdx, stack);
                }

                // IMMEDIATELY update LastTransform (like Brio lines 114-116)
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

    /// <summary>
    /// Updates LastTransform for ALL bones in the skeleton.
    /// Called ONCE after all modifications and reparenting are complete.
    /// This matches Brio's pattern - single source of truth for LastTransform.
    /// </summary>
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

    private void ApplyBoneTransform(hkaPose* pose, int boneIdx, BonePoseTransformInfo info)
    {
        // Delta mode: ADD to Havok state (like Brio)

        // Position
        var prop = info.PropagateComponents.HasFlag(TransformComponents.Position);
        var modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforePos = new Vector3(modelSpace->Translation.X, modelSpace->Translation.Y, modelSpace->Translation.Z);
        var tempPos = beforePos + info.Transform.Position;
        modelSpace->Translation = *(hkVector4f*)(&tempPos);

        // Rotation
        prop = info.PropagateComponents.HasFlag(TransformComponents.Rotation);
        modelSpace = pose->AccessBoneModelSpace(boneIdx, prop ? hkaPose.PropagateOrNot.Propagate : hkaPose.PropagateOrNot.DontPropagate);
        var beforeRot = new Quaternion(modelSpace->Rotation.X, modelSpace->Rotation.Y, modelSpace->Rotation.Z, modelSpace->Rotation.W);
        var tempRot = beforeRot * info.Transform.Rotation;
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
        var actorAddress = skeleton.Actor.Address;
        if (!_poseInfos.TryGetValue(actorAddress, out var poseInfo))
        {
            poseInfo = new SkeletonPoseInfo();
            _poseInfos[actorAddress] = poseInfo;
        }
        return poseInfo;
    }

    public void ApplyTransform(IBone bone, Transform newTransform, Transform originalTransform)
    {
        if (bone is VirtualBone)
            return;

        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);

        bonePoseInfo.Apply(newTransform, originalTransform);

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
        foreach (var actorAddress in _skeletonsToUpdate)
        {
            UpdateSkeletonCache(actorAddress);
        }

        // Also update overlay-only skeletons that don't have modifications
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
        _skeletonsToUpdate.Add(skeleton.Actor.Address);
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

        OnBoneTransformChanged?.Invoke(bone);
    }

    public void MirrorPose(ISkeleton skeleton)
    {
        var poseInfo = GetPoseInfo(skeleton);

        // Collect all bone transforms
        var transforms = new Dictionary<string, Transform>();
        var rawTransforms = new Dictionary<string, Transform>();
        foreach (var bone in skeleton.Bones)
        {
            transforms[bone.BoneName] = bone.LastTransform;
            rawTransforms[bone.BoneName] = bone.LastRawTransform;
        }

        // Swap left/right bone transforms
        foreach (var bone in skeleton.Bones)
        {
            var mirrorName = GetMirrorBoneName(bone.BoneName);
            if (mirrorName == null)
                continue;

            // Only process _l bones, _r will be handled implicitly
            if (!bone.BoneName.EndsWith("_l"))
                continue;

            if (!transforms.TryGetValue(mirrorName, out var mirrorTransform))
                continue;

            var mirrorBone = skeleton.GetBone(mirrorName);
            if (mirrorBone == null)
                continue;

            // Get the current transforms
            var boneTransform = transforms[bone.BoneName];

            // Clear and apply - mirror is a replacement, not an accumulation
            var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
            bonePoseInfo.ClearStacks();
            var invertedMirror = InvertForMirror(mirrorTransform);
            bonePoseInfo.Apply(invertedMirror, rawTransforms[bone.BoneName]);

            var mirrorPoseInfo = poseInfo.GetPoseInfo(mirrorBone.BoneName, mirrorBone.PartialId);
            mirrorPoseInfo.ClearStacks();
            var invertedBone = InvertForMirror(boneTransform);
            mirrorPoseInfo.Apply(invertedBone, rawTransforms[mirrorName]);
        }
    }

    public string? GetMirrorBoneName(string boneName)
    {
        if (boneName.EndsWith("_r"))
        {
            return string.Concat(boneName.AsSpan(0, boneName.Length - 2), "_l");
        }

        if (boneName.EndsWith("_l"))
        {
            return string.Concat(boneName.AsSpan(0, boneName.Length - 2), "_r");
        }

        return null;
    }

    private static Transform InvertForMirror(Transform t)
    {
        // For mirroring, we invert the X position and X/W rotation components
        return new Transform
        {
            Position = new Vector3(-t.Position.X, t.Position.Y, t.Position.Z),
            Rotation = new Quaternion(-t.Rotation.X, t.Rotation.Y, t.Rotation.Z, -t.Rotation.W),
            Scale = t.Scale
        };
    }

    private const float RadiansToDegrees = 180f / MathF.PI;
    private const float DegreesToRadians = MathF.PI / 180f;

    private static Vector3 QuaternionToEuler(Quaternion r)
    {
        float yaw = MathF.Atan2(2.0f * (r.Y * r.W + r.X * r.Z), 1.0f - 2.0f * (r.X * r.X + r.Y * r.Y));
        float pitch = MathF.Asin(Math.Clamp(2.0f * (r.X * r.W - r.Y * r.Z), -1f, 1f));
        float roll = MathF.Atan2(2.0f * (r.X * r.Y + r.Z * r.W), 1.0f - 2.0f * (r.X * r.X + r.Z * r.Z));

        return new Vector3(yaw, pitch, roll) * RadiansToDegrees;
    }

    private static Quaternion EulerToQuaternion(Vector3 euler)
    {
        euler *= DegreesToRadians;
        var quaternion = Quaternion.CreateFromYawPitchRoll(euler.X, euler.Y, euler.Z);
        return Quaternion.Normalize(quaternion);
    }

    public void Dispose()
    {
        _updateBonePhysicsHook?.Dispose();
        _finalizeSkeletonsHook?.Dispose();
        _framework.Update -= OnFrameworkUpdate;
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _poseInfos.Clear();
        GC.SuppressFinalize(this);
    }
}
