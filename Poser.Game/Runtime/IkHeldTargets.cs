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

using static Poser.Game.IkChainShapes;

namespace Poser.Game;

/// <summary>
/// Where a held IK chain's target is: the capture each target mode takes
/// when a chain starts holding (world point, offset from a bone or a scene
/// entity, or an actor-frame anchor), and its resolution into the
/// endpoint's model space each frame. Reads live transforms and scene
/// bindings; stores nothing of its own.
/// </summary>
internal sealed unsafe class IkHeldTargets
{
    private readonly PoseStackStore _stacks;
    private readonly Poser.Game.Bindings.StableBindingRegistry _bindings;

    public IkHeldTargets(PoseStackStore stacks, Poser.Game.Bindings.StableBindingRegistry bindings)
    {
        _stacks = stacks;
        _bindings = bindings;
    }

    public HeldTarget? CaptureEntityOffset(IBone endpoint, SelectionId target)
    {
        RefreshCache(endpoint);
        if (BoneWorld.Of(endpoint) is not { } tip
            || ResolveIkEntityTransform(_bindings, target) is not { } anchor)
            return null;
        var authored = _stacks.GetIkModification(endpoint);
        // As in Bone mode, position is a world-space offset; only the held
        // orientation follows the anchor's rotation, with no scale inheritance.
        return new HeldTarget(tip.Position - anchor.Position,
            Quaternion.Normalize(Quaternion.Inverse(anchor.Rotation) * tip.Rotation),
            authored?.Position ?? Vector3.Zero,
            authored?.Rotation ?? Quaternion.Identity);
    }

    /// <summary>World mode's capture: the tip's world position and
    /// rotation now, with the authored deltas they were taken under.</summary>
    public HeldTarget? CaptureWorld(IBone endpoint)
    {
        RefreshCache(endpoint);
        if (global::Poser.Entities.BoneWorld.Of(endpoint) is not { } tip)
            return null;
        var authored = _stacks.GetIkModification(endpoint);
        return new HeldTarget(
            tip.Position, tip.Rotation,
            authored?.Position ?? Vector3.Zero,
            authored?.Rotation ?? Quaternion.Identity);
    }

    /// <summary>Bone mode's capture: the tip's world offset and rotation
    /// RELATIVE to the target bone now, with the authored deltas.</summary>
    public HeldTarget? CaptureBoneOffset(IBone endpoint, IBone target)
    {
        RefreshCache(endpoint);
        RefreshCache(target);
        if (global::Poser.Entities.BoneWorld.Of(endpoint) is not { } tip
            || global::Poser.Entities.BoneWorld.Of(target) is not { } anchor)
            return null;
        var authored = _stacks.GetIkModification(endpoint);
        return new HeldTarget(
            tip.Position - anchor.Position,
            Quaternion.Normalize(Quaternion.Inverse(anchor.Rotation) * tip.Rotation),
            authored?.Position ?? Vector3.Zero,
            authored?.Rotation ?? Quaternion.Identity);
    }

