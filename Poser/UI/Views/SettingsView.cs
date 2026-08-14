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

/// <summary>One configured library root, edited free of the persisted
/// <c>LibrarySourceConfig</c> until Save.</summary>
public sealed class LibrarySourceVm
{
    public string Name = "";
    public string Path = "";
    public bool Enabled = true;
}

/// <summary>One third-party plugin Poser talks to, as of the last probe:
/// whether it answered, and what it said if it did not.</summary>
public sealed record IntegrationStatusVm(
    string Name, bool Available, string Detail);

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
    public bool RelativeSecondaryBones;
    public bool LinkSiblingBones;
    public bool FollowGameTarget = true;
    public bool TargetFollowsSelection;
    /// <summary>How many edits undo keeps. Zero turns undo off, which is why
    /// the slider bottoms out there rather than at one.</summary>
    public int UndoDepth = 200;

    public bool AutoSaveEnabled = true;
    public float AutoSaveIntervalSeconds = 60f;
    /// <summary>Free numeric text, not a bounded slider: a shoot with hundreds
    /// of recovery points is a legitimate setup. Held as the raw string the
    /// user is typing and parsed at the config boundary, so a half-typed value
    /// never collapses to a number mid-keystroke.</summary>
    public string AutoSaveMaxKept = "10";
    public bool AutoSaveCleanOnExit;

    /// <summary>Whether the same cadence also snapshots the WHOLE scene.</summary>
    public bool SceneSnapshotsEnabled = true;

    /// <summary>Kept whole-scene snapshots, same free-text contract as
    /// <see cref="AutoSaveMaxKept"/>: one snapshot is one large document, so
    /// its retention is counted separately from the per-actor poses.</summary>
    public string SceneSnapshotsMaxKept = "5";
    /// <summary>The auto-save root on disk, for the Open-in-Explorer row.
    /// Empty when the binder has no auto-save service to ask.</summary>
    public string AutoSaveFolder = "";

    /// <summary>Index into <c>SkeletonShapeLabels</c>, matching
    /// <c>SkeletonViewMode</c>'s declaration order.</summary>
    public int SkeletonShape;

    public bool SelectedBonesOnly;

    /// <summary>Index into <c>BonePickBehaviorLabels</c>, matching
    /// <c>BonePickBehavior</c>'s declaration order.</summary>
    public int BonePickBehavior;

    public bool ShowSkeletonLines = true;
    public float BoneLineThickness = 1.0f;
    public float BoneLineOpacity = 0.23f;
    public float BoneLineOpacityWhileUsing = 0.15f;
    public bool SkeletonLineToCircle;
    public bool HideSkeletonWhileDragging;

    public bool DimInactiveActors;
    public float InactiveActorOpacity = 0.5f;
    /// <summary>Index into <see cref="ActiveActorLabels"/>, matching
    /// <see cref="ActiveActorSource"/>'s declaration order.</summary>
    public int ActiveActorSource;

    public bool ShowFriendlyBoneNames = true;
    public bool ShowAllVieraEars;

    public float GizmoScale = 1.0f;
    public bool AllowHoldSnap;
    public float SnapRotationDegrees = 5.0f;
    public float SnapLinearStep = 0.1f;
    public bool AllowRaySnap;
    public bool KeepGizmoWhenBonesHidden = true;
    /// <summary>Indices into <see cref="HoldModifierLabels"/>, matching
    /// <see cref="OverlayHoldModifier"/>'s declaration order.</summary>
    public int DisableDotsModifier;
    public int DisableGizmoModifier;

    /// <summary>Mirrors <c>TransformConfiguration</c>'s defaults — the
    /// constant every numeric transform row was written with.</summary>
    public float TransformEntitySpeed = 0.005f;
    public float TransformBoneSpeed = 0.005f;

    /// <summary>Mirrors <c>CameraConfiguration</c>'s defaults, which are what
    /// the camera already did before any of it was configurable.</summary>
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

    /// <summary>The three Dalamud UI-hide answers. Mirrors
    /// <c>UIConfiguration</c>'s defaults: the two Poser used to force stay on,
    /// the new one starts where Poser's behaviour already was.</summary>
    public bool ShowInGPose = true;
    public bool ShowInCutscene = true;
    public bool ShowWhenGameUiHidden;

    /// <summary>The EXTRA scanned folders. The Poser homes are edited on their
    /// own rows and are deliberately absent from this list, so each of the four
    /// paths has exactly ONE place it can be changed.</summary>
    public List<LibrarySourceVm> LibrarySources = [];

    /// <summary>The four Poser home folders, as editable drafts. Blank means
    /// "the shipped default", which the binder resolves on Save.</summary>
    public string PoseFolder = "";
    public string SceneFolder = "";
    public string McdfFolder = "";
    public string AutoSaveFolderDraft = "";

    public bool UseLibraryWhenImporting;
    public bool LibraryShowExtensions;
    public string LibraryNewName = "";
    public string LibraryNewPath = "";

    /// <summary>Every registered action's two chords, edited free of the
    /// persisted config until Save. Always complete — the binder fills it
    /// through <see cref="KeybindRegistry.Resolve"/> — so a row can index it
    /// without asking whether the action is there.</summary>
    public Dictionary<string, KeybindSlots> Bindings =
        KeybindRegistry.Bindings(KeybindPreset.Poser);

    /// <summary>The action whose slot is capturing, and which slot: 0 is
    /// primary, 1 secondary. Null is "no capture in progress" — the state the
    /// page opens in and returns to on Escape.</summary>
    public string? RebindingAction;
    public int RebindingSlot;

    public int PresetIndex;
    /// <summary>The preset switcher's first press arms; the second applies.
    /// Overwriting every chord is not something a stray click gets to do.
    /// </summary>
    public bool PresetArmed;
    public string PresetStatus = "";

    /// <summary>Bumped by every chord that moves. The conflict scan is a
    /// whole-table pass, so it runs when the table changes rather than once
    /// a frame.</summary>
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

    /// <summary>What Poser found when it last asked each integration whether
    /// it was there. Snapshotted at open and on Refresh rather than read per
    /// frame: each answer is an IPC call, and a settings page has no business
    /// making three of them every draw.</summary>
    public List<IntegrationStatusVm> Integrations = [];

    /// <summary>Non-empty when the stored config could not be read on load —
    /// the sentence <c>ConfigurationService</c> minted, naming the backup.
    /// Shown as a warning wherever the reset rows are, because that is the
    /// page a user who lost their settings ends up on.</summary>
    public string ConfigLoadFailure = "";

    /// <summary>Which reset is armed, if any. First press arms, second
    /// applies: wiping settings is not something a stray click gets to do —
    /// the preset switcher's idiom, for the same reason.</summary>
    public ConfigResetScope? ResetArmed;
    public string ResetStatus = "";

    public Action? OnSave;
    public Action? OnCancel;
    public Action? OnClose;
    public Action? OnOpenRepository;
    /// <summary>Opens one of the credited upstream repositories in the
    /// browser.</summary>
    public Action<string>? OnOpenUrl;
    /// <summary>Opens a folder in the OS file explorer, creating it first when
    /// it does not exist yet (a seeded Brio/Anamnesis root may never have been
    /// created by its own tool).</summary>
    public Action<string>? OnOpenFolder;
    public Action<UITheme, int>? OnThemePreview;

    /// <summary>Applies a confirmed reset and reloads this view model from the
    /// config it just replaced. Unlike every other row, a reset WRITES
    /// immediately — it is a discard, so there is nothing for Cancel to keep.
    /// </summary>
    public Action<ConfigResetScope>? OnResetConfig;

    /// <summary>Re-probes every integration and rewrites
    /// <see cref="Integrations"/>.</summary>
    public Action? OnRefreshIntegrations;
}

