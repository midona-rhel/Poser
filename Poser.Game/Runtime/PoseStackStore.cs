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

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// The authored pose stores, one per (actor, slot), and the per-pass state
/// that sits beside them: the pre-layer animated baseline of every modified
/// bone and each attached partial's frame, which together convert between a
/// displayed transform and the pass's pre-reparent apply space. Also answers
/// the stack queries and edits that need no native pass.
/// </summary>
internal sealed class PoseStackStore
{
    // Pose info per (actor, slot) — never per skeleton instance.
    private readonly Dictionary<SkeletonKey, SkeletonPoseInfo> _poseInfos = new();

    private readonly Dictionary<(SkeletonKey Skeleton, int Partial, int Root),
        Poser.Game.Posing.PartialPoseFrame> _partialFrames = new();

    // Pre-layer animated baseline per modified concrete bone, captured by the
    // native skeleton update hook (read by GetAnimatedBaseline).
    private readonly Dictionary<(SkeletonKey Skeleton, int Partial, int Bone), Transform>
        _animatedBaselines = new();

    /// <summary>Reused snapshot buffer for <see cref="RemoveAnimatedBaselines"/>,
    /// which runs every frame for every registered-but-unposed skeleton.</summary>
    private readonly List<(SkeletonKey Skeleton, int Partial, int Bone)>
        _baselineRemovalBuffer = new();

    public int Count => _poseInfos.Count;

    public Dictionary<SkeletonKey, SkeletonPoseInfo>.KeyCollection Keys => _poseInfos.Keys;

    /// <summary>Struct enumeration for the per-frame rebuild — no boxed
    /// enumerator.</summary>
    public Dictionary<SkeletonKey, SkeletonPoseInfo>.Enumerator GetEnumerator() =>
        _poseInfos.GetEnumerator();

    public bool TryGet(SkeletonKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out SkeletonPoseInfo poseInfo) =>
        _poseInfos.TryGetValue(key, out poseInfo);

    /// <summary>
    /// The store for one (actor, slot), created on first use.
    ///
    /// <para>A plain lookup. It used to be an adoption point — purging stale
    /// instance-keyed stores, taking a parked pose, re-asserting the model
    /// transform — because the key carried the skeleton instance and a redraw
    /// filed the pose under a dead key. The key is names now, so there is
    /// nothing to adopt and nothing to purge: the store the caller gets is the
    /// one the pose was authored into, whichever skeleton is live.</para>
    /// </summary>
    public SkeletonPoseInfo GetPoseInfo(ISkeleton skeleton)
    {
        var slotKey = SkeletonKey.Of(skeleton);
        if (!_poseInfos.TryGetValue(slotKey, out var poseInfo))
        {
            poseInfo = new SkeletonPoseInfo();
            _poseInfos[slotKey] = poseInfo;
        }
        return poseInfo;
    }

    public void RemovePoseInfo(SkeletonKey key) => _poseInfos.Remove(key);

    public void RemovePartialFrames(SkeletonKey key)
    {
        foreach (var frame in _partialFrames.Keys.Where(x => x.Skeleton == key).ToArray())
            _partialFrames.Remove(frame);
    }

    /// <summary>Every store and every per-pass record (GPose exit).</summary>
    public void Clear()
    {
        _poseInfos.Clear();
        _animatedBaselines.Clear();
        _partialFrames.Clear();
    }

    /// <summary>The stores and baselines (unload).</summary>
    public void ClearStoresAndBaselines()
    {
        _poseInfos.Clear();
        _animatedBaselines.Clear();
    }

    // ── per-pass records ─────────────────────────────────────────────────

    public void ForgetPartialFrame((SkeletonKey Skeleton, int Partial, int Root) key) =>
        _partialFrames.Remove(key);

    public void SetPartialFrame(
        (SkeletonKey Skeleton, int Partial, int Root) key,
        Poser.Game.Posing.PartialPoseFrame frame) =>
        _partialFrames[key] = frame;

    public void SetAnimatedBaseline(
        (SkeletonKey Skeleton, int Partial, int Bone) key, Transform baseline) =>
        _animatedBaselines[key] = baseline;

