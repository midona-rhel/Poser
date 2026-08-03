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

    /// <summary>The shell's ONE retained declarative surface, and everything
    /// that must outlive a frame with it. It lives on the view model because
    /// the view model is what the single shell instance already is: the static
    /// entry point stays a chrome seam and reaches its state through here.
    /// </summary>
    internal readonly ShellFrame Frame;

    public AppShellViewModel() => Frame = new ShellFrame(this);
}

/// <summary>
/// ONE sidebar row's retained UI: the handlers the declared row names, and the
/// stable key its interaction identity hangs on. Every delegate is allocated
/// once and dispatches against <see cref="Row"/>, which the build rewrites each
/// frame — so a live tree costs no per-frame closure and the callbacks the
/// binder wired keep taking the row they always took.
/// </summary>
internal sealed class ShellRowUi
{
    internal readonly UiKey Key;

    /// <summary>Written by the build, read at dispatch.</summary>
    internal ShellSidebarRow Row = new();

    internal readonly Action Select;
    internal readonly Action Context;
    internal readonly Action ToggleExpand;
    internal readonly Action Target;
    internal readonly Action Visibility;
    internal readonly Action Pause;
    internal readonly Action OverlayVisibility;

    internal ShellRowUi(AppShellViewModel vm, string key)
    {
        Key = key;
        Select = () => vm.OnRowClicked?.Invoke(Row);
        Context = () => vm.OnRowContextMenu?.Invoke(Row);
        ToggleExpand = () => vm.OnRowExpandToggled?.Invoke(Row);
        Target = () => vm.OnActorTarget?.Invoke(Row);
        Visibility = () => vm.OnActorVisibility?.Invoke(Row);
        Pause = () => vm.OnActorPause?.Invoke(Row);
        OverlayVisibility = () => vm.OnOverlayVisibility?.Invoke(Row);
    }
}

