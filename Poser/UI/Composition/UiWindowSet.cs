using Dalamud.Interface.Windowing;
using Poser.Config;
using Poser.Services;
using System;
using System.Collections.Generic;

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
    private readonly IServiceProvider _services;

    /// <summary>The living pop-outs, plus the ones dismissed mid-draw that
    /// still await removal — a window cannot leave the window system while
    /// the system is iterating it.</summary>
    private readonly List<PopOutWindow> _popOuts = new();
    private readonly List<PopOutWindow> _dismissedPopOuts = new();

    public UiWindowSet(
        IGPoseService gPoseService,
        ConfigurationService configService,
        IServiceProvider services,
        MainWindow main,
        SkeletonOverlayWindow skeletonOverlay,
        GizmoOverlayWindow gizmoOverlay,
        SettingsWindow settings,
        SpawnBrowserWindow spawnBrowser,
        SkeletonOverlayPresentation overlayPresentation)
    {
        _overlayPresentation = overlayPresentation;
        _configService = configService;
        _services = services;
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
        Main.OnPopOutRequested += CreatePopOut;

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
        // Closing dismisses them (their OnClose), and the removal itself
        // waits out the draw pass.
        foreach (var popOut in _popOuts.ToArray())
            popOut.IsOpen = false;
    }

    /// <summary>Mints the frozen content window for one actor. Windows the
    /// user dismissed earlier leave the system here, outside its draw pass.
    /// </summary>
    private void CreatePopOut(Domain.Identity.ActorId actor)
    {
        FlushDismissed();
        var window = PopOutWindow.Create(_services, Main, actor);
        window.OnDismissed += dismissed => _dismissedPopOuts.Add(dismissed);
        _popOuts.Add(window);
        System.AddWindow(window);
    }

    private void FlushDismissed()
    {
        foreach (var window in _dismissedPopOuts)
        {
            _popOuts.Remove(window);
            System.RemoveWindow(window);
        }
        _dismissedPopOuts.Clear();
    }

    private void SetSkeletonOverlayOpen(bool isOpen)
        => SkeletonOverlay.UserVisible = isOpen;

    public void Dispose()
    {
        _configService.OnConfigurationChanged -= SyncSplitWindows;
        Main.OnSkeletonOverlayToggled -= SetSkeletonOverlayOpen;
        Main.OnPopOutRequested -= CreatePopOut;
        _overlayPresentation.Clear();
        System.RemoveAllWindows();
    }
}
