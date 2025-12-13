using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Poser.Entities;
using Poser.IPC;
using Poser.Services;
using Poser.UI.Components;
using Poser.UI.Controls;

namespace Poser.UI;

public class MainWindow : Window
{
    private const float DefaultWidth = 560f;
    private const float DefaultHeight = 800f;
    private const float MinWidth = 500f;
    private const float MinHeight = 400f;
    private const float SplitterHeight = 8f;
    private const float MinPanelHeight = 100f;

    private readonly IGPoseService _gPoseService;

    // UI Components
    private readonly TopBar _topBar;
    private readonly ScenePanel _scenePanel;
    private readonly PropertiesPanel _propertiesPanel;

    // Splitter state - scene has fixed height, properties takes remaining space
    private float _sceneHeight = 300f;
    private bool _isDraggingSplitter;
    private float _dragStartMouseY;
    private float _dragStartSceneHeight;

    /// <summary>
    /// Event fired when user requests to pop out the properties panel.
    /// </summary>
    public event Action<IReadOnlyList<IEntity>>? OnPropertiesPopOutRequested;

    /// <summary>
    /// Event fired when user clicks the Environment button.
    /// </summary>
    public event Action? OnEnvironmentRequested;

    /// <summary>
    /// Event fired when user clicks the References button.
    /// </summary>
    public event Action? OnReferencesRequested;

    /// <summary>
    /// Event fired when user clicks the Library button.
    /// </summary>
    public event Action? OnLibraryRequested;

    /// <summary>
    /// Event fired when user clicks the Body Map button.
    /// </summary>
    public event Action? OnBodyMapRequested;

