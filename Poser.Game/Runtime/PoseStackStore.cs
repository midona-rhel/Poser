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
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Domain.Identity;
using Poser.Game.Entities;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

namespace Poser.Game;

/// <summary>
/// Keeps the bone edits of each actor and slot, found by bone name, so they
/// survive a redraw. Beside them it keeps two records the bone update takes
/// every frame, found by bone number: where each edited bone stood in the
/// animation before the edits, and where each attached partial skeleton (such
/// as the face or hair) sat on its parent. Those records are thrown away when
/// the skeleton is built again. They turn a bone's position as shown into the
/// position the bone update works with, and back.
/// </summary>
internal sealed class PoseStackStore
{
    // The edits per actor and slot, never per skeleton object.
    private readonly Dictionary<SkeletonKey, SkeletonPoseInfo> _poseInfos = new();

    private readonly Dictionary<(SkeletonKey Skeleton, int Partial, int Root),
        Poser.Game.Posing.PartialPoseFrame> _partialFrames = new();

    // Where each edited bone stood in the animation before the edits, taken by
    // the bone update (read by GetAnimatedBaseline).
    private readonly Dictionary<(SkeletonKey Skeleton, int Partial, int Bone), Transform>
        _animatedBaselines = new();

    // The skeleton build each actor-slot's records above were taken on.
    private readonly Dictionary<SkeletonKey, long> _builds = new();

    /// <summary>Reused list for <see cref="RemoveAnimatedBaselines"/>.</summary>
    private readonly List<(SkeletonKey Skeleton, int Partial, int Bone)>
        _baselineRemovalBuffer = new();

    public int Count => _poseInfos.Count;

    public Dictionary<SkeletonKey, SkeletonPoseInfo>.KeyCollection Keys => _poseInfos.Keys;

    /// <summary>Lets the bone update list the slots every frame without allocating.</summary>
    public Dictionary<SkeletonKey, SkeletonPoseInfo>.Enumerator GetEnumerator() =>
        _poseInfos.GetEnumerator();

    public bool TryGet(SkeletonKey key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out SkeletonPoseInfo poseInfo) =>
        _poseInfos.TryGetValue(key, out poseInfo);

    /// <summary>Gets or creates the actor-slot store. Replacing a skeleton
    /// wrapper keeps the same authored pose under its stable key.</summary>
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

    /// <summary>Forgets the slot's pose and every record of the pass kept for it.</summary>
    public void Remove(SkeletonKey key)
    {
        _poseInfos.Remove(key);
        _builds.Remove(key);
        RemoveAnimatedBaselines(key);
        RemovePartialFrames(key);
    }

    /// <summary>Throws away the slot's records kept by bone number when its skeleton has
    /// been built again since they were taken, because the same number can now be another
    /// bone. The apply pass calls this before it records new ones.</summary>
    public void UseBuild(SkeletonKey key, long revision)
    {
        if (_builds.TryGetValue(key, out var known) && known == revision)
            return;
        _builds[key] = revision;
        RemoveAnimatedBaselines(key);
        RemovePartialFrames(key);
    }

    private void RemovePartialFrames(SkeletonKey key)
    {
        foreach (var frame in _partialFrames.Keys.Where(x => x.Skeleton == key).ToArray())
            _partialFrames.Remove(frame);
    }

    /// <summary>Forgets every pose and every record of the pass, on leaving GPose and on unload.</summary>
    public void Clear()
    {
        _poseInfos.Clear();
        _animatedBaselines.Clear();
        _partialFrames.Clear();
        _builds.Clear();
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

    /// <summary>Where the bone stood in the animation before the edits. A bone with no
    /// edits has no record, and where it stands now is that place.</summary>
    public Transform GetAnimatedBaseline(IBone bone) =>
        bone is not VirtualBone && _animatedBaselines.TryGetValue(
            (SkeletonKey.Of(bone.Skeleton), bone.PartialId, bone.BoneIndex),
            out var baseline)
            ? baseline
            : bone.LastTransform;

    /// <summary>Runs every frame for every skeleton that has nothing to apply, so it
    /// collects into a reused list.</summary>
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
