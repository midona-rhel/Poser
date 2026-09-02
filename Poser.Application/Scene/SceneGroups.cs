using Poser.Domain.Identity;
using Poser.Domain.Scene;

namespace Poser.Application.Scene;

/// <summary>A named set of scene entities the user moves, selects and
/// hides as one, with subgroups nested inside it. Within a group the
/// direct members list first, then the subgroups.</summary>
public sealed class SceneGroup
{
    public required Guid Id { get; init; }
    public required string Name { get; set; }

    /// <summary>The direct entity members, in the user's order.</summary>
    public required List<SelectionId> Members { get; init; }

    /// <summary>The subgroups, in the user's order.</summary>
    public List<Guid> Children { get; } = new();

    /// <summary>The group this one sits inside; null at the root.</summary>
    public Guid? ParentId { get; set; }

    public bool Locked { get; set; }

    /// <summary>The visibility GATE: closed hides everything beneath and
    /// remembers each member's own flag; open gives every member its own
    /// flag back. A closed gate anywhere up the chain keeps a member
    /// hidden.</summary>
    public bool Hidden { get; set; }
    public readonly Dictionary<SelectionId, bool> RememberedVisible = new();

    /// <summary>The play gate, actors only: closed pauses everything
    /// beneath, open lets each actor play whatever it was playing.</summary>
    public bool Paused { get; set; }
    public readonly Dictionary<SelectionId, bool> RememberedPlaying = new();

    /// <summary>The night gate, scenery only: closed puts everything
    /// beneath in its night dressing, open gives each its own back.</summary>
    public bool Night { get; set; }
    public readonly Dictionary<SelectionId, bool> RememberedNight = new();

    public int ItemCount => Members.Count + Children.Count;
}

/// <summary>One seat in the root order: an entity or a root-level group.</summary>
public readonly record struct RootSlot(SelectionId? Entity, Guid GroupId)
{
    public static RootSlot For(SelectionId entity) => new(entity, Guid.Empty);
    public static RootSlot ForGroup(Guid id) => new(null, id);
    public bool IsGroup => GroupId != Guid.Empty;
}

/// <summary>The scene's groups and the root order. Groups nest to
/// <see cref="MaxDepth"/> levels; a drop past that is refused by name.</summary>
public sealed class SceneGroups
{
    /// <summary>Root groups are depth 1; a group at depth 4 holds no groups.</summary>
    public const int MaxDepth = 4;

    private readonly List<SceneGroup> _groups = new();

    /// <summary>The root list in the USER'S order, kinds interleaved:
    /// entities and root-level groups.</summary>
    private readonly List<RootSlot> _order = new();

    public int Revision { get; private set; }

    /// <summary>Every group, nested ones included.</summary>
    public IReadOnlyList<SceneGroup> All => _groups;

    public IReadOnlyList<RootSlot> RootOrder => _order;

    public Guid? ActiveGroupId { get; set; }

    /// <summary>Marks a change made on a group object directly (an
    /// override), so the sidebar rebuilds.</summary>
    public void Touch() => Revision++;

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

    // ── creation and dissolution ─────────────────────────────────────────

    /// <summary>Groups the entities. When every member already shares one
    /// parent group the new group nests inside it; otherwise it is a root
    /// group seated where its first member sat.</summary>
    public SceneGroup? Create(
        string name, IReadOnlyList<SelectionId> members, bool allowThin = false)
    {
        var kept = new List<SelectionId>();
        foreach (var member in members)
            if (Selection.EntitySelection.IsEntity(member.Kind)
                && !kept.Contains(member))
                kept.Add(member);
        // A group being assembled from copies may start thin: its
        // subgroups nest into it right after.
        if (kept.Count < 2 && !allowThin)
            return null;

        SceneGroup? sharedParent = kept.Count > 0 ? GroupOf(kept[0]) : null;
        foreach (var member in kept)
            if (GroupOf(member) != sharedParent)
            {
                sharedParent = null;
                break;
            }
        if (sharedParent != null && Depth(sharedParent.Id) >= MaxDepth)
            sharedParent = null;

        // One home per entity: joining this group leaves any other — and
        // leaves it for good, never re-seated into the old parent on the
        // way (that listed a member twice, 2026-09-02).
        foreach (var member in kept)
        {
            RemoveMemberCore(member, reseat: false);
            RemoveRootSlot(RootSlot.For(member));
        }

        var group = new SceneGroup
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? "Group" : name.Trim(),
            Members = kept,
        };
        _groups.Add(group);
        if (sharedParent != null && Find(sharedParent.Id) is { } parent)
        {
            group.ParentId = parent.Id;
            parent.Children.Add(group.Id);
        }
        else
        {
            // The group takes its first member's seat in the root order;
            // the members' own slots fold into it.
            int at = _order.Count;
            for (int i = _order.Count - 1; i >= 0; i--)
                if (_order[i] is { IsGroup: false, Entity: { } slotted }
                    && kept.Contains(slotted))
                {
                    _order.RemoveAt(i);
                    at = i;
                }
            _order.Insert(Math.Min(at, _order.Count), RootSlot.ForGroup(group.Id));
        }
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

