using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Poser.Core;
using Poser.Services;
using Poser.UI.Components;

namespace Poser.UI;

public class MainWindow : Window
{
    private const float SidebarWidth = 560f;
    private const float SplitterHeight = 8f;
    private const float MinPanelHeight = 100f;

    private readonly IGPoseService _gPoseService;
    private readonly IAnimationService _animationService;
    private readonly IHistoryService _historyService;

    // UI Components
    private readonly TopBar _topBar;
    private readonly ScenePanel _scenePanel;
    private readonly PropertiesPanel _propertiesPanel;

    // Splitter state
    private float _propertiesRatio = 0.5f; // Properties takes 50% by default
    private bool _isDraggingSplitter;
    private float _dragOffset; // Offset from splitter center to mouse when drag started

    public MainWindow(
        IGPoseService gPoseService,
        IActorManager actorManager,
        IAnimationService animationService,
        IAnimationDataService animationDataService,
        IActorSpawnService spawnService,
        IHistoryService historyService,
        ICameraService cameraService,
        IPosingService posingService,
        IGazeService gazeService,
        ISkeletonService skeletonService,
        IEditorState editorState,
        IEventBus eventBus)
        : base($"{Poser.PluginName}###poser_sidebar_window",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar)
    {
        _gPoseService = gPoseService;
        _animationService = animationService;
        _historyService = historyService;

        // Initialize components
        _topBar = new TopBar(gPoseService, historyService);
        _scenePanel = new ScenePanel(actorManager, animationService, spawnService, historyService, cameraService, gPoseService, skeletonService, editorState, eventBus);
        _propertiesPanel = new PropertiesPanel(actorManager, posingService, animationService, animationDataService, historyService, gazeService);
    }

    public override void PreDraw()
    {
        base.PreDraw();

        var displaySize = ImGui.GetIO().DisplaySize;

        // Position window at right edge, full height
        Position = new Vector2(displaySize.X - SidebarWidth, 0);
        Size = new Vector2(SidebarWidth, displaySize.Y);

        // Lock size constraints
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(SidebarWidth, 100),
            MaximumSize = new Vector2(SidebarWidth, displaySize.Y)
        };
    }

    public override void Draw()
    {
        _topBar.Draw();
        ImGui.Separator();
        ImGui.Spacing();

        if (!_gPoseService.IsGPosing)
        {
            ImGui.TextDisabled("Enter GPose to see scene");
            return;
        }

        float totalHeight = ImGui.GetContentRegionAvail().Y;
        float splitterHeight = SplitterHeight * ImGuiHelpers.GlobalScale;
        float availableHeight = totalHeight - splitterHeight;

        // Calculate heights based on ratio
        float propertiesHeight = availableHeight * _propertiesRatio;
        float sceneHeight = availableHeight - propertiesHeight;

        // Clamp to minimums
        float minHeight = MinPanelHeight * ImGuiHelpers.GlobalScale;
        propertiesHeight = MathF.Max(propertiesHeight, minHeight);
        sceneHeight = MathF.Max(sceneHeight, minHeight);

        // Scene panel
        using (var child = ImRaii.Child("scene_region", new Vector2(-1, sceneHeight), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                _scenePanel.Draw();
            }
        }

        // Draggable splitter
        DrawSplitter(totalHeight, splitterHeight, minHeight);

        // Properties panel
        using (var child = ImRaii.Child("properties_region", new Vector2(-1, -1), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                _propertiesPanel.Draw();
            }
        }
    }

    private void DrawSplitter(float totalHeight, float splitterHeight, float minHeight)
    {
        var cursorPos = ImGui.GetCursorScreenPos();
        var availWidth = ImGui.GetContentRegionAvail().X;

        // Draw splitter bar
        var drawList = ImGui.GetWindowDrawList();
        var splitterMin = cursorPos;
        var splitterMax = new Vector2(cursorPos.X + availWidth, cursorPos.Y + splitterHeight);
        var splitterCenter = cursorPos.Y + splitterHeight / 2;

        // Highlight when hovered or dragging
        bool isHovered = ImGui.IsMouseHoveringRect(splitterMin, splitterMax);
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
        if (isHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            _isDraggingSplitter = true;
            // Store the current scene height when drag starts
            float availableHeight = totalHeight - splitterHeight;
            float currentSceneHeight = availableHeight * (1 - _propertiesRatio);
            // Calculate where the splitter top is relative to content start
            float contentStartY = ImGui.GetWindowPos().Y + ImGui.GetCursorStartPos().Y;
            float splitterTopY = contentStartY + currentSceneHeight;
            // Store offset: where mouse clicked relative to splitter top
            _dragOffset = ImGui.GetMousePos().Y - splitterTopY;
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
                float contentStartY = ImGui.GetWindowPos().Y + ImGui.GetCursorStartPos().Y;

                // Mouse position minus the initial offset gives us where splitter top should be
                float newSplitterTopY = ImGui.GetMousePos().Y - _dragOffset;
                float newSceneHeight = newSplitterTopY - contentStartY;
                float newPropertiesHeight = availableHeight - newSceneHeight;

                // Clamp and update ratio
                newPropertiesHeight = Math.Clamp(newPropertiesHeight, minHeight, availableHeight - minHeight);
                _propertiesRatio = newPropertiesHeight / availableHeight;
            }
        }

        // Reserve space for splitter
        ImGui.Dummy(new Vector2(availWidth, splitterHeight));
    }

}
