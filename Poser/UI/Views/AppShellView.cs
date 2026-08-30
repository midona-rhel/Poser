using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace Poser.UI.Views;

/// <summary>Where a dragged row lands relative to its target.</summary>
public enum RowDropPosition
{
    /// <summary>Into the target group, appended.</summary>
    Into,
    /// <summary>Before the target row, inside its group.</summary>
    Before,
    /// <summary>After the target row, inside its group.</summary>
    After,
    /// <summary>Open space: the dragged rows leave their group.</summary>
    Out,
}

public sealed class ShellSidebarRow
{
    public string Label = "";
    /// <summary>Whether the row can be dragged (entities and group
    /// heads; never bones or categories).</summary>
    public bool Draggable;
    /// <summary>Whether a drag can drop INTO this row — group heads
    /// only. An actor's disclosure is not a container: nothing else
    /// may highlight as one.</summary>
    public bool DropContainer;
    /// <summary>Whether the row lives inside a named group's subtree —
    /// the head-vs-members highlight rule reads it.</summary>
    public bool GroupMember;
    /// <summary>Group-head rows: the lock action seat.</summary>
    public bool GroupActions;
    public bool GroupLocked;
    /// <summary>Camera rows: the kind letter between the live and lock
    /// seats — M main, F free, C camera. A marker, not a control.</summary>
    public string CameraMark = "";
    /// <summary>Camera rows: whether the recenter-on-tracked-actor seat
    /// has anything to do right now.</summary>
    public bool CameraCanRecenter;
    public string Count = "";
    public TablerIcon Icon = TablerIcon.User;
    /// <summary>Named custom icon (PoserIconSources) — wins over Icon when set.</summary>
    public string? IconName;
    /// <summary>Nested rows normally draw no mark, because their guide column
    /// already spans the same distance the root's icon cell does. A nested row
    /// that is a thing rather than a grouping (the gaze anchor under an actor)
    /// opts the mark back in.</summary>
    public bool ForceIcon;
    public int Depth;              // 0 root, 1+ nested (20px indent per level)
    public bool HasChildren;
    /// <summary>Disclosure affordance shown but faded and inert — the row's
    /// children are temporarily unavailable (e.g. skeleton not yet resolved).
    /// The affordance is never erased once a row can disclose children.</summary>
    public bool ExpanderDisabled;
    /// <summary>Chevron key for this row.</summary>
    public string? ExpandKey;
    /// <summary>Key for this row's hidden bones.</summary>
    public string? OverlayMemoryKey;
    public bool Expanded;
    public bool Active;
    public object? Tag;
    /// <summary>Bones selected when this group row is clicked.</summary>
    public IReadOnlyList<Domain.Identity.BoneId>? SelectionBones;
    public bool ActorActions;
    public bool ActorVisible = true;
    public bool ActorPaused;
    /// <summary>The game's current target: its crosshair stands at full
    /// opacity, every other actor's fades — the live camera's treatment.
    /// </summary>
    public bool ActorTargeted;
    /// <summary>A light row's action slot: one eye, the same affordance an
    /// actor row wears, switching the light off without losing a setting.
    /// </summary>
    public bool LightActions;
    public bool LightOn = true;
    /// <summary>A camera row exposes its live and edit-lock states.</summary>
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

/// <summary>
/// One class of thing the world holds that the scene does not, as the footer
/// states it: a glyph that is lit while that class marks the world with
/// clickable handles and faded while it does not.
///
/// <para>The classes are footer toggles rather than scene entities.</para>
/// </summary>
public sealed class ShellWorldClass
{
    public TablerIcon Icon = TablerIcon.Circle;
    public bool On;
    /// <summary>The two hover cards, minted with the class: a warm frame states
    /// help, so it must not build the sentence.</summary>
    public string ShowHelp = "";
    public string HideHelp = "";
    /// <summary>The ImGui seat, stable across frames and distinct per class.
    /// </summary>
    public string Id = "";
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
    public string BranchLabel = "";

    /// <summary>The world-adoption classes, in the source's own order. Retained
    /// by the binder and restated in place, never rebuilt per frame.</summary>
    public List<ShellWorldClass> WorldClasses = new();

    public List<ShellTab> Tabs = new();

    public int GizmoOperation;        // 0 translate, 1 rotate, 2 scale, 3 universal
    public int GizmoSpace;            // 0 local, 1 world
    public int RotationPivot;         // 0 self, 1 parent
    public bool RotationPivotEnabled;
    public bool RotationPivotParentAvailable;
    public int SymmetryMode;          // 0 off, 1 link, 2 mirror
    /// <summary>Whether animation is enabled for the current actor.</summary>
    public bool AnimationOn;
    public bool AnimationAvailable;

    /// <summary>Physics has no availability twin: the freeze is one
    /// process-global patch, so its switch is live under every selection and
    /// under none.</summary>
    public bool PhysicsOn;

    public bool CanUndo = true;
    public bool CanRedo;

    /// <summary>Descriptions for the pending undo and redo actions.</summary>
    public string? UndoDescription;
    public string? RedoDescription;
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
    /// The content ScrollRegion's identity, keyed by the active strip and
    /// tab: ImGui persists scroll offset and extent per child id, so one
    /// shared id would carry tab A's offset into tab B's first frame and
    /// clamp-jump on the next — and strips reuse labels ("Light" is a light's
    /// whole editor and the environment's lighting tab), so the tab key alone
    /// would still share scroll memory across strips. Minted by the active
    /// tab's owner on strip/tab switch, never per frame.
    /// </summary>
    public string ContentScrollId = ContentScrollIdFor("actor", "Pose");

