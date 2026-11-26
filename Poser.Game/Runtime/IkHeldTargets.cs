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
using Poser.Game.Services;

using GameSkeleton = FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton;

using static Poser.Game.IkChainShapes;

namespace Poser.Game;

/// <summary>
/// Works out where a held IK chain's target is. When a chain starts holding,
/// this records the target the way its target mode needs: a point in the
/// world, an offset from a bone or another object in the scene, or a point
/// fixed to the actor. Each frame it turns that record into a position
/// relative to the actor's model. It keeps nothing of its own.
/// A target is where the end bone is shown, after Customize+ has changed the pose.
/// The solve converts it to the pose it works in (<see cref="CustomizePlusPose"/>).
/// </summary>
internal sealed unsafe class IkHeldTargets
{
    private readonly PoseStackStore _stacks;
    private readonly Poser.Game.Bindings.StableBindingRegistry _bindings;
    private readonly ISkeletonService _skeletons;

    public IkHeldTargets(PoseStackStore stacks, Poser.Game.Bindings.StableBindingRegistry bindings,
        CustomizePlusPose customizePlus, ISkeletonService skeletons)
    {
        _stacks = stacks;
        _bindings = bindings;
        _skeletons = skeletons;
        CustomizePlus = customizePlus;
    }

    /// <summary>Where Customize+ shows a bone, and the way back.</summary>
    public CustomizePlusPose CustomizePlus { get; }

    /// <summary>The bone a chain follows, found by partial and name in the skeleton its
    /// actor has now. A redraw builds a new skeleton and makes <paramref name="id"/> stale,
    /// but the actor and the bone's name stay, so the chain keeps following the bone.
    /// Null when the actor, the slot or the bone is gone.</summary>
    public Bone? FindBone(BoneId id) =>
        _bindings.Resolve(id.Skeleton.Actor) is { Success: true, Value: { } actor }
        && _skeletons.GetSkeleton(actor, id.Slot) is Skeleton { IsValid: true } skeleton
            ? skeleton.GetBoneByName(id.CanonicalName, id.PartialId)
            : null;

    public HeldTarget? CaptureEntityOffset(IBone endpoint, SelectionId target, IkChainState? previous = null)
    {
        return CaptureOffset(endpoint, ResolveIkEntityTransform(_bindings, target), previous);
    }

    /// <summary>Captures the tip's world transform and the bone edits already included in it.</summary>
    public HeldTarget? CaptureWorld(IBone endpoint)
    {
        RefreshCache(endpoint);
        if (global::Poser.Game.Entities.BoneWorld.Of(endpoint) is not { } tip)
            return null;
        var authored = _stacks.GetIkModification(endpoint);
        return new HeldTarget(
            tip.Position, tip.Rotation,
            authored?.Position ?? Vector3.Zero,
            authored?.Rotation ?? Quaternion.Identity);
    }

    /// <summary>Captures the held point relative to another bone.</summary>
    public HeldTarget? CaptureBoneOffset(IBone endpoint, IBone target, IkChainState? previous = null)
    {
        _ = target.LastTransform;
        RefreshCache(target);
        return CaptureOffset(endpoint, BoneWorld.Of(target), previous);
    }

    // Changing what a chain follows keeps its target where the IK solver had it.
    // A chain that was just turned on starts at the bone as shown, and is lined up
    // on its first solve.
    private HeldTarget? CaptureOffset(IBone endpoint, Transform? anchor, IkChainState? previous)
    {
        if (anchor is not { } followed) return null;
        var authored = _stacks.GetIkModification(endpoint) ?? Transform.Identity;
        Transform? tip;
        var converted = previous is { Config.Enabled: true }
            ? ResolveHeld(previous, endpoint, authored.Position, authored.Rotation, lastSolved: true)
            : null;
        if (converted is { } held && endpoint.Skeleton is Skeleton skeleton)
        {
            var frame = skeleton.GetModelTransform();
            tip = new Transform(Vector3.Transform(held.Position, frame.ToMatrix()),
                Quaternion.Normalize(frame.Rotation * held.Rotation), Vector3.One);
        }
        else
        {
            _ = endpoint.LastTransform;
            RefreshCache(endpoint);
            tip = BoneWorld.Of(endpoint);
        }
        if (tip is not { } point)
            return null;
        if (previous != null) previous.PendingRetarget = converted;
        var offset = RelativeTo(followed, point.Position, point.Rotation);
        return new HeldTarget(offset.Position, offset.Rotation, authored.Position, authored.Rotation)
            { Unseated = converted == null };
    }

