using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// What type of entity the gizmo is targeting.
/// </summary>
internal enum GizmoTargetType
{
    None,
    Actor,
    Bone
}

/// <summary>
/// Unified gizmo overlay window that handles both actor and bone transforms.
/// Injects services directly - reads state from services, calls methods on services.
/// Still uses IEventBus to publish drag start/end events for history recording.
/// </summary>
public class GizmoOverlayWindow : Window
{
    private readonly IEventBus _eventBus;
    private readonly ISelectionService _selectionService;
    private readonly IAnimationService _animationService;
    private readonly IEditorState _editorState;
    private readonly ICameraService _cameraService;
    private readonly IPosingService _posingService;
    private readonly IBonePosingService _bonePosingService;

    /// <summary>Arbitrary unique ID for ImGuizmo to track gizmo state.</summary>
    private const int GizmoId = 142857;

    // Actor gizmo state
    private Dictionary<IActor, Transform>? _actorDragStartTransforms;

    // Bone gizmo state
    private Transform? _lastFrameBoneGizmo;
    private Dictionary<IBone, Transform>? _boneDragStartModifications;

    public GizmoOverlayWindow(
        IEventBus eventBus,
        ISelectionService selectionService,
        IAnimationService animationService,
        IEditorState editorState,
        ICameraService cameraService,
        IPosingService posingService,
        IBonePosingService bonePosingService)
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
        _eventBus = eventBus;
        _selectionService = selectionService;
        _animationService = animationService;
        _editorState = editorState;
        _cameraService = cameraService;
        _posingService = posingService;
        _bonePosingService = bonePosingService;

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
        // Setup ImGuizmo once per frame
        ImGuizmo.BeginFrame();
        var io = ImGui.GetIO();
        ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        ImGuizmo.SetDrawlist();

        var targetType = GetGizmoTargetType();

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

    private GizmoTargetType GetGizmoTargetType()
    {
        var selectedBone = _selectionService.GetFirstSelected<IBone>();
        if (selectedBone != null)
            return GizmoTargetType.Bone;

        var selectedActor = _selectionService.GetFirstSelected<IActor>();
        if (selectedActor != null)
            return GizmoTargetType.Actor;

        return GizmoTargetType.None;
    }

    private void DrawActorGizmo()
    {
        var selectedActors = _selectionService.GetSelected<IActor>().ToList();
        if (selectedActors.Count == 0)
            return;

        var primaryActor = selectedActors[0];

        var viewMatrix = _cameraService.GetViewMatrix();
        var projectionMatrix = _cameraService.GetProjectionMatrix();

        // Calculate pivot position based on mode
        var (pivotPosition, pivotRotation) = CalculateActorPivot(selectedActors, primaryActor);
        var pivotTransform = new Transform(pivotPosition, pivotRotation, Vector3.One);
        var modelMatrix = pivotTransform.ToMatrix();

        // Always enable gizmo for actor manipulation (works with or without freeze)
        ImGuizmo.Enable(true);

        var viewMatrixCopy = viewMatrix;

        // Check if we're starting a new drag operation
        bool isUsing = ImGuizmo.IsUsing();
        if (isUsing && _actorDragStartTransforms == null)
        {
            // Starting to drag - store initial transforms for ALL selected actors
            _actorDragStartTransforms = new Dictionary<IActor, Transform>();
            foreach (var actor in selectedActors)
            {
                _actorDragStartTransforms[actor] = _posingService.GetEffectiveTransform(actor);
            }
            // Emit drag start event for history recording
            _eventBus.Publish(new TransformDragStartedEvent(selectedActors.Cast<IEntity>().ToList()));
        }

        // Determine gizmo mode based on transform orientation
        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Local
            ? ImGuizmoMode.Local
            : ImGuizmoMode.World;

        // Get the gizmo operation based on selected tool
        var gizmoOperation = _editorState.TransformTool switch
        {
            TransformTool.Move => ImGuizmoOperation.Translate,
            TransformTool.Rotate => ImGuizmoOperation.Rotate,
            TransformTool.Scale => ImGuizmoOperation.Scale,
            TransformTool.Universal => ImGuizmoOperation.Translate | ImGuizmoOperation.Rotate | ImGuizmoOperation.Scale,
            _ => ImGuizmoOperation.Translate
        };

        // Draw gizmo
        if (ImGuizmo.Manipulate(
            ref viewMatrixCopy,
            ref projectionMatrix,
            gizmoOperation,
            gizmoMode,
            ref modelMatrix))
        {
            var newPivotTransform = Transform.FromMatrix(modelMatrix);
            ApplyActorPivotTransform(selectedActors, primaryActor, pivotTransform, newPivotTransform);
        }

        // Check if we finished dragging
        if (!isUsing && _actorDragStartTransforms != null)
        {
            // Emit drag end event for history recording
            _eventBus.Publish(new TransformDragEndedEvent());
            _actorDragStartTransforms = null;
        }
    }

