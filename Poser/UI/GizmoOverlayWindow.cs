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
using Poser.UI.Gizmo;
using Poser.UI.Gizmo.Helpers;
using Poser.UI.Gizmo.Pivot;

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
/// Delegates to pivot strategies for transform application.
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

    // Pivot strategies
    private readonly LocalPivotStrategy _localPivot;
    private readonly ParentPivotStrategy _parentPivot;
    private readonly AveragePivotStrategy _averagePivot;
    private readonly TargetPivotStrategy _targetPivot;
    private readonly BoneSymmetryHandler _symmetryHandler;

    private const int GizmoId = 142857;

    // Actor gizmo state
    private Dictionary<IActor, Transform>? _actorDragStartTransforms;

    // Bone gizmo state
    private Transform? _lastFrameBoneGizmo;
    private readonly DragState _dragState = new();

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

        // Initialize strategies
        _symmetryHandler = new BoneSymmetryHandler(editorState, bonePosingService);
        _localPivot = new LocalPivotStrategy(bonePosingService);
        _parentPivot = new ParentPivotStrategy(bonePosingService);
        _averagePivot = new AveragePivotStrategy(bonePosingService);
        _targetPivot = new TargetPivotStrategy(bonePosingService, editorState);

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
        }
    }

    public override void PostDraw()
    {
        ImGuizmo.SetID(0);
        base.PostDraw();
    }

    private GizmoTargetType GetGizmoTargetType()
    {
        if (_selectionService.GetFirstSelected<IBone>() != null)
            return GizmoTargetType.Bone;
        if (_selectionService.GetFirstSelected<IActor>() != null)
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

        var (pivotPosition, pivotRotation) = CalculateActorPivot(selectedActors, primaryActor);
        var pivotTransform = new Transform(pivotPosition, pivotRotation, Vector3.One);
        var modelMatrix = pivotTransform.ToMatrix();

        ImGuizmo.Enable(true);
        var viewMatrixCopy = viewMatrix;

        bool isUsing = ImGuizmo.IsUsing();
        if (isUsing && _actorDragStartTransforms == null)
        {
            _actorDragStartTransforms = new Dictionary<IActor, Transform>();
            foreach (var actor in selectedActors)
                _actorDragStartTransforms[actor] = _posingService.GetEffectiveTransform(actor);
            _eventBus.Publish(new TransformDragStartedEvent(selectedActors.Cast<IEntity>().ToList()));
        }

        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Local
            ? ImGuizmoMode.Local
            : ImGuizmoMode.World;

        var gizmoOperation = GetGizmoOperation();

        if (ImGuizmo.Manipulate(ref viewMatrixCopy, ref projectionMatrix, gizmoOperation, gizmoMode, ref modelMatrix))
        {
            var newPivotTransform = Transform.FromMatrix(modelMatrix);
            ApplyActorPivotTransform(selectedActors, primaryActor, pivotTransform, newPivotTransform);
        }

        if (!isUsing && _actorDragStartTransforms != null)
        {
            _eventBus.Publish(new TransformDragEndedEvent());
            _actorDragStartTransforms = null;
        }
    }

    private void DrawBoneGizmo()
    {
        var selectedBones = _selectionService.GetSelected<IBone>().ToList();
        if (selectedBones.Count == 0)
            return;

        var primaryBone = selectedBones[0];
        var skeleton = primaryBone.Skeleton as Skeleton;
        if (skeleton == null || !skeleton.IsValid)
            return;

        var actor = skeleton.Actor;
        bool isFrozen = _animationService.IsFrozen(actor);
        _bonePosingService.RegisterSkeletonForCacheUpdate(skeleton);

        var projectionMatrix = _cameraService.GetProjectionMatrix();
        var worldViewMatrix = _cameraService.GetViewMatrix();
        worldViewMatrix.M44 = 1;

        var modelMatrix = skeleton.GetModelMatrix();
        worldViewMatrix = Matrix4x4.Multiply(modelMatrix, worldViewMatrix);

        var (pivotPosition, pivotOrientation) = CalculateBonePivot(selectedBones, primaryBone);
        var gizmoTransform = new Transform(pivotPosition, pivotOrientation, Vector3.One);

        var lastObserved = _lastFrameBoneGizmo ?? gizmoTransform;
        var lastMatrix = lastObserved.ToMatrix();

        ImGuizmo.Enable(isFrozen);

        bool isUsing = ImGuizmo.IsUsing();

        // Capture drag start state
        if (isUsing && !_dragState.IsActive && isFrozen)
        {
            CaptureDragStartState(selectedBones, skeleton, gizmoTransform);
        }

        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Global
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;

        var gizmoOperation = GetGizmoOperation();
        Transform? newTransform = null;

        if (ImGuizmo.Manipulate(ref worldViewMatrix, ref projectionMatrix, gizmoOperation, gizmoMode, ref lastMatrix))
        {
            newTransform = Transform.FromMatrix(lastMatrix);
            _lastFrameBoneGizmo = newTransform;
        }

        // Apply transform
        if (newTransform != null && isFrozen)
        {
            var expandedBones = VirtualBoneExpander.Expand(selectedBones);
            var selectedBoneNames = new HashSet<string>(expandedBones.Select(b => b.BoneName));

            var strategy = GetPivotStrategy();
            strategy.Apply(expandedBones, skeleton, lastObserved, newTransform.Value, _dragState, _symmetryHandler, selectedBoneNames);
        }

        // Finish drag
        if (_lastFrameBoneGizmo.HasValue && !isUsing)
        {
            if (_dragState.IsActive)
                _eventBus.Publish(new TransformDragEndedEvent());

            _lastFrameBoneGizmo = null;
            _dragState.Clear();
        }
    }

    private void CaptureDragStartState(List<IBone> selectedBones, Skeleton skeleton, Transform gizmoTransform)
    {
        _dragState.Initialize(gizmoTransform);

        var expandedForHistory = VirtualBoneExpander.Expand(selectedBones);

        // Capture relative positions, rotations, parent positions, and full transforms
        foreach (var bone in expandedForHistory)
        {
            if (CrossPartialHelper.IsCrossPartialBone(bone) && bone is Bone boneEntity)
            {
                // Use raw space for cross-partial bones
                var rawParentPos = CrossPartialHelper.GetEffectiveParentRawPosition(bone);
                _dragState.RelativePositions![bone] = boneEntity.LastRawTransform.Position - rawParentPos;
                _dragState.RelativeToGizmo![bone] = boneEntity.LastRawTransform.Position - gizmoTransform.Position;
                _dragState.BoneRotations![bone] = boneEntity.LastRawTransform.Rotation;
                _dragState.ParentPositions![bone] = rawParentPos;
                _dragState.BoneTransforms![bone] = boneEntity.LastRawTransform;
            }
            else
            {
                var parentPos = CrossPartialHelper.GetEffectiveParentPosition(bone);
                _dragState.RelativePositions![bone] = bone.LastTransform.Position - parentPos;
                _dragState.RelativeToGizmo![bone] = bone.LastTransform.Position - gizmoTransform.Position;
                _dragState.BoneRotations![bone] = bone.LastTransform.Rotation;
                _dragState.ParentPositions![bone] = parentPos;
                _dragState.BoneTransforms![bone] = bone.LastTransform;
            }
        }

        // Include paired bones for history
        var allBonesForHistory = new List<IBone>(expandedForHistory);
        allBonesForHistory.AddRange(_symmetryHandler.GetPairedBones(expandedForHistory, skeleton));

        foreach (var bone in allBonesForHistory)
        {
            var mod = _bonePosingService.GetModification(bone);
            _dragState.StartModifications![bone] = mod ?? new Transform { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.Zero };
        }

        _eventBus.Publish(new TransformDragStartedEvent(allBonesForHistory.Cast<IEntity>().ToList()));
    }

    private IPivotStrategy GetPivotStrategy()
    {
        return _editorState.TransformPivot switch
        {
            TransformPivot.Local => _localPivot,
            TransformPivot.Parent => _parentPivot,
            TransformPivot.Average => _averagePivot,
            TransformPivot.Target => _targetPivot,
            _ => _localPivot
        };
    }

    private ImGuizmoOperation GetGizmoOperation()
    {
        return _editorState.TransformTool switch
        {
            TransformTool.Move => ImGuizmoOperation.Translate,
            TransformTool.Rotate => ImGuizmoOperation.Rotate,
            TransformTool.Scale => ImGuizmoOperation.Scale,
            TransformTool.Universal => ImGuizmoOperation.Translate | ImGuizmoOperation.Rotate | ImGuizmoOperation.Scale,
            _ => ImGuizmoOperation.Rotate
        };
    }

    private (Vector3 position, Quaternion rotation) CalculateBonePivot(List<IBone> selectedBones, IBone primaryBone)
    {
        Vector3 pivotPosition = _editorState.TransformPivot switch
        {
            TransformPivot.Local => primaryBone.LastTransform.Position,
            TransformPivot.Parent => CrossPartialHelper.GetEffectiveParentPosition(primaryBone),
            TransformPivot.Average => CalculateAveragePosition(selectedBones),
            TransformPivot.Target => GetTargetPivotPosition(primaryBone),
            _ => primaryBone.LastTransform.Position
        };

        Quaternion pivotOrientation = _editorState.TransformOrientation switch
        {
            TransformOrientation.Global => Quaternion.Identity,
            TransformOrientation.Local => primaryBone.LastTransform.Rotation,
            TransformOrientation.Parent => CrossPartialHelper.GetEffectiveParentRotation(primaryBone),
            _ => primaryBone.LastTransform.Rotation
        };

        return (pivotPosition, pivotOrientation);
    }

    private Vector3 GetTargetPivotPosition(IBone fallbackBone)
    {
        var orbitTarget = _editorState.OrbitTarget;
        return orbitTarget?.Transform.Position ?? fallbackBone.LastTransform.Position;
    }

    private static Vector3 CalculateAveragePosition(List<IBone> bones)
    {
        if (bones.Count == 0)
            return Vector3.Zero;

        var average = Vector3.Zero;
        foreach (var bone in bones)
            average += bone.LastTransform.Position;
        return average / bones.Count;
    }

    private (Vector3 position, Quaternion rotation) CalculateActorPivot(List<IActor> selectedActors, IActor primaryActor)
    {
        Vector3 pivotPosition = _editorState.TransformPivot switch
        {
            TransformPivot.Local => _posingService.GetEffectiveTransform(primaryActor).Position,
            TransformPivot.Parent => _posingService.GetEffectiveTransform(primaryActor).Position,
            TransformPivot.Average => CalculateActorAveragePosition(selectedActors),
            TransformPivot.Target => _editorState.OrbitTarget?.Transform.Position ?? _posingService.GetEffectiveTransform(primaryActor).Position,
            _ => _posingService.GetEffectiveTransform(primaryActor).Position
        };

        Quaternion pivotRotation = _editorState.TransformOrientation switch
        {
            TransformOrientation.Global => Quaternion.Identity,
            TransformOrientation.Local => _posingService.GetEffectiveTransform(primaryActor).Rotation,
            TransformOrientation.Parent => _posingService.GetEffectiveTransform(primaryActor).Rotation,
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
            average += _posingService.GetEffectiveTransform(actor).Position;
        return average / actors.Count;
    }

    private void ApplyActorPivotTransform(List<IActor> selectedActors, IActor primaryActor, Transform oldPivot, Transform newPivot)
    {
        var positionDelta = newPivot.Position - oldPivot.Position;
        var rotationDelta = newPivot.Rotation * Quaternion.Inverse(oldPivot.Rotation);

        if (_editorState.TransformPivot == TransformPivot.Local)
        {
            foreach (var actor in selectedActors)
            {
                var actorTransform = _posingService.GetEffectiveTransform(actor);
                actorTransform.Position += positionDelta;
                actorTransform.Rotation = rotationDelta * actorTransform.Rotation;
                _posingService.SetTransformOverride(actor, actorTransform);
            }
        }
        else
        {
            foreach (var actor in selectedActors)
            {
                var actorTransform = _posingService.GetEffectiveTransform(actor);
                var relativePos = actorTransform.Position - oldPivot.Position;
                var rotatedRelativePos = Vector3.Transform(relativePos, rotationDelta);
                actorTransform.Position = newPivot.Position + rotatedRelativePos;
                actorTransform.Rotation = rotationDelta * actorTransform.Rotation;
                _posingService.SetTransformOverride(actor, actorTransform);
            }
        }
    }
}
