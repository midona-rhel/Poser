using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Application.Viewport;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Game.Bindings;
using Poser.Services;

namespace Poser.Game.Transforms;

public sealed class GroupTransformSource(
    SceneSession scene, StableBindingRegistry bindings, IViewportReads viewport,
    ICameraService camera) : IGroupTransformSource
{
    public PoseTransform? Read(TransformTargetId target) =>
        Refusal(target) == null ? viewport.GetModelTransform(target) : null;

    public string? Refusal(TransformTargetId target)
    {
        if (!scene.Contains(target)) return "A selected group member is unavailable.";
        if (target.Kind == TransformTargetKind.Bone) return "Bones cannot join an entity group transform.";
        if (target.Light is { } light)
        {
            var resolved = bindings.Resolve(light);
            if (!resolved.Success) return resolved.Detail ?? "The selected light is unavailable.";
            if (resolved.Value!.AttachedBone != null) return "Attached lights cannot join a world group transform.";
        }
        return null;
    }
    public bool TryFrame(Vector3 origin, out GroupTransformFrame frame) =>
        GroupTransformFrame.TryFromView(camera.GetViewMatrix(), origin, out frame);

    public TransformTargetId? CurrentTarget(TransformTargetId target)
    {
        if (scene.Contains(target)) return target;
        var logical = GroupTransformIdentity.LogicalId(target);
        return target.Kind switch
        {
            TransformTargetKind.Actor => scene.Snapshot.Actors.FirstOrDefault(x => x.Id.LogicalId == logical) is { } actor
                ? TransformTargetId.ForActor(actor.Id) : null,
            TransformTargetKind.Prop => scene.Snapshot.Props.FirstOrDefault(x => x.Id.LogicalId == logical) is { } prop
                ? TransformTargetId.ForProp(prop.Id) : null,
            TransformTargetKind.Light => scene.Snapshot.Lights.FirstOrDefault(x => x.Id.LogicalId == logical) is { } light
                ? TransformTargetId.ForLight(light.Id) : null,
            TransformTargetKind.WorldObject => scene.Snapshot.WorldObjects.FirstOrDefault(x => x.Id.LogicalId == logical) is { } world
                ? TransformTargetId.ForWorldObject(world.Id) : null,
            _ => bindings.CurrentTarget(target),
        };
    }
}