    /// <summary>The per-strip, per-tab scroll identity derivation — the one
    /// home for the id shape, so hosts cannot drift apart.</summary>
    public static string ContentScrollIdFor(string stripKey, string tabKey) =>
        "##shell-content-scroll/" + stripKey + "/" + tabKey;

    /// <summary>
    /// The pane owns its internal scrolling and needs the shell viewport to
    /// remain fixed. Pose uses this for fixed mode tabs and footer chrome.
    /// </summary>
    public bool ContentOwnsViewport;

    /// <summary>
    /// The pane takes the viewport wall to wall: no shell horizontal inset and
    /// no reserved scrollbar column, because the pane paints its own bands,
    /// rules and gutters against the workspace edges. The library uses this —
    /// its footer rule has to meet the same edges every other shell rule does.
    /// Wins over <see cref="ContentOwnsViewport"/>.
    /// </summary>
    public bool ContentFlush;

    /// <summary>Sidebar width, resizable within 220–400px. Unscaled px.</summary>
    public float SidebarWidthPx = 280f;

    /// <summary>Opens the library window — the sidebar titlebar's own
    /// button.</summary>
    public Action? OnLibrary;
    public Action<float>? OnSidebarResize;

    /// <summary>Inspector rail: drawn when set — 280px right column,
    /// continuous surface from the titlebar's tb-right cell to the window bottom.</summary>
    public Action<Vector2, Vector2>? DrawRail;  // (origin, size)

    /// <summary>The inspector's panel: 0 Target, 1 Environment, 2 Scene.
    /// The selector band draws only while <see cref="OnInspectorMode"/>
    /// is set — the library's per-type rails carry no selector.</summary>
    public int InspectorMode;
    public Action<int>? OnInspectorMode;

    /// <summary>What the content side is showing — "Actor", "Object",
    /// "Camera", "Environment", "Scene" — the identity label leading the
    /// tab band, so a tabless page still says what it is.</summary>
    public string ContentKind = "";

    /// <summary>Collapse-to-titlebar: only the 48px strip renders.</summary>
    public bool Collapsed;
    public Action<bool>? OnCollapse;

    /// <summary>The sidebar COLUMN folded away; the titlebar cell stays,
    /// so the brand, burger and library keep their seats.</summary>
    public bool SidebarCollapsed;
    public Action<bool>? OnSidebarCollapse;

    /// <summary>The inspector rail folded away; the titlebar keeps the
    /// reopen chevron.</summary>
    public bool InspectorCollapsed;
    public Action<bool>? OnInspectorCollapse;

    /// <summary>The rail lives in its own Inspector window.</summary>
    public bool InspectorSplit;

    /// <summary>Whether the rail column renders inside THIS window.</summary>
    internal bool RailShown =>
        DrawRail != null && !InspectorCollapsed && !InspectorSplit;

    /// <summary>The detached-mode toggle floats the toolbar strip and the
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
    public Action? OnUndo, OnRedo, OnSettings, OnHideUi, OnPopOut, OnProject;
    /// <summary>
    /// Button-opened surfaces use the button seat; context menus use the
    /// pointer because they have no button seat.
    /// </summary>
    public Action<Vector2>? OnBurger;

    /// <summary>The titlebar plus, anchored under itself exactly as
    /// <see cref="OnBurger"/> is.</summary>
    public Action<Vector2>? OnSpawn;
    public Action<ShellSidebarRow>? OnRowClicked;

    /// <summary>A drag released: <c>dragged</c> lands relative to
    /// <c>target</c> (null target = open space, which un-groups).</summary>
    public Action<ShellSidebarRow, ShellSidebarRow?, RowDropPosition>? OnRowDrop;

    /// <summary>The drag ghost's text for a row — "N selected" when the
    /// dragged row carries the whole selection with it.</summary>
    public Func<ShellSidebarRow, string>? DragGhostText;
    public Action<ShellSidebarRow>? OnRowContextMenu;
    public Action<ShellSidebarRow>? OnRowExpandToggled;
    public Action<ShellSidebarRow>? OnGroupLock;
    public Action<ShellSidebarRow>? OnCameraRecenter;
    public Action<ShellSidebarRow>? OnActorTarget;
    public Action<ShellSidebarRow>? OnActorVisibility;
    public Action<ShellSidebarRow>? OnActorPause;
    public Action<ShellSidebarRow>? OnLightVisibility;
    public Action<ShellSidebarRow>? OnCameraLive;
    public Action<ShellSidebarRow>? OnCameraLock;
    public Action<ShellSidebarRow>? OnOverlayVisibility;
    /// <summary>A footer world-class glyph was clicked, told its index into
    /// <see cref="WorldClasses"/>.</summary>
    public Action<int>? OnWorldClassToggle;
    /// <summary>Returns overlay visibility as none, partial, or all.</summary>
    public Func<IReadOnlyList<Domain.Identity.BoneId>, int>?
        OverlayVisibilityOf;
    /// <summary>The world manip-handle toggle every entity row carries; the
    /// handle state is read live, like the overlay eyes.</summary>
    public Action<ShellSidebarRow>? OnHandleToggle;
    public Func<ShellSidebarRow, bool>? IsHandleShown;
    /// <summary>A section header's plus, told the section index and that
    /// plus button's own bottom-left screen position — the anchor
    /// <see cref="OnBurger"/> documents.</summary>
    public Action<int, Vector2>? OnSectionPlus;
    /// <summary>A click on a <see cref="ShellSidebarSection.Selectable"/>
    /// header, told the section index.</summary>
    public Action<int>? OnSectionSelected;

