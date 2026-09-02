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
public class SettingsWindow : Window
{
    private SettingsViewModel _vm = new();
    private bool _saving;
    private readonly IAutoSaveService _autoSave;
    private readonly IIntegrationRuntimePort _integrations;
    private readonly Dalamud.Plugin.Services.IKeyState _keyState;
    private readonly Dalamud.Plugin.Services.IPluginLog _log;

    public SettingsWindow(
        IAutoSaveService autoSave,
        Dalamud.Plugin.Services.IKeyState keyState,
        Dalamud.Plugin.Services.IPluginLog log,
        IIntegrationRuntimePort integrations)
        : base($"Settings###{PluginConstants.PluginName}_settings",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _autoSave = autoSave;
        _integrations = integrations;
        _keyState = keyState;
        _log = log;
        WireRuntime();
        RespectCloseHotkey = false;
    }

    /// <summary>Delegates the vm needs at RUNTIME — re-wired after every
    /// vm rebuild, because LoadFromConfig REPLACES the whole vm and the
    /// constructor's wiring silently died with it: the capture read a
    /// stubbed key source through four fixes (2026-08-30).</summary>
    private void WireRuntime()
    {
        // Guarded: the indexer THROWS for virtual keys the game does not
        // track (several OEM punctuation codes), and one bad key would
        // kill the whole capture loop with an exception per frame.
        _vm.KeyDown = key =>
            _keyState.IsVirtualKeyValid(key) && _keyState[key];
        _vm.DebugLog = message => _log.Debug(message);
    }

    /// <summary>What the config held when the window opened: Cancel, or
    /// closing without Save, puts it back — every change previews live on
    /// the config while the window is open.</summary>
    private SettingsViewModel? _snapshot;

    public override void OnOpen()
    {
        _saving = false;
        LoadFromConfig();
        _snapshot = BuildViewModel();
        WireRuntime();
    }

