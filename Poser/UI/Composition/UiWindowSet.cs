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
    public ToolbarPartWindow ToolbarPart { get; }
    private readonly SkeletonOverlayPresentation _overlayPresentation;
    private readonly WorldAdoptionSource _worldAdoption;
    private readonly ConfigurationService _configService;
    private readonly IServiceProvider _services;
    // Requested state can wait for bounded icon warming.
    private bool _primaryOpenRequested;

    public bool IsPrimaryOpen => _primaryOpenRequested;

    private readonly List<PopOutWindow> _popOuts = new();
    private readonly List<PopOutWindow> _dismissedPopOuts = new();

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
        ToolbarPart = new ToolbarPartWindow(main);
        System.AddWindow(ToolbarPart);
        SidebarPart.OnReattach += ToggleDetached;
        ToolbarPart.OnReattach += ToggleDetached;
        Main.OnDetachToggleRequested += ToggleDetached;
        Main.GetSceneWindowOpen = () => SidebarPart.IsOpen;
        Main.OnSceneWindowToggleRequested += ToggleSceneWindow;

        Settings = settings;
        System.AddWindow(Settings);

        SpawnBrowser = spawnBrowser;
        System.AddWindow(SpawnBrowser);

        Main.OnPopOutRequested += CreatePopOut;

        _configService.OnConfigurationChanged += SyncSplitWindows;

        SetPrimaryOpen(
            gPoseService.IsGPosing && configService.Config.OpenOnGPoseEnter);
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
        ToolbarPart.IsOpen = detached;
        if (!detached)
            Main.ContentHidden = false;
    }

    public void ToggleSceneWindow() =>
        SidebarPart.IsOpen = !SidebarPart.IsOpen;

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

    public void CloseAll()
    {
        SetPrimaryOpen(false);
        Settings.IsOpen = false;
        foreach (var popOut in _popOuts.ToArray())
            popOut.IsOpen = false;
    }

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
        Main.OnPopOutRequested -= CreatePopOut;
        Main.OnDetachToggleRequested -= ToggleDetached;
        Main.OnSceneWindowToggleRequested -= ToggleSceneWindow;
        _overlayPresentation.Clear();
        System.RemoveAllWindows();
    }
}
