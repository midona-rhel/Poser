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
        SidebarPart.OnReattach += ToggleDetached;
        ToolbarPart.OnReattach += ToggleDetached;
        Main.OnDetachToggleRequested += ToggleDetached;

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

    /// <summary>A part window is open exactly while detached mode is on and
    /// the workspace itself is up — parts are pieces of the main window, not
    /// windows of their own standing.</summary>
    private void SyncSplitWindows()
    {
        bool detached = Main.IsOpen && _configService.Config.UI.DetachedShell;
        SidebarPart.IsOpen = detached;
        ToolbarPart.IsOpen = detached;
    }

    /// <summary>THE layout toggle. Detaching seats the sidebar window where
    /// the sidebar column stood and the toolbar strip above the old
    /// titlebar; the main window sheds the column in the same frame, so the
    /// content and the inspector never move. Merging reverses it.</summary>
    private void ToggleDetached()
    {
        var ui = _configService.Config.UI;
        bool detaching = !ui.DetachedShell;
        ui.DetachedShell = detaching;
        if (detaching)
        {
            float gs = Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale;
            SidebarPart.PlaceAt(
                Main.LastPosition,
                new System.Numerics.Vector2(
                    Main.LastSidebarWidth, Main.LastHeight));
            ToolbarPart.PlaceAt(new System.Numerics.Vector2(
                Main.LastPosition.X,
                MathF.Max(
                    0f,
                    Main.LastPosition.Y
                        - (Views.AppShellView.CollapsedBarHeight + 8f) * gs)));
            Main.ApplyDetachShift(+1);
        }
        else
        {
            Main.ApplyDetachShift(-1);
        }
        _configService.ApplyChange();
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
        Main.OnDetachToggleRequested -= ToggleDetached;
        _overlayPresentation.Clear();
        System.RemoveAllWindows();
    }
}
