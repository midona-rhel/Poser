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

    // Changing what a chain follows preserves its held point in the solver's frame.
    // A newly enabled chain starts at the displayed bone and is aligned on its first solve.
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
        return new HeldTarget(
            point.Position - followed.Position,
            Quaternion.Normalize(Quaternion.Inverse(followed.Rotation) * point.Rotation),
            authored.Position, authored.Rotation) { Unseated = converted == null };
    }

    /// <summary>Resolves the held target into solver coordinates, including edits made since capture.
    /// With <paramref name="lastSolved"/>, returns the target the last solve used when there is one.
    /// Returns null when its actor or target is unavailable.</summary>
    public (Vector3 Position, Quaternion Rotation)? ResolveHeld(
        IkChainState ik, IBone endpoint, Vector3 authoredPosition, Quaternion authoredRotation, hkaPose* pose = null,
        bool lastSolved = false)
    {
        if (lastSolved && ik.SolveTarget is { } lastTarget)
            return lastTarget;
        if (pose != null)
            RecordSolveParent(ik, endpoint, pose);
        if (pose != null && !ik.PreviewModelSpace
            && (ik.SeatPending || ik.Config.ActorAnchor is { Unseated: true }))
            Seat(ik, endpoint, authoredPosition, authoredRotation, pose);
        if (ik.HeldCapture is not { } capture)
            return null;
        if (ik.PreviewModelSpace)
            return (capture.Target + authoredPosition - capture.Translation,
                Quaternion.Normalize(capture.Rotation
                    * Quaternion.Inverse(capture.RotationDelta) * authoredRotation));
        if (ik.Config is { TargetMode: IkTargetMode.Actor, ActorAnchor: { } actorAnchor })
        {
            var actorTarget = actorAnchor.Resolve(authoredPosition, authoredRotation, ik.SolveParentFrame);
            if (pose != null) ik.SolveTarget = actorTarget;
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
                if (ik.TargetBone is not { } targetBone
                    || targetBone.Skeleton is not global::Poser.Game.Entities.Skeleton targetSkeleton
                    || !targetSkeleton.IsValid)
                    return null;
                targetSkeleton.UpdateBoneTransforms(global::Poser.Game.Entities.BoneCacheTypes.LastTransform);
                if (global::Poser.Game.Entities.BoneWorld.Of(targetBone) is not { } anchor)
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
        if (pose != null) ik.SolveTarget = (position, rotation);
        return (position, rotation);
    }

    /// <summary>Aligns a newly enabled target with the bone before solving.
    /// Customize+ edits the pose after this stage, so a capture taken from the screen is not
    /// where the solver sees the bone.</summary>
    private void Seat(IkChainState ik, IBone endpoint, Vector3 authoredPosition, Quaternion authoredRotation, hkaPose* pose)
    {
        // Each path clears its mark only once seated: a target that cannot be
        // resolved this frame is tried again on the next.
        var native = Poser.Game.Entities.NativePoseRead.Model(pose, endpoint.BoneIndex);
        if (native == null)
            return;
        var end = BoneApplyPass.ReadTransform(native);
        // Include the existing handle offset when enabling IK after a drag.
        var target = ik.PendingRetarget?.Position ?? end.Position + authoredPosition;
        var rotation = ik.PendingRetarget?.Rotation ?? end.Rotation;
        if (ik.Config is { TargetMode: IkTargetMode.Actor, ActorAnchor: { } anchor })
        {
            var seated = anchor with
            {
                Position = target, Rotation = end.Rotation,
                AuthoredPosition = authoredPosition, AuthoredRotation = authoredRotation,
                ParentTransform = anchor.ParentName == null ? null : ik.SolveParentFrame,
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

    /// <summary>Refreshes requested bone transforms. Read LastTransform first to request a new bone.</summary>
    internal static void RefreshCache(IBone bone)
    {
        if (bone.Skeleton is global::Poser.Game.Entities.Skeleton skeleton && skeleton.IsValid)
            skeleton.UpdateBoneTransforms(global::Poser.Game.Entities.BoneCacheTypes.LastTransform);
    }

    private static PoseTransform AnchorPose(Transform value) => new(value.Position, value.Rotation, value.Scale);

    private static void RecordSolveParent(IkChainState ik, IBone endpoint, hkaPose* pose)
    {
        if (ik.SolveParent == null)
        {
            var parent = NativeIkMembers(endpoint, ik.Config).FirstOrDefault()?.ParentBone;
            ik.SolveParent = parent?.PartialId == endpoint.PartialId ? parent : null;
        }
        if (ik.SolveParent is not { } solveParent)
            return;
        var native = Poser.Game.Entities.NativePoseRead.Model(pose, solveParent.BoneIndex);
        ik.SolveParentFrame = native == null ? null : AnchorPose(BoneApplyPass.ReadTransform(native));
    }

    /// <summary>
    /// How a move in the pose the last solve produced appears on screen, and the reverse.
    /// Customize+ edits the pose after the solve. An edit that is passed down (Propagate) moves
    /// every bone below the edited one: scaled along the model axes about that bone, by the
    /// bone's scale or its separate child scale, then turned about it. An edit that is not
    /// passed down changes only its own bone. So no bone's own scale says how the limb below it
    /// is displayed.
    /// This measures it instead: the linear map that carries each step from bone to bone, from
    /// the bone above the chain down to the end, from the solved pose to the screen. Where the
    /// steps don't span a direction (a straight limb), the limb's length on screen over its
    /// solved length is used.
    /// Both are identity before the first solve, for a chain whose parent is in another partial,
    /// and when the map can't be inverted.
    /// </summary>
    public (Matrix4x4 ToDisplayed, Matrix4x4 ToSolve) DisplayedFrame(IkChainState ik, IBone endpoint)
    {
        var none = (Matrix4x4.Identity, Matrix4x4.Identity);
        if (ik.SolveParent is not { } parent || ik.SolveParentFrame == null)
            return none;
        var bones = NativeIkMembers(endpoint, ik.Config);
        bones.Insert(0, parent);
        foreach (var bone in bones) _ = bone.LastTransform;
        RefreshCache(parent);
        // Least squares for the map in "solved step × map = displayed step":
        // (Σ solvedᵀ solved) × map = Σ solvedᵀ displayed.
        Matrix4x4 solvedSquare = default, solvedByDisplayed = default;
        float solvedLength = 0, displayedLength = 0;
        for (int i = 1; i < bones.Count; i++)
        {
            var solved = _stacks.ToApplySpace(bones[i], bones[i].LastRawTransform).Position
                - _stacks.ToApplySpace(bones[i - 1], bones[i - 1].LastRawTransform).Position;
            var displayed = _stacks.ToApplySpace(bones[i], bones[i].LastTransform).Position
                - _stacks.ToApplySpace(bones[i - 1], bones[i - 1].LastTransform).Position;
            solvedSquare += Outer(solved, solved);
            solvedByDisplayed += Outer(solved, displayed);
            solvedLength += solved.Length();
            displayedLength += displayed.Length();
        }
        // A weak pull toward the plain length ratio settles the directions the steps don't span.
        float pull = 1e-4f * (solvedSquare.M11 + solvedSquare.M22 + solvedSquare.M33) / 3;
        if (!Matrix4x4.Invert(solvedSquare + Matrix4x4.CreateScale(pull), out var inverse))
            return none;
        var toDisplayed = inverse
            * (solvedByDisplayed + Matrix4x4.CreateScale(pull * displayedLength / solvedLength));
        // Zero-scaled bones exist (a limb hidden by Customize+, a collapsed leash link) and give a
        // map with no way back. A solver that returned NaN gives a NaN map, which Invert accepts.
        return float.IsFinite(toDisplayed.GetDeterminant()) && Matrix4x4.Invert(toDisplayed, out var toSolve)
            ? (toDisplayed, toSolve) : none;
    }

    private static Matrix4x4 Outer(Vector3 a, Vector3 b) => new(
        a.X * b.X, a.X * b.Y, a.X * b.Z, 0,
        a.Y * b.X, a.Y * b.Y, a.Y * b.Z, 0,
        a.Z * b.X, a.Z * b.Y, a.Z * b.Z, 0,
        0, 0, 0, 0);

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
        if (ResolveHeld(previous, endpoint, authored.Position, authored.Rotation, lastSolved: true) is not { } target)
            return config;
        // Convert the solver's held target, rather than recapturing the pose before IK bends the limb.
        if (config.TargetMode == IkTargetMode.Actor && config.ActorAnchor == null)
            return config with
            {
                ActorAnchor = CaptureActorAnchor(endpoint, config) with
                {
                    Position = target.Position, Rotation = target.Rotation,
                    ParentTransform = previous.SolveParentFrame, Unseated = false,
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

    /// <summary>Captures the forearm, hand and authored rotation when End rotation changes.</summary>
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
