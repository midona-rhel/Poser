using Dalamud.Interface.Windowing;
using Poser.Config;
using Poser.Services;
using System;
using System.Collections.Generic;

namespace Poser.UI.Composition;

public sealed class UiWindowSet : IDisposable
{
    public WindowSystem System { get; } = new(PluginConstants.PluginName);
    public MainWindow Main { get; }
    public GizmoOverlayWindow GizmoOverlay { get; }
    public SkeletonOverlayWindow SkeletonOverlay { get; }
    public SettingsWindow Settings { get; }
    public SpawnBrowserWindow SpawnBrowser { get; }
    public SidebarPartWindow SidebarPart { get; }
    public InspectorPartWindow InspectorPart { get; }
    public ToolbarPartWindow ToolbarPart { get; }
    public LibraryWindow LibraryPart { get; }

    /// <summary>The PERF panel. Up exactly while its setting is on — the
    /// switch IS the window, so nothing else opens or closes it.</summary>
    public FrameProfilerWindow FrameProfilerPanel { get; }
    private readonly SkeletonOverlayPresentation _overlayPresentation;
    private readonly WorldAdoptionSource _worldAdoption;
    private readonly ConfigurationService _configService;
    private readonly IServiceProvider _services;
    // Requested state can wait for bounded icon warming.
    private bool _primaryOpenRequested;

    public bool IsPrimaryOpen => _primaryOpenRequested;


    private readonly ReferenceImageSession _referenceImages;

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
        SkeletonOverlay = skeletonOverlay;
        System.AddWindow(SkeletonOverlay);

        GizmoOverlay = gizmoOverlay;
        System.AddWindow(GizmoOverlay);

        Main = main;
        System.AddWindow(Main);

        SidebarPart = new SidebarPartWindow(main);
        System.AddWindow(SidebarPart);
        InspectorPart = new InspectorPartWindow(main);
        System.AddWindow(InspectorPart);
        LibraryPart = new LibraryWindow(main);
        System.AddWindow(LibraryPart);
        spawnBrowser.OnLibraryRequested = kind =>
        {
            Main.LibraryPane.SelectType(
                (int)PoseLibraryPane.LibraryType.Objects);
            Main.LibraryPane.SetOnlyKindFilter(kind);
            LibraryPart.IsOpen = true;
            LibraryPart.BringToFront();
        };
        Main.OnLibraryWindowRequested += () =>
        {
            LibraryPart.IsOpen = true;
            LibraryPart.BringToFront();
        };
        ToolbarPart = new ToolbarPartWindow(main);
        System.AddWindow(ToolbarPart);
        SidebarPart.OnReattach += ToggleDetached;
        InspectorPart.OnMerge += ToggleSplitInspector;
        Main.OnInspectorSplitToggleRequested += ToggleSplitInspector;
        ToolbarPart.OnReattach += ToggleDetached;
        Main.OnDetachToggleRequested += ToggleDetached;
        Main.GetSceneWindowOpen = () => SidebarPart.IsOpen;
        Main.OnSceneWindowToggleRequested += ToggleSceneWindow;
        Main.GetInspectorWindowOpen = () => InspectorPart.IsOpen;
        Main.OnInspectorWindowToggleRequested += ToggleInspectorWindow;

        Settings = settings;
        System.AddWindow(Settings);

        SpawnBrowser = spawnBrowser;
        System.AddWindow(SpawnBrowser);

        // Last in draw order, and deliberately: it reports on every window
        // registered above it, and a panel that drew first would be reporting
        // on a frame that had not happened yet.
        FrameProfilerPanel = new FrameProfilerWindow(configService);
        System.AddWindow(FrameProfilerPanel);


