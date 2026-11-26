using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Game.Bindings;
using Poser.Game.Entities;
using Poser.Game.Services;
using DomainTransform = Poser.Domain.Transforms.PoseTransform;
using LegacyLayer = Poser.Domain.Posing.BonePoseTransformInfo;
using LegacyTransform = Poser.Domain.Transforms.Transform;

namespace Poser.Game.Transforms;

/// <summary>
/// Resolves stable transform targets and applies or captures their runtime state.
/// </summary>
public sealed class TransformRuntimePort : ITransformRuntimePort
{
    private readonly StableBindingRegistry _bindings;
    private readonly PosingService _actors;
    private readonly BonePosingService _bones;

    public TransformRuntimePort(
        StableBindingRegistry bindings,
        PosingService actors,
        BonePosingService bones)
    {
        _bindings = bindings;
        _actors = actors;
        _bones = bones;
    }

    public TransformPortResult Capture(TransformTargetId target)
    {
        return target.Kind switch
        {
            TransformTargetKind.Collider when target.Collider is { } collider => CaptureCollider(target, collider),
            TransformTargetKind.Actor when target.Actor is { } actor =>
                CaptureActor(target, actor),
            TransformTargetKind.Bone when target.Bone is { } bone =>
                CaptureBone(target, bone),
            TransformTargetKind.Light when target.Light is { } light =>
                CaptureLight(target, light),
            TransformTargetKind.Prop when target.Prop is { } prop =>
                CaptureProp(target, prop),
            TransformTargetKind.WorldObject when target.WorldObject is { } world =>
                CaptureWorldObject(target, world),
            _ => TransformPortResult.Fail(
                TransformPortStatus.IdentityMismatch,
                $"Malformed transform target {target}."),
        };
    }