/// <summary>
/// The shell's declared chrome: ONE retained root that states the titlebar, the
/// sidebar, the workspace toolbar and the rail's chassis. Everything the tree
/// names is a field — a build path allocates no delegate — and every control is
/// KEYED, so the position-derived ids the imperative shell minted are gone.
///
/// <para>What it deliberately does NOT own: the content viewport, the page
/// scroll and the rail's scroll are the legacy hosting seam (the accepted
/// Settings pattern — a region owns the gutter, the pane's own root renders
/// inside it), and the sidebar's resize strip is raw input. Both are named
/// boundaries in <see cref="AppShellView.Draw"/>.</para>
/// </summary>
internal sealed class ShellFrame
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
    private const float SearchTop = 6f;
    private const float TreeTop = 38f;

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

    private static readonly Func<int, string?> GizmoHelp = static index => index switch
    {
        0 => "Move the selection",
        1 => "Rotate the selection",
        2 => "Scale the selection",
        _ => "Move, rotate, or scale with the universal gizmo",
    };

    private static readonly Func<int, string?> SpaceHelp = static index =>
        index == 0
            ? "Use the selected target's local axes"
            : "Use world-space axes";

    private static readonly Func<int, string?> PivotHelp = static index =>
        index == 0
            ? "Rotate each selected target around itself"
            : "Rotate around the selected bone's parent pivot";

    private static readonly Func<int, string?> SymmetryHelp = static index => index switch
    {
        0 => "Edit only the current selection",
        1 => "Apply the same edit to linked selections",
        _ => "Apply mirrored edits across left and right bones",
    };

    private readonly AppShellViewModel _vm;
    private readonly UiRoot _root = new();
    private readonly FilterFieldState _search = new();

    /// <summary>One holder per row, keyed by the row's STABLE tag. Swept when
    /// the scene revision changes: a structural change is the only thing that
    /// can retire a row identity, and an unchanged scene therefore keeps every
    /// holder for the shell's life.</summary>
    private readonly Dictionary<object, ShellRowUi> _rows = new();
    private ulong _rowsRevision;

    // ── hoisted handlers ─────────────────────────────────────────────────
    private readonly Action<string> _setSearch;
    private readonly Action<int> _setGizmo;
    private readonly Action<int> _setSpace;
    private readonly Action<int> _setPivot;
    private readonly Action<int> _setSymmetry;
    private readonly Action<int> _setTab;
    private readonly Action<bool> _setPhysics;
    private readonly Action _undo;
    private readonly Action _redo;
    private readonly Action _spawn;
    private readonly Action _project;
    private readonly Action _settings;
    private readonly Action _hideUi;
    private readonly Action _popOut;
    private readonly Action _collapse;
    private readonly Action _armature;
    private readonly Func<int, bool> _pivotDisabled;

    /// <summary>One "+" per section, minted on first sight and kept: the
    /// header names a handler, and a lambda per frame is what a declared tree
    /// may not cost.</summary>
    private Action[] _sectionPlus = new Action[4];
    private int _sectionPlusCount;

    // ── per-frame scratch, grown once ────────────────────────────────────
    private UiNode[] _nodes = new UiNode[64];
    private int _nodeCount;
    private string[] _tabLabels = new string[4];
    private int _tabCount;
    private int _tabActive;

    // ── memoized help ────────────────────────────────────────────────────
    // PoserKeybinds.Effective is a dictionary read, but composing the shortcut
    // into a sentence is a string per frame — so the four sentences are minted
    // only when the binding itself changes.
    private string _undoShortcut = string.Empty;
    private string _redoShortcut = string.Empty;
    private string _undoHelp = string.Empty;
    private string _undoEmptyHelp = string.Empty;
    private string _redoHelp = string.Empty;
    private string _redoEmptyHelp = string.Empty;

    private float _width;
    private float _height;

    internal ShellFrame(AppShellViewModel vm)
    {
        _vm = vm;
        _setSearch = next => _vm.SidebarSearch = next;
        _setGizmo = index => _vm.OnGizmoOperation?.Invoke(index);
        _setSpace = index => _vm.OnGizmoSpace?.Invoke(index);
        _setPivot = index => _vm.OnRotationPivot?.Invoke(index);
        _setSymmetry = index => _vm.OnSymmetry?.Invoke(index);
        _setTab = index => _vm.OnTab?.Invoke(index);
        _setPhysics = next => _vm.OnPhysics?.Invoke(next);
        _undo = () => _vm.OnUndo?.Invoke();
        _redo = () => _vm.OnRedo?.Invoke();
        _spawn = () => _vm.OnSpawn?.Invoke();
        _project = () => _vm.OnProject?.Invoke();
        _settings = () => _vm.OnSettings?.Invoke();
        _hideUi = () => _vm.OnHideUi?.Invoke();
        _popOut = () => _vm.OnPopOut?.Invoke();
        _collapse = () => _vm.OnCollapse?.Invoke(!_vm.Collapsed);
        _armature = () => _vm.OnSkeletonOverlay?.Invoke(!_vm.SkeletonOverlayOn);
        // Pivot keeps a permanent slot so tool/selection changes cannot move
        // the rest of the toolbar. Both choices refuse when pivot is
        // inapplicable; Parent additionally needs a live parent bone.
        _pivotDisabled = index => !_vm.RotationPivotEnabled
            || (index == 1 && !_vm.RotationPivotParentAvailable);
    }

    /// <summary>Everything one frame's build is TOLD; the frame reference is
    /// what the static builder reaches its state through.</summary>
    private readonly record struct Props(ShellFrame Frame);

    internal void Render(Vector2 origin, Vector2 size, float scale)
    {
        _width = size.X / scale;
        _height = size.Y / scale;
        SyncKeybindHelp();
        SyncTabs();
        PruneRows(_vm.SceneRevision);
        Props props = new(this);
        _root.Render(
            origin, size, in props, static (in Props p) => p.Frame.Build());
    }

    // ── the tree ─────────────────────────────────────────────────────────

    private UiNode Build()
    {
        AppShellViewModel vm = _vm;
        Theme theme = Crystarium.ActiveTheme;
        if (vm.Collapsed)
            return Titlebar(vm, theme);

        float bodyHeight = _height - AppShellView.TitlebarHeight;
        float railWidth = vm.DrawRail != null ? AppShellView.RailWidth : 0f;
        float sidebarWidth = vm.SidebarWidthPx;
        return new Column
        {
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fixed(_width),
                    Height = UiDim.Fixed(_height),
                },
            },
            Key = "shell",
            Children =
            [
                Titlebar(vm, theme),
                new Row
                {
                    Style = new()
                    {
                        Layout = new()
                        {
                            Width = UiDim.Fixed(_width),
                            Height = UiDim.Fixed(bodyHeight),
                        },
                    },
                    Key = "body",
                    Children =
                    [
                        Sidebar(vm, theme, sidebarWidth, bodyHeight),
                        Workspace(
                            vm,
                            theme,
                            _width - sidebarWidth - railWidth,
                            bodyHeight),
                        Rail(theme, railWidth, bodyHeight),
                    ],
                },
            ],
        };
    }

    // ── titlebar ─────────────────────────────────────────────────────────

    private UiNode Titlebar(AppShellViewModel vm, Theme theme)
    {
        float height = AppShellView.TitlebarHeight;
        float railWidth = vm.DrawRail != null ? AppShellView.RailWidth : 0f;
        UiChildren cells =
        [
            TitleLeft(vm, theme, height),
            TitleCenter(vm, theme, height),
            TitleRight(vm, theme, height, railWidth),
        ];

        // Collapsed means ONE continuous titlebar, not an empty window with a
        // surviving sidebar cell: one glass strip, no divider, no rail cell.
        if (vm.Collapsed)
            return new Chassis
            {
                Fill = AppShellView.Glass,
                Radius = theme.Radii.Window,
                Corners = UiCorners.All,
                Style = new()
                {
                    Layout = new()
                    {
                        Flow = UiFlow.Row,
                        Width = UiDim.Fixed(_width),
                        Height = UiDim.Fixed(height),
                    },
                },
                Key = "titlebar",
                Children = cells,
            };

        return new Row
        {
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fixed(_width),
                    Height = UiDim.Fixed(height),
                },
            },
            Key = "titlebar",
            Children = cells,
        };
    }

    private UiNode TitleLeft(AppShellViewModel vm, Theme theme, float height)
    {
        UiNode content = new Row
        {
            Style = new()
            {
                Layout = new()
                {
                    Align = UiAlign.Center,
                    Width = UiDim.Fill,
                    Height = UiDim.Fixed(height),
                    Padding = new EdgeInsets(
                        TitleInset, 0f, TitleActionInset, 0f),
                },
            },
            Key = "title-content",
            Children =
            [
                new Row
                {
                    Style = new()
                    {
                        Layout = new()
                        {
                            Align = UiAlign.Center,
                            Gap = theme.Spacing.Four,
                        },
                    },
                    Key = "brand",
                    Children =
                    [
                        new Label
                        {
                            Text = "Poser",
                            Style = new()
                            {
                                Type = new()
                                {
                                    FontSize = theme.Typography.BodySize,
                                    Weight = FontWeight.SemiBold,
                                },
                                Colors = new()
                                {
                                    Foreground = theme.Chrome.Text,
                                },
                            },
                        },
                        GPosePill(vm, theme),
                    ],
                },
                Spring(),
                new Row
                {
                    Style = new()
                    {
                        Layout = new()
                        {
                            Align = UiAlign.Center,
                            Gap = theme.Spacing.Two,
                        },
                    },
                    Key = "history",
                    Children =
                    [
                        new IconAction
                        {
                            Icon = TablerIcon.ArrowBackUp,
                            OnClick = _undo,
                            Disabled = !vm.CanUndo,
                            Size = theme.Controls.ShellIconAction,
                            Help = vm.CanUndo ? _undoHelp : _undoEmptyHelp,
                            Key = "undo",
                        },
                        new IconAction
                        {
                            Icon = TablerIcon.ArrowBackUp,
                            FlipX = true,
                            OnClick = _redo,
                            Disabled = !vm.CanRedo,
                            Size = theme.Controls.ShellIconAction,
                            Help = vm.CanRedo ? _redoHelp : _redoEmptyHelp,
                            Key = "redo",
                        },
                        vm.ShowSpawn
                            ? new IconAction
                            {
                                Icon = TablerIcon.Plus,
                                OnClick = _spawn,
                                Size = theme.Controls.ShellIconAction,
                                Help = "Add an actor to the scene",
                                Key = "spawn",
                            }
                            : UiNode.None,
                    ],
                },
            ],
        };

        if (vm.Collapsed)
            return new Row
            {
                Style = new()
                {
                    Layout = new()
                    {
                        Width = UiDim.Fixed(vm.SidebarWidthPx),
                        Height = UiDim.Fixed(height),
                    },
                },
                Key = "title-left",
                Children = content,
            };

        return new Chassis
        {
            Fill = AppShellView.Glass,
            Radius = theme.Radii.Window,
            Corners = UiCorners.TopLeft,
            Style = new()
            {
                Layout = new()
                {
                    Flow = UiFlow.Row,
                    Width = UiDim.Fixed(vm.SidebarWidthPx),
                    Height = UiDim.Fixed(height),
                },
            },
            Key = "title-left",
            Children = [content, Rule(AppShellView.BorderPrimary, height)],
        };
    }

    private static UiNode GPosePill(AppShellViewModel vm, Theme theme)
    {
        if (!vm.GPoseActive)
            return UiNode.None;
        Vector4 success = theme.Success;
        return new Row
        {
            Style = new()
            {
                Layout = new()
                {
                    Align = UiAlign.Center,
                    Height = UiDim.Fixed(PillHeight),
                    Padding = new EdgeInsets(
                        TitleActionInset, 0f, TitleActionInset, 0f),
                    Gap = theme.Spacing.Three,
                },
                Colors = new() { Fill = success with { W = 0.12f } },
                Shape = new() { Radius = theme.Radii.Window },
            },
            Key = "gpose",
            Children =
            [
                Dot(success),
                new Label
                {
                    Text = "GPose",
                    Style = new()
                    {
                        Type = new()
                        {
                            FontSize = theme.Typography.CaptionSize,
                            Weight = FontWeight.Medium,
                        },
                        Colors = new() { Foreground = success },
                    },
                },
            ],
        };
    }

    private UiNode TitleCenter(AppShellViewModel vm, Theme theme, float height)
        => new Row
        {
            Style = new()
            {
                Layout = new()
                {
                    Align = UiAlign.Center,
                    Width = UiDim.Fill,
                    Height = UiDim.Fixed(height),
                    Padding = new EdgeInsets(CenterInset, 0f, 0f, 0f),
                    Gap = theme.Page.ActionGap,
                },
            },
            Key = "title-center",
            Children =
            [
                vm.ShowProject
                    ? new IconAction
                    {
                        Icon = TablerIcon.Folder,
                        OnClick = _project,
                        Size = theme.Controls.ShellIconAction,
                        Help = "Open the scene project browser",
                        Key = "project",
                    }
                    : UiNode.None,
                new Segmented
                {
                    Icons = GizmoIcons,
                    Selected = vm.GizmoOperation,
                    OnChange = _setGizmo,
                    ItemHelp = GizmoHelp,
                    Key = "gizmo-operation",
                },
                new Segmented
                {
                    Items = SpaceItems,
                    Selected = vm.GizmoSpace,
                    OnChange = _setSpace,
                    ItemHelp = SpaceHelp,
                    Key = "gizmo-space",
                },
                new Segmented
                {
                    Items = PivotItems,
                    Selected = vm.RotationPivot,
                    OnChange = _setPivot,
                    ItemDisabled = _pivotDisabled,
                    ItemHelp = PivotHelp,
                    Key = "rotation-pivot",
                },
                new Segmented
                {
                    Items = SymmetryItems,
                    Selected = vm.SymmetryMode,
                    OnChange = _setSymmetry,
                    ItemHelp = SymmetryHelp,
                    Key = "symmetry",
                },
            ],
        };

    private UiNode TitleRight(
        AppShellViewModel vm, Theme theme, float height, float railWidth)
    {
        // Rightmost is the collapse chevron, then the close X (user spec).
        UiNode cluster = new Row
        {
            Style = new()
            {
                Layout = new()
                {
                    Align = UiAlign.Center,
                    Height = UiDim.Fixed(height),
                    Gap = theme.Page.ActionGap,
                },
            },
            Key = "title-actions",
            Children =
            [
                new IconAction
                {
                    Icon = TablerIcon.Armature,
                    Selected = vm.SkeletonOverlayOn,
                    OnClick = _armature,
                    Size = theme.Controls.ShellIconAction,
                    Help = "Toggle the skeleton overlay in the viewport",
                    Key = "armature",
                },
                new IconAction
                {
                    Icon = TablerIcon.Settings,
                    OnClick = _settings,
                    Size = theme.Controls.ShellIconAction,
                    Help = "Open Poser settings",
                    Key = "settings",
                },
                IconAction.Named("x") with
                {
                    OnClick = _hideUi,
                    Size = theme.Controls.ShellIconAction,
                    Help = "Hide the Poser window",
                    Key = "close",
                },
                IconAction.Named(vm.Collapsed ? "chevron-down" : "chevron-up")
                    with
                    {
                        OnClick = _collapse,
                        Size = theme.Controls.ShellIconAction,
                        Help = vm.Collapsed
                            ? "Expand the window"
                            : "Collapse to the title bar",
                        Key = "collapse",
                    },
            ],
        };

        // With the rail present the right cluster stands on a surface-1 cell
        // continuous with the rail below it (shell rule).
        if (railWidth > 0f && !vm.Collapsed)
            return new Chassis
            {
                Fill = theme.SurfaceRaised,
                Radius = theme.Radii.Window,
                Corners = UiCorners.TopRight,
                Style = new()
                {
                    Layout = new()
                    {
                        Flow = UiFlow.Row,
                        Align = UiAlign.Center,
                        Width = UiDim.Fixed(railWidth),
                        Height = UiDim.Fixed(height),
                        Padding = new EdgeInsets(0f, 0f, ClusterInset, 0f),
                    },
                },
                Key = "title-right",
                Children =
                [
                    Rule(AppShellView.BorderPrimary, height),
                    Spring(),
                    cluster,
                ],
            };

        return new Row
        {
            Style = new()
            {
                Layout = new()
                {
                    Align = UiAlign.Center,
                    Height = UiDim.Fixed(height),
                    Padding = new EdgeInsets(0f, 0f, ClusterInset, 0f),
                },
            },
            Key = "title-right",
            Children = cluster,
        };
    }

    // ── sidebar ──────────────────────────────────────────────────────────

    private UiNode Sidebar(
        AppShellViewModel vm, Theme theme, float width, float height)
    {
        float inset = theme.Page.Inset;
        // The pill spans the cell between the content inset and the 1px rule.
        float pillWidth = MathF.Max(1f, width - inset * 2f - 1f);
        float treeHeight = MathF.Max(
            1f,
            height - TreeTop - theme.Spacing.One
                - AppShellView.StatusbarHeight);

        return new Chassis
        {
            Fill = AppShellView.Glass,
            Radius = theme.Radii.Window,
            Corners = UiCorners.BottomLeft,
            Style = new()
            {
                Layout = new()
                {
                    Flow = UiFlow.Row,
                    Width = UiDim.Fixed(width),
                    Height = UiDim.Fixed(height),
                },
            },
            Key = "sidebar",
            Children =
            [
                new Column
                {
                    Style = new()
                    {
                        Layout = new()
                        {
                            Width = UiDim.Fill,
                            Height = UiDim.Fixed(height),
                        },
                    },
                    Key = "sidebar-body",
                    Children =
                    [
                        // Search stays OUTSIDE the scroll child so a large
                        // skeleton cannot push the sidebar's primary
                        // navigation affordance out of view.
                        new Row
                        {
                            Style = new()
                            {
                                Layout = new()
                                {
                                    Width = UiDim.Fill,
                                    Height = UiDim.Fixed(TreeTop),
                                    Padding = new EdgeInsets(
                                        inset, SearchTop, 0f, 0f),
                                },
                            },
                            Key = "search-band",
                            Children = Crystarium.FilterField(
                                _search,
                                vm.SidebarSearch,
                                _setSearch,
                                "Filter scene...",
                                new Vector2(pillWidth, TreeTop - SearchTop),
                                ControlStyle.Workspace with
                                {
                                    Width = UiWidth.Fixed(pillWidth),
                                },
                                key: "search"),
                        },
                        new ScrollArea
                        {
                            Height = UiDim.Fixed(treeHeight),
                            CapChildHitWidth = true,
                            Style = new()
                            {
                                Layout = new()
                                {
                                    Width = UiDim.Fill,
                                    Padding = new EdgeInsets(inset, 0f, 0f, 0f),
                                },
                            },
                            Key = "sidebar-tree",
                            Children = Tree(vm, theme),
                        },
                        Gap(theme.Spacing.One),
                        Rule(AppShellView.BorderSecondary, 1f, fill: true),
                        Statusbar(vm, theme),
                    ],
                },
                Rule(AppShellView.BorderPrimary, height),
            ],
        };
    }

    private UiNode Statusbar(AppShellViewModel vm, Theme theme) => new Row
    {
        Style = new()
        {
            Layout = new()
            {
                Align = UiAlign.Center,
                Width = UiDim.Fill,
                Height = UiDim.Fixed(AppShellView.StatusbarHeight - 1f),
                Padding = new EdgeInsets(
                    StatusInset, 0f, StatusInset, 0f),
            },
        },
        Key = "statusbar",
        Children =
        [
            Dot(theme.Success),
            new Label
            {
                Text = vm.StatusLeft,
                Style = StatusText(theme, StatusTextGap),
            },
            Spring(),
            new Label
            {
                Text = vm.StatusRight,
                Style = StatusText(theme, 0f),
            },
        ],
    };

    private static ElementSheet StatusText(Theme theme, float leadingGap) => new()
    {
        Type = new()
        {
            FontSize = theme.Typography.CaptionSize,
            Font = FontFamily.Mono,
        },
        Colors = new() { Foreground = theme.TextMuted },
        Layout = new()
        {
            Margin = new EdgeInsets(leadingGap, 0f, 0f, 0f),
        },
    };

    private UiChildren Tree(AppShellViewModel vm, Theme theme)
    {
        _nodeCount = 0;
        for (int s = 0; s < vm.Sections.Count; s++)
        {
            ShellSidebarSection section = vm.Sections[s];
            Add(SectionHeader(section, s, theme));
            for (int r = 0; r < section.Rows.Count; r++)
                Add(RowNode(vm, section.Rows[r], theme));
        }

        return UiChildren.Create(_nodes.AsSpan(0, _nodeCount));
    }

    private UiNode SectionHeader(
        ShellSidebarSection section, int index, Theme theme) => new Row
    {
        Style = new()
        {
            Layout = new()
            {
                Width = UiDim.Fill,
                Height = UiDim.Fixed(theme.Floating.CloseActionSize),
                Align = UiAlign.Start,
                Padding = new EdgeInsets(theme.Spacing.Two, 0f, 0f, 0f),
                Margin = index > 0
                    ? new EdgeInsets(0f, theme.Spacing.Four, 0f, 0f)
                    : null,
            },
        },
        Key = index,
        Children =
        [
            new Label
            {
                Text = section.Title,
                Style = new()
                {
                    Type = new()
                    {
                        FontSize = theme.Typography.LabelSize,
                        Weight = FontWeight.Medium,
                    },
                    Colors = new() { Foreground = theme.TextMuted },
                    Layout = new()
                    {
                        Margin = new EdgeInsets(0f, theme.Spacing.Two, 0f, 0f),
                    },
                },
            },
            Spring(),
            section.ShowPlus
                ? new IconAction
                {
                    Icon = TablerIcon.Plus,
                    OnClick = SectionPlus(index),
                    Size = theme.Controls.SwitchHeight,
                    Key = "plus",
                }
                : UiNode.None,
        ],
    };

    private UiNode RowNode(
        AppShellViewModel vm, ShellSidebarRow row, Theme theme)
    {
        ShellRowUi ui = RowUi(row.Tag ?? row.Label);
        ui.Row = row;
        return new TreeRow
        {
            Label = row.Label,
            Icon = row.IconName == null ? row.Icon : null,
            IconName = row.IconName,
            // Nested rows draw no mark; their guide column already spans the
            // same distance the root's icon cell does.
            HideIcon = row.Depth > 0,
            Badge = string.IsNullOrEmpty(row.Count) ? null : row.Count,
            Depth = row.Depth,
            Trunks = Trunks(row.TreeLines),
            IsLastChild = row.IsLastChild,
            Expander = row.HasChildren
                ? row.Expanded
                    ? SidebarExpander.Open
                    : SidebarExpander.Collapsed
                : SidebarExpander.None,
            ExpanderDisabled = row.ExpanderDisabled,
            Selected = row.Active,
            OnSelect = ui.Select,
            OnToggleExpand = ui.ToggleExpand,
            OnContext = ui.Context,
            Actions = RowActions(vm, row, ui, theme),
            Key = ui.Key,
        };
    }

    private static UiChildren RowActions(
        AppShellViewModel vm, ShellSidebarRow row, ShellRowUi ui, Theme theme)
    {
        float side = theme.Controls.SwitchHeight;
        if (row.ActorActions)
            return
            [
                new IconAction
                {
                    Icon = TablerIcon.Crosshair,
                    OnClick = ui.Target,
                    Size = side,
                    Help = "Set game target",
                    Key = "target",
                },
                new IconAction
                {
                    Icon = TablerIcon.Eye,
                    Selected = false,
                    Slashed = !row.ActorVisible,
                    OnClick = ui.Visibility,
                    Size = side,
                    Help = row.ActorVisible ? "Hide actor" : "Show actor",
                    Key = "visible",
                },
                new IconAction
                {
                    Icon = TablerIcon.PlayerPlay,
                    Selected = false,
                    Slashed = row.ActorPaused,
                    OnClick = ui.Pause,
                    Size = side,
                    Help = row.ActorPaused
                        ? "Resume animation"
                        : "Pause animation",
                    Key = "pause",
                },
            ];

        if (row.OverlayBones is not { } bones)
            return UiChildren.Empty;

        bool visible = vm.IsOverlayVisible?.Invoke(bones) ?? true;
        return new IconAction
        {
            Icon = visible ? TablerIcon.Eye : TablerIcon.EyeOff,
            Selected = false,
            Slashed = !visible,
            OnClick = ui.OverlayVisibility,
            Size = side,
            Help = visible
                ? "Hide from skeleton overlay"
                : "Show in skeleton overlay",
            Key = "overlay",
        };
    }

    /// <summary>The view model's per-ancestor sibling flags as the painter's
    /// trunk mask, verbatim: bit <c>a</c> is <c>TreeLines[a]</c>, and bit 0 is
    /// unused exactly as depth 0 has no trunk.</summary>
    private static uint Trunks(bool[]? lines)
    {
        if (lines == null)
            return 0u;
        uint mask = 0u;
        int levels = Math.Min(lines.Length, 32);
        for (int level = 1; level < levels; level++)
            if (lines[level])
                mask |= 1u << level;
        return mask;
    }

    // ── workspace ────────────────────────────────────────────────────────

    private UiNode Workspace(
        AppShellViewModel vm, Theme theme, float width, float height) =>
        new Column
        {
            Style = new()
            {
                Layout = new()
                {
                    Width = UiDim.Fixed(MathF.Max(1f, width)),
                    Height = UiDim.Fixed(height),
                },
            },
            Key = "workspace",
            Children = new ActionBar
            {
                // The tab strip is the SAME segmented pill every other mode
                // selector uses, not hand-drawn buttons; AlignFirstTabToCursor
                // lands the first tab's LABEL on the content inset, because the
                // pill's dark chrome is decoration and not padding.
                Left = _tabCount == 0
                    ? UiChildren.Empty
                    : new Segmented
                    {
                        Items = _tabLabels,
                        Selected = _tabActive,
                        OnChange = _setTab,
                        AlignFirstTabToCursor = true,
                        Key = "shell-tabs",
                    },
                // Actor physics occupies ONE stable right-aligned slot on every
                // workspace tab: a tab change never replaces it with selection
                // text and never moves it.
                Right =
                [
                    new Element
                    {
                        Sheet = SheetFamily.Row,
                        Style = new()
                        {
                            Layout = new()
                            {
                                Align = UiAlign.Center,
                                Gap = theme.Spacing.Three,
                            },
                        },
                        Help = vm.PhysicsAvailable
                            ? "Enable or disable physics for the selected actor"
                            : "Select an actor or bone to control physics",
                        Key = "physics",
                        Children =
                        [
                            new Label
                            {
                                Text = "Physics",
                                Style = new()
                                {
                                    Type = new()
                                    {
                                        FontSize = theme.Typography.CaptionSize,
                                        InkRise = theme.Optical.ActionBarText,
                                    },
                                    Colors = new()
                                    {
                                        Foreground = theme.FormLabel,
                                    },
                                },
                            },
                            new Switch
                            {
                                Value = vm.PhysicsOn,
                                OnToggle = _setPhysics,
                                Disabled = !vm.PhysicsAvailable,
                            },
                        ],
                    },
                    vm.ShowPopOut
                        ? new IconAction
                        {
                            Icon = TablerIcon.ExternalLink,
                            OnClick = _popOut,
                            Size = theme.Controls.ShellIconAction,
                            Key = "pop-out",
                        }
                        : UiNode.None,
                ],
                Separator = ActionBarSeparator.Bottom,
                // The toolbar shares ONE horizontal inset with the content
                // beneath it, not the modal chassis' header inset.
                Inset = AppShellView.MainHorizontalPadding,
                Key = "workspace-actions",
            },
        };

    private static UiNode Rail(Theme theme, float width, float height) =>
        width <= 0f
            ? UiNode.None
            : new Chassis
            {
                // The rail chassis is continuous with the titlebar's tb-right
                // cell; its content is hosted by the legacy scroll seam.
                Fill = theme.SurfaceRaised,
                Radius = theme.Radii.Window,
                Corners = UiCorners.BottomRight,
                Style = new()
                {
                    Layout = new()
                    {
                        Flow = UiFlow.Row,
                        Width = UiDim.Fixed(width),
                        Height = UiDim.Fixed(height),
                    },
                },
                Key = "rail",
                Children = Rule(AppShellView.BorderPrimary, height),
            };

    // ── shared leaves ────────────────────────────────────────────────────

    private static UiNode Spring() => new Element
    {
        Style = new() { Layout = new() { Width = UiDim.Fill } },
    };

    /// <summary>One full-width band of empty vertical flow.</summary>
    private static UiNode Gap(float height) => new Element
    {
        Style = new()
        {
            Layout = new()
            {
                Width = UiDim.Fill,
                Height = UiDim.Fixed(height),
            },
        },
    };

    private static UiNode Rule(Vector4 color, float height, bool fill = false)
        => new Element
        {
            Style = new()
            {
                Layout = new()
                {
                    Width = fill ? UiDim.Fill : UiDim.Fixed(1f),
                    Height = UiDim.Fixed(fill ? 1f : height),
                },
                Colors = new() { Fill = color },
            },
        };

    /// <summary>A filled circle: a square whose radius is half its side, which
    /// is what ImGui's own corner clamp makes of it.</summary>
    private static UiNode Dot(Vector4 color) => new Element
    {
        Style = new()
        {
            Layout = new()
            {
                Width = UiDim.Fixed(DotSize),
                Height = UiDim.Fixed(DotSize),
            },
            Colors = new() { Fill = color },
            Shape = new() { Radius = DotSize * 0.5f },
        },
    };

    // ── retained bookkeeping ─────────────────────────────────────────────

    private ShellRowUi RowUi(object tag)
    {
        if (_rows.TryGetValue(tag, out ShellRowUi? existing))
            return existing;
        ShellRowUi created = new(
            _vm, tag as string ?? tag.ToString() ?? string.Empty);
        _rows[tag] = created;
        return created;
    }

    private void PruneRows(ulong revision)
    {
        if (_rowsRevision == revision)
            return;
        _rowsRevision = revision;
        _rows.Clear();
    }

    private Action SectionPlus(int index)
    {
        if (index >= _sectionPlus.Length)
            Array.Resize(ref _sectionPlus, Math.Max(index + 1, _sectionPlus.Length * 2));
        while (_sectionPlusCount <= index)
        {
            int captured = _sectionPlusCount;
            _sectionPlus[captured] = () => _vm.OnSectionPlus?.Invoke(captured);
            _sectionPlusCount++;
        }

        return _sectionPlus[index];
    }

    private void Add(UiNode node)
    {
        if (_nodeCount == _nodes.Length)
            Array.Resize(ref _nodes, _nodeCount * 2);
        _nodes[_nodeCount++] = node;
    }

    /// <summary>The tab strip reads a plain array, and Segmented reads ALL of
    /// it — so the buffer is exactly the tab count and is reallocated only when
    /// that count changes, which the workspace's fixed tab set never does.
    /// </summary>
    private void SyncTabs()
    {
        int count = _vm.Tabs.Count;
        if (_tabLabels.Length != count)
            _tabLabels = new string[count];
        _tabActive = 0;
        for (int i = 0; i < count; i++)
        {
            _tabLabels[i] = _vm.Tabs[i].Label;
            if (_vm.Tabs[i].Active)
                _tabActive = i;
        }

        _tabCount = count;
    }

    private void SyncKeybindHelp()
    {
        string undo = PoserKeybinds.Effective("Undo");
        if (!string.Equals(undo, _undoShortcut, StringComparison.Ordinal))
        {
            _undoShortcut = undo;
            _undoHelp = $"Take back the last pose edit · {undo}";
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
}

/// <summary>
/// The M1 "Studio" shell. Its chrome — the exclusive-input owner, the one
/// shell-level blur, the glass chassis, the collapsed early return, the content
/// viewport, the rail's scroll and the sidebar's resize strip — is imperative by
/// name; everything between those seams is ONE declared tree
/// (<see cref="ShellFrame"/>).
/// </summary>
public static class AppShellView
{
    internal static Vector4 Glass =>
        LegacyCrystarium.FloatingSurface.FillColor;
    internal static Vector4 BorderPrimary =>
        Crystarium.ActiveTheme.Chrome.ControlBorder;
    internal static Vector4 BorderSecondary =>
        Crystarium.ActiveTheme.FormSeparator;

    public static float TitlebarHeight => Crystarium.ActiveTheme.Shell.TitlebarHeight;
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

            // One shell-level blur; child panels only add translucent fills.
            LegacyCrystarium.FloatingSurface.PrependShellBlur(
                dl, min, max, radius * s);
            // USER 2026-08-03: the main window wears the SETTINGS chassis —
            // one glass fill inside one glass edge — with the elevation shadow
            // suppressed (a shadow under a chassis that IS the window reads as
            // a halo) and the blur left to the one call above.
            LegacyCrystarium.FloatingSurface.DrawChrome(
                dl, min, max, radius, shadow: false, blur: false);

            vm.Frame.Render(min, size, s);

            if (vm.Collapsed)
            {
                DrawOuterGlassBorder(min, max);
                return; // titlebar strip only
            }

            float bodyTop = min.Y + TitlebarHeight * s;
            float railW = vm.DrawRail != null ? RailWidth * s : 0f;
            float sbw = vm.SidebarWidthPx * s;
            DrawContentViewport(
                vm,
                new Vector2(min.X + sbw, bodyTop),
                new Vector2(max.X - railW, max.Y),
                s);

            // M11: resizable sidebar — 6px col-resize strip on its right edge.
            // Raw pointer input against a named boundary: the strip has no
            // box, no state and no paint, only a drag delta.
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
                DrawRailScroll(vm, new Vector2(max.X - railW, bodyTop), max, railW, s);

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

    /// <summary>
    /// The legacy hosting seam, unchanged: the viewport child and the page
    /// scroll own the gutter and the extent bookkeeping, and the active pane's
    /// OWN root renders inside them — exactly as the Settings page is hosted.
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
        // The inset is measured from the CHILD, not the panel: the child is
        // 1px narrower than the panel (the glass border pixel), and the
        // scrollbar hugs the child's right edge.
        ImGui.SetCursorScreenPos(childOrigin);
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
            var viewportCursor = ImGui.GetCursorScreenPos();
            if (vm.ContentOwnsViewport)
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
                LegacyCrystarium.ScrollRegion(
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
                            + new Vector2(
                                MainHorizontalPadding * s,
                                0f);
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

    /// <summary>
    /// The rail's hosting seam. The child reaches the outer-right glass edge;
    /// its content keeps 12px left padding and a fixed 12px right composite
    /// gutter: 0px content gap + 12px scrollbar.
    /// </summary>
    private static void DrawRailScroll(
        AppShellViewModel vm, Vector2 railMin, Vector2 max, float railW, float s)
    {
        var railChildOrigin = railMin + new Vector2(0f, 12f) * s;
        ImGui.SetCursorScreenPos(railChildOrigin);
        LegacyCrystarium.ScrollRegion(
            "##shell-rail",
            railW / s - 1f,
            (max.Y - railMin.Y) / s - 24f,
            region =>
            {
                var railContentOrigin =
                    ImGui.GetCursorScreenPos()
                    + new Vector2(Crystarium.ActiveTheme.Page.Inset * s, 0f);
                ImGui.SetCursorScreenPos(railContentOrigin);
                vm.DrawRail!(
                    railContentOrigin,
                    new Vector2(
                        region.ContentWidth * s
                            - Crystarium.ActiveTheme.Page.Inset * s,
                        max.Y - railMin.Y - 24f * s));
            });
    }

    private static void DrawOuterGlassBorder(Vector2 min, Vector2 max) =>
        LegacyCrystarium.FloatingSurface.DrawBorder(
            min, max, Crystarium.ActiveTheme.Radii.Window);

    /// <summary>Cancels an in-progress numeric axis edit, for example when selection changes.</summary>
    public static void CancelAxisEdit()
    {
        LegacyCrystarium.CancelAxisEdit();
    }
}