/// <summary>Which slice of the config a reset row throws away. One per
/// <c>ConfigurationService</c> reset method, so the four that existed with no
/// caller each have exactly one button.</summary>
public enum ConfigResetScope
{
    All,
    Display,
    Skeleton,
    UI,
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
        (TablerIcon.ArrowsMove, "Gizmo"),
        (TablerIcon.Video, "Camera"),
        (TablerIcon.LayoutPanel, "UI"),
        (TablerIcon.Keyboard, "Keybinds"),
        (TablerIcon.Folder, "Library"),
        (TablerIcon.Info, "About"),
    };

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

    /// <summary>The two depths worth snapping to: Off, and the shipped
    /// default the slider otherwise has no way back to.</summary>
    private static readonly float[] UndoDepthMarks = [0f, 200f];

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

        if (vm.RebindingAction != null)
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
                "Auto-save whole scenes",
                vm.SceneSnapshotsEnabled,
                next => vm.SceneSnapshotsEnabled = next,
                "Also snapshot the entire scene — actors, props, lights, cameras and the environment — on the same interval, into its own folder",
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
            // The folder row moved to POSER FOLDERS, where it is editable
            // rather than merely openable — one place per path.
        });
        // Brio's Transform Slider Speed group, in the same General page it
        // sits on there.
        page.Section("TRANSFORM SPEED", form =>
        {
            form.Slider(
                "Entity drag speed",
                vm.TransformEntitySpeed,
                0.0005f,
                0.05f,
                next => vm.TransformEntitySpeed = next,
                format: "0.0000",
                help: "How far one pixel of drag moves an actor, prop, light or camera");
            form.Slider(
                "Bone drag speed",
                vm.TransformBoneSpeed,
                0.0005f,
                0.05f,
                next => vm.TransformBoneSpeed = next,
                format: "0.0000",
                help: "How far one pixel of drag moves a single bone");
        });
        page.Section("RESET", form =>
        {
            // The load-failure notice lives here and nowhere else: this is the
            // page that explains what happened to the settings and the page
            // that offers to start them over.
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

    /// <summary>One armed reset button. The caption IS the state — "Reset" or
    /// "Confirm reset" — and arming any row disarms every other, so two rows
    /// can never both be one click from firing.</summary>
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
        page.Section("RESET", form => ResetRow(
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
        // The armature's own two display options. They stood on the toolbar
        // for one round and came off it on the user's call (2026-08-14): a
        // standing preference about how the overlay LOOKS belongs in Settings,
        // and the master overlay switch that stood beside them is gone
        // entirely — bone visibility is decided per actor.
        page.Section("ARMATURE", form =>
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
        });
        page.Section("INACTIVE ACTORS", form =>
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
        page.Section("BONE NAMES", form =>
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
        page.Section("RESET", form => ResetRow(
            vm,
            form,
            ConfigResetScope.Skeleton,
            "Skeleton settings",
            "Put the bone dot, line and color settings back to their defaults"));
    }

    /// <summary>Labels for <c>SkeletonViewMode</c>, in its declaration
    /// order.</summary>
    private static readonly string[] SkeletonShapeLabels =
        ["Dots", "Octahedra", "Joints"];

    /// <summary>Labels for <c>BonePickBehavior</c>, in its declaration
    /// order.</summary>
    private static readonly string[] BonePickBehaviorLabels =
        ["Ktisis", "Brio"];

    /// <summary>Labels for <c>ActiveActorSource</c>, in its declaration
    /// order.</summary>
    private static readonly string[] ActiveActorLabels =
        ["GPose target", "Selection", "Either"];

    /// <summary>Labels for <c>OverlayHoldModifier</c>, in its declaration
    /// order.</summary>
    private static readonly string[] HoldModifierLabels =
        ["Off", "Ctrl", "Shift"];

    private static void DrawGizmo(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("SIZE", form =>
            form.Slider(
                "Gizmo size",
                vm.GizmoScale,
                0.5f,
                2f,
                next => vm.GizmoScale = next,
                format: "0.00×",
                help: "Scales the handles; they keep the same size on screen at any distance"),
            divider: false);
        page.Section("SNAPPING", form =>
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
        page.Section("HOLD TO SUSPEND", form =>
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
        page.Section("VISIBILITY", form =>
            form.Switch(
                "Keep the gizmo when bones are hidden",
                vm.KeepGizmoWhenBonesHidden,
                next => vm.KeepGizmoWhenBonesHidden = next,
                "Off means hiding a bone from the overlay takes its gizmo with it"));
    }

    /// <summary>
    /// The camera decisions that belong to the user rather than to one
    /// camera: what a new free camera starts out flying like, what the speed
    /// modifiers are worth, and how much of the game's own input a live
    /// camera takes. Per-camera Speed and Sensitivity rows still override the
    /// defaults — this page seeds them, it does not replace them.
    /// </summary>
    private static void DrawCamera(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("NEW FREE CAMERAS", form =>
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
        page.Section("SPEED MODIFIERS", form =>
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
        page.Section("GAME INPUT", form =>
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
        page.Section("LAYOUT", form =>
        {
            form.Switch(
                "Detached UI",
                vm.DetachedShell,
                next => vm.DetachedShell = next,
                "Float the toolbar and the scene sidebar as their own windows");
        }, divider: false);
        page.Section("TREE", form =>
            form.Switch(
                "Tree guide lines",
                vm.TreeGuides,
                next => vm.TreeGuides = next,
                "Show hierarchy connector lines"));
        // The three hide decisions the game makes for every plugin. Poser
        // forced the first two on before they were a choice, so those are the
        // defaults; the third is new and starts off, which is what Poser did.
        page.Section("VISIBILITY", form =>
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
        page.Section("TRANSFORM ROWS", form =>
            form.Switch(
                "Swap rotation X and Y",
                vm.SwapRotationXY,
                next => vm.SwapRotationXY = next,
                "Show the rotation row's first two columns exchanged. "
                    + "The pose itself is unchanged"));
        // Keybinds live in UIConfiguration too, so this reset takes them with
        // it — which is also the row K4 asks for, stated where it is true.
        page.Section("RESET", form => ResetRow(
            vm,
            form,
            ConfigResetScope.UI,
            "UI settings",
            "Put the layout, visibility and keybind settings back to their defaults"));
    }

    private static readonly string[] PresetLabels =
        ["Poser", "Brio", "Ktisis"];

    /// <summary>Both slot buttons take the SAME fixed width, which is the
    /// whole of the two-column reading: a chord's column is a column because
    /// every row's slot starts and ends on the same x.</summary>
    private const float KeybindSlotWidth = 132f;

    /// <summary>An unbound slot says so in words. It is a legal state, not a
    /// missing value, and a blank button would read as a broken row.</summary>
    private const string UnboundCaption = "Unbound";

    private static void DrawKeybinds(
        SettingsViewModel vm,
        Crystarium.PageScope page)
    {
        page.Section("PRESET", form =>
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
                vm.PresetStatus.Length > 0
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
                // Ktisis's per-group reset, unarmed like its own: a group is a
                // handful of rows the user can see, so the button's blast
                // radius is on screen beside it — unlike the preset switcher,
                // which replaces every chord on the page.
                form.Actions(
                    string.Empty,
                    actions => actions.Button(
                        "Reset group",
                        () => ResetKeybindGroup(vm, group, start, count),
                        help: "Put this group's chords back to Poser's defaults"),
                    alignRight: true);
            });
    }

    /// <summary>Restores one group's shipped chords, BOTH slots. Poser's own
    /// defaults are what "default" means here, whichever preset the switcher
    /// is currently showing — a reset is a return, not a re-application.
    /// </summary>
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

    /// <summary>The registry's order IS the page's order and its groups ARE
    /// the sections, so the runs are cut once from the registry rather than
    /// filtered per frame.</summary>
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

        // Both halves of a collision flag, so the row that was edited last
        // carries no more blame than the row it landed on.
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
        // The caption is the chord, so the two slots can read identically
        // ("Unbound" beside "Unbound"); the slot's own id is what keeps them
        // apart as controls.
        actions.Button(
            capturing
                ? "Press a key"
                : chord.Length > 0 ? chord : UnboundCaption,
            () =>
            {
                vm.RebindingAction = capturing ? null : action.Id;
                vm.RebindingSlot = slot;
                vm.PresetArmed = false;
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

    /// <summary>The preset switcher's confirm. First press arms with the
    /// visible warning; the second replaces BOTH slots of every action,
    /// because a preset is a statement about the whole table and a leftover
    /// secondary would be a chord the preset never claimed.</summary>
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
        page.Section("POSER FOLDERS", form =>
        {
            // The four homes Poser owns. Poses, Scenes and MCDFs are SCANNED
            // roots, so a save that lands in one shows up in its tab without
            // the user navigating anywhere; auto-saves are written rather than
            // scanned, and the service reads its root once at load.
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
        page.Section("POSE LIBRARY", form =>
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

    /// <summary>
    /// One Poser home: the path is typed, the button opens it, and the row
    /// below states the only two things that can be wrong with a typed path —
    /// it is blank (the shipped folder is used) or it does not exist yet. There
    /// is no folder PICKER in the codebase and a file dialog cannot return a
    /// directory, so validation stands in for browsing.
    /// </summary>
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
                "Coded with the use of AI. Design system transcribed from Picto.");
        }, divider: false);

        // The same attribution the first-run notice carries, from the same
        // list — Settings is where a user goes looking for it afterwards.
        page.Section("DERIVED FROM", form =>
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

        // Brio's per-integration status line and refresh button. Poser calls
        // Penumbra, Glamourer and Customize+ throughout, and when one of them
        // is missing the features that ride on it degrade quietly — this is
        // the row that says which.
        page.Section("INTEGRATIONS", form =>
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

    /// <summary>
    /// The raw-input boundary: while a slot is capturing, the next key press
    /// becomes its chord. Escape abandons the capture and Backspace clears
    /// the slot — unbound is a state the user has to be able to reach, and
    /// the only key that could mean "none" is one that cannot also be a
    /// chord.
    ///
    /// <para>The scan is <see cref="KeyChord.CapturableKeys"/> and nothing
    /// else, so the keys the page can capture are exactly the keys a stored
    /// chord can name — a press it cannot store is a press it ignores rather
    /// than one it records unfirably.</para>
    /// </summary>
    private static void CaptureRebind(SettingsViewModel vm)
    {
        if (vm.RebindingAction is not { } action
            || !vm.Bindings.TryGetValue(action, out var slots))
        {
            vm.RebindingAction = null;
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Escape))
        {
            vm.RebindingAction = null;
            return;
        }

        if (ImGui.IsKeyPressed(ImGuiKey.Backspace))
        {
            slots[vm.RebindingSlot] = string.Empty;
            vm.BindingRevision++;
            vm.RebindingAction = null;
            return;
        }

        var io = ImGui.GetIO();
        foreach (var key in KeyChord.CapturableKeys())
        {
            if (!ImGui.IsKeyPressed(key)
                || KeyChord.FromImGui(key) is not { } virtualKey)
                continue;
            slots[vm.RebindingSlot] = new KeyChord(
                io.KeyCtrl, io.KeyShift, io.KeyAlt, virtualKey).ToString();
            vm.BindingRevision++;
            vm.RebindingAction = null;
            return;
        }
    }
}