    public MainWindow(
        IGPoseService gPoseService,
        IActorManager actorManager,
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IPosingService posingService,
        IBonePosingService bonePosingService,
        IActorSpawnService spawnService,
        IHistoryService historyService,
        IGazeService gazeService,
        ISkeletonService skeletonService,
        ICameraService cameraService,
        ISelectionService selectionService,
        IEditorState editorState,
        ITextureProvider textureProvider,
        ILightingService? lightingService = null,
        IPenumbraService? penumbraService = null,
        IGlamourerService? glamourerService = null,
        ICustomizePlusService? customizePlusService = null)
        : base($"{Poser.PluginName}###poser_main_window",
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        Size = new Vector2(DefaultWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;

        _gPoseService = gPoseService;

        // Initialize components with their required services
        _topBar = new TopBar(gPoseService, editorState, historyService);

        _scenePanel = new ScenePanel(
            actorManager,
            selectionService,
            animationService,
            skeletonService,
            gPoseService,
            editorState,
            spawnService,
            cameraService,
            lightingService);

        _propertiesPanel = new PropertiesPanel(
            selectionService,
            actorManager,
            posingService,
            bonePosingService,
            animationService,
            animationDataService,
            historyService,
            gazeService,
            cameraService,
            textureProvider,
            penumbraService,
            glamourerService,
            customizePlusService);

        // Forward pop-out requests
        _propertiesPanel.OnPopOutRequested += entities => OnPropertiesPopOutRequested?.Invoke(entities);

        // Forward environment button requests
        _topBar.OnEnvironmentRequested += () => OnEnvironmentRequested?.Invoke();

        // Forward references button requests
        _topBar.OnReferencesRequested += () => OnReferencesRequested?.Invoke();

        // Forward library button requests
        _topBar.OnLibraryRequested += () => OnLibraryRequested?.Invoke();

        // Forward body map button requests
        _topBar.OnBodyMapRequested += () => OnBodyMapRequested?.Invoke();
    }

    public override void PreDraw()
    {
        base.PreDraw();

        // Apply our UI colors to the window
        ImGui.PushStyleColor(ImGuiCol.WindowBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.ChildBg, UIColors.Background);
        ImGui.PushStyleColor(ImGuiCol.Text, UIColors.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, UIColors.TextDisabled);
        ImGui.PushStyleColor(ImGuiCol.Border, UIColors.Border);
        ImGui.PushStyleColor(ImGuiCol.TitleBg, UIColors.TitleBar);
        ImGui.PushStyleColor(ImGuiCol.TitleBgActive, UIColors.TitleBarActive);
        ImGui.PushStyleColor(ImGuiCol.Button, UIColors.Button);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, UIColors.ButtonHovered);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, UIColors.ButtonActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, UIColors.ControlBackground);
        ImGui.PushStyleColor(ImGuiCol.Header, UIColors.SelectionActive);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, UIColors.SelectionHovered);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, UIColors.SelectionActiveHovered);

        // Add window padding
        float padding = Controls.Flex.ContentPadding * ImGuiHelpers.GlobalScale;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(padding, padding));

        // Set size constraints
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(MinWidth, MinHeight),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
    }

    public override void Draw()
    {
        // Normal font scale
        ImGui.SetWindowFontScale(1.0f);

        // Apply internal padding by adjusting available width for child regions
        float padding = Flex.ContentPadding * ImGuiHelpers.GlobalScale;
        var windowSize = ImGui.GetContentRegionAvail();
        float paddedWidth = windowSize.X - padding * 2;

        // Top padding
        ImGui.Dummy(new Vector2(0, padding));

        // TopBar with horizontal padding
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding);
        using (var topBarRegion = ImRaii.Child("##topbar_region", new Vector2(paddedWidth, Flex.RowHeight * ImGuiHelpers.GlobalScale), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (topBarRegion.Success)
            {
                _topBar.Draw();
            }
        }

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding);
        ImGui.PushItemWidth(paddedWidth);
        ImGui.Separator();
        ImGui.PopItemWidth();
        ImGui.Spacing();

        if (!_gPoseService.IsGPosing)
        {
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding);
            ImGui.TextDisabled("Enter GPose to see scene");
            return;
        }

        float totalHeight = ImGui.GetContentRegionAvail().Y - padding; // Reserve bottom padding
        float splitterHeight = SplitterHeight * ImGuiHelpers.GlobalScale;
        float availableHeight = totalHeight - splitterHeight;
        float minHeight = MinPanelHeight * ImGuiHelpers.GlobalScale;

        // Scene has fixed height, properties takes remaining space
        float sceneHeight = MathF.Max(_sceneHeight, minHeight);
        float propertiesHeight = MathF.Max(availableHeight - sceneHeight, minHeight);

        // If properties is at minimum, scene takes remaining
        if (availableHeight - sceneHeight < minHeight)
        {
            propertiesHeight = minHeight;
            sceneHeight = MathF.Max(availableHeight - propertiesHeight, minHeight);
        }

        // Scene panel with horizontal padding
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding);
        using (var child = ImRaii.Child("scene_region", new Vector2(paddedWidth, sceneHeight), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                _scenePanel.Draw();
            }
        }

        // Draggable splitter (full width for easier grabbing)
        DrawSplitter(totalHeight, splitterHeight, minHeight);

        // Properties panel with horizontal padding
        // Get window draw list BEFORE entering child so shadows aren't clipped
        var windowDrawList = ImGui.GetWindowDrawList();

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + padding);
        using (var child = ImRaii.Child("properties_region", new Vector2(paddedWidth, propertiesHeight), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                _propertiesPanel.Draw(windowDrawList);
            }
        }
    }

    private void DrawSplitter(float totalHeight, float splitterHeight, float minHeight)
    {
        var cursorPos = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Use InvisibleButton for reliable click detection
        ImGui.InvisibleButton("##splitter", new Vector2(availWidth, splitterHeight));
        bool isHovered = ImGui.IsItemHovered();
        bool isClicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);

        // Draw splitter bar
        var drawList = ImGui.GetWindowDrawList();
        var splitterMin = cursorPos;
        var splitterMax = new Vector2(cursorPos.X + availWidth, cursorPos.Y + splitterHeight);
        var splitterCenter = cursorPos.Y + splitterHeight / 2;

        var bgColor = (isHovered || _isDraggingSplitter)
            ? ImGui.GetColorU32(ImGuiCol.SeparatorHovered)
            : ImGui.GetColorU32(ImGuiCol.Separator);

        drawList.AddRectFilled(splitterMin, splitterMax, bgColor);

        // Draw a visible line in the center
        var lineThickness = 1f * ImGuiHelpers.GlobalScale;
        var lineColor = ImGui.GetColorU32(ImGuiCol.Border);
        drawList.AddLine(
            new Vector2(cursorPos.X, splitterCenter),
            new Vector2(cursorPos.X + availWidth, splitterCenter),
            lineColor,
            lineThickness);

        // Handle dragging
        if (isClicked)
        {
            _isDraggingSplitter = true;
            _dragStartMouseY = ImGui.GetMousePos().Y;
            _dragStartSceneHeight = _sceneHeight;
        }

        if (_isDraggingSplitter)
        {
            if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            {
                _isDraggingSplitter = false;
            }
            else
            {
                float availableHeight = totalHeight - splitterHeight;
                float mouseDelta = ImGui.GetMousePos().Y - _dragStartMouseY;
                float newSceneHeight = _dragStartSceneHeight + mouseDelta;

                // Clamp scene height to minimum
                newSceneHeight = MathF.Max(newSceneHeight, minHeight);

                float heightDelta = newSceneHeight - _sceneHeight;

                if (heightDelta > 0.1f)
                {
                    // Dragging down - grow window to keep properties same size
                    var windowPos = ImGui.GetWindowPos();
                    var currentSize = ImGui.GetWindowSize();
                    var displaySize = ImGui.GetIO().DisplaySize;

                    // Don't let window grow past screen bottom
                    float maxHeight = displaySize.Y - windowPos.Y;
                    var newSize = new Vector2(currentSize.X, MathF.Min(currentSize.Y + heightDelta, maxHeight));

                    // Only update scene height by what we actually grew
                    float actualGrowth = newSize.Y - currentSize.Y;
                    if (actualGrowth > 0.1f)
                    {
                        ImGui.SetWindowSize(newSize);
                        _sceneHeight += actualGrowth;
                    }
                }
                else if (heightDelta < -0.1f)
                {
                    // Dragging up - shrink scene, properties grows
                    _sceneHeight = Math.Clamp(newSceneHeight, minHeight, availableHeight - minHeight);
                }
            }
        }
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(1); // Pop WindowPadding
        ImGui.PopStyleColor(14); // Pop all colors pushed in PreDraw
        base.PostDraw();
    }
}
