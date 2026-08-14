using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Config;
using Poser.Entities;
using Poser.Library;
using Poser.Services;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// Binder for <see cref="SettingsView"/> (view+binder pattern —
/// docs/architecture/ui-workspace.md): loads a <see cref="SettingsViewModel"/> from
/// <see cref="ConfigurationService"/> when opened, renders the pure view, and writes
/// back + saves on Save. Cancel/close discards.
/// </summary>
public class SettingsWindow : Window
{
    private SettingsViewModel _vm = new();
    private bool _saving;
    private readonly IAutoSaveService _autoSave;

    public SettingsWindow(IAutoSaveService autoSave)
        : base($"Settings###{PluginConstants.PluginName}_settings",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
        _autoSave = autoSave;
        // Settings closes through Cancel or the chrome's own X, both of which
        // discard deliberately. Escape belongs to the deselect chord, and an
        // Escape that silently threw away a page of edits read as a crash.
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
        // The view paints its own chassis (bg-app + border trio); the host window is
        // an undecorated, transparent shell that only supplies position + input.
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
            AccentIndex = c.UI.AccentIndex,

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

            UseLibraryWhenImporting = c.Library.UseLibraryWhenImporting,
            LibraryShowExtensions = c.Library.ShowFileExtensions,

            // The homes are drafts of the CONFIGURED value, not of the shipped
            // one: a user who never touched them sees the shipped path as the
            // field's placeholder and Save leaves it shipped.
            PoseFolder = c.Library.ResolvePoseRoot(),
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
                // A seeded source (Brio/Anamnesis defaults) may point at a
                // folder its own tool never created; Explorer errors on a
                // missing path, so create it — the library scans it from now
                // on anyway ("scanned once it exists").
                System.IO.Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // An unreachable path (bad drive letter, permissions) has no
                // surface here beyond doing nothing; the row's Status line
                // already shows the path itself.
            }
        };

        // Library sources: edited as copies, so Cancel leaves the configured
        // roots untouched. The Poser homes are NOT among them — they have
        // their own rows, and listing them twice would give one path two
        // editors that disagree.
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

        // Keybinds: the stored slots filled out to the whole registry and
        // COPIED, so Cancel leaves the live bindings untouched exactly as it
        // does the library roots.
        _vm.Bindings = KeybindRegistry.Resolve(c.UI.Bindings);
        _vm.BindingRevision++;
    }

    /// <summary>
    /// The confirmed reset: the config service replaces the slice, the theme
    /// is restated because a Display or whole reset may have changed it, and
    /// the view model is rebuilt from what is now stored. Rebuilding is the
    /// point — the page must not go on showing the values that were just
    /// thrown away, and unsaved edits are exactly what a reset discards.
    /// </summary>
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
        // The reset came from a page; that page stays on screen to show the
        // result rather than throwing the user back to Display.
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
        c.GPoseTargetChangesSelection = _vm.FollowGameTarget;
        c.SelectionChangesGPoseTarget = _vm.TargetFollowsSelection;
        // Clamped, not trusted: the slider is bounded but the stored value is
        // also what a hand-edited config file hands back.
        c.UndoDepth = Math.Clamp(_vm.UndoDepth, 0, 500);

        // The interval slider is a float row over integer config; the kept count
        // is free text, so it parses here and an unusable draft (empty, blank,
        // non-numeric, zero, overflowing int) leaves the stored value alone
        // rather than resetting the user's retention behind their back.
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
        // Same free-text contract as the pose count: an unusable draft leaves
        // the stored retention alone.
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
        // The friendly-name switch is a DISPLAY rule the bone tables answer,
        // and every entity that has already resolved its own name reads it
        // live — so publishing it here is the whole of applying it.
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
        c.UI.AccentIndex = _vm.AccentIndex;

        // Clamped like the undo depth, and for the same reason: the sliders
        // are bounded but a hand-edited file is not, and these seed every
        // camera created from now on.
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

        // Replaced whole, never merged: an action dropped from the registry
        // has no row to clear it from, and a stale entry would keep firing.
        c.UI.Bindings.Clear();
        foreach (var (action, slots) in _vm.Bindings)
            c.UI.Bindings[action] = slots.Copy();

        c.Library.UseLibraryWhenImporting = _vm.UseLibraryWhenImporting;
        c.Library.ShowFileExtensions = _vm.LibraryShowExtensions;
        c.Library.Sources.Clear();
        // The homes lead the rebuilt list — they seat first on the rail, and
        // SetHomeRoot appends the ones an empty list has none of. Blank drafts
        // land on the shipped path rather than on nothing.
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
        // The homes are CONFIGURED roots and the scan aborts on the first one
        // it cannot observe, so a freshly typed path exists before the config
        // change that re-roots the library reaches the scanner.
        c.Library.EnsureHomeRootsExist();

        // Read once at load by the auto-save service, so this is the stored
        // value the NEXT session starts on; the settings page says so.
        c.AutoSave.RootDirectory = _vm.AutoSaveFolderDraft.Trim().Length == 0
            ? _autoSave.RootDirectory
            : _vm.AutoSaveFolderDraft.Trim();

        _saving = true;
        ThemeSelection.Apply(c.UI.Theme, c.UI.AccentIndex);
        svc.ApplyChange();
        IsOpen = false;
    }

    /// <summary>Whether a source name is one of the Poser homes, which the
    /// extra-folders list neither shows nor writes.</summary>
    private static bool IsHomeSource(string name)
    {
        foreach (var (home, _) in LibraryConfiguration.Homes)
            if (string.Equals(name, home, StringComparison.Ordinal))
                return true;
        return false;
    }
}
