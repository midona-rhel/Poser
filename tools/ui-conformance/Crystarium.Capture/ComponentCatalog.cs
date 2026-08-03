using System.Numerics;
using Dalamud.Bindings.ImGui;
using Poser.UI;
using Dalamud.Interface.Utility;
using FontFamily = Poser.UI.FontFamily;
// System.Windows.Forms and Poser.UI both export these names; the harness
// hosts a WinForms window, so the control types are named explicitly.
using Button = Poser.UI.Button;
using Label = Poser.UI.Label;
using Ui = Poser.UI.LegacyCrystarium;
using Rx = Poser.UI.Crystarium;
using RxRoot = Poser.UI.UiRoot;
using UiDim = Poser.UI.UiDim;

namespace Crystarium.Capture;

/// <summary>Hidden specs are candidate-only invariants (no Picto parity
/// reference exists); they render on demand but stay out of the
/// conformance catalog listing.</summary>
internal readonly record struct ComponentSpec(
    string Name,
    int Width,
    int Height,
    bool Hidden = false);

internal static class ComponentCatalog
{
    private static readonly ComponentSpec[] Specs =
    [
        new("text-label", 320, 44),
        new("text-caption", 320, 44),
        new("text-mono", 320, 44),
        new("text-disabled", 320, 74),
        new("text-truncated", 320, 44),
        new("text-truncated-cjk", 320, 44),
        new("text-truncated-combining", 320, 44),
        new("text-truncated-emoji", 320, 44),
        new("text-truncated-fit", 320, 44),
        new("text-truncated-narrow", 320, 44),
        new("text-truncated-flow", 320, 44),
        new("text-wrapped", 320, 96),
        new("text-wrapped-newline", 320, 130),
        new("text-wrapped-overwide", 320, 100),
        new("text-wrapped-flow", 320, 130),
        new("text-ws-collapse", 320, 80),
        new("text-ws-prewrap", 320, 110),
        new("text-ws-tab", 320, 74),
        new("text-ws-crlf", 320, 130),
        new("text-align-end", 320, 88),
        new("icons-grid-16", 232, 256),
        new("icons-grid-14", 216, 238),
        new("icons-states", 136, 184),
        new("btn-secondary", 320, 80),
        new("btn-secondary-hover", 320, 80),
        new("btn-secondary-disabled", 320, 80),
        new("btn-disabled-unicode", 320, 80),
        new("btn-primary", 320, 80),
        new("btn-primary-hover", 320, 80),
        new("btn-primary-disabled", 320, 80),
        new("btn-danger", 320, 80),
        new("btn-danger-hover", 320, 80),
        new("btn-danger-disabled", 320, 80),
        new("btn-width-content", 320, 80),
        new("btn-width-fixed", 320, 80),
        new("btn-width-fill", 320, 80),
        new("btn-narrow", 320, 80, Hidden: true),
        new("btn-narrow-blank", 320, 80, Hidden: true),
        new("btn-narrow-unclipped", 320, 80, Hidden: true),
        new("btn-hover-reconcile", 320, 80, Hidden: true),
        new("bar-allocation", 340, 140, Hidden: true),
        new("btn-hover-exit", 320, 80),
        new("btn-hover-mid", 320, 80),
        // PBI-015: the SAME states driven through the retained reactive
        // root. Every rbtn-X must capture byte-identical to btn-X, which
        // is what makes the declarative path the same button rather than
        // a lookalike — see verify-reactive-button.ps1.
        new("rbtn-secondary", 320, 80),
        new("rbtn-secondary-hover", 320, 80),
        new("rbtn-secondary-disabled", 320, 80),
        new("rbtn-disabled-unicode", 320, 80),
        new("rbtn-primary", 320, 80),
        new("rbtn-primary-hover", 320, 80),
        new("rbtn-primary-disabled", 320, 80),
        new("rbtn-danger", 320, 80),
        new("rbtn-danger-hover", 320, 80),
        new("rbtn-danger-disabled", 320, 80),
        new("rbtn-width-content", 320, 80),
        new("rbtn-width-fixed", 320, 80),
        new("rbtn-width-fill", 320, 80),
        new("rbtn-hover-exit", 320, 80),
        new("rbtn-hover-mid", 320, 80),
        new("rbtn-hover-reconcile", 320, 80, Hidden: true),
        new("icon-button-idle", 120, 80),
        new("icon-button-hover", 120, 80),
        new("icon-button-pressed", 120, 80),
        new("icon-button-held-outside", 120, 80),
        new("icon-button-disabled", 120, 80),
        new("icon-button-hover-mid", 120, 80),
        new("icon-button-hover-exit", 120, 80),
        new("icon-button-glyphs", 280, 80),
        new("icon-button-explicit-size", 120, 88, Hidden: true),
        new("icon-button-hover-reconcile", 120, 80, Hidden: true),
        new("icon-button-backdrop-surface", 160, 80, Hidden: true),
        new("icon-button-backdrop-raised", 160, 80, Hidden: true),
        new("icon-button-backdrop-checker", 160, 80, Hidden: true),
        new("switch-off", 120, 80),
        new("switch-on", 120, 80),
        // PBI-015 wave L: the legacy comparison states for the controls
        // Appearance consumes, so the reactive twins land on a byte-gate
        // instead of on nothing.
        new("slider", 320, 80),
        new("slider-disabled", 320, 80),
        new("colorwell", 320, 80),
        new("colorwell-disabled", 320, 80),
        new("progress", 320, 80),
        // PBI-015 wave P: the SAME five form-control states plus the two
        // switch states driven through the retained reactive root. Every
        // rX must capture byte-identical to its legacy twin — the twins
        // share the wave-M paint seam, so a differing byte is the retained
        // runtime's own. See verify-reactive-form.ps1.
        new("rslider", 320, 80),
        new("rslider-disabled", 320, 80),
        new("rcolorwell", 320, 80),
        new("rcolorwell-disabled", 320, 80),
        new("rprogress", 320, 80),
        new("rswitch-off", 120, 80),
        new("rswitch-on", 120, 80),
        new("text-input", 320, 80),
        new("input-placeholder", 320, 80),
        new("search-input", 320, 84),
        new("search-clear-hover", 320, 84),
        new("dropdown-closed", 320, 80),
        new("dropdown-open", 320, 280),
        // PBI-015 wave H: the SAME two dropdown states driven through the
        // retained reactive root. rdd-X must capture byte-identical to
        // dropdown-X — see verify-reactive-dropdown.ps1.
        new("rdd-closed", 320, 80),
        new("rdd-open", 320, 280),
        // PBI-015 wave K: the same two paths with a GENUINELY SCROLLED list —
        // ten items past the seven-row viewport, wheeled down by real
        // ImGuiIO wheel events. Hidden: the two are compared against each
        // other, not against a Picto reference.
        new("dd-scrolled", 320, 280, Hidden: true),
        new("rdd-scrolled", 320, 280, Hidden: true),
        // PBI-015 wave O: the retained picker is a REDESIGN, not a twin —
        // the surface box is unchanged (so one reference cell judges both)
        // and everything inside it is OverlayShell. Judged against the
        // Picto reference.
        new("rpicker-open", 320, 280),
        new("rpicker-multi", 320, 280),
        // PBI-016: the same two scenes through the IMPERATIVE SearchPicker.
        // The golden is the reactive capture above, so the two are compared
        // byte-for-byte, not by eye.
        new("i-picker-open", 320, 280),
        new("i-picker-multi", 320, 280),
        new("color-palette", 220, 80),
        new("sidebar-row", 320, 80),
        new("sidebar-row-hover", 320, 80),
        new("sidebar-row-selected", 320, 80),
        new("sidebar-row-selected-hover", 320, 80),
        new("sidebar-row-collapsed", 320, 80),
        new("sidebar-row-expanded", 320, 80),
        new("sidebar-row-expander-hover", 320, 80),
        new("sidebar-row-drop", 320, 80),
        new("property-row", 320, 68),
        new("section", 320, 92),
        new("section-expanded", 320, 92),
        new("section-hover", 320, 92),
        // PBI-015 wave P: the same three section states through the
        // retained root. Same 272px measure, same choreography, same
        // header paint seam — so byte-identity is the gate here too.
        new("rsection", 320, 92),
        new("rsection-expanded", 320, 92),
        new("rsection-hover", 320, 92),
        // PBI-015 phase 3B: the Settings chassis and its two new controls,
        // renderable where pixels can be measured instead of reported.
        new("rsettings-frame", 770, 570),
        new("rsegmented", 420, 150),
        new("rswatches", 340, 60),
        // PBI-015 titlebar widening: the icon-sized action in every shape the
        // shell asks for — enum glyph, registry name, mirrored glyph, and the
        // persistent toggle both plain and slashed.
        new("ricon-actions", 240, 60),
        new("tooltip", 240, 80),
        new("tooltip-pop-mid", 240, 80),
        new("context-menu", 320, 190),
        new("modal", 560, 360),
        // PBI-015: the in-window scroll container. Twenty 26px rows in a 160px
        // viewport is 520px of content — the overflow is the point, so the
        // capture must show a bar and a cut last row.
        new("rscrollarea", 320, 200),
        // PBI-015: the shell's tree row as a component. Four rows carry every
        // shape the sidebar draws — a root disclosure, a T branch with an
        // action strip, a selected terminal L with a badge, and a collapsed
        // root — so the guides, the chevron, the pill and the badge are all
        // one capture.
        new("rtreerow", 300, 190),
        // PBI-016: the same six rows through the IMPERATIVE TreeRow. The
        // golden is the reactive capture above, so the two are compared
        // byte-for-byte, not by eye.
        new("i-treerow", 300, 190),
        // PBI-015: the file surface's whole chassis — title bar, navigation
        // band, quick rail beside the explorer, footer — driven off an
        // INJECTED listing, so the capture touches no filesystem and is the
        // same picture on every machine.
        new("rfiledialog", 760, 500),
        // PBI-016 phase 4a: the same file surface through the IMPERATIVE
        // dialog. The golden is the reactive capture above, so the two are
        // compared byte-for-byte, not by eye.
        new("i-filedialog", 760, 500),
        // PBI-016: the same Settings frame through the IMPERATIVE
        // WindowFrame. The golden is the reactive capture above, so the two
        // states are compared byte-for-byte, not by eye.
        new("i-settings-frame", 770, 570),
        // PBI-016 phase 1: the three widened controls through their
        // IMPERATIVE entry points. Each golden is the reactive capture of the
        // same name, so these are compared byte-for-byte, not by eye.
        new("i-segmented", 420, 150),
        new("i-swatches", 340, 60),
        new("i-icon-actions", 240, 60),
    ];

    private static readonly string[] DropdownItems =
    [
        "Date Added",
        "Date Created",
        "Date Modified",
        "Name",
        "Rating",
        "File Size",
        "Duration",
    ];

    /// <summary>
    /// A SIBLING of <see cref="DropdownItems"/> rather than an extension of
    /// it: the seven-item fixture's captures are frozen, so the scrolled
    /// states get their own list. Ten items past the seven-row viewport is
    /// 278px of rows in a 194px window — one wheel notch scrolls it far
    /// enough to carry the selected row 0 clear off the top. Every added
    /// name is narrower than "Date Modified", so the trigger and panel keep
    /// the intrinsic width the seven-item fixture resolves.
    /// </summary>
    private static readonly string[] DropdownItemsScrolled =
    [
        "Date Added",
        "Date Created",
        "Date Modified",
        "Name",
        "Rating",
        "File Size",
        "Duration",
        "Type",
        "Author",
        "Extension",
    ];

