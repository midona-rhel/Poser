using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Config;

namespace Poser.UI.Views;

public sealed class SettingsViewModel
{
    public int Category = 1;
    public float BoneDotRadius = 5f;
    public Vector4 OverlaySelected =
        Crystarium.ActiveTheme.Palette.Primary;
    public Vector4 OverlayHovered =
        Crystarium.ActiveTheme.Palette.White;
    public Vector4 OverlayInactive =
        Crystarium.ActiveTheme.TextMuted;
    public Vector4 OverlayIkChain =
        Crystarium.ActiveTheme.Warning;
    public Vector4 OverlayMirrored =
        Crystarium.ActiveTheme.Palette.AxisY;
    public bool NsfwBones;
    public bool AnonymousMode = true;
    public UITheme Theme = UITheme.Dark;
    public int AccentIndex;

    public bool OpenOnGPose = true;
    public bool CloseWithGPose;

    public bool ShowSkeletonLines = true;
    public float BoneLineThickness = 1.0f;
    public float BoneLineOpacity = 0.23f;

    public int SidebarDock;
    public int InspectorDock = 1;
    public bool TreeGuides = true;

    public (string Action, string Binding)[] Keybinds =
    {
        ("Undo", "Ctrl+Z"),
        ("Redo", "Ctrl+Y"),
        ("Translate mode", "Ctrl+1"),
        ("Rotate mode", "Ctrl+2"),
        ("Scale mode", "Ctrl+3"),
        ("Universal mode", "Ctrl+4"),
        ("Hide UI", "Ctrl+H"),
    };
    public int RebindingIndex = -1;

    public string Version = "dev";

    public Action? OnSave;
    public Action? OnCancel;
    public Action? OnClose;
    public Action? OnOpenRepository;
    public Action<UITheme>? OnThemePreview;
}

/// <summary>
/// The Settings chassis, DECLARED: one root for the frame — the header and
/// footer action bars, the navigation rail, the 1px rule between rail and
/// page — and one for the page, hosted inside the shared scroll seam exactly
/// as the shell hosts a pane. The window chrome stays the one legacy paint
/// seam, and the rebind capture is the named raw-input boundary, running
/// after the tree as it always has.
/// </summary>
public sealed class SettingsView
{
    public static float DesignWidth =>
        Crystarium.ActiveTheme.Settings.Width;

    public static float DesignHeight =>
        Crystarium.ActiveTheme.Settings.Height;

    private static readonly (TablerIcon Icon, string Label)[] Nav =
    {
        (TablerIcon.Sliders, "General"),
        (TablerIcon.Monitor, "Display"),
        (TablerIcon.Bone, "Skeleton"),
        (TablerIcon.LayoutPanel, "UI"),
        (TablerIcon.Keyboard, "Keybinds"),
        (TablerIcon.Info, "About"),
    };

    private static readonly string[] DockOptions =
        ["Left", "Right", "Floating", "Hidden"];

    private static readonly string[] ThemeLabels =
    [
        "Auto",
        "Light",
        "Light Gray",
        "Gray",
        "Dark",
        "Blue",
        "Purple",
    ];

    private static readonly Vector4[] ThemeSwatches =
    [
        new(0.50f, 0.50f, 0.50f, 1f),
        new(1f, 1f, 1f, 1f),
        new(200f / 255f, 202f / 255f, 205f / 255f, 1f),
        new(68f / 255f, 68f / 255f, 68f / 255f, 1f),
        new(1f / 255f, 1f / 255f, 1f / 255f, 1f),
        new(40f / 255f, 53f / 255f, 110f / 255f, 1f),
        new(70f / 255f, 50f / 255f, 117f / 255f, 1f),
    ];

    private readonly UiRoot _frame = new();
    private readonly UiRoot _page = new();
    private readonly Action<LegacyCrystarium.ScrollRegionScope> _pageBody;

