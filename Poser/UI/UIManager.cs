using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Core;
using Poser.Services;
using System;

namespace Poser.UI;

public class UIManager : IUIManager
{
    private readonly IDalamudPluginInterface _pluginInterface;
    private readonly IGPoseService _gPoseService;
    private readonly IEditorState _editorState;
    private readonly IEventBus _eventBus;
    private readonly IKeyState _keyState;
    private readonly WindowSystem _windowSystem;
    private readonly MainWindow _mainWindow;
    private readonly GizmoOverlayWindow _gizmoOverlay;
    private readonly SkeletonOverlayWindow _skeletonOverlay;
    private readonly HotbarWindow _hotbarWindow;

    // Track E key state to detect press edge
    private bool _wasEPressed;

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
        _editorState = editorState;
        _eventBus = eventBus;
        _keyState = keyState;
        _windowSystem = new WindowSystem(Poser.PluginName);

        // Create windows in z-order (last added = drawn on top)

        // Skeleton overlay (lowest z-order, underneath everything)
        _skeletonOverlay = new SkeletonOverlayWindow(actorManager, cameraService, skeletonService, bonePosingService, selectionService);
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
            selectionService,
            editorState);
        _windowSystem.AddWindow(_mainWindow);

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
    }

    private void DrawUI()
    {
        // Handle E key for edit mode toggle (only in GPose)
        if (_gPoseService.IsGPosing)
        {
            HandleEditModeKey();
        }

        _windowSystem.Draw();
    }

    private void HandleEditModeKey()
    {
        // Check E key state using IKeyState (allows us to consume the key)
        bool isEPressed = _keyState[VirtualKey.E];

        // Detect rising edge (key just pressed)
        if (isEPressed && !_wasEPressed)
        {
            // Toggle posing mode
            _editorState.TogglePosingMode();

            // Consume the key to prevent game from receiving it
            _keyState[VirtualKey.E] = false;
        }

        _wasEPressed = isEPressed;
    }

    public void ToggleMainWindow()
    {
        _mainWindow.IsOpen = !_mainWindow.IsOpen;
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;

        _windowSystem.RemoveAllWindows();
    }
}
