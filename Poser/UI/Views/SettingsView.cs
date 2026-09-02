using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Poser.Config;
using Poser.Entities;
using Poser.Library;

namespace Poser.UI.Views;
public sealed class LibrarySourceVm
{
    public string Name = "";
    public string Path = "";
    public bool Enabled = true;
}
public sealed record IntegrationStatusVm(
    string Name, bool Available, string Detail);

public sealed class SettingsViewModel
{
    public int Category = 1;
    /// <summary>The rail's search: while it holds text the body shows
    /// every matching row from every page.</summary>
    public string Search = "";
    public float BoneDotRadius = 5f;
    public float MapDotRadius = 6f;
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
    // Drafted settings are previewed immediately and saved only on Save.
    public float FillOpacity = 1f;
    public bool BackdropBlur = true;

    public bool OpenOnGPose = true;
    public bool CloseWithGPose;
    public bool RelativeSecondaryBones;
    public bool LinkSiblingBones;
    public bool FollowGameTarget = true;
    public bool TargetFollowsSelection;
    public int UndoDepth = 200;

    /// <summary>Whether the frame profiler records and shows its panel.
    /// </summary>
    public bool ShowFrameProfiler;

    public bool AutoSaveEnabled = true;
    public float AutoSaveIntervalSeconds = 180f;
    public string AutoSaveMaxKept = "10";
    public bool AutoSaveCleanOnExit;
    public bool SceneSnapshotsEnabled = true;
    public string SceneSnapshotsMaxKept = "5";
    public string AutoSaveFolder = "";
    public int SkeletonShape;

    public bool SelectedBonesOnly;
    public bool PerBoneSymmetry;
    public bool AutoLinkPairedBones;
    public int BonePickBehavior;

    public bool ShowSkeletonLines = true;
    public float BoneLineThickness = 1.0f;
    public float BoneLineOpacity = 0.23f;
    public float BoneLineOpacityWhileUsing = 0.15f;
    public bool SkeletonLineToCircle;
    public bool HideSkeletonWhileDragging;
    public bool HideSkeletonOnActorSelection = true;
    public bool OnlyActiveActorBones;

    public bool DimInactiveActors;
    public float InactiveActorOpacity = 0.5f;
    public int ActiveActorSource;

    public bool ShowFriendlyBoneNames = true;
    public bool ShowAllVieraEars;

    public float GizmoScale = 1.0f;
    public bool AllowHoldSnap;
    public int GroupScale;
    public float SnapRotationDegrees = 5.0f;
    public float SnapLinearStep = 0.1f;
    public bool AllowRaySnap;
    public bool KeepGizmoWhenBonesHidden = true;
    public bool HideGizmoWithoutArmature;
    public float TransformEntitySpeed = 0.005f;
    public float TransformBoneSpeed = 0.005f;
    public float CameraDefaultSpeed = FreeCameraSpeed.Default;
    public float CameraDefaultSensitivity = 0.1f;
    public float CameraFastMultiplier = 3f;
    public float CameraSlowMultiplier = 0.3f;
    public bool CameraConsumeModifiers = true;
    public bool CameraConsumeAllInput;
    public bool CameraFlipPastNinety;
    public bool CameraLookThroughSelected;
    public int DefaultSpawnPlacement;

    public bool DetachedShell;
    public bool DetachedWindowsRemember;
    public bool TreeGuides = true;
    public bool SwapRotationXY;
    public bool ShowInGPose = true;
    public bool ShowInCutscene = true;
    public bool HideWhileManipulating;
    public bool HideWhileMovingCamera;
    public bool HideGizmoWhileManipulating;
    public bool ShowWhenGameUiHidden;
    public List<LibrarySourceVm> LibrarySources = [];
    public string PoseFolder = "";
    /// <summary>The one Poser folder the homes and auto-saves live in.</summary>
    public string PoserRoot = "";
    public string ObjectsFolder = "";
    public string SceneFolder = "";
    public string McdfFolder = "";
    public string AutoSaveFolderDraft = "";

    public bool UseLibraryWhenImporting;
    public bool LibraryShowExtensions;
    public string LibraryNewName = "";
    public string LibraryNewPath = "";
    public Dictionary<string, KeybindSlots> Bindings =
        KeybindRegistry.Bindings(KeybindPreset.Poser);
    public string? RebindingAction;
    public int RebindingSlot;

    /// <summary>The GAME's key state — the same source the runtime
    /// matcher fires from. ImGui key events do not reach an unfocused
    /// widget, which is what killed the old ImGui-based capture.</summary>
    public Func<Dalamud.Game.ClientState.Keys.VirtualKey, bool> KeyDown =
        static _ => false;

    /// <summary>Keys already down when the capture armed (or held since):
    /// a chord is the FIRST key that goes down after arming, never one
    /// still travelling from before.</summary>
    public readonly HashSet<Dalamud.Game.ClientState.Keys.VirtualKey>
        RebindHeld = new();

    /// <summary>Diagnostics for the capture: a per-frame probe of both key
    /// sources shown live in the page, and a throttled log line — added
    /// after three blind fixes; the probe reports, nobody theorizes.</summary>
    public Action<string>? DebugLog;
    public string RebindProbe = string.Empty;
    /// <summary>A refused capture's standing answer — a chord already
    /// bound elsewhere is never applied; the message stands until a new
    /// chord lands or the capture disarms.</summary>
    public string RebindRefusal = string.Empty;

    public int PresetIndex;
    public bool PresetArmed;
    public string PresetStatus = "";
    public int BindingRevision;
    private int _conflictRevision = -1;
    private Dictionary<KeybindRegistry.SlotRef, IReadOnlyList<string>>
        _conflicts = new();

    public IReadOnlyDictionary<KeybindRegistry.SlotRef, IReadOnlyList<string>>
        Conflicts
    {
        get
        {
            if (_conflictRevision == BindingRevision)
                return _conflicts;
            _conflicts = KeybindRegistry.Conflicts(Bindings);
            _conflictRevision = BindingRevision;
            return _conflicts;
        }
    }

