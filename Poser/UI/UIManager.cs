using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Entities;
using Poser.Services;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Poser.UI;

public class UIManager : IUIManager
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGPoseService _gPoseService;
    private readonly IEventBus _eventBus;
    private readonly WindowSystem _windowSystem;
    private readonly MainWindow _mainWindow;
    private readonly GizmoOverlayWindow _gizmoOverlay;
    private readonly SkeletonOverlayWindow _skeletonOverlay;
    private readonly HotbarWindow _hotbarWindow;

    // Services needed to create detached property windows
    private readonly ISelectionService _selectionService;
    private readonly IActorManager _actorManager;
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IAnimationService _animationService;
    private readonly IAnimationDataService _animationDataService;
    private readonly IHistoryService _historyService;
    private readonly IGazeService _gazeService;
    private readonly ICameraService _cameraService;

    // Track detached windows
    private readonly List<DetachedPropertiesWindow> _detachedWindows = new();

    public UIManager(
        IDalamudPluginInterface pluginInterface,
        IGPoseService gPoseService,
        IActorManager actorManager,
        ICameraService cameraService,
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IActorSpawnService spawnService,
        IHistoryService historyService,
        IPosingService posingService,
        IGazeService gazeService,
        ISkeletonService skeletonService,
        IBonePosingService bonePosingService,
        ISelectionService selectionService,
        IEditorState editorState,
        IEventBus eventBus,
        IKeyState keyState)
    {
        _pluginInterface = pluginInterface;
        _gPoseService = gPoseService;
        _eventBus = eventBus;
        _windowSystem = new WindowSystem(Poser.PluginName);

        // Store services for creating detached windows
        _selectionService = selectionService;
        _actorManager = actorManager;
        _posingService = posingService;
        _bonePosingService = bonePosingService;
        _animationService = animationService;
        _animationDataService = animationDataService;
        _historyService = historyService;
        _gazeService = gazeService;
        _cameraService = cameraService;

        // Create windows in z-order (last added = drawn on top)

        // Skeleton overlay (lowest z-order, underneath everything)
        _skeletonOverlay = new SkeletonOverlayWindow(actorManager, cameraService, skeletonService, bonePosingService, selectionService, editorState);
        _windowSystem.AddWindow(_skeletonOverlay);

        // Gizmo overlay (above skeleton overlay)
        _gizmoOverlay = new GizmoOverlayWindow(
            eventBus,
            selectionService,
            animationService,
            editorState,
            cameraService,
            posingService,
            bonePosingService);
        _windowSystem.AddWindow(_gizmoOverlay);

        // Hotbar (above gizmo)
        _hotbarWindow = new HotbarWindow(gPoseService, editorState);
        _windowSystem.AddWindow(_hotbarWindow);

        // Main sidebar (highest z-order, on top)
        _mainWindow = new MainWindow(
            gPoseService,
            actorManager,
            animationService,
            animationDataService,
            posingService,
            bonePosingService,
            spawnService,
            historyService,
            gazeService,
            skeletonService,
            cameraService,
            selectionService,
            editorState);
        _windowSystem.AddWindow(_mainWindow);

        // Subscribe to pop-out requests from properties panel
        _mainWindow.OnPropertiesPopOutRequested += CreateDetachedPropertiesWindow;

        // Hook into Dalamud's UI drawing
        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        // Enable drawing during GPose and cutscenes
        _pluginInterface.UiBuilder.DisableGposeUiHide = true;
        _pluginInterface.UiBuilder.DisableCutsceneUiHide = true;

        // Subscribe to GPose state changes to auto-open/close UI
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);

        // Windows closed by default, opens when entering GPose
        _mainWindow.IsOpen = false;
        _gizmoOverlay.IsOpen = false;
        _skeletonOverlay.IsOpen = false;
        _hotbarWindow.IsOpen = false;
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        // Open windows when entering GPose, close when exiting
        _mainWindow.IsOpen = e.IsGPosing;
        _gizmoOverlay.IsOpen = e.IsGPosing;
        _skeletonOverlay.IsOpen = e.IsGPosing;
        _hotbarWindow.IsOpen = e.IsGPosing;

        // Close all detached windows when leaving GPose
        if (!e.IsGPosing)
        {
            CloseAllDetachedWindows();
        }
    }

    private void CreateDetachedPropertiesWindow(IReadOnlyList<IEntity> entities)
    {
        var window = new DetachedPropertiesWindow(
            entities,
            _selectionService,
            _actorManager,
            _posingService,
            _bonePosingService,
            _animationService,
            _animationDataService,
            _historyService,
            _gazeService,
            _cameraService);

        window.OnCloseRequested += OnDetachedWindowCloseRequested;
        window.IsOpen = true;

        _detachedWindows.Add(window);
        _windowSystem.AddWindow(window);
    }

    private void OnDetachedWindowCloseRequested(DetachedPropertiesWindow window)
    {
        RemoveDetachedWindow(window);
    }

    private void RemoveDetachedWindow(DetachedPropertiesWindow window)
    {
        window.OnCloseRequested -= OnDetachedWindowCloseRequested;
        _windowSystem.RemoveWindow(window);
        _detachedWindows.Remove(window);
        window.Dispose();
    }

    private void CloseAllDetachedWindows()
    {
        // Create a copy to iterate since we're modifying the list
        var windowsToClose = _detachedWindows.ToArray();
        foreach (var window in windowsToClose)
        {
            RemoveDetachedWindow(window);
        }
    }

    private void DrawUI()
    {
        _windowSystem.Draw();
    }

    public void ToggleMainWindow()
    {
        _mainWindow.IsOpen = !_mainWindow.IsOpen;
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _mainWindow.OnPropertiesPopOutRequested -= CreateDetachedPropertiesWindow;
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;

        // Clean up detached windows
        CloseAllDetachedWindows();

        _windowSystem.RemoveAllWindows();
    }
}