    /// <summary>Hoisted so the reactive fixture's build closure stays
    /// static: a handler allocated per frame would be the harness's cost,
    /// not the runtime's.</summary>
    private static readonly Action<int> NoOpSelect = static _ => { };

    /// <summary>
    /// The reference cell's active row is index 1, so both picker fixtures
    /// select "Date Created" — the single one by key, the multi one by a
    /// one-element set, which is the whole difference between the two
    /// controlled shapes.
    /// </summary>
    private static readonly HashSet<string> PickerSelected = ["Date Created"];

    private static readonly Action<string> PickerNoOpPick = static _ => { };

    private static readonly Action<string, bool> PickerNoOpToggle =
        static (_, _) => { };

    private static readonly Action PickerNoOpOpen = static () => { };

    private static PickerProps<string> PickerFixtureProps(bool multi) =>
        new(
            "Date Modified",
            // The single-select surface carries NO caption band (product
            // shape); the multi variant keeps its header.
            multi ? "Sort by" : null,
            DropdownItems,
            static item => item,
            static item => item,
            multi ? null : DropdownItems[1],
            multi ? PickerSelected : null,
            null,
            multi ? null : PickerNoOpPick,
            multi ? PickerNoOpToggle : null,
            PickerNoOpOpen,
            Dense: false,
            Disabled: false,
            DisabledHelp: null,
            Multi: multi,
            TriggerWidth: default);

    private static readonly Func<UiNode> PickerSingleTree = static () =>
        Rx.PickerSurface(PickerFixtureProps(false), "fixture");

    private static readonly Func<UiNode> PickerMultiTree = static () =>
        Rx.PickerSurface(PickerFixtureProps(true), "fixture");

    /// <summary>
    /// The wave-P form twins. Every callback and every build closure is
    /// hoisted for the same reason the picker's are: a delegate allocated per
    /// frame would be the harness's cost, and a fixture that differs from its
    /// legacy twin only in what it allocates is not the comparison this sheet
    /// claims to make.
    /// </summary>
    private static readonly Action FormNoOp = static () => { };

    private static readonly Action<int> FormNoOpInt = static _ => { };

    private static readonly Action<float> FormNoOpFloat = static _ => { };

    private static readonly Action<bool> FormNoOpBool = static _ => { };

    private static readonly Action<Vector4> FormNoOpColor = static _ => { };

    /// <summary>Fixed 200px, exactly as the legacy fixture asks: the thumb
    /// centre is then arithmetic rather than a function of the cell.</summary>
    private static readonly Func<UiNode> SliderTree = static () =>
        new Slider
        {
            Value = 0.4f,
            Max = 1f,
            OnChange = FormNoOpFloat,
            StyleSheet = new() { Layout = new() { Width = UiDim.Fixed(200f) } },
        };

    private static readonly Func<UiNode> SliderDisabledTree = static () =>
        new Slider
        {
            Value = 0.4f,
            Max = 1f,
            OnChange = FormNoOpFloat,
            Disabled = true,
            StyleSheet = new() { Layout = new() { Width = UiDim.Fixed(200f) } },
        };

    private static readonly Func<UiNode> ColorWellTree = static () =>
        new ColorWell
        {
            Color = new Vector4(0.8f, 0.3f, 0.2f, 1f),
            OnChange = FormNoOpColor,
        };

    /// <summary>The absent-weapon well: a null tint reaches the twin as
    /// Vector4.Zero with disabled set, which is what makes it paint
    /// UnavailableFill instead of the colour it does not have.</summary>
    private static readonly Func<UiNode> ColorWellDisabledTree = static () =>
        new ColorWell { OnChange = FormNoOpColor, Disabled = true };

    private static readonly Func<UiNode> ProgressTree = static () =>
        new Progress { Fraction = 0.4f, Width = UiDim.Fixed(200f) };

    private static readonly Func<UiNode> SwitchOffTree = static () =>
        new Switch { OnToggle = FormNoOpBool };

    private static readonly Func<UiNode> SwitchOnTree = static () =>
        new Switch { Value = true, OnToggle = FormNoOpBool };

    /// <summary>
    /// The section twins carry NO content, because the legacy fixture they
    /// are gated against carries none either: <c>Ui.Section</c> is handed an
    /// empty <c>FormScope</c> body, so an expanded legacy section draws its
    /// rule, its header and nothing else. Rows here would be pixels the
    /// reference side does not have.
    /// </summary>
    private static readonly Func<UiNode> SectionTree = static () =>
        new Section
        {
            Title = "GENERAL",
            OnExpandedChange = FormNoOpBool,
            Key = "section",
        };

    private static readonly Func<UiNode> SectionExpandedTree = static () =>
        new Section
        {
            Title = "GENERAL",
            Expanded = true,
            OnExpandedChange = FormNoOpBool,
            Key = "section",
        };

    /// <summary>
    /// Diagnostic chassis at the Settings window's own 720x520: the
    /// rotated-H rules (full-width header/footer rules bridged by the nav
    /// rule), the footer band with its comfortable buttons, and a rail with
    /// a selected row — everything the in-game window reports against,
    /// renderable where pixels can be inspected.
    /// </summary>
    /// <summary>
    /// Declared through the SHARED chassis, and byte-gated against the
    /// hand-built frame it replaced: Settings is accepted pixel-for-pixel, so
    /// this fixture is what proves the extraction reproduced its geometry
    /// rather than approximated it.
    /// </summary>
    private static readonly Func<UiNode> SettingsFrameTree = static () =>
        new WindowChassis
        {
            Title = "Settings",
            OnClose = FormNoOp,
            RailWidth = 200f,
            Rail =
            [
                SettingsNavRow(0, "General", TablerIcon.Sliders, false),
                SettingsNavRow(1, "Display", TablerIcon.Monitor, true),
            ],
            FooterRight =
            [
                new Button { Label = "Cancel", OnClick = FormNoOp },
                new Button
                {
                    Label = "Save",
                    Style = ButtonStyle.Primary,
                    OnClick = FormNoOp,
                },
            ],
        };

    private static UiNode SettingsNavRow(
        int index, string label, TablerIcon icon, bool selected) => new Element
    {
        Sheet = SheetFamily.NavRow,
        Selected = selected,
        Index = index,
        On = new Poser.UI.Listeners { OnPick = FormNoOpInt },
        Key = index,
        Children =
        [
            new Stack
            {
                Sheet = SheetFamily.NavIconSlot,
                Children = new Glyph { Icon = icon, Size = 14f },
            },
            new Poser.UI.Label { Text = label, Sheet = SheetFamily.NavLabel },
        ],
    };

    private static readonly string[] SegmentedFixtureItems =
        ["Left", "Right", "Floating", "Hidden"];

    private static readonly TablerIcon[] SegmentedFixtureIcons =
    [
        TablerIcon.ArrowsMove,
        TablerIcon.Rotate,
        TablerIcon.ArrowsMaximize,
        TablerIcon.ArrowsDiagonal,
    ];

    /// <summary>The third tab is refused, so the disabled tone has a place to
    /// show. A static predicate keeps the fixture allocation-free.</summary>
    private static readonly Func<int, bool> SegmentedFixtureDisabled =
        static index => index == 2;

    /// <summary>Two stacked LABEL rows — the exact shape that proved the 30px
    /// band left full-height controls no separation (user 2026-08-02), so the
    /// row pitch stays inspectable where it regressed — then the ICON variant
    /// twice: once placed normally, once with AlignFirstTabToCursor, so the
    /// negative chrome-padding margin either shifts the pill or it does not.
    /// </summary>
    private static readonly Func<UiNode> SegmentedTree = static () =>
        new Column
        {
            Style = new()
            {
                Layout = new() { Width = UiDim.Fixed(400f) },
            },
            Children =
            [
                Poser.UI.Crystarium.FormSegmented(
                    "Entity sidebar", SegmentedFixtureItems, 0, FormNoOpInt, 270f),
                Poser.UI.Crystarium.FormSegmented(
                    "Inspector", SegmentedFixtureItems, 1, FormNoOpInt, 270f),
                new Segmented
                {
                    Icons = SegmentedFixtureIcons,
                    Selected = 1,
                    OnChange = FormNoOpInt,
                    ItemDisabled = SegmentedFixtureDisabled,
                    Key = "icons",
                },
                new Segmented
                {
                    Icons = SegmentedFixtureIcons,
                    Selected = 1,
                    OnChange = FormNoOpInt,
                    ItemDisabled = SegmentedFixtureDisabled,
                    AlignFirstTabToCursor = true,
                    Key = "icons-aligned",
                },
            ],
        };

    /// <summary>The five icon-action shapes the shell titlebar asks for, on one
    /// row: the enum glyph, a registry name the enum lacks, a mirrored glyph, a
    /// selected toggle and a slashed one.</summary>
    private static readonly Func<UiNode> IconActionsTree = static () =>
        new Row
        {
            Style = new()
            {
                Layout = new() { Gap = 8f, Align = UiAlign.Center },
            },
            Children =
            [
                new IconAction
                {
                    Icon = TablerIcon.Settings,
                    OnClick = FormNoOp,
                    Size = 28f,
                    Key = "enum",
                },
                IconAction.Named("x") with
                {
                    OnClick = FormNoOp,
                    Size = 28f,
                    Key = "named",
                },
                new IconAction
                {
                    Icon = TablerIcon.ArrowBackUp,
                    OnClick = FormNoOp,
                    FlipX = true,
                    Size = 28f,
                    Key = "flipped",
                },
                new IconAction
                {
                    Icon = TablerIcon.Eye,
                    OnClick = FormNoOp,
                    Selected = true,
                    Key = "selected",
                },
                new IconAction
                {
                    Icon = TablerIcon.Eye,
                    OnClick = FormNoOp,
                    Selected = false,
                    Slashed = true,
                    Key = "slashed",
                },
            ],
        };

    private static readonly Vector4[] SwatchFixtureColors =
    [
        new(0.50f, 0.50f, 0.50f, 1f),
        new(1f, 1f, 1f, 1f),
        new(0.16f, 0.21f, 0.43f, 1f),
        new(0.27f, 0.20f, 0.46f, 1f),
        new(0.94f, 0.62f, 0.10f, 1f),
    ];

    private static readonly Func<UiNode> SwatchesTree = static () =>
        new Swatches
        {
            Colors = SwatchFixtureColors,
            Selected = 2,
            OnChange = FormNoOpInt,
        };

    /// <summary>
    /// The in-window scroll container, overflowing on purpose: twenty 26px
    /// rows is 520px of content in a 160px viewport, so the capture shows the
    /// gutter's bar, a cut last row, and — because every other row carries the
    /// Selected overlay — exactly where each row's band starts. The rows are
    /// stateful sheets with no listeners, which is what proves the identity
    /// chain still reserves twenty distinct hit paths through the seam.
    /// </summary>
    private static readonly Func<UiNode> ScrollAreaTree = static () =>
    {
        UiNode[] rows = new UiNode[20];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new Poser.UI.Element
            {
                Sheet = SheetFamily.DropdownRow,
                Style = new()
                {
                    Layout = new() { Height = UiDim.Fixed(26f) },
                },
                Text = $"Row {i + 1:00}",
                Selected = i % 2 == 0,
                Key = i,
            };

        return new ScrollArea
        {
            Height = UiDim.Fixed(160f),
            CapChildHitWidth = true,
            Children = UiChildren.Create(rows),
            Key = "scroll",
        };
    };