    // Hoisted once per model (the PoseLibraryView convention): the frame's
    // chrome must not mint a closure, and every one of these closes over
    // nothing but this model. AppShellView assigns them on first draw.
    internal Action<int>? TabChosen;
    internal Action<int>? GizmoOperationChosen;
    internal Action<int>? GizmoSpaceChosen;
    internal Action<int>? RotationPivotChosen;
    internal Func<int, bool>? RotationPivotDisabled;
    internal Action<int>? SymmetryChosen;
    internal Action<bool>? AnimationToggled;
    internal Action<bool>? PhysicsToggled;
    internal Action? CollapseToggled;
    internal Action<Crystarium.ActionBarScope>? WorkspaceRightActions;
}

/// <summary>
/// The "Studio" shell, drawn per frame: the window chassis, the titlebar and
/// its control clusters, the sidebar chassis and status bar, the workspace
/// toolbar, the content viewport and the inspector rail.
///
/// <para>The sidebar's search field and tree are owned by <see
/// cref="ShellSidebar"/> behind its own cache. The shell seats it and
/// keeps everything around it: chassis, rules, status bar, resize strip.</para>
///
/// <para>Chrome draws one shell blur, one ground coat per pixel, and one edge.
/// Columns add their translucent grounds and the final edge is drawn last.</para>
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

    /// <summary>The tab strip reads all of the array, so the buffer is exactly
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

    /// <summary>The titlebar plus's press, read back the same way and for the
    /// same reason: the seat's anchor is a local, and a warm titlebar frame
    /// must not mint a closure to carry it.</summary>
    private static bool _spawnPressed;
    private static readonly Action SpawnPressed =
        static () => _spawnPressed = true;

    /// <summary>Shared glass fill for sidebar and rail panels.</summary>
    private static Vector4 Glass =>
        Crystarium.FloatingSurface.FillColor;

    /// <summary>The workspace ground. The same glass — same alpha, same blur
    /// behind it — mixed over the app ground instead of over the panels' raised
    /// surface, because the content sits below the panels in the ladder.
    /// <see cref="Theme.Surface"/> is picto --color-bg-app, the rung under
    /// SurfaceRaised. SurfaceSunken is reserved for input wells: picto's surface-2 is
    /// brighter than surface-1 — an input well, not a ground.</summary>
    private static Vector4 WellGlass =>
        Crystarium.ActiveTheme.Surface with { W = Glass.W };
    private static Vector4 BorderPrimary =>
        Crystarium.ActiveTheme.Chrome.ControlBorder;
    private static Vector4 BorderSecondary =>
        Crystarium.ActiveTheme.FormSeparator;

    /// <summary>Shared height for titlebar and modal bars.</summary>
    public static float TitlebarHeight =>
        Crystarium.ActiveTheme.Floating.ModalBarHeight;

    /// <inheritdoc cref="TitlebarHeight"/>
    public static float CollapsedBarHeight => TitlebarHeight;
    public static float SidebarWidth => Crystarium.ActiveTheme.Shell.SidebarDefaultWidth;
    public static float RowHeight => Crystarium.ActiveTheme.Controls.ListRowHeight;
    public static float ToolbarHeight => Crystarium.ActiveTheme.Shell.ToolbarHeight;
    public static float StatusbarHeight => Crystarium.ActiveTheme.Shell.StatusbarHeight;

    /// <summary>The sidebar's whole footer: the world-class band over the
    /// status band. Two bands of the one height, so a glyph sits in its band
    /// exactly as the live dot sits in the one below.</summary>
    public static float FooterHeight => StatusbarHeight * 2f;
    public static float ScrollbarWidth => Crystarium.ActiveTheme.Scrollbar.GutterWidth;
    public static float ScrollbarRadius => Crystarium.ActiveTheme.Scrollbar.Radius;
    public static float MainHorizontalPadding => Crystarium.ActiveTheme.Page.Inset;
    public static float RailWidth => Crystarium.ActiveTheme.Shell.RailWidth;