    /// <summary>The vm the CURRENT frame binds. The hoisted handlers below
    /// close over the field, so the binder replacing its vm on open costs no
    /// rebinding — every dispatch reads whichever vm this frame drew.</summary>
    private SettingsViewModel? _vm;

    /// <summary>The control-cell width the page scroll resolved this frame,
    /// which segmented tabs need BEFORE the solver runs.</summary>
    private float _controlWidth;

    private float _pageHeightPx;

    // ── hoisted handlers ─────────────────────────────────────────────────
    // A build path may allocate no delegate, so every callback the tree names
    // is a field closing over `this` and dispatching against `_vm`.
    private readonly Action<int> _setCategory;
    private readonly Action _close;
    private readonly Action _cancel;
    private readonly Action _save;
    private readonly Action _openRepository;
    private readonly Action<bool> _setOpenOnGPose;
    private readonly Action<bool> _setCloseWithGPose;
    private readonly Action<float> _setDotRadius;
    private readonly Action<Vector4> _setOverlaySelected;
    private readonly Action<Vector4> _setOverlayHovered;
    private readonly Action<Vector4> _setOverlayInactive;
    private readonly Action<Vector4> _setOverlayIkChain;
    private readonly Action<Vector4> _setOverlayMirrored;
    private readonly Action<bool> _setNsfwBones;
    private readonly Action<bool> _setAnonymousMode;
    private readonly Action<int> _setTheme;
    private readonly Action<int> _setAccent;
    private readonly Action<bool> _setShowLines;
    private readonly Action<float> _setLineThickness;
    private readonly Action<float> _setLineOpacity;
    private readonly Action<int> _setSidebarDock;
    private readonly Action<int> _setInspectorDock;
    private readonly Action<bool> _setTreeGuides;
    private readonly Action[] _toggleRebind;

    public SettingsView()
    {
        _pageBody = PageBody;
        _setCategory = next => _vm!.Category = next;
        _close = () => _vm!.OnClose?.Invoke();
        _cancel = () => _vm!.OnCancel?.Invoke();
        _save = () => _vm!.OnSave?.Invoke();
        _openRepository = () => _vm!.OnOpenRepository?.Invoke();
        _setOpenOnGPose = next => _vm!.OpenOnGPose = next;
        _setCloseWithGPose = next => _vm!.CloseWithGPose = next;
        _setDotRadius = next => _vm!.BoneDotRadius = next;
        _setOverlaySelected = next => _vm!.OverlaySelected = next;
        _setOverlayHovered = next => _vm!.OverlayHovered = next;
        _setOverlayInactive = next => _vm!.OverlayInactive = next;
        _setOverlayIkChain = next => _vm!.OverlayIkChain = next;
        _setOverlayMirrored = next => _vm!.OverlayMirrored = next;
        _setNsfwBones = next => _vm!.NsfwBones = next;
        _setAnonymousMode = next => _vm!.AnonymousMode = next;
        _setTheme = next =>
        {
            _vm!.Theme = (UITheme)next;
            _vm.OnThemePreview?.Invoke(_vm.Theme);
        };
        _setAccent = next => _vm!.AccentIndex = next;
        _setShowLines = next => _vm!.ShowSkeletonLines = next;
        _setLineThickness = next => _vm!.BoneLineThickness = next;
        _setLineOpacity = next => _vm!.BoneLineOpacity = next;
        _setSidebarDock = next => _vm!.SidebarDock = next;
        _setInspectorDock = next => _vm!.InspectorDock = next;
        _setTreeGuides = next => _vm!.TreeGuides = next;
        _toggleRebind = new Action[7];
        for (int i = 0; i < _toggleRebind.Length; i++)
        {
            int index = i;
            _toggleRebind[i] = () => _vm!.RebindingIndex =
                _vm.RebindingIndex == index ? -1 : index;
        }
    }

    /// <summary>Everything one frame's build is TOLD; the view reference is
    /// what the static builder reaches its state through.</summary>
    private readonly record struct Props(SettingsView View);