    /// <summary>Works out the held target in the IK solver's coordinates, including edits made
    /// since it was recorded.
    /// With <paramref name="lastSolved"/>, returns the target the last solve used when there is one.
    /// Returns null when its actor or target is unavailable.</summary>
    public (Vector3 Position, Quaternion Rotation)? ResolveHeld(
        IkChainState ik, IBone endpoint, Vector3 authoredPosition, Quaternion authoredRotation, hkaPose* pose = null,
        bool lastSolved = false)
    {
        if (lastSolved && ik.Build.SolveTarget is { } lastTarget)
            return lastTarget;
        if (pose != null)
            RecordSolveParent(ik, endpoint, pose);
        if (pose != null && !ik.PreviewModelSpace
            && (ik.SeatPending || ik.Config.ActorAnchor is { Unseated: true } or { ParentUnseated: true }))
            Seat(ik, endpoint, authoredPosition, authoredRotation, pose);
        if (ik.HeldCapture is not { } capture)
            return null;
        if (ik.PreviewModelSpace)
            return (capture.Target + authoredPosition - capture.Translation,
                Quaternion.Normalize(capture.Rotation
                    * Quaternion.Inverse(capture.RotationDelta) * authoredRotation));
        if (ik.Config is { TargetMode: IkTargetMode.Actor, ActorAnchor: { } actorAnchor })
        {
            var actorTarget = actorAnchor.Resolve(authoredPosition, authoredRotation, ik.Build.SolveParentFrame);
            if (pose != null) ik.Build.SolveTarget = actorTarget;
            return actorTarget;
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
                if (ik.TargetBone is not { } targetId || FindBone(targetId) is not { } targetBone)
                    return null;
                RefreshCache(targetBone);
                if (global::Poser.Game.Entities.BoneWorld.Of(targetBone) is not { } anchor)
                    return null;
                (worldPosition, worldRotation) = FromRelative(anchor, capture.Target, capture.Rotation);
                break;
            case IkTargetMode.Entity:
                if (ik.TargetEntity is not { } entity
                    || ResolveIkEntityTransform(_bindings, entity) is not { } entityTransform)
                    return null;
                (worldPosition, worldRotation) = FromRelative(entityTransform, capture.Target, capture.Rotation);
                break;
            default:
                return null;
        }
        if (endpoint.Skeleton is not global::Poser.Game.Entities.Skeleton skeleton || !skeleton.IsValid)
            return null;
        var modelFrame = skeleton.GetModelTransform();
        if (!Matrix4x4.Invert(modelFrame.ToMatrix(), out var toModel)) return null;
        var position = Vector3.Transform(worldPosition, toModel)
            + (authoredPosition - capture.Translation);
        // Remove the actor's world rotation, then apply the bone's edits since capture.
        var frame = modelFrame.Rotation;
        if (!global::Poser.Domain.Transforms.TransformMath.IsFinite(frame) || frame.LengthSquared() < 1e-6f)
            return null;
        var rotation = Quaternion.Normalize(
            Quaternion.Inverse(Quaternion.Normalize(frame)) * worldRotation
            * Quaternion.Inverse(capture.RotationDelta) * authoredRotation);
        if (!global::Poser.Domain.Transforms.TransformMath.IsFinite(position) || !global::Poser.Domain.Transforms.TransformMath.IsFinite(rotation))
            return null;
        if (pose != null) ik.Build.SolveTarget = (position, rotation);
        return (position, rotation);
    }

