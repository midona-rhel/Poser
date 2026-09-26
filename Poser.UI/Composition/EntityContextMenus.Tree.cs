using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Poser.Domain.Identity;

namespace Poser.UI;

internal sealed partial class EntityContextMenus
{
    private string? _ctxBoneExpandKey;
    private string? _ctxBranchExpandKey;
    private string _ctxBranchLabel = "category";
    private SkeletonId? _ctxBranchSkeleton;

    private ContextMenuItem[] BuildTreeSubmenu(string? key, out List<Action?> actions)
    {
        bool disabled = key == null || !string.IsNullOrEmpty(_sidebar.Filter);
        actions =
        [
            () => { if (key != null) _sidebar.SetTreeCollapsed(key, false, false); },
            () => { if (key != null) _sidebar.SetTreeCollapsed(key, true, false); },
            () => { if (key != null) _sidebar.SetTreeCollapsed(key, false, true); },
            () => { if (key != null) _sidebar.SetTreeCollapsed(key, true, true); },
            null,
            () => _openSkeletonSettings(),
        ];
        string? help = !string.IsNullOrEmpty(_sidebar.Filter)
            ? "Clear the sidebar search to change disclosure" : null;
        return
        [
            new("Expand", TablerIcon.SquarePlus, disabled: disabled, help: help),
            new("Collapse", TablerIcon.SquareMinus, disabled: disabled, help: help),
            new("Expand all", TablerIcon.SquarePlus, disabled: disabled, help: "This branch and its descendants"),
            new("Collapse all", TablerIcon.SquareMinus, disabled: disabled, help: "This branch and its descendants"),
            ContextMenuItem.Separator,
            new("Skeleton settings…", TablerIcon.Settings, help: "Shared settings for all actors, not just this branch"),
        ];
    }

    private ContextMenuItem[] BuildActorPoseSubmenu(ActorId actorId, out List<Action?> actions)
    {
        var sourceLabel = ActorNames.Display(_configuration, actorId,
            _scene.Snapshot.FindActor(actorId)?.Name ?? "Actor");
        actions =
        [
            () => _poseFileSection.RequestImportMenu(withPresets: true, target: actorId),
            () => _poseFileSection.OpenImportFromFile(actorId),
            () => _poseFileSection.RequestExportMenu(actorId),
            () => _cleanPose.Stash(actorId, sourceLabel),
        ];
        var items = new List<ContextMenuItem>
        {
            new("Import", TablerIcon.Download),
            new("Import from file", TablerIcon.FileText),
            new("Export", TablerIcon.Upload),
            new("Stash", TablerIcon.Stack2),
        };
        if (_cleanPose.HasStash)
        {
            items.Add(new("Apply stashed", TablerIcon.ArrowBackUp));
            actions.Add(() => _cleanPose.ApplyStash(actorId));
        }
        return items.ToArray();
    }

    private static void OpenContextMenu(string id, ContextMenuItem[] items)
    {
        // Right-clicking another target of the same kind replaces the menu;
        // the toggle behavior used by toolbar dropdowns must not close it.
        Crystarium.FloatingMenu.Dismiss(id);
        Crystarium.FloatingMenu.Open(id, ImGui.GetMousePos(), items,
            Crystarium.FloatingMenu.MeasureWidth(items));
    }

    private void SetGroupTreeCollapsed(Guid id, bool collapsed, bool subtree)
    {
        if (_groups.Find(id) is not { } group) return;
        _sidebar.SetTreeCollapsed("group:" + id, collapsed, false);
        if (!subtree) return;
        foreach (var member in group.Members)
            if (member.Actor is { } actor)
                _sidebar.SetTreeCollapsed("actor:" + actor, collapsed, true);
        foreach (var child in group.Children)
            SetGroupTreeCollapsed(child, collapsed, true);
    }
}
