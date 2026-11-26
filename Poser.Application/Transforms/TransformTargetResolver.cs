using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;

namespace Poser.Application.Transforms;

/// <summary>
/// What a selection moves. <c>Primary</c> is the first thing moved and
/// <c>Targets</c> keeps the order of the selection.
/// </summary>
public sealed record EffectiveTransformSelection(
    TransformTargetId Primary,
    IReadOnlyList<TransformTargetId> Targets);

/// <summary>
/// Finds the exact things a selection moves, for the inspector, the gizmo,
/// placement, groups and undo. Every selected bone is moved, even when its
/// parent is selected too.
/// </summary>
public static class TransformTargetResolver
{
    /// <summary>
    /// The thing a selected item moves: an actor, bone, light, object, world
    /// object or IK collider. Null for things the move tools never move, such
    /// as cameras and gaze targets. An overlay always maps to a collider here;
    /// whether it really is one is checked against the scene.
    /// </summary>
    public static TransformTargetId? Target(SelectionId id) => id switch
    {
        { Kind: SceneEntityKind.Bone, Bone: { } bone } => TransformTargetId.ForBone(bone),
        { Kind: SceneEntityKind.Overlay, Overlay: { } overlay } => TransformTargetId.ForCollider(overlay),
        { Kind: SceneEntityKind.Actor, Actor: { } actor } => TransformTargetId.ForActor(actor),
        { Kind: SceneEntityKind.Light, Light: { } light } => TransformTargetId.ForLight(light),
        { Kind: SceneEntityKind.Prop, Prop: { } prop } => TransformTargetId.ForProp(prop),
        { Kind: SceneEntityKind.WorldObject, WorldObject: { } world } => TransformTargetId.ForWorldObject(world),
        _ => null,
    };

    /// <summary>
    /// What the selection moves, leaving out what <paramref name="isLocked"/>
    /// holds still. When the first item left is a bone, only bones move;
    /// otherwise only things that are not bones move. Null when nothing can
    /// move, or when a selected actor, bone, light, object or world object is
    /// no longer in the scene: a move never quietly leaves part of the
    /// selection behind. An overlay that is not an IK collider is skipped.
    /// </summary>
    public static EffectiveTransformSelection? Resolve(
        IReadOnlyList<SelectionId> selected,
        SceneSnapshot snapshot,
        Func<SelectionId, bool>? isLocked = null)
    {
        var targets = new List<TransformTargetId>();
        HashSet<BoneId>? actorBones = null;
        bool? bones = null;
        foreach (var id in selected)
        {
            if (isLocked?.Invoke(id) == true)
                continue;
            bones ??= id.Kind == SceneEntityKind.Bone;
            if (Target(id) is not { } target || bones != (target.Kind == TransformTargetKind.Bone))
                continue;
            if (target.Bone is { } bone ? (actorBones ??= BonesOf(bone, snapshot)).Contains(bone) : Exists(target, snapshot))
                targets.Add(target);
            else if (target.Kind != TransformTargetKind.Collider)
                return null;
        }
        return targets.Count == 0 ? null : new EffectiveTransformSelection(targets[0], targets);
    }

    /// <summary>Every bone of the bone's actor, in all its skeletons. A bone
    /// selection belongs to one actor, so this is read once per call.</summary>
    private static HashSet<BoneId> BonesOf(BoneId bone, SceneSnapshot snapshot)
    {
        var set = new HashSet<BoneId>();
        foreach (var actor in snapshot.Actors)
        {
            if (actor.Id.LogicalId != bone.Skeleton.Actor.LogicalId)
                continue;
            foreach (var skeleton in actor.Skeletons)
                foreach (var descriptor in skeleton.Bones)
                    set.Add(descriptor.Id);
            break;
        }
        return set;
    }

    private static bool Exists(TransformTargetId target, SceneSnapshot snapshot)
    {
        switch (target)
        {
            case { Actor: { } actorId }:
                foreach (var actor in snapshot.Actors)
                    if (actor.Id.Equals(actorId))
                        return true;
                return false;
            case { Light: { } lightId }:
                foreach (var light in snapshot.Lights)
                    if (light.Id.Equals(lightId))
                        return true;
                return false;
            case { Prop: { } propId }:
                foreach (var prop in snapshot.Props)
                    if (prop.Id.Equals(propId))
                        return true;
                return false;
            case { WorldObject: { } worldId }:
                foreach (var worldObject in snapshot.WorldObjects)
                    if (worldObject.Id.Equals(worldId))
                        return true;
                return false;
            case { Collider: { } colliderId }:
                foreach (var overlay in snapshot.Overlays)
                    if (overlay.Id == colliderId && overlay.Kind == OverlayNodeKind.Collider)
                        return true;
                return false;
            default:
                return false;
        }
    }
}
