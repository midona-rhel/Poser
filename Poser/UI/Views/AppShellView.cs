using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

public sealed class ShellSidebarRow
{
    public string Label = "";
    public string Count = "";
    public TablerIcon Icon = TablerIcon.User;
    /// <summary>Named custom icon (PoserIconSources) — wins over Icon when set.</summary>
    public string? IconName;
    public int Depth;              // 0 root, 1+ nested (20px indent per level)
    public bool HasChildren;
    /// <summary>Disclosure affordance shown but faded and inert — the row's
    /// children are temporarily unavailable (e.g. skeleton not yet resolved).
    /// The affordance is never erased once a row can disclose children.</summary>
    public bool ExpanderDisabled;
    /// <summary>Set on a merged category/bone row: the chevron toggles this
    /// key while the row body still selects the bone in Tag.</summary>
    public string? ExpandKey;
    public bool Expanded;
    public bool Active;
    public object? Tag;
    public bool ActorActions;
    public bool ActorVisible = true;
    public bool ActorPaused;
    public IReadOnlyList<Domain.Identity.BoneId>? OverlayBones;

    /// <summary>Last child of its parent → curved-L branch instead of T.</summary>
    public bool IsLastChild;
    /// <summary>Per ancestor depth: does a sibling line continue at that level?</summary>
    public bool[]? TreeLines;
}

public sealed class ShellSidebarSection
{
    public string Title = "";
    public bool ShowPlus;
    public List<ShellSidebarRow> Rows = new();
}

public sealed class ShellTab
{
    public string Label = "";
    public bool Active;
    public bool SceneTab; // drawn after the divider
}

public sealed class AppShellViewModel
{
    public bool GPoseActive = true;

    public List<ShellSidebarSection> Sections = new();
    public string SidebarSearch = "";
    public string StatusLeft = "2 actors";
    public string StatusRight = "142 bones · 60 fps";

    public List<ShellTab> Tabs = new();

    public int GizmoOperation;        // 0 translate, 1 rotate, 2 scale, 3 universal
    public int GizmoSpace;            // 0 local, 1 world
    public int RotationPivot;         // 0 self, 1 parent
    public bool RotationPivotEnabled;
    public bool RotationPivotParentAvailable;
    public int SymmetryMode;          // 0 off, 1 link, 2 mirror
    public bool PhysicsOn;
    public bool PhysicsAvailable;
    public bool SkeletonOverlayOn;
    public bool CanUndo = true;
    public bool CanRedo;
    public bool ShowSpawn;
    public bool ShowProject;
    /// <summary>Whether the active tab has a faithful standalone rendering.</summary>
    public bool ShowPopOut;

    /// <summary>
    /// Draws the active tab inside the main content viewport. The size is the
    /// stable content box after the shell's horizontal inset and scrollbar
    /// gutter have been removed.
    /// </summary>
    public Action<Vector2, Vector2>? DrawContent; // (origin, size) — already scaled

    /// <summary>The active pane consumes the canonical Page composition,
    /// which owns the content inset and extent bookkeeping.</summary>
    public bool ContentUsesPage;

    /// <summary>
    /// The pane owns its internal scrolling and needs the shell viewport to
    /// remain fixed. Pose uses this for fixed mode tabs and footer chrome.
    /// </summary>
    public bool ContentOwnsViewport;

    /// <summary>Sidebar width (resizable, M11: 220–400px). Unscaled px.</summary>
    public float SidebarWidthPx = 280f;
    public Action<float>? OnSidebarResize;

    /// <summary>Inspector rail (approved M2): drawn when set — 280px right column,
    /// continuous surface from the titlebar's tb-right cell to the window bottom.</summary>
    public Action<Vector2, Vector2>? DrawRail;  // (origin, size)

    /// <summary>Collapse-to-titlebar (user spec): only the 48px strip renders.</summary>
    public bool Collapsed;
    public Action<bool>? OnCollapse;

    public Action<int>? OnTab;
    public Action<int>? OnGizmoOperation;
    public Action<int>? OnGizmoSpace;
    public Action<int>? OnRotationPivot;
    public Action<int>? OnSymmetry;
    public Action<bool>? OnPhysics;
    public Action? OnUndo, OnRedo, OnSpawn, OnSettings, OnHideUi, OnPopOut, OnProject;
    public Action<bool>? OnSkeletonOverlay;
    public Action<ShellSidebarRow>? OnRowClicked;
    public Action<ShellSidebarRow>? OnRowContextMenu;
    public Action<ShellSidebarRow>? OnRowExpandToggled;
    public Action<ShellSidebarRow>? OnActorTarget;
    public Action<ShellSidebarRow>? OnActorVisibility;
    public Action<ShellSidebarRow>? OnActorPause;
    public Action<ShellSidebarRow>? OnOverlayVisibility;
    public Func<IReadOnlyList<Domain.Identity.BoneId>, bool>?
        IsOverlayVisible;
    public Action<int>? OnSectionPlus;
}

