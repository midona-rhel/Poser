using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Poser.Core;
using Poser.Services;
using System;

namespace Poser.UI;

public class UIManager : IUIManager
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGPoseService _gPoseService;
    private readonly WindowSystem _windowSystem;
    private readonly MainWindow _mainWindow;
    private readonly GizmoOverlayWindow _gizmoOverlay;
    private readonly SkeletonOverlayWindow _skeletonOverlay;
    private readonly HotbarWindow _hotbarWindow;

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
        IEditorState editorState,
        IEventBus eventBus)
    {
        _pluginInterface = pluginInterface;
        _gPoseService = gPoseService;
        _windowSystem = new WindowSystem(Poser.PluginName);

        // Create windows in z-order (last added = drawn on top)

        // Skeleton overlay (lowest z-order, underneath everything)
        _skeletonOverlay = new SkeletonOverlayWindow(actorManager, cameraService, skeletonService);
        _windowSystem.AddWindow(_skeletonOverlay);

        // Gizmo overlay (above skeleton overlay)
        _gizmoOverlay = new GizmoOverlayWindow(actorManager, cameraService, posingService, historyService, animationService, editorState);
        _windowSystem.AddWindow(_gizmoOverlay);

        // Hotbar (above gizmo)
        _hotbarWindow = new HotbarWindow(gPoseService, editorState);
        _windowSystem.AddWindow(_hotbarWindow);

        // Main sidebar (highest z-order, on top)
        _mainWindow = new MainWindow(gPoseService, actorManager, animationService, animationDataService, spawnService, historyService, cameraService, posingService, gazeService, skeletonService, editorState, eventBus);
        _windowSystem.AddWindow(_mainWindow);

        // Hook into Dalamud's UI drawing
        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        // Enable drawing during GPose and cutscenes
        _pluginInterface.UiBuilder.DisableGposeUiHide = true;
        _pluginInterface.UiBuilder.DisableCutsceneUiHide = true;

        // Subscribe to GPose state changes to auto-open/close UI
        _gPoseService.OnGPoseStateChanged += OnGPoseStateChanged;

        // Windows closed by default, opens when entering GPose
        _mainWindow.IsOpen = false;
        _gizmoOverlay.IsOpen = false;
        _skeletonOverlay.IsOpen = false;
        _hotbarWindow.IsOpen = false;
    }

    private void OnGPoseStateChanged(bool isGPosing)
    {
        // Open windows when entering GPose, close when exiting
        _mainWindow.IsOpen = isGPosing;
        _gizmoOverlay.IsOpen = isGPosing;
        _skeletonOverlay.IsOpen = isGPosing;
        _hotbarWindow.IsOpen = isGPosing;
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
        _gPoseService.OnGPoseStateChanged -= OnGPoseStateChanged;
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;

        _windowSystem.RemoveAllWindows();
    }
}
