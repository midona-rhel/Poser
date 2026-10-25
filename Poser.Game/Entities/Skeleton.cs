using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;
using Poser.Game.Natives;

namespace Poser.Game.Entities;

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

    private readonly List<IBone> _bones = new();

    /// <summary>One read-only wrapper around the bone list; rebuilds refill the same list.</summary>
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

    /// <summary>A cached native-index lookup accepted when the skeleton pointer
    /// and bone count match. This does not detect in-place name changes.</summary>
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

    public IActor Actor { get; private set; }
    public Poser.Domain.Identity.PoseSlot Slot { get; }
    public nint CharacterBaseAddress { get; private set; }
    public IBone? RootBone { get; private set; }
    public IReadOnlyList<IBone> Bones => _bonesView;
    public bool IsValid { get; private set; }

    /// <summary>The (partial, bone) slots the apply pass's last KineDriver
    /// re-run wrote. Empty when it did not run.</summary>
    internal HashSet<(int Partial, int Bone)> DrivenBones { get; } = new();

    /// <summary>Process-wide revision assigned on each rebuild attempt, including
    /// attempts with no native skeleton. Wrapper rebinds do not advance it.</summary>
    public long BuildRevision { get; private set; }

    private static long _buildCounter;

    /// <summary>Follows the actor MANAGER's wrapper for the same logical
    /// actor: when the manager replaces the wrapper object at an unchanged
    /// identity, the cached skeleton is re-pointed instead of released. One
    /// cache entry per (actor id, slot) — never one per wrapper.</summary>
    public void RebindActor(IActor actor) => Actor = actor;

    // Slot-native discovery is OWNED by Poser.Game: this transitional entity
    // receives only a resolver returning a given actor's current
    // CharacterBase address for this slot (zero when the slot is absent).
    // It takes the actor as a parameter so an actor-wrapper rebind never
    // leaves the resolver reading a stale capture.
    private readonly Func<IActor, nint> _resolveCharacterBase;

    // Actor and slot determine the entity id, so rebuilding preserves it.
    public Skeleton(
        IActor actor,
        Poser.Domain.Identity.PoseSlot slot,
        Func<IActor, nint> resolveCharacterBase,
        Func<bool> showAllVieraEars,
        Func<bool> showFriendlyNames)
        : base(new EntityId($"skeleton_{actor.Id.Unique}_{slot}"), "Skeleton")
    {
        Actor = actor;
        Slot = slot;
        _bonesView = _bones.AsReadOnly();
        _resolveCharacterBase = resolveCharacterBase;
        _showAllVieraEars = showAllVieraEars;
        _showFriendlyNames = showFriendlyNames;
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

    /// <summary>Returns the cached map when its skeleton pointer and bone count
    /// match the pose. Otherwise callers must resolve bones by name.</summary>
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

    internal unsafe bool HasCurrentNativeLayout()
    {
        var skeleton = GetGameSkeleton();
        if (skeleton == null || skeleton->PartialSkeletonCount != _nativePartials.Length)
            return false;
        for (int partial = 0; partial < _nativePartials.Length; partial++)
        {
            hkaPose* current = null;
            // Match BuildFromGameSkeleton: the first available pose owns the map.
            for (int index = 0; index < MaxPoses; index++)
                if (skeleton->PartialSkeletons[partial].GetHavokPose(index) is var pose && pose != null)
                {
                    current = pose;
                    break;
                }
            if (current == null)
            {
                if (_nativePartials[partial].NativeSkeleton != 0) return false;
            }
            else if (!GetNativeBoneMap(partial, current).IsValid)
                return false;
        }
        return true;
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
        // Every (re)build is a cache invalidation: whatever pointers were
        // handed out before this line are no longer this skeleton's view.
        BuildRevision = System.Threading.Interlocked.Increment(ref _buildCounter);
        var gameSkeleton = GetGameSkeleton();
        if (gameSkeleton != null)
            BuildFromGameSkeleton(gameSkeleton);
    }

    private unsafe GameSkeleton* GetGameSkeleton()
    {
        // Slot-exact resolution: this skeleton reads ONLY its own slot's
        // CharacterBase; there is no fallback to the Character slot.
        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)
            _resolveCharacterBase(Actor);
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

                    var bone = new Bone(this, partialIdx, boneIdx, boneName, _showFriendlyNames);
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

                // Match name lookup: duplicate names within a partial resolve
                // to the lowest bone index, even when accessed by native index.
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

            // Match Brio: a single root uses the connected indices; multiple
            // roots attach to partial-0 bones with matching names.
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
                    // For duplicate names, use the lowest partial-0 bone index.
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

        // Before anything asks a bone whether it is hidden: the two rules that
        // need the whole bone list, or the actor, rather than the bone.
        ApplyBoneFilters();

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
    /// The per-skeleton hidden-bone verdicts: the superseded jaw and the Viera
    /// ear sets the character does not wear. Jaw filtering belongs to the
    /// native layout; ear filtering follows rendered customization without
    /// replacing bone identities or their pose/IK state.
    ///
    /// <para>The ear read is skipped entirely unless the skeleton actually has
    /// ear bones, so a non-Viera skeleton costs nothing.</para>
    /// </summary>
    private readonly Func<bool> _showAllVieraEars;
    // Ktisis' ShowFriendlyBoneNames, read live by every Bone.Name.
    private readonly Func<bool> _showFriendlyNames;
    private readonly List<Bone> _vieraEarBones = new();
    private char? _filteredEarSet;

    private void ApplyBoneFilters()
    {
        bool hasModernJaw =
            _bonesByName.ContainsKey(LegacyBoneFilters.ModernJaw);
        _vieraEarBones.Clear();
        _filteredEarSet = null;
        foreach (var entry in _bones)
        {
            var bone = (Bone)entry;
            bone.FilteredOut =
                LegacyBoneFilters.IsSupersededJaw(
                    bone.BoneName, bone.PartialId, hasModernJaw);
            if (LegacyBoneFilters.IsVieraEarBone(bone.BoneName))
                _vieraEarBones.Add(bone);
        }
        RefreshBoneFilters();
    }

    internal bool RefreshBoneFilters()
    {
        if (_vieraEarBones.Count == 0) return false;
        byte set = _showAllVieraEars() ? (byte)0 : Game.RaceFeatureRead.VieraEarSet(Actor.Address);
        // Unknown values show every set rather than hiding the equipped ears.
        char earSet = LegacyBoneFilters.IsKnownVieraEarSet(set)
            ? LegacyBoneFilters.VieraEarSetFor(set) : '\0';
        if (_filteredEarSet == earSet) return false;
        _filteredEarSet = earSet;
        bool changed = false;
        foreach (var bone in _vieraEarBones)
        {
            bool hidden = earSet != '\0' && LegacyBoneFilters.VieraEarSetOf(bone.BoneName) != earSet;
            changed |= bone.FilteredOut != hidden;
            bone.FilteredOut = hidden;
        }
        return changed;
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

        if (caches == BoneCacheTypes.LastTransform)
        {
            RefreshWantedTransforms(gameSkeleton);
            return;
        }

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

                var boneTransformPtr = NativePoseRead.Model(pose, boneIdx);
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


    /// <summary>Refreshes display transforms only for bones recently requested
    /// by a reader, as tracked by Bone.TransformWanted.</summary>
    private unsafe void RefreshWantedTransforms(GameSkeleton* gameSkeleton)
    {
        var partialCount = gameSkeleton->PartialSkeletonCount;
        hkaPose*[]? poses = null;
            foreach (var pair in _bonesByIndex)
            {
                var bone = pair.Value;
                if (!bone.TransformWanted)
                    continue;
                var (partialIdx, boneIdx) = pair.Key;
                if (partialIdx < 0 || partialIdx >= partialCount)
                    continue;
                poses ??= new hkaPose*[partialCount];
                var pose = poses[partialIdx];
                if (pose == null)
                {
                    pose = gameSkeleton->PartialSkeletons[partialIdx].GetHavokPose(0);
                    if (pose == null)
                        continue;
                    poses[partialIdx] = pose;
                }
                if (boneIdx < 0 || boneIdx >= pose->Skeleton->Bones.Length)
                    continue;
                var wantedPtr = NativePoseRead.Model(pose, boneIdx);
                if (wantedPtr == null)
                    continue;
                ref var wanted = ref *wantedPtr;
                bone.LastTransform = new Transform
                {
                    Position = new Vector3(wanted.Translation.X, wanted.Translation.Y, wanted.Translation.Z),
                    Rotation = new Quaternion(wanted.Rotation.X, wanted.Rotation.Y, wanted.Rotation.Z, wanted.Rotation.W),
                    Scale = new Vector3(wanted.Scale.X, wanted.Scale.Y, wanted.Scale.Z)
                };
            }
    }

    /// <summary>Accumulated native-base resolution time, in Stopwatch ticks.</summary>
    public static long ResolveTicks;
    public static long ResolveCalls;

    /// <summary>Resolves the native base once, refreshes requested display transforms,
    /// and returns the model matrix. Returns null when the slot is absent.</summary>
    public unsafe Matrix4x4? RefreshForDraw()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)
            _resolveCharacterBase(Actor);
        ResolveTicks += System.Diagnostics.Stopwatch.GetTimestamp() - started;
        ResolveCalls++;
        if (charaBase == null)
            return null;
        CharacterBaseAddress = (nint)charaBase;
        var gameSkeleton = charaBase->Skeleton;
        if (gameSkeleton != null)
            RefreshWantedTransforms(gameSkeleton);
        return ModelTransformOf(charaBase).ToMatrix();
    }

    /// <summary>Composes native reference locals without changing the live pose.
    /// Parents must precede children. Roots of later partials first use a
    /// partial-0 namesake, then the connected indices when no namesake exists.
    /// Attached partial roots are omitted from the exported entries.</summary>
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

    /// <summary>The rendered slot transform, including native height and Customize+ scale.</summary>
    public unsafe Transform GetModelTransform()
    {
        var charaBase = (FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)
            _resolveCharacterBase(Actor);
        return ModelTransformOf(charaBase);
    }

    public Matrix4x4 GetModelMatrix() => GetModelTransform().ToMatrix();

    private static unsafe Transform ModelTransformOf(FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase* charaBase)
    {
        if (charaBase == null || charaBase->Skeleton == null)
            return Transform.Identity;

        // Use the transform that the game used to draw this model.
        // Customize+ can change DrawObject.Scale afterward, so reading that value
        // could make the skeleton overlay and IK targets miss the visible character.
        var rendered = charaBase->Skeleton->Transform;
        return new(rendered.Position, rendered.Rotation, rendered.Scale);
    }
}
