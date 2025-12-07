using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

public enum PropertiesTab
{
    Transform,
    Gaze
}

/// <summary>
/// Renders the Properties panel showing details of the selected entity.
/// Fixed height, non-collapsible panel anchored at the bottom.
/// </summary>
public class PropertiesPanel
{
    private const float TabBarWidth = 32f;

    private readonly IActorManager _actorManager;
    private readonly IPosingService _posingService;
    private readonly IAnimationService _animationService;
    private readonly IHistoryService _historyService;
    private readonly IGazeService _gazeService;
    private readonly TransformWidget _transformWidget;

    private PropertiesTab _activeTab = PropertiesTab.Transform;

    private static readonly string[] GazeModeNames = { "None", "Forward", "Camera", "Entity" };

    public PropertiesPanel(
        IActorManager actorManager,
        IPosingService posingService,
        IAnimationService animationService,
        IHistoryService historyService,
        IGazeService gazeService)
    {
        _actorManager = actorManager;
        _posingService = posingService;
        _animationService = animationService;
        _historyService = historyService;
        _gazeService = gazeService;
        _transformWidget = new TransformWidget();

        // Wire up transform history
        _transformWidget.OnTransformCommit += OnTransformCommit;
    }

    private void OnTransformCommit(Transform oldTransform, Transform newTransform)
    {
        var actor = _actorManager.PrimarySelectedActor;
        if (actor == null) return;

        var action = new TransformHistoryAction(_posingService, actor, oldTransform, newTransform);
        _historyService.Push(action);
    }

    public void Draw()
    {
        ImGui.Text("Properties");
        ImGui.Spacing();

        var selected = _actorManager.PrimarySelectedActor;

        if (selected == null)
        {
            ImGui.TextDisabled("No entity selected");
            return;
        }

        // Layout: vertical tab bar on left, content on right
        float tabBarWidth = TabBarWidth * ImGuiHelpers.GlobalScale;
        float availHeight = ImGui.GetContentRegionAvail().Y;

        // Draw vertical tab bar
        using (var tabChild = ImRaii.Child("tab_bar", new Vector2(tabBarWidth, availHeight), false))
        {
            if (tabChild.Success)
            {
                DrawVerticalTabBar();
            }
        }

        ImGui.SameLine();

        // Draw content area
        using (var contentChild = ImRaii.Child("tab_content", new Vector2(-1, availHeight), false))
        {
            if (contentChild.Success)
            {
                DrawTabContent(selected);
            }
        }
    }

    private void DrawVerticalTabBar()
    {
        float buttonSize = TabBarWidth * ImGuiHelpers.GlobalScale - ImGui.GetStyle().WindowPadding.X;
        var size = new Vector2(buttonSize, buttonSize);

        // Transform tab (move icon)
        bool isTransformActive = _activeTab == PropertiesTab.Transform;
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), isTransformActive))
        {
            if (ImPoser.CenteredIconButton("tab_transform", FontAwesomeIcon.ArrowsAlt, size, "Transform"))
            {
                _activeTab = PropertiesTab.Transform;
            }
        }

        // Gaze tab (eye icon)
        bool isGazeActive = _activeTab == PropertiesTab.Gaze;
        using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), isGazeActive))
        {
            if (ImPoser.CenteredIconButton("tab_gaze", FontAwesomeIcon.Eye, size, "Gaze"))
            {
                _activeTab = PropertiesTab.Gaze;
            }
        }
    }

    private void DrawTabContent(ActorBase actor)
    {
        bool isFrozen = _animationService.IsFrozen(actor);

        switch (_activeTab)
        {
            case PropertiesTab.Transform:
                DrawTransformTab(actor, isFrozen);
                break;
            case PropertiesTab.Gaze:
                DrawGazeTab(actor, isFrozen);
                break;
        }
    }

    private void DrawTransformTab(ActorBase actor, bool isFrozen)
    {
        var transform = _posingService.GetEffectiveTransform(actor);

        if (_transformWidget.Draw("transform", ref transform, !isFrozen))
        {
            _posingService.SetTransformOverride(actor, transform);
        }
    }

    private void DrawGazeTab(ActorBase actor, bool isFrozen)
    {
        var gazeState = _gazeService.GetGazeState(actor);

        // Gaze mode dropdown
        ImGui.Text("Mode");
        ImGui.SameLine(80 * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(-1);
        int currentMode = (int)gazeState.Mode;
        if (ImGui.Combo("##gaze_mode", ref currentMode, GazeModeNames, GazeModeNames.Length))
        {
            _gazeService.SetGazeMode(actor, (GazeTargetMode)currentMode);
        }

        ImGui.Spacing();

        // Target type checkboxes (which body parts)
        ImGui.Text("Affect");
        ImGui.SameLine(80 * ImGuiHelpers.GlobalScale);

        bool affectBody = gazeState.TargetType.HasFlag(GazeTargetType.Body);
        if (ImGui.Checkbox("Body", ref affectBody))
        {
            var newType = affectBody
                ? gazeState.TargetType | GazeTargetType.Body
                : gazeState.TargetType & ~GazeTargetType.Body;
            _gazeService.SetGazeTargetType(actor, newType);
        }

        ImGui.SameLine();
        bool affectHead = gazeState.TargetType.HasFlag(GazeTargetType.Head);
        if (ImGui.Checkbox("Head", ref affectHead))
        {
            var newType = affectHead
                ? gazeState.TargetType | GazeTargetType.Head
                : gazeState.TargetType & ~GazeTargetType.Head;
            _gazeService.SetGazeTargetType(actor, newType);
        }

        ImGui.SameLine();
        bool affectEyes = gazeState.TargetType.HasFlag(GazeTargetType.Eyes);
        if (ImGui.Checkbox("Eyes", ref affectEyes))
        {
            var newType = affectEyes
                ? gazeState.TargetType | GazeTargetType.Eyes
                : gazeState.TargetType & ~GazeTargetType.Eyes;
            _gazeService.SetGazeTargetType(actor, newType);
        }

        ImGui.Spacing();

        // Entity target list (only shown in Entity mode)
        if (gazeState.Mode == GazeTargetMode.Entity)
        {
            ImGui.Text("Target");
            ImGui.SameLine(80 * ImGuiHelpers.GlobalScale);

            // Show combo with available entities
            var actors = _actorManager.Actors;
            string currentTargetName = gazeState.TargetEntity?.Name ?? "None";

            ImGui.SetNextItemWidth(-1);
            if (ImGui.BeginCombo("##gaze_target", currentTargetName))
            {
                foreach (var targetActor in actors)
                {
                    // Don't allow targeting self
                    if (targetActor == actor)
                        continue;

                    bool isSelected = gazeState.TargetEntity == targetActor;
                    if (ImGui.Selectable(targetActor.Name, isSelected))
                    {
                        _gazeService.SetGazeTarget(actor, targetActor);
                    }

                    if (isSelected)
                        ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Reset button
        if (ImGui.Button("Reset Gaze"))
        {
            _gazeService.ResetGaze(actor);
        }

        if (!isFrozen)
        {
            ImGui.Spacing();
            ImGui.TextColored(new System.Numerics.Vector4(1, 0.7f, 0, 1), "Freeze the actor to enable gaze control.");
        }
    }
}
