using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Keys;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Config;
using Poser.Core;
using Poser.Entities;
using Poser.IPC;
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
    private readonly EnvironmentWindow _environmentWindow;
    private readonly ReferenceImagesWindow? _referencesWindow;
    private readonly ReferenceImageOverlayWindow? _referenceOverlay;
    private readonly LibraryWindow? _libraryWindow;
    private readonly GraphicalBoneWindow _graphicalBoneWindow;

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
    private readonly ITextureProvider _textureProvider;
    private readonly ITimeService? _timeService;
    private readonly IWeatherService? _weatherService;
    private readonly ReferenceImageService? _referenceImageService;
    private readonly ILibraryService? _libraryService;
    private readonly IPoseFileService? _poseFileService;
    private readonly ConfigurationService? _configService;
    private readonly ILightingService? _lightingService;
    private readonly IVirtualCameraService? _virtualCameraService;
    private readonly IPenumbraService? _penumbraService;
    private readonly IGlamourerService? _glamourerService;
    private readonly ICustomizePlusService? _customizePlusService;

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
        IKeyState keyState,
        ITextureProvider textureProvider,
        ITimeService? timeService = null,
        IWeatherService? weatherService = null,
        ReferenceImageService? referenceImageService = null,
        ILibraryService? libraryService = null,
        IPoseFileService? poseFileService = null,
        ConfigurationService? configService = null,
        ILightingService? lightingService = null,
        IVirtualCameraService? virtualCameraService = null,
        IPenumbraService? penumbraService = null,
        IGlamourerService? glamourerService = null,
        ICustomizePlusService? customizePlusService = null)
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
        _textureProvider = textureProvider;
        _timeService = timeService;
        _weatherService = weatherService;
        _referenceImageService = referenceImageService;
        _libraryService = libraryService;
        _poseFileService = poseFileService;
        _configService = configService;
        _lightingService = lightingService;
        _virtualCameraService = virtualCameraService;
        _penumbraService = penumbraService;
        _glamourerService = glamourerService;
        _customizePlusService = customizePlusService;

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
        _hotbarWindow = new HotbarWindow(gPoseService, editorState, actorManager, skeletonService);
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
            editorState,
            textureProvider,
            lightingService,
            _virtualCameraService,
            penumbraService,
            glamourerService,
            customizePlusService);
        _windowSystem.AddWindow(_mainWindow);

        // Create environment window
        _environmentWindow = new EnvironmentWindow(timeService, weatherService);
        _windowSystem.AddWindow(_environmentWindow);

        // Create reference image windows
        if (referenceImageService != null)
        {
            _referenceOverlay = new ReferenceImageOverlayWindow(referenceImageService);
            _windowSystem.AddWindow(_referenceOverlay);

            _referencesWindow = new ReferenceImagesWindow(referenceImageService);
            _windowSystem.AddWindow(_referencesWindow);
        }

        // Create library window
        if (libraryService != null && poseFileService != null && configService != null)
        {
            _libraryWindow = new LibraryWindow(libraryService, poseFileService, selectionService, configService, textureProvider, eventBus);
            _windowSystem.AddWindow(_libraryWindow);
        }

        // Create graphical bone window
        _graphicalBoneWindow = new GraphicalBoneWindow(selectionService, actorManager, skeletonService, gPoseService, textureProvider);
        _windowSystem.AddWindow(_graphicalBoneWindow);

        // Subscribe to pop-out requests from properties panel
        _mainWindow.OnPropertiesPopOutRequested += CreateDetachedPropertiesWindow;

        // Subscribe to environment button click
        _mainWindow.OnEnvironmentRequested += ToggleEnvironmentWindow;

        // Subscribe to references button click
        _mainWindow.OnReferencesRequested += ToggleReferencesWindow;

        // Subscribe to library button click
        _mainWindow.OnLibraryRequested += ToggleLibraryWindow;

        // Subscribe to body map button click
        _mainWindow.OnBodyMapRequested += ToggleGraphicalBoneWindow;

        // Hook into Dalamud's UI drawing
        _pluginInterface.UiBuilder.Draw += DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi += ToggleMainWindow;

        // Enable drawing during GPose and cutscenes
        _pluginInterface.UiBuilder.DisableGposeUiHide = true;
        _pluginInterface.UiBuilder.DisableCutsceneUiHide = true;

        // Subscribe to GPose state changes to auto-open/close UI
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);

        // Check if already in GPose on startup
        if (_gPoseService.IsGPosing)
        {
            _mainWindow.IsOpen = true;
            _gizmoOverlay.IsOpen = true;
            _skeletonOverlay.IsOpen = true;
            _hotbarWindow.IsOpen = true;
        }
        else
        {
            // Windows closed by default, opens when entering GPose
            _mainWindow.IsOpen = false;
            _gizmoOverlay.IsOpen = false;
            _skeletonOverlay.IsOpen = false;
            _hotbarWindow.IsOpen = false;
        }

        // Environment window starts closed
        _environmentWindow.IsOpen = false;

        // Reference windows start closed
        if (_referencesWindow != null)
            _referencesWindow.IsOpen = false;
        if (_referenceOverlay != null)
            _referenceOverlay.IsOpen = false;

        // Library window starts closed
        if (_libraryWindow != null)
            _libraryWindow.IsOpen = false;

        // Graphical bone window starts closed
        _graphicalBoneWindow.IsOpen = false;
    }

    private void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        // Open windows when entering GPose, close when exiting
        _mainWindow.IsOpen = e.IsGPosing;
        _gizmoOverlay.IsOpen = e.IsGPosing;
        _skeletonOverlay.IsOpen = e.IsGPosing;
        _hotbarWindow.IsOpen = e.IsGPosing;

        // Close all detached windows and environment when leaving GPose
        if (!e.IsGPosing)
        {
            _environmentWindow.IsOpen = false;
            if (_referencesWindow != null)
                _referencesWindow.IsOpen = false;
            if (_referenceOverlay != null)
                _referenceOverlay.IsOpen = false;
            if (_libraryWindow != null)
                _libraryWindow.IsOpen = false;
            _graphicalBoneWindow.IsOpen = false;
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
            _cameraService,
            _textureProvider,
            _penumbraService,
            _glamourerService,
            _customizePlusService,
            _virtualCameraService,
            _lightingService);

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

    private void ToggleEnvironmentWindow()
    {
        _environmentWindow.IsOpen = !_environmentWindow.IsOpen;
    }

    private void ToggleReferencesWindow()
    {
        if (_referencesWindow != null)
        {
            _referencesWindow.IsOpen = !_referencesWindow.IsOpen;

            // Also toggle the overlay when opening/closing references window
            if (_referenceOverlay != null)
                _referenceOverlay.IsOpen = _referencesWindow.IsOpen;
        }
    }

    private void ToggleLibraryWindow()
    {
        if (_libraryWindow != null)
        {
            _libraryWindow.IsOpen = !_libraryWindow.IsOpen;
        }
    }

    private void ToggleGraphicalBoneWindow()
    {
        _graphicalBoneWindow.IsOpen = !_graphicalBoneWindow.IsOpen;
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        _mainWindow.OnPropertiesPopOutRequested -= CreateDetachedPropertiesWindow;
        _mainWindow.OnEnvironmentRequested -= ToggleEnvironmentWindow;
        _mainWindow.OnReferencesRequested -= ToggleReferencesWindow;
        _mainWindow.OnLibraryRequested -= ToggleLibraryWindow;
        _mainWindow.OnBodyMapRequested -= ToggleGraphicalBoneWindow;
        _pluginInterface.UiBuilder.Draw -= DrawUI;
        _pluginInterface.UiBuilder.OpenMainUi -= ToggleMainWindow;

        // Clean up detached windows
        CloseAllDetachedWindows();

        _environmentWindow.Dispose();
        _referencesWindow?.Dispose();
        _referenceOverlay?.Dispose();
        _libraryWindow?.Dispose();
        _graphicalBoneWindow.Dispose();
        _windowSystem.RemoveAllWindows();
    }
}
