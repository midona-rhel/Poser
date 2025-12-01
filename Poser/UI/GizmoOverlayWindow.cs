using System.Collections.Generic;
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
    private readonly IAnimationService _animationService;

    private const int GizmoId = 142857;

    // Track transforms when we start dragging for undo
    private Dictionary<ActorBase, Transform>? _dragStartTransforms;

    public GizmoOverlayWindow(
        IActorManager actorManager,
        ICameraService cameraService,
        IPosingService posingService,
        IHistoryService historyService,
        IAnimationService animationService)
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
        _animationService = animationService;

        RespectCloseHotkey = false;
    }

    public override void PreDraw()
    {
        base.PreDraw();

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

        // Check if the primary actor is frozen - required for posing
        bool isFrozen = _animationService.IsFrozen(selectedActor);

        var viewMatrix = _cameraService.GetViewMatrix();
        var projectionMatrix = _cameraService.GetProjectionMatrix();

        // Get the effective transform for the primary selected actor
        var transform = _posingService.GetEffectiveTransform(selectedActor);
        var modelMatrix = transform.ToMatrix();

        // Setup ImGuizmo
        ImGuizmo.BeginFrame();
        var io = ImGui.GetIO();
        ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        ImGuizmo.SetDrawlist();

        // Enable gizmo only if actor is frozen
        ImGuizmo.Enable(isFrozen);

        var viewMatrixCopy = viewMatrix;

        // Check if we're starting a new drag operation
        bool isUsing = ImGuizmo.IsUsing();
        if (isUsing && _dragStartTransforms == null && isFrozen)
        {
            // Starting to drag - store initial transforms for ALL selected frozen actors
            _dragStartTransforms = new Dictionary<ActorBase, Transform>();
            foreach (var actor in _actorManager.SelectedActors)
            {
                // Only track frozen actors
                if (_animationService.IsFrozen(actor))
                {
                    _dragStartTransforms[actor] = _posingService.GetEffectiveTransform(actor);
                }
            }
        }

        // Draw gizmo with both Translate and Rotate
        // When not frozen, the gizmo will be drawn grayed out and non-interactive
        if (ImGuizmo.Manipulate(
            ref viewMatrixCopy,
            ref projectionMatrix,
            ImGuizmoOperation.Translate | ImGuizmoOperation.Rotate,
            ImGuizmoMode.World,
            ref modelMatrix))
        {
            // Only apply if frozen
            if (isFrozen)
            {
                var newTransform = Transform.FromMatrix(modelMatrix);

                // Apply to primary actor
                _posingService.SetTransformOverride(selectedActor, newTransform);

                // Apply delta to other selected frozen actors
                ApplyDeltaToOtherActors(selectedActor, transform, newTransform);
            }
        }

        // Check if we finished dragging
        if (!isUsing && _dragStartTransforms != null)
        {
            CreateUndoAction();
            _dragStartTransforms = null;
        }
    }

    private void ApplyDeltaToOtherActors(ActorBase primary, Transform oldTransform, Transform newTransform)
    {
        // Calculate deltas
        var positionDelta = newTransform.Position - oldTransform.Position;
        var rotationDelta = newTransform.Rotation * Quaternion.Inverse(oldTransform.Rotation);

        foreach (var actor in _actorManager.SelectedActors)
        {
            if (actor == primary)
                continue;

            // Only apply to frozen actors
            if (!_animationService.IsFrozen(actor))
                continue;

            var actorTransform = _posingService.GetEffectiveTransform(actor);

            // Apply position delta
            actorTransform.Position += positionDelta;

            // Apply rotation delta (rotate around the primary actor's position)
            var relativePos = actorTransform.Position - oldTransform.Position;
            var rotatedRelativePos = Vector3.Transform(relativePos, rotationDelta);
            actorTransform.Position = newTransform.Position + rotatedRelativePos - positionDelta;

            // Also rotate the actor itself
            actorTransform.Rotation = rotationDelta * actorTransform.Rotation;

            _posingService.SetTransformOverride(actor, actorTransform);
        }
    }

    private void CreateUndoAction()
    {
        if (_dragStartTransforms == null || _dragStartTransforms.Count == 0)
            return;

        var actions = new List<IHistoryAction>();

        foreach (var (actor, startTransform) in _dragStartTransforms)
        {
            var endTransform = _posingService.GetEffectiveTransform(actor);
            if (startTransform != endTransform)
            {
                actions.Add(new TransformActorAction(_posingService, actor, startTransform, endTransform));
            }
        }

        if (actions.Count == 0)
            return;

        if (actions.Count == 1)
        {
            _historyService.Push(actions[0]);
        }
        else
        {
            var description = $"Transform {actions.Count} actors";
            _historyService.Push(new CompositeAction(description, actions));
        }
    }

    public override void PostDraw()
    {
        ImGuizmo.SetID(0);
        base.PostDraw();
    }
}
