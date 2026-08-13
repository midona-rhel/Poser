using Dalamud.Plugin.Services;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game.Bindings;
using Poser.Services;
using DomainComponents = Poser.Domain.Posing.TransformComponents;
using DomainTransform = Poser.Domain.Transforms.PoseTransform;
using LegacyComponents = Poser.Core.TransformComponents;
using LegacyLayer = Poser.Core.BonePoseTransformInfo;
using LegacyTransform = Poser.Transform;

namespace Poser.Game.Transforms;

/// <summary>
/// Runtime-owned native boundary for clean actor and bone transform commands.
/// </summary>
public sealed class TransformRuntimePort : ITransformRuntimePort
{
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly PosingService _actors;
    private readonly BonePosingService _bones;

    public TransformRuntimePort(
        IFramework framework,
        StableBindingRegistry bindings,
        PosingService actors,
        BonePosingService bones)
    {
        _framework = framework;
        _bindings = bindings;
        _actors = actors;
        _bones = bones;
    }

    public TransformPortResult Capture(TransformTargetId target)
    {
        if (!OnFrameworkThread())
            return FrameworkThreadFailure();

        return target.Kind switch
        {
            TransformTargetKind.Actor when target.Actor is { } actor =>
                CaptureActor(target, actor),
            TransformTargetKind.Bone when target.Bone is { } bone =>
                CaptureBone(target, bone),
            TransformTargetKind.Light when target.Light is { } light =>
                CaptureLight(target, light),
            TransformTargetKind.Prop when target.Prop is { } prop =>
                CaptureProp(target, prop),
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
        if (!OnFrameworkThread())
            return FrameworkThreadFailure();
        if (!DomainTransform.TryCreate(
                desired.Position,
                desired.Rotation,
                desired.Scale,
                out desired,
                out var error))
            return TransformPortResult.Fail(
                TransformPortStatus.InvalidTransform,
                error ?? "Invalid transform.");

        if (baseline.Target.Kind == TransformTargetKind.Actor &&
            baseline.Target.Actor is { } actorId)
        {
            var resolved = _bindings.Resolve(actorId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            _actors.SetTransformOverride(
                resolved.Value!,
                ToLegacy(desired));
            return TransformPortResult.Ok();
        }

        if (baseline.Target.Kind == TransformTargetKind.Bone &&
            baseline.Target.Bone is { } boneId)
        {
            var resolved = _bindings.Resolve(boneId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            var bone = resolved.Value!;
            var linked = _bones.LinkedBonesEnabled;
            _bones.LinkedBonesEnabled = false;
            try
            {
                _bones.RestorePoseStacks(
                    bone,
                    ToLegacyLayers(baseline.Pose));
                // The raw basis is read from the LIVE bone at apply time:
                // the facial bake applies its captured absolutes against
                // the settled LastRawTransform, exactly as a pose file
                // loads. The captured baseline transform (LastTransform)
                // diverges on face partials.
                _bones.ApplyTransform(
                    bone,
                    ToLegacy(desired),
                    rawBaseline ? bone.LastRawTransform : ToLegacy(baseline.Transform));
            }
            finally
            {
                _bones.LinkedBonesEnabled = linked;
            }
            return TransformPortResult.Ok();
        }

        if (baseline.Target.Kind == TransformTargetKind.Light &&
            baseline.Target.Light is { } lightId)
        {
            var resolved = _bindings.Resolve(lightId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            resolved.Value!.Transform = ToLegacy(desired);
            return TransformPortResult.Ok();
        }

        if (baseline.Target.Kind == TransformTargetKind.Prop &&
            baseline.Target.Prop is { } applyPropId)
        {
            var resolved = _bindings.Resolve(applyPropId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            resolved.Value!.Transform = ToLegacy(desired);
            return TransformPortResult.Ok();
        }

        return TransformPortResult.Fail(
            TransformPortStatus.IdentityMismatch,
            $"Malformed transform target {baseline.Target}.");
    }

    public TransformPortResult Restore(TransformTargetState state)
    {
        if (!OnFrameworkThread())
            return FrameworkThreadFailure();

        if (state.Target.Kind == TransformTargetKind.Actor &&
            state.Target.Actor is { } actorId)
        {
            var resolved = _bindings.Resolve(actorId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            if (state.HasOverride)
                _actors.SetTransformOverride(
                    resolved.Value!,
                    ToLegacy(state.Transform));
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
            resolved.Value!.Transform = ToLegacy(state.Transform);
            return TransformPortResult.Ok();
        }

        if (state.Target.Kind == TransformTargetKind.Prop &&
            state.Target.Prop is { } restorePropId)
        {
            var resolved = _bindings.Resolve(restorePropId);
            if (!resolved.Success)
                return FromBinding(resolved.Status, resolved.Detail);
            resolved.Value!.Transform = ToLegacy(state.Transform);
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
                $"Prop {propId} returned an invalid transform.");
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
        var converted = FromLegacy(bone.LastTransform);
        if (converted == null)
            return TransformPortResult.Fail(
                TransformPortStatus.InvalidTransform,
                $"Bone {boneId} returned an invalid transform.");

        var stacks = _bones.CapturePoseStacks(bone);
        var animatedBaselineRotation =
            _bones.GetAnimatedBaseline(bone).Rotation;
        return LegacyPoseStackConverter.Convert(
            target,
            converted.Value,
            animatedBaselineRotation,
            stacks);
    }

    private bool OnFrameworkThread() =>
        _framework.IsInFrameworkUpdateThread;

    private static TransformPortResult FrameworkThreadFailure() =>
        TransformPortResult.Fail(
            TransformPortStatus.NativeUnavailable,
            "Transform runtime port must execute on the framework thread.");

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

    private static LegacyTransform ToLegacy(DomainTransform value) =>
        new(value.Position, value.Rotation, value.Scale);

    private static IReadOnlyList<LegacyLayer> ToLegacyLayers(BonePose pose) =>
        pose.InteractiveOnly().Layers.Select(layer =>
            new LegacyLayer(
                ToLegacyComponents(layer.Propagation),
                new LegacyTransform(
                    layer.Delta.Position,
                    layer.Delta.Rotation,
                    layer.Delta.Scale))).ToArray();

    private static LegacyComponents ToLegacyComponents(
        DomainComponents components) =>
        (LegacyComponents)(int)components;
}
