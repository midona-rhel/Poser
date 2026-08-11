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
    /// <summary>Nested rows normally draw no mark, because their guide column
    /// already spans the same distance the root's icon cell does. A nested row
    /// that IS a thing rather than a grouping (the gaze anchor under an actor)
    /// opts the mark back in.</summary>
    public bool ForceIcon;
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
    /// <summary>A light row's action slot: one eye, the same affordance an
    /// actor row wears, switching the light off without losing a setting.
    /// </summary>
    public bool LightActions;
    public bool LightOn = true;
    /// <summary>A camera row's action slots: a lock protecting the shot,
    /// then the video mark making this the LIVE camera — the light eye's
    /// twin, except exactly one camera wears it at a time.</summary>
    public bool CameraActions;
    public bool CameraLive;
    public bool CameraLocked;
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
    /// <summary>The header row is itself a target: it selects the section
    /// rather than only naming it. Off leaves the header inert, which is what
    /// every ordinary section is.</summary>
    public bool Selectable;
    /// <summary>Only meaningful with <see cref="Selectable"/>: the header wears
    /// the row selection language while its section owns the workspace.
    /// </summary>
    public bool Active;
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
    public bool AnimationOn;
    public bool AnimationAvailable;
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
    /// The scene's structural revision. The sidebar's retained per-row state is
    /// keyed by a row's stable tag and swept when this changes: a rescan that
    /// publishes nothing new leaves every holder — and therefore every row's
    /// interaction identity and hover — exactly where it was.
    /// </summary>
    public ulong SceneRevision;

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

    /// <summary>
    /// The pane takes the viewport WALL TO WALL: no shell horizontal inset and
    /// no reserved scrollbar column, because the pane paints its own bands,
    /// rules and gutters against the workspace edges. The library uses this —
    /// its footer rule has to meet the same edges every other shell rule does.
    /// Wins over <see cref="ContentOwnsViewport"/>.
    /// </summary>
    public bool ContentFlush;

    /// <summary>Sidebar width, resizable within 220–400px. Unscaled px.</summary>
    public float SidebarWidthPx = 280f;
    public Action<float>? OnSidebarResize;

    /// <summary>Inspector rail: drawn when set — 280px right column,
    /// continuous surface from the titlebar's tb-right cell to the window bottom.</summary>
    public Action<Vector2, Vector2>? DrawRail;  // (origin, size)

    /// <summary>Collapse-to-titlebar: only the 48px strip renders.</summary>
    public bool Collapsed;
    public Action<bool>? OnCollapse;

    /// <summary>ONE toggle: detached mode floats the toolbar strip and the
    /// sidebar as their own windows; this window keeps the content and the
    /// inspector. Off is the compact single-window UI.</summary>
    public bool Detached;


    /// <summary>What the content's title names: the selected entity
    /// ("Sterling Vane", "Environment", "Library"), never the brand — the
    /// brand rides the toolbar.</summary>
    public string TitleEntity = "Poser";

    public Action<int>? OnTab;
    public Action<int>? OnGizmoOperation;
    public Action<int>? OnGizmoSpace;
    public Action<int>? OnRotationPivot;
    public Action<int>? OnSymmetry;
    public Action<bool>? OnAnimation;
    public Action<bool>? OnPhysics;
    public Action? OnUndo, OnRedo, OnSpawn, OnSettings, OnHideUi, OnPopOut, OnProject;
    /// <summary>The titlebar command menu, told the burger button's
    /// bottom-left screen position so the menu anchors under the button
    /// instead of at the mouse.</summary>
    public Action<Vector2>? OnBurger;
    public Action<bool>? OnSkeletonOverlay;
    public Action<ShellSidebarRow>? OnRowClicked;
    public Action<ShellSidebarRow>? OnRowContextMenu;
    public Action<ShellSidebarRow>? OnRowExpandToggled;
    public Action<ShellSidebarRow>? OnActorTarget;
    public Action<ShellSidebarRow>? OnActorVisibility;
    public Action<ShellSidebarRow>? OnActorPause;
    public Action<ShellSidebarRow>? OnLightVisibility;
    public Action<ShellSidebarRow>? OnCameraLive;
    public Action<ShellSidebarRow>? OnCameraLock;
    public Action<ShellSidebarRow>? OnOverlayVisibility;
    public Func<IReadOnlyList<Domain.Identity.BoneId>, bool>?
        IsOverlayVisible;
    public Action<int>? OnSectionPlus;
    /// <summary>A click on a <see cref="ShellSidebarSection.Selectable"/>
    /// header, told the section index.</summary>
    public Action<int>? OnSectionSelected;
}

/// <summary>
/// The "Studio" shell, drawn per frame: the window chassis, the titlebar and
/// its control clusters, the sidebar chassis and status bar, the workspace
/// toolbar, the content viewport and the inspector rail.
///
/// <para>The sidebar's search field and tree are NOT here — <see
/// cref="ShellSidebar"/> owns them behind its own cache. The shell seats it and
/// keeps everything around it: chassis, rules, status bar, resize strip.</para>
///
/// <para>Chrome: one shell-level blur, then the Settings glass treatment with the
/// elevation shadow suppressed (a shadow under a chassis that IS the window
/// reads as a halo). Panel fills land on top of it, so the asymmetric glass edge
/// is repainted last.</para>
/// </summary>
public static class AppShellView
{
    // ── the shell's own literals, kept where they are read ────────────────
    private const float TitleInset = 14f;
    private const float TitleActionInset = 8f;
    private const float CenterInset = 12f;
    private const float ClusterInset = 12f;
    private const float PillHeight = 20f;
    private const float DotSize = 7f;
    private const float StatusInset = 10f;
    private const float StatusTextGap = 8f;