    public void Draw(SettingsViewModel vm, Vector2 origin)
    {
        _vm = vm;
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(
            theme.Settings.Width,
            theme.Settings.Height) * scale;
        var min = origin;
        var max = origin + size;
        float barHeight = theme.Floating.ModalBarHeight * scale;
        float navigationWidth = theme.Settings.NavigationWidth * scale;

        LegacyCrystarium.FloatingSurface.DrawChrome(
            ImGui.GetWindowDrawList(),
            min,
            max,
            theme.Radii.Window);

        var props = new Props(this);
        _frame.Render(
            min, size, in props, static (in Props p) => p.View.BuildFrame());

        // The page is hosted by the shared scroll seam, exactly as the shell
        // hosts a pane: the region owns the gutter and the viewport, the
        // declared tree renders inside it.
        var pageOrigin = new Vector2(
            min.X + navigationWidth,
            min.Y + barHeight);
        _pageHeightPx = max.Y - barHeight - pageOrigin.Y;
        ImGui.SetCursorScreenPos(pageOrigin);
        LegacyCrystarium.ScrollRegion(
            "##settings-page-scroll",
            (max.X - pageOrigin.X) / scale,
            _pageHeightPx / scale,
            _pageBody);

        if (vm.RebindingIndex >= 0)
            CaptureRebind(vm);
    }

