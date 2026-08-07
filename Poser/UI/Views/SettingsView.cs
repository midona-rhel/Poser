using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Config;

namespace Poser.UI.Views;

/// <summary>One configured library root, edited free of the persisted
/// <c>LibrarySourceConfig</c> until Save.</summary>
public sealed class LibrarySourceVm
{
    public string Name = "";
    public string Path = "";
    public bool Enabled = true;
}

public sealed class SettingsViewModel
{
    public int Category = 1;
    public float BoneDotRadius = 5f;
    // Mirrors SkeletonConfiguration defaults: selected/hovered come from the
    // accent (Palette.Primary until AccentIndex drives the theme), the rest
    // are muted fixed tones — no theme token matches them (overlay colors
    // must be opaque; TextMuted-style alpha tones vanish over scenery).
    public Vector4 OverlaySelected =
        Crystarium.ActiveTheme.Palette.Primary;
    public Vector4 OverlayHovered = Vector4.Lerp(
        Crystarium.ActiveTheme.Palette.Primary, Vector4.One, 0.35f);
    public Vector4 OverlayInactive =
        new(148f / 255f, 163f / 255f, 184f / 255f, 1f);
    public Vector4 OverlayIkChain =
        new(217f / 255f, 165f / 255f, 68f / 255f, 1f);
    public Vector4 OverlayMirrored =
        new(194f / 255f, 123f / 255f, 160f / 255f, 1f);
    public bool NsfwBones;
    public bool AnonymousMode = true;
    public UITheme Theme = UITheme.Dark;
    public int AccentIndex;

    public bool OpenOnGPose = true;
    public bool CloseWithGPose;
    public bool PreservePoseAcrossRedraws = true;
    public bool FollowGameTarget = true;
    public bool TargetFollowsSelection;

    public bool AutoSaveEnabled = true;
    public float AutoSaveIntervalSeconds = 60f;
    /// <summary>Free numeric text, not a bounded slider: a shoot with hundreds
    /// of recovery points is a legitimate setup. Held as the raw string the
    /// user is typing and parsed at the config boundary, so a half-typed value
    /// never collapses to a number mid-keystroke.</summary>
    public string AutoSaveMaxKept = "10";
    public bool AutoSaveCleanOnExit;
    /// <summary>The auto-save root on disk, for the Open-in-Explorer row.
    /// Empty when the binder has no auto-save service to ask.</summary>
    public string AutoSaveFolder = "";

    public bool ShowSkeletonLines = true;
    public float BoneLineThickness = 1.0f;
    public float BoneLineOpacity = 0.23f;

    public int SidebarDock;
    public int InspectorDock = 1;
    public bool TreeGuides = true;

    public List<LibrarySourceVm> LibrarySources = [];
    public bool UseLibraryWhenImporting;
    public bool LibraryShowExtensions;
    public string LibraryNewName = "";
    public string LibraryNewPath = "";

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
    /// <summary>Opens a folder in the OS file explorer, creating it first when
    /// it does not exist yet (a seeded Brio/Anamnesis root may never have been
    /// created by its own tool).</summary>
    public Action<string>? OnOpenFolder;
    public Action<UITheme, int>? OnThemePreview;
}

/// <summary>
/// Settings: the shared <see cref="Crystarium.WindowFrame"/> is the whole
/// chassis — chrome, both bars, the rail band and its rule — and this view only
/// fills the two rectangles it hands back. The rail carries the category rows,
/// the body hosts the page through the shared scroll seam exactly as the shell
/// hosts a pane, and the rebind capture runs last as the named raw-input
/// boundary.
/// </summary>
public static class SettingsView
{
    public static float DesignWidth =>
        Crystarium.ActiveTheme.Settings.Width;

    public static float DesignHeight =>
        Crystarium.ActiveTheme.Settings.Height;

    /// <summary>The rail row's glyph slot: a 2px left margin, then a row-height
    /// square the small glyph centres in; the label starts where it ends.
    /// </summary>
    private const float NavigationIconMargin = 2f;

    private const float NavigationPillRadius = 5f;

    private static readonly (TablerIcon Icon, string Label)[] Nav =
    {
        (TablerIcon.Sliders, "General"),
        (TablerIcon.Monitor, "Display"),
        (TablerIcon.Bone, "Skeleton"),
        (TablerIcon.LayoutPanel, "UI"),
        (TablerIcon.Keyboard, "Keybinds"),
        (TablerIcon.Folder, "Library"),
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

    public static void Draw(SettingsViewModel vm, Vector2 origin)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(
            theme.Settings.Width,
            theme.Settings.Height) * scale;

        var rects = Crystarium.WindowFrame(
            "settings",
            origin,
            size,
            new WindowFrameProps
            {
                Title = "Settings",
                OnClose = () => vm.OnClose?.Invoke(),
                CloseHelp = "Close settings without saving",
                RailWidth = theme.Settings.NavigationWidth,
                FooterRight = right =>
                {
                    right.Button(
                        "Cancel",
                        () => vm.OnCancel?.Invoke(),
                        style: ControlStyle.Comfortable);
                    right.Button(
                        "Save",
                        () => vm.OnSave?.Invoke(),
                        style: ControlStyle.Comfortable,
                        variant: ButtonVariant.Primary);
                },
            });

        DrawNavigation(vm, rects.Rail);
        DrawPage(vm, rects.Body);

        if (vm.RebindingIndex >= 0)
            CaptureRebind(vm);
    }

