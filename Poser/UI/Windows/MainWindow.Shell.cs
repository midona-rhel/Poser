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
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The burger menu and the shell commands it issues.</summary>
public partial class MainWindow
{
    /// <summary>The titlebar burger menu, anchored under its own button.</summary>
    private void DrawShellMenu()
    {
        BuildShellMenu();
        if (_shellMenuOpenRequested)
        {
            _shellMenuOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##shell-burger-menu",
                _shellMenuAnchor,
                _shellMenuItems,
                Crystarium.FloatingMenu.MeasureWidth(_shellMenuItems));
        }
        int clicked = Crystarium.FloatingMenu.Draw("##shell-burger-menu");
        if (clicked >= 0 && clicked < _shellMenuItems.Length)
            InvokeShellCommand((ShellCommand)clicked);
        int subClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0
            && subParent < _shellMenuItems.Length)
            InvokeShellSubmenu((ShellCommand)subParent, subClicked);
    }

    /// <summary>Updates the shell menu when its visible state changes.</summary>
    private void BuildShellMenu()
    {
        // Pose-file commands need the selected actor's skeleton.
        bool poseTarget = SelectedSkeleton() != null;
        var uiConfig = Config.ConfigurationService.Instance.Config.UI;
        bool sceneOpen = GetSceneWindowOpen?.Invoke() ?? true;
        bool inspectorOpen = GetInspectorWindowOpen?.Invoke() ?? true;
        int layoutState = (uiConfig.DetachedShell ? 1 : 0)
            | (sceneOpen ? 2 : 0)
            | (_contentHidden ? 4 : 0)
            | (uiConfig.SplitInspector ? 8 : 0)
            | (inspectorOpen ? 16 : 0);
        if (_shellMenuRowsBuilt
            && poseTarget == _shellMenuPoseTarget
            && layoutState == _shellMenuLayoutState)
            return;
        _shellMenuRowsBuilt = true;
        _shellMenuPoseTarget = poseTarget;
        _shellMenuLayoutState = layoutState;

        FillShellMenuItems(
            _shellMenuItems,
            poseTarget,
            uiConfig.DetachedShell,
            sceneOpen,
            _contentHidden,
            uiConfig.SplitInspector,
            inspectorOpen);
    }

    /// <summary>Fills the shell menu rows for the current UI state.</summary>
    internal static void FillShellMenuItems(
        Span<ContextMenuItem> items,
        bool poseTarget,
        bool detachedShell,
        bool sceneOpen,
        bool contentHidden,
        bool splitInspector = false,
        bool inspectorOpen = true)
    {
        items[(int)ShellCommand.ShowLibrary] =
            new ContextMenuItem("Show library", TablerIcon.Book);
        items[(int)ShellCommand.OpenSpawn] =
            new ContextMenuItem("Open the spawn menu", TablerIcon.Plus);
        items[(int)ShellCommand.Pose] =
            new ContextMenuItem(
                "Pose", TablerIcon.Walk, disabled: !poseTarget,
                submenuItems:
                [
                    new ContextMenuItem("Import", TablerIcon.Download),
                    new ContextMenuItem("Export", TablerIcon.Upload),
                    new ContextMenuItem("Auto-saves", TablerIcon.DeviceFloppy),
                ]);
        items[(int)ShellCommand.Scene] =
            new ContextMenuItem(
                "Scene", TablerIcon.Movie,
                submenuItems:
                [
                    new ContextMenuItem("Save", TablerIcon.DeviceFloppy),
                ]);
        items[(int)ShellCommand.LayoutSeparator] = ContextMenuItem.Separator;
        // The properties panel is the main window's own content: it opens
        // and closes, and only while the sidebar lives apart from it.
        items[(int)ShellCommand.PropertiesPanel] =
            new ContextMenuItem(
                contentHidden
                    ? "Open the properties panel"
                    : "Close the properties panel",
                contentHidden ? TablerIcon.LayoutPanel : TablerIcon.X,
                disabled: !detachedShell);
        items[(int)ShellCommand.Sidebar] =
            new ContextMenuItem(
                "Sidebar", TablerIcon.LayoutSidebarLeft,
                submenuItems: PanelVerbs(
                    TablerIcon.LayoutSidebarLeft,
                    attached: !detachedShell, open: sceneOpen));
        items[(int)ShellCommand.Inspector] =
            new ContextMenuItem(
                "Inspector", TablerIcon.LayoutSidebarRight,
                submenuItems: PanelVerbs(
                    TablerIcon.LayoutSidebarRight,
                    attached: !splitInspector, open: inspectorOpen));
        items[(int)ShellCommand.SettingsSeparator] = ContextMenuItem.Separator;
        items[(int)ShellCommand.OpenSettings] =
            new ContextMenuItem("Open settings", TablerIcon.Settings);
    }

    /// <summary>One panel's verbs: Attach or Detach by its state, then
    /// Open or Close — which only a detached panel can do.</summary>
    private static ContextMenuItem[] PanelVerbs(
        TablerIcon glyph, bool attached, bool open) =>
    [
        new ContextMenuItem(attached ? "Detach" : "Attach", glyph),
        open
            ? new ContextMenuItem("Close", TablerIcon.X, disabled: attached)
            : new ContextMenuItem("Open", glyph, disabled: attached),
    ];

    /// <summary>Character data is saved only for an owned actor: one
    /// Poser spawned, or the player's own character.</summary>
    private bool SaveOwnedActorEntry(ActorId actorId, string name)
    {
        if (ResolveActorDescriptor(actorId) is not { IsOwned: true })
        {
            _notices.Refused(
                "Only an actor you spawned or your own character can be saved to the library.");
            return false;
        }
        return _scenePane.SaveActorEntry(actorId.LogicalId, name);
    }

    /// <summary>Whether every actor among the members is owned; a group
    /// holding anyone else's actor saves without appearance.</summary>
    private bool AllActorsOwned(IReadOnlyList<SelectionId> members)
    {
        foreach (var member in members)
            if (member.Actor is { } actorId
                && ResolveActorDescriptor(actorId) is not { IsOwned: true })
                return false;
        return true;
    }

    /// <summary>What the active pane keeps in the content footer between
    /// the two attach seats.</summary>
    private void DrawFooterMiddle(Vector2 origin, Vector2 size)
    {
        if (_activeTab == "Pose" && SelectedSkeleton() is { } skeleton)
            _poseInspector.DrawParentingBar(origin, size, skeleton);
    }

    /// <summary>Runs one row of a burger submenu, routed by its parent.</summary>
    private void InvokeShellSubmenu(ShellCommand parent, int index)
    {
        switch (parent)
        {
            case ShellCommand.Pose:
                if (SelectedSkeleton() is not { } skeleton)
                    return;
                switch (index)
                {
                    case 0:
                        _poseFileSection.RequestImportMenu(withPresets: true);
                        break;
                    case 1:
                        _poseFileSection.RequestExportMenu();
                        break;
                    case 2:
                        _poseFileSection.OpenAutoSaves(skeleton);
                        break;
                }
                break;
            case ShellCommand.Scene:
                if (index == 0)
                    _scenePane.RequestLibrarySave();
                break;
            case ShellCommand.Sidebar:
                if (index == 0)
                    RequestDetachToggle();
                else
                    OnSceneWindowToggleRequested?.Invoke();
                break;
            case ShellCommand.Inspector:
                if (index == 0)
                    OnInspectorSplitToggleRequested?.Invoke();
                else
                    OnInspectorWindowToggleRequested?.Invoke();
                break;
        }
    }

    /// <summary>Requests the shell layout toggle.</summary>
    public event Action? OnDetachToggleRequested;

    /// <summary>Requests the inspector split toggle.</summary>
    public event Action? OnInspectorSplitToggleRequested;

    internal void RequestDetachToggle() => OnDetachToggleRequested?.Invoke();

    /// <summary>Runs one command. The skeleton is resolved at invocation, not
    /// captured at build: the row array outlives every selection it was built
    /// under.</summary>
    private void InvokeShellCommand(ShellCommand command)
    {
        switch (command)
        {
            case ShellCommand.ShowLibrary:
                ShowLibrary();
                break;
            case ShellCommand.OpenSpawn:
                // The menu anchor is also the spawn browser's anchor.
                OnSpawnBrowserRequested?.Invoke(
                    _shellMenuAnchor, SpawnBrowserTab.All);
                break;
            case ShellCommand.PropertiesPanel:
                ContentHidden = !ContentHidden;
                break;
            case ShellCommand.OpenSettings:
                OnSettingsRequested?.Invoke();
                break;
        }
    }
}