    public string Version = "dev";
    public List<IntegrationStatusVm> Integrations = [];
    public string ConfigLoadFailure = "";
    public ConfigResetScope? ResetArmed;
    public string ResetStatus = "";

    public Action? OnSave;
    public Action? OnCancel;
    public Action? OnClose;
    public Action? OnOpenRepository;
    public Action<string>? OnOpenUrl;
    public Action<string>? OnOpenFolder;
    /// <summary>Opens a folder picker seeded at the first argument and
    /// hands the chosen path to the second.</summary>
    public Action<string, Action<string>>? OnBrowseFolder;

    /// <summary>A hash over every value the pages edit: the window
    /// applies the view model whenever it changes.</summary>
    public int Signature()
    {
        var hash = new HashCode();
        foreach (var field in typeof(SettingsViewModel).GetFields())
        {
            if (field.FieldType.IsPrimitive || field.FieldType == typeof(string)
                || field.FieldType == typeof(Vector4) || field.FieldType.IsEnum)
                hash.Add(field.GetValue(this));
        }
        foreach (var source in LibrarySources)
        {
            hash.Add(source.Name);
            hash.Add(source.Path);
            hash.Add(source.Enabled);
        }
        return hash.ToHashCode();
    }
    public Action<UITheme, int>? OnThemePreview;
    public Action<float, bool>? OnSurfaceEffectsPreview;
    public Action<ConfigResetScope>? OnResetConfig;
    public Action? OnRefreshIntegrations;
}
public enum ConfigResetScope
{
    All,
    Display,
    Skeleton,
    UI,
}
public static class SettingsView
{
    public static float DesignWidth =>
        Crystarium.ActiveTheme.Settings.Width;

    public static float DesignHeight =>
        Crystarium.ActiveTheme.Settings.Height;
    private const float NavigationPillRadius = 5f;

    /// <summary>Positional against <c>ObjectPlacementMode</c>.</summary>
    private static readonly string[] SpawnPlacementLabels =
        ["Where they were saved", "Relative to the saved camera",
         "Relative to the saved actor", "In front of the camera"];

    private static readonly (TablerIcon Icon, string Label)[] Nav =
    {
        (TablerIcon.Sliders, "General"),
        (TablerIcon.Monitor, "Display"),
        (TablerIcon.Bone, "Skeleton"),
        (TablerIcon.ArrowsMove, "Gizmo"),
        (TablerIcon.Video, "Camera"),
        (TablerIcon.LayoutPanel, "UI"),
        (TablerIcon.Keyboard, "Keybinds"),
        (TablerIcon.Folder, "Library"),
        (TablerIcon.Info, "About"),
    };

    private static readonly float[] UndoDepthMarks = [0f, 200f];

    public static int PageCount => Nav.Length;

    /// <summary>The search settles a moment after the last keystroke and
    /// the results for the settled text crossfade in — the way a settings
    /// search behaves everywhere else (VS Code, Chrome, macOS): nothing
    /// moves, the old set is replaced by the new one in place.</summary>
    private const double SearchSettleSeconds = 0.12;
    private static string _lastSearch = string.Empty;
    private static double _searchChangedAt;
    private static string _settledSearch = string.Empty;
    private static double _settledAt;

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