    /// <summary>
    /// The sidebar tree, in the four shapes the shell actually draws: a root
    /// that discloses (chevron in its 16px slot, icon, no guides), a nested
    /// leaf with siblings below it (full trunk + arm) carrying an action strip,
    /// the LAST nested leaf (hard L) selected and badged, and a collapsed root.
    /// The rows are keyed by name because a tree reorders under disclosure.
    /// </summary>
    private static readonly Func<UiNode> TreeRowTree = static () =>
        new Column
        {
            Style = new()
            {
                Layout = new() { Width = UiDim.Fixed(250f) },
            },
            Children =
            [
                new TreeRow
                {
                    Label = "Midona Rhel",
                    Icon = TablerIcon.User,
                    Expander = SidebarExpander.Open,
                    OnSelect = FormNoOp,
                    OnToggleExpand = FormNoOp,
                    Key = "actor",
                },
                new TreeRow
                {
                    Label = "Spine",
                    Depth = 1,
                    OnSelect = FormNoOp,
                    Actions =
                    [
                        new IconAction
                        {
                            Icon = TablerIcon.Crosshair,
                            OnClick = FormNoOp,
                            Size = 20f,
                            Key = "target",
                        },
                        new IconAction
                        {
                            Icon = TablerIcon.Eye,
                            OnClick = FormNoOp,
                            Selected = true,
                            Size = 20f,
                            Key = "visible",
                        },
                    ],
                    Key = "spine",
                },
                // The FORK: a nested row that itself discloses — the split
                // trunk with the cutout around its chevron — plus its child,
                // whose trunk mask carries the continuing depth-1 line. The
                // one branch shape the first four rows cannot reach.
                new TreeRow
                {
                    Label = "Arms",
                    Depth = 1,
                    Expander = SidebarExpander.Open,
                    OnSelect = FormNoOp,
                    OnToggleExpand = FormNoOp,
                    Key = "arms",
                },
                new TreeRow
                {
                    Label = "L Hand",
                    Depth = 2,
                    Trunks = 0b10,
                    IsLastChild = true,
                    OnSelect = FormNoOp,
                    Key = "l-hand",
                },
                new TreeRow
                {
                    Label = "Head",
                    Depth = 1,
                    IsLastChild = true,
                    Selected = true,
                    Badge = "12",
                    OnSelect = FormNoOp,
                    Key = "head",
                },
                new TreeRow
                {
                    Label = "Chocobo",
                    Icon = TablerIcon.Paw,
                    Expander = SidebarExpander.Collapsed,
                    OnSelect = FormNoOp,
                    OnToggleExpand = FormNoOp,
                    Key = "companion",
                },
            ],
        };

    /// <summary>
    /// The file surface's INJECTED disk. Every timestamp is frozen and every
    /// probe answers without touching the machine, so the fixture is the same
    /// picture on a build agent as on a workstation — which is the whole point
    /// of the dialog's listing seam.
    /// </summary>
    private sealed class FileDialogListing : IFileListingSource
    {
        internal const string Folder = @"C:\Users\Midona\Documents\Poses";

        internal static readonly FileDialogListing Instance = new();

        private static readonly DateTime Stamp =
            new(2026, 7, 14, 21, 6, 0, DateTimeKind.Utc);

        public string DefaultPath => Folder;

        public bool DirectoryExists(string path) => true;

        public string? Parent(string path) => @"C:\Users\Midona\Documents";

        public void Enumerate(string path, List<FileListingEntry> into)
        {
            void Directory(string name, int days) => into.Add(
                new FileListingEntry(
                    name, path + "\\" + name, true, Stamp.AddDays(-days)));
            void File(string name, int days) => into.Add(
                new FileListingEntry(
                    name, path + "\\" + name, false, Stamp.AddDays(-days)));

            Directory("Archive", 41);
            Directory("Combat", 12);
            Directory("Portraits", 3);
            File("heroic-stand.pose", 1);
            File("kneeling-vow.pose", 6);
            File("sitting-ledge.pose", 9);
            File("wind-swept.pose", 22);
        }

        public void QuickAccess(List<FileQuickEntry> into)
        {
            into.Add(new FileQuickEntry(
                "Desktop", @"C:\Users\Midona\Desktop", TablerIcon.DeviceDesktop));
            into.Add(new FileQuickEntry(
                "Documents", @"C:\Users\Midona\Documents", TablerIcon.FileText));
            into.Add(new FileQuickEntry(
                "Pictures", @"C:\Users\Midona\Pictures", TablerIcon.Photo));
            into.Add(new FileQuickEntry(
                "Downloads", @"C:\Users\Midona\Downloads", TablerIcon.Download));
            // The folder the fixture is standing in, so the rail shows its
            // selected pill and the explorer shows its listing at once.
            into.Add(new FileQuickEntry("Poses", Folder, TablerIcon.Folder));
            into.Add(new FileQuickEntry(@"C:\", @"C:\", TablerIcon.Stack2));
        }
    }

    /// <summary>The file surface is RETAINED — it owns a UiRoot, a history
    /// stack and two native islands — but its root's identity cache lives in
    /// the per-state ImGui context, so the instance is rebuilt on frame 0 of
    /// every run rather than shared with the next state.</summary>
    private static Ui.FileDialog? fileDialog;

    private static readonly string[] FileDialogExtensions = [".pose"];

    private static readonly ContextMenuItem[] MenuItems =
    [
        new("Set game target", TablerIcon.Crosshair),
        new("Hide", TablerIcon.EyeOff),
        new("Rename…", TablerIcon.Edit),
        ContextMenuItem.Separator,
        new("Despawn", TablerIcon.X, danger: true),
    ];

    // One retained root per STATE. A state runs 40 frames in a fresh ImGui
    // context, and the root is what carries scopes, motion identity and the
    // interaction-id cache across them — so it must outlive the frame loop
    // and must never be shared with the next state, whose context is gone.
    private static RxRoot? reactiveRoot;
    private static string? reactiveRootState;
    // A static build callback cannot capture the frame counter, so the
    // frame-dependent fixture parameter is parked here instead.
    private static bool reactiveDisabled;

    /// <summary>
    /// SearchPicker is a RETAINED object — it stores the anchor rect its
    /// Open call sampled and has to outlive the frame loop — but its popup
    /// lives in the per-state ImGui context, so the instance is rebuilt on
    /// frame 0 of every run rather than shared with the next state.
    /// </summary>
    private static Ui.SearchPicker<string>? searchPicker;

    public static IReadOnlyList<ComponentSpec> All =>
        Specs.Where(spec => !spec.Hidden).ToArray();

    public static ComponentSpec Get(string name) =>
        Specs.FirstOrDefault(
            item => string.Equals(
                item.Name, name, StringComparison.OrdinalIgnoreCase))
        is { Name.Length: > 0 } match
            ? match
            : throw new ArgumentException(
                $"Unknown component '{name}'. " +
                $"Expected one of: {string.Join(", ", Specs.Select(x => x.Name))}.");

    private static RxRoot ReactiveRoot(string state)
    {
        if (reactiveRootState != state)
        {
            reactiveRoot = new RxRoot();
            reactiveRootState = state;
        }

        return reactiveRoot!;
    }

    private static void DrawIconGrid(Vector2 origin, float size)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float pitch = (size + 8f) * scale;
        var names = Tabler.ShippedNames();
        for (int i = 0; i < names.Count; i++)
        {
            var min = origin + new Vector2(i % 8 * pitch, i / 8 * pitch);
            Ui.IconIn(min, min + new Vector2(size * scale), names[i]);
        }
    }

    private static void DrawIconStates(Vector2 origin)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float pitch = 24f * scale;
        string[] icons = ["settings", "eye", "trash", "chevron-down"];
        for (int column = 0; column < icons.Length; column++)
        {
            float x = origin.X + column * pitch;
            void Cell(
                int row, float size, Vector4? color = null,
                float opacity = 1f, bool disabled = false,
                float? stroke = null)
            {
                var min = new Vector2(x, origin.Y + row * pitch);
                Ui.IconIn(
                    min, min + new Vector2(size * scale), icons[column],
                    color, opacity: opacity, disabled: disabled,
                    strokeWidth: stroke);
            }
            Cell(0, 16f);
            Cell(1, 16f, opacity: 0.8f);
            Cell(2, 16f, disabled: true);
            Cell(3, 16f, Ui.ActiveTheme.Accent);
            Cell(4, 16f, stroke: 1.5f);
            Cell(5, 11f, Ui.ActiveTheme.Accent);
        }
    }

    /// <summary>
    /// First frame on which tooltip-pop-mid registers its help target.
    ///
    /// <para>HoverHelp is driven by <c>ImGui.GetTime()</c>, which advances
    /// by the harness's fixed 1/60s step, so the entrance midpoint is
    /// exact arithmetic rather than a wall-clock race. Registering on
    /// frame F stamps the pending target at F; the 400ms open delay is
    /// exactly 24 steps, so the entrance starts on frame F+24 and the
    /// 150ms pop spans 9 further steps. The harness observes frame 38
    /// (it presents the last two frames and reads back the prior buffer,
    /// reproducing the legacy 40-present path), so F = 9 leaves
    /// 38 - (9 + 24) = 5 elapsed steps — linear progress 5/9, the same
    /// midpoint btn-hover-mid uses, eased by CSS <c>ease</c> to
    /// 0.852283.</para>
    ///
    /// <para>Nine idle frames also drain any card the PREVIOUS batch
    /// entry left open: its exit is the same 150ms = 9 steps, and the
    /// unregistered frames null the pending id, so the delay measured
    /// from frame 9 is the same in a batch as in an isolated capture.</para>
    /// </summary>
    public const int PopMidRegisterFrame = 9;

    /// <summary>
    /// The frame the scrolled fixtures deliver their wheel notch on: late
    /// enough that the reactive menu (opened by a real click at frame 2) is
    /// already up, early enough that the pointer is long gone by the
    /// presented frames.
    /// </summary>
    private const int ScrollWheelFrame = 10;

    /// <summary>
    /// Vertical wheel for a frame, in ImGui notches — the units
    /// <c>AddMouseWheelEvent</c> takes. A notch is
    /// <c>min(5 * fontSize, 0.67 * viewport)</c>, which is a FONT metric and
    /// so not something a fixture should depend on; two notches overshoot
    /// this list's 84px of travel (ten 26px rows on 2px gaps inside a 194px
    /// seven-row viewport) from any plausible font size, so the scroll
    /// clamps to its maximum and lands on exactly three rows whatever the
    /// atlas resolves. Zero on every other frame and every other state, and
    /// ImGui drops a zero wheel event before it queues it.
    /// </summary>
    public static float WheelFor(string name, int frame) =>
        (name is "dd-scrolled" or "rdd-scrolled") && frame == ScrollWheelFrame
            ? -2f
            : 0f;