        _configService.OnConfigurationChanged += SyncSplitWindows;
        _configService.OnConfigurationChanged += SyncFrameProfiler;
        SyncFrameProfiler();

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
        _primaryOpenRequested = isOpen;
        if (isOpen)
            return;
        ApplyPrimaryOpen(isOpen);
    }

    public void AdvancePrimaryOpen(bool previewBackingReady)
    {
        if (_primaryOpenRequested
            && !Main.IsOpen
            && Crystarium.StartupIconsReady
            && previewBackingReady)
            ApplyPrimaryOpen(true);
    }

    // Standalone windows remain independent of primary readiness.
    private void ApplyPrimaryOpen(bool isOpen)
    {
        if (isOpen)
            _referenceImages.Restore();
        foreach (var window in _referenceWindows)
            window.IsOpen =
                isOpen && !ReferenceImageSession.IsHidden(window.Image);
        Main.IsOpen = isOpen;
        GizmoOverlay.IsOpen = isOpen;
        SkeletonOverlay.IsOpen = isOpen;
        SkeletonOverlay.UserVisible = isOpen;
        if (!isOpen)
        {
            _overlayPresentation.Clear();
            _worldAdoption.EndSession();
        }
        SyncSplitWindows();
    }

    private void SyncSplitWindows()
    {
        bool detached = Main.IsOpen && _configService.Config.UI.DetachedShell;
        SidebarPart.IsOpen = detached;
        InspectorPart.IsOpen =
            Main.IsOpen && _configService.Config.UI.SplitInspector;
        // The toolbar is ALWAYS its own window — merging windows never
        // merges the toolbar (the standard's shell roles).
        ToolbarPart.IsOpen = Main.IsOpen;
        if (!Main.IsOpen)
            LibraryPart.IsOpen = false;
        if (!detached)
            Main.ContentHidden = false;
    }

    public void ToggleSceneWindow() =>
        SidebarPart.IsOpen = !SidebarPart.IsOpen;

    /// <summary>Shows or hides the split inspector window without merging
    /// it; only meaningful while the inspector is split.</summary>
    private void ToggleInspectorWindow()
    {
        if (_configService.Config.UI.SplitInspector)
            InspectorPart.IsOpen = !InspectorPart.IsOpen;
    }

    /// <summary>The inspector's own split: the rail leaves the shell for
    /// its own window and comes back through the same toggle — the bar's
    /// merge, or the burger.</summary>
    private void ToggleSplitInspector()
    {
        var ui = _configService.Config.UI;
        ui.SplitInspector = !ui.SplitInspector;
        InspectorPart.IsOpen = Main.IsOpen && ui.SplitInspector;
        // The properties window sheds or regains the rail's width so the
        // split reads as a split, not a widening.
        Main.ApplyRailShift(ui.SplitInspector ? +1 : -1);
        if (ui.SplitInspector && !ui.DetachedWindowsRemember)
            InspectorPart.PlaceAt(
                Main.RailSeatScreen,
                new System.Numerics.Vector2(
                    global::Poser.UI.Views.AppShellView.RailWidth + 2f,
                    Main.LastHeight));
        _configService.ApplyChange();
    }

    private void ToggleDetached()
    {
        var ui = _configService.Config.UI;
        bool detaching = !ui.DetachedShell;
        ui.DetachedShell = detaching;
        if (detaching)
        {
            // Seated where it sat attached, unless the window is to open
            // where it was last: then ImGui's own memory of the window
            // stands. The toolbar is ALWAYS its own window with its own
            // remembered position — detaching the shell must not move it.
            if (!ui.DetachedWindowsRemember)
                SidebarPart.PlaceAt(
                    Main.LastPosition,
                    new System.Numerics.Vector2(
                        Main.LastSidebarWidth, Main.LastHeight));
            Main.ApplyDetachShift(+1);
        }
        else
        {
            Main.ApplyDetachShift(-1);
        }
        _configService.ApplyChange();
    }

    /// <summary>Reference images live for the GPose session: leaving it
    /// closes every one, roster included.</summary>
    public void CloseReferenceImages()
    {
        foreach (var image in global::System.Linq.Enumerable.ToArray(_referenceImages.Instances))
            _referenceImages.Close(image);
    }

    public void CloseAll()
    {
        SetPrimaryOpen(false);
        Settings.IsOpen = false;
    }

    public void PumpReferenceImages()
    {
        FlushDismissedReference();
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
        Main.OnDetachToggleRequested -= ToggleDetached;
        Main.OnSceneWindowToggleRequested -= ToggleSceneWindow;
        Main.OnInspectorWindowToggleRequested -= ToggleInspectorWindow;
        _overlayPresentation.Clear();
        System.RemoveAllWindows();
    }
}
