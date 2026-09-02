using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game;
using Poser.Game.Transforms;
using Poser.Domain.Companions;
using Poser.Game.Posing;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Drag and drop between rows and groups, and the tree collapse state.</summary>
public partial class MainWindow
{
    /// <summary>A tree drag released. The root list is the USER'S order —
    /// any entity or group head re-seats at the caret, kinds interleaved.
    /// Group structure rides the same gesture: INTO a head joins, beside a
    /// member inserts there, open space just leaves the group. Dragging a
    /// selected row carries the whole entity selection.</summary>
    private void OnRowDropped(
        ShellSidebarRow dragged,
        ShellSidebarRow? target,
        RowDropPosition position)
    {
        int pointerLevel = _vm.DropLevel;
        (target, position) = ResolveDropLevel(target, position);
        _log.Debug(
            $"Sidebar drop: {DescribeRow(dragged)} -> {(target == null ? "nothing" : DescribeRow(target))} "
            + $"{position} at level {pointerLevel}");
        // A group head re-seats among the root slots like anything else;
        // open space is the end of the list.
        if (dragged.Tag is GroupRowTag draggedGroup)
        {
            DropGroup(draggedGroup.Id, target, position);
            return;
        }

        if (dragged.Tag is not SelectionId draggedId
            || !global::Poser.Application.Selection.EntitySelection
                .IsEntity(draggedId.Kind))
            return;
        var moved = new List<SelectionId>();
        if (_selection.IsSelected(draggedId))
        {
            foreach (var id in _selection.Selected)
                if (global::Poser.Application.Selection.EntitySelection
                        .IsEntity(id.Kind))
                    moved.Add(id);
        }
        if (moved.Count == 0)
            moved.Add(draggedId);

        // Into a group's head: append in drag order.
        if (target?.Tag is GroupRowTag intoGroup
            && position == RowDropPosition.Into)
        {
            foreach (var id in moved)
            {
                LeaveGroupOverrides(id);
                _groupSteps.AddMember(intoGroup.Id, id);
                JoinGroupOverrides(id);
            }
            return;
        }

        // Beside a grouped member: insert at its place in that group.
        if (target?.Tag is SelectionId targetId
            && _groups.GroupOf(targetId) is { } host)
        {
            int index = host.Members.IndexOf(targetId);
            if (position == RowDropPosition.After)
                index++;
            foreach (var id in moved)
            {
                LeaveGroupOverrides(id);
                _groupSteps.AddMember(host.Id, id, index);
                JoinGroupOverrides(id);
                index = host.Members.IndexOf(id) + 1;
            }
            return;
        }

        // Beside a nested group: the dragged rows join that group's parent.
        if (target?.Tag is GroupRowTag besideGroup
            && position is RowDropPosition.Before or RowDropPosition.After
            && _groups.Find(besideGroup.Id) is { ParentId: { } parentId })
        {
            foreach (var id in moved)
            {
                LeaveGroupOverrides(id);
                _groupSteps.AddMember(parentId, id);
                JoinGroupOverrides(id);
            }
            return;
        }

        // A root seam: the dragged rows leave any group and re-seat at
        // the caret, in carry order.
        if (target != null
            && position is RowDropPosition.Before or RowDropPosition.After
            && RootSlotOf(target) is { } anchor)
        {
            bool after = position == RowDropPosition.After;
            foreach (var id in moved)
            {
                LeaveGroupOverrides(id);
                _groupSteps.RemoveMember(id);
                _groupSteps.MoveRoot(RootSlot.For(id), anchor, after);
                anchor = RootSlot.For(id);
                after = true;
            }
            return;
        }

        // Open space: the end of the root list, leaving any group — the
        // caret at the tree's tail marks exactly this.
        foreach (var id in moved)
        {
            LeaveGroupOverrides(id);
            _groupSteps.RemoveMember(id);
            _groupSteps.MoveRootToEnd(RootSlot.For(id));
        }
    }

    /// <summary>Folds or opens the tree: the root row alone, or the root
    /// and everything keyed beneath it.</summary>
    private void SetTreeCollapsed(string root, bool collapsed, bool subtree)
    {
        void Set(string key)
        {
            if (collapsed)
                _collapsedNodes.Add(key);
            else
                _collapsedNodes.Remove(key);
        }
        Set(root);
        if (!subtree)
            return;
        IEnumerable<string> keys = _knownActorNodes.Concat(_knownCategoryNodes);
        foreach (var key in keys.ToArray())
            if (key.StartsWith(root + "/", StringComparison.Ordinal))
                Set(key);
    }

    /// <summary>The root slot a drop row stands for: a group head or a
    /// grouped member answers its group's slot, an ungrouped entity its
    /// own. Rows with no root stake — bones, categories, reference
    /// images, attached rows — answer null and the drop is a no-op.</summary>
    private string DescribeRow(ShellSidebarRow row) => row.Tag switch
    {
        GroupRowTag tag => $"group '{row.Label}' ({tag.Id.ToString()[..8]}, depth {row.Depth})",
        SelectionId id => $"{id.Kind} '{row.Label}' (depth {row.Depth})",
        _ => $"'{row.Label}'",
    };