    /// <summary>The rail's content: the frame owns the band and its rule, this
    /// owns the inset and the rows.</summary>
    private static void DrawNavigation(
        SettingsViewModel vm,
        WindowFrameRect rail)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset;
        ImGui.SetCursorScreenPos(rail.Min + new Vector2(inset * scale));
        Crystarium.ScrollRegion(
            "##settings-navigation",
            rail.Size.X / scale - inset * 2f,
            rail.Size.Y / scale - inset * 2f,
            region =>
            {
                for (int i = 0; i < Nav.Length; i++)
                    if (NavigationRow(
                            $"##settings-nav-{i}",
                            Nav[i].Label,
                            Nav[i].Icon,
                            vm.Category == i,
                            region.ContentWidth * scale))
                        vm.Category = i;
            });
    }

    /// <summary>
    /// One rail row. The settings rail is NOT a tree row: its pill runs flush
    /// to the row box and its glyph is full opacity, so the row is drawn here
    /// from primitives rather than through <c>TreeRow</c>. Only Settings has
    /// this shape, so it stays private to the view.
    /// </summary>
    private static bool NavigationRow(
        string id,
        string label,
        TablerIcon icon,
        bool selected,
        float width)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float height = theme.Controls.ListRowHeight * scale;

        // Rows stack flush at the row height: the ambient vertical spacing is
        // the surrounding flow's, not the rail's.
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        var hit = Interactive.Reserve(
            id, new Vector2(width, height), disabled: false);
        ImGui.PopStyleVar();

        var fill = selected
            ? theme.Chrome.SidebarSelected
            : hit.Hovered
                ? theme.Chrome.SidebarHover
                : Vector4.Zero;
        if (fill.W > 0f)
            ImGui.GetWindowDrawList().AddRectFilled(
                hit.ScreenMin,
                hit.ScreenMax,
                ImGui.ColorConvertFloat4ToU32(fill),
                NavigationPillRadius * scale);

        float glyph = theme.Controls.SmallIconSize * scale;
        var slotMin = new Vector2(
            hit.ScreenMin.X + NavigationIconMargin * scale, hit.ScreenMin.Y);
        var glyphMin = slotMin + new Vector2((height - glyph) * 0.5f);
        Crystarium.IconIn(glyphMin, glyphMin + new Vector2(glyph), icon);
        float labelX = slotMin.X + height;
        Crystarium.TextInBand(
            new Vector2(labelX, hit.ScreenMin.Y),
            new Vector2(hit.ScreenMax.X - labelX, height),
            label,
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Color = theme.Text,
            },
            besideIcon: true);
        return hit.Activated;
    }

    /// <summary>The body slot: one scroll seam holding the category page.
    /// </summary>
    private static void DrawPage(SettingsViewModel vm, WindowFrameRect body)
    {
        float scale = ImGuiHelpers.GlobalScale;
        float height = body.Size.Y;
        ImGui.SetCursorScreenPos(body.Min);
        Crystarium.ScrollRegion(
            "##settings-page-scroll",
            body.Size.X / scale,
            height / scale,
            region => Crystarium.Page(
                "settings-page",
                ImGui.GetCursorScreenPos(),
                new Vector2(region.ContentWidth * scale, height),
                page => DrawCategory(vm, page),
                // Settings rows carry sentence-length labels; the shared
                // 94px column truncates them.
                labelColumnWidth:
                    Crystarium.ActiveTheme.Settings.LabelColumnWidth));
    }

    private static void DrawCategory(
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
            case 5:
                DrawLibrary(vm, page);
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
            form.Switch(
                "Keep pose through redraws",
                vm.PreservePoseAcrossRedraws,
                next => vm.PreservePoseAcrossRedraws = next,
                "Restore the authored pose after an actor redraw (Penumbra collections, Glamourer, MCDF)");
            form.Switch(
                "Follow game target",
                vm.FollowGameTarget,
                next => vm.FollowGameTarget = next,
                "Targeting an actor in GPose selects it in Poser");
            form.Switch(
                "Game target follows selection",
                vm.TargetFollowsSelection,
                next => vm.TargetFollowsSelection = next,
                "Selecting an actor in Poser targets it in GPose");
        }, divider: false);
        // Auto-save lives beside the other GPose-lifecycle switches: it starts
        // and stops with GPose exactly as Open/Close with GPose do, and the
        // Library category is about reading existing pose folders, not writing
        // recovery ones.
        page.Section("AUTO-SAVE", form =>
        {
            form.Switch(
                "Auto-save poses",
                vm.AutoSaveEnabled,
                next => vm.AutoSaveEnabled = next,
                "Back up actors with pose edits to timestamped folders while in GPose");
            form.Slider(
                "Save interval",
                vm.AutoSaveIntervalSeconds,
                10f,
                600f,
                next => vm.AutoSaveIntervalSeconds = next,
                format: "0 s");
            form.TextInput(
                "Kept auto-saves",
                vm.AutoSaveMaxKept,
                next => vm.AutoSaveMaxKept = next,
                placeholder: "10",
                help: "How many snapshot folders to keep; the oldest are deleted first");
            form.Switch(
                "Clean up on GPose exit",
                vm.AutoSaveCleanOnExit,
                next => vm.AutoSaveCleanOnExit = next,
                "Delete all auto-saves when leaving GPose normally; after a crash they remain for recovery");
            form.Actions("Folder", actions => actions.Button(
                "Open in Explorer",
                () => vm.OnOpenFolder?.Invoke(vm.AutoSaveFolder),
                disabled: vm.AutoSaveFolder.Length == 0,
                help: "Show the auto-save snapshot folders in Windows Explorer"));
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
        }, divider: false);
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
        {
            form.Swatches(
                "Theme",
                ThemeSwatches,
                (int)vm.Theme,
                next =>
                {
                    vm.Theme = (UITheme)next;
                    vm.OnThemePreview?.Invoke(vm.Theme, vm.AccentIndex);
                },
                ThemeLabels);
            form.Swatches(
                "Accent",
                Crystarium.ActiveTheme.Settings.AccentOptions,
                vm.AccentIndex,
                next =>
                {
                    vm.AccentIndex = next;
                    vm.OnThemePreview?.Invoke(vm.Theme, vm.AccentIndex);
                });
        });
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
        }, divider: false);
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
        }, divider: false);
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
        }, divider: false);
    }

    private static void DrawLibrary(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("POSE LIBRARY", form =>
        {
            form.Switch(
                "Use library for Import",
                vm.UseLibraryWhenImporting,
                next => vm.UseLibraryWhenImporting = next,
                "Import… buttons open the pose library instead of the file dialog");
            form.Switch(
                "Show file extensions",
                vm.LibraryShowExtensions,
                next => vm.LibraryShowExtensions = next,
                "Tile names carry .pose / .cmp");
        }, divider: false);
        page.Section("SOURCE FOLDERS", form =>
        {
            // The remove is deferred past the loop: the action fires DURING the
            // row that owns it, and shortening the list under the iteration
            // would drop the row after it for a frame.
            int removing = -1;
            for (int i = 0; i < vm.LibrarySources.Count; i++)
            {
                int index = i;
                var source = vm.LibrarySources[index];
                form.SwitchActions(
                    string.IsNullOrWhiteSpace(source.Name)
                        ? $"Source {index + 1}"
                        : source.Name,
                    source.Enabled,
                    next => source.Enabled = next,
                    actions =>
                    {
                        actions.Button(
                            "Open",
                            () => vm.OnOpenFolder?.Invoke(source.Path),
                            disabled: string.IsNullOrWhiteSpace(source.Path),
                            help: "Show this folder in Windows Explorer");
                        actions.Button(
                            "Remove",
                            () => removing = index,
                            help: "Stop scanning this folder");
                    },
                    "Scan this folder for poses");
                form.Status(source.Path);
            }
            if (removing >= 0)
                vm.LibrarySources.RemoveAt(removing);

            form.TextInput(
                "Name",
                vm.LibraryNewName,
                next => vm.LibraryNewName = next,
                placeholder: "Taken from the folder when left blank");
            form.TextInput(
                "Folder",
                vm.LibraryNewPath,
                next => vm.LibraryNewPath = next,
                placeholder: "Full path to a folder of poses");
            form.Actions(
                string.Empty,
                actions => actions.Button(
                    "Add",
                    () => AddLibrarySource(vm),
                    disabled: string.IsNullOrWhiteSpace(vm.LibraryNewPath)));
            string pending = vm.LibraryNewPath.Trim();
            if (pending.Length > 0 && !System.IO.Directory.Exists(pending))
                form.Status(
                    "Folder does not exist yet — it is scanned once it does.");
        });
    }

    /// <summary>Commits the add-source drafts, naming the source after its
    /// last path segment when the name is left blank.</summary>
    private static void AddLibrarySource(SettingsViewModel vm)
    {
        string path = vm.LibraryNewPath.Trim();
        if (path.Length == 0)
            return;

        string name = vm.LibraryNewName.Trim();
        if (name.Length == 0)
            name = System.IO.Path.GetFileName(path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar));
        if (name.Length == 0)
            name = path;

        vm.LibrarySources.Add(new LibrarySourceVm
        {
            Name = name,
            Path = path,
        });
        vm.LibraryNewName = string.Empty;
        vm.LibraryNewPath = string.Empty;
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
        }, divider: false);
    }

    /// <summary>The raw-input boundary: while a row is rebinding, the next key
    /// press becomes its binding and Escape abandons the capture.</summary>
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