    /// <summary>Ungroups: members and subgroups reclaim the group's seat
    /// in its parent (or the root order), in their order — ungrouping
    /// never scatters rows.</summary>
    public void Dissolve(Guid id)
    {
        if (Find(id) is not { Locked: false } group)
            return;
        Release(group);
        _groups.Remove(group);
        Revision++;
    }

    private void Release(SceneGroup group)
    {
        if (group.ParentId is { } parentId && Find(parentId) is { } parent)
        {
            int at = parent.Children.IndexOf(group.Id);
            if (at >= 0)
                parent.Children.RemoveAt(at);
            else
                at = parent.Children.Count;
            parent.Members.AddRange(group.Members);
            foreach (var childId in group.Children)
            {
                parent.Children.Insert(Math.Min(at++, parent.Children.Count), childId);
                if (Find(childId) is { } child)
                    child.ParentId = parent.Id;
            }
            return;
        }
        int seat = RootIndexOfGroup(group.Id);
        if (seat >= 0)
            _order.RemoveAt(seat);
        else
            seat = _order.Count;
        foreach (var member in group.Members)
            _order.Insert(seat++, RootSlot.For(member));
        foreach (var childId in group.Children)
        {
            _order.Insert(seat++, RootSlot.ForGroup(childId));
            if (Find(childId) is { } child)
                child.ParentId = null;
        }
    }

    // ── membership ───────────────────────────────────────────────────────

    public void RemoveMember(SelectionId member)
    {
        if (RemoveMemberCore(member, reseat: true))
            Revision++;
    }

