using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Transforms;
using Poser.Application.Viewport;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Game.Bindings;

namespace Poser.Game.Transforms;

/// <summary>Reads what the group code needs from the game: where a member is,
/// whether it can move, and the camera's view for a new group's frame.</summary>
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

    /// <summary>The thing <paramref name="target"/> stands for now: itself, the
    /// same thing after a redraw, or the thing undo brought back in place of a
    /// deleted one. Group members are looked up through this before members
    /// that are gone leave their group, so undoing a removal puts the members
    /// back into their groups.</summary>
    public TransformTargetId? CurrentTarget(TransformTargetId target) =>
        scene.CurrentTransformTarget(target)
        ?? (history?.ResolveLifecycleTarget(target) is { } successor && successor != target
            ? scene.CurrentTransformTarget(successor) : null);
}