    public override void OnClose()
    {
        if (!_saving && _snapshot != null)
        {
            _vm = _snapshot;
            ApplyToConfig(preview: true);
            var ui = ConfigurationService.Instance.Config.UI;
            ThemeSelection.Apply(ui.Theme, ui.AccentIndex);
            Crystarium.FloatingSurface.ConfigureEffects(
                ui.FillOpacity, ui.BackdropBlur);
        }
        _snapshot = null;
        _saving = false;
        // The page is remembered either way: it is where the window
        // opens, not a setting anyone saves.
        var svc = ConfigurationService.Instance;
        if (svc.Config.UI.LastSettingsPage != _vm.Category)
        {
            svc.Config.UI.LastSettingsPage = _vm.Category;
            svc.ApplyChange();
        }
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
            SettingsView.Draw(_vm, min);
            // Live preview: the config carries the edits as they happen;
            // only Save persists them and only Cancel takes them back.
            if (_snapshot != null)
                ApplyToConfig(preview: true);
        }
        finally
        {
            Interactive.EndOwner(owner);
        }
    }

    private void LoadFromConfig() => _vm = BuildViewModel();

    private SettingsViewModel BuildViewModel()
    {
        var c = ConfigurationService.Instance.Config;
        var vm = new SettingsViewModel
        {
            Category = Math.Clamp(c.UI.LastSettingsPage, 0, SettingsView.PageCount - 1),
            OpenOnGPose = c.OpenOnGPoseEnter,
            CloseWithGPose = c.CloseWithGPose,
            RelativeSecondaryBones = c.RelativeSecondaryBones,
            LinkSiblingBones = c.LinkSiblingBones,
            FollowGameTarget = c.GPoseTargetChangesSelection,
            TargetFollowsSelection = c.SelectionChangesGPoseTarget,
            UndoDepth = c.UndoDepth,
            ShowFrameProfiler = c.UI.ShowFrameProfiler,

            AutoSaveEnabled = c.AutoSave.Enabled,
            AutoSaveIntervalSeconds = c.AutoSave.IntervalSeconds,
            AutoSaveMaxKept = c.AutoSave.MaxAutoSaves.ToString(CultureInfo.InvariantCulture),
            AutoSaveCleanOnExit = c.AutoSave.CleanOnExit,
            SceneSnapshotsEnabled = c.AutoSave.SceneSnapshots,
            SceneSnapshotsMaxKept =
                c.AutoSave.MaxSceneSnapshots.ToString(CultureInfo.InvariantCulture),
            AutoSaveFolder = _autoSave.RootDirectory,

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
            DimInactiveActors = c.Skeleton.DimInactiveActors,
            InactiveActorOpacity = c.Skeleton.InactiveActorOpacity,
            ActiveActorSource = (int)c.Skeleton.ActiveActorSource,
            ShowFriendlyBoneNames = c.Skeleton.ShowFriendlyBoneNames,
            ShowAllVieraEars = c.Skeleton.ShowAllVieraEars,

            GizmoScale = c.Gizmo.GizmoScale,
            AllowHoldSnap = c.Gizmo.AllowHoldSnap,
            GroupScale = (int)c.Gizmo.GroupScale,
            SnapRotationDegrees = c.Gizmo.SnapRotationDegrees,
            SnapLinearStep = c.Gizmo.SnapLinearStep,
            AllowRaySnap = c.Gizmo.AllowRaySnap,
            KeepGizmoWhenBonesHidden = c.Gizmo.KeepGizmoWhenBonesHidden,
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
            CameraConsumeAllInput = c.Camera.ConsumeAllGameInput,
            CameraFlipPastNinety = c.Camera.FlipBindsPastNinety,
            CameraLookThroughSelected = c.Camera.LookThroughSelectedCamera,
            DefaultSpawnPlacement = (int)c.DefaultSpawnPlacement,

            DetachedShell = c.UI.DetachedShell,
            TreeGuides = c.UI.ShowTreeGuides,
            ShowInGPose = c.UI.ShowInGPose,
            HideWhileManipulating = c.UI.HideWhileManipulating,
            HideGizmoWhileManipulating = c.UI.HideGizmoWhileManipulating,
            ShowInCutscene = c.UI.ShowInCutscene,
            ShowWhenGameUiHidden = c.UI.ShowWhenGameUiHidden,
            SwapRotationXY = c.UI.SwapRotationXY,

            UseLibraryWhenImporting = c.Library.UseLibraryWhenImporting,
            LibraryShowExtensions = c.Library.ShowFileExtensions,
            PoserRoot = c.Library.ResolveRoot(),
            PoseFolder = c.Library.ResolvePoseRoot(),
            ObjectsFolder = c.Library.ResolveObjectsRoot(),
            SceneFolder = c.Library.ResolveSceneRoot(),
            McdfFolder = c.Library.ResolveMcdfRoot(),
            AutoSaveFolderDraft = c.AutoSave.RootDirectory,

            ConfigLoadFailure = ConfigurationService.Instance.LoadFailure,

            Version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev",
            OnSave = SaveToConfig,
            OnResetConfig = ResetConfig,
            OnCancel = () => IsOpen = false,
            OnClose = () => IsOpen = false,
            OnThemePreview = ThemeSelection.Apply,
            OnSurfaceEffectsPreview = Crystarium.FloatingSurface.ConfigureEffects,
        };
        vm.OnRefreshIntegrations = () => ReadIntegrations(vm);
        vm.OnOpenRepository = () =>
            Process.Start(new ProcessStartInfo("https://github.com/midona-rhel/Poser") { UseShellExecute = true });
        vm.OnOpenUrl = url => Dalamud.Utility.Util.OpenLink(url);
        vm.OnOpenFolder = path =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            try
            {
                System.IO.Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
            }
        };
        foreach (var source in c.Library.Sources)
        {
            if (IsHomeSource(source.Name))
                continue;
            vm.LibrarySources.Add(new LibrarySourceVm
            {
                Name = source.Name,
                Path = source.Path,
                Enabled = source.Enabled,
            });
        }

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
        // A reset is committed: Cancel afterwards keeps it.
        _snapshot = BuildViewModel();
        _vm.Category = category;
        _vm.ResetStatus = "Reset. These are the shipped defaults.";
        ThemeSelection.Apply(
            svc.Config.UI.Theme, svc.Config.UI.AccentIndex);
    }

    private void SaveToConfig()
    {
        ApplyToConfig(preview: false);
        var svc = ConfigurationService.Instance;
        var c = svc.Config;
        _saving = true;
        ThemeSelection.Apply(c.UI.Theme, c.UI.AccentIndex);
        Crystarium.FloatingSurface.ConfigureEffects(
            c.UI.FillOpacity, c.UI.BackdropBlur);
        svc.ApplyChange();
        IsOpen = false;
    }

    /// <summary>The view model onto the config. A preview skips what
    /// touches disk or the keybind registry: library folders, the
    /// auto-save root, the bindings.</summary>
    private void ApplyToConfig(bool preview)
    {
        var svc = ConfigurationService.Instance;
        var c = svc.Config;

        c.OpenOnGPoseEnter = _vm.OpenOnGPose;
        c.CloseWithGPose = _vm.CloseWithGPose;
        c.RelativeSecondaryBones = _vm.RelativeSecondaryBones;
        c.LinkSiblingBones = _vm.LinkSiblingBones;
        c.GPoseTargetChangesSelection = _vm.FollowGameTarget;
        c.SelectionChangesGPoseTarget = _vm.TargetFollowsSelection;
        c.UndoDepth = Math.Clamp(_vm.UndoDepth, 0, 500);
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
        c.Skeleton.MapDotRadius = _vm.MapDotRadius;
        c.PerBoneSymmetry = _vm.PerBoneSymmetry;
        c.AutoLinkPairedBones = _vm.AutoLinkPairedBones;
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
        c.Skeleton.HideSkeletonOnActorSelection =
            _vm.HideSkeletonOnActorSelection;
        c.Skeleton.OnlyActiveActorBones = _vm.OnlyActiveActorBones;
        c.Skeleton.DimInactiveActors = _vm.DimInactiveActors;
        c.Skeleton.InactiveActorOpacity =
            Math.Clamp(_vm.InactiveActorOpacity, 0f, 1f);
        c.Skeleton.ActiveActorSource =
            (ActiveActorSource)Math.Clamp(_vm.ActiveActorSource, 0, 2);
        c.Skeleton.ShowFriendlyBoneNames = _vm.ShowFriendlyBoneNames;
        Core.BoneInfo.BoneInfoService.ShowFriendlyNames =
            _vm.ShowFriendlyBoneNames;
        c.Skeleton.ShowAllVieraEars = _vm.ShowAllVieraEars;

        c.Gizmo.GizmoScale = Math.Clamp(_vm.GizmoScale, 0.5f, 2f);
        c.Gizmo.AllowHoldSnap = _vm.AllowHoldSnap;
        c.Gizmo.GroupScale = (global::Poser.Domain.Transforms.GroupScaleMode)Math.Clamp(_vm.GroupScale, 0, 1);
        c.Gizmo.SnapRotationDegrees =
            Math.Clamp(_vm.SnapRotationDegrees, 0.5f, 45f);
        c.Gizmo.SnapLinearStep = Math.Clamp(_vm.SnapLinearStep, 0.01f, 1f);
        c.Gizmo.AllowRaySnap = _vm.AllowRaySnap;
        c.Gizmo.KeepGizmoWhenBonesHidden = _vm.KeepGizmoWhenBonesHidden;
        c.Gizmo.HideGizmoWithoutArmature = _vm.HideGizmoWithoutArmature;

        c.Display.ShowNsfwBones = _vm.NsfwBones;
        c.Display.AnonymousMode = _vm.AnonymousMode;
        c.UI.Theme = _vm.Theme;
        // Persist a concrete accent position.
        c.UI.AccentIndex = ThemeSelection.NormalizeAccentIndex(_vm.AccentIndex);
        c.UI.FillOpacity = _vm.FillOpacity;
        // Blur is stored separately from surface alpha.
        c.UI.BackdropBlur = _vm.BackdropBlur;

        c.Transform.EntitySpeed =
            Math.Clamp(_vm.TransformEntitySpeed, 0.0005f, 0.05f);
        c.Transform.BoneSpeed =
            Math.Clamp(_vm.TransformBoneSpeed, 0.0005f, 0.05f);
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
        c.Camera.LookThroughSelectedCamera = _vm.CameraLookThroughSelected;
        c.DefaultSpawnPlacement =
            (global::Poser.Files.ObjectPlacementMode)_vm.DefaultSpawnPlacement;

        c.UI.DetachedShell = _vm.DetachedShell;
        c.UI.ShowTreeGuides = _vm.TreeGuides;
        c.UI.ShowInGPose = _vm.ShowInGPose;
        c.UI.HideWhileManipulating = _vm.HideWhileManipulating;
        c.UI.HideGizmoWhileManipulating = _vm.HideGizmoWhileManipulating;
        c.UI.ShowInCutscene = _vm.ShowInCutscene;
        c.UI.ShowWhenGameUiHidden = _vm.ShowWhenGameUiHidden;
        c.UI.SwapRotationXY = _vm.SwapRotationXY;
        c.UI.ShowFrameProfiler = _vm.ShowFrameProfiler;

        if (preview)
            return;

        // Replaced whole, never merged: an action dropped from the registry
        // has no row to clear it from, and a stale entry would keep firing.
        c.UI.Bindings.Clear();
        foreach (var (action, slots) in _vm.Bindings)
            c.UI.Bindings[action] = slots.Copy();

        c.Library.UseLibraryWhenImporting = _vm.UseLibraryWhenImporting;
        c.Library.ShowFileExtensions = _vm.LibraryShowExtensions;
        // The objects home has no folder row here, so the rebuild below
        // must carry its configured path across — dropping it stranded
        // every entry save in a folder no tab scanned.
        // One root, reserved leaves: the homes and the auto-save folder
        // are named inside it, never chosen apart.
        string root = _vm.PoserRoot.Trim().Length == 0
            ? LibraryConfiguration.DefaultRoot
            : _vm.PoserRoot.Trim();
        _vm.PoseFolder = System.IO.Path.Combine(root, LibraryConfiguration.PosesLeaf);
        _vm.ObjectsFolder = System.IO.Path.Combine(root, LibraryConfiguration.ObjectsLeaf);
        _vm.SceneFolder = System.IO.Path.Combine(root, LibraryConfiguration.ScenesLeaf);
        _vm.McdfFolder = System.IO.Path.Combine(root, LibraryConfiguration.McdfLeaf);
        _vm.AutoSaveFolderDraft = System.IO.Path.Combine(root, LibraryConfiguration.AutoSavesLeaf);
        c.Library.Sources.Clear();
        c.Library.SetHomeRoot(
            LibraryConfiguration.PoseSourceName,
            LibraryConfiguration.DefaultPoseRoot,
            _vm.PoseFolder);
        c.Library.SetHomeRoot(
            LibraryConfiguration.ObjectsSourceName,
            LibraryConfiguration.DefaultObjectsRoot,
            _vm.ObjectsFolder);
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
        c.Library.EnsureHomeRootsExist();
        c.AutoSave.RootDirectory = _vm.AutoSaveFolderDraft.Trim().Length == 0
            ? _autoSave.RootDirectory
            : _vm.AutoSaveFolderDraft.Trim();
    }
    private static bool IsHomeSource(string name)
    {
        foreach (var (home, _) in LibraryConfiguration.Homes)
            if (string.Equals(name, home, StringComparison.Ordinal))
                return true;
        return false;
    }
}
