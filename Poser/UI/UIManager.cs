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
    private readonly HotbarWindow _hotbarWindow;

    public UIManager(
        IDalamudPluginInterface pluginInterface,
        IGPoseService gPoseService,
        IActorManager actorManager,
        ICameraService cameraService,
        IAnimationService animationService,
        IHistoryService historyService,
        IPosingService posingService,
        IGazeService gazeService,
        IEditorState editorState,
        EventBus eventBus)
    {
        _pluginInterface = pluginInterface;
        _gPoseService = gPoseService;
        _windowSystem = new WindowSystem(Poser.PluginName);

        // Create windows in z-order (first added = drawn first = underneath)

        // Hotbar at bottom (lowest priority)
        _hotbarWindow = new HotbarWindow(gPoseService, editorState);
        _windowSystem.AddWindow(_hotbarWindow);

        // Gizmo overlay
        _gizmoOverlay = new GizmoOverlayWindow(actorManager, cameraService, posingService, historyService, animationService, editorState);
        _windowSystem.AddWindow(_gizmoOverlay);

        // Main sidebar (highest priority, on top)
        _mainWindow = new MainWindow(gPoseService, actorManager, animationService, historyService, posingService, gazeService, eventBus);
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
        _hotbarWindow.IsOpen = false;
    }

    private void OnGPoseStateChanged(bool isGPosing)
    {
        // Open windows when entering GPose, close when exiting
        _mainWindow.IsOpen = isGPosing;
        _gizmoOverlay.IsOpen = isGPosing;
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
