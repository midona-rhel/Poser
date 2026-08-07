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

    /// <summary>
    /// One partial's map from NATIVE bone index to the bone
    /// <see cref="GetBoneByName"/> resolves for that native bone's name,
    /// together with the identity of the native array it was built from.
    /// </summary>
    private readonly struct NativePartial(nint nativeSkeleton, Bone?[] bones)
    {
        public readonly nint NativeSkeleton = nativeSkeleton;
        public readonly Bone?[] Bones = bones;
    }

    /// <summary>Indexed by partial index; rebuilt with the bone lists, so its
    /// lifetime is exactly <c>_bonesByIndex</c>'s.</summary>
    private NativePartial[] _nativePartials = Array.Empty<NativePartial>();

    /// <summary>
    /// A VERIFIED view of one partial's native-index→bone map: handed out only
    /// when the map was built from the same native <c>hkaSkeleton</c>, with the
    /// same bone count, that the caller is iterating. Per-frame native passes
    /// use it to resolve bones without marshaling a managed string per bone;
    /// <see cref="IsValid"/> false means they must fall back to the name path.
    /// </summary>
    public readonly struct NativeBoneMap
    {
        private readonly Bone?[]? _bones;

        internal NativeBoneMap(Bone?[] bones) => _bones = bones;

        public bool IsValid => _bones is not null;

        public Bone? this[int boneIndex] =>
            _bones is { } bones && (uint)boneIndex < (uint)bones.Length
                ? bones[boneIndex]
                : null;
    }

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

    /// <summary>
    /// The native-index→bone map for one partial, or an invalid handle when it
    /// cannot be proven to describe <paramref name="pose"/>. The proof is the
    /// pair (native <c>hkaSkeleton</c> pointer, bone count) captured when the
    /// map was built: a partial that rebound to a different native skeleton, a
    /// partial that did not exist at build time, and a partial whose bone array
    /// changed length all fail it, and the caller resolves by name instead.
    /// A wrong-bone write is pose corruption, so the map is never assumed.
    /// </summary>
    internal unsafe NativeBoneMap GetNativeBoneMap(int partialId, hkaPose* pose)
    {
        if (pose == null || (uint)partialId >= (uint)_nativePartials.Length)
            return default;

        var entry = _nativePartials[partialId];
        var native = pose->Skeleton;
        if (entry.Bones == null || native == null ||
            entry.NativeSkeleton != (nint)native ||
            entry.Bones.Length != native->Bones.Length)
        {
            return default;
        }

        return new NativeBoneMap(entry.Bones);
    }

    public void Refresh()
    {
        // Clear existing data
        _bones.Clear();
        _bonesByName.Clear();
        _bonesByIndex.Clear();
        _nativePartials = Array.Empty<NativePartial>();
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

        // Built alongside the bones, from the names this pass already marshals.
        var nativePartials = new NativePartial[partialCount];

        // Lazily built lowest-index-per-name map of partial 0, used to link
        // multi-root partials by name (see the connect step below).
        Dictionary<string, Bone>? partial0FirstByName = null;

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

                // Third pass: freeze what GetBoneByName(name, partialIdx) will
                // answer for every native index of THIS pose. That method
                // resolves the LOWEST-indexed bone of the partial carrying the
                // name (its dictionary fast path stores the first bone built
                // with a name, its linear fallback scans build order, and build
                // order inside a partial is ascending bone index) — so filling
                // the map ascending and keeping the first bone per name
                // reproduces it exactly, duplicate names within the partial
                // included. Names never cross partials here: the map is per
                // partial, exactly as the name lookup's partialId argument is.
                var indexMap = new Bone?[boneCount];
                var firstByName = new Dictionary<string, Bone>(
                    boneCount, StringComparer.Ordinal);
                for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
                {
                    if (!partialBones[partialIdx].TryGetValue(boneIdx, out var mapped))
                        continue;
                    if (!firstByName.TryGetValue(mapped.BoneName, out var first))
                        firstByName[mapped.BoneName] = first = mapped;
                    indexMap[boneIdx] = first;
                }
                nativePartials[partialIdx] =
                    new NativePartial((nint)pose->Skeleton, indexMap);

                break; // Only process the first valid pose
            }

            // Connect non-root partials to partial 0. Brio's rule is
            // either/or on the partial's root count (Brio Skeleton.cs:99-125):
            // exactly ONE root -> link via ConnectedParentBoneIndex/
            // ConnectedBoneIndex (Brio Skeleton.cs:101-110); MULTIPLE roots
            // -> map EVERY root to its partial-0 namesake by name (Brio
            // Skeleton.cs:111-124). The connected-index link is NOT applied
            // in the multi-root case, and a root with no partial-0 namesake
            // simply stays unlinked (Brio Skeleton.cs:117 null check).
            if (partialIdx > 0 && partialBones[0].Count > 0)
            {
                var rootBones = partialBones[partialIdx]
                    .OrderBy(kv => kv.Key)
                    .Select(kv => kv.Value)
                    .Where(b => b.IsPartialRoot)
                    .ToList();

                if (rootBones.Count == 1)
                {
                    var connectedParentIndex = partial->ConnectedParentBoneIndex;
                    var connectedBoneIndex = partial->ConnectedBoneIndex;

                    if (partialBones[0].TryGetValue(connectedParentIndex, out var parentBone) &&
                        partialBones[partialIdx].TryGetValue(connectedBoneIndex, out var childBone))
                    {
                        parentBone.AddChildBone(childBone);
                    }
                }
                else
                {
                    // Brio resolves the namesake with
                    // PartialSkeleton.GetBone(string)
                    // (Brio PartialSkeleton.cs:40-47): first ordinal match in
                    // insertion order, and partial-0 bones are inserted in
                    // ascending index order — i.e. the LOWEST-indexed
                    // partial-0 bone carrying the raw havok name.
                    if (partial0FirstByName == null)
                    {
                        partial0FirstByName = new Dictionary<string, Bone>(
                            partialBones[0].Count, StringComparer.Ordinal);
                        foreach (var kv in partialBones[0].OrderBy(kv => kv.Key))
                        {
                            if (!partial0FirstByName.ContainsKey(kv.Value.BoneName))
                                partial0FirstByName[kv.Value.BoneName] = kv.Value;
                        }
                    }

                    foreach (var rootBone in rootBones)
                    {
                        if (partial0FirstByName.TryGetValue(rootBone.BoneName, out var namesake))
                        {
                            namesake.AddChildBone(rootBone);
                        }
                    }
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
        _nativePartials = nativePartials;

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
    /// Ktisis' "Set to reference pose" source data (EntityPoseConverter.
    /// LoadReferencePose: hkaPose::SetToReferencePose + SyncModelSpace per
    /// partial) read WITHOUT mutating the live pose: each partial's
    /// hkaSkeleton reference locals composed down the parent chain — havok
    /// orders parents before children, so one forward pass suffices. A
    /// non-zero partial has no place of its own: at runtime the game drives
    /// its roots from partial 0 — a single-root partial through the
    /// connected parent bone, a multi-root partial per-root through the
    /// partial-0 NAMESAKE (the same either/or the entity linking above
    /// mirrors from Brio) — so each root's anchor is that partial-0 bone's
    /// composed reference and the root's own reference local is ignored.
    /// Attach-driven partial roots are left out of the result
    /// (CreatePoseFile's export rule); by name they are the partial-0 bone
    /// the import's instance expansion already covers.
    /// </summary>
    public unsafe IReadOnlyList<(IBone Bone, Transform Reference)> CaptureReferencePose()
    {
        var result = new List<(IBone Bone, Transform Reference)>();
        var gameSkeleton = GetGameSkeleton();
        if (gameSkeleton == null)
            return result;

        var partialCount = gameSkeleton->PartialSkeletonCount;
        Transform[]? partial0Model = null;
        Dictionary<string, Transform>? partial0ByName = null;
        for (int partialIdx = 0; partialIdx < partialCount; partialIdx++)
        {
            var partial = &gameSkeleton->PartialSkeletons[partialIdx];
            var pose = partial->GetHavokPose(0);
            if (pose == null || pose->Skeleton == null)
                continue;

            var havokSkeleton = pose->Skeleton;
            var boneCount = havokSkeleton->Bones.Length;
            if (havokSkeleton->ReferencePose.Length < boneCount)
                continue;

            var model = new Transform[boneCount];
            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                var local = havokSkeleton->ReferencePose[boneIdx];
                var localTransform = new Transform
                {
                    Position = new Vector3(local.Translation.X, local.Translation.Y, local.Translation.Z),
                    Rotation = new Quaternion(local.Rotation.X, local.Rotation.Y, local.Rotation.Z, local.Rotation.W),
                    Scale = new Vector3(local.Scale.X, local.Scale.Y, local.Scale.Z)
                };
                var parentIndex = havokSkeleton->ParentIndices[boneIdx];
                if (parentIndex >= 0 && parentIndex < boneIdx)
                    model[boneIdx] = ComposeReference(model[parentIndex], localTransform);
                else if (partialIdx > 0 && partial0ByName != null &&
                         _bonesByIndex.TryGetValue((partialIdx, boneIdx), out var rootBone) &&
                         partial0ByName.TryGetValue(rootBone.BoneName, out var namesake))
                    model[boneIdx] = namesake;
                else if (partialIdx > 0 && partial0Model != null &&
                         boneIdx == partial->ConnectedBoneIndex &&
                         partial->ConnectedParentBoneIndex >= 0 &&
                         partial->ConnectedParentBoneIndex < partial0Model.Length)
                    model[boneIdx] = partial0Model[partial->ConnectedParentBoneIndex];
                else
                    model[boneIdx] = localTransform;
            }
            if (partialIdx == 0)
            {
                partial0Model = model;
                // Lowest index wins per name — the same rule the runtime's
                // name lookups and the linking step above resolve with.
                partial0ByName = new Dictionary<string, Transform>(
                    boneCount, StringComparer.Ordinal);
                for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
                {
                    if (_bonesByIndex.TryGetValue((0, boneIdx), out var named) &&
                        !partial0ByName.ContainsKey(named.BoneName))
                        partial0ByName[named.BoneName] = model[boneIdx];
                }
            }

            for (int boneIdx = 0; boneIdx < boneCount; boneIdx++)
            {
                if (!_bonesByIndex.TryGetValue((partialIdx, boneIdx), out var bone))
                    continue;
                if (bone.IsPartialRoot && !bone.IsSkeletonRoot)
                    continue;
                result.Add((bone, model[boneIdx]));
            }
        }
        return result;
    }

    /// <summary>Parent-then-local model-space composition, via the same
    /// S·R·T matrices every other transform in this assembly composes with.</summary>
    internal static Transform ComposeReference(in Transform parent, in Transform local) =>
        Transform.FromMatrix(local.ToMatrix() * parent.ToMatrix());

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