    public static Vector2 PointerFor(string name, float scale, int frame)
    {
        if (name == "context-menu")
            return new Vector2(40, 40) * scale;
        // The scrolled fixtures need the pointer INSIDE the popup list on the
        // wheel frame — ImGui routes the wheel to the hovered window — and
        // parked offscreen well before the presented frames, so the settled
        // capture shows no hovered row. (64,120) is inside the seven-row
        // viewport of a menu anchored under the (24,24) trigger; (64,37) is
        // the trigger itself, which only the reactive twin has to click
        // because its portal handle is path-derived.
        if (name is "dd-scrolled" or "rdd-scrolled")
        {
            if (name == "rdd-scrolled" && frame is >= 1 and <= 4)
                return new Vector2(64, 37) * scale;
            return frame is >= 6 and <= 16
                ? new Vector2(64, 120) * scale
                : new Vector2(-1000, -1000);
        }
        // Hover states park the pointer inside the control; hover-exit
        // leaves after 15 frames so the 150ms background transition has
        // settled back to idle by capture. The shared inside point (84,40)
        // is inside every -hover fixture's rect, including the sidebar
        // rows (272x26 at the (24,24) stage origin) — sidebar hover is
        // therefore real pointer input hit-tested by Interactive.Reserve,
        // never a flag.
        // btn-hover-mid targets five 1/60s hover frames of the 150ms
        // transition = linear progress 5/9, captured mid-flight on the
        // fixed-timestep final frame. The harness's queued-event-to-
        // hover latency is exactly ONE frame (measured: entering at 35
        // yields four advances, matching the 4-step composite to the
        // pixel), so entry is calibrated to frame 34.
        bool inside = name.EndsWith("-hover", StringComparison.Ordinal)
            || (name == "btn-hover-exit" && frame < 15)
            || (name == "btn-hover-mid" && frame >= 34)
            || (name == "btn-hover-reconcile" && frame < 20)
            // The reactive twins share their btn counterpart's choreography
            // exactly; identical input is what makes the byte-identity
            // assertion a statement about the RENDERER, not the script.
            || (name == "rbtn-hover-exit" && frame < 15)
            || (name == "rbtn-hover-mid" && frame >= 34)
            || (name == "rbtn-hover-reconcile" && frame < 20)
            // The reactive portal id is PATH-derived, so the open state
            // cannot be staged with an OpenPopover call the way the legacy
            // twin is: the menu is opened the honest way, by a real click
            // on the trigger. The pointer leaves on frame 5 so the settled
            // capture shows exactly what dropdown-open shows — trigger
            // unhovered, popup open, no row under the pointer.
            || (name == "rdd-open" && frame is >= 1 and <= 4)
            // Same choreography for the picker twins: the surface is opened
            // by a real press on the trigger, and the pointer leaves before
            // the presented frames so no row is hovered at capture.
            || (name.StartsWith("rpicker-", StringComparison.Ordinal)
                && frame is >= 1 and <= 4)
            || name == "icon-button-pressed"
            || (name == "icon-button-held-outside" && frame < 15)
            || (name == "icon-button-hover-exit" && frame < 15)
            // IconButton starts a CSS-shaped transition at t=0 on the
            // first hovered frame, so enter one frame earlier than the
            // older incremental text-button transition: after ImGui's
            // queued-event latency this leaves five 1/60s advances.
            || (name == "icon-button-hover-mid" && frame >= 33)
            || (name.StartsWith(
                    "icon-button-backdrop-", StringComparison.Ordinal)
                && frame >= 33)
            || (name == "icon-button-hover-reconcile" && frame < 20);
        // The expander hover is SCOPED to the chevron, so its pointer cannot
        // be the shared row-body point: a ROOT chevron is the 18px box at the
        // row's left edge, which at the (24,24) stage origin is x 24..42 over
        // the 26px row — centre (33,37). Parking there proves the scoping too:
        // at (84,40) the row would hover but the triangle would not lift.
        // InspectorSection puts 21px of chrome (margin 10 + 1px rule +
        // padding 10) above its 26px .header, so the shared (84,40) point
        // lands in the padding, ABOVE the interactive row. The header
        // spans y 45..71 at the (24,24) stage origin; its centre is 58.
        // The clear affordance is SCOPED to its 18px hit area: the field
        // is 272 wide at the (24,24) origin, so its right edge is 296 and
        // the affordance centres 13px in at (283, 42) over the 36px
        // search row. The shared (84,40) point would hover the field but
        // not the affordance.
        // The dropdown trigger is the 26px .btn at the (24,24) stage origin,
        // wide enough for "Date Modified" plus the chevron slot; this point
        // is inside it and clear of both edges.
        return inside
            ? (name == "rdd-open"
                || name.StartsWith("rpicker-", StringComparison.Ordinal)
                ? new Vector2(64, 37)
                // The reactive section twin shares the legacy header's box
                // exactly, so it shares the point that lands on it.
                : name is "section-hover" or "rsection-hover"
                ? new Vector2(84, 58)
                : name == "search-clear-hover"
                ? new Vector2(283, 42)
                : name == "sidebar-row-expander-hover"
                ? new Vector2(33, 37)
                : name.StartsWith("icon-button", StringComparison.Ordinal)
                    ? (name.StartsWith(
                            "icon-button-backdrop-", StringComparison.Ordinal)
                        ? new Vector2(118, 38)
                        : new Vector2(38, 38))
                    : new Vector2(84, 40)) * scale
            : new Vector2(-1000, -1000);
    }

    /// <summary>The pressed fixture holds a real primary pointer down;
    /// no persistent selected class stands in for :active.</summary>
    public static IEnumerable<(int Button, bool Down)> MouseButtonEventsFor(
        string name, int frame)
    {
        if ((name == "icon-button-pressed"
                || name == "icon-button-held-outside")
            && frame == 5)
            yield return (0, true);
        // rdd-open's menu is opened by a real press/release on the trigger,
        // inside the frames PointerFor parks the pointer there.
        if (name == "rdd-open" && frame == 2)
            yield return (0, true);
        if (name == "rdd-open" && frame == 4)
            yield return (0, false);
        // Same choreography for the scrolled reactive twin; the wheel notch
        // lands six frames after the release, with the menu already up.
        if (name == "rdd-scrolled" && frame == 2)
            yield return (0, true);
        if (name == "rdd-scrolled" && frame == 4)
            yield return (0, false);
        // The picker twins open the same honest way.
        if (name.StartsWith("rpicker-", StringComparison.Ordinal) && frame == 2)
            yield return (0, true);
        if (name.StartsWith("rpicker-", StringComparison.Ordinal) && frame == 4)
            yield return (0, false);
    }

