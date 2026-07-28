using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

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
}

/// <summary>
/// Settings shell whose ordinary controls use the shared Page, Form,
/// ScrollRegion, and ActionBar compositions.
/// </summary>
public static class SettingsView
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

    public static void Draw(SettingsViewModel vm, Vector2 origin)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(
            theme.Settings.Width,
            theme.Settings.Height) * scale;
        var min = origin;
        var max = origin + size;
        float barHeight = theme.Floating.ModalBarHeight * scale;
        float navigationWidth =
            theme.Settings.NavigationWidth * scale;
        float inset = theme.Floating.HeaderInset * scale;
        var bodyMin = new Vector2(min.X, min.Y + barHeight);
        var bodyMax = new Vector2(max.X, max.Y - barHeight);
        var drawList = ImGui.GetWindowDrawList();

        Crystarium.FloatingSurface.DrawChrome(
            drawList,
            min,
            max,
            theme.Radii.Window);

        Crystarium.ActionBar(
            "settings-header",
            min + new Vector2(inset, 0f),
            new Vector2(size.X - inset * 2f, barHeight),
            left => left.Label("Settings"),
            right => right.Icon(
                TablerIcon.X,
                () => vm.OnClose?.Invoke(),
                "Close settings"),
            ActionBarSeparator.Bottom);

        drawList.AddRectFilled(
            bodyMin,
            new Vector2(bodyMin.X + navigationWidth, bodyMax.Y),
            ImGui.ColorConvertFloat4ToU32(theme.SurfaceRaised));
        drawList.AddRectFilled(
            new Vector2(
                bodyMin.X + navigationWidth
                    - MathF.Max(1f, scale),
                bodyMin.Y),
            new Vector2(
                bodyMin.X + navigationWidth,
                bodyMax.Y),
            ImGui.ColorConvertFloat4ToU32(theme.FormSeparator));

        float navigationInset = theme.Page.Inset * scale;
        ImGui.SetCursorScreenPos(
            bodyMin + new Vector2(navigationInset));
        Crystarium.ScrollRegion(
            "##settings-navigation",
            theme.Settings.NavigationWidth
                - theme.Page.Inset * 2f,
            (bodyMax.Y - bodyMin.Y) / scale
                - theme.Page.Inset * 2f,
            region =>
            {
                for (int i = 0; i < Nav.Length; i++)
                {
                    int category = i;
                    if (region.ListRow(
                            $"##settings-nav-{i}",
                            Nav[i].Label,
                            Nav[i].Icon,
                            selected: vm.Category == i))
                        vm.Category = category;
                }
            });

        var pageOrigin = new Vector2(
            bodyMin.X + navigationWidth,
            bodyMin.Y);
        float pageWidth = max.X - pageOrigin.X;
        float pageHeight = bodyMax.Y - pageOrigin.Y;
        ImGui.SetCursorScreenPos(pageOrigin);
        Crystarium.ScrollRegion(
            "##settings-page-scroll",
            pageWidth / scale,
            pageHeight / scale,
            region =>
            {
                var contentOrigin = ImGui.GetCursorScreenPos();
                Crystarium.Page(
                    "settings-page",
                    contentOrigin,
                    new Vector2(
                        region.ContentWidth * scale,
                        pageHeight),
                    page => DrawPage(vm, page));
            });

        drawList.AddRectFilled(
            new Vector2(min.X, bodyMax.Y),
            max,
            ImGui.ColorConvertFloat4ToU32(
                theme.Chrome.ModalFooter),
            theme.Radii.Window * scale,
            ImDrawFlags.RoundCornersBottom);
        Crystarium.ActionBar(
            "settings-footer",
            new Vector2(min.X + inset, bodyMax.Y),
            new Vector2(size.X - inset * 2f, barHeight),
            _ => { },
            right =>
            {
                right.Button(
                    "Cancel",
                    () => vm.OnCancel?.Invoke(),
                    style: ControlStyle.Comfortable);
                right.Button(
                    "Save",
                    () => vm.OnSave?.Invoke(),
                    style: ControlStyle.Comfortable with
                    {
                        Primary = true,
                    });
            });

        if (vm.RebindingIndex >= 0)
            CaptureRebind(vm);
    }

    private static void DrawPage(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        switch (vm.Category)
        {
            case 0:
                DrawGeneral(vm, page);
                break;
            case 1:
                DrawDisplay(vm, page);
                break;
            case 2:
                DrawSkeleton(vm, page);
                break;
            case 3:
                DrawUi(vm, page);
                break;
            case 4:
                DrawKeybinds(vm, page);
                break;
            default:
                DrawAbout(vm, page);
                break;
        }
    }

    private static void DrawGeneral(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("BEHAVIOR", form =>
        {
            form.Switch(
                "Open with GPose",
                vm.OpenOnGPose,
                next => vm.OpenOnGPose = next,
                "Show Poser automatically when entering GPose");
            form.Switch(
                "Close with GPose",
                vm.CloseWithGPose,
                next => vm.CloseWithGPose = next,
                "Hide all Poser windows when leaving GPose");
        });
    }

    private static void DrawDisplay(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("BONE OVERLAY", form =>
        {
            form.Slider(
                "Bone dot radius",
                vm.BoneDotRadius,
                2f,
                12f,
                next => vm.BoneDotRadius = next,
                format: "0 px");
            form.ColorWells("Overlay colors", wells =>
            {
                wells.Well(
                    "Selected",
                    vm.OverlaySelected,
                    next => vm.OverlaySelected = next);
                wells.Well(
                    "Hovered",
                    vm.OverlayHovered,
                    next => vm.OverlayHovered = next);
                wells.Well(
                    "Inactive",
                    vm.OverlayInactive,
                    next => vm.OverlayInactive = next);
                wells.Well(
                    "IK chain",
                    vm.OverlayIkChain,
                    next => vm.OverlayIkChain = next);
                wells.Well(
                    "Mirrored",
                    vm.OverlayMirrored,
                    next => vm.OverlayMirrored = next);
            });
        });
        page.Section("FILTERS & PRIVACY", form =>
        {
            form.Switch(
                "NSFW bones",
                vm.NsfwBones,
                next => vm.NsfwBones = next,
                "Show IVCS and extended bone groups");
            form.Switch(
                "Anonymous mode",
                vm.AnonymousMode,
                next => vm.AnonymousMode = next,
                "Mask character names throughout the UI");
        });
        page.Section("THEME", form =>
            form.Swatches(
                "Accent",
                Crystarium.ActiveTheme.Settings.AccentOptions,
                vm.AccentIndex,
                next => vm.AccentIndex = next));
    }

    private static void DrawSkeleton(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("SKELETON LINES", form =>
        {
            form.Switch(
                "Show lines",
                vm.ShowSkeletonLines,
                next => vm.ShowSkeletonLines = next,
                "Connect parent and child bones in the overlay");
            form.Slider(
                "Line thickness",
                vm.BoneLineThickness,
                0.5f,
                4f,
                next => vm.BoneLineThickness = next,
                format: "0.0 px");
            form.Slider(
                "Line opacity",
                vm.BoneLineOpacity,
                0f,
                1f,
                next => vm.BoneLineOpacity = next,
                format: "0%");
        });
    }

    private static void DrawUi(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("LAYOUT", form =>
        {
            form.Segmented(
                "Entity sidebar",
                DockOptions,
                vm.SidebarDock,
                next => vm.SidebarDock = next);
            form.Segmented(
                "Inspector",
                DockOptions,
                vm.InspectorDock,
                next => vm.InspectorDock = next);
        });
        page.Section("TREE", form =>
            form.Switch(
                "Tree guide lines",
                vm.TreeGuides,
                next => vm.TreeGuides = next,
                "Show hierarchy connector lines"));
    }

    private static void DrawKeybinds(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("KEYBINDS", form =>
        {
            for (int i = 0; i < vm.Keybinds.Length; i++)
            {
                int index = i;
                bool rebinding = vm.RebindingIndex == index;
                form.ReadOnlyWithActions(
                    vm.Keybinds[index].Action,
                    rebinding
                        ? "Press a key…"
                        : vm.Keybinds[index].Binding,
                    actions => actions.Button(
                        rebinding ? "Cancel" : "Rebind",
                        () => vm.RebindingIndex =
                            rebinding ? -1 : index));
            }
        });
    }

    private static void DrawAbout(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("ABOUT", form =>
        {
            form.ReadOnly("Poser", vm.Version);
            form.ReadOnly("Stack", "Crystarium · PosingCore");
            form.Actions("Source", actions => actions.Button(
                "Open repository",
                () => vm.OnOpenRepository?.Invoke()));
            form.Status(
                "Design system transcribed from Picto. Brio and Ktisis are interaction references.");
        });
    }

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
