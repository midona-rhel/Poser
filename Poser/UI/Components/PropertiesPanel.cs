using Dalamud.Bindings.ImGui;
using Poser.Entities;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Renders the Properties panel showing details of the selected entity.
/// Fixed height, non-collapsible panel anchored at the bottom.
/// </summary>
public class PropertiesPanel
{
    private readonly IActorManager _actorManager;
    private readonly IPosingService _posingService;
    private readonly IAnimationService _animationService;
    private readonly IHistoryService _historyService;
    private readonly TransformWidget _transformWidget;

    public PropertiesPanel(IActorManager actorManager, IPosingService posingService, IAnimationService animationService, IHistoryService historyService)
    {
        _actorManager = actorManager;
        _posingService = posingService;
        _animationService = animationService;
        _historyService = historyService;
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

        DrawEntityProperties(selected);
    }

    private void DrawEntityProperties(ActorBase actor)
    {
        bool isFrozen = _animationService.IsFrozen(actor);

        // Freeze checkbox with hint in parentheses
        bool frozen = isFrozen;
        string label = isFrozen ? "Freeze Animation" : "Freeze Animation (freeze to enable posing)";
        if (ImGui.Checkbox(label, ref frozen))
        {
            if (frozen)
                _animationService.Freeze(actor);
            else
                _animationService.Unfreeze(actor);
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Transform - draw directly, no collapsible header
        ImGui.TextDisabled("Transform");
        ImGui.Spacing();

        var transform = _posingService.GetEffectiveTransform(actor);

        if (_transformWidget.Draw("transform", ref transform, !isFrozen))
        {
            _posingService.SetTransformOverride(actor, transform);
        }
    }
}
