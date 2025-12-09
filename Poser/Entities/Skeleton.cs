using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using Poser.Core;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Entities;

/// <summary>
/// Represents a skeleton attached to an actor.
/// </summary>
public class Skeleton : EntityBase, ISkeleton
{
    private const int MaxPoses = 4;

    // CharacterBase memory offsets for scale factors (from Brio's BrioCharacterBase)
    private const int CharacterBaseScaleFactor1Offset = 0x2A0;
    private const int CharacterBaseScaleFactor2Offset = 0x2A4;

    private readonly List<IBone> _bones = new();
    private readonly Dictionary<string, Bone> _bonesByName = new();
    private readonly Dictionary<(int, int), Bone> _bonesByIndex = new();

    public IActor Actor { get; }
    public IBone? RootBone { get; private set; }
    public IReadOnlyList<IBone> Bones => _bones.AsReadOnly();
    public bool IsValid { get; private set; }

    /// <summary>
    /// Skeletons are always collapsible.
    /// </summary>
    public override bool IsCollapsible => true;

    /// <summary>
    /// Entity type is Skeleton.
    /// </summary>
    public override EntityType EntityType => EntityType.Skeleton;

    public Skeleton(IActor actor)
        : base(EntityId.New(), "Skeleton")
    {
        Actor = actor;
        IsCollapsed = true; // Start collapsed by default
        BuildSkeleton();
    }

    public IBone? GetBone(string name)
    {
        return _bonesByName.TryGetValue(name, out var bone) ? bone : null;
    }

    public IBone? GetBone(int partialId, int boneIndex)
    {
        return _bonesByIndex.TryGetValue((partialId, boneIndex), out var bone) ? bone : null;
    }

    public Bone? GetBoneByName(string name, int partialId)
    {
        // Fast path: check dictionary first (O(1) for most lookups)
        if (_bonesByName.TryGetValue(name, out var bone) && bone.PartialId == partialId)
            return bone;

        // Slow path: linear search if bone exists in different partial
        foreach (var b in _bones)
        {
            if (b.BoneName == name && b.PartialId == partialId)
                return b as Bone;
        }
        return null;
    }

    public void Refresh()
    {
        // Clear existing data
        _bones.Clear();
        _bonesByName.Clear();
        _bonesByIndex.Clear();
        RootBone = null;
        IsValid = false;

        // Clear children from entity hierarchy
        foreach (var child in Children.ToList())
        {
            DetachChild(child);
        }

        // Rebuild
        BuildSkeleton();
    }

    private unsafe void BuildSkeleton()
    {
        var gameSkeleton = GetGameSkeleton();
        if (gameSkeleton != null)
            BuildFromGameSkeleton(gameSkeleton);
    }

    private unsafe GameSkeleton* GetGameSkeleton()
    {
        if (Actor.Address == nint.Zero)
            return null;

        var character = (Character*)Actor.Address;
        if (character == null)
            return null;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null)
            return null;

