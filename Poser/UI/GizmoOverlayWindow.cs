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

        // Calculate gizmo position and orientation based on pivot and orientation modes
        var (pivotPosition, pivotOrientation) = CalculateBonePivot(selectedBones, primaryBone);
        var gizmoTransform = new Transform(pivotPosition, pivotOrientation, Vector3.One);

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
            // Expand virtual bones when capturing initial state
            var expandedForHistory = ExpandVirtualBones(selectedBones);
            foreach (var bone in expandedForHistory)
            {
                var mod = _bonePosingService.GetModification(bone);
                _boneDragStartModifications[bone] = mod ?? new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.Zero };
            }
            // Emit drag start event for history recording
            _eventBus.Publish(new TransformDragStartedEvent(expandedForHistory.Cast<IEntity>().ToList()));
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

        // Determine ImGuizmo mode based on orientation
        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Global
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;

        Transform? newTransform = null;

        // Draw gizmo
        if (ImGuizmo.Manipulate(
            ref worldViewMatrix,
            ref projectionMatrix,
            gizmoOperation,
            gizmoMode,
            ref lastMatrix))
        {
            newTransform = Transform.FromMatrix(lastMatrix);
            _lastFrameBoneGizmo = newTransform;
        }

        // Apply transform if changed (only if frozen)
        if (newTransform != null && isFrozen)
        {
            // Expand virtual bones to their constituent bones
            // (deduplicates pivot bones to prevent double-transform when category + its bone are both selected)
            var expandedBones = ExpandVirtualBones(selectedBones);

            // Apply transform based on pivot mode
            ApplyBonePivotTransform(expandedBones, skeleton, lastObserved, newTransform.Value);
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

    private (Vector3 position, Quaternion rotation) CalculateBonePivot(List<IBone> selectedBones, IBone primaryBone)
    {
        // Calculate pivot position based on TransformPivot
        Vector3 pivotPosition = _editorState.TransformPivot switch
        {
            TransformPivot.Local => primaryBone.LastTransform.Position,
            TransformPivot.Parent => primaryBone.ParentBone?.LastTransform.Position
                                     ?? primaryBone.LastTransform.Position,
            TransformPivot.Average => CalculateAveragePosition(selectedBones),
            _ => primaryBone.LastTransform.Position
        };

        // Calculate pivot orientation based on TransformOrientation
        Quaternion pivotOrientation = _editorState.TransformOrientation switch
        {
            TransformOrientation.Global => Quaternion.Identity,
            TransformOrientation.Local => primaryBone.LastTransform.Rotation,
            TransformOrientation.Parent => primaryBone.ParentBone?.LastTransform.Rotation
                                           ?? primaryBone.LastTransform.Rotation,
            _ => primaryBone.LastTransform.Rotation
        };

        return (pivotPosition, pivotOrientation);
    }

    private static Vector3 CalculateAveragePosition(List<IBone> bones)
    {
        if (bones.Count == 0)
            return Vector3.Zero;

        var average = Vector3.Zero;
        foreach (var bone in bones)
        {
            average += bone.LastTransform.Position;
        }
        return average / bones.Count;
    }

    private void ApplyBonePivotTransform(List<IBone> bones, Skeleton skeleton, Transform oldPivot, Transform newPivot)
    {
        // Calculate deltas from pivot transform
        var positionDelta = newPivot.Position - oldPivot.Position;
        var rotationDelta = newPivot.Rotation * Quaternion.Inverse(oldPivot.Rotation);

        // Scale should never propagate to children
        const TransformComponents propagate = TransformComponents.Position | TransformComponents.Rotation;

        // Build set of selected bone names for symmetry deduplication
        var selectedBoneNames = new HashSet<string>(bones.Select(b => b.BoneName));

        // For single bone with Local pivot, apply transform directly
        if (_editorState.TransformPivot == TransformPivot.Local && bones.Count == 1)
        {
            var bone = bones[0];
            _bonePosingService.ApplyTransform(bone, newPivot, oldPivot, propagate, accumulate: true);

            // Apply symmetry if enabled
            ApplySymmetryTransform(bone, skeleton, selectedBoneNames, newPivot, oldPivot, propagate);
            return;
        }

        // For multi-bone selection:
        // - Translation: apply position delta directly to each bone
        // - Rotation: apply rotation delta to each bone's rotation (no position orbiting)
        // This avoids drift when rotating parent-child bones together
        foreach (var bone in bones)
        {
            var newPosition = bone.LastTransform.Position + positionDelta;
            var newRotation = rotationDelta * bone.LastTransform.Rotation;

            var newBoneTransform = new Transform(newPosition, newRotation, bone.LastTransform.Scale);
            _bonePosingService.ApplyTransform(bone, newBoneTransform, bone.LastTransform, propagate, accumulate: true);

            // Apply symmetry if enabled
            ApplySymmetryTransform(bone, skeleton, selectedBoneNames, newBoneTransform, bone.LastTransform, propagate);
        }
    }

    /// <summary>
    /// Apply symmetry transform to the paired bone (if exists and not already selected).
    /// </summary>
    private void ApplySymmetryTransform(IBone bone, Skeleton skeleton, HashSet<string> selectedBoneNames,
        Transform newTransform, Transform oldTransform, TransformComponents propagate)
    {
        if (_editorState.SymmetryMode == SymmetryMode.Off)
            return;

        // Find paired bone name (swap _l <-> _r)
        var pairedName = GetPairedBoneName(bone.BoneName);
        if (pairedName == null)
            return;

        // Skip if paired bone is already selected (will be transformed directly)
        if (selectedBoneNames.Contains(pairedName))
            return;

        // Find the paired bone in the skeleton
        var pairedBone = skeleton.Bones.FirstOrDefault(b => b.BoneName == pairedName);
        if (pairedBone == null)
            return;

        // Calculate deltas from the original bone's transform
        var positionDelta = newTransform.Position - oldTransform.Position;
        var rotationDelta = newTransform.Rotation * Quaternion.Inverse(oldTransform.Rotation);

        Transform symmetryTransform;
        if (_editorState.SymmetryMode == SymmetryMode.Copy)
        {
            // Copy: apply same delta to paired bone
            symmetryTransform = new Transform(
                pairedBone.LastTransform.Position + positionDelta,
                Quaternion.Normalize(rotationDelta * pairedBone.LastTransform.Rotation),
                pairedBone.LastTransform.Scale);
        }
        else // Mirror
        {
            // Mirror: invert X, Y, and Z deltas
            var mirroredPositionDelta = new Vector3(-positionDelta.X, -positionDelta.Y, -positionDelta.Z);

            // Rotation delta: invert X, Y, and Z, keep W
            var mirroredRotationDelta = new Quaternion(-rotationDelta.X, -rotationDelta.Y, -rotationDelta.Z, rotationDelta.W);

            symmetryTransform = new Transform(
                pairedBone.LastTransform.Position + mirroredPositionDelta,
                Quaternion.Normalize(mirroredRotationDelta * pairedBone.LastTransform.Rotation),
                pairedBone.LastTransform.Scale);
        }

        _bonePosingService.ApplyTransform(pairedBone, symmetryTransform, pairedBone.LastTransform, propagate, accumulate: true);
    }

    /// <summary>
    /// Get the paired bone name by swapping _l <-> _r suffix.
    /// Returns null if bone has no pair.
    /// </summary>
    private static string? GetPairedBoneName(string boneName)
    {
        if (boneName.EndsWith("_l"))
            return boneName[..^2] + "_r";
        if (boneName.EndsWith("_r"))
            return boneName[..^2] + "_l";
        return null;
    }

    private (Vector3 position, Quaternion rotation) CalculateActorPivot(List<IActor> selectedActors, IActor primaryActor)
    {
        // Calculate pivot position based on TransformPivot
        Vector3 pivotPosition = _editorState.TransformPivot switch
        {
            TransformPivot.Local => _posingService.GetEffectiveTransform(primaryActor).Position,
            TransformPivot.Parent => _posingService.GetEffectiveTransform(primaryActor).Position, // Actors don't have parents
            TransformPivot.Average => CalculateActorAveragePosition(selectedActors),
            _ => _posingService.GetEffectiveTransform(primaryActor).Position
        };

        // Calculate pivot orientation based on TransformOrientation
        Quaternion pivotRotation = _editorState.TransformOrientation switch
        {
            TransformOrientation.Global => Quaternion.Identity,
            TransformOrientation.Local => _posingService.GetEffectiveTransform(primaryActor).Rotation,
            TransformOrientation.Parent => _posingService.GetEffectiveTransform(primaryActor).Rotation, // Actors don't have parents
            _ => _posingService.GetEffectiveTransform(primaryActor).Rotation
        };

        return (pivotPosition, pivotRotation);
    }

    private Vector3 CalculateActorAveragePosition(List<IActor> actors)
    {
        if (actors.Count == 0)
            return Vector3.Zero;

        var average = Vector3.Zero;
        foreach (var actor in actors)
        {
            average += _posingService.GetEffectiveTransform(actor).Position;
        }
        return average / actors.Count;
    }

    private void ApplyActorPivotTransform(List<IActor> selectedActors, IActor primaryActor, Transform oldPivot, Transform newPivot)
    {
        // Calculate deltas
        var positionDelta = newPivot.Position - oldPivot.Position;
        var rotationDelta = newPivot.Rotation * Quaternion.Inverse(oldPivot.Rotation);

        if (_editorState.TransformPivot == TransformPivot.Local)
        {
            // Local mode: each actor rotates around its own center
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
            // Average/Parent mode: all actors rotate around the pivot point
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
    /// Expands virtual bones to their pivot bone only (not all constituents).
    /// Regular bones are passed through unchanged.
    /// Deduplicates: if a VirtualBone's PivotBone is also directly selected, skip it.
    /// </summary>
    private static List<IBone> ExpandVirtualBones(IReadOnlyList<IBone> selectedBones)
    {
        // Collect pivot bones from VirtualBones for deduplication
        var pivotBones = new HashSet<IBone>();
        foreach (var bone in selectedBones)
        {
            if (bone is VirtualBone vb && vb.PivotBone != null)
                pivotBones.Add(vb.PivotBone);
        }

        var expandedBones = new List<IBone>();

        foreach (var bone in selectedBones)
        {
            if (bone is VirtualBone virtualBone)
            {
                // VirtualBone: only transform the pivot bone (e.g., "Head" → neck only)
                if (virtualBone.PivotBone != null)
                {
                    expandedBones.Add(virtualBone.PivotBone);
                }
                // If no pivot bone, this is an averaged category - skip transform
                // (user must select individual bones to transform)
            }
            else
            {
                // Regular bone - skip if it's a pivot bone of a selected VirtualBone
                // (already added via the VirtualBone above)
                if (!pivotBones.Contains(bone))
                    expandedBones.Add(bone);
            }
        }

        return expandedBones;
    }
}
