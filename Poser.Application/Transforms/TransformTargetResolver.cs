using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Transforms;

/// <summary>
/// The effective transform selection derived from the ordered selection and
/// the scene snapshot. <c>Primary</c> is the first selected target and
/// <c>Targets</c> preserves selection order.
/// </summary>
public sealed record EffectiveTransformSelection(
    TransformTargetId Primary,
    IReadOnlyList<TransformTargetId> Targets);

/// <summary>
/// One shared resolution of "what does a transform act on" for the inspector
/// and the gizmo. Every selected bone is a target — the user explicitly
/// reversed PBI-001's descendant filtering in the 2026-07-24 walkthrough:
/// selecting a knee and its calf must transform BOTH from their own frozen
/// baselines (the gesture applies each target absolutely from its captured
/// state, so an ancestor's propagation cannot compound into a feedback
/// loop).
/// </summary>
public static class TransformTargetResolver
{
    public static EffectiveTransformSelection? Resolve(
        IReadOnlyList<SelectionId> selected,
        SceneSnapshot snapshot,
        Func<SelectionId, bool>? isLocked = null)
    {
        // The lock predicate is selection-aware at the call sites: a
        // locked group's child refuses INDIVIDUALLY, while a selection
        // holding the whole membership moves the group as one. Whatever
        // the predicate refuses leaves the resolution — no target, no
        // gizmo seat — before any branch runs.
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

        // The ANONYMOUS GROUP: entities spanning kinds resolve together —
        // each transformable member a target from its own baseline, in
        // selection order. Cameras and overlays ride along untargeted
        // (they carry no world transform target), and one stale
        // transformable member unresolves the whole selection, the
        // uniform branches' own rule.
        if (Selection.EntitySelection.IsMixedEntities(selected))
        {
            var mixed = new List<TransformTargetId>();
            foreach (var id in selected)
            {
                switch (id)
                {
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
