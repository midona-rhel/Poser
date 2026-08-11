using Dalamud.Interface.Windowing;
using Poser.Config;
using Poser.Services;
using System;

namespace Poser.UI.Composition;

/// <summary>
/// Owns draw order for the focused posing workspace, settings, the spawn
/// browser, the split-shell part windows, and the two viewport interaction
/// canvases.
/// </summary>
public sealed class UiWindowSet : IDisposable
{
    public WindowSystem System { get; } = new(PluginConstants.PluginName);
    public MainWindow Main { get; }
    public GizmoOverlayWindow GizmoOverlay { get; }
    public SkeletonOverlayWindow SkeletonOverlay { get; }
    public SettingsWindow Settings { get; }
    public SpawnBrowserWindow SpawnBrowser { get; }
    public SidebarPartWindow SidebarPart { get; }
    public ToolbarPartWindow ToolbarPart { get; }
    public InspectorPartWindow InspectorPart { get; }
    private readonly SkeletonOverlayPresentation _overlayPresentation;
    private readonly ConfigurationService _configService;

    public UiWindowSet(
        IGPoseService gPoseService,
        ConfigurationService configService,
        MainWindow main,
        SkeletonOverlayWindow skeletonOverlay,
        GizmoOverlayWindow gizmoOverlay,
        SettingsWindow settings,
        SpawnBrowserWindow spawnBrowser,
        SkeletonOverlayPresentation overlayPresentation)
    {
        _overlayPresentation = overlayPresentation;
        _configService = configService;
        // Draw order is intentional: overlays first, normal windows after them.
        SkeletonOverlay = skeletonOverlay;
        System.AddWindow(SkeletonOverlay);

        GizmoOverlay = gizmoOverlay;
        System.AddWindow(GizmoOverlay);

        Main = main;
        System.AddWindow(Main);

        // The split parts draw MainWindow's per-frame view model, so they are
        // registered — and therefore drawn — after it.
        SidebarPart = new SidebarPartWindow(main);
        System.AddWindow(SidebarPart);
        ToolbarPart = new ToolbarPartWindow(main);
        System.AddWindow(ToolbarPart);
        InspectorPart = new InspectorPartWindow(main);
        System.AddWindow(InspectorPart);
        SidebarPart.OnReattach += () => MainWindow.ToggleSplit(ShellPart.Sidebar);
        ToolbarPart.OnReattach += () => MainWindow.ToggleSplit(ShellPart.Toolbar);
        InspectorPart.OnReattach +=
            () => MainWindow.ToggleSplit(ShellPart.Inspector);

        Settings = settings;
        System.AddWindow(Settings);

        SpawnBrowser = spawnBrowser;
        System.AddWindow(SpawnBrowser);

        Main.GetSkeletonOverlayOn = () => SkeletonOverlay.UserVisible;
        Main.OnSkeletonOverlayToggled += SetSkeletonOverlayOpen;

        // Split flags change through ApplyChange (the burger menu, the
        // settings page), and this is the one sync point that turns them
        // into open part windows.
        _configService.OnConfigurationChanged += SyncSplitWindows;

        // Loading mid-GPose obeys the same rule as entering it: the workspace
        // only appears when the user asked for it to.
        SetPrimaryOpen(
            gPoseService.IsGPosing && configService.Config.OpenOnGPoseEnter);
    }

    public void SetPrimaryOpen(bool isOpen)
    {
        Main.IsOpen = isOpen;
        GizmoOverlay.IsOpen = isOpen;
        // The window itself follows the session like the gizmo overlay; the
        // Armature toggle starts Off each GPose/UI session and a bone
        // selection forces the armature visible regardless of the toggle.
        // Session end resets the toggle so the next session starts Off.
        SkeletonOverlay.IsOpen = isOpen;
        if (!isOpen)
        {
            SkeletonOverlay.UserVisible = false;
            _overlayPresentation.Clear();
        }
        SyncSplitWindows();
    }

    /// <summary>A part window is open exactly while its split flag is set and
    /// the workspace itself is up — parts are pieces of the main window, not
    /// windows of their own standing.</summary>
    private void SyncSplitWindows()
    {
        var ui = _configService.Config.UI;
        SidebarPart.IsOpen = Main.IsOpen && ui.SplitSidebar;
        ToolbarPart.IsOpen = Main.IsOpen && ui.SplitToolbar;
        InspectorPart.IsOpen = Main.IsOpen && ui.SplitInspector;
    }

    /// <summary>Every surface down, settings included. Only the
    /// Close-with-GPose path wants this; manual toggles do not.</summary>
    public void CloseAll()
    {
        SetPrimaryOpen(false);
        Settings.IsOpen = false;
    }

    private void SetSkeletonOverlayOpen(bool isOpen)
        => SkeletonOverlay.UserVisible = isOpen;

    public void Dispose()
    {
        _configService.OnConfigurationChanged -= SyncSplitWindows;
        Main.OnSkeletonOverlayToggled -= SetSkeletonOverlayOpen;
        _overlayPresentation.Clear();
        System.RemoveAllWindows();
    }
}