    public TransformPortResult ApplyAbsolute(
        TransformTargetState baseline,
        DomainTransform desired,
        bool rawBaseline = false)
    {
        if (!DomainTransform.TryCreate(
                desired.Position,
                desired.Rotation,
                desired.Scale,
                out desired,
                out var error))
            return TransformPortResult.Fail(
                TransformPortStatus.InvalidTransform,
                error ?? "Invalid transform.");

        if (baseline.Target.Collider is { } colliderId)
            return WriteCollider(colliderId, desired);

        if (baseline.Target.Kind == TransformTargetKind.Actor &&
            baseline.Target.Actor is { } actorId)
        {
            var resolved = _bindings.Resolve(actorId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            return _actors.SetTransformOverride(resolved.Value!, LegacyTransform.FromPose(desired))
                ? TransformPortResult.Ok()
                : TransformPortResult.Fail(TransformPortStatus.Rejected, "The actor transform could not be written.");
        }

        if (baseline.Target.Kind == TransformTargetKind.Bone &&
            baseline.Target.Bone is { } boneId)
        {
            var resolved = _bindings.Resolve(boneId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            var bone = resolved.Value!;
            var suppressed = _bones.LinkedBonesSuppressed;
            _bones.LinkedBonesSuppressed = true;
            try
            {
                _bones.RestorePoseStacks(
                    bone,
                    ToLegacyLayers(baseline.Pose));
                // Baking an expression into the pose works from the bone's
                // value as it is now, before face bones are moved onto the
                // head, the way a pose file loads. The value read when the
                // change began differs from it on face bones.
                _bones.ApplyTransform(
                    bone,
                    LegacyTransform.FromPose(desired),
                    rawBaseline ? bone.LastRawTransform : LegacyTransform.FromPose(baseline.Transform));
            }
            finally
            {
                _bones.LinkedBonesSuppressed = suppressed;
            }
            return TransformPortResult.Ok();
        }

        if (baseline.Target.Kind == TransformTargetKind.Light &&
            baseline.Target.Light is { } lightId)
        {
            var resolved = _bindings.Resolve(lightId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            resolved.Value!.Transform = LegacyTransform.FromPose(desired);
            return TransformPortResult.Ok();
        }

        if (baseline.Target.Kind == TransformTargetKind.Prop &&
            baseline.Target.Prop is { } applyPropId)
        {
            var resolved = _bindings.Resolve(applyPropId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            resolved.Value!.Transform = LegacyTransform.FromPose(desired);
            return TransformPortResult.Ok();
        }

        if (baseline.Target.Kind == TransformTargetKind.WorldObject &&
            baseline.Target.WorldObject is { } applyWorldId)
        {
            var resolved = _bindings.Resolve(applyWorldId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            resolved.Value!.Transform = LegacyTransform.FromPose(desired);
            return TransformPortResult.Ok();
        }

        return TransformPortResult.Fail(
            TransformPortStatus.IdentityMismatch,
            $"Malformed transform target {baseline.Target}.");
    }

    public TransformPortResult Restore(TransformTargetState state)
    {
        if (state.Target.Collider is { } colliderId)
            return WriteCollider(colliderId, state.Transform);

        if (state.Target.Kind == TransformTargetKind.Actor &&
            state.Target.Actor is { } actorId)
        {
            var resolved = _bindings.Resolve(actorId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            if (state.HasOverride)
                return _actors.SetTransformOverride(resolved.Value!, LegacyTransform.FromPose(state.Transform))
                    ? TransformPortResult.Ok()
                    : TransformPortResult.Fail(TransformPortStatus.Rejected, "The actor transform could not be restored.");
            else
                _actors.ClearTransformOverride(resolved.Value!);
            return TransformPortResult.Ok();
        }

        if (state.Target.Kind == TransformTargetKind.Bone &&
            state.Target.Bone is { } boneId)
        {
            var resolved = _bindings.Resolve(boneId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            _bones.RestorePoseStacks(
                resolved.Value!,
                ToLegacyLayers(state.Pose));
            return TransformPortResult.Ok();
        }

        if (state.Target.Kind == TransformTargetKind.Light &&
            state.Target.Light is { } lightId)
        {
            var resolved = _bindings.Resolve(lightId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            resolved.Value!.Transform = LegacyTransform.FromPose(state.Transform);
            return TransformPortResult.Ok();
        }

        if (state.Target.Kind == TransformTargetKind.Prop &&
            state.Target.Prop is { } restorePropId)
        {
            var resolved = _bindings.Resolve(restorePropId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            resolved.Value!.Transform = LegacyTransform.FromPose(state.Transform);
            return TransformPortResult.Ok();
        }

        if (state.Target.Kind == TransformTargetKind.WorldObject &&
            state.Target.WorldObject is { } restoreWorldId)
        {
            var resolved = _bindings.Resolve(restoreWorldId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            resolved.Value!.Transform = LegacyTransform.FromPose(state.Transform);
            return TransformPortResult.Ok();
        }

        return TransformPortResult.Fail(
            TransformPortStatus.IdentityMismatch,
            $"Malformed transform target {state.Target}.");
    }

    /// <summary>
    /// A light's transform is its whole state: there is no override to clear
    /// and no pose stack to rebuild, so the capture always reports
    /// HasOverride = true and carries an empty pose.
    /// </summary>
    private TransformPortResult CaptureLight(
        TransformTargetId target,
        LightId lightId)
    {
        var resolved = _bindings.Resolve(lightId);
        if (!resolved.Success)
            return FromBinding(resolved.Status, resolved.Detail);
        var converted = FromLegacy(resolved.Value!.Transform);
        if (converted == null)
            return TransformPortResult.Fail(
                TransformPortStatus.InvalidTransform,
                $"Light {lightId} returned an invalid transform.");
        return TransformPortResult.Ok(new TransformTargetState(
            target,
            converted.Value,
            new BonePose(),
            true));
    }

    private TransformPortResult CaptureCollider(TransformTargetId target, OverlayId id)
    {
        var resolved = _bindings.Resolve(id);
        if (!resolved.Success) return FromBinding(resolved.Status, resolved.Detail);
        return resolved.Value!.State.Collider is { } collider
            ? TransformPortResult.Ok(new TransformTargetState(target, collider.Transform, new BonePose(), true))
            : TransformPortResult.Fail(TransformPortStatus.IdentityMismatch, "Not an IK collider.");
    }

    private TransformPortResult WriteCollider(OverlayId id, DomainTransform desired)
    {
        var resolved = _bindings.Resolve(id);
        if (!resolved.Success) return FromBinding(resolved.Status, resolved.Detail);
        var node = resolved.Value!;
        if (node.State.Collider is not { } collider)
            return TransformPortResult.Fail(TransformPortStatus.IdentityMismatch, "Not an IK collider.");
        node.State = node.State with { Collider = collider with { Transform = desired } };
        return TransformPortResult.Ok();
    }

    private TransformPortResult CaptureProp(
        TransformTargetId target,
        PropId propId)
    {
        var resolved = _bindings.Resolve(propId);
        if (!resolved.Success)
            return FromBinding(resolved.Status, resolved.Detail);
        var converted = FromLegacy(resolved.Value!.Transform);
        if (converted == null)
            return TransformPortResult.Fail(
                TransformPortStatus.InvalidTransform,
                $"Object {propId} returned an invalid transform.");
        return TransformPortResult.Ok(new TransformTargetState(
            target,
            converted.Value,
            new BonePose(),
            true));
    }

    /// <summary>
    /// An adopted world object's transform is its whole editable state, exactly
    /// as a prop's is. Undoing one is a WRITE BACK onto the same borrowed
    /// object — never a release: the transform history moves things, and only
    /// the lifecycle history gives them back.
    /// </summary>
    private TransformPortResult CaptureWorldObject(
        TransformTargetId target,
        WorldObjectId worldObjectId)
    {
        var resolved = _bindings.Resolve(worldObjectId);
        if (!resolved.Success)
            return FromBinding(resolved.Status, resolved.Detail);
        var converted = FromLegacy(resolved.Value!.Transform);
        if (converted == null)
            return TransformPortResult.Fail(
                TransformPortStatus.InvalidTransform,
                $"World object {worldObjectId} returned an invalid transform.");
        return TransformPortResult.Ok(new TransformTargetState(
            target,
            converted.Value,
            new BonePose(),
            true));
    }

    private TransformPortResult CaptureActor(
        TransformTargetId target,
        ActorId actorId)
    {
        var resolved = _bindings.Resolve(actorId);
        if (!resolved.Success)
            return FromBinding(resolved.Status, resolved.Detail);
        var actor = resolved.Value!;
        var converted = FromLegacy(_actors.GetEffectiveTransform(actor));
        if (converted == null)
            return TransformPortResult.Fail(
                TransformPortStatus.InvalidTransform,
                $"Actor {actorId} returned an invalid transform.");
        return TransformPortResult.Ok(new TransformTargetState(
            target,
            converted.Value,
            new BonePose(),
            _actors.HasTransformOverride(actor)));
    }

    private TransformPortResult CaptureBone(
        TransformTargetId target,
        BoneId boneId)
    {
        var resolved = _bindings.Resolve(boneId);
        if (!resolved.Success)
            return FromBinding(resolved.Status, resolved.Detail);
        var bone = resolved.Value!;
        // A bone can hold a value that is not a usable position, rotation and
        // scale. Its nearest usable value is read instead, so a move can still
        // start and be put back.
        var converted = FromLegacy(bone.LastTransform)
            ?? FromLegacy(Sanitized(bone.LastTransform));
        if (converted == null)
            return TransformPortResult.Fail(
                TransformPortStatus.InvalidTransform,
                $"Bone {boneId} returned an invalid transform.");

        var stacks = _bones.CapturePoseStacks(bone);
        var animatedBaselineRotation =
            _bones.GetAnimatedBaseline(bone).Rotation;
        var result = LegacyPoseStackConverter.Convert(
            target,
            converted.Value,
            animatedBaselineRotation,
            stacks);
        return result.State is { } state && PoseMath.GetMirrorBoneName(bone.BoneName) != null
            ? result with { State = state with { ParentRotationFromRest = ParentRotationFromRest(bone) } }
            : result;
    }

    /// <summary>
    /// The parent's current rotation compared with its rotation in the skeleton's
    /// starting pose. The current rotation is read from the game's pose, because the
    /// stored copy is only kept up to date for bones on screen. A face bone's parent
    /// is the head in the face skeleton, which the starting pose lists under the
    /// body skeleton's head of the same name.
    /// </summary>
    private static unsafe Quaternion ParentRotationFromRest(IBone bone)
    {
        if (bone.ParentBone is not { } parent || bone.Skeleton is not Skeleton skeleton)
            return Quaternion.Identity;
        var native = skeleton.GetGameSkeletonPointer();
        if (native == null || parent.PartialId < 0 || parent.PartialId >= native->PartialSkeletonCount)
            return Quaternion.Identity;
        var current = NativePoseRead.Model(
            native->PartialSkeletons[parent.PartialId].GetHavokPose(0), parent.BoneIndex);
        if (current == null)
            return Quaternion.Identity;
        var rest = skeleton.CaptureReferencePose();
        var match = rest.FirstOrDefault(entry => entry.Bone == parent);
        if (match.Bone == null)
            match = rest.FirstOrDefault(entry =>
                entry.Bone.PartialId == 0 && entry.Bone.BoneName == parent.BoneName);
        if (match.Bone == null)
            return Quaternion.Identity;
        return Quaternion.Normalize(
            new Quaternion(current->Rotation.X, current->Rotation.Y, current->Rotation.Z, current->Rotation.W)
            * Quaternion.Inverse(match.Reference.Rotation));
    }

    private static TransformPortResult FromBinding(
        BindingStatus status,
        string? detail) =>
        TransformPortResult.Fail(
            status switch
            {
                BindingStatus.StaleTarget =>
                    TransformPortStatus.StaleTarget,
                BindingStatus.IdentityMismatch =>
                    TransformPortStatus.IdentityMismatch,
                _ => TransformPortStatus.NativeUnavailable,
            },
            detail ?? "Native binding is unavailable.");

    private static DomainTransform? FromLegacy(LegacyTransform value) =>
        DomainTransform.TryCreate(
            value.Position,
            value.Rotation,
            value.Scale,
            out var converted,
            out _)
            ? converted
            : null;

    private static LegacyTransform Sanitized(LegacyTransform value)
    {
        var rotation = Poser.Domain.Transforms.TransformMath.IsValidRotation(value.Rotation)
            ? Poser.Domain.Transforms.TransformMath.NormalizeRotation(value.Rotation)
            : System.Numerics.Quaternion.Identity;
        var position = Poser.Domain.Transforms.TransformMath.IsFinite(value.Position)
            ? value.Position
            : System.Numerics.Vector3.Zero;
        var scale = Poser.Domain.Transforms.TransformMath.IsFinite(value.Scale)
            ? value.Scale
            : System.Numerics.Vector3.One;
        return new LegacyTransform(position, rotation, scale);
    }

    private static IReadOnlyList<LegacyLayer> ToLegacyLayers(BonePose pose) =>
        pose.InteractiveOnly().Layers.Select(layer =>
            new LegacyLayer(
                layer.Propagation,
                new LegacyTransform(
                    layer.Delta.Position,
                    layer.Delta.Rotation,
                    layer.Delta.Scale))).ToArray();
}