/// <summary>
/// M1 "Studio" shell — pixel transcription of the approved
/// docs/mockups/m1-mainwindow-shell.html (stage A): 48px titlebar
/// (280px surface-1 left cell + bg-app center), 280px sidebar with 26px rows,
/// tree guides and 26px statusbar, main region with 44px toolbar
/// (selection-typed tabs · divider · scene tabs, crumb, pop-out) and the
/// scrollable inspector content.
/// </summary>
public static class AppShellView
{
    private static Vector4 BgApp =>
        Crystarium.FloatingSurface.FillColor;
    private static Vector4 Surface1 =>
        Crystarium.ActiveTheme.SurfaceRaised;
    private static Vector4 TextPrimary =>
        Crystarium.ActiveTheme.Chrome.Text;
    private static Vector4 TextTertiary =>
        Crystarium.ActiveTheme.TextMuted;
    private static Vector4 BorderPrimary =>
        Crystarium.ActiveTheme.Chrome.ControlBorder;
    private static Vector4 BorderSecondary =>
        Crystarium.ActiveTheme.FormSeparator;
    private static Vector4 SurfaceHover =>
        Crystarium.ActiveTheme.Chrome.ControlFill;
    private static Vector4 SurfaceActive =>
        Crystarium.ActiveTheme.Chrome.WeakOverlay;
    private static Vector4 Success =>
        Crystarium.ActiveTheme.Success;
    // One inline axis editor may be active at a time. This belongs to the
    // view because the edit surface is an AppShell primitive, not entity state.

    public static float TitlebarHeight => Crystarium.ActiveTheme.Shell.TitlebarHeight;
    public static float SidebarWidth => Crystarium.ActiveTheme.Shell.SidebarDefaultWidth;
    public static float RowHeight => Crystarium.ActiveTheme.Controls.ListRowHeight;
    public static float ToolbarHeight => Crystarium.ActiveTheme.Shell.ToolbarHeight;
    public static float StatusbarHeight => Crystarium.ActiveTheme.Shell.StatusbarHeight;
    public static float ScrollbarWidth => Crystarium.ActiveTheme.Scrollbar.GutterWidth;
    public static float ScrollbarRadius => Crystarium.ActiveTheme.Scrollbar.Radius;
    private static float SidebarHorizontalPadding => Crystarium.ActiveTheme.Page.Inset;
    public static float MainHorizontalPadding => Crystarium.ActiveTheme.Page.Inset;

