using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Config;
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
    private LiveSnapshot _snapshot;

    /// <summary>
    /// The settings the running UI reads live, as they stood before the window
    /// opened. Cancel/close restores exactly these; every other field on the
    /// page is save-only and never reaches the running config.
    /// </summary>
    private readonly record struct LiveSnapshot(
        float BoneDotRadius,
        uint SelectedBoneColor,
        uint HoveredBoneColor,
        uint BoneColor,
        uint IkChainColor,
        uint MirroredBoneColor,
        bool ShowSkeletonLines,
        float BoneLineThickness,
        float BoneLineOpacity,
        bool ShowTreeGuides,
        UITheme Theme,
        int AccentIndex);

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
        _snapshot = Capture();
        LoadFromConfig();
    }

    public override void OnClose()
    {
        if (!_saving)
            Restore();
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

        StageLive();
    }

    private static LiveSnapshot Capture()
    {
        var c = ConfigurationService.Instance.Config;
        return new LiveSnapshot(
            c.Skeleton.BoneDotRadius,
            c.Skeleton.SelectedBoneColor,
            c.Skeleton.HoveredBoneColor,
            c.Skeleton.BoneColor,
            c.Skeleton.IkChainColor,
            c.Skeleton.MirroredBoneColor,
            c.Skeleton.ShowSkeletonLines,
            c.Skeleton.BoneLineThickness,
            c.Skeleton.BoneLineOpacity,
            c.UI.ShowTreeGuides,
            c.UI.Theme,
            c.UI.AccentIndex);
    }

    /// <summary>
    /// Copies the live-readable edits into the running config every frame the
    /// window is up, so overlay colors, dot size, line width/opacity and the
    /// tree guides answer the controls immediately. Nothing is persisted —
    /// Save writes and saves, Cancel restores <see cref="_snapshot"/>.
    /// </summary>
    private void StageLive()
    {
        var c = ConfigurationService.Instance.Config;
        c.Skeleton.BoneDotRadius = _vm.BoneDotRadius;
        c.Skeleton.SelectedBoneColor =
            ImGui.ColorConvertFloat4ToU32(_vm.OverlaySelected);
        c.Skeleton.HoveredBoneColor =
            ImGui.ColorConvertFloat4ToU32(_vm.OverlayHovered);
        c.Skeleton.BoneColor =
            ImGui.ColorConvertFloat4ToU32(_vm.OverlayInactive);
        c.Skeleton.IkChainColor =
            ImGui.ColorConvertFloat4ToU32(_vm.OverlayIkChain);
        c.Skeleton.MirroredBoneColor =
            ImGui.ColorConvertFloat4ToU32(_vm.OverlayMirrored);
        c.Skeleton.ShowSkeletonLines = _vm.ShowSkeletonLines;
        c.Skeleton.BoneLineThickness = _vm.BoneLineThickness;
        c.Skeleton.BoneLineOpacity = _vm.BoneLineOpacity;
        c.UI.ShowTreeGuides = _vm.TreeGuides;
    }

    private void Restore()
    {
        var c = ConfigurationService.Instance.Config;
        c.Skeleton.BoneDotRadius = _snapshot.BoneDotRadius;
        c.Skeleton.SelectedBoneColor = _snapshot.SelectedBoneColor;
        c.Skeleton.HoveredBoneColor = _snapshot.HoveredBoneColor;
        c.Skeleton.BoneColor = _snapshot.BoneColor;
        c.Skeleton.IkChainColor = _snapshot.IkChainColor;
        c.Skeleton.MirroredBoneColor = _snapshot.MirroredBoneColor;
        c.Skeleton.ShowSkeletonLines = _snapshot.ShowSkeletonLines;
        c.Skeleton.BoneLineThickness = _snapshot.BoneLineThickness;
        c.Skeleton.BoneLineOpacity = _snapshot.BoneLineOpacity;
        c.UI.ShowTreeGuides = _snapshot.ShowTreeGuides;
        c.UI.Theme = _snapshot.Theme;
        c.UI.AccentIndex = _snapshot.AccentIndex;
        // The preview painted Crystarium directly, so the config values are the
        // only truth left to repaint from.
        ThemeSelection.Apply(c.UI.Theme, c.UI.AccentIndex);
    }

    private void LoadFromConfig()
    {
        var c = ConfigurationService.Instance.Config;
        _vm = new SettingsViewModel
        {
            Category = 1,
            OpenOnGPose = c.OpenOnGPoseEnter,
            CloseWithGPose = c.CloseWithGPose,

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

            Version = typeof(SettingsWindow).Assembly.GetName().Version?.ToString(3) ?? "dev",
            OnSave = SaveToConfig,
            OnCancel = () => IsOpen = false,
            OnClose = () => IsOpen = false,
            OnAppearancePreview = () =>
                ThemeSelection.Apply(_vm.Theme, _vm.AccentIndex),
        };
        _vm.OnOpenRepository = () =>
            Process.Start(new ProcessStartInfo("https://github.com/midona-rhel/Poser") { UseShellExecute = true });

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

        _saving = true;
        ThemeSelection.Apply(c.UI.Theme, c.UI.AccentIndex);
        svc.ApplyChange();
        IsOpen = false;
    }
}
