using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Files;

namespace Poser.Game.Scene;

/// <summary>Maps captured application values to legacy scene-file references.</summary>
internal static class SceneStructureCodec
{
    public static void Write(
        SceneFile scene, SceneStructureSnapshot snapshot,
        IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId> actorIdentities)
    {
        try
        {
            var actorKeys = new Dictionary<Guid, Guid>();
            foreach (var pair in actorIdentities)
                actorKeys[pair.Value.LogicalId] = pair.Key;

            SceneStructureRef? RefOf(
                global::Poser.Domain.Identity.SelectionId member)
            {
                string? kind = member.Kind switch
                {
                    global::Poser.Domain.Identity.SceneEntityKind.Actor => "actor",
                    global::Poser.Domain.Identity.SceneEntityKind.Prop => "prop",
                    global::Poser.Domain.Identity.SceneEntityKind.WorldObject =>
                        "worldObject",
                    global::Poser.Domain.Identity.SceneEntityKind.Light => "light",
                    global::Poser.Domain.Identity.SceneEntityKind.Camera => "camera",
                    global::Poser.Domain.Identity.SceneEntityKind.Overlay =>
                        "overlay",
                    _ => null,
                };
                if (kind == null)
                    return null;
                Guid? logical = member switch
                {
                    { Actor: { } actor } => actor.LogicalId,
                    { Prop: { } prop } => prop.LogicalId,
                    { WorldObject: { } worldObject } => worldObject.LogicalId,
                    { Light: { } light } => light.LogicalId,
                    { Camera: { } camera } => camera.LogicalId,
                    { Overlay: { } overlay } => overlay.LogicalId,
                    _ => null,
                };
                if (logical is not { } key)
                    return null;
                if (kind == "actor"
                    && !actorKeys.TryGetValue(key, out key))
                    return null;
                return new SceneStructureRef { Kind = kind, Key = key };
            }

            SceneStructureRef? TransformRefOf(
                global::Poser.Domain.Identity.TransformTargetId target)
            {
                return target switch
                {
                    { Kind: TransformTargetKind.Actor, Actor: { } actor }
                        when actorKeys.TryGetValue(actor.LogicalId, out var key) =>
                        new SceneStructureRef { Kind = "actor", Key = key },
                    { Kind: TransformTargetKind.Prop, Prop: { } prop } =>
                        new SceneStructureRef { Kind = "prop", Key = prop.LogicalId },
                    { Kind: TransformTargetKind.WorldObject, WorldObject: { } world } =>
                        new SceneStructureRef { Kind = "worldObject", Key = world.LogicalId },
                    { Kind: TransformTargetKind.Collider, Collider: { } collider } =>
                        new SceneStructureRef { Kind = "overlay", Key = collider.LogicalId },
                    { Kind: TransformTargetKind.Light, Light: { } light } =>
                        new SceneStructureRef { Kind = "light", Key = light.LogicalId },
                    _ => null,
                };
            }

            var groups = new List<SceneGroupEntry>();
            foreach (var group in snapshot.Groups)
            {
                var entry = new SceneGroupEntry
                {
                    Key = group.Key,
                    Name = group.Name,
                    Parent = group.Parent,
                };
                if (group.Transform is { } groupState)
                {
                    var transform = new SceneGroupTransformEntry
                    {
                        FrameOrigin = groupState.Baseline.Frame.Origin,
                        FrameRotation = groupState.Baseline.Frame.Rotation,
                        Position = groupState.Controls.Position,
                        Rotation = groupState.Controls.Rotation,
                        SpacingScale = groupState.Controls.SpacingScale,
                        OwnScale = groupState.Controls.OwnScale,
                    };
                    foreach (var (target, initial) in groupState.Baseline.InitialTransforms)
                        if (TransformRefOf(target) is { } reference
                            && groupState.Expected.TryGetValue(target, out var expected))
                            transform.Members.Add(new SceneGroupTransformMember
                            {
                                Member = reference,
                                Initial = initial,
                                Expected = expected,
                            });
                    if (transform.Members.Count == groupState.Baseline.InitialTransforms.Count)
                        entry.Transform = transform;
                }
                foreach (var member in group.Members)
                    if (RefOf(member) is { } reference)
                        entry.Members.Add(reference);
                groups.Add(entry);
            }
            if (groups.Count > 0)
                scene.Groups = groups;

            var order = new List<SceneStructureRef>();
            foreach (var slot in snapshot.RootOrder)
            {
                if (slot.IsGroup)
                    order.Add(new SceneStructureRef
                    {
                        Kind = "group",
                        Key = slot.GroupId,
                    });
                else if (slot.Entity is { } entity
                    && RefOf(entity) is { } reference)
                    order.Add(reference);
            }
            if (order.Count > 0)
                scene.RootOrder = order;
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException("Could not capture scene group state.", exception);
        }
    }

}