    /// <summary>Hoists the model-forwarding callbacks once per model, per the
    /// codebase's own idiom (PoseLibraryView, SpawnBrowserView, ShellSidebar):
    /// a warm chrome frame must not mint a closure. Each closes over nothing
    /// but the model and reads its state at invoke time.</summary>
    private static void EnsureHoisted(AppShellViewModel vm)
    {
        vm.TabChosen ??= chosen => vm.OnTab?.Invoke(chosen);
        vm.GizmoOperationChosen ??= index => vm.OnGizmoOperation?.Invoke(index);
        vm.GizmoSpaceChosen ??= index => vm.OnGizmoSpace?.Invoke(index);
        vm.RotationPivotChosen ??= index => vm.OnRotationPivot?.Invoke(index);
        vm.RotationPivotDisabled ??= index => !vm.RotationPivotEnabled
            || (index == 1 && !vm.RotationPivotParentAvailable);
        vm.SymmetryChosen ??= index => vm.OnSymmetry?.Invoke(index);
        vm.AnimationToggled ??= next => vm.OnAnimation?.Invoke(next);
        vm.PhysicsToggled ??= next => vm.OnPhysics?.Invoke(next);
        vm.CollapseToggled ??= () => vm.OnCollapse?.Invoke(!vm.Collapsed);
        vm.WorkspaceRightActions ??= right =>
        {
            // Omit the switch on an entity with no animation: a switch
            // that can never be thrown is chrome pretending to be a control.
            // Physics has no such gate — one global patch, always live — so
            // it holds the bar's trailing slot alone whenever animation
            // cannot be shown.
            if (vm.AnimationAvailable)
                right.Switch(
                    "Animation",
                    vm.AnimationOn,
                    vm.AnimationToggled!,
                    vm.AnimationOn
                        ? "Switch off to pause this actor's animation"
                        : "Switch on to resume this actor's animation");
            right.Switch(
                "Physics",
                vm.PhysicsOn,
                vm.PhysicsToggled!,
                vm.PhysicsOn
                    ? "Switch off to freeze physics for the whole scene"
                    : "Switch on to resume physics for the whole scene");
            // World adoption belongs to the scene tree, not this toolbar.
        };
    }

