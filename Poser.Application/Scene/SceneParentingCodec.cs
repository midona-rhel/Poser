using Poser.Domain.Identity;
using Poser.Domain.Transforms;
using Poser.Documents.Files;

namespace Poser.Application.Scene;

/// <summary>Writes parent links into a scene file, naming each entity by its key
/// in the file. A companion is named through the actor it belongs to.</summary>
public static class SceneParentingCodec
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
                return key == Guid.Empty ? null : new() { Kind = SceneStructureKind.Companion, Key = key };
            }
            return key == Guid.Empty ? null : new() { Kind = SceneStructureKind.Actor, Key = key };
        }
        return id switch
        {
            { Light: { } v } => new() { Kind = SceneStructureKind.Light, Key = v.LogicalId },
            { Prop: { } v } => new() { Kind = SceneStructureKind.Prop, Key = v.LogicalId },
            { Overlay: { } v } => new() { Kind = SceneStructureKind.Overlay, Key = v.LogicalId },
            { WorldObject: { } v } => new() { Kind = SceneStructureKind.WorldObject, Key = v.LogicalId },
            { Camera: { } v } => new() { Kind = SceneStructureKind.Camera, Key = v.LogicalId },
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
            // A link to an actor this save leaves out is kept for now, so the
            // save can report it when it drops the link and the child keeps its
            // saved placement.
            if (targetRef == null && (link.Target.Actor ?? link.Target.Bone?.Skeleton.Actor) is { } excluded)
                targetRef = new() { Kind = SceneStructureKind.Actor, Key = excluded.LogicalId };
            if (targetRef == null) continue;
            parents.Add(new() { Child = childRef, Target = targetRef,
                BoneName = link.Target.Bone?.CanonicalName, Slot = link.Target.Bone?.Slot ?? PoseSlot.Character,
                Partial = link.Target.Bone?.PartialId ?? 0, Offset = link.Offset });
        }
        scene.Parents = parents.Count > 0 ? parents : null;
    }
}
