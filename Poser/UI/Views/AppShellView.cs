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
    public string CrumbPrefix = "";   // tertiary part ("Midona Rhel · ")
    public string CrumbBold = "";     // primary part ("j_te_l")

    public int GizmoOperation;        // 0 translate, 1 rotate, 2 scale, 3 universal
    public int GizmoSpace;            // 0 local, 1 world
    public int RotationPivot;         // 0 self, 1 parent
    public bool ShowRotationPivot;    // Rotate tool + bone selection only
    public bool RotationPivotParentAvailable;
    public int SymmetryMode;          // 0 off, 1 link, 2 mirror
    public bool LinkedOn;
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
    public Action<bool>? OnLinked;
    public Action? OnUndo, OnRedo, OnSpawn, OnSettings, OnHideUi, OnPopOut, OnProject, OnSelectTarget;
    public Action<bool>? OnSkeletonOverlay;
    public Action<ShellSidebarRow>? OnRowClicked;
    public Action<ShellSidebarRow>? OnRowContextMenu;
    public Action<ShellSidebarRow>? OnRowExpandToggled;
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
    // ── palette (picto tokens; sources cited in PictoStylesheet) ─────────
    private static readonly Vector4 BgApp          = new(24 / 255f, 25 / 255f, 27 / 255f, 1f);
    private static readonly Vector4 Surface1       = new(36 / 255f, 37 / 255f, 40 / 255f, 1f);
    private static readonly Vector4 Surface2       = new(42 / 255f, 42 / 255f, 46 / 255f, 1f);
    private static readonly Vector4 TextPrimary    = new(1f, 1f, 1f, 1f);
    private static readonly Vector4 TextSecondary  = new(1f, 1f, 1f, 0.72f);
    private static readonly Vector4 TextTertiary   = new(1f, 1f, 1f, 0.50f);
    private static readonly Vector4 BorderPrimary  = new(1f, 1f, 1f, 0.14f);
    private static readonly Vector4 BorderSecondary= new(1f, 1f, 1f, 0.08f);
    private static readonly Vector4 Black20        = new(0f, 0f, 0f, 0.20f);
    private static readonly Vector4 HoverOverlay   = new(1f, 1f, 1f, 0.06f);
    private static readonly Vector4 SubtleOverlay  = new(1f, 1f, 1f, 0.08f);
    private static readonly Vector4 ActiveOverlay  = new(1f, 1f, 1f, 0.10f);
    private static readonly Vector4 SurfaceHover   = new(1f, 1f, 1f, 0.05f);
    private static readonly Vector4 SurfaceActive  = new(1f, 1f, 1f, 0.09f);
    private static readonly Vector4 Success        = new(126 / 255f, 211 / 255f, 160 / 255f, 1f);
    private static Vector4 AxisX => Crystarium.ActiveTheme.Palette.AxisX;
    private static Vector4 AxisY => Crystarium.ActiveTheme.Palette.AxisY;
    private static Vector4 AxisZ => Crystarium.ActiveTheme.Palette.AxisZ;

    // One inline axis editor may be active at a time. This belongs to the
    // view because the edit surface is an AppShell primitive, not entity state.
    private static string? _axisEditId;
    private static float _axisEditValue;
    private static bool _axisEditNeedsFocus;

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

        // window chassis: bg-app fill + glass border trio, radius 10
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BgApp)), 10f * s);
        DrawTitlebar(vm, min, max, s, dl);

        if (vm.Collapsed)
        {
            DrawOuterGlassBorder(min, max, s);
            return; // titlebar strip only
        }

        // One scrollbar treatment for sidebar, main content, and inspector.
        // Transparent track + 12px gutter + 4px rounded thumb transcribes the
        // Picto global scrollbar used by the approved shell mockups.
        Crystarium.PushScrollbarStyle();

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
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            if (ImGui.BeginChild("##shell-rail", new Vector2(railW - 1f * s, max.Y - railMin.Y - 24f * s), false, ImGuiWindowFlags.None))
            {
                // Child cursor already includes -ScrollY; add only the fixed
                // horizontal inset so wheel scrolling remains functional.
                var railContentOrigin = ImGui.GetCursorScreenPos() + new Vector2(12f * s, 0f);
                ImGui.SetCursorScreenPos(railContentOrigin);
                vm.DrawRail(railContentOrigin, new Vector2(railW - 25f * s, max.Y - railMin.Y - 24f * s));
            }
            ImGui.EndChild();
            ImGui.PopStyleVar();
        }

        Crystarium.PopScrollbarStyle();

        // Panel fills are intentionally drawn after the base chassis. Repaint
        // its asymmetric glass edge last so sidebar/rail surfaces cannot hide
        // the left, right, or bottom glass borders.
        DrawOuterGlassBorder(min, max, s);
    }

    public static float RailWidth => Crystarium.ActiveTheme.Shell.RailWidth;

    // ── titlebar ─────────────────────────────────────────────────────────

    private static void DrawTitlebar(AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        float h = TitlebarHeight * s;
        var leftMax = new Vector2(min.X + vm.SidebarWidthPx * s, min.Y + h);

        if (vm.Collapsed)
        {
            // Collapsed means one continuous titlebar, not an empty window with
            // a surviving sidebar cell. Paint one glass strip with no divider.
            var barMax = new Vector2(max.X, min.Y + h);
            Crystarium.FloatingSurface.PrependShellBlur(dl, min, barMax, 10f * s);
            dl.AddRectFilled(min, barMax,
                ImGui.ColorConvertFloat4ToU32(
                    ColorEx.ApplyAlpha(Crystarium.FloatingSurface.FillColor)),
                10f * s);
        }
        else
        {
            // left cell: GLASS (M11) — overlay recipe: real backdrop blur + 92% fill
            Crystarium.FloatingSurface.PrependShellBlur(dl, min, leftMax, 10f * s);
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
        float undoY = min.Y + (h - 28f * s) / 2f;
        float titleRight = leftMax.X - 8f * s;
        if (vm.ShowSpawn)
        {
            IconButton(
                dl,
                new Vector2(titleRight - 28f * s, undoY),
                TablerIcon.Plus,
                false,
                s,
                vm.OnSpawn,
                help: "Add an actor to the scene");
            titleRight -= (28f + 4f) * s;
        }
        IconButton(dl, new Vector2(titleRight - 28f * s, undoY), TablerIcon.ArrowBackUp, false, s, vm.OnRedo,
            dimmed: !vm.CanRedo, flipX: true,
            help: vm.CanRedo ? "Reapply the change you undid" : "Nothing to redo",
            helpShortcut: PoserKeybinds.Effective("Redo"));
        IconButton(dl, new Vector2(titleRight - (28f + 4f + 28f) * s, undoY), TablerIcon.ArrowBackUp, false, s,
            vm.OnUndo, dimmed: !vm.CanUndo,
            help: vm.CanUndo ? "Take back the last pose edit" : "Nothing to undo",
            helpShortcut: PoserKeybinds.Effective("Undo"));

        // center strip
        float x = leftMax.X + 12f * s;
        float cy = min.Y + (h - 28f * s) / 2f;
        if (vm.ShowProject)
        {
            IconButton(dl, new Vector2(x, cy), TablerIcon.Folder, false, s, vm.OnProject,
                help: "Open the scene project browser");
            x += (28f + 10f) * s;
        }

        // gizmo op seg (icon tabs) + space seg
        x = IconSeg(dl, new Vector2(x, min.Y + (h - 30f * s) / 2f),
            new[] { TablerIcon.ArrowsMove, TablerIcon.Rotate, TablerIcon.ArrowsDiagonal,
                TablerIcon.ArrowsMaximize },
            vm.GizmoOperation, s, i => vm.OnGizmoOperation?.Invoke(i));
        x += 10f * s;
        x = TextSeg(dl, new Vector2(x, min.Y + (h - 30f * s) / 2f),
            new[] { "Local", "World" }, vm.GizmoSpace, s, i => vm.OnGizmoSpace?.Invoke(i));
        // pivot seg (Rotate + bone only; Parent disabled without a parent),
        // then symmetry and linked — kept in the toolbar so they stay
        // available while the window is collapsed.
        if (vm.ShowRotationPivot)
        {
            x += 10f * s;
            x = TextSeg(dl, new Vector2(x, min.Y + (h - 30f * s) / 2f),
                new[] { "Self", "Parent" }, vm.RotationPivot, s,
                i => vm.OnRotationPivot?.Invoke(i),
                itemDisabled: i => i == 1 && !vm.RotationPivotParentAvailable);
        }
        x += 10f * s;
        x = TextSeg(dl, new Vector2(x, min.Y + (h - 30f * s) / 2f),
            new[] { "Off", "Link", "Mirror" }, vm.SymmetryMode, s,
            i => vm.OnSymmetry?.Invoke(i));
        x += 10f * s;
        IconButtonNamed(dl, new Vector2(x, min.Y + (h - 28f * s) / 2f), "link",
            vm.LinkedOn, s, () => vm.OnLinked?.Invoke(!vm.LinkedOn),
            help: "Edit linked bones together — mirrored pairs move as one");
        x += (28f + 10f) * s;

        // tb-right cell: when the rail is present, the right cluster sits on a
        // surface-1 cell continuous with the rail below (shell rule)
        if (vm.DrawRail != null && !vm.Collapsed)
        {
            var cellMin = new Vector2(max.X - RailWidth * s, min.Y);
            dl.AddRectFilled(cellMin, new Vector2(max.X, min.Y + h), ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Surface1)), 10f * s, ImDrawFlags.RoundCornersTopRight);
            dl.AddRectFilled(cellMin, new Vector2(cellMin.X + 1f * s, min.Y + h), ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderPrimary)));
        }

        // right cluster (rightmost = collapse chevron, then close X — user spec)
        float rx = max.X - 12f * s - 28f * s;
        IconButtonNamed(dl, new Vector2(rx, cy), vm.Collapsed ? "chevron-down" : "chevron-up", false, s,
            () => vm.OnCollapse?.Invoke(!vm.Collapsed),
            help: vm.Collapsed ? "Expand the window" : "Collapse to the title bar");
        rx -= (28f + 10f) * s;
        IconButtonNamed(dl, new Vector2(rx, cy), "x", false, s, vm.OnHideUi,
            help: "Hide the Poser window"); // close window
        rx -= (28f + 10f) * s;
        IconButton(dl, new Vector2(rx, cy), TablerIcon.Settings, false, s, vm.OnSettings,
            help: "Open Poser settings");
        rx -= (28f + 10f) * s;
        IconButton(dl, new Vector2(rx, cy), TablerIcon.Armature, vm.SkeletonOverlayOn, s,
            () => vm.OnSkeletonOverlay?.Invoke(!vm.SkeletonOverlayOn),
            help: "Toggle the skeleton overlay in the viewport");
        rx -= (28f + 10f) * s;
        IconButton(dl, new Vector2(rx, cy), TablerIcon.UserCircle, false, s, vm.OnSelectTarget,
            help: "Select your in-game target");
    }

    // ── sidebar ──────────────────────────────────────────────────────────

    private static void DrawSidebar(AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        // M11 glass chassis — SAME recipe as the overlays: backdrop blur + 92% fill.
        Crystarium.FloatingSurface.PrependShellBlur(dl, min, max, 10f * s);
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
            ref vm.SidebarSearch,
            "Filter scene...",
            (max.X - min.X) / s - SidebarHorizontalPadding * 2f - 1f);


        // Scroll child spans to the sidebar border so the scrollbar sits AT the
        // right edge; rows take the avail width (a gutter, never an overlap).
        float treeTop = min.Y + 38f * s;
        ImGui.SetCursorScreenPos(new Vector2(min.X + SidebarHorizontalPadding * s, treeTop));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        float childW = max.X - 1f * s - (min.X + SidebarHorizontalPadding * s);
        bool treeOpen = ImGui.BeginChild("##sidebar-tree", new Vector2(childW, statusTop - treeTop - 2f * s), false, ImGuiWindowFlags.None);

        // Draw through the CHILD's list — the parent list is not clipped to the
        // scroll region and bled rows over the section header and statusbar.
        var cdl = ImGui.GetWindowDrawList();
        // CSS `scrollbar-gutter: stable` equivalent: reserve exactly 12px even
        // before overflow exists. Since the tree itself starts 12px from the
        // left edge, right scrollbar + content gap equals that left padding.
        float innerW = childW - ScrollbarWidth * s;
        var cursor = ImGui.GetCursorScreenPos();
        var treeStart = cursor;
        int sectionIndex = 0;

        if (treeOpen)
        foreach (var section in vm.Sections)
        {
            if (sectionIndex > 0)
                cursor.Y += 8f * s;

            // header 24px — no full-width reserve here: it would swallow the
            // plus button's click (overlapping reserves, first one wins).
            ViewText.Label(cursor + new Vector2(4f, 4f) * s, section.Title, 12f, FontWeight.Medium, TextTertiary);
            if (section.ShowPlus)
            {
                // Keep the action completely inside the content column, clear of
                // the stable scrollbar gutter and the child clip boundary.
                ImGui.SetCursorScreenPos(new Vector2(cursor.X + innerW - 24f * s, cursor.Y + 3f * s));
                var plusHit = Interactive.Reserve($"##sbp-{sectionIndex}", new Vector2(18f, 18f) * s, disabled: false);
                float plusIconSize = 14f * s;
                ImGui.SetCursorScreenPos(plusHit.ScreenMin +
                    (plusHit.ScreenMax - plusHit.ScreenMin - new Vector2(plusIconSize)) * 0.5f);
                Crystarium.Icon(TablerIcon.Plus, plusIconSize,
                    ColorEx.ApplyAlpha(plusHit.Hovered ? TextPrimary : TextTertiary));
                int capture = sectionIndex;
                if (plusHit.Clicked) vm.OnSectionPlus?.Invoke(capture);
            }
            cursor.Y += 24f * s;

            int rowIndex = 0;
            foreach (var row in section.Rows)
            {
                DrawRow(vm, row, cursor, innerW, s, cdl, $"{sectionIndex}-{rowIndex++}");
                cursor.Y += RowHeight * s;
            }
            sectionIndex++;
        }
        // register the content extent so the child can scroll
        ImGui.SetCursorScreenPos(treeStart);
        ImGui.Dummy(new Vector2(innerW, cursor.Y - treeStart.Y));
        ImGui.EndChild();
        ImGui.PopStyleVar();

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

        // ONE reserve per row; the expander is dispatched by mouse position on
        // click. (A second overlapping reserve never receives the click — the
        // row reserve submitted first wins it. Round-4 dead-arrow defect.)
        ImGui.SetCursorScreenPos(cursor);
        var hit = Interactive.Reserve($"##sbr-{id}", new Vector2(innerW, RowHeight * s), disabled: false);

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
        float badgeReserve = row.Count.Length > 0
            ? ViewText.Measure(row.Count, 11f, mono: true) + 16f * s
            : 6f * s;
        ImGui.PushClipRect(new Vector2(cursor.X, cursor.Y),
            new Vector2(cursor.X + innerW - badgeReserve, cursor.Y + RowHeight * s), true);
        ViewText.Label(Crystarium.ActiveTheme.Optical.Snap(new Vector2(
                x, cursor.Y + 5f * s + Crystarium.ActiveTheme.Optical.SidebarText * s)),
            row.Label, 13f, FontWeight.Regular, TextPrimary);
        ImGui.PopClipRect();

        if (row.Count.Length > 0)
            ViewText.Label(Crystarium.ActiveTheme.Optical.Snap(new Vector2(
                    cursor.X + innerW - 8f * s - ViewText.Measure(row.Count, 11f, mono: true),
                    cursor.Y + 7f * s + Crystarium.ActiveTheme.Optical.SidebarText * s)),
                row.Count, 11f, FontWeight.Regular, TextSecondary, mono: true);

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
            const float segmentedHeightPx = 30f;
            ImGui.SetCursorScreenPos(new Vector2(
                min.X + MainHorizontalPadding * s,
                min.Y + (ToolbarHeight - segmentedHeightPx) / 2f * s));
            int chosen = active;
            if (Crystarium.SegmentedControl("##shell-tabs", labels, ref chosen,
                    maxWidth: 0f, alignFirstTabToCursor: true) && chosen != active)
                vm.OnTab?.Invoke(chosen);
        }

        // crumb + optional pop-out. Only tabs with a faithful standalone view
        // expose this action; selection panes must not open the retired legacy
        // properties UI under a modern-shell icon.
        float rx = max.X - MainHorizontalPadding * s;
        if (vm.ShowPopOut)
        {
            rx -= 28f * s;
            IconButton(dl, new Vector2(rx, min.Y + (ToolbarHeight - 28f) / 2f * s),
                TablerIcon.ExternalLink, false, s, vm.OnPopOut);
        }
        if (vm.CrumbBold.Length > 0 || vm.CrumbPrefix.Length > 0)
        {
            float boldW = ViewText.Measure(vm.CrumbBold, 12f, FontWeight.Medium);
            float prefixW = ViewText.Measure(vm.CrumbPrefix, 12f);
            float cx = rx - 8f * s - boldW - prefixW;
            ViewText.Label(new Vector2(cx, min.Y + (ToolbarHeight - 12f) / 2f * s - 2f * s), vm.CrumbPrefix, 12f, FontWeight.Regular, TextTertiary);
            ViewText.Label(new Vector2(cx + prefixW, min.Y + (ToolbarHeight - 12f) / 2f * s - 2f * s), vm.CrumbBold, 12f, FontWeight.Medium, TextPrimary);
        }

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
        float contentWidth = childSize.X - MainHorizontalPadding * 2f * s;
        ImGui.SetCursorScreenPos(childOrigin);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        var childFlags = vm.ContentOwnsViewport
            ? ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse
            : ImGuiWindowFlags.None;
        if (ImGui.BeginChild("##shell-content", childSize, false, childFlags))
        {
            var childCursor = ImGui.GetCursorScreenPos();
            if (vm.ContentUsesPage)
            {
                vm.DrawContent?.Invoke(
                    childCursor,
                    new Vector2(childSize.X, ImGui.GetContentRegionAvail().Y));
            }
            else
            {
                // Preserve the child's vertical scroll transform; only the stable
                // horizontal content inset is manual.
                var contentOrigin = childCursor
                    + new Vector2(MainHorizontalPadding * s, 0f);
                var contentSize = new Vector2(
                    contentWidth,
                    ImGui.GetContentRegionAvail().Y);
                ImGui.SetCursorScreenPos(contentOrigin);
                vm.DrawContent?.Invoke(contentOrigin, contentSize);
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    private static void DrawOuterGlassBorder(Vector2 min, Vector2 max, float s)
    {
        Crystarium.FloatingSurface.DrawBorder(min, max, 10f * s);
    }

    // ── transform content helpers (M1 .prow/.scrub) ─────────────────────

    public static float ScrubRow(ImDrawListPtr dl, Vector2 cursor, float width, string label, string x, string y, string z, float s)
    {
        ViewText.Label(cursor + new Vector2(0f, 7f) * s, label, 12f, FontWeight.Regular, TextTertiary);
        float axesX = cursor.X + 94f * s;
        float axisW = (width - 94f * s - 12f * s) / 3f;
        DrawAxis(dl, new Vector2(axesX, cursor.Y), axisW, "X", x, AxisX, s);
        DrawAxis(dl, new Vector2(axesX + axisW + 6f * s, cursor.Y), axisW, "Y", y, AxisY, s);
        DrawAxis(dl, new Vector2(axesX + (axisW + 6f * s) * 2f, cursor.Y), axisW, "Z", z, AxisZ, s);
        return 26f * s + 8f * s;
    }

    /// <summary>
    /// Interactive variant of <see cref="ScrubRow"/>. Each axis well supports
    /// horizontal drag, modifier-aware mouse-wheel stepping, and double-click
    /// numeric entry. <paramref name="released"/> fires at every commit point.
    /// </summary>
    public static float ScrubRowDrag(ImDrawListPtr dl, Vector2 cursor, float width, string id, string label,
        ref Vector3 value, float perPixel, string fmt, float s, out bool changed, out bool released)
    {
        changed = false;
        released = false;
        ViewText.Label(cursor + new Vector2(0f, 7f) * s, label, 12f, FontWeight.Regular, TextTertiary);
        float axesX = cursor.X + 94f * s;
        float axisW = (width - 94f * s - 12f * s) / 3f;
        changed |= DragAxis(dl, new Vector2(axesX, cursor.Y), axisW, $"{id}-x", "X", ref value.X, AxisX, perPixel, fmt, s, ref released);
        changed |= DragAxis(dl, new Vector2(axesX + axisW + 6f * s, cursor.Y), axisW, $"{id}-y", "Y", ref value.Y, AxisY, perPixel, fmt, s, ref released);
        changed |= DragAxis(dl, new Vector2(axesX + (axisW + 6f * s) * 2f, cursor.Y), axisW, $"{id}-z", "Z", ref value.Z, AxisZ, perPixel, fmt, s, ref released);
        return 26f * s + 8f * s;
    }

    /// <summary>
    /// Single-value drag well: the SAME numeric cell as the transform axes
    /// (horizontal drag with Ctrl fine / Shift coarse, double-click to
    /// type, commit on release or Enter), for rows that pair a number with
    /// a slider — Ktisis' input-plus-slider line in this product's own
    /// vocabulary. No axis letter; neutral letter color.
    /// </summary>
    public static bool DragValueCell(ImDrawListPtr dl, Vector2 pos, float width, string id,
        ref float value, float perPixel, string fmt, float s, out bool released,
        bool disabled = false)
    {
        released = false;
        if (disabled)
        {
            DrawAxis(dl, pos, width, "",
                value.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture),
                TextTertiary, s);
            return false;
        }
        bool wasReleased = false;
        bool changed = DragAxis(dl, pos, width, id, "", ref value, TextTertiary,
            perPixel, fmt, s, ref wasReleased);
        released = wasReleased;
        return changed;
    }

    /// <summary>
    /// One axis well with its letter and axis color — the same cell the
    /// transform rows use, exported so a form row can lay several across
    /// one control region without ScrubRowDrag's own label column.
    /// </summary>
    public static bool DragAxisWell(ImDrawListPtr dl, Vector2 pos, float width, string id, string axis,
        ref float value, Vector4 color, float perPixel, string fmt, float s, out bool released)
    {
        bool wasReleased = false;
        bool changed = DragAxis(dl, pos, width, id, axis, ref value, color, perPixel, fmt, s, ref wasReleased);
        released = wasReleased;
        return changed;
    }

    /// <summary>Cancels an in-progress numeric axis edit, for example when selection changes.</summary>
    public static void CancelAxisEdit()
    {
        _axisEditId = null;
        _axisEditNeedsFocus = false;
    }

    private static bool DragAxis(ImDrawListPtr dl, Vector2 pos, float width, string id, string axis,
        ref float value, Vector4 color, float perPixel, string fmt, float s, ref bool released)
    {
        if (_axisEditId == id)
            return EditAxisValue(dl, pos, width, id, axis, ref value, color, fmt, s, ref released);

        ImGui.SetCursorScreenPos(pos);
        ImGui.InvisibleButton(id, new Vector2(width, 26f * s));
        bool active = ImGui.IsItemActive();
        bool hovered = ImGui.IsItemHovered();

        if (hovered && ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
        {
            _axisEditId = id;
            _axisEditValue = value;
            _axisEditNeedsFocus = true;
            active = false;
        }

        bool changed = false;
        var io = ImGui.GetIO();
        // The mouse wheel is navigation: hovering a numeric field never edits
        // a transform, and the wheel is left unconsumed so it keeps scrolling
        // the inspector. Horizontal drag is the pointer-edit interaction.
        if (active)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            float delta = io.MouseDelta.X;
            if (delta != 0f)
            {
                value += delta * perPixel * DragModifierMultiplier(io);
                changed = true;
            }
        }

        if (ImGui.IsItemDeactivated())
            released = true;

        DrawAxis(dl, pos, width, axis,
            value.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture), color, s);
        if (active)
            dl.AddRect(pos + new Vector2(0.5f, 0.5f), pos + new Vector2(width, 26f * s) - new Vector2(0.5f, 0.5f),
                ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color with { W = 0.6f })), 4f * s, ImDrawFlags.None, 1f * s);

        if (hovered && _axisEditId == null)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
            Crystarium.HoverHelp.Explain(id, pos, pos + new Vector2(width, 26f * s),
                "Drag to adjust · Ctrl fine ×0.1 · Shift coarse ×10 · Double-click to type");
        }

        return changed;
    }

    /// <summary>
    /// Shared drag-sensitivity policy: Ctrl fine (0.1×), Shift coarse (10×),
    /// Ctrl+Shift back to normal (1×). Scales pointer deltas only — the
    /// gesture still accumulates from its frozen baseline.
    /// </summary>
    public static float DragModifierMultiplier(ImGuiIOPtr io) =>
        io.KeyCtrl && io.KeyShift ? 1f :
        io.KeyCtrl ? 0.1f :
        io.KeyShift ? 10f : 1f;

    private static bool EditAxisValue(ImDrawListPtr dl, Vector2 pos, float width, string id, string axis,
        ref float value, Vector4 color, string fmt, float s, ref bool released)
    {
        DrawAxis(dl, pos, width, axis,
            _axisEditValue.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture), color, s);

        float editX = axis.Length > 0 ? 18f : 4f;
        ImGui.SetCursorScreenPos(pos + new Vector2(editX * s, 2f * s));
        ImGui.SetNextItemWidth(MathF.Max(1f, width - (editX + 2f) * s));
        if (_axisEditNeedsFocus)
            ImGui.SetKeyboardFocusHere();

        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4f, 3f) * s);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 3f * s);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Surface2);
        ImGui.PushStyleColor(ImGuiCol.FrameBgHovered, Surface2);
        ImGui.PushStyleColor(ImGuiCol.FrameBgActive, Surface2);
        bool enter = ImGui.InputFloat($"##axis-edit-{id}", ref _axisEditValue, 0f, 0f,
            InputFloatFormat(fmt), ImGuiInputTextFlags.AutoSelectAll | ImGuiInputTextFlags.EnterReturnsTrue);
        bool editedOnDeactivate = ImGui.IsItemDeactivatedAfterEdit();
        bool deactivated = ImGui.IsItemDeactivated();
        bool cancelled = ImGui.IsKeyPressed(ImGuiKey.Escape);
        ImGui.PopStyleColor(3);
        ImGui.PopStyleVar(2);
        _axisEditNeedsFocus = false;

        if (cancelled)
        {
            CancelAxisEdit();
            return false;
        }

        if (enter || editedOnDeactivate)
        {
            value = _axisEditValue;
            released = true;
            CancelAxisEdit();
            return true;
        }

        if (deactivated)
            CancelAxisEdit();

        return false;
    }

    private static string InputFloatFormat(string displayFormat)
    {
        int dot = displayFormat.IndexOf('.');
        int decimals = dot < 0 ? 0 : displayFormat.Length - dot - 1;
        return $"%.{decimals}f";
    }

    private static void DrawAxis(ImDrawListPtr dl, Vector2 pos, float width, string axis, string value, Vector4 color, float s)
    {
        var min = pos;
        var max = pos + new Vector2(width, 26f * s);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Black20)), 4f * s);
        dl.AddRect(min + new Vector2(0.5f, 0.5f), max - new Vector2(0.5f, 0.5f),
            ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(BorderSecondary)), 4f * s, ImDrawFlags.None, 1f * s);
        ViewText.Label(min + new Vector2(8f, 7f) * s, axis, 12f, FontWeight.Regular, color, mono: true);
        float valueIndent = axis.Length > 0 ? 8f + 14f : 8f;
        ViewText.Label(min + new Vector2(valueIndent, 7f) * s, value, 12f, FontWeight.Regular, TextPrimary, mono: true);
    }

    // ── shared small controls ────────────────────────────────────────────

    private static void IconButtonNamed(ImDrawListPtr dl, Vector2 pos, string iconName, bool on, float s, Action? onClick, bool dimmed = false, string? help = null, string? helpShortcut = null)
    {
        ImGui.SetCursorScreenPos(pos);
        var hit = Interactive.Reserve($"##ibn-{iconName}-{pos.X:0}-{pos.Y:0}", new Vector2(28f, 28f) * s, disabled: dimmed);
        if (help != null &&
            (hit.Hovered || (dimmed && Crystarium.HoverHelp.HelpHovered(hit.ScreenMin, hit.ScreenMax))))
            Crystarium.HoverHelp.Explain($"tb-{iconName}", hit.ScreenMin, hit.ScreenMax, help, helpShortcut);
        if (on)
            dl.AddRectFilled(hit.ScreenMin, hit.ScreenMax, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(ActiveOverlay)), 5f * s);
        else if (hit.Hovered && !dimmed)
            dl.AddRectFilled(hit.ScreenMin, hit.ScreenMax, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(HoverOverlay)), 5f * s);
        float alpha = dimmed ? 0.35f : (on || hit.Hovered ? 1f : 0.8f);
        ImGui.SetCursorScreenPos(pos + new Vector2(6f, 6f) * s);
        Crystarium.Icon(iconName, 16f * s, ColorEx.ApplyAlpha(TextPrimary with { W = alpha }));
        if (hit.Clicked && !dimmed)
            onClick?.Invoke();
    }

    private static void IconButton(ImDrawListPtr dl, Vector2 pos, TablerIcon icon, bool on, float s, Action? onClick, bool dimmed = false, bool flipX = false, string? help = null, string? helpShortcut = null)
    {
        ImGui.SetCursorScreenPos(pos);
        var hit = Interactive.Reserve($"##ib-{icon}-{pos.X:0}-{pos.Y:0}", new Vector2(28f, 28f) * s, disabled: dimmed);
        // A dimmed action still explains itself (why it is unavailable
        // is part of its help); hover is re-derived occlusion-aware.
        if (help != null &&
            (hit.Hovered || (dimmed && Crystarium.HoverHelp.HelpHovered(hit.ScreenMin, hit.ScreenMax))))
            Crystarium.HoverHelp.Explain($"tb-{icon}", hit.ScreenMin, hit.ScreenMax, help, helpShortcut);
        if (on)
            dl.AddRectFilled(hit.ScreenMin, hit.ScreenMax, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(ActiveOverlay)), 5f * s);
        else if (hit.Hovered && !dimmed)
            dl.AddRectFilled(hit.ScreenMin, hit.ScreenMax, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(HoverOverlay)), 5f * s);

        float alpha = dimmed ? 0.35f : (on || hit.Hovered ? 1f : 0.8f);
        ImGui.SetCursorScreenPos(pos + new Vector2(6f, 6f) * s);
        Crystarium.Icon(icon, 16f * s, ColorEx.ApplyAlpha(TextPrimary with { W = alpha }), flipX);
        if (hit.Clicked && !dimmed)
            onClick?.Invoke();
    }

    private static float IconSeg(ImDrawListPtr dl, Vector2 pos, TablerIcon[] icons, int active, float s, Action<int> onSelect)
    {
        float tabW = 34f * s;
        var min = pos;
        var max = pos + new Vector2(3f * s * 2f + tabW * icons.Length + 2f * s * (icons.Length - 1), 30f * s);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Black20)), 7f * s);

        for (int i = 0; i < icons.Length; i++)
        {
            var tabMin = new Vector2(min.X + 3f * s + i * (tabW + 2f * s), min.Y + 3f * s);
            var tabMax = tabMin + new Vector2(tabW, 24f * s);
            ImGui.SetCursorScreenPos(tabMin);
            var hit = Interactive.Reserve($"##iseg-{pos.X:0}-{i}", new Vector2(tabW, 24f * s) / s * s, disabled: false);
            if (i == active)
                dl.AddRectFilled(tabMin, tabMax, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Surface2)), 5f * s);
            ImGui.SetCursorScreenPos(tabMin + new Vector2((tabW - 14f * s) / 2f, 5f * s));
            Crystarium.Icon(icons[i], 14f * s, ColorEx.ApplyAlpha(i == active ? TextPrimary : TextSecondary));
            int capture = i;
            if (hit.Clicked) onSelect(capture);
        }
        return max.X;
    }

    private static float TextSeg(ImDrawListPtr dl, Vector2 pos, string[] labels, int active, float s, Action<int> onSelect,
        Func<int, bool>? itemDisabled = null)
    {
        float x = pos.X + 3f * s;
        float totalW = 6f * s + 2f * s * (labels.Length - 1);
        var widths = new float[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            widths[i] = ViewText.Measure(labels[i], 12f) + 20f * s;
            totalW += widths[i];
        }
        var min = pos;
        var max = pos + new Vector2(totalW, 30f * s);
        dl.AddRectFilled(min, max, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Black20)), 7f * s);

        for (int i = 0; i < labels.Length; i++)
        {
            bool disabled = itemDisabled?.Invoke(i) == true;
            var tabMin = new Vector2(x, min.Y + 3f * s);
            var tabMax = tabMin + new Vector2(widths[i], 24f * s);
            ImGui.SetCursorScreenPos(tabMin);
            var hit = Interactive.Reserve($"##tseg-{pos.X:0}-{i}", new Vector2(widths[i] / s, 24f), disabled);
            if (i == active)
                dl.AddRectFilled(tabMin, tabMax, ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(Surface2)), 5f * s);
            var labelColor = i == active ? TextPrimary : TextSecondary;
            if (disabled)
                labelColor = labelColor with { W = labelColor.W * 0.35f };
            ViewText.Label(new Vector2(x + 10f * s, min.Y + 3f * s + 5f * s), labels[i], 12f, FontWeight.Regular,
                labelColor);
            int capture = i;
            if (hit.Clicked) onSelect(capture);
            x += widths[i] + 2f * s;
        }
        return max.X;
    }
}