    public static void Draw(AppShellViewModel vm, Vector2 origin, Vector2 size)
    {
        ArgumentNullException.ThrowIfNull(vm);
        EnsureHoisted(vm);
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

            // Draw the shared blur and elevation once; each column supplies
            // its own translucent ground and the final edge.
            Crystarium.FloatingSurface.DrawChrome(
                dl, min, max, radius, fill: false, border: false);

            // The workspace ground uses the same glass treatment over the
            // darker app surface, separate from raised sidebar and rail cells.
            //
            // In collapsed mode the titlebar is the complete workspace band.
            if (vm.Collapsed)
            {
                dl.AddRectFilled(min, max, U32(WellGlass), radius * s);
            }
            else
            {
                float wellLeft = vm.Detached
                    ? 0f
                    : vm.SidebarWidthPx * s;
                float wellRight = vm.RailShown ? RailWidth * s : 0f;
                // Only the window's own corners round; an edge that meets a
                // panel is square. The radius is dropped along with them, so
                // the flags cannot fall through to ImGui's round-everything
                // default.
                ImDrawFlags corners = 0;
                if (wellLeft <= 0f)
                    corners |= ImDrawFlags.RoundCornersTopLeft
                        | ImDrawFlags.RoundCornersBottomLeft;
                if (wellRight <= 0f)
                    corners |= ImDrawFlags.RoundCornersTopRight
                        | ImDrawFlags.RoundCornersBottomRight;
                dl.AddRectFilled(
                    new Vector2(min.X + wellLeft, min.Y),
                    new Vector2(max.X - wellRight, max.Y),
                    U32(WellGlass),
                    corners == 0 ? 0f : radius * s,
                    corners);
            }

            SyncKeybindHelp();
            DrawTitlebar(vm, min, max, s, dl);

            // Double-clicking the bar's open band collapses — the chevron's
            // gesture twin. Every bar item was submitted by the call above,
            // so a hovered button keeps its own clicks.
            if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left)
                && !ImGui.IsAnyItemHovered())
            {
                var barMouse = ImGui.GetMousePos();
                if (barMouse.X >= min.X && barMouse.X < max.X
                    && barMouse.Y >= min.Y
                    && barMouse.Y < min.Y + TitlebarHeight * s)
                    vm.CollapseToggled?.Invoke();
            }

            if (vm.Collapsed)
            {
                DrawOuterGlassBorder(min, max);
                return; // titlebar strip only
            }

            float bodyTop = min.Y + TitlebarHeight * s;
            float railW = vm.RailShown ? RailWidth * s : 0f;
            // Detached mode: the sidebar is its own window; the content and
            // the inspector stay together here. Collapsed, the column folds
            // away and the well takes its width.
            float sbw = vm.Detached || vm.SidebarCollapsed
                ? 0f
                : vm.SidebarWidthPx * s;

            if (!vm.Detached && !vm.SidebarCollapsed)
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

            if (!vm.Detached && !vm.SidebarCollapsed)
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
            vm.RailShown && !vm.Collapsed ? RailWidth * s : 0f;

        // Each column lays its panel ground once and stops at its own edge;
        // the workspace ground remains visible between columns.
        if (!vm.Collapsed && !vm.Detached && cellWidth > 0f)
        {
            var cellMax = new Vector2(min.X + cellWidth, min.Y + height);
            dl.AddRectFilled(
                min, cellMax, U32(Glass), radius, ImDrawFlags.RoundCornersTopLeft);
            dl.AddRectFilled(
                new Vector2(cellMax.X - rule, min.Y), cellMax, U32(BorderPrimary));
        }
        if (!vm.Collapsed && railWidth > 0f)
        {
            // With the rail present the right cluster stands on a cell
            // continuous with the rail below it (shell rule) — the same panel
            // ground the sidebar's cell wears, so the two columns framing the
            // well are one material and one glass.
            var railMin = new Vector2(max.X - railWidth, min.Y);
            dl.AddRectFilled(
                railMin,
                new Vector2(max.X, min.Y + height),
                U32(Glass),
                radius,
                ImDrawFlags.RoundCornersTopRight);
            dl.AddRectFilled(
                railMin,
                new Vector2(railMin.X + rule, min.Y + height),
                U32(BorderPrimary));
        }

        if (vm.Detached)
        {
            // The detached main window is the properties window — an
            // INTERNAL name: the user just sees the name of whatever they
            // have selected. The rail below is the inspector.
            string title = vm.TitleEntity == "Poser"
                ? "Inspector"
                : vm.TitleEntity;
            // The title stands on the content column's own inset, so the
            // window's left side reads as one aligned edge: title, tab
            // strips and content.
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
            // The pill stays on the toolbar window — the cell carries no
            // duplicate of anything the toolbar already states. The
            // sidebar itself never hides: no chevron, no collapse.
            float brandEnd = DrawBrandPill(
                vm, min.X + TitleInset * s, min.Y, height, s, dl,
                pill: false);
            // The burger LEFT-aligns by the brand; the Library text
            // button keeps the cell's right.
            float burgerSide = theme.Controls.ShellIconAction;
            float burgerX = brandEnd + theme.Spacing.Four * s;
            float burgerY = min.Y + (height - burgerSide * s) * 0.5f;
            IconAt(
                new Vector2(burgerX, burgerY),
                TablerIcon.Menu2, burgerSide, BurgerPressed,
                "##shell-burger",
                help: "Actions");
            if (_burgerPressed)
            {
                _burgerPressed = false;
                vm.OnBurger?.Invoke(
                    new Vector2(burgerX, burgerY + burgerSide * s));
            }
            // The title cell's content stops at the divider's x whether or
            // not the divider paints this state: collapse must not shift
            // the cluster by the rule's pixel.
            DrawCellActions(
                vm,
                min.X + cellWidth - rule - TitleActionInset * s,
                min.Y,
                height,
                s);
            // The gizmo cluster lives on the TOOLBAR window — always its
            // own window — never in this titlebar.
        }
        DrawTitleActions(vm, max.X, min.Y, height, s);

        // The CONTENT selector lives in the TITLEBAR, beside the window
        // action icons and measured against their cluster: Target shows
        // the selection's tabs; Environment and Scene swap the content
        // side. The inspector below never swaps — it is only ever the
        // selected object.
        if (!vm.Collapsed && vm.OnInspectorMode is { } onMode)
        {
            // The first segment IS the selection's kind — Actor, Object,
            // Camera… — at a FIXED width sized to the widest kind name,
            // so a selection change never moves a pixel of this band.
            string kind = vm.ContentKind.Length > 0
                ? vm.ContentKind
                : "Target";
            string[] modes = [kind, "Environment", "Scene"];
            float kindSlot = WidestKindWidth() ;
            var segSize = Crystarium.MeasureSegmentedControl(modes);
            float pillPadding = theme.Spacing.Six * s;
            float fixedWidth = segSize.X
                - (Crystarium.MeasureText(
                        kind, KindMeasureStyle).X + pillPadding * 2f)
                + kindSlot + pillPadding * 2f;
            // The selector docks on the CONTENT side of the divider
            // between the content and the inspector — it swaps the
            // content, so it stands over what it governs.
            float railEdge = vm.RailShown && !vm.Collapsed
                ? RailWidth * s
                : 0f;
            ImGui.SetCursorScreenPos(new Vector2(
                max.X - railEdge - theme.Page.ActionGap * s - fixedWidth,
                min.Y + (height - segSize.Y) * 0.5f));
            Crystarium.SegmentedControl(
                "##content-mode",
                modes,
                vm.InspectorMode,
                onMode,
                itemWidth: index => index == 0
                    ? kindSlot + pillPadding * 2f
                    : Crystarium.MeasureText(
                        modes[index], KindMeasureStyle).X + pillPadding * 2f,
                itemHelp: index => index switch
                {
                    0 => KindHelp(kind),
                    1 => "Edit the environment",
                    2 => "Save and load the scene",
                    _ => null,
                });
        }
    }

    /// <summary>"Poser" and the GPose pill, drawn at <paramref name="x"/> in
    /// a band of <paramref name="height"/>; returns the x past them. One
    /// renderer for the toolbar's two hosts — the titlebar centre and the
    /// floating toolbar window.</summary>
    private static float DrawBrandPill(
        AppShellViewModel vm, float x, float top, float height, float s,
        ImDrawListPtr dl, bool pill = true)
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
        if (!pill || !vm.GPoseActive)
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

    /// <summary>The Library TEXT button alone, right-aligned in the title
    /// cell — the burger left-aligns by the brand. Nothing else: undo,
    /// redo, spawn and the GPose pill live on the toolbar window, and the
    /// cell never duplicates the toolbar.</summary>
    private static void DrawCellActions(
        AppShellViewModel vm, float right, float top, float height, float s)
    {
        var theme = Crystarium.ActiveTheme;
        float side = theme.Controls.ShellIconAction;
        float y = top + (height - side * s) * 0.5f;
        float x = right;
        if (vm.OnLibrary is { } onLibrary)
        {
            var labelStyle = new TextStyle
            { Size = theme.Typography.LabelSize };
            float labelWidth = Crystarium.MeasureText(
                "Library", labelStyle).X;
            float buttonWidth = labelWidth / s + theme.Spacing.Six * 2f;
            x -= buttonWidth * s;
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            Crystarium.Button(
                "Library",
                onLibrary,
                style: ControlStyle.Square(side) with
                { Width = UiWidth.Fixed(buttonWidth) },
                help: "Open the library",
                id: "##shell-library");
            x -= theme.Page.ActionGap * s;
        }
        // The sidebar's own fold: the COLUMN goes, this cell stays — the
        // brand, burger, library and this chevron keep their seats.
        if (vm.OnSidebarCollapse is { } onSidebarCollapse)
        {
            x -= side * s;
            IconAt(
                new Vector2(x, y),
                TablerIcon.ChevronRight,
                side,
                () => onSidebarCollapse(!vm.SidebarCollapsed),
                "##shell-sidebar-fold",
                flipX: !vm.SidebarCollapsed,
                help: vm.SidebarCollapsed
                    ? "Show the scene tree"
                    : "Fold the scene tree away");
        }
    }


    /// <summary>The four segment groups — gizmo operation, space, pivot,
    /// symmetry — drawn once per frame from exactly one host: the titlebar
    /// centre, or the floating toolbar when the toolbar is split. One set of
    /// ids, so hover and motion state survive the move between hosts.</summary>
    private static void DrawGizmoCluster(
        AppShellViewModel vm, float x, float top, float height, float s)
    {
        float gap = Crystarium.ActiveTheme.Page.ActionGap * s;
        x = Segments(
            x, top, height,
            "##shell-gizmo-operation",
            GizmoIcons,
            vm.GizmoOperation,
            vm.GizmoOperationChosen!,
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
            vm.GizmoSpaceChosen!,
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
            vm.RotationPivotChosen!,
            itemDisabled: vm.RotationPivotDisabled,
            itemHelp: static index => index == 0
                ? "Rotate each selected bone in place"
                : "Rotate the selected bone around its parent bone") + gap;
        Segments(
            x, top, height,
            "##shell-symmetry",
            SymmetryItems,
            vm.SymmetryMode,
            vm.SymmetryChosen!,
            itemHelp: static index => index switch
            {
                0 => "Edit only the current selection",
                1 => "Also apply the same edit to the opposite-side bone",
                _ => "Also apply a mirrored edit to the opposite-side bone",
            });
        // Overlay visibility is configured in Settings and per-actor sidebar
        // controls; no extra controls follow the gizmo segments.
    }

    /// <summary>Rightmost is the collapse chevron, then the close X.
    /// </summary>
    /// <summary>The kind segment's hover — the same verb-first shape as
    /// its siblings, minted per kind so no frame formats one.</summary>
    private static string KindHelp(string kind) => kind switch
    {
        "Actor" => "Edit the actor",
        "Object" => "Edit the object",
        "Camera" => "Edit the camera",
        "Light" => "Edit the light",
        "Overlay" => "Edit the overlay",
        _ => "Edit the selection",
    };

    /// <summary>The label style the selector's segments measure with —
    /// the pill's own face.</summary>
    private static TextStyle KindMeasureStyle => new()
    { Size = Crystarium.ActiveTheme.Typography.LabelSize };

    /// <summary>The widest kind name the selector's first segment can
    /// carry — its FIXED slot, so selection changes never move it.</summary>
    private static float WidestKindWidth()
    {
        float widest = 0f;
        foreach (var kind in (ReadOnlySpan<string>)
            ["Target", "Actor", "Object", "Camera", "Light", "Overlay",
                "Selection"])
            widest = MathF.Max(
                widest, Crystarium.MeasureText(kind, KindMeasureStyle).X);
        return widest;
    }

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
            vm.CollapseToggled,
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
        // The inspector's own fold — absent while the rail lives in its
        // own window, which manages itself.
        if (vm.DrawRail != null && !vm.InspectorSplit
            && vm.OnInspectorCollapse is { } onInspectorCollapse)
        {
            x -= step;
            IconAt(
                new Vector2(x, y),
                TablerIcon.ChevronRight,
                side,
                () => onInspectorCollapse(!vm.InspectorCollapsed),
                "##shell-inspector-fold",
                flipX: vm.InspectorCollapsed,
                help: vm.InspectorCollapsed
                    ? "Show the inspector"
                    : "Fold the inspector away");
        }
        // The pop-out remains available from the titlebar toolbar.
        if (vm.ShowPopOut)
        {
            x -= step;
            IconAt(
                new Vector2(x, y), TablerIcon.ExternalLink, side, vm.OnPopOut,
                "##shell-popout",
                help: "Pop the selected actor's content into its own window");
        }
        // Armature visibility is controlled by the sidebar and settings, not
        // by this titlebar cluster.
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
        float footerTop = max.Y - FooterHeight * s;
        // The search band and the tree; the chassis, the divider rule and the
        // footer are the shell's.
        Sidebar.Draw(
            vm,
            min,
            new Vector2(
                max.X - min.X,
                footerTop - theme.Spacing.One * s - min.Y));

        dl.AddRectFilled(
            new Vector2(min.X, footerTop),
            new Vector2(bodyRight, footerTop + rule),
            U32(BorderSecondary));
        DrawFooter(
            vm, new Vector2(min.X, footerTop + rule),
            new Vector2(bodyRight, max.Y), s, dl);
    }

    /// <summary>The sidebar's footer: the world-class glyph band, then the
    /// status band — actor count and frame rate.</summary>
    private static void DrawFooter(
        AppShellViewModel vm, Vector2 min, Vector2 max, float s, ImDrawListPtr dl)
    {
        float split = max.Y - StatusbarHeight * s;
        DrawWorldClasses(vm, min, new Vector2(max.X, split), s);
        DrawStatusbar(vm, new Vector2(min.X, split), max, s, dl);
    }

    /// <summary>The world-adoption classes as lit-or-faded glyphs, on the
    /// status band's own left inset. Faded is off — the one engaged/faded
    /// language every action glyph in the shell speaks.</summary>
    private static void DrawWorldClasses(
        AppShellViewModel vm, Vector2 min, Vector2 max, float s)
    {
        var theme = Crystarium.ActiveTheme;
        float side = theme.Controls.SwitchHeight;
        float step = (side + theme.Page.ActionGap) * s;
        float y = min.Y + (max.Y - min.Y - side * s) * 0.5f;
        float x = min.X + StatusInset * s;
        for (int i = 0; i < vm.WorldClasses.Count; i++)
        {
            var entry = vm.WorldClasses[i];
            ImGui.SetCursorScreenPos(new Vector2(x, y));
            // Captured per glyph: the callback outlives this loop iteration.
            int index = i;
            if (Crystarium.TemporaryIconToggle(
                    entry.Icon,
                    selected: false,
                    style: ControlStyle.Square(side),
                    help: entry.On ? entry.HideHelp : entry.ShowHelp,
                    id: entry.Id,
                    dimmed: !entry.On))
                vm.OnWorldClassToggle?.Invoke(index);
            x += step;
        }
        // The spawn plus lives beside the sidebar's search now — this band
        // is the adopt glyphs' alone.
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

        if (!string.IsNullOrEmpty(vm.BranchLabel))
        {
            float rightEdge = max.X - StatusInset * s - rightWidth - StatusTextGap * s;
            float leftEdge = dotMin.X + dot + StatusTextGap * s + leftWidth + StatusTextGap * s;
            float available = MathF.Max(0f, rightEdge - leftEdge);
            if (!(available > 0f))
                return;
            var branchStyle = style with { Color = theme.Accent };
            string? fitted = Crystarium.FitTruncated(vm.BranchLabel, branchStyle, available);
            string shown = fitted ?? vm.BranchLabel;
            float shownWidth = fitted is null ? Crystarium.MeasureText(shown, branchStyle).X : available;
            var branchMin = new Vector2(rightEdge - shownWidth, min.Y);
            ImGui.SetCursorScreenPos(branchMin);
            ImGui.InvisibleButton("##build-branch", new Vector2(shownWidth, height));
            bool hovered = ImGui.IsItemHovered();
            ImGui.SetCursorScreenPos(branchMin);
            Crystarium.TextInBand(
                branchMin, new Vector2(shownWidth, height), shown, branchStyle,
                fitted is null ? TextConstraint.Intrinsic : TextConstraint.Truncate(shownWidth));
            if (hovered && fitted is not null)
                Crystarium.HoverHelp.Preview(
                    "build-branch", branchMin,
                    branchMin + new Vector2(shownWidth, height), vm.BranchLabel);
        }
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
            // The tab strip uses the shared segmented pill every other mode
            // selector uses, not hand-drawn buttons; alignFirstTabToCursor
            // lands the first tab's label on the content inset, because the
            // pill's dark chrome is decoration and not padding.
            var size = Crystarium.MeasureSegmentedControl(_tabLabels);
            ImGui.SetCursorScreenPos(new Vector2(
                min.X + inset,
                min.Y + (ToolbarHeight * s - size.Y) * 0.5f));
            Crystarium.SegmentedControl(
                "##shell-tabs",
                _tabLabels,
                _tabActive,
                vm.TabChosen!,
                alignFirstTabToCursor: true);
        }

        // Actor physics occupies one stable right-aligned slot on every
        // workspace tab: a tab change never replaces it with selection text and
        // never moves it.
        Crystarium.ActionBar(
            "shell-workspace-actions",
            new Vector2(min.X + inset, min.Y),
            new Vector2(max.X - min.X - inset * 2f, ToolbarHeight * s),
            static _ => { },
            vm.WorkspaceRightActions,
            ActionBarSeparator.None);


        DrawContentViewport(vm, min, max, s);
    }

    /// <summary>
    /// The hosting seam: the viewport child and the page scroll own the gutter
    /// and the extent bookkeeping, and the active pane's own root renders inside
    /// them — exactly as the Settings page is hosted.
    /// </summary>
    private static void DrawContentViewport(
        AppShellViewModel vm, Vector2 min, Vector2 max, float s)
    {
        float toolbarBottom = min.Y + ToolbarHeight * s;
        // The child stops one border pixel short of shell-owned edges. In
        // detached mode its left edge is also shell-owned.
        float leftEdge = vm.Detached ? 1f * s : 0f;
        // Toolbar and content share the horizontal inset; the shell owns the
        // origin while panes own their internal spacing.
        var childOrigin = new Vector2(min.X + leftEdge, toolbarBottom);
        var childSize = new Vector2(
            max.X - min.X - 1f * s - leftEdge,
            max.Y - toolbarBottom - 1f * s);
        // Measure the inset from the child so the border and scrollbar remain
        // outside the pane's content box.
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
                    vm.ContentScrollId,
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
        // The panel ground, once — the same coat the sidebar's chassis wears
        // and the same one this rail's titlebar cell wears, so the column is
        // one glass from the bar to the window's bottom.
        dl.AddRectFilled(
            railMin, max, U32(Glass), theme.Radii.Window * s,
            ImDrawFlags.RoundCornersBottomRight);
        dl.AddRectFilled(
            railMin, new Vector2(railMin.X + 1f * s, max.Y), U32(BorderPrimary));

        RailScrollSeam(vm, railMin, max, railWidth, s);
    }

    /// <summary>The rail's scroll seam and content invocation, shared by the
    /// attached rail and the floating inspector window. The chassis around it
    /// is each host's own.</summary>
    /// <summary>The split Inspector window's content: the same seam the
    /// attached rail draws, inside a host-owned chassis.</summary>
    internal static void DrawRailContent(
        AppShellViewModel vm, Vector2 min, Vector2 max)
    {
        float s = ImGuiHelpers.GlobalScale;
        RailScrollSeam(vm, min, max, (max.X - min.X) / s, s);
    }

    private static void RailScrollSeam(
        AppShellViewModel vm, Vector2 railMin, Vector2 max,
        float railWidth, float s)
    {
        var theme = Crystarium.ActiveTheme;
        ImGui.SetCursorScreenPos(railMin + new Vector2(0f, 12f * s));
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
        float footerTop = max.Y - FooterHeight * s;
        Sidebar.Draw(
            vm,
            min,
            new Vector2(
                max.X - min.X,
                footerTop - theme.Spacing.One * s - min.Y));
        dl.AddRectFilled(
            new Vector2(min.X, footerTop),
            new Vector2(max.X, footerTop + rule),
            U32(BorderSecondary));
        DrawFooter(
            vm, new Vector2(min.X, footerTop + rule), max, s, dl);
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

    /// <summary>
    /// The tooltip for one history button. The generic line is the fallback,
    /// not the answer: when the stack knows what its top entry is, the button
    /// says so, because "the last move, rotation or scale" is simply untrue of
    /// a pose import or a spawn.
    ///
    /// <para>Descriptions are authored as sentence openers ("Transform 2
    /// bones", "Mirror edits"), so the leading capital is dropped to let them
    /// sit inside the verb rather than after it.</para>
    /// </summary>
    private static string HistoryHelp(
        bool available,
        string? description,
        string verb,
        string shortcut,
        string generic,
        string empty)
    {
        if (!available)
            return empty;
        if (string.IsNullOrWhiteSpace(description))
            return generic;
        string named = char.IsUpper(description[0])
            ? char.ToLowerInvariant(description[0]) + description[1..]
            : description;
        return $"{verb} {named} · {shortcut}";
    }

    /// <summary>Cancels an in-progress numeric axis edit, for example when selection changes.</summary>
    public static void CancelAxisEdit()
    {
        Crystarium.CancelAxisEdit();
    }

    // ── the split shell's standalone parts ───────────────────────────────
    // Each part draws with the same retained state and ids it has inside the
    // shell — the sidebar cache, the segment motion channels, the keybind
    // help — so splitting a part moves it without resetting it. Exactly one
    // host draws a part per frame; the split flags are that gate.

    /// <summary>The floating toolbar's content: the brand and its GPose
    /// pill, the command menu, undo/redo, then the same four segment groups
    /// the titlebar centre hosts when attached. The spawn plus stays with
    /// the scene window.</summary>
    public static void DrawToolbarContent(
        AppShellViewModel vm, Vector2 origin, float height)
    {
        EnsureHoisted(vm);
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
            help: HistoryHelp(
                vm.CanUndo, vm.UndoDescription, "Undo", _undoShortcut,
                _undoHelp, _undoEmptyHelp));
        x += step;
        IconAt(
            new Vector2(x, y), TablerIcon.ArrowBackUp, side, vm.OnRedo,
            "##shell-redo",
            disabled: !vm.CanRedo,
            flipX: true,
            help: HistoryHelp(
                vm.CanRedo, vm.RedoDescription, "Redo", _redoShortcut,
                _redoHelp, _redoEmptyHelp));
        x += step;
        if (vm.ShowSpawn)
        {
            IconAt(
                new Vector2(x, y), TablerIcon.Plus, side, SpawnPressed,
                "##shell-spawn",
                help: "Add an actor or object to the scene");
            if (_spawnPressed)
            {
                _spawnPressed = false;
                vm.OnSpawn?.Invoke(new Vector2(x, y + side * s));
            }
            x += step;
        }
        if (vm.ShowProject)
        {
            IconAt(
                new Vector2(x, y), TablerIcon.Folder, side, vm.OnProject,
                "##shell-project",
                help: "Open the scene project browser");
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
        // Burger, undo, redo, then spawn and project when shown.
        float icons = step * (3f
            + (vm.ShowSpawn ? 1f : 0f)
            + (vm.ShowProject ? 1f : 0f));
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