    /// <summary>The frozen animated/reference baseline beneath the authored
    /// layers; a bone without applied layers has no captured baseline, and its
    /// current transform IS its baseline.</summary>
    public Transform GetAnimatedBaseline(IBone bone) =>
        bone is not VirtualBone && _animatedBaselines.TryGetValue(
            (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex),
            out var baseline)
            ? baseline
            : bone.LastTransform;

    /// <summary>Runs every frame for every registered-but-unposed skeleton
    /// (OnFrameworkUpdate), so it collects into a reused buffer instead of the
    /// LINQ chain + array it used to allocate per skeleton per frame.</summary>
    public void RemoveAnimatedBaselines(SkeletonKey slotKey)
    {
        if (_animatedBaselines.Count == 0)
            return;

        _baselineRemovalBuffer.Clear();
        foreach (var key in _animatedBaselines.Keys)
        {
            if (key.Skeleton == slotKey)
                _baselineRemovalBuffer.Add(key);
        }

        for (var i = 0; i < _baselineRemovalBuffer.Count; i++)
            _animatedBaselines.Remove(_baselineRemovalBuffer[i]);
    }

    // ── apply space ──────────────────────────────────────────────────────

    public Transform ToApplySpace(IBone bone, Transform visible)
    {
        var root = bone;
        while (!root.IsPartialRoot && root.ParentBone is { } parent && parent.PartialId == bone.PartialId)
            root = parent;
        if (!root.IsPartialRoot || root.IsSkeletonRoot)
            return visible;
        return _partialFrames.TryGetValue((SkeletonKey.Of(bone.Skeleton), bone.PartialId, root.BoneIndex), out var frame)
            ? frame.ToApply(visible) : visible;
    }

    public Transform FromApplySpace(IBone bone, Transform applied)
    {
        var root = bone;
        while (!root.IsPartialRoot && root.ParentBone is { } parent && parent.PartialId == bone.PartialId)
            root = parent;
        if (!root.IsPartialRoot || root.IsSkeletonRoot) return applied;
        return _partialFrames.TryGetValue((SkeletonKey.Of(bone.Skeleton), bone.PartialId, root.BoneIndex), out var frame)
            && frame.Before.Scale.X != 0 && frame.Before.Scale.Y != 0 && frame.Before.Scale.Z != 0
                ? frame.ToDisplay(applied) : applied;
    }

    // ── stack queries and edits ──────────────────────────────────────────

    public Transform? GetIkModification(IBone bone) =>
        _poseInfos.TryGetValue(SkeletonKey.Of(bone.Skeleton), out var pose)
            ? pose.GetPoseInfo(bone.BoneName, bone.PartialId).IkModification()
            : (Transform?)null;

    public void ResetBone(IBone bone)
    {
        var poseInfo = GetPoseInfo(bone.Skeleton);
        var bonePoseInfo = poseInfo.GetPoseInfo(bone.BoneName, bone.PartialId);
        bonePoseInfo.ClearStacks();
        _animatedBaselines.Remove(
            (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex));

    }

    public void ResetSkeleton(ISkeleton skeleton)
    {
        var slotKey = SkeletonKey.Of(skeleton);
        if (_poseInfos.TryGetValue(slotKey, out var poseInfo))
        {
            poseInfo.Clear();
        }
        RemoveAnimatedBaselines(slotKey);
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
        bonePoseInfo.RestoreInteractiveStacks(stacks);
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

        // LastRawTransform is the posed value (anim ⊕ existing stacks), so the
        // diff is only valid on top of those stacks — they must survive, like
        // Brio's PosingCapability.FlipBone which accumulates and never clears.
        bonePoseInfo.Apply(ToApplySpace(bone, newTransform), ToApplySpace(bone, bone.LastRawTransform));

    }

    private static Vector3 QuaternionToEuler(Quaternion r) => PoseMath.QuaternionToEuler(r);

    private static Quaternion EulerToQuaternion(Vector3 euler) => PoseMath.EulerToQuaternion(euler);
}
