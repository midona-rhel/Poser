using Poser.Domain.Scene;
using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Integration;
using Poser.UI.Views;

using Poser.Domain.Cameras;
using Poser.UI.Widgets;
using Poser.Domain;
using Poser.Domain.Preferences;
using Poser.Documents.Config;
using Poser.Documents.Library;
using Poser.Application.Library;

namespace Poser.UI;
public class SettingsWindow : Window
{
#if DEBUG
    /// <summary>Effective config and the Settings window's live preview value.
    /// They can differ until the preview frame applies or Cancel restores it.</summary>
    public (bool Effective, bool Preview) DebugFriendlyBoneNames() =>
        (_configuration.Config.Skeleton.ShowFriendlyBoneNames,
            _vm.ShowFriendlyBoneNames);
#endif

    private SettingsViewModel _vm = new();
    private readonly SettingsView.SearchSettle _search = new();
    public bool IsRebindingShortcut => IsOpen && _vm.RebindingAction != null;
    private bool _saving;
    private readonly IPenumbraPort _penumbra;
    private readonly IGlamourerPort _glamourer;
    private readonly ICustomizePlusPort _customizePlus;
    private readonly Controls.IssueReportModal _issueReport;
    private readonly Dalamud.Plugin.Services.IKeyState _keyState;
    private readonly Dalamud.Plugin.Services.IPluginLog _log;
    private readonly IPoseLibraryService _library;
    private readonly UserNotices _notices;
    private readonly global::Poser.Application.Settings.ReleaseNotesState _releaseNotes;
    private bool _openLibrary;
    private bool _openSkeleton;

    private readonly global::Poser.Application.Settings.ConfigurationService _configuration;

    public SettingsWindow(
        global::Poser.Application.Settings.ConfigurationService configuration,
        Dalamud.Plugin.Services.IKeyState keyState,
        Dalamud.Plugin.Services.IPluginLog log,
        IPenumbraPort penumbra,
        IGlamourerPort glamourer,
        ICustomizePlusPort customizePlus,
        Controls.IssueReportModal issueReport,
        IPoseLibraryService library,
        UserNotices notices,
        global::Poser.Application.Settings.ReleaseNotesState releaseNotes)
        : base($"Settings###{PluginConstants.PluginName}_settings",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _configuration = configuration;
        _penumbra = penumbra;
        _glamourer = glamourer;
        _customizePlus = customizePlus;
        _issueReport = issueReport;
        _keyState = keyState;
        _log = log;
        _library = library;
        _notices = notices;
        _releaseNotes = releaseNotes;
        WireRuntime();
        RespectCloseHotkey = false;
    }

    /// <summary>Connects shortcut capture to the game's key state and logger.
    /// These delegates belong to the current view model; replacing it drops them.</summary>
    private void WireRuntime()
    {
        // The indexer throws for untracked keys, including some punctuation
        // codes. Check validity before reading each key during capture.
        _vm.KeyDown = key =>
            _keyState.IsVirtualKeyValid(key) && _keyState[key];
        _vm.DebugLog = message => _log.Debug(message);
    }

    /// <summary>What the config held when the window opened: Cancel, or
    /// closing without Save, puts previewed settings back. Library sources
    /// remain a separate draft until Save and are never restored on Cancel.</summary>
    private SettingsViewModel? _snapshot;
    private int _appliedSignature;
    private readonly Dalamud.Interface.ImGuiFileDialog.FileDialogManager _folderDialog = new();

    public override void OnOpen()
    {
        _saving = false;
        LoadFromConfig();
        _snapshot = BuildViewModel();
        WireRuntime();
        if (_openLibrary)
        {
            _vm.Category = SettingsView.LibraryPage;
            _vm.Search = string.Empty;
            _vm.ResetPageScroll = true;
            _openLibrary = false;
        }
        if (_openSkeleton)
        {
            _vm.Category = SettingsView.SkeletonPage;
            _vm.Search = string.Empty;
            _vm.ResetPageScroll = true;
            _openSkeleton = false;
        }
    }

    public void OpenSkeleton()
    {
        if (IsOpen)
        {
            _vm.Category = SettingsView.SkeletonPage;
            _vm.Search = string.Empty;
            _vm.ResetPageScroll = true;
        }
        else
        {
            _openSkeleton = true;
            IsOpen = true;
        }
    }

