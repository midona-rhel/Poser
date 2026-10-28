using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Transforms;

/// <summary>
/// The effective transform selection derived from the ordered selection and
/// the scene snapshot. <c>Primary</c> is the first resolved target and
/// <c>Targets</c> preserves selection order.
/// </summary>
public sealed record EffectiveTransformSelection(
    TransformTargetId Primary,
    IReadOnlyList<TransformTargetId> Targets);

/// <summary>
/// Resolves transform targets for the inspector and gizmo. Every selected
/// bone is included, even when its parent is also selected. Gestures apply
/// each target from its captured starting state so parent movement does not
/// accumulate into the child's next update.
/// </summary>
public static class TransformTargetResolver
{
    public static EffectiveTransformSelection? Resolve(
        IReadOnlyList<SelectionId> selected,
        SceneSnapshot snapshot,
        Func<SelectionId, bool>? isLocked = null)
    {
        // Callers choose whether a fully selected locked group may move.
        // Exclude IDs rejected by that policy before resolving target kinds.
        if (isLocked != null && AnyLocked(selected, isLocked))
        {
            var free = new List<SelectionId>();
            foreach (var id in selected)
                if (!isLocked(id))
                    free.Add(id);
            selected = free;
        }
        if (selected.Count == 0)
            return null;

        // Mixed selections preserve the order of supported targets. Cameras
        // and non-collider overlays are omitted. A missing actor, light, prop,
        // or world object rejects the selection; unavailable colliders are omitted.
        if (Selection.EntitySelection.IsMixedEntities(selected))
        {
            var mixed = new List<TransformTargetId>();
            foreach (var id in selected)
            {
                switch (id)
                {
                    case { Kind: SceneEntityKind.Overlay, Overlay: { } colliderId }:
                        if (snapshot.Overlays.Any(x => x.Id == colliderId && x.Kind == Domain.Presentation.OverlayNodeKind.Collider))
                            mixed.Add(TransformTargetId.ForCollider(colliderId));
                        break;
                    case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                    {
                        bool exists = false;
                        foreach (var actor in snapshot.Actors)
                            if (actor.Id.Equals(actorId))
                            {
                                exists = true;
                                break;
                            }
                        if (!exists)
                            return null;
                        mixed.Add(TransformTargetId.ForActor(actorId));
                        break;
                    }
                    case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                    {
                        bool exists = false;
                        foreach (var light in snapshot.Lights)
                            if (light.Id.Equals(lightId))
                            {
                                exists = true;
                                break;
                            }
                        if (!exists)
                            return null;
                        mixed.Add(TransformTargetId.ForLight(lightId));
                        break;
                    }
                    case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                    {
                        bool exists = false;
                        foreach (var prop in snapshot.Props)
                            if (prop.Id.Equals(propId))
                            {
                                exists = true;
                                break;
                            }
                        if (!exists)
                            return null;
                        mixed.Add(TransformTargetId.ForProp(propId));
                        break;
                    }
                    case
                    {
                        Kind: SceneEntityKind.WorldObject,
                        WorldObject: { } worldId
                    }:
                    {
                        bool exists = false;
                        foreach (var worldObject in snapshot.WorldObjects)
                            if (worldObject.Id.Equals(worldId))
                            {
                                exists = true;
                                break;
                            }
                        if (!exists)
                            return null;
                        mixed.Add(TransformTargetId.ForWorldObject(worldId));
                        break;
                    }
                }
            }
            return mixed.Count == 0
                ? null
                : new EffectiveTransformSelection(mixed[0], mixed);
        }

        if (selected[0].Kind == SceneEntityKind.Actor)
        {
            var actorTargets = new List<TransformTargetId>();
            foreach (var id in selected)
            {
                if (id is not { Kind: SceneEntityKind.Actor, Actor: { } actorId })
                    continue;
                // Every selected actor must exist exactly (generation included)
                // in the snapshot; a stale target makes the whole selection
                // unresolvable rather than silently shrinking the gesture.
                var exists = false;
                foreach (var actor in snapshot.Actors)
                {
                    if (!actor.Id.Equals(actorId))
                        continue;
                    exists = true;
                    break;
                }
                if (!exists)
                    return null;
                actorTargets.Add(TransformTargetId.ForActor(actorId));
            }
            return actorTargets.Count == 0
                ? null
                : new EffectiveTransformSelection(actorTargets[0], actorTargets);
        }

        if (selected[0].Kind == SceneEntityKind.Overlay)
        {
            var colliders = selected.Where(id => id.Overlay is { } overlay &&
                snapshot.Overlays.Any(x => x.Id == overlay && x.Kind == Domain.Presentation.OverlayNodeKind.Collider))
                .Select(id => TransformTargetId.ForCollider(id.Overlay!.Value)).ToArray();
            return colliders.Length == 0 ? null : new(colliders[0], colliders);
        }

        if (selected[0].Kind == SceneEntityKind.Light)
        {
            var lightTargets = new List<TransformTargetId>();
            foreach (var id in selected)
            {
                if (id is not { Kind: SceneEntityKind.Light, Light: { } lightId })
                    continue;
                // Same all-or-nothing rule as actors: one stale light makes the
                // whole selection unresolvable rather than silently shrinking.
                var exists = false;
                foreach (var light in snapshot.Lights)
                {
                    if (!light.Id.Equals(lightId))
                        continue;
                    exists = true;
                    break;
                }
                if (!exists)
                    return null;
                lightTargets.Add(TransformTargetId.ForLight(lightId));
            }
            return lightTargets.Count == 0
                ? null
                : new EffectiveTransformSelection(lightTargets[0], lightTargets);
        }

        if (selected[0].Kind == SceneEntityKind.Prop)
        {
            var propTargets = new List<TransformTargetId>();
            foreach (var id in selected)
            {
                if (id is not { Kind: SceneEntityKind.Prop, Prop: { } propId })
                    continue;
                // Same all-or-nothing rule as lights: one stale prop makes the
                // whole selection unresolvable rather than silently shrinking.
                var exists = false;
                foreach (var prop in snapshot.Props)
                {
                    if (!prop.Id.Equals(propId))
                        continue;
                    exists = true;
                    break;
                }
                if (!exists)
                    return null;
                propTargets.Add(TransformTargetId.ForProp(propId));
            }
            return propTargets.Count == 0
                ? null
                : new EffectiveTransformSelection(propTargets[0], propTargets);
        }

        if (selected[0].Kind == SceneEntityKind.WorldObject)
        {
            var worldTargets = new List<TransformTargetId>();
            foreach (var id in selected)
            {
                if (id is not
                    { Kind: SceneEntityKind.WorldObject, WorldObject: { } worldId })
                    continue;
                // Same all-or-nothing rule as props: one stale claim makes the
                // whole selection unresolvable rather than silently shrinking.
                var exists = false;
                foreach (var worldObject in snapshot.WorldObjects)
                {
                    if (!worldObject.Id.Equals(worldId))
                        continue;
                    exists = true;
                    break;
                }
                if (!exists)
                    return null;
                worldTargets.Add(TransformTargetId.ForWorldObject(worldId));
            }
            return worldTargets.Count == 0
                ? null
                : new EffectiveTransformSelection(worldTargets[0], worldTargets);
        }

        var bones = new List<BoneId>();
        foreach (var id in selected)
            if (id is { Kind: SceneEntityKind.Bone, Bone: { } boneId })
                bones.Add(boneId);
        if (bones.Count == 0)
            return null;

        var lineage = bones[0].Skeleton.Actor.LogicalId;
        // A bone selection may span slots of the same actor; every selected
        // bone must exist exactly in its OWN slot's current descriptor. A
        // missing slot or an absent (stale-generation) bone makes the
        // selection unresolvable — an unknown bone is never a target.
        Dictionary<BoneId, BoneDescriptor>? byId = null;
        foreach (var actor in snapshot.Actors)
        {
            if (actor.Id.LogicalId != lineage)
                continue;
            byId = new Dictionary<BoneId, BoneDescriptor>();
            foreach (var skeleton in actor.Skeletons)
            foreach (var descriptor in skeleton.Bones)
                byId[descriptor.Id] = descriptor;
            break;
        }
        if (byId == null)
            return null;

        var targets = new List<TransformTargetId>();
        foreach (var boneId in bones)
        {
            if (!byId.ContainsKey(boneId))
                return null;
            targets.Add(TransformTargetId.ForBone(boneId));
        }

        return new EffectiveTransformSelection(targets[0], targets);
    }

    private static bool AnyLocked(
        IReadOnlyList<SelectionId> selected, Func<SelectionId, bool> isLocked)
    {
        foreach (var id in selected)
            if (isLocked(id))
                return true;
        return false;
    }
}
