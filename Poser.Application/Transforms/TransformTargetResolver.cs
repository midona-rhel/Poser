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
}
