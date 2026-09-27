using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Viewport;
using Poser.Domain.Transforms;
using Poser.Domain.Identity;
using Poser.Services;

namespace Poser.Application.Transforms;

public interface ISelectionPlacement
{
    GestureResult MoveToCamera();
    GestureResult MoveToCamera(IReadOnlyList<SelectionId> targets);
    bool CanPlace(SelectionId target);
    GestureResult ResetComponent(SelectionId target, TransformOperation component);
}

public sealed class SelectionPlacement(
    SelectionSession selection, SceneSession scene, SceneGroups groups,
    IViewportReads viewport, ICameraProjection camera, ITransformFacade transforms) : ISelectionPlacement
{
    public GestureResult MoveToCamera() => MoveToCamera(selection.Selected);

    public bool CanPlace(SelectionId target) => ResolveOne(target) is { } id && Read(id) != null;

    private TransformTargetId? ResolveOne(SelectionId target)
    {
        if (target.Bone != null || groups.IsLockedMember(target) || target.Overlay is { } overlay && viewport.GetCollider(overlay)?.Locked == true)
            return null;
        var resolved = TransformTargetResolver.Resolve([target], scene.Snapshot);
        return resolved?.Targets.Count == 1 ? resolved.Primary : null;
    }

    private PoseTransform? Read(TransformTargetId target) => target.Actor is { } actor
        ? viewport.GetActorTransform(actor) : viewport.GetModelTransform(target);

    public GestureResult ResetComponent(SelectionId target, TransformOperation component)
    {
        if (ResolveOne(target) is not { } id || Read(id) is not { } current)
            return GestureResult.Fail("This entity is unavailable or its transform is locked.");
        if (component != TransformOperation.Rotate && component != TransformOperation.Scale)
            return GestureResult.Fail("Only rotation and scale have neutral component values.");
        if (target.Light != null && component == TransformOperation.Scale)
            return GestureResult.Fail("Lights do not have a scale transform.");
        return transforms.SetAbsolute(id, component == TransformOperation.Rotate
            ? current with { Rotation = Quaternion.Identity } : current with { Scale = Vector3.One },
            component == TransformOperation.Rotate ? "Reset rotation" : "Reset scale");
    }

    public GestureResult MoveToCamera(IReadOnlyList<SelectionId> targetsToMove)
    {
        var resolved = TransformTargetResolver.Resolve(targetsToMove, scene.Snapshot,
            id => groups.IsLockedMember(id) || id.Overlay is { } overlay && viewport.GetCollider(overlay)?.Locked == true);
        if (resolved is not { } targets)
            return GestureResult.Fail("Nothing movable is selected.");
        var sum = Vector3.Zero;
        int count = 0;
        foreach (var target in targets.Targets)
        {
            var pose = target is { Kind: TransformTargetKind.Actor, Actor: { } actor }
                ? viewport.GetActorTransform(actor) : viewport.GetModelTransform(target);
            if (pose is not { } position) continue;
            sum += position.Position;
            count++;
        }
        if (count == 0) return GestureResult.Fail("Nothing movable is selected.");
        var look = camera.GetLookDirection();
        if (look.LengthSquared() < 1e-6f) look = Vector3.UnitZ;
        var goal = camera.GetCameraPosition() + Vector3.Normalize(look) * 2.5f;
        var begin = transforms.Begin(targets.Targets, TransformOperation.Translate,
            TransformSpace.World, description: "Move to camera");
        if (!begin.Success || begin.GestureId is not { } gesture) return begin;
        var updated = transforms.Update(gesture,
            new TransformDelta(goal - sum / count, Quaternion.Identity, Vector3.One));
        if (!updated.Success)
        {
            var cancelled = transforms.Cancel(gesture);
            return cancelled.Success ? updated : cancelled;
        }
        return transforms.Commit(gesture);
    }
}
