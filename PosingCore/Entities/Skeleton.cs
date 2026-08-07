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

/// <summary>Which bone transform caches a refresh may write. Brio's
/// CacheTypes: LastRawTransform belongs to the update-phase apply pass and
/// must never be written from the draw phase, where render-phase plugins
/// (Customize+) have already stamped the model pose.</summary>
[Flags]
public enum BoneCacheTypes
{
    None = 0,
    LastTransform = 1 << 0,
    LastRawTransform = 1 << 1,
    All = LastTransform | LastRawTransform,
}

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

    /// <summary>Live view over <c>_bones</c>, allocated once. Refresh() clears
    /// and refills the SAME list, so the wrapper survives a rebuild; building a
    /// fresh one per access charged an allocation to every <c>skeleton.Bones</c>
    /// read, including the per-bone loops of the scene refresh.</summary>
    private readonly System.Collections.ObjectModel.ReadOnlyCollection<IBone> _bonesView;

    private readonly Dictionary<string, Bone> _bonesByName = new();
    private readonly Dictionary<(int, int), Bone> _bonesByIndex = new();

    public IActor Actor { get; }
    public Poser.Domain.Identity.PoseSlot Slot { get; }
    public nint CharacterBaseAddress { get; private set; }
    public IBone? RootBone { get; private set; }
    public IReadOnlyList<IBone> Bones => _bonesView;
    public bool IsValid { get; private set; }

    /// <summary>
    /// Skeletons are always collapsible.
    /// </summary>
    public override bool IsCollapsible => true;

    /// <summary>
    /// Entity type is Skeleton.
    /// </summary>
    public override EntityType EntityType => EntityType.Skeleton;

    // Slot-native discovery is OWNED by Poser.Game: this transitional entity
    // receives only a resolver returning the slot's current CharacterBase
    // address (zero when the slot is absent).
    private readonly Func<nint> _resolveCharacterBase;

    public Skeleton(
        IActor actor,
        Poser.Domain.Identity.PoseSlot slot,
        Func<nint> resolveCharacterBase)
        : base(EntityId.New(), "Skeleton")
    {
        Actor = actor;
        Slot = slot;
        _bonesView = _bones.AsReadOnly();
        _resolveCharacterBase = resolveCharacterBase;
        IsCollapsed = true; // Start collapsed by default
        IsVisible = false; // Start unchecked (not visible in overlay)
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
        // Slot-exact resolution: this skeleton reads ONLY its own slot's
        // CharacterBase; there is no fallback to the Character slot.
        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)
            _resolveCharacterBase();
        if (charaBase == null)
            return null;
        CharacterBaseAddress = (nint)charaBase;
        return charaBase->Skeleton;
    }

    /// <summary>The current native skeleton pointer for this slot, or null
    /// when the slot is absent. Runtime apply paths use this so a weapon or
    /// ornament stack can never be written through the Character skeleton.</summary>
    internal unsafe GameSkeleton* GetGameSkeletonPointer() => GetGameSkeleton();

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

                    // Store first bone with this name for quick lookup
                    // Use GetBoneByName(name, partialId) for partial-specific lookup
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
    ///
    /// Draw-phase callers must pass <see cref="BoneCacheTypes.LastTransform"/>
    /// only: by draw time, render-phase plugins (Customize+) have already
    /// multiplied their own changes into the model pose, and a raw cache
    /// written here would smuggle those into every delta computed against it
    /// (bake, export, rawBaseline writes). LastRawTransform is owned by the
    /// update-phase apply pass alone — the same split Brio makes with its
    /// CacheTypes flag.
    /// </summary>
    public unsafe void UpdateBoneTransforms(BoneCacheTypes caches = BoneCacheTypes.All)
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
                var transform = new Transform
                {
                    Position = new Vector3(boneTransform.Translation.X, boneTransform.Translation.Y, boneTransform.Translation.Z),
                    Rotation = new Quaternion(boneTransform.Rotation.X, boneTransform.Rotation.Y, boneTransform.Rotation.Z, boneTransform.Rotation.W),
                    Scale = new Vector3(boneTransform.Scale.X, boneTransform.Scale.Y, boneTransform.Scale.Z)
                };
                if ((caches & BoneCacheTypes.LastRawTransform) != 0)
                    bone.LastRawTransform = transform;
                if ((caches & BoneCacheTypes.LastTransform) != 0)
                    bone.LastTransform = transform;
            }
        }
    }

    /// <summary>
    /// Gets the model matrix for transforming bone positions to world space.
    /// Includes the character's ScaleFactor like Brio does.
    /// </summary>
    public unsafe Matrix4x4 GetModelMatrix()
    {
        // The matrix comes from THIS slot's draw object: a weapon's model
        // moves with the hand, not with the actor origin.
        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)
            _resolveCharacterBase();
        if (charaBase == null)
            return Matrix4x4.Identity;

        var position = charaBase->DrawObject.Object.Position;
        var rotation = charaBase->DrawObject.Object.Rotation;
        // Include ScaleFactor like Brio does (ScaleFactor1 * ScaleFactor2 at offsets 0x2A0 and 0x2A4)
        var scaleFactor = GetScaleFactor(charaBase);
        var scale = charaBase->DrawObject.Object.Scale * scaleFactor;

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
        if (charaBase == null)
            return 1f;

        var basePtr = (byte*)charaBase;
        var scaleFactor1 = *(float*)(basePtr + CharacterBaseScaleFactor1Offset);
        var scaleFactor2 = *(float*)(basePtr + CharacterBaseScaleFactor2Offset);
        return scaleFactor1 * scaleFactor2;
    }
}