    /// <summary>Aligns a newly enabled target with the end bone as it will be shown, before
    /// the first solve. A capture read from the screen can be a frame old.</summary>
    private void Seat(IkChainState ik, IBone endpoint, Vector3 authoredPosition, Quaternion authoredRotation, hkaPose* pose)
    {
        // Each path clears its mark only once the target is lined up: a target
        // that cannot be worked out this frame is tried again on the next.
        var native = Poser.Game.Entities.NativePoseRead.Model(pose, endpoint.BoneIndex);
        if (native == null)
            return;
        var posed = BoneApplyPass.ReadTransform(native);
        var shift = CustomizePlus.For(endpoint);
        var end = new Transform(shift.Shown(posed.Position), shift.Shown(posed.Rotation), posed.Scale);
        // Include the existing handle offset when enabling IK after a drag.
        var target = ik.PendingRetarget?.Position ?? end.Position + authoredPosition;
        var rotation = ik.PendingRetarget?.Rotation ?? end.Rotation;
        if (ik.Config is { TargetMode: IkTargetMode.Actor, ActorAnchor: { } anchor })
        {
            // A target carried over from FABRIK or Rope keeps its point; only the parent
            // bone is read again.
            var seated = anchor.ParentUnseated
                ? anchor with
                {
                    ParentTransform = anchor.ParentName == null ? null : ik.Build.SolveParentFrame,
                    ParentUnseated = false,
                }
                : anchor with
                {
                    Position = target, Rotation = end.Rotation,
                    AuthoredPosition = authoredPosition, AuthoredRotation = authoredRotation,
                    ParentTransform = anchor.ParentName == null ? null : ik.Build.SolveParentFrame,
                    Unseated = false,
                };
            if (!seated.IsValid)
                return;
            ik.Config = ik.Config with { ActorAnchor = seated };
            ik.HeldCapture = new(seated.Position, seated.Rotation, seated.AuthoredPosition, seated.AuthoredRotation);
            ik.SeatPending = false;
            return;
        }
        // Resolve once without entering Seat again, then adjust the captured offset.
        ik.SeatPending = false;
        if (ik.HeldCapture is not { } capture
            || ResolveHeld(ik, endpoint, authoredPosition, authoredRotation, pose) is not { } resolved)
        {
            ik.SeatPending = ik.HeldCapture != null;
            return;
        }
        var rotationDelta = Quaternion.Normalize(authoredRotation * Quaternion.Inverse(rotation)
            * resolved.Rotation * Quaternion.Inverse(authoredRotation) * capture.RotationDelta);
        var shifted = capture with
        {
            Translation = capture.Translation + resolved.Position - target,
            RotationDelta = rotationDelta,
        };
        if (TransformMath.IsFinite(shifted.Translation) && TransformMath.IsFinite(shifted.RotationDelta))
        {
            ik.HeldCapture = shifted;
            ik.PendingRetarget = null;
        }
        else
            ik.SeatPending = true;
    }

    /// <summary>Updates the cached transforms of the bones that were asked for. Reading a
    /// bone's LastTransform is what asks for it, so read that first.</summary>
    internal static void RefreshCache(IBone bone)
    {
        if (bone.Skeleton is global::Poser.Game.Entities.Skeleton skeleton && skeleton.IsValid)
            skeleton.UpdateBoneTransforms(global::Poser.Game.Entities.BoneCacheTypes.LastTransform);
    }

    private static PoseTransform AnchorPose(Transform value) => new(value.Position, value.Rotation, value.Scale);