    public void AddMember(Guid groupId, SelectionId member, int index = -1)
    {
        if (!Selection.EntitySelection.IsEntity(member.Kind)
            || Find(groupId) is not { } group)
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
            RemoveMemberCore(member, reseat: false);
            RemoveRootSlot(RootSlot.For(member));
        }
        if (index < 0 || index > group.Members.Count)
            index = group.Members.Count;
        group.Members.Insert(index, member);
        Revision++;
    }

    // ── nesting ──────────────────────────────────────────────────────────

    /// <summary>Whether <paramref name="childId"/> may sit inside
    /// <paramref name="parentId"/>: not itself, not one of its own
    /// descendants, and no deeper than <see cref="MaxDepth"/> counting the
    /// child's own subtree.</summary>
    public bool CanNest(Guid childId, Guid parentId, out string reason)
    {
        reason = "";
        if (childId == parentId)
        {
            reason = "A group cannot hold itself.";
            return false;
        }
        if (Find(childId) is not { } child || Find(parentId) is not { } parent)
        {
            reason = "That group is gone.";
            return false;
        }
        foreach (var ancestor in Ancestors(parent))
            if (ancestor.Id == childId)
            {
                reason = "A group cannot be moved into one of its own subgroups.";
                return false;
            }
        int depth = Depth(parentId) + Height(child);
        if (depth > MaxDepth)
        {
            reason = $"Groups nest {MaxDepth} deep at most; this would be {depth}.";
            return false;
        }
        return true;
    }

    /// <summary>Moves a group inside another, at <paramref name="index"/>
    /// among the parent's subgroups (-1 = last). Refused per
    /// <see cref="CanNest"/>.</summary>
    public bool Nest(Guid childId, Guid parentId, int index = -1)
    {
        if (!CanNest(childId, parentId, out _)
            || Find(childId) is not { } child || Find(parentId) is not { } parent)
            return false;
        int existing = parent.Children.IndexOf(childId);
        if (existing >= 0)
        {
            parent.Children.RemoveAt(existing);
            if (index > existing)
                index--;
        }
        else
        {
            Detach(child);
        }
        if (index < 0 || index > parent.Children.Count)
            index = parent.Children.Count;
        parent.Children.Insert(index, childId);
        child.ParentId = parent.Id;
        Revision++;
        return true;
    }

    /// <summary>Moves a nested group out to the root order, beside
    /// <paramref name="anchor"/> (or at the end).</summary>
    public void Unnest(Guid groupId, RootSlot? anchor = null, bool after = true)
    {
        if (Find(groupId) is not { ParentId: not null } group)
            return;
        Detach(group);
        var slot = RootSlot.ForGroup(groupId);
        RemoveRootSlot(slot);
        int to = anchor is { } a ? _order.IndexOf(a) : -1;
        if (to < 0)
            _order.Add(slot);
        else
            _order.Insert(after ? to + 1 : to, slot);
        Revision++;
    }

    private void Detach(SceneGroup group)
    {
        if (group.ParentId is { } parentId && Find(parentId) is { } parent)
            parent.Children.Remove(group.Id);
        else
            RemoveRootSlot(RootSlot.ForGroup(group.Id));
        group.ParentId = null;
    }

    public SceneGroup? ParentOf(SceneGroup group) =>
        group.ParentId is { } id ? Find(id) : null;

    /// <summary>The group's parents, nearest first.</summary>
    public IEnumerable<SceneGroup> Ancestors(SceneGroup group)
    {
        var current = ParentOf(group);
        int guard = 0;
        while (current != null && guard++ < 16)
        {
            yield return current;
            current = ParentOf(current);
        }
    }

    /// <summary>Root groups are 1.</summary>
    public int Depth(Guid id)
    {
        if (Find(id) is not { } group)
            return 0;
        int depth = 1;
        foreach (var _ in Ancestors(group))
            depth++;
        return depth;
    }

    /// <summary>Levels in the group's own subtree, itself included.</summary>
    public int Height(SceneGroup group)
    {
        int height = 1;
        foreach (var childId in group.Children)
            if (Find(childId) is { } child)
                height = Math.Max(height, 1 + Height(child));
        return height;
    }

    /// <summary>Every entity in the group and its subgroups, in order.</summary>
    public IEnumerable<SelectionId> Descendants(SceneGroup group)
    {
        foreach (var member in group.Members)
            yield return member;
        foreach (var childId in group.Children)
            if (Find(childId) is { } child)
                foreach (var nested in Descendants(child))
                    yield return nested;
    }

    /// <summary>The topmost group above (or being) this one.</summary>
    public SceneGroup RootOf(SceneGroup group)
    {
        var top = group;
        foreach (var ancestor in Ancestors(group))
            top = ancestor;
        return top;
    }

    // ── the root order ───────────────────────────────────────────────────

    public IReadOnlyList<RootSlot> SyncRoot(
        IReadOnlyList<SelectionId> rootEntities)
    {
        for (int i = _order.Count - 1; i >= 0; i--)
        {
            var slot = _order[i];
            bool keep = slot.IsGroup
                ? Find(slot.GroupId) is { ParentId: null }
                : slot.Entity is { } id && ContainsEntity(rootEntities, id);
            // A slot also leaves when an earlier copy already holds the
            // seat — the structural verbs guess positions and this sweep
            // is their garbage collector.
            if (!keep || _order.IndexOf(slot) < i)
                _order.RemoveAt(i);
        }
        foreach (var group in _groups)
            if (group.ParentId == null && RootIndexOfGroup(group.Id) < 0)
                _order.Add(RootSlot.ForGroup(group.Id));
        foreach (var id in rootEntities)
            if (_order.IndexOf(RootSlot.For(id)) < 0)
                _order.Add(RootSlot.For(id));
        return _order;
    }

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

    public void MoveRootToEnd(RootSlot moved)
    {
        int from = _order.IndexOf(moved);
        if (from < 0 || from == _order.Count - 1)
            return;
        _order.RemoveAt(from);
        _order.Add(moved);
        Revision++;
    }

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

    // ── lookups ──────────────────────────────────────────────────────────

    public SceneGroup? Find(Guid id) =>
        _groups.Find(group => group.Id == id);

    /// <summary>The group an entity sits in directly.</summary>
    public SceneGroup? GroupOf(SelectionId member) =>
        _groups.Find(group => group.Members.Contains(member));

    /// <summary>The active group when the selection is exactly its
    /// entities (subgroups included).</summary>
    public SceneGroup? ActiveSelection(IReadOnlyList<SelectionId> selected)
    {
        if (ActiveGroupId is not { } id)
            return null;
        if (Find(id) is { } group && SelectionEquals(group, selected))
            return group;
        ActiveGroupId = null;
        return null;
    }

    private bool SelectionEquals(
        SceneGroup group, IReadOnlyList<SelectionId> selected)
    {
        var all = new List<SelectionId>(Descendants(group));
        int entities = 0;
        foreach (var id in selected)
        {
            if (!Selection.EntitySelection.IsEntity(id.Kind))
                continue;
            entities++;
            if (!all.Contains(id))
                return false;
        }
        return entities >= 2 && entities == all.Count;
    }

    // ── locks ────────────────────────────────────────────────────────────

    public void SetLocked(Guid id, bool locked)
    {
        if (Find(id) is not { } group || group.Locked == locked)
            return;
        group.Locked = locked;
        Revision++;
    }

    /// <summary>Locked itself or under a locked group. A lock means one
    /// thing: nothing beneath transforms in the scene. Sidebar moves,
    /// nesting, renaming and dissolving ignore it.</summary>
    public bool IsLocked(SceneGroup group)
    {
        if (group.Locked)
            return true;
        foreach (var ancestor in Ancestors(group))
            if (ancestor.Locked)
                return true;
        return false;
    }

    public bool IsLockedMember(SelectionId member) =>
        GroupOf(member) is { } group && IsLocked(group);

    public bool IsLockedChild(
        SelectionId member, IReadOnlyList<SelectionId> selected)
    {
        if (GroupOf(member) is not { } group || !IsLocked(group))
            return false;
        var locked = group;
        foreach (var ancestor in Ancestors(group))
            if (ancestor.Locked)
                locked = ancestor;
        foreach (var one in Descendants(locked))
            if (!ContainsEntity(selected, one))
                return true;
        return false;
    }

    // ── housekeeping ─────────────────────────────────────────────────────

    /// <summary>Drops members that no longer exist; a group left with
    /// fewer than two items dissolves into its parent.</summary>
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
        }
        // Thinned groups dissolve deepest first, so a parent counts what
        // its children left behind.
        bool thinned;
        do
        {
            thinned = false;
            for (int i = _groups.Count - 1; i >= 0; i--)
                if (_groups[i].ItemCount < 2)
                {
                    DissolveThinned(i);
                    thinned = true;
                    changed = true;
                    break;
                }
        }
        while (thinned);
        if (changed)
            Revision++;
        return changed;
    }

    private bool RemoveMemberCore(SelectionId member, bool reseat)
    {
        for (int i = _groups.Count - 1; i >= 0; i--)
        {
            var group = _groups[i];
            if (!group.Members.Remove(member))
                continue;
            if (reseat)
            {
                // The freed entity lands beside its old group: in the
                // parent, or in the root order.
                if (group.ParentId is { } parentId && Find(parentId) is { } parent)
                {
                    parent.Members.Add(member);
                }
                else
                {
                    int at = RootIndexOfGroup(group.Id);
                    if (at >= 0)
                        _order.Insert(at + 1, RootSlot.For(member));
                    else
                        _order.Add(RootSlot.For(member));
                }
            }
            if (group.ItemCount < 2)
                DissolveThinned(i);
            return true;
        }
        return false;
    }

    private void DissolveThinned(int index)
    {
        var group = _groups[index];
        Release(group);
        _groups.RemoveAt(index);
    }

    private void RemoveRootSlot(RootSlot slot)
    {
        int at = _order.IndexOf(slot);
        if (at >= 0)
            _order.RemoveAt(at);
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
