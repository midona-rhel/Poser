using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Core;
using Poser.Entities;
using Poser.History;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// Unified gizmo overlay window that handles both actor and bone transforms.
/// </summary>
public class GizmoOverlayWindow : Window
{
    private readonly IActorManager _actorManager;
    private readonly ICameraService _cameraService;
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;
    private readonly ISkeletonService _skeletonService;
    private readonly IHistoryService _historyService;
    private readonly IAnimationService _animationService;
    private readonly IEditorState _editorState;

    /// <summary>Arbitrary unique ID for ImGuizmo to track gizmo state.</summary>
    private const int GizmoId = 142857;

    // Actor gizmo state
    private Dictionary<IActor, Transform>? _actorDragStartTransforms;

    // Bone gizmo state
    private Transform? _lastFrameBoneGizmo;
    private Transform? _boneDragStartModification;

    public GizmoOverlayWindow(
        IActorManager actorManager,
        ICameraService cameraService,
        IPosingService posingService,
        IBonePosingService bonePosingService,
        ISkeletonService skeletonService,
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
        _bonePosingService = bonePosingService;
        _skeletonService = skeletonService;
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
        var targetType = _editorState.GetGizmoTargetType();

        switch (targetType)
        {
            case GizmoTargetType.Bone:
                DrawBoneGizmo();
                break;
            case GizmoTargetType.Actor:
                DrawActorGizmo();
                break;
            case GizmoTargetType.None:
            default:
                // No target, no gizmo
                break;
        }
    }

    private void DrawActorGizmo()
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
        var (pivotPosition, pivotRotation) = CalculateActorPivot();
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
        if (isUsing && _actorDragStartTransforms == null && anyFrozen)
        {
            // Starting to drag - store initial transforms for ALL selected frozen actors
            _actorDragStartTransforms = new Dictionary<IActor, Transform>();
            foreach (var actor in _actorManager.SelectedActors)
            {
                if (_animationService.IsFrozen(actor))
                {
                    _actorDragStartTransforms[actor] = _posingService.GetEffectiveTransform(actor);
                }
            }
        }