    private void DrawBoneGizmo()
    {
        var selectedBones = _selectionService.GetSelected<IBone>().ToList();
        if (selectedBones.Count == 0)
            return;

        // Use the first selected bone as the primary for gizmo positioning
        var primaryBone = selectedBones[0];
        var skeleton = primaryBone.Skeleton as Skeleton;
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

        // Calculate gizmo position based on pivot mode
        Transform gizmoTransform;
        if (selectedBones.Count == 1 || _editorState.TransformPivot == TransformPivot.Individual)
        {
            // Single bone or individual pivot - use primary bone's transform
            gizmoTransform = primaryBone.LastTransform;
        }
        else
        {
            // Multiple bones with median pivot - calculate average position
            var averagePosition = Vector3.Zero;
            foreach (var bone in selectedBones)
            {
                averagePosition += bone.LastTransform.Position;
            }
            averagePosition /= selectedBones.Count;

            // Use primary bone's rotation for gizmo orientation
            gizmoTransform = new Transform
            {
                Position = averagePosition,
                Rotation = primaryBone.LastTransform.Rotation,
                Scale = Vector3.One
            };
        }

        // Use last observed, or current if not tracking yet
        var lastObserved = _lastFrameBoneGizmo ?? gizmoTransform;
        var lastMatrix = lastObserved.ToMatrix();

        // Enable gizmo only if actor is frozen
        ImGuizmo.Enable(isFrozen);

        bool isUsing = ImGuizmo.IsUsing();

        // Capture for history on drag start (only if frozen)
        if (isUsing && _boneDragStartModifications == null && isFrozen)
        {
            _boneDragStartModifications = new Dictionary<IBone, Transform>();
            foreach (var bone in selectedBones)
            {
                var mod = _bonePosingService.GetModification(bone);
                _boneDragStartModifications[bone] = mod ?? new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.Zero };
            }
            // Emit drag start event for history recording
            _eventBus.Publish(new TransformDragStartedEvent(selectedBones.Cast<IEntity>().ToList()));
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
            // Filter to only "root" bones - bones that don't have an ancestor also in the selection.
            // This prevents double-applying transforms (e.g., rotating arm also rotates elbow/wrist
            // through the hierarchy, so we shouldn't also rotate elbow/wrist directly).
            var rootBones = GetSelectionRootBones(selectedBones);

            // Apply transform only to root bones
            // Gizmo passes incremental deltas (lastObserved changes each frame), so ACCUMULATE
            foreach (var bone in rootBones)
            {
                _bonePosingService.ApplyTransform(bone, newTransform.Value, lastObserved, TransformComponents.All, accumulate: true);
            }
        }

        // Finish drag - emit event for history
        if (_lastFrameBoneGizmo.HasValue && !isUsing)
        {
            if (_boneDragStartModifications != null && _boneDragStartModifications.Count > 0)
            {
                // Emit drag end event for history recording
                _eventBus.Publish(new TransformDragEndedEvent());
            }

            _lastFrameBoneGizmo = null;
            _boneDragStartModifications = null;
        }
    }

    private (Vector3 position, Quaternion rotation) CalculateActorPivot(List<IActor> selectedActors, IActor primaryActor)
    {
        // First calculate the pivot position based on TransformPivot
        Vector3 pivotPosition;
        switch (_editorState.TransformPivot)
        {
            case TransformPivot.Median:
                // Average position of all selected actors
                var averagePosition = Vector3.Zero;
                int actorCount = 0;
                foreach (var actor in selectedActors)
                {
                    averagePosition += _posingService.GetEffectiveTransform(actor).Position;
                    actorCount++;
                }
                if (actorCount > 0)
                    averagePosition /= actorCount;
                pivotPosition = averagePosition;
                break;

            case TransformPivot.Individual:
            case TransformPivot.Parent:
            default:
                // Use primary actor's position for gizmo display
                pivotPosition = _posingService.GetEffectiveTransform(primaryActor).Position;
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
                pivotRotation = _posingService.GetEffectiveTransform(primaryActor).Rotation;
                break;
        }

        return (pivotPosition, pivotRotation);
    }

    private void ApplyActorPivotTransform(List<IActor> selectedActors, IActor primaryActor, Transform oldPivot, Transform newPivot)
    {
        // Calculate deltas
        var positionDelta = newPivot.Position - oldPivot.Position;
        var rotationDelta = newPivot.Rotation * Quaternion.Inverse(oldPivot.Rotation);

        if (_editorState.TransformPivot == TransformPivot.Individual)
        {
            // Individual mode: each actor rotates around its own center
            foreach (var actor in selectedActors)
            {
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
            foreach (var actor in selectedActors)
            {
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

    public override void PostDraw()
    {
        ImGuizmo.SetID(0);
        base.PostDraw();
    }

    /// <summary>
    /// Filters a list of selected bones to only include "root" bones -
    /// bones that don't have an ancestor also in the selection.
    /// This prevents double-applying transforms through the bone hierarchy.
    /// </summary>
    private static List<IBone> GetSelectionRootBones(IReadOnlyList<IBone> selectedBones)
    {
        if (selectedBones.Count <= 1)
            return selectedBones.ToList();

        var selectedSet = new HashSet<IBone>(selectedBones);
        var rootBones = new List<IBone>();

        foreach (var bone in selectedBones)
        {
            // Check if any ancestor of this bone is also selected
            bool hasSelectedAncestor = false;
            var parent = bone.ParentBone;
            while (parent != null)
            {
                if (selectedSet.Contains(parent))
                {
                    hasSelectedAncestor = true;
                    break;
                }
                parent = parent.ParentBone;
            }

            // Only include if no ancestor is selected
            if (!hasSelectedAncestor)
            {
                rootBones.Add(bone);
            }
        }

        return rootBones;
    }
}
