using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Integration;
using Poser.Config;
using Poser.Entities;
using Poser.Library;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Binds settings configuration to the settings view.</summary>
public class SettingsWindow : Window
{
    private SettingsViewModel _vm = new();
    private bool _saving;
    private readonly IAutoSaveService _autoSave;
    private readonly IIntegrationRuntimePort _integrations;

    public SettingsWindow(
        IAutoSaveService autoSave,
        IIntegrationRuntimePort integrations)
        : base($"Settings###{PluginConstants.PluginName}_settings",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _autoSave = autoSave;
        _integrations = integrations;
        // Close and Cancel discard unsaved edits.
        RespectCloseHotkey = false;
    }

    public override void OnOpen()
    {
        _saving = false;
        LoadFromConfig();
    }

    public override void OnClose()
    {
        if (!_saving)
        {
            var ui = ConfigurationService.Instance.Config.UI;
            ThemeSelection.Apply(ui.Theme, ui.AccentIndex);
        }
        _saving = false;
    }

    public override void PreDraw()
    {
        Size = new Vector2(SettingsView.DesignWidth, SettingsView.DesignHeight);
        SizeCondition = ImGuiCond.Always;
    }

    public override void Draw()
    {
        // The host supplies position and input; the view paints its frame.
        var min = ImGui.GetWindowPos();
        var owner = Interactive.BeginOwner(
            "poser-settings",
            InteractionLayer.Window,
            min,
            min + ImGui.GetWindowSize());
        try
        {
            SettingsView.Draw(_vm, min);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    private void LoadFromConfig()
    {
        var c = ConfigurationService.Instance.Config;
        _vm = new SettingsViewModel
        {
            Category = 1,
            OpenOnGPose = c.OpenOnGPoseEnter,
            CloseWithGPose = c.CloseWithGPose,
            PreservePoseAcrossRedraws = c.PreservePoseAcrossRedraws,
            RelativeSecondaryBones = c.RelativeSecondaryBones,
            LinkSiblingBones = c.LinkSiblingBones,
            FollowGameTarget = c.GPoseTargetChangesSelection,
            TargetFollowsSelection = c.SelectionChangesGPoseTarget,
            UndoDepth = c.UndoDepth,

            AutoSaveEnabled = c.AutoSave.Enabled,
            AutoSaveIntervalSeconds = c.AutoSave.IntervalSeconds,
            AutoSaveMaxKept = c.AutoSave.MaxAutoSaves.ToString(CultureInfo.InvariantCulture),
            AutoSaveCleanOnExit = c.AutoSave.CleanOnExit,
            SceneSnapshotsEnabled = c.AutoSave.SceneSnapshots,
            SceneSnapshotsMaxKept =
                c.AutoSave.MaxSceneSnapshots.ToString(CultureInfo.InvariantCulture),
            AutoSaveFolder = _autoSave.RootDirectory,

            BoneDotRadius = c.Skeleton.BoneDotRadius,
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
            DimInactiveActors = c.Skeleton.DimInactiveActors,
            InactiveActorOpacity = c.Skeleton.InactiveActorOpacity,
            ActiveActorSource = (int)c.Skeleton.ActiveActorSource,
            ShowFriendlyBoneNames = c.Skeleton.ShowFriendlyBoneNames,
            ShowAllVieraEars = c.Skeleton.ShowAllVieraEars,

            GizmoScale = c.Gizmo.GizmoScale,
            AllowHoldSnap = c.Gizmo.AllowHoldSnap,
            SnapRotationDegrees = c.Gizmo.SnapRotationDegrees,
            SnapLinearStep = c.Gizmo.SnapLinearStep,
            AllowRaySnap = c.Gizmo.AllowRaySnap,
            KeepGizmoWhenBonesHidden = c.Gizmo.KeepGizmoWhenBonesHidden,
            DisableDotsModifier = (int)c.Gizmo.DisableDotsModifier,
            DisableGizmoModifier = (int)c.Gizmo.DisableGizmoModifier,

            NsfwBones = c.Display.ShowNsfwBones,
            AnonymousMode = c.Display.AnonymousMode,
            Theme = c.UI.Theme,
            AccentIndex = ThemeSelection.NormalizeAccentIndex(c.UI.AccentIndex),

            TransformEntitySpeed = c.Transform.EntitySpeed,
            TransformBoneSpeed = c.Transform.BoneSpeed,

            CameraDefaultSpeed = c.Camera.DefaultMovementSpeed,
            CameraDefaultSensitivity = c.Camera.DefaultMouseSensitivity,
            CameraFastMultiplier = c.Camera.FastMultiplier,
            CameraSlowMultiplier = c.Camera.SlowMultiplier,
            CameraConsumeModifiers = c.Camera.ConsumeModifiersWhileFlying,
            CameraConsumeAllInput = c.Camera.ConsumeAllGameInput,
            CameraFlipPastNinety = c.Camera.FlipBindsPastNinety,

            DetachedShell = c.UI.DetachedShell,
            TreeGuides = c.UI.ShowTreeGuides,
            ShowInGPose = c.UI.ShowInGPose,
            ShowInCutscene = c.UI.ShowInCutscene,
            ShowWhenGameUiHidden = c.UI.ShowWhenGameUiHidden,
            SwapRotationXY = c.UI.SwapRotationXY,

            UseLibraryWhenImporting = c.Library.UseLibraryWhenImporting,
            LibraryShowExtensions = c.Library.ShowFileExtensions,

            // Home paths remain editable drafts until Save.
            PoseFolder = c.Library.ResolvePoseRoot(),
            SceneFolder = c.Library.ResolveSceneRoot(),
            McdfFolder = c.Library.ResolveMcdfRoot(),
            AutoSaveFolderDraft = c.AutoSave.RootDirectory,

            ConfigLoadFailure = ConfigurationService.Instance.LoadFailure,

            Version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev",
            OnSave = SaveToConfig,
            OnResetConfig = ResetConfig,
            OnRefreshIntegrations = () => ReadIntegrations(_vm),
            OnCancel = () => IsOpen = false,
            OnClose = () => IsOpen = false,
            OnThemePreview = ThemeSelection.Apply,
        };
        _vm.OnOpenRepository = () =>
            Process.Start(new ProcessStartInfo("https://github.com/midona-rhel/Poser") { UseShellExecute = true });
        _vm.OnOpenUrl = url => Dalamud.Utility.Util.OpenLink(url);
        _vm.OnOpenFolder = path =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                // Ensure the selected library directory exists before opening it.
                System.IO.Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // Opening an unavailable folder leaves the settings window open.
            }
        };

        // Home folders have dedicated fields and are excluded from this list.
        foreach (var source in c.Library.Sources)
        {
            if (IsHomeSource(source.Name))
                continue;
            _vm.LibrarySources.Add(new LibrarySourceVm
            {
                Name = source.Name,
                Path = source.Path,
                Enabled = source.Enabled,
            });
        }

        ReadIntegrations(_vm);

        // Edit a resolved copy so Cancel preserves live bindings.
        _vm.Bindings = KeybindRegistry.Resolve(c.UI.Bindings);
        _vm.BindingRevision++;
    }

