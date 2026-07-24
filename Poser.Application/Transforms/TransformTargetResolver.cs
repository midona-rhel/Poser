using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Transforms;

/// <summary>
/// The effective transform selection derived from the ordered selection and
/// the scene snapshot. <c>Primary</c> is the first surviving root in original
/// selection order; <c>Targets</c> keeps original selection order with the
/// primary first.
/// </summary>
public sealed record EffectiveTransformSelection(
    TransformTargetId Primary,
    IReadOnlyList<TransformTargetId> Targets);

/// <summary>
/// One shared resolution of "what does a transform act on" for the inspector
/// and the gizmo (PBI-001 clarification: selection primary vs effective
/// transform primary). Selected descendants of selected ancestors drop out —
/// propagation already carries the ancestor's edit — and a filtered
/// descendant, including a filtered selection primary, never re-enters the
/// target list.
/// </summary>
public static class TransformTargetResolver
{
    public static EffectiveTransformSelection? Resolve(
        IReadOnlyList<SelectionId> selected,
        SceneSnapshot snapshot)
    {
        if (selected.Count == 0)
            return null;

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

        var bones = new List<BoneId>();
        foreach (var id in selected)
            if (id is { Kind: SceneEntityKind.Bone, Bone: { } boneId })
                bones.Add(boneId);
        if (bones.Count == 0)
            return null;

        var lineage = bones[0].Skeleton.Actor.LogicalId;
        IReadOnlyList<BoneDescriptor>? descriptors = null;
        foreach (var actor in snapshot.Actors)
        {
            if (actor.Id.LogicalId != lineage)
                continue;
            descriptors = actor.Skeleton?.Bones;
            break;
        }
        // Every selected bone must exist exactly in its current skeleton
        // descriptor. A missing skeleton or an absent (stale-generation) bone
        // makes the selection unresolvable — an unknown bone is never treated
        // as a root.
        if (descriptors == null)
            return null;
        var byId = descriptors.ToDictionary(descriptor => descriptor.Id);

        var selectedSet = bones.ToHashSet();
        var roots = new List<TransformTargetId>();
        foreach (var boneId in bones)
        {
            if (!byId.TryGetValue(boneId, out var descriptor))
                return null;

            var hasSelectedAncestor = false;
            var parent = descriptor.Parent;
            while (parent is { } parentId)
            {
                if (selectedSet.Contains(parentId))
                {
                    hasSelectedAncestor = true;
                    break;
                }
                parent = byId.TryGetValue(parentId, out var parentDescriptor)
                    ? parentDescriptor.Parent
                    : null;
            }
            if (!hasSelectedAncestor)
                roots.Add(TransformTargetId.ForBone(boneId));
        }

        return roots.Count == 0
            ? null
            : new EffectiveTransformSelection(roots[0], roots);
    }
}