        if (drawObject->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            return null;

        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)drawObject;
        return charaBase->Skeleton;
    }

    private unsafe void BuildFromGameSkeleton(GameSkeleton* gameSkeleton)
    {
        var partialCount = gameSkeleton->PartialSkeletonCount;

        // Dictionary to track bones by partial and index for parenting
        var partialBones = new Dictionary<int, Dictionary<int, Bone>>();

        for (int partialIdx = 0; partialIdx < partialCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            partialBones[partialIdx] = new Dictionary<int, Bone>();

            for (int poseIdx = 0; poseIdx < MaxPoses; poseIdx++)
            {
                var pose = partial->GetHavokPose(poseIdx);
                if (pose == null)
                    continue;

                var boneCount = pose->Skeleton->Bones.Length;
                for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
                {
                    // Skip if we already have this bone
                    if (partialBones[partialIdx].ContainsKey(boneIdx))
                        continue;

                    var rawBone = pose->Skeleton->Bones[boneIdx];
                    var boneName = rawBone.Name.String ?? $"bone_{partialIdx}_{boneIdx}";
                    var parentIndex = pose->Skeleton->ParentIndices[boneIdx];

                    var bone = new Bone(this, partialIdx, boneIdx, boneName);
                    partialBones[partialIdx][boneIdx] = bone;
                    _bones.Add(bone);

                    // Use full path for unique lookup
                    var uniqueKey = $"{partialIdx}_{boneName}";
                    if (!_bonesByName.ContainsKey(boneName))
                        _bonesByName[boneName] = bone;
                    _bonesByIndex[(partialIdx, boneIdx)] = bone;

                    // Handle root bones
                    if (parentIndex < 0)
                    {
                        bone.IsPartialRoot = true;

                        if (partialIdx == 0)
                        {
                            bone.IsSkeletonRoot = true;
                            RootBone = bone;
                        }
                    }
                }

                // Second pass: set up parent-child relationships within this partial
                for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
                {
                    var parentIndex = pose->Skeleton->ParentIndices[boneIdx];
                    if (parentIndex >= 0 && partialBones[partialIdx].TryGetValue(boneIdx, out var bone))
                    {
                        if (partialBones[partialIdx].TryGetValue(parentIndex, out var parentBone))
                        {
                            parentBone.AddChildBone(bone);
                        }
                    }
                }

                break; // Only process the first valid pose
            }

            // Connect non-root partials to partial 0
            if (partialIdx > 0 && partialBones[0].Count > 0)
            {
                var connectedParentIndex = partial->ConnectedParentBoneIndex;
                var connectedBoneIndex = partial->ConnectedBoneIndex;

                if (partialBones[0].TryGetValue(connectedParentIndex, out var parentBone) &&
                    partialBones[partialIdx].TryGetValue(connectedBoneIndex, out var childBone))
                {
                    parentBone.AddChildBone(childBone);
                }
            }
        }

        // Attach root bone (or first non-hidden bone) to this skeleton entity
        if (RootBone != null)
        {
            // Find first visible child of root (root itself is typically hidden)
            var visibleRoot = RootBone;
            if (RootBone.IsHiddenBone && RootBone.ChildBones.Count > 0)
            {
                // Attach all non-hidden children directly to skeleton
                foreach (var child in RootBone.ChildBones.Where(b => !b.IsHiddenBone))
                {
                    AttachChild((Bone)child);
                }
            }
            else if (!RootBone.IsHiddenBone)
            {
                AttachChild((Bone)RootBone);
            }
        }

        IsValid = _bones.Count > 0;

        // Initialize bone transforms immediately so they're ready for display
        if (IsValid)
        {
            UpdateBoneTransforms();
        }
    }

    /// <summary>
    /// Updates the cached transforms for all bones by reading from game memory.
    /// Should be called each frame when the overlay is visible.
    /// </summary>
    public unsafe void UpdateBoneTransforms()
    {
        var gameSkeleton = GetGameSkeleton();
        if (gameSkeleton == null)
            return;

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
                if (!_bonesByIndex.TryGetValue((partialIdx, boneIdx), out var bone))
                    continue;

                var boneTransformPtr = pose->AccessBoneModelSpace(boneIdx, hkaPose.PropagateOrNot.DontPropagate);
                if (boneTransformPtr == null)
                    continue;

                ref var boneTransform = ref *boneTransformPtr;
                bone.LastTransform = new Transform
                {
                    Position = new Vector3(boneTransform.Translation.X, boneTransform.Translation.Y, boneTransform.Translation.Z),
                    Rotation = new Quaternion(boneTransform.Rotation.X, boneTransform.Rotation.Y, boneTransform.Rotation.Z, boneTransform.Rotation.W),
                    Scale = new Vector3(boneTransform.Scale.X, boneTransform.Scale.Y, boneTransform.Scale.Z)
                };
            }
        }
    }

    /// <summary>
    /// Gets the model matrix for transforming bone positions to world space.
    /// Includes the character's ScaleFactor like Brio does.
    /// </summary>
    public unsafe Matrix4x4 GetModelMatrix()
    {
        if (Actor.Address == nint.Zero)
            return Matrix4x4.Identity;

        var character = (Character*)Actor.Address;
        if (character == null)
            return Matrix4x4.Identity;

        var drawObject = character->GameObject.DrawObject;
        if (drawObject == null)
            return Matrix4x4.Identity;

        if (drawObject->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            return Matrix4x4.Identity;

        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)drawObject;

        var position = drawObject->Object.Position;
        var rotation = drawObject->Object.Rotation;
        // Include ScaleFactor like Brio does (ScaleFactor1 * ScaleFactor2 at offsets 0x2A0 and 0x2A4)
        var scaleFactor = GetScaleFactor(charaBase);
        var scale = drawObject->Object.Scale * scaleFactor;

        return Matrix4x4.CreateScale(scale) *
               Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(position);
    }

    /// <summary>
    /// Gets the scale factor from CharacterBase (ScaleFactor1 * ScaleFactor2).
    /// Based on Brio's BrioCharacterBase offsets.
    /// </summary>
    private static unsafe float GetScaleFactor(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charaBase)
    {
        var basePtr = (byte*)charaBase;
        var scaleFactor1 = *(float*)(basePtr + CharacterBaseScaleFactor1Offset);
        var scaleFactor2 = *(float*)(basePtr + CharacterBaseScaleFactor2Offset);
        return scaleFactor1 * scaleFactor2;
    }
}