    /// <summary>Refreshes the integration status snapshot.</summary>
    private void ReadIntegrations(SettingsViewModel vm)
    {
        vm.Integrations.Clear();
        vm.Integrations.Add(new IntegrationStatusVm(
            "Penumbra",
            _integrations.Penumbra.Available,
            _integrations.Penumbra.Detail));
        vm.Integrations.Add(new IntegrationStatusVm(
            "Glamourer",
            _integrations.Glamourer.Available,
            _integrations.Glamourer.Detail));
        vm.Integrations.Add(new IntegrationStatusVm(
            "Customize+",
            _integrations.CustomizePlus.Available,
            _integrations.CustomizePlus.Detail));
    }

    /// <summary>Resets one configuration slice and reloads the view model.</summary>
    private void ResetConfig(ConfigResetScope scope)
    {
        var svc = ConfigurationService.Instance;
        switch (scope)
        {
            case ConfigResetScope.Display:
                svc.ResetDisplay();
                break;
            case ConfigResetScope.Skeleton:
                svc.ResetSkeleton();
                break;
            case ConfigResetScope.UI:
                svc.ResetUI();
                break;
            default:
                svc.Reset();
                break;
        }

        int category = _vm.Category;
        LoadFromConfig();
        // Keep the current settings category after reset.
        _vm.Category = category;
        _vm.ResetStatus = "Reset. These are the shipped defaults.";
        ThemeSelection.Apply(
            svc.Config.UI.Theme, svc.Config.UI.AccentIndex);
    }