    private void PageBody(LegacyCrystarium.ScrollRegionScope region)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        _controlWidth = MathF.Min(
                region.ContentWidth - theme.Page.Inset,
                theme.Page.MaximumContentWidth)
            - theme.Form.LabelColumnWidth;
        var props = new Props(this);
        _page.Render(
            ImGui.GetCursorScreenPos(),
            new Vector2(region.ContentWidth * scale, _pageHeightPx),
            in props,
            static (in Props p) => p.View.BuildPage());
    }

    private UiNode BuildFrame()
    {
        var vm = _vm!;
        var theme = Crystarium.ActiveTheme;
        var rows = new UiNode[Nav.Length];
        for (int i = 0; i < Nav.Length; i++)
        {
            rows[i] = new Element
            {
                Sheet = SheetFamily.NavRow,
                Selected = vm.Category == i,
                Index = i,
                On = new Listeners { OnPick = _setCategory },
                Key = i,
                Children =
                [
                    new Stack
                    {
                        Sheet = SheetFamily.NavIconSlot,
                        Children = new Glyph
                        {
                            Icon = Nav[i].Icon,
                            Size = theme.Controls.SmallIconSize,
                        },
                    },
                    new Label { Text = Nav[i].Label, Sheet = SheetFamily.NavLabel },
                ],
            };
        }

        return new Column
        {
            Style = new()
            {
                Layout = new() { Width = UiDim.Fill, Height = UiDim.Fill },
            },
            Children =
            [
                new ActionBar
                {
                    Left = ActionBar.Title("Settings"),
                    Right = new IconAction
                    {
                        Icon = TablerIcon.X,
                        OnClick = _close,
                        Help = "Close settings",
                    },
                    Separator = ActionBarSeparator.Bottom,
                    Key = "header",
                },
                new Row
                {
                    Style = new()
                    {
                        Layout = new() { Width = UiDim.Fill, Height = UiDim.Fill },
                    },
                    Children =
                    [
                        new Column
                        {
                            Sheet = SheetFamily.NavRail,
                            Style = new()
                            {
                                Layout = new()
                                {
                                    Width = UiDim.Fixed(
                                        theme.Settings.NavigationWidth - 1f),
                                },
                            },
                            Children = UiChildren.Create(rows),
                        },
                        // The rail/page rule, flowed as the rail's last pixel
                        // exactly where the imperative fill put it.
                        new Element
                        {
                            Sheet = SheetFamily.BarRule,
                            Style = new()
                            {
                                Layout = new()
                                {
                                    Width = UiDim.Fixed(1f),
                                    Height = UiDim.Fill,
                                },
                            },
                        },
                    ],
                },
                new ActionBar
                {
                    Right =
                    [
                        new Button { Label = "Cancel", OnClick = _cancel },
                        new Button
                        {
                            Label = "Save",
                            Style = ButtonStyle.Primary,
                            OnClick = _save,
                        },
                    ],
                    Separator = ActionBarSeparator.Top,
                    FooterChrome = true,
                    Key = "footer",
                },
            ],
        };
    }

    private UiNode BuildPage()
    {
        var vm = _vm!;
        return vm.Category switch
        {
            0 => BuildGeneral(vm),
            1 => BuildDisplay(vm),
            2 => BuildSkeleton(vm),
            3 => BuildUi(vm),
            4 => BuildKeybinds(vm),
            _ => BuildAbout(vm),
        };
    }

    private UiNode BuildGeneral(SettingsViewModel vm) => Crystarium.Page(
    [
        new Section
        {
            Title = "BEHAVIOR",
            NoDivider = true,
            Key = "behavior",
            Children =
            [
                Crystarium.FormSwitch(
                    "Open with GPose", vm.OpenOnGPose, _setOpenOnGPose,
                    help: "Show Poser automatically when entering GPose"),
                Crystarium.FormSwitch(
                    "Close with GPose", vm.CloseWithGPose, _setCloseWithGPose,
                    help: "Hide all Poser windows when leaving GPose"),
            ],
        },
    ]);

    private UiNode BuildDisplay(SettingsViewModel vm) => Crystarium.Page(
    [
        new Section
        {
            Title = "BONE OVERLAY",
            NoDivider = true,
            Key = "bone-overlay",
            Children =
            [
                Crystarium.FormSlider(
                    "Bone dot radius", vm.BoneDotRadius, 2f, 12f,
                    _setDotRadius, format: "0 px"),
                Crystarium.FormColorWells(
                    "Overlay colors",
                    [
                        Crystarium.ColorWellCell(
                            "Selected", vm.OverlaySelected, _setOverlaySelected),
                        Crystarium.ColorWellCell(
                            "Hovered", vm.OverlayHovered, _setOverlayHovered),
                        Crystarium.ColorWellCell(
                            "Inactive", vm.OverlayInactive, _setOverlayInactive),
                        Crystarium.ColorWellCell(
                            "IK chain", vm.OverlayIkChain, _setOverlayIkChain),
                        Crystarium.ColorWellCell(
                            "Mirrored", vm.OverlayMirrored, _setOverlayMirrored),
                    ]),
            ],
        },
        new Section
        {
            Title = "FILTERS & PRIVACY",
            Key = "filters",
            Children =
            [
                Crystarium.FormSwitch(
                    "NSFW bones", vm.NsfwBones, _setNsfwBones,
                    help: "Show IVCS and extended bone groups"),
                Crystarium.FormSwitch(
                    "Anonymous mode", vm.AnonymousMode, _setAnonymousMode,
                    help: "Mask character names throughout the UI"),
            ],
        },
        new Section
        {
            Title = "THEME",
            Key = "theme",
            Children =
            [
                Crystarium.FormSwatches(
                    "Theme", ThemeSwatches, (int)vm.Theme, _setTheme,
                    ThemeLabels),
                Crystarium.FormSwatches(
                    "Accent",
                    Crystarium.ActiveTheme.Settings.AccentOptions,
                    vm.AccentIndex,
                    _setAccent),
            ],
        },
    ]);

    private UiNode BuildSkeleton(SettingsViewModel vm) => Crystarium.Page(
    [
        new Section
        {
            Title = "SKELETON LINES",
            NoDivider = true,
            Key = "skeleton-lines",
            Children =
            [
                Crystarium.FormSwitch(
                    "Show lines", vm.ShowSkeletonLines, _setShowLines,
                    help: "Connect parent and child bones in the overlay"),
                Crystarium.FormSlider(
                    "Line thickness", vm.BoneLineThickness, 0.5f, 4f,
                    _setLineThickness, format: "0.0 px"),
                Crystarium.FormSlider(
                    "Line opacity", vm.BoneLineOpacity, 0f, 1f,
                    _setLineOpacity, format: "0%"),
            ],
        },
    ]);

    private UiNode BuildUi(SettingsViewModel vm) => Crystarium.Page(
    [
        new Section
        {
            Title = "LAYOUT",
            NoDivider = true,
            Key = "layout",
            Children =
            [
                Crystarium.FormSegmented(
                    "Entity sidebar", DockOptions, vm.SidebarDock,
                    _setSidebarDock, _controlWidth),
                Crystarium.FormSegmented(
                    "Inspector", DockOptions, vm.InspectorDock,
                    _setInspectorDock, _controlWidth),
            ],
        },
        new Section
        {
            Title = "TREE",
            Key = "tree",
            Children = Crystarium.FormSwitch(
                "Tree guide lines", vm.TreeGuides, _setTreeGuides,
                help: "Show hierarchy connector lines"),
        },
    ]);

    private UiNode BuildKeybinds(SettingsViewModel vm)
    {
        var rows = new UiNode[vm.Keybinds.Length];
        for (int i = 0; i < vm.Keybinds.Length; i++)
        {
            bool rebinding = vm.RebindingIndex == i;
            rows[i] = Crystarium.FormReadOnlyActions(
                vm.Keybinds[i].Action,
                rebinding ? "Press a key…" : vm.Keybinds[i].Binding,
                unavailable: false,
                [
                    new Button
                    {
                        Label = rebinding ? "Cancel" : "Rebind",
                        Dense = true,
                        OnClick = _toggleRebind[i],
                    },
                ],
                key: i);
        }

        return Crystarium.Page(
        [
            new Section
            {
                Title = "KEYBINDS",
                NoDivider = true,
                Key = "keybinds",
                Children = UiChildren.Create(rows),
            },
        ]);
    }

    private UiNode BuildAbout(SettingsViewModel vm) => Crystarium.Page(
    [
        new Section
        {
            Title = "ABOUT",
            NoDivider = true,
            Key = "about",
            Children =
            [
                Crystarium.FormReadOnly("Poser", vm.Version),
                Crystarium.FormReadOnly("Stack", "Crystarium · PosingCore"),
                Crystarium.FormActions(
                    "Source",
                    new Button
                    {
                        Label = "Open repository",
                        Dense = true,
                        OnClick = _openRepository,
                    }),
                Crystarium.FormStatus(
                    "Design system transcribed from Picto. Brio and Ktisis are interaction references."),
            ],
        },
    ]);

    private static void CaptureRebind(SettingsViewModel vm)
    {
        var io = ImGui.GetIO();
        for (var key = ImGuiKey.A; key <= ImGuiKey.F12; key++)
        {
            if (key is ImGuiKey.LeftCtrl
                or ImGuiKey.RightCtrl
                or ImGuiKey.LeftShift
                or ImGuiKey.RightShift
                or ImGuiKey.LeftAlt
                or ImGuiKey.RightAlt)
                continue;
            if (!ImGui.IsKeyPressed(key))
                continue;

            string name = key.ToString();
            if (name.StartsWith("_"))
                name = name[1..];
            string binding =
                (io.KeyCtrl ? "Ctrl+" : "")
                + (io.KeyShift ? "Shift+" : "")
                + (io.KeyAlt ? "Alt+" : "")
                + name;
            vm.Keybinds[vm.RebindingIndex] =
                (vm.Keybinds[vm.RebindingIndex].Action, binding);
            vm.RebindingIndex = -1;
            return;
        }
        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            vm.RebindingIndex = -1;
    }
}
