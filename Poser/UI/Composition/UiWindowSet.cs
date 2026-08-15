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

    /// <summary>The PERF panel. Up exactly while its setting is on — the
    /// switch IS the window, so nothing else opens or closes it.</summary>
    public FrameProfilerWindow FrameProfilerPanel { get; }
    private readonly SkeletonOverlayPresentation _overlayPresentation;
    private readonly WorldAdoptionSource _worldAdoption;
    private readonly ConfigurationService _configService;
    private readonly IServiceProvider _services;

    /// <summary>The living pop-outs, plus the ones dismissed mid-draw that
    /// still await removal — a window cannot leave the window system while
    /// the system is iterating it.</summary>
    private readonly List<PopOutWindow> _popOuts = new();
    private readonly List<PopOutWindow> _dismissedPopOuts = new();

    private readonly ReferenceImageSession _referenceImages;

    /// <summary>One window per reference picture, plus the ones closed
    /// mid-draw that still await removal — the same deferral the pop-outs
    /// need, for the same reason.</summary>
    private readonly List<ReferenceImageWindow> _referenceWindows = new();
    private readonly List<ReferenceImageWindow> _dismissedReference = new();

    public UiWindowSet(
        IGPoseService gPoseService,
        ConfigurationService configService,
        IServiceProvider services,
        MainWindow main,
        SkeletonOverlayWindow skeletonOverlay,
        GizmoOverlayWindow gizmoOverlay,
        SettingsWindow settings,
        SpawnBrowserWindow spawnBrowser,
        SkeletonOverlayPresentation overlayPresentation,
        WorldAdoptionSource worldAdoption,
        ReferenceImageSession referenceImages)
    {
        _referenceImages = referenceImages;
        _referenceImages.OnAdded += AddReferenceWindow;
        _referenceImages.OnRemoved += DismissReferenceWindow;
        _overlayPresentation = overlayPresentation;
        _worldAdoption = worldAdoption;
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
        // The strip's window roster: Scene closes and reopens from there.
        Main.GetSceneWindowOpen = () => SidebarPart.IsOpen;
        Main.OnSceneWindowToggleRequested += ToggleSceneWindow;

        Settings = settings;
        System.AddWindow(Settings);

        SpawnBrowser = spawnBrowser;
        System.AddWindow(SpawnBrowser);

        // Last in draw order, and deliberately: it reports on every window
        // registered above it, and a panel that drew first would be reporting
        // on a frame that had not happened yet.
        FrameProfilerPanel = new FrameProfilerWindow(configService);
        System.AddWindow(FrameProfilerPanel);

        Main.OnPopOutRequested += CreatePopOut;

        // Split flags change through ApplyChange (the burger menu, the
        // settings page), and this is the one sync point that turns them
        // into open part windows.
        _configService.OnConfigurationChanged += SyncSplitWindows;
        _configService.OnConfigurationChanged += SyncFrameProfiler;
        SyncFrameProfiler();

        // Loading mid-GPose obeys the same rule as entering it: the workspace
        // only appears when the user asked for it to.
        SetPrimaryOpen(
            gPoseService.IsGPosing && configService.Config.OpenOnGPoseEnter);
    }

    /// <summary>The profiler's panel and its recording are ONE state, and the
    /// setting is that state. Nothing else may flip either half — a panel that
    /// could be closed while the scopes kept measuring would leave a session
    /// paying for a tool nobody is reading.</summary>
    private void SyncFrameProfiler()
    {
        bool showing = _configService.Config.UI.ShowFrameProfiler;
        FrameProfilerPanel.IsOpen = showing;
        FrameProfiler.SetEnabled(showing);
    }

    public void SetPrimaryOpen(bool isOpen)
    {
        // The stored roster is rebuilt the first time the workspace is up —
        // Ktisis rebuilds its own at scene setup. Restoring here rather than
        // at construction keeps a refusal for a picture whose file has gone
        // from firing before there is a session to see it in.
        if (isOpen)
            _referenceImages.Restore();
        foreach (var window in _referenceWindows)
            window.IsOpen =
                isOpen && !ReferenceImageSession.IsHidden(window.Image);
        Main.IsOpen = isOpen;
        GizmoOverlay.IsOpen = isOpen;
        // The window itself follows the session like the gizmo overlay, and a
        // bone selection forces the armature visible regardless of the toggle.
        SkeletonOverlay.IsOpen = isOpen;
        // The master switch starts ON for each session — Ktisis ships
        // Overlay.Visible = true — so the sidebar's eyes drive the armature
        // exactly as they always have and the switch is the way to take the
        // whole thing away. Session end puts it back where it started.
        SkeletonOverlay.UserVisible = isOpen;
        if (!isOpen)
        {
            _overlayPresentation.Clear();
            // The adoption layer is session state for the same reason the
            // Armature toggle is: the next session starts with the world
            // unmarked.
            _worldAdoption.EndSession();
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
        if (!detached)
            Main.ContentHidden = false;
    }

    /// <summary>Public for the keybind: the strip's roster button and the
    /// chord are the same act, so they go through the same call.</summary>
    public void ToggleSceneWindow() =>
        SidebarPart.IsOpen = !SidebarPart.IsOpen;

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

    /// <summary>
    /// One frame of reference-image housekeeping, run AFTER the window system
    /// has drawn: a picture closed from its own title bar leaves the system
    /// here, outside the draw pass, and the add dialog is pumped from the UI
    /// root rather than from a window — the surface that opens it (the spawn
    /// browser) closes on focus loss, and a dialog pumped from a closed window
    /// is a dead dialog.
    /// </summary>
    public void PumpReferenceImages()
    {
        FlushDismissedReference();
        // The session owns "set aside", so the windows follow it here rather
        // than the sidebar reaching across to a window it does not own. One
        // read per picture per frame, against a bool the row already restates.
        if (Main.IsOpen)
            foreach (var window in _referenceWindows)
                window.IsOpen = !ReferenceImageSession.IsHidden(window.Image);
        _referenceImages.Tick();
        _referenceImages.DrawDialogs();
    }

    private void AddReferenceWindow(ReferenceImageInstance image)
    {
        FlushDismissedReference();
        var window = new ReferenceImageWindow(_referenceImages, image)
        {
            // A picture added while the workspace is up appears at once; one
            // restored before it opens waits for SetPrimaryOpen. A picture the
            // sidebar eye had set aside stays aside through both.
            IsOpen = Main.IsOpen && !ReferenceImageSession.IsHidden(image),
        };
        _referenceWindows.Add(window);
        System.AddWindow(window);
    }

    private void DismissReferenceWindow(ReferenceImageInstance image)
    {
        foreach (var window in _referenceWindows)
            if (window.Image == image)
            {
                window.IsOpen = false;
                _dismissedReference.Add(window);
            }
    }

    private void FlushDismissedReference()
    {
        if (_dismissedReference.Count == 0)
            return;
        foreach (var window in _dismissedReference)
        {
            _referenceWindows.Remove(window);
            System.RemoveWindow(window);
        }
        _dismissedReference.Clear();
    }

    public void Dispose()
    {
        _referenceImages.OnAdded -= AddReferenceWindow;
        _referenceImages.OnRemoved -= DismissReferenceWindow;
        _referenceWindows.Clear();
        _dismissedReference.Clear();
        _configService.OnConfigurationChanged -= SyncSplitWindows;
        _configService.OnConfigurationChanged -= SyncFrameProfiler;
        Main.OnPopOutRequested -= CreatePopOut;
        Main.OnDetachToggleRequested -= ToggleDetached;
        Main.OnSceneWindowToggleRequested -= ToggleSceneWindow;
        _overlayPresentation.Clear();
        System.RemoveAllWindows();
    }
}
