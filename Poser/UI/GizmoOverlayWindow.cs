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
    private readonly IEditorState _editorState;

    /// <summary>Arbitrary unique ID for ImGuizmo to track gizmo state.</summary>
    private const int GizmoId = 142857;

    // Track transforms when we start dragging for undo
    private Dictionary<ActorBase, Transform>? _dragStartTransforms;

    public GizmoOverlayWindow(
        IActorManager actorManager,
        ICameraService cameraService,
        IPosingService posingService,
        IHistoryService historyService,
        IAnimationService animationService,
        IEditorState editorState)
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
        _editorState = editorState;

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

        // Check if at least one selected actor is frozen
        bool anyFrozen = false;
        foreach (var actor in _actorManager.SelectedActors)
        {
            if (_animationService.IsFrozen(actor))
            {
                anyFrozen = true;
                break;
            }
        }

        var viewMatrix = _cameraService.GetViewMatrix();
        var projectionMatrix = _cameraService.GetProjectionMatrix();

        // Calculate pivot position based on mode
        var (pivotPosition, pivotRotation) = CalculatePivot();
        var pivotTransform = new Transform(pivotPosition, pivotRotation, Vector3.One);
        var modelMatrix = pivotTransform.ToMatrix();

        // Setup ImGuizmo
        ImGuizmo.BeginFrame();
        var io = ImGui.GetIO();
        ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        ImGuizmo.SetDrawlist();

        // Enable gizmo only if at least one actor is frozen
        ImGuizmo.Enable(anyFrozen);

        var viewMatrixCopy = viewMatrix;

        // Check if we're starting a new drag operation
        bool isUsing = ImGuizmo.IsUsing();
        if (isUsing && _dragStartTransforms == null && anyFrozen)
        {
            // Starting to drag - store initial transforms for ALL selected frozen actors
            _dragStartTransforms = new Dictionary<ActorBase, Transform>();
            foreach (var actor in _actorManager.SelectedActors)
            {
                if (_animationService.IsFrozen(actor))
                {
                    _dragStartTransforms[actor] = _posingService.GetEffectiveTransform(actor);
                }
            }
        }

        // Determine gizmo mode based on pivot mode
        var gizmoMode = _editorState.PivotMode == PivotMode.Local ? ImGuizmoMode.Local : ImGuizmoMode.World;

        // Draw gizmo with both Translate and Rotate
        if (ImGuizmo.Manipulate(
            ref viewMatrixCopy,
            ref projectionMatrix,
            ImGuizmoOperation.Translate | ImGuizmoOperation.Rotate,
            gizmoMode,
            ref modelMatrix))
        {
            if (anyFrozen)
            {
                var newPivotTransform = Transform.FromMatrix(modelMatrix);
                ApplyPivotTransform(pivotTransform, newPivotTransform);
            }
        }

        // Check if we finished dragging
        if (!isUsing && _dragStartTransforms != null)
        {
            CreateUndoAction();
            _dragStartTransforms = null;
        }
    }

    private (Vector3 position, Quaternion rotation) CalculatePivot()
    {
        var selectedActors = _actorManager.SelectedActors;
        var primaryActor = _actorManager.PrimarySelectedActor;

        switch (_editorState.PivotMode)
        {
            case PivotMode.World:
                // Rotate around primary (first selected) actor's position
                if (primaryActor != null)
                {
                    var t = _posingService.GetEffectiveTransform(primaryActor);
                    return (t.Position, Quaternion.Identity);
                }
                return (Vector3.Zero, Quaternion.Identity);

            case PivotMode.Average:
                // Average position of all selected actors
                var averagePosition = Vector3.Zero;
                int frozenCount = 0;
                foreach (var actor in selectedActors)
                {
                    if (_animationService.IsFrozen(actor))
                    {
                        averagePosition += _posingService.GetEffectiveTransform(actor).Position;
                        frozenCount++;
                    }
                }
                if (frozenCount > 0)
                    averagePosition /= frozenCount;
                return (averagePosition, Quaternion.Identity);

            case PivotMode.Local:
            default:
                // Each actor rotates around itself - use primary for gizmo display
                if (primaryActor != null)
                {
                    var t = _posingService.GetEffectiveTransform(primaryActor);
                    return (t.Position, t.Rotation);
                }
                return (Vector3.Zero, Quaternion.Identity);
        }
    }

    private void ApplyPivotTransform(Transform oldPivot, Transform newPivot)
    {
        // Calculate deltas
        var positionDelta = newPivot.Position - oldPivot.Position;
        var rotationDelta = newPivot.Rotation * Quaternion.Inverse(oldPivot.Rotation);

        if (_editorState.PivotMode == PivotMode.Local)
        {
            // Local mode: each actor rotates around its own center
            foreach (var actor in _actorManager.SelectedActors)
            {
                if (!_animationService.IsFrozen(actor))
                    continue;

                var actorTransform = _posingService.GetEffectiveTransform(actor);

                // Apply position delta (translation)
                actorTransform.Position += positionDelta;

                // Apply rotation to the actor itself (around its own center)
                actorTransform.Rotation = rotationDelta * actorTransform.Rotation;

                _posingService.SetTransformOverride(actor, actorTransform);
            }
        }
        else
        {
            // World/Average mode: all actors rotate around the pivot point
            foreach (var actor in _actorManager.SelectedActors)
            {
                if (!_animationService.IsFrozen(actor))
                    continue;

                var actorTransform = _posingService.GetEffectiveTransform(actor);

                // Rotate position around the pivot
                var relativePos = actorTransform.Position - oldPivot.Position;
                var rotatedRelativePos = Vector3.Transform(relativePos, rotationDelta);
                actorTransform.Position = newPivot.Position + rotatedRelativePos;

                // Rotate the actor itself
                actorTransform.Rotation = rotationDelta * actorTransform.Rotation;

                _posingService.SetTransformOverride(actor, actorTransform);
            }
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
