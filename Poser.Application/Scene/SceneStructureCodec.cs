using Poser.Application.Scene;
using Poser.Domain.Identity;
using Poser.Documents.Files;

namespace Poser.Application.Scene;

/// <summary>Maps captured application values to legacy scene-file references.</summary>
internal static class SceneStructureCodec
{
    public static void Write(
        SceneFile scene, SceneStructureSnapshot snapshot,
        IReadOnlyDictionary<Guid, Poser.Domain.Identity.ActorId> actorIdentities)
    {
        try
        {
            SceneStructureRef? RefOf(SelectionId member) =>
                SceneParentingCodec.Reference(member, actorIdentities);

            SceneStructureRef? TransformRefOf(TransformTargetId target) => RefOf(target.ToSelectionId());

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
                        Kind = SceneStructureKind.Group,
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
