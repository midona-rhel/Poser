using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Poser.Core;
using Poser.Entities;
using Poser.History;
using Poser.Services;
using Poser.UI.Components;

namespace Poser.UI;

public class MainWindow : Window
{
    private const float SidebarWidth = 560f;

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
            ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoMove)
    {
        _gPoseService = gPoseService;
        _animationService = animationService;
        _historyService = historyService;

        // Initialize components
        _topBar = new TopBar(gPoseService, historyService);
        _scenePanel = new ScenePanel(actorManager, animationService, eventBus);
        _propertiesPanel = new PropertiesPanel(actorManager, posingService);

        // Wire up events from ScenePanel
        _scenePanel.OnAnimationFreezeToggle += HandleAnimationFreezeToggle;
        _scenePanel.OnPhysicsFreezeToggle += HandlePhysicsFreezeToggle;
        _scenePanel.OnSpawnClone += HandleSpawnClone;
        _scenePanel.OnDeleteSelected += HandleDeleteSelected;

        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(SidebarWidth, 400),
            MaximumSize = new Vector2(SidebarWidth, 4000)
        };
    }

    public override void PreDraw()
    {
        base.PreDraw();

        var displaySize = ImGui.GetIO().DisplaySize;
        Position = new Vector2(displaySize.X - SidebarWidth, 0);
        Size = new Vector2(SidebarWidth, displaySize.Y);
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

        _scenePanel.Draw();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        _propertiesPanel.Draw();
    }

    private void HandleAnimationFreezeToggle(ActorBase actor, bool freeze)
    {
        var action = new FreezeAnimationAction(_animationService, actor, freeze);
        _historyService.Push(action);
    }

    private void HandlePhysicsFreezeToggle(ActorBase actor, bool freeze)
    {
        var action = new FreezePhysicsAction(_animationService, actor, freeze);
        _historyService.Push(action);
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
