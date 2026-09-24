using System.Numerics;
using Poser.Application.Transforms;
using Poser.Domain.Identity;
using Poser.Domain.Transforms;

namespace Poser.Application.Scene;

public sealed record SceneStructureGroup(Guid Key, string Name, Guid? Parent,
    IReadOnlyList<SelectionId> Members, GroupTransformSnapshot? Transform,
    bool HasTransform, Quaternion? LegacyFrame);

public sealed record SceneStructureSnapshot(IReadOnlyList<SceneStructureGroup> Groups,
    IReadOnlyList<RootSlot> RootOrder);

public interface ISceneStructure
{
    SceneStructureSnapshot Capture();
    IReadOnlyList<Guid> Import(IReadOnlyList<SceneStructureGroup> groups, IReadOnlyList<RootSlot> order);
    void Remove(IReadOnlyList<Guid> groups);
}

/// <summary>Captures and restores scene structure without file formats, native handles or a UI.</summary>
public sealed class SceneStructure(SceneGroups groups, GroupTransformCoordinator transforms,
    GroupTransformState state) : ISceneStructure
{
    public SceneStructureSnapshot Capture()
    {
        var captured = groups.All.Where(group => groups.Descendants(group).Count() >= 2)
            .Select(group =>
            {
                var transform = state.NamedSnapshot(group.Id);
                return new SceneStructureGroup(group.Id, group.Name, group.ParentId,
                    group.Members.ToArray(), transform, transform != null, null);
            }).ToArray();
        return new(captured, groups.RootOrder.ToArray());
    }

    public IReadOnlyList<Guid> Import(IReadOnlyList<SceneStructureGroup> entries, IReadOnlyList<RootSlot> order)
    {
        var before = groups.Capture();
        var previousTransforms = state.CaptureNamed();
        var ids = new Dictionary<Guid, Guid>();
        try
        {
            foreach (var entry in entries)
                if (groups.Create(entry.Name, entry.Members, allowThin: true) is { } group)
                    ids.Add(entry.Key, group.Id);
            foreach (var entry in entries)
                if (entry.Parent is { } parent && ids.TryGetValue(parent, out var parentId)
                    && ids.TryGetValue(entry.Key, out var childId))
                    groups.Nest(childId, parentId);
            foreach (var entry in entries)
            {
                if (!ids.TryGetValue(entry.Key, out var id) || groups.Find(id) is not { } group) continue;
                var targets = groups.Descendants(group).Select(GroupTransformCoordinator.Target).ToArray();
                var snapshot = entry.Transform;
                if (targets.Any(target => target == null)
                    || snapshot?.HasSameMembership(targets.Select(target => target!.Value).ToArray()) != true)
                    snapshot = null;
                transforms.Import(group, snapshot, entry.HasTransform, entry.LegacyFrame);
            }
            var restoredOrder = new List<RootSlot>();
            foreach (var slot in order)
                if (!slot.IsGroup) restoredOrder.Add(slot);
                else if (ids.TryGetValue(slot.GroupId, out var id)) restoredOrder.Add(RootSlot.ForGroup(id));
            if (restoredOrder.Count > 0) groups.RestoreOrder(restoredOrder);
            return ids.Values.ToArray();
        }
        catch
        {
            groups.Restore(before);
            state.RestoreNamed(previousTransforms);
            throw;
        }
    }

    public void Remove(IReadOnlyList<Guid> ids)
    {
        // Remove only this import's groups; unrelated groups may have been edited since loading.
        for (int index = ids.Count - 1; index >= 0; index--)
        {
            groups.SetLocked(ids[index], false);
            groups.Dissolve(ids[index]);
            state.Forget(ids[index]);
        }
    }
}