    /// <summary>The held target in the endpoint's model space this frame:
    /// the captured world point and rotation (World) or the target bone's
    /// world transform with the captured offsets (Bone), brought into model
    /// space through the skeleton's matrix, then moved and turned by what
    /// was authored since capture. Null when it cannot be resolved.</summary>
    public (Vector3 Position, Quaternion Rotation)? ResolveHeld(
        IkChainState ik, IBone endpoint, Vector3 authoredPosition, Quaternion authoredRotation, hkaPose* pose = null)
    {
        if (ik.HeldCapture is not { } capture)
            return null;
        if (ik.PreviewModelSpace)
            return (capture.Target + authoredPosition - capture.Translation,
                Quaternion.Normalize(capture.Rotation
                    * Quaternion.Inverse(capture.RotationDelta) * authoredRotation));
        if (ik.Config is { TargetMode: IkTargetMode.Actor, ActorAnchor: { } actorAnchor })
        {
            return actorAnchor.Resolve(authoredPosition, authoredRotation, ActorParentFrame(endpoint, actorAnchor, pose));
        }
        Vector3 worldPosition;
        Quaternion worldRotation;
        switch (ik.Config.TargetMode)
        {
            case Poser.Domain.Posing.IkTargetMode.World:
                worldPosition = capture.Target;
                worldRotation = capture.Rotation;
                break;
            case Poser.Domain.Posing.IkTargetMode.Bone:
                if (ik.TargetBone is not { } targetBone
                    || targetBone.Skeleton is not global::Poser.Entities.Skeleton targetSkeleton
                    || !targetSkeleton.IsValid)
                    return null;
                targetSkeleton.UpdateBoneTransforms(global::Poser.Entities.BoneCacheTypes.LastTransform);
                if (global::Poser.Entities.BoneWorld.Of(targetBone) is not { } anchor)
                    return null;
                worldPosition = anchor.Position + capture.Target;
                worldRotation = Quaternion.Normalize(anchor.Rotation * capture.Rotation);
                break;
            case IkTargetMode.Entity:
                if (ik.TargetEntity is not { } entity
                    || ResolveIkEntityTransform(_bindings, entity) is not { } entityTransform)
                    return null;
                worldPosition = entityTransform.Position + capture.Target;
                worldRotation = Quaternion.Normalize(entityTransform.Rotation * capture.Rotation);
                break;
            default:
                return null;
        }
        if (endpoint.Skeleton is not global::Poser.Entities.Skeleton skeleton || !skeleton.IsValid
            || !Matrix4x4.Invert(skeleton.GetModelMatrix(), out var toModel))
            return null;
        var position = Vector3.Transform(worldPosition, toModel)
            + (authoredPosition - capture.Translation);
        // System.Numerics multiplies right-to-left: the world rotation is
        // frame * model (model first, then the actor's frame), so model =
        // frame⁻¹ * world. The authored turn since capture rides on the
        // end, where the delta stack puts it.
        var frame = global::Poser.Domain.Transforms.Transform.FromMatrix(skeleton.GetModelMatrix()).Rotation;
        if (!global::Poser.Domain.Transforms.TransformMath.IsFinite(frame) || frame.LengthSquared() < 1e-6f)
            return null;
        var rotation = Quaternion.Normalize(
            Quaternion.Inverse(Quaternion.Normalize(frame)) * worldRotation
            * Quaternion.Inverse(capture.RotationDelta) * authoredRotation);
        if (!global::Poser.Domain.Transforms.TransformMath.IsFinite(position) || !global::Poser.Domain.Transforms.TransformMath.IsFinite(rotation))
            return null;
        return (position, rotation);
    }

    /// <summary>A bone the apply pass never visits (no stack) keeps a
    /// stale cached transform; a capture reads the live pose.</summary>
    internal static void RefreshCache(IBone bone)
    {
        if (bone.Skeleton is global::Poser.Entities.Skeleton skeleton && skeleton.IsValid)
            skeleton.UpdateBoneTransforms(global::Poser.Entities.BoneCacheTypes.LastTransform);
    }

    private static PoseTransform AnchorPose(Transform value) => new(value.Position, value.Rotation, value.Scale);
    public IkActorAnchor CaptureActorAnchor(IBone endpoint, IkChainConfig config)
    {
        var members = NativeIkMembers(endpoint, config);
        var parent = members.FirstOrDefault()?.ParentBone;
        if (parent?.PartialId != endpoint.PartialId) parent = null;
        _ = endpoint.LastTransform;
        if (parent != null) _ = parent.LastTransform;
        RefreshCache(endpoint);
        var model = _stacks.ToApplySpace(endpoint, endpoint.LastTransform);
        var authored = _stacks.GetIkModification(endpoint) ?? Transform.Identity;
        return new(model.Position, model.Rotation, authored.Position, authored.Rotation)
        {
            ParentName = parent?.BoneName, ParentPartial = parent?.PartialId ?? 0,
            ParentTransform = parent == null ? null : AnchorPose(_stacks.ToApplySpace(parent, parent.LastTransform)),
        };
    }

    public PoseTransform? ActorParentFrame(IBone endpoint, IkActorAnchor anchor, hkaPose* pose = null)
    {
        if (anchor.ParentName == null) return null;
        var parent = endpoint.Skeleton.Bones.FirstOrDefault(b => b.PartialId == anchor.ParentPartial
            && b.BoneName == anchor.ParentName);
        if (parent == null) return null;
        // During evaluation, caches may precede an edited grandparent. Read
        // this pass's model transform; the chosen parent is outside the solve.
        if (pose != null && parent.PartialId == endpoint.PartialId)
        {
            var native = pose->AccessBoneModelSpace(parent.BoneIndex, hkaPose.PropagateOrNot.DontPropagate);
            return native == null ? null : AnchorPose(BoneApplyPass.ReadTransform(native));
        }
        // An off-screen parent need not have a recent display cache. Mark it
        // wanted before refreshing, including on the first drag after rotation.
        _ = parent.LastTransform;
        RefreshCache(parent);
        return AnchorPose(_stacks.ToApplySpace(parent, parent.LastTransform));
    }
}
