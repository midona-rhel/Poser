using System;
using System.Collections.Generic;
using Dalamud.Bindings.ImGui;
using Poser.Domain.Identity;
using Poser.UI.Widgets;

namespace Poser.UI;

internal sealed partial class EntityContextMenus
{
    private string? _ctxBoneExpandKey;
    private string? _ctxBranchExpandKey;
    private string _ctxBranchLabel = "category";
    private SkeletonId? _ctxBranchSkeleton;

    private ContextMenuItem[] BuildTreeSubmenu(string? key)
    {
        bool disabled = key == null || !string.IsNullOrEmpty(_sidebar.Filter);
        string? help = !string.IsNullOrEmpty(_sidebar.Filter)
            ? "Clear the sidebar search to change disclosure" : null;
        void Set(bool collapsed, bool subtree) { if (key != null) _sidebar.SetTreeCollapsed(key, collapsed, subtree); }
        return
        [
            new("Expand", TablerIcon.SquarePlus, disabled: disabled, help: help) { OnInvoke = () => Set(false, false) },
            new("Collapse", TablerIcon.SquareMinus, disabled: disabled, help: help) { OnInvoke = () => Set(true, false) },
            new("Expand all", TablerIcon.SquarePlus, disabled: disabled, help: "This branch and its descendants") { OnInvoke = () => Set(false, true) },
            new("Collapse all", TablerIcon.SquareMinus, disabled: disabled, help: "This branch and its descendants") { OnInvoke = () => Set(true, true) },
            ContextMenuItem.Separator,
            new("Skeleton settings…", TablerIcon.Settings, help: "Shared settings for all actors, not just this branch") { OnInvoke = () => _openSkeletonSettings() },
        ];
    }

    private ContextMenuItem[] BuildActorPoseSubmenu(ActorId actorId)
    {
        var items = new List<ContextMenuItem>
        {
            new("Import", TablerIcon.Download) { OnInvoke = () => _poseFileSection.RequestImportMenu(actorId) },
            new("Import from file", TablerIcon.FileText) { OnInvoke = () => _poseFileSection.OpenImportFromFile(actorId) },
            new("Import from clipboard", TablerIcon.Copy) { OnInvoke = () => _poseFileSection.ImportFromClipboard(actorId) },
            new("Export", TablerIcon.Upload, submenuItems: _poseFileSection.BuildExportSubmenu(actorId)),
            new("Stash", TablerIcon.Stack2) { OnInvoke = () => _cleanPose.Stash(actorId) },
        };
        if (_cleanPose.HasStash)
            items.Add(new("Apply stashed", TablerIcon.ArrowBackUp) { OnInvoke = () => _cleanPose.ApplyStash(actorId) });
        return items.ToArray();
    }

    private static void OpenContextMenu(string id, ContextMenuItem[] items)
    {
        // Right-clicking another target of the same kind replaces the menu;
        // the toggle behavior used by toolbar dropdowns must not close it.
        FloatingMenu.Dismiss(id);
        FloatingMenu.Open(id, ImGui.GetMousePos(), items,
            FloatingMenu.MeasureWidth(items));
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
