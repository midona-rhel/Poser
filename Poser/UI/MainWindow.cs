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
    private const float PropertiesMinHeight = 200f;

    private readonly IGPoseService _gPoseService;
    private readonly IAnimationService _animationService;
    private readonly IHistoryService _historyService;

    // UI Components
    private readonly TopBar _topBar;
    private readonly ScenePanel _scenePanel;
    private readonly PropertiesPanel _propertiesPanel;

    public MainWindow(
        IGPoseService gPoseService,
        IActorManager actorManager,
        IAnimationService animationService,
        IHistoryService historyService,
        IPosingService posingService,
        EventBus eventBus)
        : base($"{Poser.PluginName}###poser_sidebar_window",
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoScrollbar)
    {
        _gPoseService = gPoseService;
        _animationService = animationService;
        _historyService = historyService;

        // Initialize components
        _topBar = new TopBar(gPoseService, historyService);
        _scenePanel = new ScenePanel(actorManager, animationService, eventBus);
        _propertiesPanel = new PropertiesPanel(actorManager, posingService, animationService, historyService);

        // Wire up events from ScenePanel
        _scenePanel.OnSpawnClone += HandleSpawnClone;
        _scenePanel.OnDeleteSelected += HandleDeleteSelected;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        var displaySize = ImGui.GetIO().DisplaySize;

        // Position window at right edge, full height
        Position = new Vector2(displaySize.X - SidebarWidth, 0);
        Size = new Vector2(SidebarWidth, displaySize.Y);

        // Lock size constraints to screen height
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(SidebarWidth, displaySize.Y),
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

        // Properties gets priority - use minimum height
        float propertiesHeight = PropertiesMinHeight * ImGuiHelpers.GlobalScale;

        // Scene gets remaining space
        float sceneHeight = totalHeight - propertiesHeight - ImGui.GetStyle().ItemSpacing.Y;

        // Scene panel - scrollable child that fills top portion
        using (var child = ImRaii.Child("scene_region", new Vector2(-1, sceneHeight), false,
            ImGuiWindowFlags.AlwaysUseWindowPadding))
        {
            if (child.Success)
            {
                _scenePanel.Draw();
            }
        }

        ImGui.Separator();

        // Properties panel - child region to properly constrain width, no scroll
        using (var child = ImRaii.Child("properties_region", new Vector2(-1, -1), false,
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse))
        {
            if (child.Success)
            {
                _propertiesPanel.Draw();
            }
        }
    }

    private void HandleSpawnClone()
    {
        // TODO: Implement actor spawning
    }

    private void HandleDeleteSelected()
    {
        // TODO: Implement actor deletion
    }
}