    private static void RecordSolveParent(IkChainState ik, IBone endpoint, hkaPose* pose)
    {
        if (ik.Build.SolveParent == null)
        {
            var parent = NativeIkMembers(endpoint, ik.Config).FirstOrDefault()?.ParentBone;
            ik.Build.SolveParent = parent?.PartialId == endpoint.PartialId ? parent : null;
        }
        if (ik.Build.SolveParent is not { } solveParent)
            return;
        var native = Poser.Game.Entities.NativePoseRead.Model(pose, solveParent.BoneIndex);
        ik.Build.SolveParentFrame = native == null ? null : AnchorPose(BoneApplyPass.ReadTransform(native));
    }

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
            Unseated = true,
            ParentName = parent?.BoneName, ParentPartial = parent?.PartialId ?? 0,
            ParentTransform = parent == null ? null : AnchorPose(_stacks.ToApplySpace(parent, parent.LastTransform)),
        };
    }

    public IkChainConfig ChangeTargetMode(IkChainState previous, IBone endpoint, IkChainConfig config)
    {
        var authored = _stacks.GetIkModification(endpoint) ?? Transform.Identity;
        // Convert the solver's held target, rather than recapturing the pose before IK bends the limb.
        return ResolveHeld(previous, endpoint, authored.Position, authored.Rotation, lastSolved: true) is { } target
            ? AnchorAt(endpoint, config, target, previous.Build.SolveParentFrame) : config;
    }

    /// <summary>Puts a Two Joint or CCD target on the end bone where the last solve left it.
    /// For a chain coming from FABRIK or Rope, which keep no held target of their own.</summary>
    public IkChainConfig AnchorOnSolvedEnd(IBone endpoint, IkChainConfig config)
    {
        var posed = _stacks.ToApplySpace(endpoint, endpoint.LastRawTransform);
        var shift = CustomizePlus.For(endpoint);
        var end = new Transform(shift.Shown(posed.Position), shift.Shown(posed.Rotation), posed.Scale);
        var parent = NativeIkMembers(endpoint, config).FirstOrDefault()?.ParentBone;
        // An anchor left from when the chain last used Two Joint describes a pose that has
        // since changed.
        return AnchorAt(endpoint, config with { ActorAnchor = null, WorldAnchor = null },
            (end.Position, end.Rotation),
            parent?.PartialId == endpoint.PartialId
                ? AnchorPose(_stacks.ToApplySpace(parent, parent.LastRawTransform)) : null,
            parentUnseated: true);
    }

    private IkChainConfig AnchorAt(IBone endpoint, IkChainConfig config,
        (Vector3 Position, Quaternion Rotation) target, PoseTransform? parentFrame, bool parentUnseated = false)
    {
        var authored = _stacks.GetIkModification(endpoint) ?? Transform.Identity;
        if (config.TargetMode == IkTargetMode.Actor && config.ActorAnchor == null)
            return config with
            {
                ActorAnchor = CaptureActorAnchor(endpoint, config) with
                {
                    Position = target.Position, Rotation = target.Rotation,
                    ParentTransform = parentFrame, Unseated = false, ParentUnseated = parentUnseated,
                },
            };
        if (config.TargetMode == IkTargetMode.World && config.WorldAnchor == null
            && endpoint.Skeleton is Skeleton skeleton)
        {
            var frame = skeleton.GetModelTransform();
            return config with
            {
                WorldAnchor = new HeldTarget(Vector3.Transform(target.Position, frame.ToMatrix()),
                    Quaternion.Normalize(frame.Rotation * target.Rotation), authored.Position, authored.Rotation),
            };
        }
        return config;
    }

    /// <summary>Records the forearm and hand rotations, and the hand's own edited rotation, when
    /// End rotation changes.</summary>
    public IkEndRotationAnchor? CaptureEndAnchor(IBone endpoint, int secondJoint)
    {
        var forearm = endpoint.Skeleton.Bones.FirstOrDefault(b => b.PartialId == endpoint.PartialId
            && b.BoneIndex == secondJoint);
        if (forearm == null) return null;
        _ = endpoint.LastTransform;
        _ = forearm.LastTransform;
        RefreshCache(endpoint);
        var anchor = new IkEndRotationAnchor(
            _stacks.ToApplySpace(forearm, forearm.LastTransform).Rotation,
            _stacks.ToApplySpace(endpoint, endpoint.LastTransform).Rotation,
            (_stacks.GetIkModification(endpoint) ?? Transform.Identity).Rotation);
        return anchor.IsValid ? anchor : null;
    }
}