    private static readonly TablerIcon[] GizmoIcons =
    [
        TablerIcon.ArrowsMove,
        TablerIcon.Rotate,
        TablerIcon.ArrowsDiagonal,
        TablerIcon.ArrowsMaximize,
    ];

    private static readonly string[] SpaceItems = ["Local", "World"];
    private static readonly string[] PivotItems = ["Self", "Parent"];
    private static readonly string[] SymmetryItems = ["Off", "Link", "Mirror"];

    /// <summary>The one sidebar, retained: its flat cache is what makes a warm
    /// frame cost the visible band instead of the whole tree.</summary>
    private static readonly ShellSidebar Sidebar = new();

    /// <summary>The tab strip reads ALL of the array, so the buffer is exactly
    /// the tab count and is reallocated only when that count changes.</summary>
    private static string[] _tabLabels = [];
    private static int _tabActive;

    // Composing a keybind into a sentence is a string per frame, so the four
    // sentences are minted only when the binding itself changes.
    private static string _undoShortcut = string.Empty;
    private static string _redoShortcut = string.Empty;
    private static string _undoHelp = string.Empty;
    private static string _undoEmptyHelp = string.Empty;
    private static string _redoHelp = string.Empty;
    private static string _redoEmptyHelp = string.Empty;

    /// <summary>The burger's press, reported by a hoisted callback that closes
    /// over nothing and is consumed inside the same seat.</summary>
    private static bool _burgerPressed;
    private static readonly Action BurgerPressed =
        static () => _burgerPressed = true;

    private static Vector4 Glass =>
        Crystarium.FloatingSurface.FillColor;
    private static Vector4 BorderPrimary =>
        Crystarium.ActiveTheme.Chrome.ControlBorder;
    private static Vector4 BorderSecondary =>
        Crystarium.ActiveTheme.FormSeparator;

    /// <summary>ONE bar height: the titlebar (expanded AND collapsed), the
    /// floating toolbar, the part and pop-out headers all share the modal bar
    /// height — collapse must not move a single icon (user 2026-08-11).
    /// </summary>
    public static float TitlebarHeight =>
        Crystarium.ActiveTheme.Floating.ModalBarHeight;

    /// <inheritdoc cref="TitlebarHeight"/>
    public static float CollapsedBarHeight => TitlebarHeight;
    public static float SidebarWidth => Crystarium.ActiveTheme.Shell.SidebarDefaultWidth;
    public static float RowHeight => Crystarium.ActiveTheme.Controls.ListRowHeight;
    public static float ToolbarHeight => Crystarium.ActiveTheme.Shell.ToolbarHeight;
    public static float StatusbarHeight => Crystarium.ActiveTheme.Shell.StatusbarHeight;
    public static float ScrollbarWidth => Crystarium.ActiveTheme.Scrollbar.GutterWidth;
    public static float ScrollbarRadius => Crystarium.ActiveTheme.Scrollbar.Radius;
    public static float MainHorizontalPadding => Crystarium.ActiveTheme.Page.Inset;
    public static float RailWidth => Crystarium.ActiveTheme.Shell.RailWidth;

