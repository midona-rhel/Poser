using System;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Config;
using Poser.Library;
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

    public SettingsWindow()
        : base($"Settings###{PluginConstants.PluginName}_settings",
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoResize)
    {
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

            AutoSaveEnabled = c.AutoSave.Enabled,
            AutoSaveIntervalSeconds = c.AutoSave.IntervalSeconds,
            AutoSaveMaxKept = c.AutoSave.MaxAutoSaves.ToString(CultureInfo.InvariantCulture),
            AutoSaveCleanOnExit = c.AutoSave.CleanOnExit,

            BoneDotRadius = c.Skeleton.BoneDotRadius,
            OverlaySelected = ImGui.ColorConvertU32ToFloat4(c.Skeleton.SelectedBoneColor),
            OverlayHovered = ImGui.ColorConvertU32ToFloat4(c.Skeleton.HoveredBoneColor),
            OverlayInactive = ImGui.ColorConvertU32ToFloat4(c.Skeleton.BoneColor),
            OverlayIkChain = ImGui.ColorConvertU32ToFloat4(c.Skeleton.IkChainColor),
            OverlayMirrored = ImGui.ColorConvertU32ToFloat4(c.Skeleton.MirroredBoneColor),
            ShowSkeletonLines = c.Skeleton.ShowSkeletonLines,
            BoneLineThickness = c.Skeleton.BoneLineThickness,
            BoneLineOpacity = c.Skeleton.BoneLineOpacity,

            NsfwBones = c.Display.ShowNsfwBones,
            AnonymousMode = c.Display.AnonymousMode,
            Theme = c.UI.Theme,
            AccentIndex = c.UI.AccentIndex,

            SidebarDock = (int)c.UI.SidebarDock,
            InspectorDock = (int)c.UI.InspectorDock,
            TreeGuides = c.UI.ShowTreeGuides,

            UseLibraryWhenImporting = c.Library.UseLibraryWhenImporting,
            LibraryShowExtensions = c.Library.ShowFileExtensions,

            Version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev",
            OnSave = SaveToConfig,
            OnCancel = () => IsOpen = false,
            OnClose = () => IsOpen = false,
            OnThemePreview = ThemeSelection.Apply,
        };
        _vm.OnOpenRepository = () =>
            Process.Start(new ProcessStartInfo("https://github.com/midona-rhel/Poser") { UseShellExecute = true });

        // Library sources: edited as copies, so Cancel leaves the configured
        // roots untouched.
        foreach (var source in c.Library.Sources)
            _vm.LibrarySources.Add(new LibrarySourceVm
            {
                Name = source.Name,
                Path = source.Path,
                Enabled = source.Enabled,
            });

        // Keybinds: stored overrides on top of the view defaults.
        for (int i = 0; i < _vm.Keybinds.Length; i++)
            if (c.UI.Keybinds.TryGetValue(_vm.Keybinds[i].Action, out var bound))
                _vm.Keybinds[i] = (_vm.Keybinds[i].Action, bound);
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

        c.Skeleton.BoneDotRadius = _vm.BoneDotRadius;
        c.Skeleton.SelectedBoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlaySelected);
        c.Skeleton.HoveredBoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayHovered);
        c.Skeleton.BoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayInactive);
        c.Skeleton.IkChainColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayIkChain);
        c.Skeleton.MirroredBoneColor = ImGui.ColorConvertFloat4ToU32(_vm.OverlayMirrored);
        c.Skeleton.ShowSkeletonLines = _vm.ShowSkeletonLines;
        c.Skeleton.BoneLineThickness = _vm.BoneLineThickness;
        c.Skeleton.BoneLineOpacity = _vm.BoneLineOpacity;

        c.Display.ShowNsfwBones = _vm.NsfwBones;
        c.Display.AnonymousMode = _vm.AnonymousMode;
        c.UI.Theme = _vm.Theme;
        c.UI.AccentIndex = _vm.AccentIndex;

        c.UI.SidebarDock = (PanelDock)_vm.SidebarDock;
        c.UI.InspectorDock = (PanelDock)_vm.InspectorDock;
        c.UI.ShowTreeGuides = _vm.TreeGuides;

        foreach (var (action, binding) in _vm.Keybinds)
            c.UI.Keybinds[action] = binding;

        c.Library.UseLibraryWhenImporting = _vm.UseLibraryWhenImporting;
        c.Library.ShowFileExtensions = _vm.LibraryShowExtensions;
        c.Library.Sources.Clear();
        foreach (var source in _vm.LibrarySources)
        {
            string path = source.Path.Trim();
            string name = source.Name.Trim();
            if (path.Length == 0 && name.Length == 0)
                continue;
            c.Library.Sources.Add(new LibrarySourceConfig
            {
                Name = name,
                Path = path,
                Enabled = source.Enabled,
            });
        }

        _saving = true;
        ThemeSelection.Apply(c.UI.Theme, c.UI.AccentIndex);
        svc.ApplyChange();
        IsOpen = false;
    }
}