    private void SaveToConfig()
    {
        var svc = ConfigurationService.Instance;
        var c = svc.Config;

        c.OpenOnGPoseEnter = _vm.OpenOnGPose;
        c.CloseWithGPose = _vm.CloseWithGPose;
        c.PreservePoseAcrossRedraws = _vm.PreservePoseAcrossRedraws;
        c.RelativeSecondaryBones = _vm.RelativeSecondaryBones;
        c.LinkSiblingBones = _vm.LinkSiblingBones;
        c.GPoseTargetChangesSelection = _vm.FollowGameTarget;
        c.SelectionChangesGPoseTarget = _vm.TargetFollowsSelection;
        // Stored values are clamped to the control range.
        c.UndoDepth = Math.Clamp(_vm.UndoDepth, 0, 500);

        // Invalid retention drafts preserve the stored value.
        c.AutoSave.Enabled = _vm.AutoSaveEnabled;
        c.AutoSave.IntervalSeconds = (int)MathF.Round(_vm.AutoSaveIntervalSeconds);
        if (int.TryParse(
                _vm.AutoSaveMaxKept.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int keptAutoSaves)
            && keptAutoSaves >= 1)
            c.AutoSave.MaxAutoSaves = keptAutoSaves;
        _vm.AutoSaveMaxKept =
            c.AutoSave.MaxAutoSaves.ToString(CultureInfo.InvariantCulture);
        c.AutoSave.CleanOnExit = _vm.AutoSaveCleanOnExit;
        // Invalid retention drafts preserve the stored value.
        c.AutoSave.SceneSnapshots = _vm.SceneSnapshotsEnabled;
        if (int.TryParse(
                _vm.SceneSnapshotsMaxKept.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int keptSceneSnapshots)
            && keptSceneSnapshots >= 1)
            c.AutoSave.MaxSceneSnapshots = keptSceneSnapshots;
        _vm.SceneSnapshotsMaxKept =
            c.AutoSave.MaxSceneSnapshots.ToString(CultureInfo.InvariantCulture);

        c.Skeleton.BoneDotRadius = _vm.BoneDotRadius;
        c.Skeleton.SelectedBoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlaySelected);
        c.Skeleton.HoveredBoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayHovered);
        c.Skeleton.BoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayInactive);
        c.Skeleton.IkChainColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayIkChain);
        c.Skeleton.MirroredBoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayMirrored);
        c.Skeleton.SkeletonViewMode =
            (SkeletonViewMode)Math.Clamp(_vm.SkeletonShape, 0, 2);
        c.Skeleton.ShowSelectedBonesOnly = _vm.SelectedBonesOnly;
        c.Skeleton.BonePickBehavior =
            (BonePickBehavior)Math.Clamp(_vm.BonePickBehavior, 0, 1);
        c.Skeleton.ShowSkeletonLines = _vm.ShowSkeletonLines;
        c.Skeleton.BoneLineThickness = _vm.BoneLineThickness;
        c.Skeleton.BoneLineOpacity = _vm.BoneLineOpacity;
        c.Skeleton.BoneLineOpacityWhileUsing = _vm.BoneLineOpacityWhileUsing;
        c.Skeleton.SkeletonLineToCircle = _vm.SkeletonLineToCircle;
        c.Skeleton.HideSkeletonWhileDragging = _vm.HideSkeletonWhileDragging;
        c.Skeleton.DimInactiveActors = _vm.DimInactiveActors;
        c.Skeleton.InactiveActorOpacity =
            Math.Clamp(_vm.InactiveActorOpacity, 0f, 1f);
        c.Skeleton.ActiveActorSource =
            (ActiveActorSource)Math.Clamp(_vm.ActiveActorSource, 0, 2);
        // Bone names read this setting live.
        c.Skeleton.ShowFriendlyBoneNames = _vm.ShowFriendlyBoneNames;
        Core.BoneInfo.BoneInfoService.ShowFriendlyNames =
            _vm.ShowFriendlyBoneNames;
        c.Skeleton.ShowAllVieraEars = _vm.ShowAllVieraEars;

        c.Gizmo.GizmoScale = Math.Clamp(_vm.GizmoScale, 0.5f, 2f);
        c.Gizmo.AllowHoldSnap = _vm.AllowHoldSnap;
        c.Gizmo.SnapRotationDegrees =
            Math.Clamp(_vm.SnapRotationDegrees, 0.5f, 45f);
        c.Gizmo.SnapLinearStep = Math.Clamp(_vm.SnapLinearStep, 0.01f, 1f);
        c.Gizmo.AllowRaySnap = _vm.AllowRaySnap;
        c.Gizmo.KeepGizmoWhenBonesHidden = _vm.KeepGizmoWhenBonesHidden;
        c.Gizmo.DisableDotsModifier =
            (OverlayHoldModifier)Math.Clamp(_vm.DisableDotsModifier, 0, 2);
        c.Gizmo.DisableGizmoModifier =
            (OverlayHoldModifier)Math.Clamp(_vm.DisableGizmoModifier, 0, 2);

        c.Display.ShowNsfwBones = _vm.NsfwBones;
        c.Display.AnonymousMode = _vm.AnonymousMode;
        c.UI.Theme = _vm.Theme;
        // Persist a concrete accent position.
        c.UI.AccentIndex = ThemeSelection.NormalizeAccentIndex(_vm.AccentIndex);

        c.Transform.EntitySpeed =
            Math.Clamp(_vm.TransformEntitySpeed, 0.0005f, 0.05f);
        c.Transform.BoneSpeed =
            Math.Clamp(_vm.TransformBoneSpeed, 0.0005f, 0.05f);

        // Stored camera values are clamped to the control ranges.
        c.Camera.DefaultMovementSpeed = Math.Clamp(
            _vm.CameraDefaultSpeed,
            FreeCameraSpeed.Minimum,
            FreeCameraSpeed.Maximum);
        c.Camera.DefaultMouseSensitivity =
            Math.Clamp(_vm.CameraDefaultSensitivity, 0.001f, 0.2f);
        c.Camera.FastMultiplier = Math.Clamp(_vm.CameraFastMultiplier, 1f, 10f);
        c.Camera.SlowMultiplier = Math.Clamp(_vm.CameraSlowMultiplier, 0.05f, 1f);
        c.Camera.ConsumeModifiersWhileFlying = _vm.CameraConsumeModifiers;
        c.Camera.ConsumeAllGameInput = _vm.CameraConsumeAllInput;
        c.Camera.FlipBindsPastNinety = _vm.CameraFlipPastNinety;

        c.UI.DetachedShell = _vm.DetachedShell;
        c.UI.ShowTreeGuides = _vm.TreeGuides;
        c.UI.ShowInGPose = _vm.ShowInGPose;
        c.UI.ShowInCutscene = _vm.ShowInCutscene;
        c.UI.ShowWhenGameUiHidden = _vm.ShowWhenGameUiHidden;
        c.UI.SwapRotationXY = _vm.SwapRotationXY;

        // Save only registered bindings.
        c.UI.Bindings.Clear();
        foreach (var (action, slots) in _vm.Bindings)
            c.UI.Bindings[action] = slots.Copy();

        c.Library.UseLibraryWhenImporting = _vm.UseLibraryWhenImporting;
        c.Library.ShowFileExtensions = _vm.LibraryShowExtensions;
        c.Library.Sources.Clear();
        // Rebuild configured home folders before extra sources.
        c.Library.SetHomeRoot(
            LibraryConfiguration.PoseSourceName,
            LibraryConfiguration.DefaultPoseRoot,
            _vm.PoseFolder);
        c.Library.SetHomeRoot(
            LibraryConfiguration.SceneSourceName,
            LibraryConfiguration.DefaultSceneRoot,
            _vm.SceneFolder);
        c.Library.SetHomeRoot(
            LibraryConfiguration.McdfSourceName,
            LibraryConfiguration.DefaultMcdfRoot,
            _vm.McdfFolder);
        foreach (var source in _vm.LibrarySources)
        {
            string path = source.Path.Trim();
            string name = source.Name.Trim();
            if (path.Length == 0 && name.Length == 0)
                continue;
            if (IsHomeSource(name))
                continue;
            c.Library.Sources.Add(new LibrarySourceConfig
            {
                Name = name,
                Path = path,
                Enabled = source.Enabled,
            });
        }
        // Create configured home folders before applying the new paths.
        c.Library.EnsureHomeRootsExist();

        // The auto-save root applies on the next session.
        c.AutoSave.RootDirectory = _vm.AutoSaveFolderDraft.Trim().Length == 0
            ? _autoSave.RootDirectory
            : _vm.AutoSaveFolderDraft.Trim();

        _saving = true;
        ThemeSelection.Apply(c.UI.Theme, c.UI.AccentIndex);
        svc.ApplyChange();
        IsOpen = false;
    }

    /// <summary>Checks whether a source name identifies a home folder.</summary>
    private static bool IsHomeSource(string name)
    {
        foreach (var (home, _) in LibraryConfiguration.Homes)
            if (string.Equals(name, home, StringComparison.Ordinal))
                return true;
        return false;
    }
}