    public void OpenLibrary()
    {
        if (IsOpen)
        {
            _vm.Category = SettingsView.LibraryPage;
            _vm.Search = string.Empty;
            _vm.ShowSourceIssues = false;
            _vm.ResetPageScroll = true;
        }
        else
        {
            _openLibrary = true;
            IsOpen = true;
        }
    }

    public override void OnClose()
    {
        if (!_saving && _snapshot != null)
        {
            _vm = _snapshot;
            ApplyToConfig(preview: true);
            _configuration.NotifyChanged();
        }
        _snapshot = null;
        _saving = false;
    }

    public override void PreDraw()
    {
        Size = new Vector2(SettingsView.DesignWidth, SettingsView.DesignHeight);
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        var min = ImGui.GetWindowPos();
        var owner = Interactive.BeginOwner(
            "poser-settings",
            InteractionLayer.Window,
            min,
            min + ImGui.GetWindowSize());
        try
        {
            _vm.SourceSnapshot = _library.Snapshot;
            _vm.SourceScanBusy = _library.IsScanning;
            SettingsView.Draw(_vm, _search, min);
            _folderDialog.Draw();
            // Live: a change lands on the config and is announced the
            // frame it happens, so every consumer follows at once; only
            // Save persists it and only Cancel takes it back.
            if (_snapshot != null && _vm.Signature() != _appliedSignature)
            {
                _appliedSignature = _vm.Signature();
                ApplyToConfig(preview: true);
                _configuration.NotifyChanged();
            }
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    private void LoadFromConfig() => _vm = BuildViewModel();

    private SettingsViewModel BuildViewModel()
    {
        var c = _configuration.Config;
        var vm = new SettingsViewModel
        {
            Category = 0,
            ResetPageScroll = true,
            OpenOnGPose = c.OpenOnGPoseEnter,
            CloseWithGPose = c.CloseWithGPose,
            RelativeSecondaryBones = c.RelativeSecondaryBones,
            LinkSiblingBones = c.LinkSiblingBones,
            FollowGameTarget = c.GPoseTargetChangesSelection,
            TargetFollowsSelection = c.SelectionChangesGPoseTarget,
            BlockGPoseMouseTargeting = c.BlockGPoseMouseTargeting,
            StartGPoseWithoutDepthOfField = c.StartGPoseWithoutDepthOfField,
            UndoDepth = c.UndoDepth,
            ShowFrameProfiler = c.UI.ShowFrameProfiler,
            ShowNativeCheck = c.UI.ShowNativeCheck,

            AutoSaveEnabled = c.AutoSave.Enabled,
            AutoSaveIntervalSeconds = c.AutoSave.IntervalSeconds,
            AutoSaveCleanOnExit = c.AutoSave.CleanOnExit,
            SceneSnapshotsMaxKept = c.AutoSave.MaxSceneSnapshots,

            BoneDotRadius = c.Skeleton.BoneDotRadius,
            MapDotRadius = c.Skeleton.MapDotRadius,
            PerBoneSymmetry = c.PerBoneSymmetry,
            AutoLinkPairedBones = c.AutoLinkPairedBones,
            OverlaySelected = ImGui.ColorConvertU32ToFloat4(c.Skeleton.SelectedBoneColor),
            OverlayHovered = ImGui.ColorConvertU32ToFloat4(c.Skeleton.HoveredBoneColor),
            OverlayInactive = ImGui.ColorConvertU32ToFloat4(c.Skeleton.BoneColor),
            OverlayIkChain = ImGui.ColorConvertU32ToFloat4(c.Skeleton.IkChainColor),
            OverlayMirrored = ImGui.ColorConvertU32ToFloat4(c.Skeleton.MirroredBoneColor),
            SkeletonShape = (int)c.Skeleton.SkeletonViewMode,
            SelectedBonesOnly = c.Skeleton.ShowSelectedBonesOnly,
            BonePickBehavior = (int)c.Skeleton.BonePickBehavior,
            ShowSkeletonLines = c.Skeleton.ShowSkeletonLines,
            BoneLineThickness = c.Skeleton.BoneLineThickness,
            BoneLineOpacity = c.Skeleton.BoneLineOpacity,
            BoneLineOpacityWhileUsing = c.Skeleton.BoneLineOpacityWhileUsing,
            SkeletonLineToCircle = c.Skeleton.SkeletonLineToCircle,
            HideSkeletonWhileDragging = c.Skeleton.HideSkeletonWhileDragging,
            HideSkeletonOnActorSelection =
                c.Skeleton.HideSkeletonOnActorSelection,
            OnlyActiveActorBones = c.Skeleton.OnlyActiveActorBones,
            DefaultBonePresets = c.Skeleton.BoneVisibilityPresets.ToDictionary(p => p.Name, p => p.ShowByDefault, StringComparer.OrdinalIgnoreCase),
            BoneMapDefaults = c.Skeleton.BoneMapDefaults.ToList(),
            BoneMapPresets = c.Skeleton.BoneMapPresets.ToList(),
            DimInactiveActors = c.Skeleton.DimInactiveActors,
            InactiveActorOpacity = c.Skeleton.InactiveActorOpacity,
            ActiveActorSource = (int)c.Skeleton.ActiveActorSource,
            ShowFriendlyBoneNames = c.Skeleton.ShowFriendlyBoneNames,
            ShowAllVieraEars = c.Skeleton.ShowAllVieraEars,
            DriversFollowPose = c.Skeleton.DriversFollowPose,

            GizmoScale = c.Gizmo.GizmoScale,
            AllowHoldSnap = c.Gizmo.AllowHoldSnap,
            GroupScale = (int)c.Gizmo.GroupScale,
            SnapRotationDegrees = c.Gizmo.SnapRotationDegrees,
            SnapLinearStep = c.Gizmo.SnapLinearStep,
            AllowRaySnap = c.Gizmo.AllowRaySnap,
            KeepGizmoWhenBonesHidden = c.Gizmo.KeepGizmoWhenBonesHidden,
            UniversalCenterTranslates = c.Gizmo.UniversalCenterTranslates,
            HideGizmoWithoutArmature = c.Gizmo.HideGizmoWithoutArmature,

            NsfwBones = c.Display.ShowNsfwBones,
            AnonymousMode = c.Display.AnonymousMode,
            Theme = c.UI.Theme,
            AccentIndex = ThemeSelection.NormalizeAccentIndex(c.UI.AccentIndex),
            FillOpacity = c.UI.FillOpacity,
            BackdropBlur = c.UI.BackdropBlur,

            TransformEntitySpeed = c.Transform.EntitySpeed,
            TransformBoneSpeed = c.Transform.BoneSpeed,

            CameraDefaultSpeed = c.Camera.DefaultMovementSpeed,
            CameraDefaultSensitivity = c.Camera.DefaultMouseSensitivity,
            CameraFastMultiplier = c.Camera.FastMultiplier,
            CameraSlowMultiplier = c.Camera.SlowMultiplier,
            CameraConsumeModifiers = c.Camera.ConsumeModifiersWhileFlying,
            KeepBoundKeysFromGame = c.Camera.KeepBoundKeysFromGame,
            CameraFlipPastNinety = c.Camera.FlipBindsPastNinety,
            CameraLookThroughSelected = c.Camera.LookThroughSelectedCamera,
            CameraUpdateOrbitWithActorPosition = c.Camera.UpdateOrbitWithActorPosition,
            DefaultSpawnPlacement = (int)c.DefaultSpawnPlacement,

            DetachedShell = c.UI.DetachedShell,
            HideHandlesByDefault = c.UI.HideHandlesByDefault,
            TreeGuides = c.UI.ShowTreeGuides,
            ShowInGPose = c.UI.ShowInGPose,
            HideWhileManipulating = c.UI.HideWhileManipulating,
            HideWhileMovingCamera = c.UI.HideWhileMovingCamera,
            HideGizmoWhileManipulating = c.UI.HideGizmoWhileManipulating,
            KeepIkGizmoVisibleWhileManipulating = c.UI.KeepIkGizmoVisibleWhileManipulating,
            ShowInCutscene = c.UI.ShowInCutscene,
            ShowWhenGameUiHidden = c.UI.ShowWhenGameUiHidden,
            SwapRotationXY = c.UI.SwapRotationXY,

            UseLibraryWhenImporting = c.Library.UseLibraryWhenImporting,
            LibraryShowExtensions = c.Library.ShowFileExtensions,
            Library = new LibrarySettingsDraft(c.Library),

            ConfigLoadFailure = _configuration.LoadFailure,

            Version = System.Version.Parse(_releaseNotes.Version).ToString(3),
            OnSave = SaveToConfig,
            OnResetConfig = ResetConfig,
            OnCancel = () => IsOpen = false,
            OnClose = () => IsOpen = false,
        };
        vm.OnRefreshIntegrations = () => ReadIntegrations(vm);
        vm.OnBrowseFolder = (start, chosen) =>
            _folderDialog.OpenFolderDialog(
                "Choose folder",
                (ok, path) =>
                {
                    if (ok && !string.IsNullOrWhiteSpace(path))
                        chosen(path);
                },
                string.IsNullOrWhiteSpace(start) ? null : start);
        vm.OnReportIssue = _issueReport.Open;
        vm.OnOpenReleaseNotes = _releaseNotes.Open;
        vm.OnOpenRepository = () =>
            Process.Start(new ProcessStartInfo("https://github.com/midona-rhel/Poser") { UseShellExecute = true });
        vm.OnOpenUrl = url => Dalamud.Utility.Util.OpenLink(url);
        // The open button is only enabled for a saved row whose folder the
        // last scan found, but the folder can be removed after that scan.
        vm.OnOpenSource = source =>
        {
            var path = source.Path;
            try
            {
                if (!System.IO.Directory.Exists(path))
                {
                    _notices.Report(Outcome.Fail($"Folder '{path}' is missing or inaccessible. No folder was created."), "Source folders ▸ Open folder");
                    return;
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _notices.Report(Outcome.Fail($"Could not open '{path}': {ex.Message}"), "Source folders ▸ Open folder");
            }
        };
        vm.OnRetrySources = () =>
        {
            vm.LibraryStatus = string.Empty;
            _library.RequestScan();
        };
        vm.OnRepairSource = issue =>
        {
            if (LibrarySettingsDraft.TryRepair(issue, out var detail))
                _notices.Done("Folder created. Retrying saved sources.");
            else
                _notices.Report(Outcome.Fail(detail), "Create folder");
            _library.RequestScan();
        };

        ReadIntegrations(vm);
        vm.Bindings = KeybindRegistry.Resolve(c.UI.Bindings);
        vm.BindingRevision++;
        return vm;
    }
    private void ReadIntegrations(SettingsViewModel vm)
    {
        vm.Integrations.Clear();
        vm.Integrations.Add(new IntegrationStatusVm(
            "Penumbra",
            _penumbra.Penumbra.Available,
            _penumbra.Penumbra.Detail));
        vm.Integrations.Add(new IntegrationStatusVm(
            "Glamourer",
            _glamourer.Glamourer.Available,
            _glamourer.Glamourer.Detail));
        vm.Integrations.Add(new IntegrationStatusVm(
            "Customize+",
            _customizePlus.CustomizePlus.Available,
            _customizePlus.CustomizePlus.Detail));
    }
    /// <summary>
    /// Puts one page's settings back to their defaults and saves them, and
    /// saves nothing else. The window shows unsaved changes by writing them
    /// into the settings every frame (see <see cref="Draw"/>), and
    /// <see cref="_snapshot"/> holds the saved values that Cancel puts back.
    /// So the saved values go back before the reset saves, and the other
    /// pages' unsaved changes go back on top after it: Save still keeps them
    /// and Cancel still takes them back. Reset everything discards every
    /// unsaved change.
    /// </summary>
    private void ResetConfig(ConfigResetScope scope)
    {
        var unsaved = _vm;
        if (scope == ConfigResetScope.All)
        {
            _configuration.Reset();
            _snapshot = BuildViewModel();
            _vm = BuildViewModel();
        }
        else
        {
            _vm = _snapshot!;
            ApplyToConfig(preview: true);
            ResetPage(scope, save: true);
            _snapshot = BuildViewModel();

            _vm = unsaved;
            ApplyToConfig(preview: true);
            ResetPage(scope, save: false);
            var shown = BuildViewModel();
            // What the preview doesn't write stays as it was edited: key
            // bindings (unless this page reset them) and the Library page.
            if (scope != ConfigResetScope.UI)
            {
                shown.Bindings = unsaved.Bindings;
                shown.PresetIndex = unsaved.PresetIndex;
            }
            shown.Library = unsaved.Library;
            shown.UseLibraryWhenImporting = unsaved.UseLibraryWhenImporting;
            shown.LibraryShowExtensions = unsaved.LibraryShowExtensions;
            _vm = shown;
            _configuration.NotifyChanged();
        }

        _vm.Category = unsaved.Category;
        _vm.ResetStatus = "Reset. These are the shipped defaults.";
        _appliedSignature = _vm.Signature();
        WireRuntime();
    }

    private void ResetPage(ConfigResetScope scope, bool save)
    {
        switch (scope)
        {
            case ConfigResetScope.Display:
                _configuration.ResetDisplay(save);
                break;
            case ConfigResetScope.Skeleton:
                _configuration.ResetSkeleton(save);
                break;
            case ConfigResetScope.UI:
                _configuration.ResetUI(save);
                break;
        }
    }

    private void SaveToConfig()
    {
        if (!_vm.Library.TryApply(_configuration.Config.Library, out var detail))
        {
            _vm.LibraryStatus = detail;
            _notices.Report(Outcome.Fail(detail), "Settings ▸ Save", refusal: true);
            _vm.Category = SettingsView.LibraryPage;
            return;
        }
        ApplyToConfig(preview: false);
        _saving = true;
        _configuration.Save();
        _library.RequestScan();
        IsOpen = false;
    }

    /// <summary>The view model onto the config. A preview skips what
    /// touches disk or the keybind registry: library folders, the
    /// auto-save root, the bindings.</summary>
    private void ApplyToConfig(bool preview)
    {
        var svc = _configuration;
        var c = svc.Config;

        c.OpenOnGPoseEnter = _vm.OpenOnGPose;
        c.CloseWithGPose = _vm.CloseWithGPose;
        c.RelativeSecondaryBones = _vm.RelativeSecondaryBones;
        c.LinkSiblingBones = _vm.LinkSiblingBones;
        c.GPoseTargetChangesSelection = _vm.FollowGameTarget;
        c.SelectionChangesGPoseTarget = _vm.TargetFollowsSelection;
        c.BlockGPoseMouseTargeting = _vm.BlockGPoseMouseTargeting;
        c.StartGPoseWithoutDepthOfField = _vm.StartGPoseWithoutDepthOfField;
        c.UndoDepth = _vm.UndoDepth;
        c.AutoSave.Enabled = _vm.AutoSaveEnabled;
        c.AutoSave.IntervalSeconds = (int)MathF.Round(_vm.AutoSaveIntervalSeconds);
        c.AutoSave.CleanOnExit = _vm.AutoSaveCleanOnExit;
        // The settings class keeps at least one backup; the field shows what it kept.
        c.AutoSave.MaxSceneSnapshots = _vm.SceneSnapshotsMaxKept;
        _vm.SceneSnapshotsMaxKept = c.AutoSave.MaxSceneSnapshots;

        c.Skeleton.BoneDotRadius = _vm.BoneDotRadius;
        c.Skeleton.MapDotRadius = _vm.MapDotRadius;
        c.PerBoneSymmetry = _vm.PerBoneSymmetry;
        c.AutoLinkPairedBones = _vm.AutoLinkPairedBones;
        c.Skeleton.SelectedBoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlaySelected);
        c.Skeleton.HoveredBoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayHovered);
        c.Skeleton.BoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayInactive);
        c.Skeleton.IkChainColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayIkChain);
        c.Skeleton.MirroredBoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayMirrored);
        c.Skeleton.SkeletonViewMode = (SkeletonViewMode)_vm.SkeletonShape;
        c.Skeleton.ShowSelectedBonesOnly = _vm.SelectedBonesOnly;
        c.Skeleton.BonePickBehavior = (BonePickBehavior)_vm.BonePickBehavior;
        c.Skeleton.ShowSkeletonLines = _vm.ShowSkeletonLines;
        c.Skeleton.BoneLineThickness = _vm.BoneLineThickness;
        c.Skeleton.BoneLineOpacity = _vm.BoneLineOpacity;
        c.Skeleton.BoneLineOpacityWhileUsing = _vm.BoneLineOpacityWhileUsing;
        c.Skeleton.SkeletonLineToCircle = _vm.SkeletonLineToCircle;
        c.Skeleton.HideSkeletonWhileDragging = _vm.HideSkeletonWhileDragging;
        c.Skeleton.HideSkeletonOnActorSelection =
            _vm.HideSkeletonOnActorSelection;
        c.Skeleton.OnlyActiveActorBones = _vm.OnlyActiveActorBones;
        foreach (var preset in c.Skeleton.BoneVisibilityPresets)
            if (_vm.DefaultBonePresets.TryGetValue(preset.Name, out var show)) preset.ShowByDefault = show;
        c.Skeleton.BoneMapDefaults = _vm.BoneMapDefaults.ToList();
        c.Skeleton.DimInactiveActors = _vm.DimInactiveActors;
        c.Skeleton.InactiveActorOpacity = _vm.InactiveActorOpacity;
        c.Skeleton.ActiveActorSource = (ActiveActorSource)_vm.ActiveActorSource;
        c.Skeleton.ShowFriendlyBoneNames = _vm.ShowFriendlyBoneNames;
        c.Skeleton.ShowAllVieraEars = _vm.ShowAllVieraEars;
        c.Skeleton.DriversFollowPose = _vm.DriversFollowPose;

        c.Gizmo.GizmoScale = _vm.GizmoScale;
        c.Gizmo.AllowHoldSnap = _vm.AllowHoldSnap;
        c.Gizmo.GroupScale = (global::Poser.Domain.Transforms.GroupScaleMode)_vm.GroupScale;
        c.Gizmo.SnapRotationDegrees = _vm.SnapRotationDegrees;
        c.Gizmo.SnapLinearStep = _vm.SnapLinearStep;
        c.Gizmo.AllowRaySnap = _vm.AllowRaySnap;
        c.Gizmo.KeepGizmoWhenBonesHidden = _vm.KeepGizmoWhenBonesHidden;
        c.Gizmo.UniversalCenterTranslates = _vm.UniversalCenterTranslates;
        c.Gizmo.HideGizmoWithoutArmature = _vm.HideGizmoWithoutArmature;

        c.Display.ShowNsfwBones = _vm.NsfwBones;
        c.Display.AnonymousMode = _vm.AnonymousMode;
        c.UI.Theme = _vm.Theme;
        c.UI.AccentIndex = _vm.AccentIndex;
        c.UI.FillOpacity = _vm.FillOpacity;
        // Blur is stored separately from surface alpha.
        c.UI.BackdropBlur = _vm.BackdropBlur;

        c.Transform.EntitySpeed = _vm.TransformEntitySpeed;
        c.Transform.BoneSpeed = _vm.TransformBoneSpeed;
        c.Camera.DefaultMovementSpeed = _vm.CameraDefaultSpeed;
        c.Camera.DefaultMouseSensitivity = _vm.CameraDefaultSensitivity;
        c.Camera.FastMultiplier = _vm.CameraFastMultiplier;
        c.Camera.SlowMultiplier = _vm.CameraSlowMultiplier;
        c.Camera.ConsumeModifiersWhileFlying = _vm.CameraConsumeModifiers;
        c.Camera.KeepBoundKeysFromGame = _vm.KeepBoundKeysFromGame;
        c.Camera.FlipBindsPastNinety = _vm.CameraFlipPastNinety;
        c.Camera.LookThroughSelectedCamera = _vm.CameraLookThroughSelected;
        c.Camera.UpdateOrbitWithActorPosition = _vm.CameraUpdateOrbitWithActorPosition;
        c.DefaultSpawnPlacement =
            (global::Poser.Domain.Scene.ObjectPlacementMode)_vm.DefaultSpawnPlacement;

        c.UI.DetachedShell = _vm.DetachedShell;
        c.UI.HideHandlesByDefault = _vm.HideHandlesByDefault;
        c.UI.ShowTreeGuides = _vm.TreeGuides;
        c.UI.ShowInGPose = _vm.ShowInGPose;
        c.UI.HideWhileManipulating = _vm.HideWhileManipulating;
        c.UI.HideWhileMovingCamera = _vm.HideWhileMovingCamera;
        c.UI.HideGizmoWhileManipulating = _vm.HideGizmoWhileManipulating;
        c.UI.KeepIkGizmoVisibleWhileManipulating = _vm.KeepIkGizmoVisibleWhileManipulating;
        c.UI.ShowInCutscene = _vm.ShowInCutscene;
        c.UI.ShowWhenGameUiHidden = _vm.ShowWhenGameUiHidden;
        c.UI.SwapRotationXY = _vm.SwapRotationXY;
        c.UI.ShowFrameProfiler = _vm.ShowFrameProfiler;
        c.UI.ShowNativeCheck = _vm.ShowNativeCheck;

        if (preview)
            return;

        // Replaced whole, never merged: an action dropped from the registry
        // has no row to clear it from, and a stale entry would keep firing.
        c.UI.Bindings.Clear();
        foreach (var (action, slots) in _vm.Bindings)
            c.UI.Bindings[action] = slots.Copy();

        c.Library.UseLibraryWhenImporting = _vm.UseLibraryWhenImporting;
        c.Library.ShowFileExtensions = _vm.LibraryShowExtensions;
        // Sources are committed once by the draft transaction in SaveToConfig;
        // preview and Cancel never replace them.
        c.Library.EnsureHomeRootsExist();
    }
}
