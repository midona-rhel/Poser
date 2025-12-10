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
/// Simple delta-based system like Brio - bones rotate around themselves.
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

    private const int GizmoId = 142857;

    // Actor gizmo state
    private Dictionary<IActor, Transform>? _actorDragStartTransforms;

    // Bone gizmo state - tracks transform per bone for delta calculation
    private Dictionary<IBone, Transform>? _boneTrackingTransforms;

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

        var actorTransform = _posingService.GetEffectiveTransform(primaryActor);
        var modelMatrix = actorTransform.ToMatrix();

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

        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Global
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;

        var gizmoOperation = GetGizmoOperation();

        if (ImGuizmo.Manipulate(ref viewMatrixCopy, ref projectionMatrix, gizmoOperation, gizmoMode, ref modelMatrix))
        {
            var newTransform = Transform.FromMatrix(modelMatrix);
            _posingService.SetTransformOverride(primaryActor, newTransform);
        }

        if (!isUsing && _actorDragStartTransforms != null)
        {
            _eventBus.Publish(new TransformDragEndedEvent());
            _actorDragStartTransforms = null;
        }
    }

    private void DrawBoneGizmo()
    {
        // Get all selected bones and virtual bones
        var selectedBones = _selectionService.GetSelected<IBone>().ToList();
        if (selectedBones.Count == 0)
            return;

        // Separate VirtualBones from regular bones
        var virtualBones = selectedBones.OfType<VirtualBone>().ToList();
        var regularBones = selectedBones.Where(b => b is not VirtualBone).ToList();
        var explicitlySelectedNames = regularBones.Select(b => b.BoneName).ToHashSet();

        // Expand virtual bones to their constituent bones
        // Track by bone name to handle deduplication
        var addedBoneNames = new HashSet<string>();
        var expandedBones = new List<IBone>();

        foreach (var vb in virtualBones)
        {
            foreach (var constituent in vb.ConstituentBones)
            {
                if (addedBoneNames.Add(constituent.BoneName))
                    expandedBones.Add(constituent);
            }
        }

        // Add regular bones that aren't already covered by a VirtualBone (by name)
        foreach (var bone in regularBones)
        {
            if (addedBoneNames.Add(bone.BoneName))
                expandedBones.Add(bone);
        }

        if (expandedBones.Count == 0)
            return;

        // Filter to root bones for VirtualBone constituents only
        // Explicitly selected bones are always transformed, even if their parent is also selected
        var rootBones = expandedBones.Where(b =>
        {
            // Always include explicitly selected bones
            if (explicitlySelectedNames.Contains(b.BoneName))
                return true;

            // For VirtualBone constituents, only include if parent is not in selection
            return b.ParentBone == null || !addedBoneNames.Contains(b.ParentBone.BoneName);
        }).ToList();

        // Use first selected bone for gizmo display (could be VirtualBone's pivot or first regular)
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

        // Get primary bone's ACTUAL current transform for gizmo - always use real position, not cached
        var currentTransform = primaryBone.LastTransform;

        // Track per-bone for drag events only (not for baseline calculation)
        if (_boneTrackingTransforms == null)
        {
            _boneTrackingTransforms = new Dictionary<IBone, Transform>();
        }

        // Gizmo always uses current actual position - no stale baseline
        var lastMatrix = currentTransform.ToMatrix();

        ImGuizmo.Enable(isFrozen);

        bool isUsing = ImGuizmo.IsUsing();

        // Publish drag start event
        if (isUsing && _boneTrackingTransforms.Count == 0 && isFrozen)
        {
            foreach (var bone in rootBones)
            {
                _boneTrackingTransforms[bone] = bone.LastTransform;
            }
            _boneTrackingTransforms[primaryBone] = currentTransform;
            _eventBus.Publish(new TransformDragStartedEvent(rootBones.Cast<IEntity>().ToList()));
        }

        var gizmoMode = _editorState.TransformOrientation == TransformOrientation.Global
            ? ImGuizmoMode.World
            : ImGuizmoMode.Local;

        var gizmoOperation = GetGizmoOperation();

        if (ImGuizmo.Manipulate(ref worldViewMatrix, ref projectionMatrix, gizmoOperation, gizmoMode, ref lastMatrix))
        {
            var newTransform = Transform.FromMatrix(lastMatrix);

            if (isFrozen)
            {
                // Primary bone: use ACTUAL current position as baseline (not cached)
                _bonePosingService.ApplyTransform(primaryBone, newTransform, currentTransform);

                // Secondary bones: apply same relative change using their ACTUAL positions
                foreach (var bone in rootBones.Where(b => b != primaryBone))
                {
                    var boneCurrentTransform = bone.LastTransform;  // ACTUAL position, not cached
                    var boneNewTransform = CalculateSecondaryBoneTransform(
                        currentTransform, newTransform, boneCurrentTransform);
                    _bonePosingService.ApplyTransform(bone, boneNewTransform, boneCurrentTransform);
                }

                // Apply symmetry transforms using actual positions
                ApplySymmetryTransforms(primaryBone, currentTransform, newTransform, skeleton, addedBoneNames);
            }
        }

        // End drag
        if (_boneTrackingTransforms.Count > 0 && !isUsing)
        {
            _eventBus.Publish(new TransformDragEndedEvent());
            _boneTrackingTransforms.Clear();
        }
    }

    /// <summary>
    /// Calculate the new transform for a secondary bone based on primary bone's change.
    /// Uses Brio's formulas: additive position/scale, Conjugate multiplication for rotation.
    /// </summary>
    private static Transform CalculateSecondaryBoneTransform(
        Transform primaryBefore, Transform primaryAfter, Transform secondaryBefore)
    {
        // Position: add the same offset (ADDITIVE)
        var positionDelta = primaryAfter.Position - primaryBefore.Position;

        // Rotation: apply the same rotation delta using Brio's formula
        // Conjugate(before) * after gives the relative rotation
        var rotationDelta = Quaternion.Normalize(
            Quaternion.Conjugate(primaryBefore.Rotation) * primaryAfter.Rotation);

        // Scale: add the same scale change (ADDITIVE, not ratio!)
        var scaleDelta = primaryAfter.Scale - primaryBefore.Scale;

        return new Transform
        {
            Position = secondaryBefore.Position + positionDelta,
            Rotation = Quaternion.Normalize(secondaryBefore.Rotation * rotationDelta),
            Scale = secondaryBefore.Scale + scaleDelta
        };
    }

    /// <summary>
    /// Gets the paired bone name for symmetry (swaps _l/_r suffix).
    /// </summary>
    private static string? GetPairedBoneName(string boneName)
    {
        if (boneName.EndsWith("_l")) return boneName[..^2] + "_r";
        if (boneName.EndsWith("_r")) return boneName[..^2] + "_l";
        return null;
    }

    /// <summary>
    /// Apply symmetry transforms to the paired bone.
    /// </summary>
    private void ApplySymmetryTransforms(
        IBone primaryBone,
        Transform primaryBefore,
        Transform primaryAfter,
        Skeleton skeleton,
        HashSet<string> allSelectedBoneNames)
    {
        if (_editorState.SymmetryMode == SymmetryMode.Off)
            return;

        var pairedName = GetPairedBoneName(primaryBone.BoneName);
        if (pairedName == null)
            return;

        // Don't apply if paired bone is already selected
        if (allSelectedBoneNames.Contains(pairedName))
            return;

        var pairedBone = skeleton.Bones.FirstOrDefault(b => b.BoneName == pairedName);
        if (pairedBone == null)
            return;

        // Use ACTUAL current position, not cached
        var pairedCurrentTransform = pairedBone.LastTransform;

        Transform pairedNewTransform;
        if (_editorState.SymmetryMode == SymmetryMode.Copy)
        {
            // Copy: apply the same change
            pairedNewTransform = CalculateSecondaryBoneTransform(
                primaryBefore, primaryAfter, pairedCurrentTransform);
        }
        else
        {
            // Mirror: apply mirrored change
            var mirroredAfter = MirrorTransform(primaryBefore, primaryAfter);
            pairedNewTransform = CalculateSecondaryBoneTransform(
                primaryBefore, mirroredAfter, pairedCurrentTransform);
        }

        _bonePosingService.ApplyTransform(pairedBone, pairedNewTransform, pairedCurrentTransform);
    }

    /// <summary>
    /// Mirror a transform change for symmetry mode.
    /// </summary>
    private static Transform MirrorTransform(Transform before, Transform after)
    {
        // Mirror position delta: negate X
        var positionDelta = after.Position - before.Position;
        var mirroredPositionDelta = new Vector3(-positionDelta.X, positionDelta.Y, positionDelta.Z);

        // Mirror rotation using Brio's approach: Conjugate the rotation delta
        var rotationDelta = Quaternion.Normalize(
            Quaternion.Conjugate(before.Rotation) * after.Rotation);
        var mirroredRotationDelta = Quaternion.Conjugate(rotationDelta);

        return new Transform
        {
            Position = before.Position + mirroredPositionDelta,
            Rotation = Quaternion.Normalize(before.Rotation * mirroredRotationDelta),
            Scale = after.Scale // Scale doesn't need mirroring
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
}
