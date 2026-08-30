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
    public float BoneDotRadius = 5f;
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
    public float AutoSaveIntervalSeconds = 60f;
    public string AutoSaveMaxKept = "10";
    public bool AutoSaveCleanOnExit;
    public bool SceneSnapshotsEnabled = true;
    public string SceneSnapshotsMaxKept = "5";
    public string AutoSaveFolder = "";
    public int SkeletonShape;

    public bool SelectedBonesOnly;
    public int BonePickBehavior;

    public bool ShowSkeletonLines = true;
    public float BoneLineThickness = 1.0f;
    public float BoneLineOpacity = 0.23f;
    public float BoneLineOpacityWhileUsing = 0.15f;
    public bool SkeletonLineToCircle;
    public bool HideSkeletonWhileDragging;
    public bool HideSkeletonOnActorSelection = true;

    public bool DimInactiveActors;
    public float InactiveActorOpacity = 0.5f;
    public int ActiveActorSource;

    public bool ShowFriendlyBoneNames = true;
    public bool ShowAllVieraEars;

    public float GizmoScale = 1.0f;
    public bool AllowHoldSnap;
    public float SnapRotationDegrees = 5.0f;
    public float SnapLinearStep = 0.1f;
    public bool AllowRaySnap;
    public bool KeepGizmoWhenBonesHidden = true;
    public int DisableDotsModifier;
    public int DisableGizmoModifier;
    public float TransformEntitySpeed = 0.005f;
    public float TransformBoneSpeed = 0.005f;
    public float CameraDefaultSpeed = FreeCameraSpeed.Default;
    public float CameraDefaultSensitivity = 0.1f;
    public float CameraFastMultiplier = 3f;
    public float CameraSlowMultiplier = 0.3f;
    public bool CameraConsumeModifiers = true;
    public bool CameraConsumeAllInput;
    public bool CameraFlipPastNinety;

    public bool DetachedShell;
    public bool TreeGuides = true;
    public bool SwapRotationXY;
    public bool ShowInGPose = true;
    public bool ShowInCutscene = true;
    public bool ShowWhenGameUiHidden;
    public List<LibrarySourceVm> LibrarySources = [];
    public string PoseFolder = "";
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
    public int RebindProbeFrame;

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
    private const float NavigationIconMargin = 2f;

    private const float NavigationPillRadius = 5f;

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
                "Show Poser automatically when entering GPose");
            form.Switch(
                "Close with GPose",
                vm.CloseWithGPose,
                next => vm.CloseWithGPose = next,
                "Hide all Poser windows when leaving GPose");
            form.Switch(
                "Link left and right bones",
                vm.LinkSiblingBones,
                next => vm.LinkSiblingBones = next,
                "Selecting a bone also selects its opposite-side counterpart, "
                    + "and keeps both eyes and the ear chains in step");
            form.Switch(
                "Keep relative angles between bones",
                vm.RelativeSecondaryBones,
                next => vm.RelativeSecondaryBones = next,
                "With several bones selected, turn the rest about the first "
                    + "one's frame so each keeps its angle to it");
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
            form.Slider(
                "Undo history",
                vm.UndoDepth,
                0f,
                500f,
                next => vm.UndoDepth = (int)MathF.Round(next),
                readout: static value => value < 1f
                    ? "Off"
                    : ((int)MathF.Round(value)).ToString(
                        CultureInfo.InvariantCulture) + " steps",
                marks: UndoDepthMarks,
                help: "How many edits Poser can undo; zero turns undo off");
        }, divider: false);
        page.Section("Auto-save", form =>
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
                "Auto-save whole scenes",
                vm.SceneSnapshotsEnabled,
                next => vm.SceneSnapshotsEnabled = next,
                "Also snapshot the entire scene — actors, objects, lights, cameras and the environment — on the same interval, into its own folder",
                disabled: !vm.AutoSaveEnabled);
            form.TextInput(
                "Kept scene snapshots",
                vm.SceneSnapshotsMaxKept,
                next => vm.SceneSnapshotsMaxKept = next,
                placeholder: "5",
                help: "How many whole-scene snapshots to keep; the oldest are deleted first",
                disabled: !vm.AutoSaveEnabled || !vm.SceneSnapshotsEnabled);
            form.Switch(
                "Clean up on GPose exit",
                vm.AutoSaveCleanOnExit,
                next => vm.AutoSaveCleanOnExit = next,
                "Delete all auto-saves when leaving GPose normally; after a crash they remain for recovery");
        });
        page.Section("Transform speed", form =>
        {
            form.Slider(
                "Entity drag speed",
                vm.TransformEntitySpeed,
                0.0005f,
                0.05f,
                next => vm.TransformEntitySpeed = next,
                format: "0.0000",
                help: "How far one pixel of drag moves an actor, object, light or camera");
            form.Slider(
                "Bone drag speed",
                vm.TransformBoneSpeed,
                0.0005f,
                0.05f,
                next => vm.TransformBoneSpeed = next,
                format: "0.0000",
                help: "How far one pixel of drag moves a single bone");
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
                "Measure what each Poser window, pane and section costs the "
                    + "frame, and list it worst first. Off, it records "
                    + "nothing.");
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
        page.Section("Bone overlay", form =>
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
        page.Section("Filters & privacy", form =>
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
                "UI fill opacity",
                vm.FillOpacity,
                UIConfiguration.MinimumFillOpacity,
                1f,
                next =>
                {
                    vm.FillOpacity = UIConfiguration.ClampFillOpacity(next);
                    vm.OnSurfaceEffectsPreview?.Invoke(
                        vm.FillOpacity, vm.BackdropBlur);
                },
                format: "0 %");
            form.Switch(
                "Backdrop blur",
                vm.BackdropBlur,
                next =>
                {
                    vm.BackdropBlur = next;
                    vm.OnSurfaceEffectsPreview?.Invoke(
                        vm.FillOpacity, vm.BackdropBlur);
                },
                "Blur window and popup backdrops; tooltips always stay unblurred");
        });
        page.Section("Reset", form => ResetRow(
            vm,
            form,
            ConfigResetScope.Display,
            "Display settings",
            "Put the overlay colors, filters and theme back to their defaults"));
    }

    private static void DrawSkeleton(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("Armature", form =>
        {
            form.Dropdown(
                "Bone shape",
                SkeletonShapeLabels,
                vm.SkeletonShape,
                next => vm.SkeletonShape = next,
                "How each bone is drawn: a plain dot, a solid pointing at its "
                    + "child, or a large joint");
            form.Switch(
                "Only selected bones",
                vm.SelectedBonesOnly,
                next => vm.SelectedBonesOnly = next,
                "Draw the bones that are selected and nothing else");
            form.Dropdown(
                "Bone pick behavior",
                BonePickBehaviorLabels,
                vm.BonePickBehavior,
                next => vm.BonePickBehavior = next,
                "What the wheel does over a stack of overlapping bones: "
                    + "Ktisis moves the highlight and the click picks it, "
                    + "Brio selects each bone as the wheel reaches it");
        }, divider: false);
        page.Section("Skeleton lines", form =>
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
            form.Slider(
                "Line opacity while dragging",
                vm.BoneLineOpacityWhileUsing,
                0f,
                1f,
                next => vm.BoneLineOpacityWhileUsing = next,
                format: "0%",
                help: "How visible the connectors stay while a gizmo handle is held",
                disabled: vm.HideSkeletonWhileDragging);
            form.Switch(
                "Stop lines at the dot",
                vm.SkeletonLineToCircle,
                next => vm.SkeletonLineToCircle = next,
                "Draw each connector to the edge of the bone circle instead of through its centre");
            form.Switch(
                "Hide the skeleton while dragging",
                vm.HideSkeletonWhileDragging,
                next => vm.HideSkeletonWhileDragging = next,
                "Take the dots and lines away for the length of a gizmo drag");
            form.Switch(
                "Hide skeleton when only an actor is selected",
                vm.HideSkeletonOnActorSelection,
                next => vm.HideSkeletonOnActorSelection = next,
                "Keep actor selection from opening the whole armature; "
                    + "select a bone to show its anchor");
        });
        page.Section("Inactive actors", form =>
        {
            form.Switch(
                "Dim inactive actors",
                vm.DimInactiveActors,
                next => vm.DimInactiveActors = next,
                "Fade every actor's overlay except the active one");
            form.Slider(
                "Inactive opacity",
                vm.InactiveActorOpacity,
                0f,
                1f,
                next => vm.InactiveActorOpacity = next,
                format: "0%",
                disabled: !vm.DimInactiveActors);
            form.Dropdown(
                "Active actor is",
                ActiveActorLabels,
                vm.ActiveActorSource,
                next => vm.ActiveActorSource = next,
                "Which actor counts as active: the game's GPose target, the current selection, or either",
                disabled: !vm.DimInactiveActors);
        });
        page.Section("Bone names", form =>
        {
            form.Switch(
                "Friendly bone names",
                vm.ShowFriendlyBoneNames,
                next => vm.ShowFriendlyBoneNames = next,
                "Name bones the way a person would (\"Jaw\") instead of the way the skeleton does (\"j_f_ago\")");
            form.Switch(
                "Show unused Viera ears",
                vm.ShowAllVieraEars,
                next => vm.ShowAllVieraEars = next,
                "Keep all four Viera ear sets, not only the pair the character wears");
        });
        page.Section("Reset", form => ResetRow(
            vm,
            form,
            ConfigResetScope.Skeleton,
            "Skeleton settings",
            "Put the bone dot, line and color settings back to their defaults"));
    }
    private static readonly string[] SkeletonShapeLabels =
        ["Dots", "Octahedra", "Joints"];
    private static readonly string[] BonePickBehaviorLabels =
        ["Ktisis", "Brio"];
    private static readonly string[] ActiveActorLabels =
        ["GPose target", "Selection", "Either"];
    private static readonly string[] HoldModifierLabels =
        ["Off", "Ctrl", "Shift"];

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
                help: "Scales the handles; they keep the same size on screen at any distance"),
            divider: false);
        page.Section("Snapping", form =>
        {
            form.Switch(
                "Hold Ctrl to snap",
                vm.AllowHoldSnap,
                next => vm.AllowHoldSnap = next,
                "Quantise a drag to fixed steps while Ctrl is held; add Shift for a tenth of the step");
            form.Slider(
                "Rotation step",
                vm.SnapRotationDegrees,
                0.5f,
                45f,
                next => vm.SnapRotationDegrees = next,
                format: "0.0°",
                disabled: !vm.AllowHoldSnap);
            form.Slider(
                "Move and scale step",
                vm.SnapLinearStep,
                0.01f,
                1f,
                next => vm.SnapLinearStep = next,
                format: "0.00",
                disabled: !vm.AllowHoldSnap);
            form.Switch(
                "Hold Shift to snap to the world",
                vm.AllowRaySnap,
                next => vm.AllowRaySnap = next,
                "While moving, put the target wherever the pointer meets the scene");
        });
        page.Section("Hold to suspend", form =>
        {
            form.Dropdown(
                "Disable bone dots",
                HoldModifierLabels,
                vm.DisableDotsModifier,
                next => vm.DisableDotsModifier = next,
                "Hold this to make the dots and lines non-interactive, so a gizmo handle underneath them can be grabbed");
            form.Dropdown(
                "Disable the gizmo",
                HoldModifierLabels,
                vm.DisableGizmoModifier,
                next => vm.DisableGizmoModifier = next,
                "Hold this to let the pointer through the gizmo to the bone dot behind it");
        });
        page.Section("Visibility", form =>
            form.Switch(
                "Keep the gizmo when bones are hidden",
                vm.KeepGizmoWhenBonesHidden,
                next => vm.KeepGizmoWhenBonesHidden = next,
                "Off means hiding a bone from the overlay takes its gizmo with it"));
    }
    private static void DrawCamera(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("New free cameras", form =>
        {
            form.Slider(
                "Movement speed",
                vm.CameraDefaultSpeed,
                FreeCameraSpeed.Minimum,
                FreeCameraSpeed.Maximum,
                next => vm.CameraDefaultSpeed = next,
                format: "0.000",
                help: "The fly speed a newly created free camera starts with");
            form.Slider(
                "Mouse sensitivity",
                vm.CameraDefaultSensitivity,
                0.001f,
                0.2f,
                next => vm.CameraDefaultSensitivity = next,
                format: "0.000",
                help: "How far a right-drag turns a newly created free camera");
        }, divider: false);
        page.Section("Speed modifiers", form =>
        {
            form.Slider(
                "Hold Ctrl",
                vm.CameraFastMultiplier,
                1f,
                10f,
                next => vm.CameraFastMultiplier = next,
                format: "0.0×",
                help: "What holding Ctrl multiplies the fly speed by");
            form.Slider(
                "Hold Alt",
                vm.CameraSlowMultiplier,
                0.05f,
                1f,
                next => vm.CameraSlowMultiplier = next,
                format: "0.00×",
                help: "What holding Alt multiplies the fly speed by");
        });
        page.Section("Game input", form =>
        {
            form.Switch(
                "Consume modifiers while flying",
                vm.CameraConsumeModifiers,
                next => vm.CameraConsumeModifiers = next,
                "Take Space, Shift, Ctrl and Alt off the game while a free camera flies; off lets your character still jump and sprint");
            form.Switch(
                "Consume all game input in GPose",
                vm.CameraConsumeAllInput,
                next => vm.CameraConsumeAllInput = next,
                "Take every key off the game while in GPose, except Escape and Enter");
            form.Switch(
                "Flip fly keys past 90°",
                vm.CameraFlipPastNinety,
                next => vm.CameraFlipPastNinety = next,
                "Once the camera is rolled past a quarter turn, invert the sideways and vertical fly keys so they still move you the way the screen shows");
        });
    }

    private static void DrawUi(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("Layout", form =>
        {
            form.Switch(
                "Detached UI",
                vm.DetachedShell,
                next => vm.DetachedShell = next,
                "Float the toolbar and the scene sidebar as their own windows");
        }, divider: false);
        page.Section("Tree", form =>
            form.Switch(
                "Tree guide lines",
                vm.TreeGuides,
                next => vm.TreeGuides = next,
                "Show hierarchy connector lines"));
        page.Section("Visibility", form =>
        {
            form.Switch(
                "Show in GPose",
                vm.ShowInGPose,
                next => vm.ShowInGPose = next,
                "Keep Poser on screen while GPose hides the game's UI");
            form.Switch(
                "Show in cutscenes",
                vm.ShowInCutscene,
                next => vm.ShowInCutscene = next,
                "Keep Poser on screen during cutscenes");
            form.Switch(
                "Show when the game UI is hidden",
                vm.ShowWhenGameUiHidden,
                next => vm.ShowWhenGameUiHidden = next,
                "Keep Poser on screen after you hide the HUD yourself (Scroll Lock) or the game hides it for you");
        });
        page.Section("Transform rows", form =>
            form.Switch(
                "Swap rotation X and Y",
                vm.SwapRotationXY,
                next => vm.SwapRotationXY = next,
                "Show the rotation row's first two columns exchanged. "
                    + "The pose itself is unchanged"));
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
        page.Section("Poser folders", form =>
        {
            HomeFolder(
                form, vm, "Poses", vm.PoseFolder,
                next => vm.PoseFolder = next,
                LibraryConfiguration.DefaultPoseRoot,
                "Where saved poses go, and the folder the Poses tab scans");
            HomeFolder(
                form, vm, "Scenes", vm.SceneFolder,
                next => vm.SceneFolder = next,
                LibraryConfiguration.DefaultSceneRoot,
                "Where saved scenes go, and the folder the Scenes tab scans");
            HomeFolder(
                form, vm, "Character files", vm.McdfFolder,
                next => vm.McdfFolder = next,
                LibraryConfiguration.DefaultMcdfRoot,
                "Where exported character files go, and the folder the MCDF tab scans");
            HomeFolder(
                form, vm, "Auto-saves", vm.AutoSaveFolderDraft,
                next => vm.AutoSaveFolderDraft = next,
                vm.AutoSaveFolder,
                "Where auto-save snapshot folders are written");
            if (vm.AutoSaveFolder.Length > 0 &&
                !string.Equals(
                    vm.AutoSaveFolderDraft.Trim(),
                    vm.AutoSaveFolder,
                    StringComparison.OrdinalIgnoreCase))
            {
                form.Status(
                    "Auto-saves keep writing to " + vm.AutoSaveFolder
                    + " until Poser is reloaded.");
            }
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
            actions => actions.Button(
                "Open",
                () => vm.OnOpenFolder?.Invoke(
                    value.Trim().Length == 0 ? shipped : value.Trim()),
                help: "Show this folder in Windows Explorer"),
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

        // The live probe: what BOTH sources see this frame, shown in the
        // page and logged (throttled). This line is the ground truth the
        // three blind fixes never had.
        int gameDown = 0;
        int imguiDown = 0;
        string first = "none";
        foreach (var (probeKey, probeImGui) in KeyChord.CapturableTokens())
        {
            bool g = vm.KeyDown(probeKey);
            bool m = ImGui.IsKeyDown(probeImGui);
            if (g) gameDown++;
            if (m) imguiDown++;
            if ((g || m) && first == "none")
                first = probeKey.ToString();
        }
        vm.RebindProbe =
            $"Listening for {action}… game:{gameDown} imgui:{imguiDown} "
            + $"first:{first} held:{vm.RebindHeld.Count} "
            + $"capture:{io.WantCaptureKeyboard} text:{io.WantTextInput}";
        if (++vm.RebindProbeFrame % 30 == 0 || first != "none")
            vm.DebugLog?.Invoke($"[Rebind] {vm.RebindProbe}");

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
            slots[vm.RebindingSlot] = new KeyChord(
                io.KeyCtrl || vm.KeyDown(
                    Dalamud.Game.ClientState.Keys.VirtualKey.CONTROL),
                io.KeyShift || vm.KeyDown(
                    Dalamud.Game.ClientState.Keys.VirtualKey.SHIFT),
                io.KeyAlt || vm.KeyDown(
                    Dalamud.Game.ClientState.Keys.VirtualKey.MENU),
                key).ToString();
            vm.BindingRevision++;
            vm.RebindingAction = null;
            vm.RebindHeld.Clear();
            return;
        }
    }
}