    public static void Draw(AppShellViewModel vm, Vector2 origin, Vector2 size)
    {
        ArgumentNullException.ThrowIfNull(vm);
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
            float radius = Crystarium.ActiveTheme.Radii.Window;

            // THE one window chrome — the same DrawChrome defaults every
            // floating surface calls (user 2026-08-11: one chrome everywhere).
            Crystarium.FloatingSurface.DrawChrome(dl, min, max, radius);

            SyncKeybindHelp();
            DrawTitlebar(vm, min, max, s, dl);

            if (vm.Collapsed)
            {
                DrawOuterGlassBorder(min, max);
                return; // titlebar strip only
            }

            float bodyTop = min.Y + TitlebarHeight * s;
            float railW = vm.DrawRail != null ? RailWidth * s : 0f;
            // Detached mode: the sidebar is its own window; the content and
            // the inspector stay together here.
            float sbw = vm.Detached ? 0f : vm.SidebarWidthPx * s;

            if (!vm.Detached)
                DrawSidebar(
                    vm, new Vector2(min.X, bodyTop),
                    new Vector2(min.X + sbw, max.Y), s, dl);
            DrawWorkspace(
                vm,
                new Vector2(min.X + sbw, bodyTop),
                new Vector2(max.X - railW, max.Y),
                s,
                dl);
            if (railW > 0f)
                DrawRail(vm, new Vector2(max.X - railW, bodyTop), max, railW, s, dl);

            if (!vm.Detached)
                DrawSidebarResize(vm, min.X + sbw, bodyTop, max.Y, s);

            // Panel fills are intentionally drawn after the base chassis.
            // Repaint the asymmetric glass edge last so sidebar and rail
            // surfaces cannot hide the left, right, or bottom glass borders.
            DrawOuterGlassBorder(min, max);
        }
        finally
        {
            Interactive.EndOwner(shellOwner);
        }
    }

    // ── titlebar ─────────────────────────────────────────────────────────

    private static void DrawTitlebar(
        AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        float height = TitlebarHeight * s;
        float radius = theme.Radii.Window * s;
        float rule = 1f * s;
        float cellWidth = vm.Detached ? 0f : vm.SidebarWidthPx * s;
        float railWidth =
            vm.DrawRail != null && !vm.Collapsed ? RailWidth * s : 0f;

        if (vm.Collapsed || vm.Detached)
        {
            // Collapsed — and detached, whose sidebar cell is a window of its
            // own — means ONE continuous titlebar: one glass strip, no
            // divider.
            dl.AddRectFilled(
                min, new Vector2(max.X, min.Y + height), U32(Glass), radius);
        }
        else
        {
            var cellMax = new Vector2(min.X + cellWidth, min.Y + height);
            dl.AddRectFilled(
                min, cellMax, U32(Glass), radius, ImDrawFlags.RoundCornersTopLeft);
            dl.AddRectFilled(
                new Vector2(cellMax.X - rule, min.Y), cellMax, U32(BorderPrimary));
        }
        if (!vm.Collapsed && railWidth > 0f)
        {
            // With the rail present the right cluster stands on a surface-1
            // cell continuous with the rail below it (shell rule).
            var railMin = new Vector2(max.X - railWidth, min.Y);
            dl.AddRectFilled(
                railMin,
                new Vector2(max.X, min.Y + height),
                U32(theme.SurfaceRaised),
                radius,
                ImDrawFlags.RoundCornersTopRight);
            dl.AddRectFilled(
                railMin,
                new Vector2(railMin.X + rule, min.Y + height),
                U32(BorderPrimary));
        }

        if (vm.Detached)
        {
            // The detached main window IS the inspector's window: it names
            // itself so, with the selected entity (user 2026-08-11).
            string title = vm.TitleEntity == "Poser"
                ? "Inspector"
                : $"Inspector – {vm.TitleEntity}";
            // The title stands on the CONTENT column's own inset, so the
            // window's left side reads as one aligned edge: title, tab
            // strips, content (user 2026-08-11).
            Crystarium.TextInBand(
                new Vector2(min.X + MainHorizontalPadding * s, min.Y),
                new Vector2(
                    MathF.Max(1f, max.X - min.X
                        - MainHorizontalPadding * 2f * s),
                    height),
                title,
                new TextStyle
                {
                    Size = theme.Typography.BodySize,
                    Weight = FontWeight.SemiBold,
                    Color = theme.Chrome.Text,
                });
        }
        else
        {
            DrawBrand(vm, min, height, s, dl);
            // The title cell's content stops at the divider's x whether or
            // not the divider paints this state: collapse must not shift the
            // cluster by the rule's pixel (user 2026-08-11).
            DrawHistory(
                vm,
                min.X + cellWidth - rule - TitleActionInset * s,
                min.Y,
                height,
                s);
            DrawTitleCenter(vm, min.X + cellWidth, min.Y, height, s);
        }
        DrawTitleActions(vm, max.X, min.Y, height, s);
    }

    /// <summary>The sidebar's title cell OWNS the brand and its GPose pill
    /// while attached; detached mode takes them to the floating toolbar
    /// (user 2026-08-11).</summary>
    private static void DrawBrand(
        AppShellViewModel vm, Vector2 min, float height, float s, ImDrawListPtr dl)
        => DrawBrandPill(vm, min.X + TitleInset * s, min.Y, height, s, dl);

    /// <summary>"Poser" and the GPose pill, drawn at <paramref name="x"/> in
    /// a band of <paramref name="height"/>; returns the x past them. One
    /// renderer for the toolbar's two hosts — the titlebar centre and the
    /// floating toolbar window.</summary>
    private static float DrawBrandPill(
        AppShellViewModel vm, float x, float top, float height, float s,
        ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        var nameStyle = new TextStyle
        {
            Size = theme.Typography.BodySize,
            Weight = FontWeight.SemiBold,
            Color = theme.Chrome.Text,
        };
        float nameWidth = Crystarium.MeasureText("Poser", nameStyle).X;
        Crystarium.TextInBand(
            new Vector2(x, top), new Vector2(nameWidth, height),
            "Poser", nameStyle);
        if (!vm.GPoseActive)
            return x + nameWidth;

        var success = theme.Success;
        var pillStyle = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Weight = FontWeight.Medium,
            Color = success,
        };
        float textWidth = Crystarium.MeasureText("GPose", pillStyle).X;
        float pillHeight = PillHeight * s;
        float dot = DotSize * s;
        var pillMin = new Vector2(
            x + nameWidth + theme.Spacing.Four * s,
            top + (height - pillHeight) * 0.5f);
        var pillMax = pillMin + new Vector2(
            TitleActionInset * 2f * s + dot + theme.Spacing.Three * s + textWidth,
            pillHeight);
        dl.AddRectFilled(
            pillMin, pillMax, U32(success with { W = 0.12f }),
            theme.Radii.Window * s);

        var dotMin = new Vector2(
            pillMin.X + TitleActionInset * s,
            pillMin.Y + (pillHeight - dot) * 0.5f);
        dl.AddCircleFilled(
            dotMin + new Vector2(dot * 0.5f), dot * 0.5f, U32(success));
        Crystarium.TextInBand(
            new Vector2(dotMin.X + dot + theme.Spacing.Three * s, pillMin.Y),
            new Vector2(textWidth, pillHeight),
            "GPose",
            pillStyle);
        return pillMax.X;
    }

    /// <summary>What <see cref="DrawBrandPill"/> spans, screen px.</summary>
    private static float MeasureBrandPill(AppShellViewModel vm, float s)
    {
        var theme = Crystarium.ActiveTheme;
        float width = Crystarium.MeasureText(
            "Poser",
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Weight = FontWeight.SemiBold,
            }).X;
        if (!vm.GPoseActive)
            return width;
        float text = Crystarium.MeasureText(
            "GPose",
            new TextStyle
            {
                Size = theme.Typography.CaptionSize,
                Weight = FontWeight.Medium,
            }).X;
        return width
            + theme.Spacing.Four * s
            + TitleActionInset * 2f * s
            + DotSize * s
            + theme.Spacing.Three * s
            + text;
    }

    /// <summary>Menu, undo, redo and spawn, right-aligned in the title cell.</summary>
    private static void DrawHistory(
        AppShellViewModel vm, float right, float top, float height, float s)
    {
        var theme = Crystarium.ActiveTheme;
        float side = theme.Controls.ShellIconAction;
        float step = (side + theme.Spacing.Two) * s;
        int count = vm.ShowSpawn ? 4 : 3;
        float y = top + (height - side * s) * 0.5f;
        float x = right - count * side * s - (count - 1) * theme.Spacing.Two * s;

        // The command menu hangs off its own button, not off the pointer, so
        // the seat hands its bottom-left corner to the opener. The click
        // callback captures NOTHING — a warm titlebar frame must not mint a
        // closure — so the press is reported through a static flag the seat
        // reads back one line later, while the anchor is still a local.
        IconAt(
            new Vector2(x, y), TablerIcon.Menu2, side, BurgerPressed,
            "##shell-burger",
            help: "Actions");
        if (_burgerPressed)
        {
            _burgerPressed = false;
            vm.OnBurger?.Invoke(new Vector2(x, y + side * s));
        }
        x += step;
        IconAt(
            new Vector2(x, y), TablerIcon.ArrowBackUp, side, vm.OnUndo,
            "##shell-undo",
            disabled: !vm.CanUndo,
            help: vm.CanUndo ? _undoHelp : _undoEmptyHelp);
        x += step;
        IconAt(
            new Vector2(x, y), TablerIcon.ArrowBackUp, side, vm.OnRedo,
            "##shell-redo",
            disabled: !vm.CanRedo,
            flipX: true,
            help: vm.CanRedo ? _redoHelp : _redoEmptyHelp);
        if (!vm.ShowSpawn)
            return;
        IconAt(
            new Vector2(x + step, y), TablerIcon.Plus, side, vm.OnSpawn,
            "##shell-spawn",
            help: "Add an actor or prop to the scene");
    }

    private static void DrawTitleCenter(
        AppShellViewModel vm, float left, float top, float height, float s)
    {
        var theme = Crystarium.ActiveTheme;
        float side = theme.Controls.ShellIconAction;
        float gap = theme.Page.ActionGap * s;
        float x = left + CenterInset * s;
        if (vm.ShowProject)
        {
            IconAt(
                new Vector2(x, top + (height - side * s) * 0.5f),
                TablerIcon.Folder, side, vm.OnProject, "##shell-project",
                help: "Open the scene project browser");
            x += side * s + gap;
        }

        DrawGizmoCluster(vm, x, top, height, s);
    }

    /// <summary>The four segment groups — gizmo operation, space, pivot,
    /// symmetry — drawn once per frame from exactly one host: the titlebar
    /// centre, or the floating toolbar when the toolbar is split. One set of
    /// ids, so hover and motion state survive the move between hosts.</summary>
    private static float DrawGizmoCluster(
        AppShellViewModel vm, float x, float top, float height, float s)
    {
        float gap = Crystarium.ActiveTheme.Page.ActionGap * s;
        x = Segments(
            x, top, height,
            "##shell-gizmo-operation",
            GizmoIcons,
            vm.GizmoOperation,
            index => vm.OnGizmoOperation?.Invoke(index),
            itemHelp: static index => index switch
            {
                0 => "Use the gizmo to move the selection",
                1 => "Use the gizmo to rotate the selection",
                2 => "Use the gizmo to scale the selection",
                _ => "Use one gizmo to move, rotate and scale",
            }) + gap;
        x = Segments(
            x, top, height,
            "##shell-gizmo-space",
            SpaceItems,
            vm.GizmoSpace,
            index => vm.OnGizmoSpace?.Invoke(index),
            itemHelp: static index => index == 0
                ? "Use the selection's own axes"
                : "Use the world axes") + gap;
        // Pivot keeps a permanent slot so tool/selection changes cannot move the
        // rest of the toolbar. Both choices refuse when pivot is inapplicable;
        // Parent additionally needs a live parent bone.
        x = Segments(
            x, top, height,
            "##shell-rotation-pivot",
            PivotItems,
            vm.RotationPivot,
            index => vm.OnRotationPivot?.Invoke(index),
            itemDisabled: index => !vm.RotationPivotEnabled
                || (index == 1 && !vm.RotationPivotParentAvailable),
            itemHelp: static index => index == 0
                ? "Rotate each selected bone in place"
                : "Rotate the selected bone around its parent bone") + gap;
        return Segments(
            x, top, height,
            "##shell-symmetry",
            SymmetryItems,
            vm.SymmetryMode,
            index => vm.OnSymmetry?.Invoke(index),
            itemHelp: static index => index switch
            {
                0 => "Edit only the current selection",
                1 => "Also apply the same edit to the opposite-side bone",
                _ => "Also apply a mirrored edit to the opposite-side bone",
            });
    }

    /// <summary>Rightmost is the collapse chevron, then the close X.
    /// </summary>
    private static void DrawTitleActions(
        AppShellViewModel vm, float right, float top, float height, float s)
    {
        var theme = Crystarium.ActiveTheme;
        float side = theme.Controls.ShellIconAction;
        float step = (side + theme.Page.ActionGap) * s;
        float y = top + (height - side * s) * 0.5f;
        float x = right - ClusterInset * s - side * s;

        NamedIconAt(
            new Vector2(x, y),
            vm.Collapsed ? "chevron-down" : "chevron-up",
            side,
            () => vm.OnCollapse?.Invoke(!vm.Collapsed),
            "##shell-collapse",
            vm.Collapsed ? "Expand the window" : "Collapse to the title bar");
        x -= step;
        NamedIconAt(
            new Vector2(x, y), "x", side, vm.OnHideUi, "##shell-close",
            "Hide the Poser window");
        x -= step;
        IconAt(
            new Vector2(x, y), TablerIcon.Settings, side, vm.OnSettings,
            "##shell-settings", help: "Open Poser settings");
        // The pop-out lives on the TITLE bar, not the workspace bar
        // (user 2026-08-11).
        if (vm.ShowPopOut)
        {
            x -= step;
            IconAt(
                new Vector2(x, y), TablerIcon.ExternalLink, side, vm.OnPopOut,
                "##shell-popout",
                help: "Pop the selected actor's content into its own window");
        }
        // The armature toggle left this bar (user 2026-08-11): its
        // replacement is a design decision that has not landed, so the
        // SkeletonOverlayOn/OnSkeletonOverlay seams stay wired for it and
        // the overlay's own UserVisible semantics are untouched.
    }

    // ── sidebar ──────────────────────────────────────────────────────────

    private static void DrawSidebar(
        AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        float rule = 1f * s;
        // The chassis carries the divider on its right edge; everything inside
        // stops at it.
        dl.AddRectFilled(
            min, max, U32(Glass), theme.Radii.Window * s,
            ImDrawFlags.RoundCornersBottomLeft);
        dl.AddRectFilled(
            new Vector2(max.X - rule, min.Y), max, U32(BorderPrimary));

        float bodyRight = max.X - rule;
        float statusTop = max.Y - StatusbarHeight * s;
        // The search band and the tree; the chassis, the divider rule and the
        // status bar are the shell's.
        Sidebar.Draw(
            vm,
            min,
            new Vector2(
                max.X - min.X,
                statusTop - theme.Spacing.One * s - min.Y));

        dl.AddRectFilled(
            new Vector2(min.X, statusTop),
            new Vector2(bodyRight, statusTop + rule),
            U32(BorderSecondary));
        DrawStatusbar(
            vm, new Vector2(min.X, statusTop + rule),
            new Vector2(bodyRight, max.Y), s, dl);
    }

    /// <summary>Status information only: actor count and frame rate.</summary>
    private static void DrawStatusbar(
        AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        float height = max.Y - min.Y;
        float dot = DotSize * s;
        var dotMin = new Vector2(
            min.X + StatusInset * s, min.Y + (height - dot) * 0.5f);
        dl.AddCircleFilled(
            dotMin + new Vector2(dot * 0.5f), dot * 0.5f, U32(theme.Success));

        var style = new TextStyle
        {
            Size = theme.Typography.CaptionSize,
            Color = theme.TextMuted,
            Family = FontFamily.Mono,
        };
        float leftWidth = Crystarium.MeasureText(vm.StatusLeft, style).X;
        Crystarium.TextInBand(
            new Vector2(dotMin.X + dot + StatusTextGap * s, min.Y),
            new Vector2(leftWidth, height),
            vm.StatusLeft,
            style);
        float rightWidth = Crystarium.MeasureText(vm.StatusRight, style).X;
        Crystarium.TextInBand(
            new Vector2(max.X - StatusInset * s - rightWidth, min.Y),
            new Vector2(rightWidth, height),
            vm.StatusRight,
            style);
    }

    /// <summary>The 6px col-resize strip on the sidebar's right edge. Raw
    /// pointer input against a named boundary — no box, no state, no paint,
    /// only a drag delta.</summary>
    private static void DrawSidebarResize(
        AppShellViewModel vm, float edge, float top, float bottom, float s)
    {
        var theme = Crystarium.ActiveTheme;
        ImGui.SetCursorScreenPos(new Vector2(edge - 3f * s, top));
        ImGui.InvisibleButton("##sidebar-resize", new Vector2(6f * s, bottom - top));
        if (ImGui.IsItemHovered() || ImGui.IsItemActive())
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeEw);
        if (ImGui.IsItemActive() && ImGui.GetIO().MouseDelta.X != 0f)
            vm.OnSidebarResize?.Invoke(Math.Clamp(
                vm.SidebarWidthPx + ImGui.GetIO().MouseDelta.X / s,
                theme.Shell.SidebarMinimumWidth,
                theme.Shell.SidebarMaximumWidth));
    }

    // ── workspace ────────────────────────────────────────────────────────

    private static void DrawWorkspace(
        AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        float toolbarBottom = min.Y + ToolbarHeight * s;
        float inset = MainHorizontalPadding * s;
        // The rule spans the bar's whole box, past the inset its items keep.
        dl.AddRectFilled(
            new Vector2(min.X, toolbarBottom - 1f * s),
            new Vector2(max.X, toolbarBottom),
            U32(BorderSecondary));

        SyncTabs(vm);
        if (_tabLabels.Length > 0)
        {
            // The tab strip is the SAME segmented pill every other mode
            // selector uses, not hand-drawn buttons; alignFirstTabToCursor
            // lands the first tab's LABEL on the content inset, because the
            // pill's dark chrome is decoration and not padding.
            var size = Crystarium.MeasureSegmentedControl(_tabLabels);
            ImGui.SetCursorScreenPos(new Vector2(
                min.X + inset,
                min.Y + (ToolbarHeight * s - size.Y) * 0.5f));
            Crystarium.SegmentedControl(
                "##shell-tabs",
                _tabLabels,
                _tabActive,
                chosen => vm.OnTab?.Invoke(chosen),
                alignFirstTabToCursor: true);
        }

        // Actor physics occupies ONE stable right-aligned slot on every
        // workspace tab: a tab change never replaces it with selection text and
        // never moves it.
        Crystarium.ActionBar(
            "shell-workspace-actions",
            new Vector2(min.X + inset, min.Y),
            new Vector2(max.X - min.X - inset * 2f, ToolbarHeight * s),
            static _ => { },
            right =>
            {
                right.Switch(
                    "Animation",
                    vm.AnimationOn,
                    next => vm.OnAnimation?.Invoke(next),
                    !vm.AnimationAvailable
                        ? "Select an actor to pause its animation"
                        : vm.AnimationOn
                            ? "Switch off to pause this actor's animation"
                            : "Switch on to resume this actor's animation",
                    disabled: !vm.AnimationAvailable);
                right.Switch(
                    "Physics",
                    vm.PhysicsOn,
                    next => vm.OnPhysics?.Invoke(next),
                    !vm.PhysicsAvailable
                        ? "Select an actor or bone to freeze physics for the whole scene"
                        : vm.PhysicsOn
                            ? "Switch off to freeze physics for the whole scene"
                            : "Switch on to resume physics for the whole scene",
                    disabled: !vm.PhysicsAvailable);
            },
            ActionBarSeparator.None);

        DrawContentViewport(vm, min, max, s);
    }

    /// <summary>
    /// The hosting seam: the viewport child and the page scroll own the gutter
    /// and the extent bookkeeping, and the active pane's OWN root renders inside
    /// them — exactly as the Settings page is hosted.
    /// </summary>
    private static void DrawContentViewport(
        AppShellViewModel vm, Vector2 min, Vector2 max, float s)
    {
        float toolbarBottom = min.Y + ToolbarHeight * s;
        // Toolbar and content share one 12px horizontal inset. The viewport
        // still reaches the outer-right glass edge, and content width always
        // excludes the 12px scrollbar gutter so overflow cannot cause reflow.
        // ONE content origin for every tab: panes own their breathing room,
        // the shell owns the origin.
        var childOrigin = new Vector2(min.X, toolbarBottom);
        var childSize = new Vector2(
            max.X - min.X - 1f * s,
            max.Y - toolbarBottom - 1f * s);
        // The inset is measured from the CHILD, not the panel: the child is 1px
        // narrower than the panel (the glass border pixel), and the scrollbar
        // hugs the child's right edge.
        ImGui.SetCursorScreenPos(childOrigin);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        if (ImGui.BeginChild(
                "##shell-content-viewport",
                childSize,
                false,
                ImGuiWindowFlags.NoScrollbar
                | ImGuiWindowFlags.NoScrollWithMouse))
        {
            var viewportCursor = ImGui.GetCursorScreenPos();
            if (vm.ContentFlush)
            {
                vm.DrawContent?.Invoke(
                    viewportCursor,
                    new Vector2(
                        childSize.X,
                        MathF.Max(0f, ImGui.GetContentRegionAvail().Y)));
            }
            else if (vm.ContentOwnsViewport)
            {
                float contentInset = MainHorizontalPadding * s;
                var contentOrigin = viewportCursor
                    + new Vector2(contentInset, 0f);
                vm.DrawContent?.Invoke(
                    contentOrigin,
                    new Vector2(
                        MathF.Max(
                            0f,
                            childSize.X
                                - ScrollbarWidth * s
                                - contentInset * 2f),
                        MathF.Max(
                            0f,
                            ImGui.GetContentRegionAvail().Y)));
            }
            else
            {
                ImGui.SetCursorScreenPos(viewportCursor);
                Crystarium.ScrollRegion(
                    "##shell-content-scroll",
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
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();
    }

    // ── rail ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The rail's chassis — surface-1 continuous with the titlebar's tb-right
    /// cell — and its hosting seam. The child reaches the outer-right glass
    /// edge; its content keeps 12px left padding and a fixed 12px right
    /// composite gutter: 0px content gap + 12px scrollbar.
    /// </summary>
    private static void DrawRail(
        AppShellViewModel vm,
        Vector2 railMin,
        Vector2 max,
        float railWidth,
        float s,
        ImDrawListPtr dl)
    {
        var theme = Crystarium.ActiveTheme;
        dl.AddRectFilled(
            railMin, max, U32(theme.SurfaceRaised), theme.Radii.Window * s,
            ImDrawFlags.RoundCornersBottomRight);
        dl.AddRectFilled(
            railMin, new Vector2(railMin.X + 1f * s, max.Y), U32(BorderPrimary));

        RailScrollSeam(vm, railMin, max, railWidth, s);
    }

    /// <summary>The rail's scroll seam and content invocation, shared by the
    /// attached rail and the floating inspector window. The chassis around it
    /// is each host's own.</summary>
    private static void RailScrollSeam(
        AppShellViewModel vm, Vector2 railMin, Vector2 max,
        float railWidth, float s)
    {
        var theme = Crystarium.ActiveTheme;
        ImGui.SetCursorScreenPos(railMin + new Vector2(0f, 12f) * s);
        Crystarium.ScrollRegion(
            "##shell-rail",
            railWidth / s - 1f,
            (max.Y - railMin.Y) / s - 24f,
            region =>
            {
                var contentOrigin = ImGui.GetCursorScreenPos()
                    + new Vector2(theme.Page.Inset * s, 0f);
                ImGui.SetCursorScreenPos(contentOrigin);
                vm.DrawRail!(
                    contentOrigin,
                    new Vector2(
                        region.ContentWidth * s - theme.Page.Inset * s,
                        max.Y - railMin.Y - 24f * s));
            });
    }

    /// <summary>The floating sidebar's content: the search band, the tree and
    /// the status bar, drawn into the hosting window's content box. The
    /// chassis is the caller's.</summary>
    public static void DrawSidebarContent(
        AppShellViewModel vm, Vector2 min, Vector2 max)
    {
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        var dl = ImGui.GetWindowDrawList();
        float rule = 1f * s;
        float statusTop = max.Y - StatusbarHeight * s;
        Sidebar.Draw(
            vm,
            min,
            new Vector2(
                max.X - min.X,
                statusTop - theme.Spacing.One * s - min.Y));
        dl.AddRectFilled(
            new Vector2(min.X, statusTop),
            new Vector2(max.X, statusTop + rule),
            U32(BorderSecondary));
        DrawStatusbar(
            vm, new Vector2(min.X, statusTop + rule), max, s, dl);
    }

    // ── shared seats ─────────────────────────────────────────────────────

    private static void IconAt(
        Vector2 position,
        TablerIcon icon,
        float side,
        Action? onClick,
        string id,
        bool disabled = false,
        bool flipX = false,
        string? help = null)
    {
        ImGui.SetCursorScreenPos(position);
        Crystarium.IconButton(
            icon, onClick, ControlStyle.Square(side), disabled, help, id, flipX);
    }

    private static void NamedIconAt(
        Vector2 position,
        string icon,
        float side,
        Action? onClick,
        string id,
        string? help = null)
    {
        ImGui.SetCursorScreenPos(position);
        Crystarium.IconButton(
            icon, onClick, ControlStyle.Square(side), help: help, id: id);
    }

    /// <summary>Seats a segmented control centred in the title band and reports
    /// the x its next neighbour starts from.</summary>
    private static float Segments(
        float x,
        float bandTop,
        float bandHeight,
        string id,
        TablerIcon[] items,
        int selected,
        Action<int> onChange,
        Func<int, string?>? itemHelp = null)
    {
        var size = Crystarium.MeasureSegmentedControl(items);
        ImGui.SetCursorScreenPos(
            new Vector2(x, bandTop + (bandHeight - size.Y) * 0.5f));
        Crystarium.SegmentedControl(
            id, items, selected, onChange, itemHelp: itemHelp);
        return x + size.X;
    }

    private static float Segments(
        float x,
        float bandTop,
        float bandHeight,
        string id,
        string[] items,
        int selected,
        Action<int> onChange,
        Func<int, bool>? itemDisabled = null,
        Func<int, string?>? itemHelp = null)
    {
        var size = Crystarium.MeasureSegmentedControl(items);
        ImGui.SetCursorScreenPos(
            new Vector2(x, bandTop + (bandHeight - size.Y) * 0.5f));
        Crystarium.SegmentedControl(
            id,
            items,
            selected,
            onChange,
            itemDisabled: itemDisabled,
            itemHelp: itemHelp);
        return x + size.X;
    }

    private static uint U32(Vector4 color) =>
        ImGui.ColorConvertFloat4ToU32(ColorEx.ApplyAlpha(color));

    private static void DrawOuterGlassBorder(Vector2 min, Vector2 max) =>
        Crystarium.FloatingSurface.DrawBorder(
            min, max, Crystarium.ActiveTheme.Radii.Window);

    private static void SyncTabs(AppShellViewModel vm)
    {
        int count = vm.Tabs.Count;
        if (_tabLabels.Length != count)
            _tabLabels = new string[count];
        _tabActive = 0;
        for (int i = 0; i < count; i++)
        {
            _tabLabels[i] = vm.Tabs[i].Label;
            if (vm.Tabs[i].Active)
                _tabActive = i;
        }
    }

    private static void SyncKeybindHelp()
    {
        string undo = PoserKeybinds.Effective("Undo");
        if (!string.Equals(undo, _undoShortcut, StringComparison.Ordinal))
        {
            _undoShortcut = undo;
            _undoHelp = $"Undo the last move, rotation or scale · {undo}";
            _undoEmptyHelp = $"Nothing to undo · {undo}";
        }

        string redo = PoserKeybinds.Effective("Redo");
        if (!string.Equals(redo, _redoShortcut, StringComparison.Ordinal))
        {
            _redoShortcut = redo;
            _redoHelp = $"Reapply the change you undid · {redo}";
            _redoEmptyHelp = $"Nothing to redo · {redo}";
        }
    }

    /// <summary>Cancels an in-progress numeric axis edit, for example when selection changes.</summary>
    public static void CancelAxisEdit()
    {
        Crystarium.CancelAxisEdit();
    }

    // ── the split shell's standalone parts ───────────────────────────────
    // Each part draws with the SAME retained state and ids it has inside the
    // shell — the sidebar cache, the segment motion channels, the keybind
    // help — so splitting a part moves it without resetting it. Exactly one
    // host draws a part per frame; the split flags are that gate.

    /// <summary>The floating toolbar's content: the brand and its GPose
    /// pill, the command menu, undo/redo, then the same four segment groups
    /// the titlebar centre hosts when attached. The spawn plus stays with
    /// the scene window (user 2026-08-11).</summary>
    public static void DrawToolbarContent(
        AppShellViewModel vm, Vector2 origin, float height)
    {
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float side = theme.Controls.ShellIconAction;
        float step = (side + theme.Spacing.Two) * s;
        SyncKeybindHelp();
        float x = DrawBrandPill(
                vm, origin.X, origin.Y, height, s, ImGui.GetWindowDrawList())
            + CenterInset * s;
        float y = origin.Y + (height - side * s) * 0.5f;
        IconAt(
            new Vector2(x, y), TablerIcon.Menu2, side, BurgerPressed,
            "##shell-burger",
            help: "Actions");
        if (_burgerPressed)
        {
            _burgerPressed = false;
            vm.OnBurger?.Invoke(new Vector2(x, y + side * s));
        }
        x += step;
        IconAt(
            new Vector2(x, y), TablerIcon.ArrowBackUp, side, vm.OnUndo,
            "##shell-undo",
            disabled: !vm.CanUndo,
            help: vm.CanUndo ? _undoHelp : _undoEmptyHelp);
        x += step;
        IconAt(
            new Vector2(x, y), TablerIcon.ArrowBackUp, side, vm.OnRedo,
            "##shell-redo",
            disabled: !vm.CanRedo,
            flipX: true,
            help: vm.CanRedo ? _redoHelp : _redoEmptyHelp);
        x += step;
        if (vm.ShowSpawn)
        {
            IconAt(
                new Vector2(x, y), TablerIcon.Plus, side, vm.OnSpawn,
                "##shell-spawn",
                help: "Add an actor or prop to the scene");
            x += step;
        }
        x += CenterInset * s - theme.Spacing.Two * s;
        DrawGizmoCluster(vm, x, origin.Y, height, s);
    }

    /// <summary>What <see cref="DrawToolbarContent"/> will span, screen px,
    /// so the hosting window sizes itself before drawing.</summary>
    public static float MeasureToolbar(AppShellViewModel vm)
    {
        float s = ImGuiHelpers.GlobalScale;
        var theme = Crystarium.ActiveTheme;
        float gap = theme.Page.ActionGap * s;
        float side = theme.Controls.ShellIconAction;
        float step = (side + theme.Spacing.Two) * s;
        // Burger, undo, redo, spawn, then the two window toggles at the end.
        float icons = step * (vm.ShowSpawn ? 4f : 3f);
        return MeasureBrandPill(vm, s)
            + CenterInset * s
            + icons
            + CenterInset * s - theme.Spacing.Two * s
            + Crystarium.MeasureSegmentedControl(GizmoIcons).X + gap
            + Crystarium.MeasureSegmentedControl(SpaceItems).X + gap
            + Crystarium.MeasureSegmentedControl(PivotItems).X + gap
            + Crystarium.MeasureSegmentedControl(SymmetryItems).X;
    }
}
