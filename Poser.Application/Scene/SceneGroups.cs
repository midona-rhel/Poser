using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Scene;

/// <summary>One named group of scene entities. ONE depth by construction:
/// members are entities, never groups.</summary>
public sealed class SceneGroup
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }
    public required List<SelectionId> Members { get; init; }
}

/// <summary>
/// The scene's named groups. A group is NAMING AND STRUCTURE over the
/// anonymous group: selecting one selects its members, and every
/// manipulation (centroid gizmo, group ball, move to camera) is the
/// multiselect machinery — this store never transforms anything. An
/// entity lives in at most ONE group; membership is entity-only, so a
/// group can never contain a group. In-memory for now; persistence
/// arrives with the scene-save slice.
/// </summary>
public sealed class SceneGroups
{
    private readonly List<SceneGroup> _groups = new();

    /// <summary>Bumped on every structural change — the sidebar rebuild
    /// gates on it exactly as it gates on the scene revision.</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<SceneGroup> All => _groups;

    public SceneGroup? Create(string name, IReadOnlyList<SelectionId> members)
    {
        var kept = new List<SelectionId>();
        foreach (var member in members)
            if (Selection.EntitySelection.IsEntity(member.Kind)
                && !kept.Contains(member))
                kept.Add(member);
        if (kept.Count < 2)
            return null;
        // One home per entity: joining this group leaves any other.
        foreach (var member in kept)
            RemoveMemberCore(member);
        var group = new SceneGroup
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? "Group" : name.Trim(),
            Members = kept,
        };
        _groups.Add(group);
        Revision++;
        return group;
    }

    public void Rename(Guid id, string name)
    {
        if (Find(id) is not { } group || string.IsNullOrWhiteSpace(name))
            return;
        group.Name = name.Trim();
        Revision++;
    }

    public void Dissolve(Guid id)
    {
        if (Find(id) is not { } group)
            return;
        _groups.Remove(group);
        Revision++;
    }

    /// <summary>An entity left the scene: it leaves its group, and a group
    /// thinned below two members dissolves — a group of one is a selection.
    /// </summary>
    public void RemoveMember(SelectionId member)
    {
        if (RemoveMemberCore(member))
            Revision++;
    }

    public SceneGroup? Find(Guid id) =>
        _groups.Find(group => group.Id == id);

    public SceneGroup? GroupOf(SelectionId member) =>
        _groups.Find(group => group.Members.Contains(member));

    /// <summary>The group whose member set EQUALS the selection's entity
    /// set, if any — how the UI knows a multiselect IS a named group.</summary>
    public SceneGroup? MatchSelection(IReadOnlyList<SelectionId> selected)
    {
        int entities = 0;
        foreach (var id in selected)
            if (Selection.EntitySelection.IsEntity(id.Kind))
                entities++;
        if (entities < 2)
            return null;
        foreach (var group in _groups)
        {
            if (group.Members.Count != entities)
                continue;
            bool all = true;
            foreach (var id in selected)
            {
                if (!Selection.EntitySelection.IsEntity(id.Kind))
                    continue;
                if (!group.Members.Contains(id))
                {
                    all = false;
                    break;
                }
            }
            if (all)
                return group;
        }
        return null;
    }

    /// <summary>Drops members the snapshot no longer contains. Returns
    /// whether anything changed (the caller already rebuilds).</summary>
    public bool Prune(Func<SelectionId, bool> exists)
    {
        bool changed = false;
        for (int i = _groups.Count - 1; i >= 0; i--)
        {
            var group = _groups[i];
            for (int m = group.Members.Count - 1; m >= 0; m--)
                if (!exists(group.Members[m]))
                {
                    group.Members.RemoveAt(m);
                    changed = true;
                }
            if (group.Members.Count < 2)
            {
                _groups.RemoveAt(i);
                changed = true;
            }
        }
        if (changed)
            Revision++;
        return changed;
    }

    private bool RemoveMemberCore(SelectionId member)
    {
        for (int i = _groups.Count - 1; i >= 0; i--)
        {
            var group = _groups[i];
            if (!group.Members.Remove(member))
                continue;
            if (group.Members.Count < 2)
                _groups.RemoveAt(i);
            return true;
        }
        return false;
    }
}
