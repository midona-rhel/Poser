using System.Numerics;
using Poser.Application.Scene;
using Poser.Application.Viewport;
using Poser.Domain.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Transforms;

public interface ISelectionPlacement
{
    /// <summary>Moves what is given in front of the camera as one undo step,
    /// "Move to camera". Several entities move together and keep their spacing,
    /// rotation and size. One light on its own also turns to shine where the
    /// camera looks. Members of a locked group stay where they are.</summary>
    GestureResult MoveToCamera(IReadOnlyList<SelectionId> targets);
    bool CanPlace(SelectionId target);
    GestureResult ResetComponent(SelectionId target, TransformOperation component);
}

public sealed class SelectionPlacement(
    SceneSession scene, SceneGroups groups,
    IViewportReads viewport, ICameraProjection camera, ITransformFacade transforms) : ISelectionPlacement
{
    public bool CanPlace(SelectionId target) => ResolveOne(target) is { } id && Read(id) != null;

    private TransformTargetId? ResolveOne(SelectionId target)
    {
        if (target.Bone != null)
            return null;
        var resolved = TransformSelectionPolicy.Placement([target], scene.Snapshot, groups, viewport);
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
        var resolved = TransformSelectionPolicy.Placement(targetsToMove, scene.Snapshot, groups, viewport);
        if (resolved is not { } targets)
            return GestureResult.Fail("Nothing movable is selected.");
        // An entity whose game object is no longer bound has no transform to
        // read; the others still move.
        var sum = Vector3.Zero;
        int count = 0;
        var scale = Vector3.One;
        foreach (var target in targets.Targets)
        {
            if (Read(target) is not { } pose) continue;
            sum += pose.Position;
            scale = pose.Scale;
            count++;
        }
        if (count == 0) return GestureResult.Fail("Nothing movable is selected.");
        // The camera reads as no direction only when the game has no usable
        // camera; the world's forward axis is then as good as any.
        var look = camera.GetLookDirection();
        if (look.LengthSquared() < 1e-6f) look = Vector3.UnitZ;
        var eye = camera.GetCameraPosition();

        // A light asked for on its own lands where a new light appears, one
        // yalm out, and turns to shine along the view.
        if (targetsToMove is [{ Light: not null }] && targets.Targets is [var light])
        {
            var placed = LightPlacement.FromCamera(eye, look, scale);
            return transforms.SetAbsolute(light,
                new PoseTransform(placed.Position, placed.Rotation, placed.Scale), "Move to camera");
        }

        var goal = eye + Vector3.Normalize(look) * 2.5f;
        var begin = transforms.Begin(targets.Targets, TransformOperation.Translate,
            TransformSpace.World, description: "Move to camera", selection: targetsToMove);
        if (!begin.Success || begin.GestureId is not { } gesture) return begin;
        var updated = transforms.Update(gesture,
            new TransformDelta(goal - sum / count, Quaternion.Identity, Vector3.One));
        return updated.Success ? transforms.Commit(gesture) : updated;
    }
}
