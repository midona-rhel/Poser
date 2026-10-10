using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Files;
using Poser.Documents.Files;

namespace Poser.Application.Scene;

internal static class SceneParentingCodec
{
    internal static SceneStructureRef? Reference(SelectionId id, IReadOnlyDictionary<Guid, ActorId> actors,
        Func<ActorId, ActorId?>? companionOwner = null)
    {
        var actor = id.Actor ?? id.Bone?.Skeleton.Actor;
        if (actor is { } owner)
        {
            var key = actors.FirstOrDefault(pair => pair.Value == owner).Key;
            if (key == Guid.Empty && companionOwner?.Invoke(owner) is { } root)
            {
                key = actors.FirstOrDefault(pair => pair.Value == root).Key;
                return key == Guid.Empty ? null : new() { Kind = "companion", Key = key };
            }
            return key == Guid.Empty ? null : new() { Kind = "actor", Key = key };
        }
        return id switch
        {
            { Light: { } v } => new() { Kind = "light", Key = v.LogicalId },
            { Prop: { } v } => new() { Kind = "prop", Key = v.LogicalId },
            { Overlay: { } v } => new() { Kind = "overlay", Key = v.LogicalId },
            { WorldObject: { } v } => new() { Kind = "worldObject", Key = v.LogicalId },
            { Camera: { } v } => new() { Kind = "camera", Key = v.LogicalId },
            _ => null,
        };
    }

    public static void Write(SceneFile scene, IReadOnlyDictionary<SelectionId, TransformParent> links,
        IReadOnlyDictionary<Guid, ActorId> actors, Func<ActorId, ActorId?> companionOwner)
    {
        var parents = new List<SceneParentLink>();
        foreach (var (child, link) in links)
        {
            if (Reference(child, actors, companionOwner) is not { } childRef) continue;
            var targetRef = Reference(link.Target, actors, companionOwner);
            // Keep an excluded actor reference until policy pruning can report the static fallback.
            if (targetRef == null && (link.Target.Actor ?? link.Target.Bone?.Skeleton.Actor) is { } excluded)
                targetRef = new() { Kind = "actor", Key = excluded.LogicalId };
            if (targetRef == null) continue;
            parents.Add(new() { Child = childRef, Target = targetRef,
                BoneName = link.Target.Bone?.CanonicalName, Slot = link.Target.Bone?.Slot ?? PoseSlot.Character,
                Partial = link.Target.Bone?.PartialId ?? 0, Offset = link.Offset });
        }
        scene.Parents = parents.Count > 0 ? parents : null;
    }
}