        // Determine gizmo mode based on transform orientation
        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Local
            ? ImGuizmoMode.Local
            : ImGuizmoMode.World;

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
                ApplyActorPivotTransform(pivotTransform, newPivotTransform);
            }
        }

        // Check if we finished dragging
        if (!isUsing && _actorDragStartTransforms != null)
        {
            CreateActorUndoAction();
            _actorDragStartTransforms = null;
        }
    }

    private void DrawBoneGizmo()
    {
        var selectedBone = _editorState.SelectedBone;
        if (selectedBone == null)
            return;

        var skeleton = selectedBone.Skeleton as Skeleton;
        if (skeleton == null || !skeleton.IsValid)
            return;

        // Check if the actor is frozen - bones can only be manipulated when frozen
        var actor = skeleton.Actor;
        bool isFrozen = _animationService.IsFrozen(actor);

        // Register skeleton for cache updates
        _bonePosingService.RegisterSkeletonForCacheUpdate(skeleton);

        // Get camera matrices
        var projectionMatrix = _cameraService.GetProjectionMatrix();
        var worldViewMatrix = _cameraService.GetViewMatrix();
        worldViewMatrix.M44 = 1; // Important fix from Brio

        // Get the model matrix and incorporate it into the view matrix
        var modelMatrix = skeleton.GetModelMatrix();
        worldViewMatrix = Matrix4x4.Multiply(modelMatrix, worldViewMatrix);

        // Get the bone's current transform
        var currentTransform = selectedBone.LastTransform;

        // Use last observed, or current if not tracking yet
        var lastObserved = _lastFrameBoneGizmo ?? currentTransform;
        var lastMatrix = lastObserved.ToMatrix();

        // Setup ImGuizmo
        ImGuizmo.BeginFrame();
        var io = ImGui.GetIO();
        ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        ImGuizmo.SetDrawlist();

        // Enable gizmo only if actor is frozen
        ImGuizmo.Enable(isFrozen);

        bool isUsing = ImGuizmo.IsUsing();

        // Capture for history on drag start (only if frozen)
        if (isUsing && _lastFrameBoneGizmo == null && isFrozen)
        {
            _boneDragStartModification = _bonePosingService.GetModification(selectedBone);
        }

        // Get the gizmo operation based on selected tool
        var gizmoOperation = _editorState.TransformTool switch
        {
            TransformTool.Move => ImGuizmoOperation.Translate,
            TransformTool.Rotate => ImGuizmoOperation.Rotate,
            TransformTool.Scale => ImGuizmoOperation.Scale,
            TransformTool.Universal => ImGuizmoOperation.Translate | ImGuizmoOperation.Rotate | ImGuizmoOperation.Scale,
            _ => ImGuizmoOperation.Rotate
        };

        Transform? newTransform = null;

        // Draw gizmo
        if (ImGuizmo.Manipulate(
            ref worldViewMatrix,
            ref projectionMatrix,
            gizmoOperation,
            ImGuizmoMode.Local,
            ref lastMatrix))
        {
            newTransform = Transform.FromMatrix(lastMatrix);
            _lastFrameBoneGizmo = newTransform;
        }

        // Apply transform if changed (only if frozen)
        if (newTransform != null && isFrozen)
        {
            var delta = newTransform.Value.CalculateDiff(lastObserved);
            _bonePosingService.ApplyTransform(selectedBone, delta, null, TransformComponents.All);
        }

        // Finish drag - create undo action
        if (_lastFrameBoneGizmo.HasValue && !isUsing)
        {
            var modification = _bonePosingService.GetModification(selectedBone);
            if (modification.HasValue)
            {
                var action = new TransformBoneAction(
                    _bonePosingService,
                    selectedBone,
                    _boneDragStartModification ?? new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.Zero },
                    modification.Value);
                _historyService.Record(action);
            }
            _lastFrameBoneGizmo = null;
            _boneDragStartModification = null;
        }
    }

    private (Vector3 position, Quaternion rotation) CalculateActorPivot()
    {
        var selectedActors = _actorManager.SelectedActors;
        var primaryActor = _actorManager.PrimarySelectedActor;

        // First calculate the pivot position based on TransformPivot
        Vector3 pivotPosition;
        switch (_editorState.TransformPivot)
        {
            case TransformPivot.Median:
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
                pivotPosition = averagePosition;
                break;

            case TransformPivot.Individual:
            case TransformPivot.Parent:
            default:
                // Use primary actor's position for gizmo display
                if (primaryActor != null)
                {
                    pivotPosition = _posingService.GetEffectiveTransform(primaryActor).Position;
                }
                else
                {
                    pivotPosition = Vector3.Zero;
                }
                break;
        }

        // Then calculate the rotation based on TransformOrientation
        Quaternion pivotRotation;
        switch (_editorState.TransformOrientation)
        {
            case TransformOrientation.Global:
                pivotRotation = Quaternion.Identity;
                break;

            case TransformOrientation.Local:
            case TransformOrientation.Parent:
            default:
                // Use primary actor's rotation for local orientation
                if (primaryActor != null)
                {
                    pivotRotation = _posingService.GetEffectiveTransform(primaryActor).Rotation;
                }
                else
                {
                    pivotRotation = Quaternion.Identity;
                }
                break;
        }

        return (pivotPosition, pivotRotation);
    }

    private void ApplyActorPivotTransform(Transform oldPivot, Transform newPivot)
    {
        // Calculate deltas
        var positionDelta = newPivot.Position - oldPivot.Position;
        var rotationDelta = newPivot.Rotation * Quaternion.Inverse(oldPivot.Rotation);

        if (_editorState.TransformPivot == TransformPivot.Individual)
        {
            // Individual mode: each actor rotates around its own center
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
            // Median/Parent mode: all actors rotate around the pivot point
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

    private void CreateActorUndoAction()
    {
        if (_actorDragStartTransforms == null || _actorDragStartTransforms.Count == 0)
            return;

        var actions = new List<IHistoryAction>();

        foreach (var (actor, startTransform) in _actorDragStartTransforms)
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
