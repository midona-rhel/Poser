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
}

public sealed class SelectionPlacement(
    SelectionSession selection, SceneSession scene, SceneGroups groups,
    IViewportReads viewport, ICameraProjection camera, ITransformFacade transforms) : ISelectionPlacement
{
    public GestureResult MoveToCamera()
    {
        var resolved = TransformTargetResolver.Resolve(selection.Selected, scene.Snapshot, groups.IsLockedMember);
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