        if (vm.RebindingAction != null)
            CaptureRebind(vm);
    }
    private static void DrawNavigation(
        SettingsViewModel vm,
        WindowFrameRect rail)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        float inset = theme.Page.Inset;
        // The search sits above the pages and reaches across every one of
        // them: while it holds text, the body shows what matches.
        float searchHeight = theme.Controls.SearchHeight;
        ImGui.SetCursorScreenPos(rail.Min + new Vector2(inset, inset) * scale);
        Crystarium.FilterPill(
            "##settings-search",
            vm.Search,
            next => vm.Search = next,
            "Search",
            ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(rail.Size.X / scale - inset * 2f),
            });
        float top = rail.Min.Y + (inset + searchHeight + theme.Page.ActionGap) * scale;
        float rowHeight = theme.Settings.NavigationRowHeight * scale;
        // The page glyphs stand in the search's own icon column.
        float glyphInset = (inset + theme.Controls.InputPaddingX) * scale;
        for (int i = 0; i < Nav.Length; i++)
        {
            ImGui.SetCursorScreenPos(new Vector2(rail.Min.X + inset * scale, top + rowHeight * i));
            if (NavigationRow(
                    $"##settings-nav-{i}",
                    Nav[i].Label,
                    Nav[i].Icon,
                    vm.Category == i && vm.Search.Length == 0,
                    rail.Size.X - inset * 2f * scale,
                    rowHeight,
                    glyphInset - inset * scale))
            {
                vm.Category = i;
                vm.Search = string.Empty;
            }
        }
    }

    /// <summary>One page row: a rounded pill with the page inset on both
    /// sides; the glyph sits in the search's icon column.</summary>
    private static bool NavigationRow(
        string id,
        string label,
        TablerIcon icon,
        bool selected,
        float width,
        float height,
        float inset)
    {
        var theme = Crystarium.ActiveTheme;
        float scale = ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing;
        ImGui.PushStyleVar(
            ImGuiStyleVar.ItemSpacing, new Vector2(spacing.X, 0f));
        var hit = Interactive.Reserve(
            id, new Vector2(width, height), disabled: false);
        ImGui.PopStyleVar();

        // Selected is the strong fill; a hover is a hint at half that,
        // so the two never read as two selections.
        var fill = selected
            ? theme.Chrome.SidebarSelected
            : hit.Hovered
                ? theme.Chrome.SidebarHover with { W = theme.Chrome.SidebarHover.W * 0.5f }
                : Vector4.Zero;
        if (fill.W > 0f)
            ImGui.GetWindowDrawList().AddRectFilled(
                hit.ScreenMin,
                hit.ScreenMax,
                ImGui.ColorConvertFloat4ToU32(fill),
                NavigationPillRadius * scale);

        float glyph = theme.Controls.SmallIconSize * scale;
        var slotMin = new Vector2(hit.ScreenMin.X + inset, hit.ScreenMin.Y);
        var glyphMin = slotMin + new Vector2(0f, (height - glyph) * 0.5f);
        Crystarium.IconIn(glyphMin, glyphMin + new Vector2(glyph), icon);
        float labelX = slotMin.X + glyph + theme.Controls.SearchIconGap * scale;
        Crystarium.TextInBand(
            new Vector2(labelX, hit.ScreenMin.Y),
            new Vector2(hit.ScreenMax.X - labelX - inset, height),
            label,
            new TextStyle
            {
                Size = theme.Typography.BodySize,
                Color = theme.Text,
            },
            besideIcon: true);
        return hit.Activated;
    }

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
                page =>
                {
                    if (vm.Search.Trim().Length == 0)
                        DrawCategory(vm, page);
                    else
                        DrawSearch(vm, page);
                },
                labelColumnWidth:
                    Crystarium.ActiveTheme.Settings.LabelColumnWidth));
    }

    /// <summary>Every page is probed for what it would draw; a section
    /// whose title matches shows whole, otherwise the rows whose label or
    /// hover matches. Section titles carry their page's name, and the
    /// results fade in from the moment the search changed.</summary>
    private static readonly string[] DetachedPlacementOptions =
        ["Beside the properties window", "Where they were last"];

    private static void DrawSearch(SettingsViewModel vm, Crystarium.PageScope page)
    {
        double now = ImGui.GetTime();
        string typed = vm.Search.Trim();
        if (!string.Equals(typed, _lastSearch, StringComparison.Ordinal))
        {
            _lastSearch = typed;
            _searchChangedAt = now;
        }
        if (!string.Equals(_settledSearch, _lastSearch, StringComparison.Ordinal)
            && now - _searchChangedAt >= SearchSettleSeconds)
        {
            _settledSearch = _lastSearch;
            _settledAt = now;
        }
        string needle = _settledSearch;
        if (needle.Length == 0)
        {
            // Nothing has settled yet: the page stays until it does.
            DrawCategory(vm, page);
            return;
        }
        float fade = Crystarium.ActiveTheme.Motion.Fast;
        float ease = fade <= 0f
            ? 1f
            : Math.Clamp((float)(now - _settledAt) / fade, 0f, 1f);
        int mark = Crystarium.VertexMark();
        bool Hit(string? text) =>
            text != null && text.Contains(needle, StringComparison.OrdinalIgnoreCase);
        int any = 0;
        for (int category = 0; category < Nav.Length; category++)
        {
            var sections = new HashSet<string>(StringComparer.Ordinal);
            var rows = new HashSet<(string Section, string Label)>();
            bool wholePage = Hit(Nav[category].Label);
            page.Probe = (section, label, help) =>
            {
                if (wholePage || Hit(section))
                    sections.Add(section);
                if (label.Length > 0 && (Hit(label) || Hit(help)))
                {
                    sections.Add(section);
                    rows.Add((section, label));
                }
            };
            int saved = vm.Category;
            vm.Category = category;
            DrawCategory(vm, page);
            page.Probe = null;
            if (sections.Count == 0)
            {
                vm.Category = saved;
                continue;
            }
            page.SectionPrefix = Nav[category].Label + " · ";
            page.SectionFilter = section => sections.Contains(section);
            page.RowFilter = (section, label, help) =>
                wholePage || Hit(section) || rows.Contains((section, label));
            DrawCategory(vm, page);
            page.SectionPrefix = null;
            page.SectionFilter = null;
            page.RowFilter = null;
            vm.Category = saved;
            any++;
        }
        if (any == 0)
            page.EmptyState($"Nothing matches \"{needle}\".");
        Crystarium.FadeSince(mark, ease);
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
                DrawGizmo(vm, page);
                break;
            case 4:
                DrawCamera(vm, page);
                break;
            case 5:
                DrawUi(vm, page);
                break;
            case 6:
                DrawKeybinds(vm, page);
                break;
            case 7:
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
        page.Section("Behavior", form =>
        {
            form.Switch(
                "Open with GPose",
                vm.OpenOnGPose,
                next => vm.OpenOnGPose = next,
                "Poser's windows open by themselves when you enter GPose");
            form.Switch(
                "Close with GPose",
                vm.CloseWithGPose,
                next => vm.CloseWithGPose = next,
                "Poser's windows close by themselves when you leave GPose");
            form.Switch(
                "Selection follows the game target",
                vm.FollowGameTarget,
                next => vm.FollowGameTarget = next,
                "Targeting a character in GPose selects it in Poser's sidebar");
            form.Switch(
                "Game target follows selection",
                vm.TargetFollowsSelection,
                next => vm.TargetFollowsSelection = next,
                "Selecting an actor in the sidebar makes it GPose's target");
            form.Slider(
                "Undo steps",
                vm.UndoDepth,
                0f,
                500f,
                next => vm.UndoDepth = (int)MathF.Round(next),
                format: "0",
                marks: UndoDepthMarks,
                help: "How many edits you can undo; 0 turns undo off");
        }, divider: false);
        page.Section("Spawning", form =>
            form.Dropdown(
                "Library entries land",
                SpawnPlacementLabels,
                vm.DefaultSpawnPlacement,
                next => vm.DefaultSpawnPlacement = next,
                help: "Where an actor, object or scene from the library appears when you load it"));
        page.Section("Auto-save", form =>
        {
            bool saving = vm.AutoSaveEnabled;
            bool scenes = saving && vm.SceneSnapshotsEnabled;
            form.Switch(
                "Auto-save poses",
                vm.AutoSaveEnabled,
                next => vm.AutoSaveEnabled = next,
                "While you are in GPose, every actor you have posed is saved to a backup folder on a timer");
            form.Slider(
                "Every",
                vm.AutoSaveIntervalSeconds,
                10f,
                600f,
                next => vm.AutoSaveIntervalSeconds = next,
                format: "0 s",
                help: "Seconds between backups",
                disabled: !saving);
            form.Number(
                "Backups kept",
                ParseCount(vm.AutoSaveMaxKept, 10),
                next => vm.AutoSaveMaxKept = CountText(next),
                perPixel: 0.1f,
                format: "0",
                help: "Older backups are deleted once there are more than this",
                disabled: !saving);
            form.Switch(
                "Auto-save the scene too",
                vm.SceneSnapshotsEnabled,
                next => vm.SceneSnapshotsEnabled = next,
                "The whole scene, everything in the sidebar, is saved on the same timer into its own folder",
                disabled: !saving);
            form.Number(
                "Scene backups kept",
                ParseCount(vm.SceneSnapshotsMaxKept, 5),
                next => vm.SceneSnapshotsMaxKept = CountText(next),
                perPixel: 0.1f,
                format: "0",
                help: "Older scene backups are deleted once there are more than this",
                disabled: !scenes);
            form.Switch(
                "Delete backups on exit",
                vm.AutoSaveCleanOnExit,
                next => vm.AutoSaveCleanOnExit = next,
                "Leaving GPose normally clears the backups; after a crash they stay so you can recover",
                disabled: !saving);
        });
        // The one diagnostic surface a photographer is ever pointed at, on the
        // page they already open. It is a switch rather than a hidden command
        // because a feature reachable only from a console is not shipped.
        page.Section("Diagnostics", form =>
        {
            form.Switch(
                "Show frame profiler",
                vm.ShowFrameProfiler,
                next => vm.ShowFrameProfiler = next,
                "A window listing what each part of Poser costs per frame, slowest first");
        });
        page.Section("Reset", form =>
        {
            if (vm.ConfigLoadFailure.Length > 0)
                form.Status(vm.ConfigLoadFailure, warning: true);
            ResetRow(
                vm,
                form,
                ConfigResetScope.All,
                "Everything",
                "Put every Poser setting back to its shipped default");
        });
    }

    private static float ParseCount(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
        && value >= 1
            ? value
            : fallback;

    private static string CountText(float value) =>
        Math.Max(1, (int)MathF.Round(value)).ToString(CultureInfo.InvariantCulture);

    private static void ResetRow(
        SettingsViewModel vm,
        Crystarium.FormScope form,
        ConfigResetScope scope,
        string label,
        string help)
    {
        bool armed = vm.ResetArmed == scope;
        form.Actions(label, actions => actions.Button(
            armed ? "Confirm reset" : "Reset",
            () =>
            {
                if (!armed)
                {
                    vm.ResetArmed = scope;
                    vm.ResetStatus =
                        $"{label} goes back to defaults, discarding anything "
                        + "unsaved on this page. Press Confirm reset to apply.";
                    return;
                }
                vm.ResetArmed = null;
                vm.OnResetConfig?.Invoke(scope);
            },
            variant: armed ? ButtonVariant.Danger : ButtonVariant.Secondary,
            help: help));
        if (vm.ResetStatus.Length > 0)
            form.Status(vm.ResetStatus, warning: armed);
    }

    private static void DrawDisplay(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("Theme", form =>
        {
            form.ThemeSwatches(
                "Theme",
                ThemeSelection.VisibleChoices,
                ThemeSelection.VisibleIndex(vm.Theme),
                next =>
                {
                    vm.Theme = next;
                    vm.OnThemePreview?.Invoke(vm.Theme, vm.AccentIndex);
                });
            form.Swatches(
                "Accent",
                Theme.AccentOptions,
                vm.AccentIndex,
                next =>
                {
                    vm.AccentIndex = next;
                    vm.OnThemePreview?.Invoke(vm.Theme, vm.AccentIndex);
                });
            form.Slider(
                "Window opacity",
                vm.FillOpacity,
                UIConfiguration.MinimumFillOpacity,
                1f,
                next =>
                {
                    vm.FillOpacity = UIConfiguration.ClampFillOpacity(next);
                    vm.OnSurfaceEffectsPreview?.Invoke(
                        vm.FillOpacity, vm.BackdropBlur);
                },
                format: "0 %",
                help: "How see-through Poser's windows are");
            form.Switch(
                "Blur behind windows",
                vm.BackdropBlur,
                next =>
                {
                    vm.BackdropBlur = next;
                    vm.OnSurfaceEffectsPreview?.Invoke(
                        vm.FillOpacity, vm.BackdropBlur);
                },
                "The game behind a window is blurred instead of showing through sharp");
        }, divider: false);
        page.Section("Privacy", form =>
            form.Switch(
                "Anonymous mode",
                vm.AnonymousMode,
                next => vm.AnonymousMode = next,
                "Character names are replaced everywhere in Poser, for streaming and screenshots"));
        page.Section("Reset", form => ResetRow(
            vm,
            form,
            ConfigResetScope.Display,
            "Display settings",
            "Put the theme, opacity and privacy settings back to their defaults"));
    }

    private static void DrawSkeleton(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("Bones", form =>
        {
            form.Dropdown(
                "Draw bones as",
                SkeletonShapeLabels,
                vm.SkeletonShape,
                next => vm.SkeletonShape = next,
                "Dots, solids that point at the child bone, or joints");
            form.PairRows();
            form.Slider(
                "Dot size",
                vm.BoneDotRadius,
                2f,
                12f,
                next => vm.BoneDotRadius = next,
                format: "0 px",
                help: "The size of a bone dot on screen");
            form.Slider(
                "Map dot size",
                vm.MapDotRadius,
                3f,
                12f,
                next => vm.MapDotRadius = next,
                format: "0 px",
                help: "The size of a dot on the body and face maps");
            form.EndPair();
            form.Switch(
                "Only selected bones",
                vm.SelectedBonesOnly,
                next => vm.SelectedBonesOnly = next,
                "Only the bones you have selected are drawn; everything else waits for a hover");
            form.Switch(
                "Only the active actor's bones",
                vm.OnlyActiveActorBones,
                next => vm.OnlyActiveActorBones = next,
                "Bones draw for the actor you are working on and no other; with several actors selected none draw");
            form.Switch(
                "NSFW bones",
                vm.NsfwBones,
                next => vm.NsfwBones = next,
                "IVCS and other adult bone sets appear in the tree and the overlay");
            form.Switch(
                "All Viera ear sets",
                vm.ShowAllVieraEars,
                next => vm.ShowAllVieraEars = next,
                "Every Viera ear set is listed, not only the one the character wears");
        }, divider: false);
        page.Section("Colors", form =>
            form.ColorWells("Bones", wells =>
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
            }));
        page.Section("Lines", form =>
        {
            bool lines = vm.ShowSkeletonLines;
            form.Switch(
                "Show lines",
                vm.ShowSkeletonLines,
                next => vm.ShowSkeletonLines = next,
                "A line joins each bone to its parent");
            form.PairRows();
            form.Slider(
                "Thickness",
                vm.BoneLineThickness,
                0.5f,
                4f,
                next => vm.BoneLineThickness = next,
                format: "0.0 px",
                disabled: !lines);
            form.Slider(
                "Opacity",
                vm.BoneLineOpacity,
                0f,
                1f,
                next => vm.BoneLineOpacity = next,
                format: "0%",
                disabled: !lines);
            form.EndPair();
            form.Switch(
                "Stop at the dot",
                vm.SkeletonLineToCircle,
                next => vm.SkeletonLineToCircle = next,
                "Lines end at the edge of a dot instead of running through it",
                disabled: !lines);
        });
        page.Section("Selecting", form =>
        {
            form.Switch(
                "Selecting an actor keeps bones hidden",
                vm.HideSkeletonOnActorSelection,
                next => vm.HideSkeletonOnActorSelection = next,
                "Clicking an actor does not draw its whole skeleton; bones appear once you select one");
            form.Dropdown(
                "Wheel over stacked bones",
                BonePickBehaviorLabels,
                vm.BonePickBehavior,
                next => vm.BonePickBehavior = next,
                "Ktisis: the wheel moves the highlight and a click picks it. Brio: the wheel selects each bone as it reaches it");
            form.Switch(
                "Remember Link and Mirror per bone",
                vm.PerBoneSymmetry,
                next => vm.PerBoneSymmetry = next,
                "The toolbar's Link and Mirror modes are kept for each bone separately");
            form.Switch(
                "Eyes and ears move together",
                vm.AutoLinkPairedBones,
                next => vm.AutoLinkPairedBones = next,
                "Moving one eye or ear bone moves its pair");
            form.Switch(
                "Select left and right together",
                vm.LinkSiblingBones,
                next => vm.LinkSiblingBones = next,
                "Selecting a bone also selects its opposite side");
            form.Switch(
                "Keep relative angles",
                vm.RelativeSecondaryBones,
                next => vm.RelativeSecondaryBones = next,
                "With several bones selected, the others turn around the first one instead of each around itself");
        });
        page.Section("Inactive actors", form =>
        {
            bool dim = vm.DimInactiveActors;
            form.Switch(
                "Fade inactive actors",
                vm.DimInactiveActors,
                next => vm.DimInactiveActors = next,
                "Bones of every actor but the active one are drawn faded");
            form.Slider(
                "Faded opacity",
                vm.InactiveActorOpacity,
                0f,
                1f,
                next => vm.InactiveActorOpacity = next,
                format: "0%",
                disabled: !dim);
            form.Dropdown(
                "The active actor is",
                ActiveActorLabels,
                vm.ActiveActorSource,
                next => vm.ActiveActorSource = next,
                "The one GPose targets, the one selected in Poser, or either",
                disabled: !dim);
        });
        page.Section("Names", form =>
            form.Switch(
                "Friendly bone names",
                vm.ShowFriendlyBoneNames,
                next => vm.ShowFriendlyBoneNames = next,
                "\"Jaw\" instead of the game's \"j_f_ago\""));
        page.Section("Reset", form => ResetRow(
            vm,
            form,
            ConfigResetScope.Skeleton,
            "Skeleton settings",
            "Put the bone, line and color settings back to their defaults"));
    }

    private static readonly string[] GroupScaleLabels =
        ["Sizes and spacing", "Spacing only"];
    private static readonly string[] SkeletonShapeLabels =
        ["Dots", "Octahedra", "Joints"];
    private static readonly string[] BonePickBehaviorLabels =
        ["Ktisis", "Brio"];
    private static readonly string[] ActiveActorLabels =
        ["GPose target", "Selection", "Either"];

    private static void DrawGizmo(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("Size", form =>
            form.Slider(
                "Gizmo size",
                vm.GizmoScale,
                0.5f,
                2f,
                next => vm.GizmoScale = next,
                format: "0.00×",
                help: "How large the handles are on screen; they stay this size at any distance"),
            divider: false);
        page.Section("Drag speed", form =>
        {
            form.PairRows();
            form.Slider(
                "Actors and objects",
                vm.TransformEntitySpeed,
                0.0005f,
                0.05f,
                next => vm.TransformEntitySpeed = next,
                format: "0.0000",
                help: "How far one pixel of drag moves an actor, object, light or camera");
            form.Slider(
                "Bones",
                vm.TransformBoneSpeed,
                0.0005f,
                0.05f,
                next => vm.TransformBoneSpeed = next,
                format: "0.0000",
                help: "How far one pixel of drag moves a bone");
            form.EndPair();
        });
        page.Section("Snapping", form =>
        {
            bool snap = vm.AllowHoldSnap;
            form.Switch(
                "Hold Z to snap",
                vm.AllowHoldSnap,
                next => vm.AllowHoldSnap = next,
                "While Z is held a drag moves in steps; add Shift for a tenth of the step");
            form.PairRows();
            form.Slider(
                "Rotation step",
                vm.SnapRotationDegrees,
                0.5f,
                45f,
                next => vm.SnapRotationDegrees = next,
                format: "0.0°",
                disabled: !snap);
            form.Slider(
                "Move and scale step",
                vm.SnapLinearStep,
                0.01f,
                1f,
                next => vm.SnapLinearStep = next,
                format: "0.00",
                disabled: !snap);
            form.EndPair();
            form.Switch(
                "Hold X to snap to surfaces",
                vm.AllowRaySnap,
                next => vm.AllowRaySnap = next,
                "While X is held, what you move lands wherever the pointer touches the scene");
        });
        page.Section("Groups", form =>
            form.Dropdown(
                "Scaling a group",
                GroupScaleLabels,
                vm.GroupScale,
                next => vm.GroupScale = next,
                help: "Grow the members and the space between them, or only the space between them"));
        // Everything that happens for the length of a drag, world gizmo or
        // inspector ball alike, lives here and nowhere else.
        page.Section("While dragging", form =>
        {
            bool hideWindows = vm.HideWhileManipulating;
            form.Switch(
                "Hide the windows",
                vm.HideWhileManipulating,
                next => vm.HideWhileManipulating = next,
                "Every Poser window fades out while you drag a handle, so you see the pose");
            form.Switch(
                "Hide while the camera moves",
                vm.HideWhileMovingCamera,
                next => vm.HideWhileMovingCamera = next,
                "The windows fade out while you fly or drag the camera");
            form.Switch(
                "Hide the gizmo too",
                vm.HideGizmoWhileManipulating,
                next => vm.HideGizmoWhileManipulating = next,
                "The gizmo fades with the windows; the drag and its readout stay",
                disabled: !hideWindows);
            form.Switch(
                "Hide the bones",
                vm.HideSkeletonWhileDragging,
                next => vm.HideSkeletonWhileDragging = next,
                "Dots and lines disappear while you drag");
            form.Slider(
                "Line opacity",
                vm.BoneLineOpacityWhileUsing,
                0f,
                1f,
                next => vm.BoneLineOpacityWhileUsing = next,
                format: "0%",
                help: "How visible the bone lines stay while you drag, when they are not hidden",
                disabled: !vm.ShowSkeletonLines || vm.HideSkeletonWhileDragging);
        });
        page.Section("Visibility", form =>
        {
            bool keep = vm.KeepGizmoWhenBonesHidden;
            form.Switch(
                "Keep the gizmo without bones",
                vm.KeepGizmoWhenBonesHidden,
                next => vm.KeepGizmoWhenBonesHidden = next,
                "A selected bone keeps its gizmo even when its bones are hidden from the overlay");
            form.Switch(
                "Hide the gizmo with the skeleton",
                vm.HideGizmoWithoutArmature,
                next => vm.HideGizmoWithoutArmature = next,
                "Turning the bone overlay off takes the gizmo with it",
                disabled: keep);
        });
    }

    private static void DrawCamera(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("New free cameras", form =>
        {
            form.PairRows();
            form.Slider(
                "Fly speed",
                vm.CameraDefaultSpeed,
                FreeCameraSpeed.Minimum,
                FreeCameraSpeed.Maximum,
                next => vm.CameraDefaultSpeed = next,
                format: "0.000",
                help: "The speed a new free camera flies at");
            form.Slider(
                "Mouse sensitivity",
                vm.CameraDefaultSensitivity,
                0.001f,
                0.2f,
                next => vm.CameraDefaultSensitivity = next,
                format: "0.000",
                help: "How far a right-drag turns a new free camera");
            form.EndPair();
        }, divider: false);
        page.Section("Selection", form =>
            form.Switch(
                "Look through a selected camera",
                vm.CameraLookThroughSelected,
                next => vm.CameraLookThroughSelected = next,
                "Selecting a camera in the sidebar switches the view to it"));
        page.Section("Speed keys", form =>
        {
            form.PairRows();
            form.Slider(
                "Shift",
                vm.CameraFastMultiplier,
                1f,
                10f,
                next => vm.CameraFastMultiplier = next,
                format: "0.0×",
                help: "Holding Shift multiplies the fly speed by this");
            form.Slider(
                "Ctrl",
                vm.CameraSlowMultiplier,
                0.05f,
                1f,
                next => vm.CameraSlowMultiplier = next,
                format: "0.00×",
                help: "Holding Ctrl multiplies the fly speed by this");
            form.EndPair();
        });
        page.Section("Game input", form =>
        {
            form.Switch(
                "Keep flight keys from the game",
                vm.CameraConsumeModifiers,
                next => vm.CameraConsumeModifiers = next,
                "While a free camera flies, the game does not see Space, C, Shift or Ctrl");
            form.Switch(
                "Keep every key from the game",
                vm.CameraConsumeAllInput,
                next => vm.CameraConsumeAllInput = next,
                "While in GPose the game sees no keys at all except Escape and Enter");
            form.Switch(
                "Flip fly keys past 90°",
                vm.CameraFlipPastNinety,
                next => vm.CameraFlipPastNinety = next,
                "Once the camera is rolled more than a quarter turn, sideways and up keys swap so they still move you the way the screen shows");
        });
    }

    private static void DrawUi(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("Layout", form =>
        {
            form.Switch(
                "Detached windows",
                vm.DetachedShell,
                next => vm.DetachedShell = next,
                "The toolbar and the sidebar float as separate windows you can place anywhere");
            form.Dropdown(
                "Detached windows open",
                DetachedPlacementOptions,
                vm.DetachedWindowsRemember ? 1 : 0,
                next => vm.DetachedWindowsRemember = next == 1,
                help: "Where the sidebar and the inspector appear when detached");
            form.Switch(
                "Tree guide lines",
                vm.TreeGuides,
                next => vm.TreeGuides = next,
                "Lines in the sidebar show what belongs under what");
            form.Switch(
                "Swap rotation X and Y",
                vm.SwapRotationXY,
                next => vm.SwapRotationXY = next,
                "The rotation row shows its first two columns the other way round; the pose itself is unchanged");
        }, divider: false);
        page.Section("Visibility", form =>
        {
            form.Switch(
                "Show while the game UI is hidden",
                vm.ShowInGPose,
                next => vm.ShowInGPose = next,
                "Poser stays on screen when GPose hides the game's own interface");
            form.Switch(
                "Show in cutscenes",
                vm.ShowInCutscene,
                next => vm.ShowInCutscene = next,
                "Poser stays on screen during cutscenes");
            form.Switch(
                "Show after you hide the HUD",
                vm.ShowWhenGameUiHidden,
                next => vm.ShowWhenGameUiHidden = next,
                "Poser stays on screen after Scroll Lock hides the HUD, or the game hides it for you");
        });
        page.Section("Reset", form => ResetRow(
            vm,
            form,
            ConfigResetScope.UI,
            "UI settings",
            "Put the layout, visibility and keybind settings back to their defaults"));
    }

    private static readonly string[] PresetLabels =
        ["Poser", "Brio", "Ktisis"];
    private const float KeybindSlotWidth = 132f;
    private const string UnboundCaption = "Unbound";

    private static void DrawKeybinds(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("Preset", form =>
        {
            form.Segmented(
                "Chords from",
                PresetLabels,
                vm.PresetIndex,
                next =>
                {
                    vm.PresetIndex = next;
                    vm.PresetArmed = false;
                    vm.PresetStatus = string.Empty;
                },
                help: "Which tool's keyboard layout to take");
            form.Actions(
                string.Empty,
                actions => actions.Button(
                    vm.PresetArmed ? "Confirm preset" : "Apply preset",
                    () => ApplyPreset(vm),
                    variant: vm.PresetArmed
                        ? ButtonVariant.Primary
                        : ButtonVariant.Secondary));
            form.Status(
                vm.RebindingAction != null
                    ? vm.RebindProbe
                    : vm.PresetStatus.Length > 0
                        ? vm.PresetStatus
                        : "Click a slot below to rebind it. Escape cancels, "
                            + "Backspace clears it.",
                warning: vm.PresetArmed);
        }, divider: false);

        foreach (var (group, start, count) in KeybindGroups)
            page.Section(group, form =>
            {
                for (int i = start; i < start + count; i++)
                    DrawKeybindRow(vm, form, KeybindRegistry.Actions[i]);
                form.Actions(
                    string.Empty,
                    actions => actions.Button(
                        "Reset group",
                        () => ResetKeybindGroup(vm, group, start, count),
                        help: "Put this group's chords back to Poser's defaults"),
                    alignRight: true);
            });
    }
    private static void ResetKeybindGroup(
        SettingsViewModel vm, string group, int start, int count)
    {
        for (int i = start; i < start + count; i++)
            vm.Bindings[KeybindRegistry.Actions[i].Id] =
                KeybindRegistry.Default(KeybindRegistry.Actions[i].Id);
        vm.RebindingAction = null;
        vm.PresetArmed = false;
        vm.BindingRevision++;
        vm.PresetStatus =
            $"{group} chords are back to Poser's defaults. Save to keep them.";
    }
    private static readonly (string Group, int Start, int Count)[]
        KeybindGroups = BuildKeybindGroups();

    private static (string, int, int)[] BuildKeybindGroups()
    {
        var groups = new List<(string Group, int Start, int Count)>();
        var actions = KeybindRegistry.Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            if (groups.Count > 0
                && string.Equals(
                    groups[^1].Group, actions[i].Group, StringComparison.Ordinal))
            {
                groups[^1] = groups[^1] with { Count = groups[^1].Count + 1 };
                continue;
            }
            groups.Add((actions[i].Group, i, 1));
        }
        return groups.ConvertAll(
            entry => (entry.Group, entry.Start, entry.Count)).ToArray();
    }

    private static void DrawKeybindRow(
        SettingsViewModel vm,
        Crystarium.FormScope form,
        KeybindAction action)
    {
        var slots = vm.Bindings[action.Id];
        form.Actions(
            action.Id,
            actions =>
            {
                DrawKeybindSlot(vm, actions, action, slots, 0);
                DrawKeybindSlot(vm, actions, action, slots, 1);
            },
            help: action.Help);
        var conflicts = vm.Conflicts;
        var others = conflicts.TryGetValue(
                new KeybindRegistry.SlotRef(action.Id, 0), out var primary)
            ? primary
            : conflicts.TryGetValue(
                new KeybindRegistry.SlotRef(action.Id, 1), out var secondary)
                ? secondary
                : null;
        if (others is { Count: > 0 })
            form.Status(
                "Also bound to " + string.Join(", ", others) + ".",
                warning: true);
    }

    private static void DrawKeybindSlot(
        SettingsViewModel vm,
        Crystarium.ActionScope actions,
        KeybindAction action,
        KeybindSlots slots,
        int slot)
    {
        bool capturing = vm.RebindingSlot == slot
            && string.Equals(vm.RebindingAction, action.Id, StringComparison.Ordinal);
        string chord = slots[slot];
        actions.Button(
            capturing
                ? "Press a key"
                : chord.Length > 0 ? chord : UnboundCaption,
            () =>
            {
                vm.RebindingAction = capturing ? null : action.Id;
                vm.RebindingSlot = slot;
                vm.PresetArmed = false;
                vm.RebindRefusal = string.Empty;
                vm.RebindHeld.Clear();
                foreach (var (key, imguiKey) in KeyChord.CapturableTokens())
                    if (vm.KeyDown(key) || ImGui.IsKeyDown(imguiKey))
                        vm.RebindHeld.Add(key);
            },
            style: ControlStyle.Workspace with
            {
                Width = UiWidth.Fixed(KeybindSlotWidth),
            },
            help: slot == 0
                ? "Primary chord — click to rebind"
                : "Secondary chord — click to rebind",
            id: slot == 0 ? "primary" : "secondary");
    }
    private static void ApplyPreset(SettingsViewModel vm)
    {
        string name = PresetLabels[vm.PresetIndex];
        if (!vm.PresetArmed)
        {
            vm.PresetArmed = true;
            vm.PresetStatus = $"{name} chords replace every binding below, "
                + "both slots. Press Confirm preset to apply.";
            return;
        }
        vm.PresetArmed = false;
        vm.RebindingAction = null;
        vm.Bindings = KeybindRegistry.Bindings((KeybindPreset)vm.PresetIndex);
        vm.BindingRevision++;
        vm.PresetStatus = $"{name} chords loaded. Save to keep them.";
    }

    private static void DrawLibrary(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("Poser folder", form =>
        {
            HomeFolder(
                form, vm, "Folder", vm.PoserRoot,
                next => vm.PoserRoot = next,
                LibraryConfiguration.DefaultRoot,
                "Everything Poser saves lives here: Poses, Objects, Scenes, MCDFs and Auto-saves are folders inside it");
        }, divider: false);
        page.Section("Pose library", form =>
        {
            form.Switch(
                "Use library for Import",
                vm.UseLibraryWhenImporting,
                next => vm.UseLibraryWhenImporting = next,
                "Import buttons open the pose library instead of the file dialog");
            form.Switch(
                "Show file extensions",
                vm.LibraryShowExtensions,
                next => vm.LibraryShowExtensions = next,
                "Tile names carry .pose / .cmp");
        }, divider: false);
        page.Section("Source folders", form =>
        {
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
    private static void HomeFolder(
        Crystarium.FormScope form,
        SettingsViewModel vm,
        string label,
        string value,
        Action<string> onChange,
        string shipped,
        string help)
    {
        form.TextInputActions(
            label,
            value,
            onChange,
            actions =>
            {
                actions.Button(
                    "Browse",
                    () => vm.OnBrowseFolder?.Invoke(
                        value.Trim().Length == 0 ? shipped : value.Trim(),
                        onChange));
                actions.Button(
                    "Open",
                    () => vm.OnOpenFolder?.Invoke(
                        value.Trim().Length == 0 ? shipped : value.Trim()));
            },
            placeholder: shipped,
            help: help);

        string typed = value.Trim();
        if (typed.Length == 0)
            form.Status("Using " + shipped);
        else if (!System.IO.Directory.Exists(typed))
            form.Status("Folder does not exist yet — Poser creates it.");
    }
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
        page.Section("About", form =>
        {
            form.ReadOnly("Poser", vm.Version);
            form.ReadOnly("Stack", "Crystarium · PosingCore");
            form.Actions("Source", actions => actions.Button(
                "Open repository",
                () => vm.OnOpenRepository?.Invoke()));
            form.Status(
                "Coded with the use of AI. Design system transcribed from Picto.");
        }, divider: false);
        page.Section("Derived from", form =>
        {
            form.Actions("Repositories", actions =>
            {
                foreach (var project in Config.FirstRunNotice.Upstream)
                    actions.Button(
                        project.Name,
                        () => vm.OnOpenUrl?.Invoke(project.Url),
                        help: project.Url);
            });
            foreach (var project in Config.FirstRunNotice.Upstream)
                form.ReadOnly(project.Name, project.Credit);
            form.Status(
                "Poser is derivative of and heavily inspired by these projects.");
        });
        page.Section("Integrations", form =>
        {
            foreach (var integration in vm.Integrations)
                form.ReadOnly(
                    integration.Name,
                    integration.Available ? "Available" : integration.Detail,
                    unavailable: !integration.Available,
                    help: integration.Available
                        ? $"Poser can talk to {integration.Name}"
                        : $"Poser cannot talk to {integration.Name}; the "
                            + "features that need it are unavailable");
            if (vm.Integrations.Count == 0)
                form.Status("No integrations have been probed yet.");
            form.Actions(string.Empty, actions => actions.Button(
                "Refresh",
                () => vm.OnRefreshIntegrations?.Invoke(),
                help: "Ask each plugin again whether it is there"));
        });
    }
    private static void CaptureRebind(SettingsViewModel vm)
    {
        if (vm.RebindingAction is not { } action
            || !vm.Bindings.TryGetValue(action, out var slots))
        {
            vm.RebindingAction = null;
            vm.RebindHeld.Clear();
            return;
        }

        // The capture reads the GAME's key state — the same source the
        // runtime matcher fires from — because ImGui key events never
        // reach an unfocused widget. Edge detection is manual: a key
        // already down when the capture armed stays ignored until it has
        // been released once.
        var io = ImGui.GetIO();

        // The probe found the stubbed key source (2026-08-30) and retired;
        // the armed line states the plain instructions — or the refusal,
        // which stands until another chord lands.
        vm.RebindProbe = vm.RebindRefusal.Length > 0
            ? vm.RebindRefusal
            : $"Listening for {action}… press a chord. Escape cancels, "
                + "Backspace clears the slot.";

        if (vm.KeyDown(Dalamud.Game.ClientState.Keys.VirtualKey.ESCAPE)
            || ImGui.IsKeyDown(ImGuiKey.Escape))
        {
            vm.RebindingAction = null;
            vm.RebindHeld.Clear();
            return;
        }

        if (vm.KeyDown(Dalamud.Game.ClientState.Keys.VirtualKey.BACK)
            || ImGui.IsKeyDown(ImGuiKey.Backspace))
        {
            slots[vm.RebindingSlot] = string.Empty;
            vm.BindingRevision++;
            vm.RebindingAction = null;
            vm.RebindHeld.Clear();
            return;
        }

        foreach (var (key, imguiKey) in KeyChord.CapturableTokens())
        {
            bool down = vm.KeyDown(key) || ImGui.IsKeyDown(imguiKey);
            if (!down)
            {
                vm.RebindHeld.Remove(key);
                continue;
            }
            if (vm.RebindHeld.Contains(key))
                continue;
            string chord = new KeyChord(
                io.KeyCtrl || vm.KeyDown(
                    Dalamud.Game.ClientState.Keys.VirtualKey.CONTROL),
                io.KeyShift || vm.KeyDown(
                    Dalamud.Game.ClientState.Keys.VirtualKey.SHIFT),
                io.KeyAlt || vm.KeyDown(
                    Dalamud.Game.ClientState.Keys.VirtualKey.MENU),
                key).ToString();
            // NO colliding binds: a chord already bound anywhere else is
            // REFUSED — the capture stays armed and says who holds it.
            string? holder = null;
            foreach (var (otherAction, otherSlots) in vm.Bindings)
                for (int otherSlot = 0; otherSlot < 2; otherSlot++)
                {
                    if (otherAction == action
                        && otherSlot == vm.RebindingSlot)
                        continue;
                    if (string.Equals(
                            otherSlots[otherSlot], chord,
                            StringComparison.Ordinal))
                        holder = otherAction;
                }
            if (holder != null)
            {
                vm.RebindRefusal =
                    $"{chord} is bound to “{holder}” — press "
                    + "another chord";
                vm.RebindHeld.Add(key);
                return;
            }
            slots[vm.RebindingSlot] = chord;
            vm.BindingRevision++;
            vm.RebindingAction = null;
            vm.RebindHeld.Clear();
            vm.RebindRefusal = string.Empty;
            return;
        }
    }
}
