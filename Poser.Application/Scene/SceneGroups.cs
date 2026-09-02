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

    /// <summary>A locked group freezes its CHILDREN: a member selected
    /// on its own refuses world transforms, nothing drags in, out, or
    /// around, and the structure verbs (rename, ungroup, destroy) wait
    /// for the unlock. The GROUP itself stays movable — a selection
    /// holding the whole membership moves it as one thing (ruled
    /// 2026-08-31; the whole-placement reading was wrong). Visibility
    /// and animation stay free.</summary>
    public bool Locked { get; set; }
}

/// <summary>One root slot of the outliner: an ungrouped entity or a
/// group head. Exactly one of the two is set.</summary>
public readonly record struct RootSlot(SelectionId? Entity, Guid GroupId)
{
    public static RootSlot For(SelectionId entity) => new(entity, Guid.Empty);
    public static RootSlot ForGroup(Guid id) => new(null, id);
    public bool IsGroup => GroupId != Guid.Empty;
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

    /// <summary>Forgets every group and the root order.</summary>
    public void Clear()
    {
        if (_groups.Count == 0 && _order.Count == 0 && ActiveGroupId == null)
            return;
        _groups.Clear();
        _order.Clear();
        ActiveGroupId = null;
        Revision++;
    }

    /// <summary>The root list in the USER'S order, kinds interleaved:
    /// group heads and ungrouped entities, one slot each. Attached and
    /// grouped entities hold no slot; <see cref="SyncRoot"/> reconciles
    /// membership every rebuild while the structural verbs below keep
    /// positions meaningful.</summary>
    private readonly List<RootSlot> _order = new();

    /// <summary>Bumped on every structural change — the sidebar rebuild
    /// gates on it exactly as it gates on the scene revision.</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<SceneGroup> All => _groups;

    public SceneGroup? Create(string name, IReadOnlyList<SelectionId> members)
    {
        var kept = new List<SelectionId>();
        foreach (var member in members)
            if (Selection.EntitySelection.IsEntity(member.Kind)
                && !kept.Contains(member)
                // A locked group keeps its members: a new group cannot
                // steal them.
                && !IsLockedMember(member))
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
        // The group takes its first member's seat in the root order; the
        // members' own slots fold into it.
        int at = _order.Count;
        for (int i = _order.Count - 1; i >= 0; i--)
            if (_order[i] is { IsGroup: false, Entity: { } slotted }
                && kept.Contains(slotted))
            {
                _order.RemoveAt(i);
                at = i;
            }
        _order.Insert(Math.Min(at, _order.Count), RootSlot.ForGroup(group.Id));
        Revision++;
        return group;
    }

    public void Rename(Guid id, string name)
    {
        if (Find(id) is not { Locked: false } group
            || string.IsNullOrWhiteSpace(name))
            return;
        group.Name = name.Trim();
        Revision++;
    }

    public void Dissolve(Guid id)
    {
        if (Find(id) is not { Locked: false } group)
            return;
        // The members reclaim the group's seat in the root order, in
        // member order — ungrouping never scatters rows.
        int at = RootIndexOfGroup(id);
        if (at >= 0)
            _order.RemoveAt(at);
        else
            at = _order.Count;
        for (int m = 0; m < group.Members.Count; m++)
            _order.Insert(at + m, RootSlot.For(group.Members[m]));
        _groups.Remove(group);
        Revision++;
    }

    /// <summary>An entity left the scene: it leaves its group, and a group
    /// thinned below two members dissolves — a group of one is a selection.
    /// </summary>
    public void RemoveMember(SelectionId member)
    {
        // A locked group keeps its members; the scene's own prune is the
        // one force that overrides (a despawned member is simply gone).
        if (IsLockedMember(member))
            return;
        if (RemoveMemberCore(member))
            Revision++;
    }

    /// <summary>Puts one entity into a group at <paramref name="index"/>
    /// (clamped; negative appends). Joining leaves any other group; a
    /// member of the SAME group moves to the new place instead.</summary>
    public void AddMember(Guid groupId, SelectionId member, int index = -1)
    {
        if (!Selection.EntitySelection.IsEntity(member.Kind)
            || Find(groupId) is not { Locked: false } group
            || IsLockedMember(member))
            return;
        int existing = group.Members.IndexOf(member);
        if (existing >= 0)
        {
            group.Members.RemoveAt(existing);
            if (index > existing)
                index--;
        }
        else
        {
            RemoveMemberCore(member);
            // The removal above may have dissolved nothing relevant, but
            // it can never have touched THIS group (the member was not in
            // it), so the group reference stays valid.
        }
        if (index < 0 || index > group.Members.Count)
            index = group.Members.Count;
        group.Members.Insert(index, member);
        Revision++;
    }

    /// <summary>The root list in display order — valid after
    /// <see cref="SyncRoot"/> ran for the current scene.</summary>
    public IReadOnlyList<RootSlot> RootOrder => _order;

    /// <summary>Reconciles the root order against the scene: the caller
    /// hands every root-eligible ungrouped entity, in kind order. Stale
    /// slots leave, missing ones append — a new spawn lands at the
    /// bottom. Reconciliation never bumps <see cref="Revision"/>: it
    /// runs inside the rebuild that already reflects it.</summary>
    public IReadOnlyList<RootSlot> SyncRoot(
        IReadOnlyList<SelectionId> rootEntities)
    {
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            var slot = _order[i];
            bool keep = slot.IsGroup
                ? Find(slot.GroupId) != null
                : slot.Entity is { } id && ContainsEntity(rootEntities, id);
            // A slot also leaves when an earlier copy already holds the
            // seat — the structural verbs guess positions and this sweep
            // is their garbage collector.
            if (!keep || _order.IndexOf(slot) < i)
                _order.RemoveAt(i);
        }
        foreach (var group in _groups)
            if (RootIndexOfGroup(group.Id) < 0)
                _order.Add(RootSlot.ForGroup(group.Id));
        foreach (var id in rootEntities)
            if (_order.IndexOf(RootSlot.For(id)) < 0)
                _order.Add(RootSlot.For(id));
        return _order;
    }

    /// <summary>A loaded document's order restored: each named slot moves
    /// to the end in the given sequence, so a cleared-and-loaded scene
    /// reads back in its saved order and a merge-load's entities arrive
    /// at the bottom, still ordered among themselves. Unknown slots are
    /// seated too — the next sync prunes any that never materialize.</summary>
    public void RestoreOrder(IReadOnlyList<RootSlot> slots)
    {
        if (slots.Count == 0)
            return;
        foreach (var slot in slots)
        {
            int at = _order.IndexOf(slot);
            if (at >= 0)
                _order.RemoveAt(at);
            _order.Add(slot);
        }
        Revision++;
    }

    /// <summary>The open-space drop: <paramref name="moved"/> re-seats at
    /// the END of the root list.</summary>
    public void MoveRootToEnd(RootSlot moved)
    {
        int from = _order.IndexOf(moved);
        if (from < 0 || from == _order.Count - 1)
            return;
        _order.RemoveAt(from);
        _order.Add(moved);
        Revision++;
    }

    /// <summary>Reorders the root list: <paramref name="moved"/> re-seats
    /// itself before or after <paramref name="target"/>. Unknown slots
    /// no-op — the sync owns membership, this owns order only.</summary>
    public void MoveRoot(RootSlot moved, RootSlot target, bool after)
    {
        if (moved == target)
            return;
        int from = _order.IndexOf(moved);
        if (from < 0)
            return;
        _order.RemoveAt(from);
        int to = _order.IndexOf(target);
        if (to < 0)
        {
            _order.Insert(from, moved);
            return;
        }
        _order.Insert(after ? to + 1 : to, moved);
        Revision++;
    }

    public SceneGroup? Find(Guid id) =>
        _groups.Find(group => group.Id == id);

    public SceneGroup? GroupOf(SelectionId member) =>
        _groups.Find(group => group.Members.Contains(member));

    /// <summary>The group the user EXPLICITLY selected by clicking its
    /// head. Set by the head click alone — hand-selecting every member
    /// stays a member-level selection, never this. It survives only while
    /// the selection still IS that group's membership.</summary>
    public Guid? ActiveGroupId { get; set; }

    /// <summary>The explicitly selected group, if the selection still
    /// equals its membership — the ONLY way a multiselect counts as "the
    /// group". A drifted selection clears the state.</summary>
    public SceneGroup? ActiveSelection(IReadOnlyList<SelectionId> selected)
    {
        if (ActiveGroupId is not { } id)
            return null;
        if (Find(id) is { } group && SelectionEquals(group, selected))
            return group;
        ActiveGroupId = null;
        return null;
    }

    /// <summary>Whether the selection's entity set equals the group's
    /// member set.</summary>
    private static bool SelectionEquals(
        SceneGroup group, IReadOnlyList<SelectionId> selected)
    {
        int entities = 0;
        foreach (var id in selected)
        {
            if (!Selection.EntitySelection.IsEntity(id.Kind))
                continue;
            entities++;
            if (!group.Members.Contains(id))
                return false;
        }
        return entities >= 2 && entities == group.Members.Count;
    }

    /// <summary>The lock, one verb: flipping it bumps the revision so the
    /// rows restate their grips.</summary>
    public void SetLocked(Guid id, bool locked)
    {
        if (Find(id) is not { } group || group.Locked == locked)
            return;
        group.Locked = locked;
        Revision++;
    }

    /// <summary>Whether the entity belongs to a locked group — the
    /// STRUCTURE question (drag, rename, dissolve).</summary>
    public bool IsLockedMember(SelectionId member) =>
        GroupOf(member) is { Locked: true };

    /// <summary>The TRANSFORM question, selection-aware: a locked group
    /// freezes its children individually, but a selection holding the
    /// whole membership is the group moving as one — the lock never
    /// refuses the group itself.</summary>
    public bool IsLockedChild(
        SelectionId member, IReadOnlyList<SelectionId> selected)
    {
        if (GroupOf(member) is not { Locked: true } group)
            return false;
        foreach (var one in group.Members)
            if (!ContainsEntity(selected, one))
                return true;
        return false;
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
                DissolveThinned(i);
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
            // The freed entity lands beside its old group. A member that
            // is actually moving into ANOTHER group leaves a stray slot
            // here; the next root sync collects it.
            int at = RootIndexOfGroup(group.Id);
            if (at >= 0)
                _order.Insert(at + 1, RootSlot.For(member));
            else
                _order.Add(RootSlot.For(member));
            if (group.Members.Count < 2)
                DissolveThinned(i);
            return true;
        }
        return false;
    }

    /// <summary>A group thinned below two dissolves in place: the
    /// survivor, if any, takes the group's seat in the root order.</summary>
    private void DissolveThinned(int index)
    {
        var group = _groups[index];
        int at = RootIndexOfGroup(group.Id);
        if (at >= 0)
        {
            _order.RemoveAt(at);
            if (group.Members.Count == 1)
                _order.Insert(at, RootSlot.For(group.Members[0]));
        }
        _groups.RemoveAt(index);
    }

    private int RootIndexOfGroup(Guid id)
    {
        for (int i = 0; i < _order.Count; i++)
            if (_order[i].IsGroup && _order[i].GroupId == id)
                return i;
        return -1;
    }

    private static bool ContainsEntity(
        IReadOnlyList<SelectionId> entities, SelectionId id)
    {
        foreach (var candidate in entities)
            if (candidate.Equals(id))
                return true;
        return false;
    }
}
