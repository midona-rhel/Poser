using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Application.Viewport;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Game.Bindings;

namespace Poser.Game.Transforms;

public sealed class GroupTransformSource(
    SceneSession scene, StableBindingRegistry bindings, IViewportReads viewport,
    ICameraProjection camera, EditHistory? history = null) : IGroupTransformSource
{
    public PoseTransform? Read(TransformTargetId target) =>
        // Lock/attachment refusal governs editing, not reading existing state.
        scene.Contains(target) ? viewport.GetModelTransform(target) : null;

    public string? Refusal(TransformTargetId target)
    {
        if (!scene.Contains(target)) return "A selected group member is unavailable.";
        if (target.Kind == TransformTargetKind.Bone) return "Bones cannot join an entity group transform.";
        if (target.Collider is { } collider && bindings.Resolve(collider).Value?.State.Collider?.Locked == true)
            return "The IK collider transform is locked.";
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

    /// <summary>The live target for <paramref name="target"/>: itself or its
    /// lineage successor, else the entity a lifecycle undo brought back in
    /// its place (a new id). Group members are remapped through this before
    /// the prune, so a structure restored with a removal's undo keeps the
    /// members that came back (#494).</summary>
    public TransformTargetId? CurrentTarget(TransformTargetId target) =>
        scene.CurrentTransformTarget(target)
        ?? (history?.ResolveLifecycleTarget(target) is { } successor && successor != target
            ? scene.CurrentTransformTarget(successor) : null);
}
