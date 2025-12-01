using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Entities;
using Poser.History;
using Poser.Services;

namespace Poser.UI;

public class GizmoOverlayWindow : Window
{
    private readonly IActorManager _actorManager;
    private readonly ICameraService _cameraService;
    private readonly IPosingService _posingService;
    private readonly IHistoryService _historyService;

    private const int GizmoId = 142857;

    // Track the transform when we start dragging for undo
    private Transform? _dragStartTransform;
    private ActorBase? _dragActor;

    public GizmoOverlayWindow(
        IActorManager actorManager,
        ICameraService cameraService,
        IPosingService posingService,
        IHistoryService historyService)
        : base("##poser_gizmo_overlay",
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings)
    {
        _actorManager = actorManager;
        _cameraService = cameraService;
        _posingService = posingService;
        _historyService = historyService;

        // This window needs to be non-interactable except for the gizmo
        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        // Position at top-left corner, spanning the entire screen
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero, ImGuiCond.Always);

        var io = ImGui.GetIO();
        Size = io.DisplaySize;
        SizeCondition = ImGuiCond.Always;

        ImGuizmo.SetID(GizmoId);
    }

    public override void Draw()
    {
        var selectedActor = _actorManager.PrimarySelectedActor;
        if (selectedActor == null)
            return;

        // Get camera matrices
        var viewMatrix = _cameraService.GetViewMatrix();
        var projectionMatrix = _cameraService.GetProjectionMatrix();

        // Get the effective transform (override or game state)
        var transform = _posingService.GetEffectiveTransform(selectedActor);

        // Create model matrix from actor transform
        var modelMatrix = transform.ToMatrix();

        // Setup ImGuizmo
        ImGuizmo.BeginFrame();
        var io = ImGui.GetIO();
        ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        ImGuizmo.SetDrawlist();
        ImGuizmo.Enable(true);

        // Note: We need a mutable copy of viewMatrix for ImGuizmo
        var viewMatrixCopy = viewMatrix;

        // Check if we're starting a new drag operation
        bool isUsing = ImGuizmo.IsUsing();
        if (isUsing && _dragStartTransform == null)
        {
            // Starting to drag - store the initial transform
            _dragStartTransform = transform;
            _dragActor = selectedActor;
        }

        // Draw the gizmo and handle manipulation
        if (ImGuizmo.Manipulate(
            ref viewMatrixCopy,
            ref projectionMatrix,
            ImGuizmoOperation.Translate,
            ImGuizmoMode.World,
            ref modelMatrix))
        {
            // Extract the new transform from the manipulated matrix
            var newTransform = Transform.FromMatrix(modelMatrix);

            // Apply the transform to the primary selected actor
            _posingService.SetTransformOverride(selectedActor, newTransform);

            // Also apply to other selected actors (offset by their relative positions)
            ApplyToOtherSelectedActors(selectedActor, transform, newTransform);
        }

        // Check if we finished dragging
        if (!isUsing && _dragStartTransform.HasValue && _dragActor != null)
        {
            // Finished dragging - create undo action
            var finalTransform = _posingService.GetEffectiveTransform(_dragActor);
            if (_dragStartTransform.Value != finalTransform)
            {
                var action = new TranslateActorAction(
                    _posingService,
                    _dragActor,
                    _dragStartTransform.Value,
                    finalTransform);
                _historyService.Push(action);
            }

            _dragStartTransform = null;
            _dragActor = null;
        }
    }

    /// <summary>
    /// Applies the same delta transform to other selected actors.
    /// </summary>
    private void ApplyToOtherSelectedActors(ActorBase primary, Transform oldTransform, Transform newTransform)
    {
        // Calculate the delta
        var positionDelta = newTransform.Position - oldTransform.Position;

        foreach (var actor in _actorManager.SelectedActors)
        {
            if (actor == primary)
                continue;

            var actorTransform = _posingService.GetEffectiveTransform(actor);
            actorTransform.Position += positionDelta;
            _posingService.SetTransformOverride(actor, actorTransform);
        }
    }

    public override void PostDraw()
    {
        ImGuizmo.SetID(0);
        base.PostDraw();
    }
}
