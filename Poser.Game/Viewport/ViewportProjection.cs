using System.Numerics;
using Dalamud.Plugin.Services;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game.Bindings;

namespace Poser.Game.Viewport;

/// <summary>
/// Frame-scoped stable-id spatial reads for presentation surfaces
/// (docs/game/viewport-projection.md). Every query resolves a stable id
/// through <see cref="StableBindingRegistry"/> for exactly one read and
/// returns an immutable value; pointers and legacy entities never leave the
/// runtime boundary, and stale generations yield no result instead of a value
/// read from a reused address.
///
/// <para>Results are valid for the current frame only. Gestures freeze their
/// baselines through <c>TransformGestureService.Begin</c> capture — never
/// through these reads.</para>
/// </summary>
public sealed class ViewportProjection
{
    private readonly IFramework _framework;
    private readonly StableBindingRegistry _bindings;
    private readonly PosingService _actors;

    public ViewportProjection(
        IFramework framework,
        StableBindingRegistry bindings,
        PosingService actors)
    {
        _framework = framework;
        _bindings = bindings;
        _actors = actors;
    }

    /// <summary>
    /// Current model-space transform of a bone target or effective world
    /// transform of an actor target. Null off the framework thread or when
    /// the id is stale, mismatched, missing, or carries non-finite values.
    /// </summary>
    public PoseTransform? GetModelTransform(TransformTargetId target) =>
        target.Kind switch
        {
            TransformTargetKind.Actor when target.Actor is { } actorId =>
                GetActorTransform(actorId),
            TransformTargetKind.Bone when target.Bone is { } boneId =>
                GetBoneModelTransform(boneId),
            _ => null,
        };

    public PoseTransform? GetActorTransform(ActorId id)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return null;
        var actor = _bindings.Resolve(id);
        if (!actor.Success)
            return null;
        var transform = _actors.GetEffectiveTransform(actor.Value!);
        return ToPoseTransform(transform);
    }

    public PoseTransform? GetBoneModelTransform(BoneId id)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return null;
        var bone = _bindings.Resolve(id);
        return bone.Success
            ? ToPoseTransform(bone.Value!.LastTransform)
            : null;
    }

    /// <summary>
    /// Model-space transform of the bone's parent, for parent-local display
    /// composition. Null for partial roots or unresolved ids.
    /// </summary>
    public PoseTransform? GetParentModelTransform(BoneId id)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return null;
        var bone = _bindings.Resolve(id);
        if (!bone.Success || bone.Value!.ParentBone is not { } parent)
            return null;
        return ToPoseTransform(parent.LastTransform);
    }

    /// <summary>
    /// Owning skeleton's model→world matrix for a bone target (folded into
    /// the gizmo view matrix, Brio's convention).
    /// </summary>
    public Matrix4x4? GetSkeletonModelMatrix(BoneId id)
    {
        if (!_framework.IsInFrameworkUpdateThread)
            return null;
        var bone = _bindings.Resolve(id);
        if (!bone.Success)
            return null;
        return bone.Value!.Skeleton is Skeleton skeleton
            ? skeleton.GetModelMatrix()
            : null;
    }

    private static PoseTransform? ToPoseTransform(Transform transform) =>
        PoseTransform.TryCreate(
            transform.Position,
            transform.Rotation,
            transform.Scale,
            out var value,
            out _)
            ? value
            : null;
}
