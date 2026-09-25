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
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Duplicating entities and groups.</summary>
public partial class MainWindow
{
    /// <summary>Duplicates the selection: a whole group as a new group,
    /// otherwise each entity by its own kind through the same history-
    /// seamed calls the single menus use. Borrowed objects with no model
    /// have no copy; the selection stays on the ORIGINALS — the copies'
    /// bindings land on the scene's own refresh.</summary>
    private void DuplicateSelection(bool withPose)
    {
        if (_groups.ActiveSelection(_selection.Selected) is { } whole)
        {
            DuplicateGroup(whole, withPose);
            return;
        }
        foreach (var id in _selection.Selected.ToArray())
            DuplicateEntity(id, withPose);
    }

    private static ContextMenuItem[] DuplicateSubmenu(bool posable) =>
    [
        new ContextMenuItem("Duplicate", TablerIcon.Copy),
        new ContextMenuItem("Duplicate with pose", TablerIcon.Stack2,
            disabled: !posable),
    ];

    /// <summary>One entity's copy receipt, or null when
    /// the kind has none or the copy failed.</summary>
    private SceneEntityHandle? DuplicateEntity(SelectionId id, bool withPose)
    {
        var result = _creation.Duplicate(id, withPose);
        if (result.Handle is null) _notices.Failed(result.Detail ?? "The entity could not be duplicated.");
        return result.Handle;
    }

    // ── duplicating groups ───────────────────────────────────────────────
    // The copies spawn at once; their bindings land on the scene's own
    // refresh, so the group is assembled from the pump once every copy
    // has an id (or patience runs out and what did bind is grouped).

    private sealed class GroupCopy
    {
        public string Name = "";
        public bool Hidden, Paused, Night;
        public readonly List<SceneEntityHandle> Members = new();
        public readonly List<GroupCopy> Children = new();
        public Guid? Parent;
        public int Index = -1;
        public global::Poser.Application.Scene.RootSlot? Anchor;
        public int Frames;
    }

    private readonly List<GroupCopy> _groupCopies = new();

    private const int GroupCopyPatience = 120;

    /// <summary>Copies the group and everything beneath it into a new
    /// group with the next name in its series, seated right after the original at the
    /// same level, gates and all.</summary>
    private void DuplicateGroup(global::Poser.Application.Scene.SceneGroup group, bool withPose)
    {
        var copy = CopyGroupTree(group, withPose);
        copy.Parent = group.ParentId;
        if (group.ParentId is { } parentId && _groups.Find(parentId) is { } parent)
            copy.Index = parent.Children.IndexOf(group.Id) + 1;
        else
            copy.Anchor = global::Poser.Application.Scene.RootSlot.ForGroup(group.Id);
        _groupCopies.Add(copy);
    }

    private GroupCopy CopyGroupTree(global::Poser.Application.Scene.SceneGroup group, bool withPose)
    {
        var copy = new GroupCopy
        {
            Name = group.Name,
            Hidden = group.Hidden,
            Paused = group.Paused,
            Night = group.Night,
        };
        foreach (var member in group.Members)
            if (DuplicateEntity(member, withPose) is { } made)
                copy.Members.Add(made);
        foreach (var childId in group.Children)
            if (_groups.Find(childId) is { } child)
                copy.Children.Add(CopyGroupTree(child, withPose));
        return copy;
    }

    private void PumpGroupCopies()
    {
        _pendingDuplicate.Reconcile(handle => _creation.Resolve(handle), _selection);
        for (int i = _groupCopies.Count - 1; i >= 0; i--)
        {
            var copy = _groupCopies[i];
            if (!CopyBound(copy) && ++copy.Frames < GroupCopyPatience)
                continue;
            _groupCopies.RemoveAt(i);
            var made = _groupSteps.Run("Duplicate group", () =>
            {
                var result = RealizeGroupCopy(copy);
                if (result == null) return null;
                if (copy.Parent is { } parentId && _groups.Find(parentId) != null)
                    _groupSteps.Nest(result.Id, parentId, copy.Index);
                else if (copy.Anchor is { } anchor)
                    _groupSteps.MoveRoot(
                        global::Poser.Application.Scene.RootSlot.ForGroup(result.Id), anchor, after: true);
                return result;
            });
            if (made == null)
            {
                _notices.Failed($"'{copy.Name}' could not be duplicated: nothing in it copied.");
                continue;
            }
        }
    }

    private bool CopyBound(GroupCopy copy)
    {
        foreach (var member in copy.Members)
            if (ResolveCopy(member) == null)
                return false;
        foreach (var child in copy.Children)
            if (!CopyBound(child))
                return false;
        return true;
    }

    private global::Poser.Application.Scene.SceneGroup? RealizeGroupCopy(GroupCopy copy)
    {
        var ids = new List<SelectionId>();
        foreach (var member in copy.Members)
            if (ResolveCopy(member) is { } id)
                ids.Add(id);
        var children = new List<global::Poser.Application.Scene.SceneGroup>();
        foreach (var child in copy.Children)
            if (RealizeGroupCopy(child) is { } made)
                children.Add(made);
        if (ids.Count + children.Count == 0)
            return null;
        var group = _groupSteps.Create(
            EntityNames.Next(copy.Name, _groups.All.Select(x => x.Name)), ids, allowThin: true);
        if (group == null)
            return null;
        foreach (var child in children)
            _groupSteps.Nest(child.Id, group.Id);
        if (copy.Hidden)
            SetGroupHidden(group, true);
        if (copy.Paused)
            SetGroupPaused(group, true);
        if (copy.Night)
            SetGroupNight(group, true);
        return group;
    }

    private SelectionId? ResolveCopy(SceneEntityHandle receipt) => _creation.Resolve(receipt);

    private readonly Composition.PendingSelection<SceneEntityHandle> _pendingDuplicate = new();

    private void DuplicateAndSelect(SelectionId source, bool withPose = false)
    {
        if (DuplicateEntity(source, withPose) is { } copy)
            _pendingDuplicate.Arm(copy);
    }
}