    /// <summary>The pointer's indent decides the level at a seam. Right of
    /// a group head's indent, "after" it means "first inside it"; left of a
    /// group's last row's indent, "after" it means "after the group" — one
    /// level out per 20px, so a drag can climb out of nested groups in one
    /// motion. Returns the row to act on and the position against it.</summary>
    private (ShellSidebarRow? Target, RowDropPosition Position) ResolveDropLevel(
        ShellSidebarRow? target, RowDropPosition position)
    {
        int level = _vm.DropLevel;
        _vm.DropLevel = -1;
        if (target == null || level < 0
            || position is not (RowDropPosition.Before or RowDropPosition.After))
            return (target, position);
        if (level > target.Depth)
        {
            if (position == RowDropPosition.After && target.Tag is GroupRowTag)
                return (target, RowDropPosition.Into);
            return (target, position);
        }
        if (level >= target.Depth || position != RowDropPosition.After)
            return (target, position);
        // Climb: the group at the pointer's level that contains this row.
        var host = HostGroupOf(target);
        var climbed = host;
        int depth = target.Depth - 1;
        while (climbed != null && depth > level)
        {
            climbed = _groups.ParentOf(climbed);
            depth--;
        }
        if (climbed == null || host == null)
            return (target, position);
        var stand = new ShellSidebarRow { Depth = depth, Tag = new GroupRowTag(climbed.Id) };
        return (stand, RowDropPosition.After);
    }

    /// <summary>A dragged group: onto a group head it nests there; beside
    /// a nested row it becomes a sibling in that row's group; beside a root
    /// row or into nothing it comes out to the root order. A nest past the
    /// depth limit is refused by name and nothing moves.</summary>
    private void DropGroup(Guid groupId, ShellSidebarRow? target, RowDropPosition position)
    {
        if (target?.Tag is GroupRowTag intoGroup && position == RowDropPosition.Into)
        {
            if (_groups.CanNest(groupId, intoGroup.Id, out var reason))
                _log.Debug($"Sidebar drop: nest -> {_groupSteps.Nest(groupId, intoGroup.Id)}");
            else
                _notices.Failed($"Group not moved: {reason}");
            return;
        }
        // Beside a row that lives inside a group: a sibling there.
        if (target != null && position is RowDropPosition.Before or RowDropPosition.After
            && HostGroupOf(target) is { } host)
        {
            if (!_groups.CanNest(groupId, host.Id, out var reason))
            {
                _notices.Failed($"Group not moved: {reason}");
                return;
            }
            int index = target.Tag is GroupRowTag sibling ? host.Children.IndexOf(sibling.Id) : -1;
            if (index >= 0 && position == RowDropPosition.After)
                index++;
            _groupSteps.Nest(groupId, host.Id, index);
            return;
        }
        // The root order.
        var slot = RootSlot.ForGroup(groupId);
        if (target != null && position is RowDropPosition.Before or RowDropPosition.After
            && RootSlotOf(target) is { } anchor)
        {
            if (_groups.Find(groupId) is { ParentId: not null })
                _groupSteps.Unnest(groupId, anchor, position == RowDropPosition.After);
            else
                _groupSteps.MoveRoot(slot, anchor, position == RowDropPosition.After);
            return;
        }
        if (_groups.Find(groupId) is { ParentId: not null })
            _groupSteps.Unnest(groupId);
        else
            _groupSteps.MoveRootToEnd(slot);
    }

    /// <summary>The group a row sits INSIDE: a member's group, or a nested
    /// group's parent. Null for anything at the root.</summary>
    private global::Poser.Application.Scene.SceneGroup? HostGroupOf(ShellSidebarRow row)
    {
        if (row.Tag is GroupRowTag tag)
            return _groups.Find(tag.Id) is { } group ? _groups.ParentOf(group) : null;
        if (row.Tag is SelectionId id)
            return _groups.GroupOf(id);
        return null;
    }

    private RootSlot? RootSlotOf(ShellSidebarRow row)
    {
        if (row.Tag is GroupRowTag tag)
            return _groups.Find(tag.Id) is { } group
                ? RootSlot.ForGroup(_groups.RootOf(group).Id)
                : null;
        if (row.Tag is not SelectionId id
            || !global::Poser.Application.Selection.EntitySelection
                .IsEntity(id.Kind))
            return null;
        if (_groups.GroupOf(id) is { } host)
            return RootSlot.ForGroup(_groups.RootOf(host).Id);
        return RootSlot.For(id);
    }

    /// <summary>The drag ghost's text: a dragged row that rides with the
    /// entity multiselect announces the whole cargo, not just itself.</summary>
    private string DragGhostFor(ShellSidebarRow row)
    {
        if (row.Tag is not SelectionId id
            || !global::Poser.Application.Selection.EntitySelection
                .IsEntity(id.Kind)
            || !_selection.IsSelected(id))
            return row.Label;
        int entities = global::Poser.Application.Selection.EntitySelection
            .CountEntities(_selection.Selected);
        if (entities < 2)
            return row.Label;
        if (_multiTitleCount != entities)
        {
            _multiTitleCount = entities;
            _multiTitle = $"{entities} selected";
        }
        return _multiTitle;
    }

    private IActor? ResolveActorRow(ShellSidebarRow row)
    {
        if (row.Tag is not SelectionId
            { Kind: SceneEntityKind.Actor, Actor: { } actorId })
            return null;
        var resolved = _bindings.Resolve(actorId);
        return resolved.Success ? resolved.Value : null;
    }

    // ── typed tab content hosted inside the shell ──────────────────────
}