    public static void Draw(AppShellViewModel vm, Vector2 origin, Vector2 size)
    {
        float s = ImGuiHelpers.GlobalScale;
        var min = origin;
        var max = origin + size;
        var dl = ImGui.GetWindowDrawList();
        var shellOwner = Interactive.BeginOwner(
            "poser-main-shell",
            InteractionLayer.Window,
            min,
            max);
        try
        {

        // One shell-level blur; child panels only add translucent fills.
        Crystarium.FloatingSurface.PrependShellBlur(
            dl, min, max, Crystarium.ActiveTheme.Radii.Window * s);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BgApp)), 10f * s);
        DrawTitlebar(vm, min, max, s, dl);

        if (vm.Collapsed)
        {
            DrawOuterGlassBorder(min, max, s);
            return; // titlebar strip only
        }

        float bodyTop = min.Y + TitlebarHeight * s;
        float railW = vm.DrawRail != null ? RailWidth * s : 0f;
        float sbw = vm.SidebarWidthPx * s;
        DrawSidebar(vm, new Vector2(min.X, bodyTop), new Vector2(min.X + sbw, max.Y), s, dl);
        DrawMain(vm, new Vector2(min.X + sbw, bodyTop), new Vector2(max.X - railW, max.Y), s, dl);

        // M11: resizable sidebar — 6px col-resize strip on its right edge
        ImGui.SetCursorScreenPos(new Vector2(min.X + sbw - 3f * s, bodyTop));
        ImGui.InvisibleButton("##sidebar-resize", new Vector2(6f * s, max.Y - bodyTop));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        if (ImGui.IsItemActive() && ImGui.GetIO().MouseDelta.X != 0f)
            vm.OnSidebarResize?.Invoke(Math.Clamp(
                vm.SidebarWidthPx + ImGui.GetIO().MouseDelta.X / s,
                Crystarium.ActiveTheme.Shell.SidebarMinimumWidth,
                Crystarium.ActiveTheme.Shell.SidebarMaximumWidth));

        if (vm.DrawRail != null)
        {
            // rail chassis: surface-1 + border-left, continuous with tb-right
            var railMin = new Vector2(max.X - railW, min.Y + TitlebarHeight * s);
            dl.AddRectFilled(railMin, max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Surface1)), 10f * s, ImDrawFlags.RoundCornersBottomRight);
            dl.AddRectFilled(railMin, new Vector2(railMin.X + 1f * s, max.Y), ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderPrimary)));
            // The child reaches the outer-right glass edge; its content keeps
            // 12px left padding and a fixed 12px right composite gutter:
            // 0px content gap + 12px scrollbar.
            var railChildOrigin = railMin + new Vector2(0f, 12f) * s;
            ImGui.SetCursorScreenPos(railChildOrigin);
            Crystarium.ScrollRegion(
                "##shell-rail",
                railW / s - 1f,
                (max.Y - railMin.Y) / s - 24f,
                region =>
                {
                    var railContentOrigin =
                        ImGui.GetCursorScreenPos()
                        + new Vector2(
                            Crystarium.ActiveTheme.Page.Inset * s,
                            0f);
                    ImGui.SetCursorScreenPos(railContentOrigin);
                    vm.DrawRail(
                        railContentOrigin,
                        new Vector2(
                            region.ContentWidth * s
                                - Crystarium.ActiveTheme.Page.Inset * s,
                            max.Y - railMin.Y - 24f * s));
                });
        }

        // Panel fills are intentionally drawn after the base chassis. Repaint
        // its asymmetric glass edge last so sidebar/rail surfaces cannot hide
        // the left, right, or bottom glass borders.
        DrawOuterGlassBorder(min, max, s);
        }
        finally
        {
            Interactive.EndOwner(shellOwner);
        }
    }

    public static float RailWidth => Crystarium.ActiveTheme.Shell.RailWidth;

    // ── titlebar ─────────────────────────────────────────────────────────

    private static void DrawTitlebar(AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        float h = TitlebarHeight * s;
        var leftMax = new Vector2(min.X + vm.SidebarWidthPx * s, min.Y + h);
        float actionSize =
            Crystarium.ActiveTheme.Controls.ShellIconAction;
        float actionGap = Crystarium.ActiveTheme.Page.ActionGap;
        float compactGap = Crystarium.ActiveTheme.Spacing.Two;
        float segmentHeight =
            Crystarium.ActiveTheme.Controls.NavigationHeight;

        if (vm.Collapsed)
        {
            // Collapsed means one continuous titlebar, not an empty window with
            // a surviving sidebar cell. Paint one glass strip with no divider.
            var barMax = new Vector2(max.X, min.Y + h);
            dl.AddRectFilled(min, barMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Crystarium.FloatingSurface.FillColor)),
                10f * s);
        }
        else
        {
            // left cell: translucent fill over the one shell-level blur.
            dl.AddRectFilled(min, leftMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Crystarium.FloatingSurface.FillColor)),
                10f * s, ImDrawFlags.RoundCornersTopLeft);
            dl.AddRectFilled(new Vector2(leftMax.X - 1f * s, min.Y), leftMax,
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderPrimary)));
        }

        // app name + GPose pill
        ViewText.Label(min + new Vector2(14f, (TitlebarHeight - 16f) / 2f) * s, "Poser", 13f, FontWeight.SemiBold, TextPrimary);
        float appW = ViewText.Measure("Poser", 13f, FontWeight.SemiBold);
        if (vm.GPoseActive)
        {
            var pillMin = new Vector2(min.X + 14f * s + appW + 8f * s, min.Y + (h - 20f * s) / 2f);
            float pillTextW = ViewText.Measure("GPose", 11f, FontWeight.Medium);
            var pillMax = pillMin + new Vector2(8f * s + 7f * s + 6f * s + pillTextW + 8f * s, 20f * s);
            dl.AddRectFilled(pillMin, pillMax, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Success with { W = 0.12f })), 10f * s);
            var dotC = new Vector2(pillMin.X + 8f * s + 3.5f * s, pillMin.Y + 10f * s);
            dl.AddCircleFilled(dotC, 3.5f * s, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Success)));
            ViewText.Label(new Vector2(dotC.X + 3.5f * s + 6f * s, pillMin.Y + 4f * s), "GPose", 11f, FontWeight.Medium, Success);
        }

        // undo/redo sit right-aligned in the sidebar's title cell, directly
        // before the spawn button.
        float undoY = min.Y + (h - actionSize * s) / 2f;
        float titleRight = leftMax.X - 8f * s;
        if (vm.ShowSpawn)
        {
            PlaceIconButton(
                dl,
                new Vector2(titleRight - actionSize * s, undoY),
                TablerIcon.Plus,
                false,
                s,
                vm.OnSpawn,
                help: "Add an actor to the scene");
            titleRight -= (actionSize + compactGap) * s;
        }
        PlaceIconButton(dl, new Vector2(titleRight - actionSize * s, undoY), TablerIcon.ArrowBackUp, false, s, vm.OnRedo,
            dimmed: !vm.CanRedo, flipX: true,
            help: vm.CanRedo ? "Reapply the change you undid" : "Nothing to redo",
            helpShortcut: PoserKeybinds.Effective("Redo"));
        PlaceIconButton(dl, new Vector2(titleRight - (actionSize + compactGap + actionSize) * s, undoY), TablerIcon.ArrowBackUp, false, s,
            vm.OnUndo, dimmed: !vm.CanUndo,
            help: vm.CanUndo ? "Take back the last pose edit" : "Nothing to undo",
            helpShortcut: PoserKeybinds.Effective("Undo"));

        // center strip
        float x = leftMax.X + 12f * s;
        float cy = min.Y + (h - actionSize * s) / 2f;
        if (vm.ShowProject)
        {
            PlaceIconButton(dl, new Vector2(x, cy), TablerIcon.Folder, false, s, vm.OnProject,
                help: "Open the scene project browser");
            x += (actionSize + actionGap) * s;
        }

        // gizmo op seg (icon tabs) + space seg
        x = PlaceIconSegments(new Vector2(x, min.Y + (h - segmentHeight * s) / 2f),
            new[] { TablerIcon.ArrowsMove, TablerIcon.Rotate, TablerIcon.ArrowsDiagonal,
                TablerIcon.ArrowsMaximize },
            vm.GizmoOperation, s, i => vm.OnGizmoOperation?.Invoke(i),
            itemHelp: i => i switch
            {
                0 => "Move the selection",
                1 => "Rotate the selection",
                2 => "Scale the selection",
                _ => "Move, rotate, or scale with the universal gizmo",
            });
        x += actionGap * s;
        x = PlaceTextSegments(new Vector2(x, min.Y + (h - segmentHeight * s) / 2f),
            new[] { "Local", "World" }, vm.GizmoSpace, s,
            i => vm.OnGizmoSpace?.Invoke(i),
            itemHelp: i => i == 0
                ? "Use the selected target's local axes"
                : "Use world-space axes");
        // Pivot keeps a permanent slot so tool/selection changes cannot move
        // the rest of the toolbar. Both choices disable when pivot is
        // inapplicable; Parent additionally needs a live parent bone.
        x += actionGap * s;
        x = PlaceTextSegments(new Vector2(x, min.Y + (h - segmentHeight * s) / 2f),
            new[] { "Self", "Parent" }, vm.RotationPivot, s,
            i => vm.OnRotationPivot?.Invoke(i),
            itemDisabled: i => !vm.RotationPivotEnabled
                || (i == 1 && !vm.RotationPivotParentAvailable),
            itemHelp: i => i == 0
                ? "Rotate each selected target around itself"
                : "Rotate around the selected bone's parent pivot");
        x += actionGap * s;
        x = PlaceTextSegments(new Vector2(x, min.Y + (h - segmentHeight * s) / 2f),
            new[] { "Off", "Link", "Mirror" }, vm.SymmetryMode, s,
            i => vm.OnSymmetry?.Invoke(i),
            itemHelp: i => i switch
            {
                0 => "Edit only the current selection",
                1 => "Apply the same edit to linked selections",
                _ => "Apply mirrored edits across left and right bones",
            });

        // tb-right cell: when the rail is present, the right cluster sits on a
        // surface-1 cell continuous with the rail below (shell rule)
        if (vm.DrawRail != null && !vm.Collapsed)
        {
            var cellMin = new Vector2(max.X - RailWidth * s, min.Y);
            dl.AddRectFilled(cellMin, new Vector2(max.X, min.Y + h), ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Surface1)), 10f * s, ImDrawFlags.RoundCornersTopRight);
            dl.AddRectFilled(cellMin, new Vector2(cellMin.X + 1f * s, min.Y + h), ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderPrimary)));
        }

        // right cluster (rightmost = collapse chevron, then close X — user spec)
        float rx = max.X - 12f * s - actionSize * s;
        PlaceNamedIconButton(new Vector2(rx, cy), vm.Collapsed ? "chevron-down" : "chevron-up", false, s,
            () => vm.OnCollapse?.Invoke(!vm.Collapsed),
            help: vm.Collapsed ? "Expand the window" : "Collapse to the title bar");
        rx -= (actionSize + actionGap) * s;
        PlaceNamedIconButton(new Vector2(rx, cy), "x", false, s, vm.OnHideUi,
            help: "Hide the Poser window"); // close window
        rx -= (actionSize + actionGap) * s;
        PlaceIconButton(dl, new Vector2(rx, cy), TablerIcon.Settings, false, s, vm.OnSettings,
            help: "Open Poser settings");
        rx -= (actionSize + actionGap) * s;
        PlaceIconButton(dl, new Vector2(rx, cy), TablerIcon.Armature, vm.SkeletonOverlayOn, s,
            () => vm.OnSkeletonOverlay?.Invoke(!vm.SkeletonOverlayOn),
            help: "Toggle the skeleton overlay in the viewport");
    }

    // ── sidebar ──────────────────────────────────────────────────────────

    private static void DrawSidebar(AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        // Sidebar fill composes over the one shell-level blur.
        dl.AddRectFilled(
            min,
            max,
            ImGui.ColorConvertFloat4ToU32(
                ColorEx.ApplyAlpha(Crystarium.FloatingSurface.FillColor)),
            10f * s,
            ImDrawFlags.RoundCornersBottomLeft);
        dl.AddRectFilled(new Vector2(max.X - 1f * s, min.Y), max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderPrimary)));

        float statusTop = max.Y - StatusbarHeight * s;

        // Search stays outside the scroll child so a large skeleton cannot
        // push its primary navigation affordance out of view.
        ImGui.SetCursorScreenPos(min + new Vector2(SidebarHorizontalPadding, 6f) * s);
        Crystarium.FilterPill(
            "##sidebar-search",
            vm.SidebarSearch,
            next => vm.SidebarSearch = next,
            "Filter scene...",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(
                    (max.X - min.X) / s
                    - SidebarHorizontalPadding * 2f - 1f),
            });


        // Scroll child spans to the sidebar border so the scrollbar sits AT the
        // right edge; rows take the avail width (a gutter, never an overlap).
        float treeTop = min.Y + 38f * s;
        ImGui.SetCursorScreenPos(new Vector2(min.X + SidebarHorizontalPadding * s, treeTop));
        float childW = max.X - 1f * s - (min.X + SidebarHorizontalPadding * s);
        Crystarium.ScrollRegion(
            "##sidebar-tree",
            childW / s,
            (statusTop - treeTop) / s
                - Crystarium.ActiveTheme.Spacing.One,
            region =>
            {
                var cdl = ImGui.GetWindowDrawList();
                float innerW = region.ContentWidth * s;
                var cursor = ImGui.GetCursorScreenPos();
                var treeStart = cursor;
                int sectionIndex = 0;
                foreach (var section in vm.Sections)
                {
                    if (sectionIndex > 0)
                        cursor.Y +=
                            Crystarium.ActiveTheme.Spacing.Four * s;
                    ViewText.Label(
                        cursor + new Vector2(
                            Crystarium.ActiveTheme.Spacing.Two,
                            Crystarium.ActiveTheme.Spacing.Two) * s,
                        section.Title,
                        Crystarium.ActiveTheme.Typography.LabelSize,
                        FontWeight.Medium,
                        TextTertiary);
                    if (section.ShowPlus)
                    {
                        ImGui.SetCursorScreenPos(new Vector2(
                            cursor.X + innerW
                                - Crystarium.ActiveTheme.Floating
                                    .CloseActionSize * s,
                            cursor.Y
                                + Crystarium.ActiveTheme.Spacing.Three * s));
                        int capture = sectionIndex;
                        Crystarium.IconButton(
                            TablerIcon.Plus,
                            () => vm.OnSectionPlus?.Invoke(capture),
                            ControlStyle.Square(
                                Crystarium.ActiveTheme.Controls.SwitchHeight)
                                with { Bare = true },
                            id: $"##sbp-{sectionIndex}");
                    }
                    cursor.Y +=
                        Crystarium.ActiveTheme.Floating.CloseActionSize * s;

                    int rowIndex = 0;
                    foreach (var row in section.Rows)
                    {
                        DrawRow(
                            vm,
                            row,
                            cursor,
                            innerW,
                            s,
                            cdl,
                            $"{sectionIndex}-{rowIndex++}");
                        cursor.Y += RowHeight * s;
                    }
                    sectionIndex++;
                }
                ImGui.SetCursorScreenPos(treeStart);
                ImGui.Dummy(
                    new Vector2(innerW, cursor.Y - treeStart.Y));
            });

        // statusbar: status information only (actor count, FPS)
        dl.AddRectFilled(new Vector2(min.X, statusTop), new Vector2(max.X - 1f * s, statusTop + 1f * s),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderSecondary)));
        var dotCenter = new Vector2(min.X + 10f * s + 3.5f * s, statusTop + StatusbarHeight * s / 2f);
        dl.AddCircleFilled(dotCenter, 3.5f * s, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Success)));
        ViewText.Label(new Vector2(dotCenter.X + 3.5f * s + 8f * s, statusTop + 7f * s), vm.StatusLeft, 11f, FontWeight.Regular, TextTertiary, mono: true);
        ViewText.Label(new Vector2(max.X - 10f * s - ViewText.Measure(vm.StatusRight, 11f, mono: true), statusTop + 7f * s),
            vm.StatusRight, 11f, FontWeight.Regular, TextTertiary, mono: true);
    }

    private static void DrawRow(AppShellViewModel vm, ShellSidebarRow row, Vector2 cursor, float innerW, float s, ImDrawListPtr dl, string id)
    {
        const float Indent = 20f;
        const float RootIconCenter = 24f; // 16px expander slot + half of the 16px actor icon
        const float LabelOffsetFromGuide = 14f;
        int d = row.Depth;

        float GuideX(int depth) => cursor.X + (RootIconCenter + (depth - 1) * Indent) * s;

        float actionReserve = row.ActorActions
            ? 66f * s
            : row.OverlayBones != null
                ? 22f * s
                : 0f;
        float bodyWidth = MathF.Max(1f, innerW - actionReserve);
        // The body and action strip are disjoint hit regions. A compact action
        // can therefore never select or disclose its row.
        ImGui.SetCursorScreenPos(cursor);
        var hit = Interactive.Reserve(
            $"##sbr-{id}", new Vector2(bodyWidth, RowHeight * s), disabled: false);

        float arrowMinX, arrowMaxX;
        if (d == 0) { arrowMinX = cursor.X; arrowMaxX = cursor.X + 18f * s; }
        else
        {
            float gx = GuideX(d);
            arrowMinX = gx - 8f * s;
            arrowMaxX = gx + 10f * s;
        }
        bool overArrow = row.HasChildren && !row.ExpanderDisabled && hit.Hovered
            && ImGui.GetMousePos().X >= arrowMinX && ImGui.GetMousePos().X <= arrowMaxX;

        // Nested pills start after the shared 8.5px branch arm and retain 4px
        // padding before the label. Connector ink never runs under selection.
        float pillMinX = d > 0 ? GuideX(d) + 10f * s : cursor.X + 1f * s;
        var pillMin = new Vector2(pillMinX, cursor.Y);
        var pillMax = new Vector2(cursor.X + innerW, cursor.Y + (RowHeight - 1f) * s);
        if (row.Active)
            dl.AddRectFilled(pillMin, pillMax, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(SurfaceActive)), 5f * s);
        else if (hit.Hovered)
            dl.AddRectFilled(pillMin, pillMax, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(SurfaceHover)), 5f * s);

        uint guide = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(TextTertiary));

        // Every guide column is derived from the actor icon's center. This keeps
        // the first trunk directly beneath that icon and all descendant trunks,
        // including terminal L branches, on the same 20px grid.
        if (d > 0 && row.TreeLines != null)
        {
            for (int a = 1; a < d && a < row.TreeLines.Length; a++)
            {
                if (!row.TreeLines[a]) continue;
                float gx = GuideX(a);
                DrawGuideVertical(dl, gx, cursor.Y, cursor.Y + RowHeight * s, guide, s);
            }
        }

        // Branch connector: T / hard-L / arrow-cutout.
        if (d > 0)
        {
            float gx = GuideX(d);
            float X(float v) => gx + v * s;
            float Y(float v) => cursor.Y + v * s;

            if (row.HasChildren)
            {
                // Cutout: line gap Y=9..17 under the triangle.
                DrawGuideVertical(dl, X(0f), Y(0f), Y(9f), guide, s);
                if (!row.IsLastChild)
                    DrawGuideVertical(dl, X(0f), Y(17f), Y(26f), guide, s);
                DrawGuideHorizontal(dl, X(4.5f), X(8.5f), Y(13f), guide, s);
            }
            else if (row.IsLastChild)
            {
                // Crisp hard L with edge-joined legs and no heavy stroked corner.
                DrawGuideElbow(dl, X(0f), Y(0f), X(8.5f), Y(13f), guide, s);
            }
            else
            {
                DrawGuideVertical(dl, X(0f), Y(0f), Y(26f), guide, s);
                DrawGuideHorizontal(dl, X(0.5f), X(8.5f), Y(13f), guide, s);
            }

            // shared disclosure affordance overlapping the cutout gap
            if (row.HasChildren)
                DrawDisclosureChevron(dl, new Vector2(X(0f), Y(13f)), row, overArrow, s);
        }

        float x;
        if (d == 0)
        {
            // root rows keep the icon layout: 16px expander slot + 26px icon cell
            x = cursor.X;
            if (row.HasChildren)
                DrawDisclosureChevron(dl, new Vector2(x + 8f * s, cursor.Y + 13f * s), row, overArrow, s);
            x += 16f * s;
            ImGui.SetCursorScreenPos(new Vector2(x, cursor.Y + 5f * s));
            if (row.IconName != null)
                Crystarium.Icon(row.IconName, 16f * s, ColorEx.ApplyAlpha(TextPrimary with { W = 0.85f }));
            else
                Crystarium.Icon(row.Icon, 16f * s, ColorEx.ApplyAlpha(TextPrimary with { W = 0.85f }));
            x += 22f * s;
        }
        else
        {
            // Nested labels stay a fixed distance to the right of their guide.
            x = GuideX(d) + LabelOffsetFromGuide * s;
        }

        // clip the label short of the badge so long names never run under it
        float badgeReserve = actionReserve + 6f * s;
        ImGui.PushClipRect(new Vector2(cursor.X, cursor.Y),
            new Vector2(cursor.X + innerW - badgeReserve, cursor.Y + RowHeight * s), true);
        ViewText.Label(Crystarium.ActiveTheme.Optical.Snap(new Vector2(
                x, cursor.Y + 5f * s + Crystarium.ActiveTheme.Optical.SidebarText * s)),
            row.Label, 13f, FontWeight.Regular, TextPrimary);
        ImGui.PopClipRect();

        if (row.ActorActions)
        {
            float ax = cursor.X + innerW - actionReserve;
            DrawRowAction(
                $"##target-{id}", new Vector2(ax, cursor.Y + 3f * s),
                TablerIcon.Crosshair, false, s,
                () => vm.OnActorTarget?.Invoke(row),
                "Set game target");
            ax += 22f * s;
            DrawRowAction(
                $"##visible-{id}", new Vector2(ax, cursor.Y + 3f * s),
                TablerIcon.Eye,
                !row.ActorVisible, s,
                () => vm.OnActorVisibility?.Invoke(row),
                row.ActorVisible ? "Hide actor" : "Show actor");
            ax += 22f * s;
            DrawRowAction(
                $"##pause-{id}", new Vector2(ax, cursor.Y + 3f * s),
                TablerIcon.PlayerPlay,
                row.ActorPaused, s,
                () => vm.OnActorPause?.Invoke(row),
                row.ActorPaused ? "Resume animation" : "Pause animation");
        }
        else if (row.OverlayBones != null)
        {
            bool visible = vm.IsOverlayVisible?.Invoke(row.OverlayBones)
                ?? true;
            DrawRowAction(
                $"##overlay-{id}",
                new Vector2(cursor.X + innerW - 22f * s, cursor.Y + 3f * s),
                visible ? TablerIcon.Eye : TablerIcon.EyeOff,
                !visible, s,
                () => vm.OnOverlayVisibility?.Invoke(row),
                visible ? "Hide from skeleton overlay" : "Show in skeleton overlay");
        }

        if (hit.Clicked)
        {
            if (overArrow) vm.OnRowExpandToggled?.Invoke(row);
            else vm.OnRowClicked?.Invoke(row);
        }

        if (hit.Hovered && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
            vm.OnRowContextMenu?.Invoke(row);
    }

    /// <summary>
    /// The one disclosure affordance for actor and category rows: the compact
    /// filled triangle, visible in collapsed and expanded states,
    /// hover-emphasized over its 18px hit zone, faded and inert while the
    /// row's children are temporarily unavailable. NOTE: PBI-002 runtime
    /// round 1 specified Tabler chevrons here; the user explicitly requested
    /// the original triangle affordance back during the 2026-07-24 in-game
    /// session — this supersedes that clarification line.
    /// </summary>
    private static void DrawDisclosureChevron(ImDrawListPtr dl, Vector2 center, ShellSidebarRow row, bool hovered, float s)
    {
        float alpha = row.ExpanderDisabled ? 0.25f : hovered ? 1f : 0.7f;
        uint color = ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(TextPrimary with { W = alpha }));
        if (row.Expanded)
            dl.AddTriangleFilled(
                center + new Vector2(-3.5f, -2.5f) * s,
                center + new Vector2(3.5f, -2.5f) * s,
                center + new Vector2(0f, 2.5f) * s, color);
        else
            dl.AddTriangleFilled(
                center + new Vector2(-2.5f, -3.5f) * s,
                center + new Vector2(2.5f, 0f) * s,
                center + new Vector2(-2.5f, 3.5f) * s, color);
    }

    /// <summary>
    /// Filled guide segments meet edge-to-edge at row boundaries. Unlike separate
    /// anti-aliased line caps, they do not stack alpha at a shared endpoint.
    /// </summary>
    private static void DrawGuideVertical(
        ImDrawListPtr dl,
        float x,
        float y0,
        float y1,
        uint color,
        float scale)
    {
        float half = Math.Max(1f, scale) * 0.5f;
        dl.AddRectFilled(new Vector2(x - half, y0), new Vector2(x + half, y1), color);
    }

    private static void DrawGuideHorizontal(
        ImDrawListPtr dl,
        float x0,
        float x1,
        float y,
        uint color,
        float scale)
    {
        float half = Math.Max(1f, scale) * 0.5f;
        dl.AddRectFilled(new Vector2(x0, y - half), new Vector2(x1, y + half), color);
    }


    private static void DrawGuideElbow(
        ImDrawListPtr dl,
        float x,
        float y0,
        float x1,
        float y,
        uint color,
        float scale)
    {
        float half = Math.Max(1f, scale) * 0.5f;

        // The vertical leg owns the square corner. The horizontal leg begins at
        // its right edge, so the translucent geometry touches but never overlaps.
        dl.AddRectFilled(
            new Vector2(x - half, y0),
            new Vector2(x + half, y + half),
            color);
        dl.AddRectFilled(
            new Vector2(x + half, y - half),
            new Vector2(x1, y + half),
            color);
    }
    // ── main region ──────────────────────────────────────────────────────

    private static void DrawMain(AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        // toolbar 44px + bottom hairline
        float toolbarBottom = min.Y + ToolbarHeight * s;
        dl.AddRectFilled(new Vector2(min.X, toolbarBottom - 1f * s), new Vector2(max.X, toolbarBottom),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderSecondary)));

        // The tab strip is the SAME segmented pill every other mode
        // selector uses (Body/Face/Matrix/3D), not hand-drawn buttons.
        // alignFirstTabToCursor puts the first tab's LABEL on the content
        // inset — the pill's dark chrome is decoration, not padding, so it
        // hangs left of the alignment line exactly as the inspector's does.
        if (vm.Tabs.Count > 0)
        {
            var labels = new string[vm.Tabs.Count];
            int active = 0;
            for (int i = 0; i < vm.Tabs.Count; i++)
            {
                labels[i] = vm.Tabs[i].Label;
                if (vm.Tabs[i].Active)
                    active = i;
            }
            float segmentedHeightPx =
                Crystarium.ActiveTheme.Controls.NavigationHeight;
            ImGui.SetCursorScreenPos(new Vector2(
                min.X + MainHorizontalPadding * s,
                min.Y + (ToolbarHeight - segmentedHeightPx) / 2f * s));
            Crystarium.SegmentedControl(
                "##shell-tabs",
                labels,
                active,
                chosen => vm.OnTab?.Invoke(chosen),
                alignFirstTabToCursor: true);
        }

        // Actor physics occupies one stable right-aligned slot on every
        // workspace tab. Tab changes never replace it with selection text or
        // move the control.
        float rx = max.X - MainHorizontalPadding * s;
        if (vm.ShowPopOut)
        {
            float actionSize =
                Crystarium.ActiveTheme.Controls.ShellIconAction;
            rx -= actionSize * s;
            PlaceIconButton(dl, new Vector2(
                    rx,
                    min.Y + (ToolbarHeight - actionSize) / 2f * s),
                TablerIcon.ExternalLink, false, s, vm.OnPopOut);
            rx -= Crystarium.ActiveTheme.Page.ActionGap * s;
        }
        Crystarium.ActionBar(
            "shell-workspace-actions",
            min,
            new Vector2(rx - min.X, ToolbarHeight * s),
            _ => { },
            right =>
            {
                right.Switch(
                    "Physics",
                    vm.PhysicsOn,
                    next => vm.OnPhysics?.Invoke(next),
                    vm.PhysicsAvailable
                        ? "Enable or disable physics for the selected actor"
                        : "Select an actor or bone to control physics",
                    disabled: !vm.PhysicsAvailable);
            },
            ActionBarSeparator.None);

        // Toolbar and content share one 12px horizontal inset. The viewport
        // still reaches the outer-right glass edge, and content width always
        // excludes the 12px scrollbar gutter so overflow cannot cause reflow.
        // ONE content origin for every tab. The old 4px gap applied only
        // to tabs that scroll in this child, so the same empty-state line
        // sat 4px lower on one tab than the other and jumped on switch.
        // Panes own their breathing room; the shell owns the origin.
        var childOrigin = new Vector2(min.X, toolbarBottom);
        var childSize = new Vector2(
            max.X - min.X - 1f * s,
            max.Y - toolbarBottom - 1f * s);
        // The inset is measured from the CHILD, not the panel: the child is
        // 1px narrower than the panel (the glass border pixel), and the
        // scrollbar hugs the child's right edge. Deriving content width
        // from the panel put the content's right edge 1px INTO the
        // scrollbar band, so flush-right controls overlapped the thumb.
        float contentWidth = childSize.X
            - MainHorizontalPadding * 2f * s
            - ScrollbarWidth * s;
        ImGui.SetCursorScreenPos(childOrigin);
        if (vm.ContentOwnsViewport)
        {
            ImGui.PushStyleVar(
                ImGuiStyleVar.WindowPadding,
                Vector2.Zero);
            if (ImGui.BeginChild(
                    "##shell-content-viewport",
                    childSize,
                    false,
                    ImGuiWindowFlags.NoScrollbar
                    | ImGuiWindowFlags.NoScrollWithMouse))
            {
                var childCursor = ImGui.GetCursorScreenPos();
                float contentInset =
                    MainHorizontalPadding * s;
                var contentOrigin = childCursor
                    + new Vector2(contentInset);
                vm.DrawContent?.Invoke(
                    contentOrigin,
                    new Vector2(
                        contentWidth,
                        MathF.Max(
                            0f,
                            ImGui.GetContentRegionAvail().Y
                                - contentInset)));
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
            return;
        }
        Crystarium.ScrollRegion(
            "##shell-content",
            childSize.X / s,
            childSize.Y / s,
            region =>
            {
                var childCursor = ImGui.GetCursorScreenPos();
                if (vm.ContentUsesPage)
                {
                    vm.DrawContent?.Invoke(
                        childCursor,
                        new Vector2(
                            region.ContentWidth * s,
                            childSize.Y));
                    return;
                }
                var contentOrigin = childCursor
                    + new Vector2(MainHorizontalPadding * s, 0f);
                ImGui.SetCursorScreenPos(contentOrigin);
                vm.DrawContent?.Invoke(
                    contentOrigin,
                    new Vector2(
                        MathF.Max(
                            0f,
                            region.ContentWidth * s
                                - MainHorizontalPadding * 2f * s),
                        childSize.Y));
            });
    }

    private static void DrawOuterGlassBorder(Vector2 min, Vector2 max, float s)
    {
        Crystarium.FloatingSurface.DrawBorder(min, max, 10f * s);
    }

    /// <summary>Cancels an in-progress numeric axis edit, for example when selection changes.</summary>
    public static void CancelAxisEdit()
    {
        Crystarium.CancelAxisEdit();
    }

    private static void DrawRowAction(
        string id,
        Vector2 pos,
        TablerIcon icon,
        bool inactive,
        float scale,
        Action action,
        string help)
    {
        ImGui.SetCursorScreenPos(pos);
        Crystarium.IconButton(
            icon,
            action,
            ControlStyle.Square(
                Crystarium.ActiveTheme.Controls.SwitchHeight) with
            {
                Bare = true,
                Slashed = inactive,
            },
            help: help,
            id: id);
    }

    // ── shared small controls ────────────────────────────────────────────

    private static void PlaceNamedIconButton(
        Vector2 position,
        string icon,
        bool selected,
        float scale,
        Action? onClick,
        bool dimmed = false,
        string? help = null,
        string? helpShortcut = null)
    {
        ImGui.SetCursorScreenPos(position);
        Crystarium.IconButton(
            icon,
            onClick,
            ControlStyle.Square(
                Crystarium.ActiveTheme.Controls.ShellIconAction) with
            {
                Bare = true,
                Selected = selected,
            },
            dimmed,
            CombinedHelp(help, helpShortcut),
            $"##shell-icon-{icon}-{position.X:0}-{position.Y:0}");
    }

    private static void PlaceIconButton(
        ImDrawListPtr _,
        Vector2 position,
        TablerIcon icon,
        bool selected,
        float scale,
        Action? onClick,
        bool dimmed = false,
        bool flipX = false,
        string? help = null,
        string? helpShortcut = null)
    {
        ImGui.SetCursorScreenPos(position);
        Crystarium.IconButton(
            icon,
            onClick,
            ControlStyle.Square(
                Crystarium.ActiveTheme.Controls.ShellIconAction) with
            {
                Bare = true,
                Selected = selected,
            },
            dimmed,
            CombinedHelp(help, helpShortcut),
            $"##shell-icon-{icon}-{position.X:0}-{position.Y:0}",
            flipX);
    }

    private static float PlaceIconSegments(
        Vector2 position,
        TablerIcon[] icons,
        int selected,
        float scale,
        Action<int> onSelect,
        Func<int, string?>? itemHelp = null)
    {
        ImGui.SetCursorScreenPos(position);
        var size = Crystarium.MeasureSegmentedControl(icons);
        Crystarium.SegmentedControl(
            $"##shell-icon-segments-{position.X:0}",
            icons,
            selected,
            onSelect,
            itemHelp: itemHelp);
        return position.X + size.X;
    }

    private static float PlaceTextSegments(
        Vector2 position,
        string[] labels,
        int selected,
        float scale,
        Action<int> onSelect,
        Func<int, bool>? itemDisabled = null,
        Func<int, string?>? itemHelp = null)
    {
        ImGui.SetCursorScreenPos(position);
        var size = Crystarium.MeasureSegmentedControl(labels);
        Crystarium.SegmentedControl(
            $"##shell-text-segments-{position.X:0}",
            labels,
            selected,
            onSelect,
            itemDisabled: itemDisabled,
            itemHelp: itemHelp);
        return position.X + size.X;
    }

    private static string? CombinedHelp(
        string? help,
        string? shortcut) =>
        shortcut == null
            ? help
            : $"{help} · {shortcut}";
}