    public static void Draw(string name, int frame, Vector2 canvas)
    {
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(canvas);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin(
            "##crystarium-conformance",
            ImGuiWindowFlags.NoDecoration
            | ImGuiWindowFlags.NoMove
            | ImGuiWindowFlags.NoSavedSettings
            | ImGuiWindowFlags.NoScrollWithMouse
            | ImGuiWindowFlags.NoBackground
            | ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.PopStyleVar();

        float scale =
            Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
        var origin = new Vector2(24, 24) * scale;
        ImGui.SetCursorScreenPos(origin);

        switch (name)
        {
            case "text-label":
                // Picto typography: src/app/globals.css body — 13px
                // --font-family at --color-text-primary.
                Ui.Text("Actor display name");
                break;
            case "text-caption":
                // src/shared/ui/PropertyRow/PropertyRow.module.css .label —
                // 11px --color-text-secondary.
                Ui.Text("Opacity", new TextStyle
                {
                    Size = Ui.ActiveTheme.Typography.CaptionSize,
                    Color = Ui.ActiveTheme.TextDim,
                });
                break;
            case "text-mono":
                // PropertyRow.module.css .value.valueMono — 11px mono
                // tabular numerals at opacity .8 over text-primary.
                Ui.Text("1.000", new TextStyle
                {
                    Size = Ui.ActiveTheme.Typography.CaptionSize,
                    Family = FontFamily.Mono,
                    Color = Ui.ActiveTheme.Text with { W = 0.8f },
                });
                break;
            case "text-disabled":
            {
                // src/shared/ui/ContextMenu/ContextMenu.module.css
                // .item.disabled > .label — the real Picto disabled
                // selector: opacity .4 on a 26px flex menu row with 6px
                // horizontal padding, 13px text-primary, flex-centered.
                var disabledStyle = new TextStyle { Disabled = true };
                float rowHeight = 26f * ImGuiHelpers.GlobalScale;
                float lineHeight = Ui.MeasureText("Unavailable action", disabledStyle).Y;
                Ui.TextAt(
                    origin + new Vector2(
                        6f * ImGuiHelpers.GlobalScale,
                        (rowHeight - lineHeight) * 0.5f),
                    "Unavailable action", disabledStyle);
                break;
            }
            case "text-truncated":
                // ContextMenu.module.css .label — single line,
                // ellipsis-truncated inside 140px.
                Ui.Text("The quick brown fox jumps over", default,
                    TextConstraint.Truncate(140f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-cjk":
                // ContextMenu.module.css .label idiom — grapheme-safe
                // backoff over ideographs (no Latin word boundaries).
                Ui.Text("素早い茶色の狐が飛び跳ねる", default,
                    TextConstraint.Truncate(140f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-combining":
                // ContextMenu.module.css .label idiom — combining acute
                // accents (U+0301) must never separate from their base.
                Ui.Text("réservé déjà touché", default,
                    TextConstraint.Truncate(100f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-emoji":
                // ContextMenu.module.css .label idiom — the surrogate
                // pair (U+1F600) must never be split by the backoff.
                Ui.Text("Emoji \U0001F600 marker overflow test", default,
                    TextConstraint.Truncate(110f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-fit":
                // ContextMenu.module.css .label idiom — text that fits
                // must pass through untouched, no ellipsis.
                Ui.Text("The quick brown fox", default,
                    TextConstraint.Truncate(140f * ImGuiHelpers.GlobalScale));
                break;
            case "text-truncated-narrow":
                // ContextMenu.module.css .label idiom — narrower than
                // the ellipsis itself: Blink drops the ellipsis and
                // clips the raw run, and so does the renderer's clip.
                Ui.Text("Unreachable", default,
                    TextConstraint.Truncate(6f * ImGuiHelpers.GlobalScale));
                break;
            case "text-wrapped":
                // src/shared/ui/GlassModal/GlassModal.module.css
                // .helpText — 11px --color-text-tertiary wrapped at
                // line-height 1.4 inside 220px.
                Ui.Text(
                    "Poser keeps the incoming state and restores it exactly when the override is removed.",
                    new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.TextMuted,
                    },
                    TextConstraint.Wrap(220f * ImGuiHelpers.GlobalScale, 1.4f));
                break;
            case "text-wrapped-newline":
                // src/shared/ui/InspectorField/InspectorField.module.css
                // .popover .popoverText — 13px text-primary at
                // line-height 1.5, white-space: pre-wrap preserving the
                // explicit newline, inside 200px.
                Ui.Text(
                    "First line\nSecond block that wraps onward, and onward.",
                    default,
                    TextConstraint.Wrap(
                        200f * ImGuiHelpers.GlobalScale, 1.5f,
                        TextWhitespace.PreWrap));
                break;
            case "text-wrapped-overwide":
                // GlassModal.module.css .helpText inside 120px — the
                // over-wide token overflows its line (CSS overflow-wrap
                // normal), it is never hard-broken.
                Ui.Text(
                    "A veryverylongunbrokentoken overflows.",
                    new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.TextMuted,
                    },
                    TextConstraint.Wrap(120f * ImGuiHelpers.GlobalScale, 1.4f));
                break;
            case "text-truncated-flow":
                // ContextMenu.module.css .label in a fixed 140px flex slot
                // with a VISIBLE following sibling (flex gap 0): the
                // truncated run occupies its constraint width in layout,
                // so the sibling starts at the box edge.
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
                Ui.Text("The quick brown fox jumps over", default,
                    TextConstraint.Truncate(140f * ImGuiHelpers.GlobalScale));
                ImGui.SameLine();
                Ui.Text("Next");
                ImGui.PopStyleVar();
                break;
            case "text-wrapped-flow":
                // GlassModal.module.css .helpText wrapped at 160px with a
                // body-text block sibling BELOW (block flow, margin 0):
                // the wrap block's layout height positions the sibling.
                // Both blocks share the container's left edge, so the
                // sibling re-anchors to the stage origin the same way
                // both divs inherit the stage padding.
                ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, Vector2.Zero);
                Ui.Text(
                    "Poser keeps the incoming state and restores it exactly.",
                    new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.TextMuted,
                    },
                    TextConstraint.Wrap(160f * ImGuiHelpers.GlobalScale, 1.4f));
                ImGui.SetCursorScreenPos(new Vector2(
                    origin.X, ImGui.GetCursorScreenPos().Y));
                Ui.Text("After");
                ImGui.PopStyleVar();
                break;
            case "text-ws-collapse":
                // GlassModal.module.css .helpText (white-space: normal) —
                // repeated spaces AND the newline all collapse to single
                // spaces before wrapping inside 220px.
                Ui.Text(
                    "Collapse   runs of   spacing\nacross   breaks in the   source text.",
                    new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.TextMuted,
                    },
                    TextConstraint.Wrap(220f * ImGuiHelpers.GlobalScale, 1.4f));
                break;
            case "text-ws-prewrap":
                // InspectorField.module.css .popover .popoverText
                // (white-space: pre-wrap) — the doubled and tripled
                // spaces stay visible and the newline breaks.
                Ui.Text(
                    "Keep  two   spaces\nand the break",
                    default,
                    TextConstraint.Wrap(
                        200f * ImGuiHelpers.GlobalScale, 1.5f,
                        TextWhitespace.PreWrap));
                break;
            case "text-ws-tab":
                // InspectorField.module.css .popover .popoverText — tabs
                // preserved by pre-wrap advance to 8-space-width stops
                // (CSS default tab-size: 8).
                Ui.Text(
                    "a\tbc\tdef\ttab stops",
                    default,
                    TextConstraint.Wrap(
                        260f * ImGuiHelpers.GlobalScale, 1.5f,
                        TextWhitespace.PreWrap));
                break;
            case "text-ws-crlf":
                // text-wrapped-newline's exact content with CRLF line
                // separators: presentation normalization must make this
                // capture pixel-identical to the LF twin, as the HTML
                // parser normalizes CRLF before layout on the reference.
                Ui.Text(
                    "First line\r\nSecond block that wraps onward, and onward.",
                    default,
                    TextConstraint.Wrap(
                        200f * ImGuiHelpers.GlobalScale, 1.5f,
                        TextWhitespace.PreWrap));
                break;
            case "text-align-end":
            {
                // src/shared/ui/PropertyRow/PropertyRow.module.css —
                // .row (20px, nowrap) with .label (11px secondary,
                // min-width 64) and .value (flex:1, 11px primary at
                // opacity .8, text-align: right); second row uses
                // .valueMono. End alignment through the canonical
                // constrained renderer pins each value to the row's end
                // edge. Picto has no end-aligned ELLIPSIS grammar, so
                // the truncating-End case has no reference here.
                float s2 = ImGuiHelpers.GlobalScale;
                float rowWidth = 200f * s2;
                float labelWidth = 64f * s2;
                float rowHeight = 20f * s2;
                var labelStyle = new TextStyle
                {
                    Size = Ui.ActiveTheme.Typography.CaptionSize,
                    Color = Ui.ActiveTheme.TextDim,
                };
                var rows = new (string Label, string Value, TextStyle Style)[]
                {
                    ("Opacity", "1.000", new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Color = Ui.ActiveTheme.Text with { W = 0.8f },
                    }),
                    ("Scale", "0.750", new TextStyle
                    {
                        Size = Ui.ActiveTheme.Typography.CaptionSize,
                        Family = FontFamily.Mono,
                        Color = Ui.ActiveTheme.Text with { W = 0.8f },
                    }),
                };
                for (int i = 0; i < rows.Length; i++)
                {
                    float rowTop = origin.Y + i * rowHeight;
                    float lineHeight = Ui.MeasureText(
                        rows[i].Label, labelStyle).Y;
                    float textY = rowTop + (rowHeight - lineHeight) * 0.5f;
                    Ui.TextAt(
                        new Vector2(origin.X, textY),
                        rows[i].Label, labelStyle);
                    Ui.TextAt(
                        new Vector2(origin.X + labelWidth, textY),
                        rows[i].Value, rows[i].Style,
                        TextConstraint.Truncate(
                            rowWidth - labelWidth, TextAlign.End));
                }
                break;
            }
            case "icons-grid-16":
                // Every shipped icon (Tabler.ShippedNames — custom
                // overriding Tabler, ordinal-sorted) at 16px on a 24px
                // pitch; idle theme text, exactly the bare-SVG rendering
                // Picto uses.
                DrawIconGrid(origin, 16f);
                break;
            case "icons-grid-14":
                // The same shipped set at 14px on a 22px pitch.
                DrawIconGrid(origin, 14f);
                break;
            case "icons-states":
                // Representative states mirroring the reference rows:
                // idle, resting .8 (iconSlot), disabled .4 (menu
                // .disabled), accent tint, stroke 1.5 chrome idiom, and
                // the 11px rail size.
                DrawIconStates(origin);
                break;
            case "btn-secondary":
            case "btn-secondary-hover":
            case "btn-hover-exit":
            case "btn-hover-mid":
                // actionButton.module.css .btn — hover/focus states are
                // pointer- and Tab-driven through the real interaction
                // path; hover-exit settles back to idle after the pointer
                // leaves mid-capture.
                Ui.Button("Apply changes", id: "##btn");
                break;
            case "btn-secondary-disabled":
                Ui.Button("Apply changes", disabled: true, id: "##btn");
                break;
            case "btn-disabled-unicode":
                // Disabled labels must stay readable across scripts:
                // Latin, CJK (the merged fallback face), a combining
                // mark, and a glyph outside the atlas ranges (missing-
                // glyph fallback).
                Ui.Button("Wait 待機 x̃ €", disabled: true, id: "##btn");
                break;
            case "btn-primary":
            case "btn-primary-hover":
                Ui.Button(
                    "Apply changes",
                    variant: ButtonVariant.Primary,
                    id: "##btn");
                break;
            case "btn-primary-disabled":
                Ui.Button(
                    "Apply changes",
                    variant: ButtonVariant.Primary,
                    disabled: true,
                    id: "##btn");
                break;
            case "btn-danger":
            case "btn-danger-hover":
                Ui.Button(
                    "Apply changes",
                    variant: ButtonVariant.Danger,
                    id: "##btn");
                break;
            case "btn-danger-disabled":
                Ui.Button(
                    "Apply changes",
                    variant: ButtonVariant.Danger,
                    disabled: true,
                    id: "##btn");
                break;
            case "btn-width-content":
                // Content: intrinsic label width + the canonical 16px
                // padding and 1px border on each side.
                Ui.Button("OK", id: "##btn");
                break;
            case "btn-width-fixed":
                Ui.Button(
                    "Apply changes",
                    style: new ControlStyle { Width = UiWidth.Fixed(160f) },
                    id: "##btn");
                break;
            case "btn-width-fill":
                // Fill resolves the BOUNDED allocated region (the 240px
                // child), never the surrounding window.
                ImGui.BeginChild(
                    "##fill-region",
                    new Vector2(240f * ImGuiHelpers.GlobalScale, 40f * ImGuiHelpers.GlobalScale),
                    false,
                    ImGuiWindowFlags.NoBackground);
                Ui.Button(
                    "Apply changes",
                    style: new ControlStyle { Width = UiWidth.Fill },
                    id: "##btn");
                ImGui.EndChild();
                break;
            case "btn-narrow":
                // Narrower than the label: the canonical component clips
                // to its visual bounds. Candidate-only invariant (Picto's
                // native button does not clip) — see verify-button-clip.
                Ui.Button(
                    "Apply changes",
                    style: new ControlStyle { Width = UiWidth.Fixed(60f) },
                    id: "##btn");
                break;
            case "btn-narrow-blank":
                // Chrome-only twin of btn-narrow: subtracting it from the
                // labelled capture isolates glyph pixels unambiguously.
                Ui.Button(
                    "",
                    style: new ControlStyle { Width = UiWidth.Fixed(60f) },
                    id: "##btn");
                break;
            case "btn-narrow-unclipped":
            {
                // NEGATIVE CONTROL for the clip invariant: the same
                // over-wide label deliberately drawn WITHOUT the button's
                // clip. The clip test must FAIL on this state, proving
                // the glyph mask detects escapes.
                Ui.Button(
                    "",
                    style: new ControlStyle { Width = UiWidth.Fixed(60f) },
                    id: "##btn");
                float s2 = ImGuiHelpers.GlobalScale;
                var boxMin = origin;
                var boxSize = new Vector2(60f, 32f) * s2;
                var unclippedStyle = new TextStyle { };
                var m = Ui.MeasureText("Apply changes", unclippedStyle);
                Ui.TextAt(
                    boxMin + (boxSize - m) * 0.5f,
                    "Apply changes", unclippedStyle);
                break;
            }
            case "btn-hover-reconcile":
                // Deterministic sequence: hover settles (frames 0..17,
                // pointer inside from 0), disabled at 18..30 while the
                // pointer leaves at 20, re-enabled from 31. The final
                // enabled frame must equal idle — stale hover fill must
                // not replay. Asserted against btn-secondary's capture.
                Ui.Button(
                    "Apply changes",
                    disabled: frame is >= 18 and <= 30,
                    id: "##btn");
                break;
            // ---- Reactive twins (PBI-015) ---------------------------
            // Same stage origin, same label, same variant, same pointer
            // script as the btn family above; only the PATH differs, so
            // any pixel difference is the retained runtime's own.
            case "rbtn-secondary":
            case "rbtn-secondary-hover":
            case "rbtn-hover-exit":
            case "rbtn-hover-mid":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button { Label = "Apply changes" });
                break;
            case "rbtn-secondary-disabled":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button
                    {
                        Label = "Apply changes",
                        Disabled = true,
                    });
                break;
            case "rbtn-disabled-unicode":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button
                    {
                        Label = "Wait 待機 x̃ €",
                        Disabled = true,
                    });
                break;
            case "rbtn-primary":
            case "rbtn-primary-hover":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button
                    {
                        Label = "Apply changes",
                        Style = ButtonStyle.Primary,
                    });
                break;
            case "rbtn-primary-disabled":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button
                    {
                        Label = "Apply changes",
                        Style = ButtonStyle.Primary,
                        Disabled = true,
                    });
                break;
            case "rbtn-danger":
            case "rbtn-danger-hover":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button
                    {
                        Label = "Apply changes",
                        Style = ButtonStyle.Danger,
                    });
                break;
            case "rbtn-danger-disabled":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button
                    {
                        Label = "Apply changes",
                        Style = ButtonStyle.Danger,
                        Disabled = true,
                    });
                break;
            case "rbtn-width-content":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button { Label = "OK" });
                break;
            case "rbtn-width-fixed":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button
                    {
                        Label = "Apply changes",
                        StyleSheet = new()
                        {
                            Layout = new() { Width = UiDim.Fixed(160f) },
                        },
                    });
                break;
            case "rbtn-width-fill":
                // The root's own allocation IS the bounded region here, so
                // Fill resolves the 240px span the legacy twin gets from a
                // child window. Render takes PHYSICAL pixels.
                ReactiveRoot(name).Render(
                    origin,
                    new Vector2(240f, 40f) * ImGuiHelpers.GlobalScale,
                    static () => new Button
                    {
                        Label = "Apply changes",
                        StyleSheet = new()
                        {
                            Layout = new() { Width = UiDim.Fill },
                        },
                    });
                break;
            case "rbtn-hover-reconcile":
                reactiveDisabled = frame is >= 18 and <= 30;
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Button
                    {
                        Label = "Apply changes",
                        Disabled = reactiveDisabled,
                    });
                break;
            case "bar-allocation":
            {
                // ActionBar allocation invariant: Content + Fixed + Fill
                // inside a 260px allocation on a 340px canvas, left- and
                // right-aligned. Every rectangle must stay inside the
                // allocation, and Fill must resolve the REMAINING bar
                // allocation, not the wider window.
                float s3 = ImGuiHelpers.GlobalScale;
                var alloc = new Vector2(260f, 40f) * s3;
                Ui.ActionBar(
                    "##bar-left",
                    origin,
                    alloc,
                    bar =>
                    {
                        bar.Button("OK", () => { });
                        bar.Button("Fixed", () => { }, style: new ControlStyle
                        { Width = UiWidth.Fixed(70f) });
                        bar.Button("Fill", () => { }, style: new ControlStyle
                        { Width = UiWidth.Fill });
                    },
                    separator: ActionBarSeparator.None);
                Ui.ActionBar(
                    "##bar-right",
                    origin + new Vector2(0f, 56f * s3),
                    alloc,
                    _ => { },
                    right: bar =>
                    {
                        bar.Button("OK", () => { });
                        bar.Button("Fixed", () => { }, style: new ControlStyle
                        { Width = UiWidth.Fixed(70f) });
                        bar.Button("Fill", () => { }, style: new ControlStyle
                        { Width = UiWidth.Fill });
                    },
                    separator: ActionBarSeparator.None);
                break;
            }
            case "icon-button-idle":
                Ui.IconButton(
                    TablerIcon.Settings,
                    id: "##icon-button");
                break;
            case "icon-button-hover":
            case "icon-button-pressed":
            case "icon-button-held-outside":
            case "icon-button-hover-mid":
            case "icon-button-hover-exit":
                Ui.IconButton(
                    TablerIcon.Settings,
                    id: "##icon-button");
                break;
            case "icon-button-disabled":
                Ui.IconButton(
                    TablerIcon.Settings,
                    disabled: true,
                    help: "Settings are unavailable while loading",
                    id: "##icon-button");
                break;
            case "icon-button-glyphs":
            {
                var glyphs = new[]
                {
                    TablerIcon.Plus,
                    TablerIcon.X,
                    TablerIcon.ChevronRight,
                    TablerIcon.ChevronDown,
                    TablerIcon.ArrowBackUp,
                    TablerIcon.Folder,
                    TablerIcon.Settings,
                };
                for (int i = 0; i < glyphs.Length; i++)
                {
                    ImGui.SetCursorScreenPos(
                        origin + new Vector2(i * 32f * scale, 0f));
                    Ui.IconButton(
                        glyphs[i],
                        id: $"##icon-button-glyph-{i}");
                }
                break;
            }
            case "icon-button-explicit-size":
                Ui.IconButton(
                    TablerIcon.Settings,
                    style: ControlStyle.Square(36f),
                    id: "##icon-button");
                break;
            case "icon-button-hover-reconcile":
                Ui.IconButton(
                    TablerIcon.Settings,
                    disabled: frame >= 15 && frame < 25,
                    id: "##icon-button");
                break;
            case "icon-button-backdrop-surface":
            case "icon-button-backdrop-raised":
            case "icon-button-backdrop-checker":
            {
                var dl = ImGui.GetWindowDrawList();
                Vector4 first = name.EndsWith("raised", StringComparison.Ordinal)
                    ? Ui.ActiveTheme.SurfaceRaised
                    : Ui.ActiveTheme.Surface;
                Vector4 second = name.EndsWith("checker", StringComparison.Ordinal)
                    ? Ui.ActiveTheme.Accent
                    : first;
                float tile = 8f * scale;
                for (float y = 0; y < canvas.Y; y += tile)
                    for (float x = 0; x < canvas.X; x += tile)
                    {
                        var color = ((int)(x / tile) + (int)(y / tile)) % 2 == 0
                            ? first
                            : second;
                        dl.AddRectFilled(
                            new Vector2(x, y),
                            Vector2.Min(new Vector2(x + tile, y + tile), canvas),
                            ImGui.ColorConvertFloat4ToU32(color));
                    }
                ImGui.SetCursorScreenPos(origin);
                Ui.IconButton(TablerIcon.Plus, id: "##backdrop-idle");
                ImGui.SetCursorScreenPos(origin + new Vector2(40f * scale, 0f));
                Ui.IconButton(
                    TablerIcon.ArrowBackUp, disabled: true,
                    flipX: true,
                    id: "##backdrop-disabled");
                ImGui.SetCursorScreenPos(origin + new Vector2(80f * scale, 0f));
                Ui.IconButton(
                    TablerIcon.ArrowBackUp,
                    id: "##backdrop-transition");
                break;
            }
            case "switch-off":
                Ui.Switch(
                    "##switch-off", false, _ => { });
                break;
            case "switch-on":
                Ui.Switch(
                    "##switch-on", true, _ => { });
                break;
            // ---- Form controls (PBI-015 wave L) ---------------------
            // The four controls PageForm hands the Appearance pane, in the
            // exact shapes that pane asks for. A Fixed 200px width keeps
            // the slider's geometry independent of the canvas, so the
            // thumb centre is arithmetic (24 + 7 + .4 * 186) rather than
            // a function of the cell.
            case "slider":
            case "slider-disabled":
                Ui.Slider(
                    "##slider",
                    0.4f,
                    0f,
                    1f,
                    _ => { },
                    new ControlStyle { Width = UiWidth.Fixed(200f) },
                    disabled: name == "slider-disabled");
                break;
            case "colorwell":
                Ui.ColorWell(
                    "##colorwell",
                    new Vector4(0.8f, 0.3f, 0.2f, 1f),
                    _ => { },
                    rgbOnly: true);
                break;
            // The absent-weapon well, exactly as PageForm.ColorWellScope
            // passes it: a null tint becomes Vector4.Zero with disabled
            // set, which is what makes the well paint UnavailableFill
            // instead of the colour it does not have.
            case "colorwell-disabled":
                Ui.ColorWell(
                    "##colorwell-disabled",
                    Vector4.Zero,
                    _ => { },
                    rgbOnly: true,
                    disabled: true);
                break;
            case "progress":
                Ui.ProgressBar(0.4f, 200f);
                break;
            // ---- Reactive form twins (PBI-015 wave P) ----------------
            // Same stage origin, same values, same fixed 200px measure as
            // the five states above and the two switch states further up;
            // only the PATH differs, so any pixel difference is the
            // retained runtime's own.
            case "rslider":
                ReactiveRoot(name).Render(
                    origin, ImGui.GetContentRegionAvail(), SliderTree);
                break;
            case "rslider-disabled":
                ReactiveRoot(name).Render(
                    origin, ImGui.GetContentRegionAvail(),
                    SliderDisabledTree);
                break;
            case "rcolorwell":
                ReactiveRoot(name).Render(
                    origin, ImGui.GetContentRegionAvail(), ColorWellTree);
                break;
            case "rcolorwell-disabled":
                ReactiveRoot(name).Render(
                    origin, ImGui.GetContentRegionAvail(),
                    ColorWellDisabledTree);
                break;
            case "rprogress":
                ReactiveRoot(name).Render(
                    origin, ImGui.GetContentRegionAvail(), ProgressTree);
                break;
            case "rswitch-off":
                ReactiveRoot(name).Render(
                    origin, ImGui.GetContentRegionAvail(), SwitchOffTree);
                break;
            case "rswitch-on":
                ReactiveRoot(name).Render(
                    origin, ImGui.GetContentRegionAvail(), SwitchOnTree);
                break;
            // ---- Imperative picker (PBI-016) -------------------------
            // The same two scenes and the same trigger as the reactive
            // states below, so the goldens judge them byte-for-byte.
            // SearchPicker samples its anchor from the CURRENT ImGui item,
            // so Open must follow the trigger immediately; and Open only
            // requests the popover — Draw is what opens it, the same frame.
            // Opening once on frame 0 (not every frame) is what makes the
            // remaining 39 frames a settled surface rather than a
            // re-entering one.
            case "i-picker-open":
                Ui.Button("Date Modified", id: "##i-picker-trigger");
                if (frame == 0)
                {
                    searchPicker = new Ui.SearchPicker<string>("catalog");
                    searchPicker.Open(
                        "catalog",
                        DropdownItems,
                        static item => item,
                        static item => item,
                        selectedKey: DropdownItems[1]);
                }
                searchPicker!.Draw();
                break;
            case "i-picker-multi":
                Ui.Button("Date Modified", id: "##i-picker-trigger");
                if (frame == 0)
                {
                    searchPicker = new Ui.SearchPicker<string>("catalog");
                    searchPicker.OpenMulti(
                        "catalog",
                        "Sort by",
                        DropdownItems,
                        static item => item,
                        static item => item,
                        PickerSelected,
                        PickerNoOpToggle);
                }
                searchPicker!.Draw();
                break;
            // ---- Reactive picker (PBI-015 wave O) --------------------
            // The SAME trigger the legacy fixture draws, at the same stage
            // origin, so the comparison isolates the panel: the surface
            // clamps to (20,22) either way and covers the button. The menu
            // is earned by a real click, as every retained portal's is.
            case "rpicker-open":
                ReactiveRoot(name).Render(
                    origin, ImGui.GetContentRegionAvail(), PickerSingleTree);
                break;
            case "rpicker-multi":
                ReactiveRoot(name).Render(
                    origin, ImGui.GetContentRegionAvail(), PickerMultiTree);
                break;
            case "text-input":
                Ui.TextInput(
                    "##text-input",
                    "Filter scene…",
                    _ => { },
                    new ControlStyle
                    {
                        Width = UiWidth.Fixed(272),
                    });
                break;
            // ::placeholder is what an EMPTY field shows; the fixture
            // therefore carries no value at all.
            case "input-placeholder":
                Ui.TextInput(
                    "##input-placeholder",
                    string.Empty,
                    _ => { },
                    new ControlStyle
                    {
                        Width = UiWidth.Fixed(272),
                    },
                    placeholder: "Filter scene…");
                break;
            // .input:focus, reached the way a user reaches it: the Tab in
            case "search-input":
                Ui.FilterPill(
                    "##search",
                    "Search",
                    _ => { },
                    "Search",
                    new ControlStyle
                    {
                        Width = UiWidth.Fixed(272),
                    });
                break;
            // The clear affordance under a REAL pointer: PointerFor parks
            // it on the reserved hit area, which wins hover arbitration
            // from the InputText it overlaps.
            case "search-clear-hover":
                Ui.FilterPill(
                    "##search-clear-hover",
                    "Search",
                    _ => { },
                    "Search",
                    new ControlStyle
                    {
                        Width = UiWidth.Fixed(272),
                    });
                break;
            case "dropdown-closed":
                Ui.Dropdown(
                    "##dropdown-closed",
                    DropdownItems,
                    0,
                    _ => { },
                    new ControlStyle
                    {
                        Width = UiWidth.Content,
                    });
                break;
            case "dropdown-open":
                if (frame == 0)
                    Ui.OpenPopover("##dropdown-open_popup");
                Ui.Dropdown(
                    "##dropdown-open",
                    DropdownItems,
                    0,
                    _ => { },
                    new ControlStyle
                    {
                        Width = UiWidth.Content,
                    });
                break;
            // ---- Reactive dropdown twins (PBI-015 wave H) -----------
            // Same stage origin, same fixture, same Content width; the
            // open state reaches its menu through a real click instead of
            // the legacy twin's staged OpenPopover, because the retained
            // portal's handle is derived from the element path.
            case "rdd-closed":
            case "rdd-open":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Dropdown
                    {
                        Items = DropdownItems,
                        OnChange = NoOpSelect,
                    });
                break;
            // ---- Genuinely scrolled twins (PBI-015 wave K) ----------
            // Ten items, one wheel notch down. The legacy twin stages its
            // menu the same way dropdown-open does; the reactive one earns
            // it with a real click. Both are then wheeled by the SAME
            // event on the SAME frame with the pointer at the SAME point.
            case "dd-scrolled":
                if (frame == 0)
                    Ui.OpenPopover("##dd-scrolled_popup");
                Ui.Dropdown(
                    "##dd-scrolled",
                    DropdownItemsScrolled,
                    0,
                    _ => { },
                    new ControlStyle
                    {
                        Width = UiWidth.Content,
                    });
                break;
            case "rdd-scrolled":
                ReactiveRoot(name).Render(
                    origin,
                    ImGui.GetContentRegionAvail(),
                    static () => new Dropdown
                    {
                        Items = DropdownItemsScrolled,
                        OnChange = NoOpSelect,
                    });
                break;
            case "color-palette":
                DrawPalette();
                break;
            case "sidebar-row":
            case "sidebar-row-hover":
                // Hover is NOT a flag here: the pointer parked over the
                // row rect by PointerFor is what Interactive.Reserve
                // hit-tests, so .row:hover comes from real input.
                DrawSidebar(selected: false);
                break;
            case "sidebar-row-selected":
            case "sidebar-row-selected-hover":
                // Same real pointer, over a SELECTED row: the deliberate
                // Poser deviation (selection stays dominant where Picto's
                // .row:hover::before out-specifies .selected::before).
                DrawSidebar(selected: true);
                break;
            case "sidebar-row-collapsed":
            // The pointer PointerFor parks on the arrow box is what
            // Interactive.Reserve hit-tests for the expander's own item,
            // so .expandArrow:hover .triangle comes from real input — the
            // row draws identically either way.
            case "sidebar-row-expander-hover":
                DrawSidebarTree(SidebarExpander.Collapsed);
                break;
            case "sidebar-row-expanded":
                DrawSidebarTree(SidebarExpander.Open);
                break;
            case "sidebar-row-drop":
                DrawSidebarDrop();
                break;
            case "property-row":
                // No retained Crystarium counterpart exists. An empty
                // candidate is intentional: the report marks the Picto
                // foreground as missing instead of comparing invented chrome.
                break;
            // Collapsed, expanded and header-hover all draw the SAME call;
            // only `open` and the parked pointer differ, so the chevron
            // rung each state reports comes from the component, never from
            // a fixture flag.
            case "section":
            case "section-hover":
                Ui.Section(
                    "##section",
                    "GENERAL",
                    origin,
                    272 * scale,
                    open: false,
                    _ => { },
                    _ => { });
                break;
            case "section-expanded":
                Ui.Section(
                    "##section",
                    "GENERAL",
                    origin,
                    272 * scale,
                    open: true,
                    _ => { },
                    _ => { });
                break;
            // ---- Reactive section twins (PBI-015 wave P) -------------
            // The legacy fixture is handed an explicit 272px measure, so
            // the retained root gets the same span rather than whatever
            // the cell leaves: the width is the fixture's, not the
            // canvas's, on both paths. Collapsed, expanded and header-
            // hover all build the SAME call; only `expanded` and the
            // parked pointer differ.
            case "rsection":
            case "rsection-hover":
                ReactiveRoot(name).Render(
                    origin,
                    new Vector2(
                        272f * scale, ImGui.GetContentRegionAvail().Y),
                    SectionTree);
                break;
            case "rsection-expanded":
                ReactiveRoot(name).Render(
                    origin,
                    new Vector2(
                        272f * scale, ImGui.GetContentRegionAvail().Y),
                    SectionExpandedTree);
                break;
            case "rsettings-frame":
                Ui.FloatingSurface.DrawChrome(
                    ImGui.GetWindowDrawList(),
                    origin,
                    origin + new Vector2(720f, 520f) * scale,
                    Ui.ActiveTheme.Radii.Window);
                ReactiveRoot(name).Render(
                    origin, new Vector2(720f, 520f) * scale, SettingsFrameTree);
                break;
            case "i-settings-frame":
                DrawImperativeSettingsFrame(origin, scale);
                break;
            case "rsegmented":
                ReactiveRoot(name).Render(
                    origin, new Vector2(420f, 120f) * scale, SegmentedTree);
                break;
            case "ricon-actions":
                ReactiveRoot(name).Render(
                    origin, new Vector2(200f, 32f) * scale, IconActionsTree);
                break;
            case "rswatches":
                ReactiveRoot(name).Render(
                    origin, new Vector2(340f, 60f) * scale, SwatchesTree);
                break;
            case "i-segmented":
                DrawImperativeSegmented(origin, scale);
                break;
            case "i-swatches":
                Ui.SwatchPalette(
                    "##i-swatches",
                    SwatchFixtureColors,
                    2,
                    static _ => { });
                break;
            case "i-icon-actions":
                DrawImperativeIconActions(origin, scale);
                break;
            case "tooltip":
                // The reference cell draws the KbdTooltip label box at the
                // stage origin with no anchor at all, so the fixture pins
                // the card there too: a Right placement is the only side
                // whose result does not depend on the card's own measured
                // width. Target right edge 18 + offset 6 = x 24; target
                // centre y 36 - half the 24px card = y 24. Both hold at
                // any global scale.
                Ui.HoverHelp.Explain(
                    "##tooltip",
                    new Vector2(6, 30) * scale,
                    new Vector2(18, 42) * scale,
                    "Undo",
                    "Ctrl+Z",
                    HoverHelpSide.Right);
                break;
            case "tooltip-pop-mid":
                // The same card, captured mid-entrance. Registration
                // starts at PopMidRegisterFrame so the 400ms open delay
                // expires with a known number of 1/60s entrance steps left
                // before the observed frame; see the constant.
                if (frame >= PopMidRegisterFrame)
                    Ui.HoverHelp.Explain(
                        "##tooltip-pop-mid",
                        new Vector2(6, 30) * scale,
                        new Vector2(18, 42) * scale,
                        "Undo",
                        "Ctrl+Z",
                        HoverHelpSide.Right);
                break;
            case "context-menu":
                if (frame == 0)
                {
                    // FloatingMenu is a retained static surface and Open
                    // TOGGLES an already-open menu closed — a prior batch
                    // entry leaving it open would capture a closing menu.
                    // Dismiss first so every entry opens fresh, exactly
                    // like an isolated capture.
                    Ui.FloatingMenu.DismissAll();
                    Ui.FloatingMenu.Open(
                        "##context-menu",
                        origin,
                        MenuItems);
                }
                Ui.FloatingMenu.Draw("##context-menu");
                break;
            case "modal":
                Ui.Modal(
                    "##modal",
                    true,
                    _ => { },
                    "Import pose",
                    () => Ui.Text(
                        "Apply heroic-stand.pose to Midona Rhel?"),
                    () =>
                    {
                        Ui.Button("Cancel", id: "##modal-cancel");
                        ImGui.SameLine(
                            0,
                            Ui.ActiveTheme.Page.ActionGap * scale);
                        Ui.Button(
                            "Import",
                            variant: ButtonVariant.Primary,
                            id: "##modal-import");
                    },
                    ModalSize.Small,
                    height: 220);
                break;
            case "rscrollarea":
                ReactiveRoot(name).Render(
                    origin, new Vector2(280f, 160f) * scale, ScrollAreaTree);
                break;
            case "rtreerow":
                // 250 wide, not the cell's 300: the strip and the badge are
                // RIGHT-anchored, so a row that overran the cell would cut the
                // very things this fixture exists to show.
                ReactiveRoot(name).Render(
                    origin, new Vector2(250f, 120f) * scale, TreeRowTree);
                break;
            case "i-treerow":
                DrawImperativeTreeRow(origin, scale);
                break;
            case "rfiledialog":
                if (frame == 0)
                {
                    fileDialog = new Ui.FileDialog(
                        "Import Pose", FileDialogExtensions)
                    {
                        Source = FileDialogListing.Instance,
                    };
                    // Rehome, not Open: Open claims the exclusive chain for a
                    // window nobody draws here, which would occlude the cell.
                    fileDialog.Rehome(FileDialogListing.Folder);
                }

                // PBI-016 phase 4a: the dialog behind this state is the
                // IMPERATIVE one now — the retained tree it was named for is
                // gone — so it paints its own glass and the fixture no longer
                // draws a second. The state renders i-filedialog until phase 6
                // retires it; the frozen golden PNG is what i-filedialog is
                // judged against.
                fileDialog!.RenderFrame(
                    origin,
                    new Vector2(
                        Ui.ActiveTheme.FileDialog.Width,
                        Ui.ActiveTheme.FileDialog.Height) * scale);
                break;
            case "i-filedialog":
                if (frame == 0)
                {
                    fileDialog = new Ui.FileDialog(
                        "Import Pose", FileDialogExtensions)
                    {
                        Source = FileDialogListing.Instance,
                    };
                    fileDialog.Rehome(FileDialogListing.Folder);
                }

                // No DrawChrome here: the imperative frame paints its own
                // glass, which the retained root left to its caller.
                fileDialog!.RenderFrame(
                    origin,
                    new Vector2(
                        Ui.ActiveTheme.FileDialog.Width,
                        Ui.ActiveTheme.FileDialog.Height) * scale);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name));
        }

        ImGui.End();
    }

    /// <summary>
    /// The Settings frame drawn imperatively: <c>Ui.WindowFrame</c> paints the
    /// chrome, the two bars and the rail band, and this fixture fills the rail
    /// the way a caller does. The rows are drawn from primitives rather than
    /// through SidebarRow — the accepted rail row has no pill inset and a full
    /// -opacity glyph, which SidebarRow's picto row does not — so the row draw
    /// moves into the shared row component when phase 1 lands it.
    /// </summary>
    private static void DrawImperativeSettingsFrame(Vector2 origin, float scale)
    {
        var theme = Ui.ActiveTheme;
        var rects = Ui.WindowFrame(
            "i-settings",
            origin,
            new Vector2(720f, 520f) * scale,
            new WindowFrameProps
            {
                Title = "Settings",
                OnClose = static () => { },
                CloseHelp = "Close settings",
                RailWidth = theme.Settings.NavigationWidth,
                FooterRight = static right =>
                {
                    right.Button(
                        "Cancel", static () => { },
                        style: ControlStyle.Comfortable);
                    right.Button(
                        "Save", static () => { },
                        style: ControlStyle.Comfortable,
                        variant: ButtonVariant.Primary);
                },
            });

        float inset = theme.Page.Inset * scale;
        var rowOrigin = rects.Rail.Min + new Vector2(inset);
        float rowWidth = rects.Rail.Size.X - inset * 2f;
        DrawImperativeNavRow(
            rowOrigin, rowWidth, "General", TablerIcon.Sliders, false, scale);
        DrawImperativeNavRow(
            rowOrigin + new Vector2(
                0f, theme.Controls.ListRowHeight * scale),
            rowWidth, "Display", TablerIcon.Monitor, true, scale);
    }

    private static void DrawImperativeNavRow(
        Vector2 min, float width, string label, TablerIcon icon,
        bool selected, float scale)
    {
        var theme = Ui.ActiveTheme;
        float height = theme.Controls.ListRowHeight * scale;
        var max = min + new Vector2(width, height);
        if (selected)
            ImGui.GetWindowDrawList().AddRectFilled(
                min,
                max,
                ImGui.ColorConvertFloat4ToU32(theme.Chrome.SidebarSelected),
                5f * scale);
        // A row-height icon slot at a 2px left margin, carrying the 14px
        // sidebar glyph; the label starts where the slot ends.
        float glyph = 14f * scale;
        var slotMin = new Vector2(min.X + 2f * scale, min.Y);
        var glyphMin = slotMin + new Vector2((height - glyph) * 0.5f);
        Ui.IconIn(glyphMin, glyphMin + new Vector2(glyph), icon);
        float labelX = slotMin.X + height;
        Ui.TextInBand(
            new Vector2(labelX, min.Y),
            new Vector2(max.X - labelX, height),
            label,
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Color = theme.Text,
            },
            besideIcon: true);
    }

    /// <summary>
    /// The same four segmented rows the reactive fixture builds, through the
    /// IMPERATIVE control. DEVIATION: the golden's two label rows are FORM
    /// rows, and PageForm is another agent's file this phase, so the band is
    /// seated by hand — the 34px FormRowHeight pitch, the 94px label column,
    /// the pill centred in the band — which is the geometry a form row
    /// resolves anyway. The two icon rows differ only in the trough shift.
    /// </summary>
    private static void DrawImperativeSegmented(Vector2 origin, float scale)
    {
        var theme = Ui.ActiveTheme;
        float band = theme.Controls.FormRowHeight * scale;
        float pill = theme.Controls.NavigationHeight * scale;
        var control = new ControlStyle { Width = UiWidth.Fixed(270) };
        var labelStyle = new TextStyle
        {
            Size = theme.Typography.LabelSize,
            Color = theme.FormLabel,
        };

        void LabelRow(int index, string label, int selected)
        {
            var bandMin = origin + new Vector2(0f, index * band);
            // The retained row centres the run's LINE BOX in the band and
            // snaps; TextInBand's ink-metric seat is a different rule and
            // would land a pixel off the golden.
            var measured = Ui.MeasureText(label, labelStyle);
            Ui.TextAt(
                new Vector2(
                    MathF.Round(bandMin.X),
                    MathF.Round(bandMin.Y + (band - measured.Y) * 0.5f)),
                label,
                labelStyle);
            ImGui.SetCursorScreenPos(bandMin + new Vector2(
                theme.Form.LabelColumnWidth * scale, (band - pill) * 0.5f));
            Ui.SegmentedControl(
                $"##i-segmented-{index}",
                SegmentedFixtureItems,
                selected,
                static _ => { },
                control);
        }

        LabelRow(0, "Entity sidebar", 0);
        LabelRow(1, "Inspector", 1);

        var icons = origin + new Vector2(0f, 2f * band);
        ImGui.SetCursorScreenPos(icons);
        Ui.SegmentedControl(
            "##i-segmented-icons",
            SegmentedFixtureIcons,
            1,
            static _ => { },
            itemDisabled: SegmentedFixtureDisabled);
        ImGui.SetCursorScreenPos(icons + new Vector2(0f, pill));
        Ui.SegmentedControl(
            "##i-segmented-icons-aligned",
            SegmentedFixtureIcons,
            1,
            static _ => { },
            alignFirstTabToCursor: true,
            itemDisabled: SegmentedFixtureDisabled);
    }

    /// <summary>The five icon-action shapes on one row through the IMPERATIVE
    /// entry points: the enum glyph, a registry name, a mirrored glyph, then
    /// the persistent toggle selected and slashed. 28px squares on the
    /// reactive fixture's 8px gap.</summary>
    private static void DrawImperativeIconActions(Vector2 origin, float scale)
    {
        float pitch = (Ui.ActiveTheme.Controls.ShellIconAction + 8f) * scale;
        void Seat(int index) =>
            ImGui.SetCursorScreenPos(origin + new Vector2(index * pitch, 0f));

        Seat(0);
        Ui.IconButton(TablerIcon.Settings, id: "##i-icon-enum");
        Seat(1);
        Ui.IconButton("x", id: "##i-icon-named");
        Seat(2);
        Ui.IconButton(
            TablerIcon.ArrowBackUp, id: "##i-icon-flipped", flipX: true);
        Seat(3);
        Ui.TemporaryIconToggle(
            TablerIcon.Eye, selected: true, id: "##i-icon-selected");
        Seat(4);
        Ui.TemporaryIconToggle(
            TablerIcon.Eye, selected: false, id: "##i-icon-slashed",
            slashed: true);
    }

    /// <summary>
    /// Picto's <c>.palette</c> pill holding four <c>.swatchWrap</c>es.
    /// Both halves are now real Crystarium primitives:
    /// <c>Ui.ColorPalette</c> paints the container chrome and owns the
    /// box model (border, 6px padding, 2px gap, vertical centring in the
    /// 24px content box) that this fixture used to reproduce by hand.
    /// </summary>
    private static void DrawPalette()
    {
        Vector4[] colors =
        [
            new(0x32 / 255f, 0x97 / 255f, 1, 1),
            new(0x9b / 255f, 0x7c / 255f, 1, 1),
            new(0x51 / 255f, 0xcf / 255f, 0x66 / 255f, 1),
            new(1, 0x6b / 255f, 0x6b / 255f, 1),
        ];
        Ui.ColorPalette(
            colors.Length,
            i => Ui.Swatch($"##swatch-{i}", colors[i], active: false));
    }

    private static void DrawSidebar(bool selected)
    {
        var props = new TreeRowProps
        {
            Icon = TablerIcon.User,
            Selected = selected,
        };
        Ui.TreeRow(
            "##sidebar-row",
            "Midona Rhel",
            in props,
            new ControlStyle
            {
                Width = UiWidth.Fixed(272),
            });
    }

    /// <summary>
    /// A ROOT row that discloses: the chevron in its own 16px slot beside the
    /// mark, with the badge right-aligned at the content edge, so the expander
    /// in both rotations and the count are one capture. Deliberately depth 0 —
    /// a lone nested row would draw a trunk with no neighbours to join, and the
    /// guide shapes are gated by the six-row <c>i-treerow</c> scene instead.
    /// </summary>
    private static void DrawSidebarTree(SidebarExpander expander)
    {
        var props = new TreeRowProps
        {
            Icon = TablerIcon.Folder,
            Badge = "12",
            Expander = expander,
        };
        Ui.TreeRow(
            "##sidebar-row",
            "Party members",
            in props,
            new ControlStyle
            {
                Width = UiWidth.Fixed(272),
            });
    }

    /// <summary><see cref="TreeRowProps.DropTarget"/> is the row's only
    /// drop-state input; it paints picto's <c>.dropInside::before</c>
    /// (primary-10 over a 1px primary-30 hairline).</summary>
    private static void DrawSidebarDrop()
    {
        var props = new TreeRowProps
        {
            Icon = TablerIcon.User,
            DropTarget = true,
        };
        Ui.TreeRow(
            "##sidebar-row",
            "Midona Rhel",
            in props,
            new ControlStyle
            {
                Width = UiWidth.Fixed(272),
            });
    }

    /// <summary>
    /// The SAME six-row tree the reactive fixture builds, through the
    /// imperative row. 250 wide, not the cell's 300: the strip and the badge
    /// are right-anchored, so a row that overran the cell would cut the very
    /// things this fixture exists to show. The golden is the reactive capture,
    /// so the two states are compared byte-for-byte, not by eye.
    /// </summary>
    private static void DrawImperativeTreeRow(Vector2 origin, float scale)
    {
        var row = new ControlStyle { Width = UiWidth.Fixed(250) };
        var square = new ControlStyle
        {
            Width = UiWidth.Fixed(20),
            Height = UiHeight.Fixed(20),
        };
        // The cell has no container to indent the flow, so each band is seated
        // at the stage origin explicitly — the reactive twin's Column does the
        // same job on its side.
        float pitch = Ui.ActiveTheme.Controls.ListRowHeight * scale;
        void Seat(int index) =>
            ImGui.SetCursorScreenPos(origin + new Vector2(0f, index * pitch));

        Seat(0);
        var actor = new TreeRowProps
        {
            Icon = TablerIcon.User,
            Expander = SidebarExpander.Open,
        };
        Ui.TreeRow("##i-treerow-actor", "Midona Rhel", in actor, row);

        Seat(1);
        var spine = new TreeRowProps { Depth = 1, ActionSlots = 2 };
        Ui.TreeRow(
            "##i-treerow-spine", "Spine", in spine, out var strip, row);
        ImGui.SetCursorScreenPos(strip);
        Ui.IconButton(
            TablerIcon.Crosshair, id: "##i-treerow-target", style: square);
        ImGui.SetCursorScreenPos(strip + new Vector2(22f * scale, 0f));
        Ui.TemporaryIconToggle(
            TablerIcon.Eye, selected: true, id: "##i-treerow-visible",
            style: square);

        // The FORK: a nested row that itself discloses — the split trunk with
        // the cutout around its chevron — plus its child, whose trunk mask
        // carries the continuing depth-1 line.
        Seat(2);
        var arms = new TreeRowProps
        {
            Depth = 1,
            Expander = SidebarExpander.Open,
        };
        Ui.TreeRow("##i-treerow-arms", "Arms", in arms, row);

        Seat(3);
        var hand = new TreeRowProps
        {
            Depth = 2,
            Trunks = 0b10,
            IsLastChild = true,
        };
        Ui.TreeRow("##i-treerow-l-hand", "L Hand", in hand, row);

        Seat(4);
        var head = new TreeRowProps
        {
            Depth = 1,
            IsLastChild = true,
            Selected = true,
            Badge = "12",
        };
        Ui.TreeRow("##i-treerow-head", "Head", in head, row);

        Seat(5);
        var companion = new TreeRowProps
        {
            Icon = TablerIcon.Paw,
            Expander = SidebarExpander.Collapsed,
        };
        Ui.TreeRow("##i-treerow-companion", "Chocobo", in companion, row);
    }
}
