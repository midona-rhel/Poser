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
/// Overlay window that draws skeleton bones on screen and handles bone posing.
/// </summary>
public class SkeletonOverlayWindow : Window
{
    private readonly IActorManager _actorManager;
    private readonly ICameraService _cameraService;
    private readonly ISkeletonService _skeletonService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IHistoryService _historyService;
    private readonly IEditorState _editorState;

    // Configuration
    private const float BoneCircleSize = 4f;
    private const float SelectedBoneCircleSize = 6f;
    private const float LineThickness = 1.5f;

    // Colors
    private static readonly uint LineColor = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.5f));
    private static readonly uint DotColor = ImGui.GetColorU32(new Vector4(1.0f, 1.0f, 1.0f, 0.9f));
    private static readonly uint DotOutlineColor = ImGui.GetColorU32(new Vector4(0.0f, 0.0f, 0.0f, 0.8f));
    private static readonly uint HoveredDotColor = ImGui.GetColorU32(new Vector4(1.0f, 0.9f, 0.5f, 1.0f));
    private static readonly uint SelectedDotColor = ImGui.GetColorU32(new Vector4(0.3f, 0.7f, 1.0f, 1.0f));
    private static readonly uint ModifiedDotColor = ImGui.GetColorU32(new Vector4(1.0f, 0.5f, 0.3f, 1.0f));

    // Gizmo state
    private const int GizmoId = 857142;
    private Transform? _trackingTransform;
    private Transform? _dragStartTransform;

    // Clickable bone tracking
    private class ClickableBone
    {
        public IBone Bone { get; init; } = null!;
        public Vector2 ScreenPosition { get; init; }
        public Vector2? ParentScreenPosition { get; init; }
        public float Size { get; init; }
        public bool IsHovered { get; set; }
        public bool IsSelected { get; set; }
        public bool IsModified { get; set; }
    }

    public SkeletonOverlayWindow(
        IActorManager actorManager,
        ICameraService cameraService,
        ISkeletonService skeletonService,
        IBonePosingService bonePosingService,
        IHistoryService historyService,
        IEditorState editorState)
        : base("##poser_skeleton_overlay",
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
        _skeletonService = skeletonService;
        _bonePosingService = bonePosingService;
        _historyService = historyService;
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
        var drawList = ImGui.GetWindowDrawList();
        var viewportPos = ImGui.GetMainViewport().Pos;
        var clickables = new List<ClickableBone>();

        // Collect all bones and their screen positions
        foreach (var actor in _actorManager.Actors)
        {
            var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
            if (skeleton == null || !skeleton.IsValid || !skeleton.IsOverlayVisible)
                continue;

            // Update bone transforms from game memory
            skeleton.UpdateBoneTransforms();

            // Get model matrix for world-space conversion
            var modelMatrix = skeleton.GetModelMatrix();

            // Collect screen positions for all bones
            var boneScreenPositions = new Dictionary<IBone, Vector2>();

            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsHiddenBone)
                    continue;

                // Transform bone position to world space
                var worldPos = Vector3.Transform(bone.LastTransform.Position, modelMatrix);

                // Convert to screen coordinates
                if (_cameraService.WorldToScreen(worldPos, out var screenPos))
                {
                    var finalScreenPos = viewportPos + screenPos;
                    boneScreenPositions[bone] = finalScreenPos;

                    var isSelected = _editorState.SelectedBone == bone;
                    var isModified = _bonePosingService.HasModifications(bone);

                    Vector2? parentScreenPos = null;
                    if (bone.ParentBone != null && boneScreenPositions.TryGetValue(bone.ParentBone, out var parentPos))
                    {
                        parentScreenPos = parentPos;
                    }

                    clickables.Add(new ClickableBone
                    {
                        Bone = bone,
                        ScreenPosition = finalScreenPos,
                        ParentScreenPosition = parentScreenPos,
                        Size = isSelected ? SelectedBoneCircleSize : BoneCircleSize,
                        IsSelected = isSelected,
                        IsModified = isModified
                    });
                }
            }
        }

        // Handle input for bone selection
        HandleBoneInput(clickables);

        // Draw lines first (behind dots)
        DrawBoneLines(drawList, clickables);

        // Draw dots on top
        DrawBoneDots(drawList, clickables);

        // Draw gizmo for selected bone
        DrawBoneGizmo();
    }

    private void HandleBoneInput(List<ClickableBone> clickables)
    {
        // With NoInputs flag, we check mouse position manually
        // Bone selection is done through the EntityList, not clicking on overlay
        // This method now only updates hover state for visual feedback

        var io = ImGui.GetIO();
        var mousePos = io.MousePos;

        // Check which bones are hovered (for visual feedback only)
        foreach (var clickable in clickables)
        {
            var scaledSize = (clickable.Size + 2f) * ImGuiHelpers.GlobalScale;
            var distSq = Vector2.DistanceSquared(mousePos, clickable.ScreenPosition);

            if (distSq <= scaledSize * scaledSize)
            {
                clickable.IsHovered = true;
            }
        }
    }

    private void DrawBoneLines(ImDrawListPtr drawList, List<ClickableBone> clickables)
    {
        foreach (var clickable in clickables)
        {
            if (clickable.ParentScreenPosition == null)
                continue;

            var scaledThickness = LineThickness * ImGuiHelpers.GlobalScale;
            drawList.AddLine(clickable.ParentScreenPosition.Value, clickable.ScreenPosition, LineColor, scaledThickness);
        }
    }

    private void DrawBoneDots(ImDrawListPtr drawList, List<ClickableBone> clickables)
    {
        foreach (var clickable in clickables)
        {
            var scaledSize = clickable.Size * ImGuiHelpers.GlobalScale;

            // Determine color based on state
            uint color;
            if (clickable.IsSelected)
                color = SelectedDotColor;
            else if (clickable.IsHovered)
                color = HoveredDotColor;
            else if (clickable.IsModified)
                color = ModifiedDotColor;
            else
                color = DotColor;

            // Draw outline
            drawList.AddCircle(clickable.ScreenPosition, scaledSize + 1, DotOutlineColor, 12, 1.5f * ImGuiHelpers.GlobalScale);

            // Draw filled circle
            if (clickable.IsSelected || clickable.IsHovered)
                drawList.AddCircleFilled(clickable.ScreenPosition, scaledSize, color, 12);
            else
                drawList.AddCircle(clickable.ScreenPosition, scaledSize, color, 12, 1.5f * ImGuiHelpers.GlobalScale);
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

        // Get camera matrices
        var viewMatrix = _cameraService.GetViewMatrix();
        var projectionMatrix = _cameraService.GetProjectionMatrix();

        // Get the bone's world transform
        var modelMatrix = skeleton.GetModelMatrix();
        var boneWorldPos = Vector3.Transform(selectedBone.LastTransform.Position, modelMatrix);

        // Calculate gizmo transform based on pivot mode
        var (pivotPosition, pivotRotation) = CalculateBonePivot(selectedBone, boneWorldPos, modelMatrix);
        var pivotTransform = new Transform
        {
            Position = pivotPosition,
            Rotation = pivotRotation,
            Scale = Vector3.One
        };
        var gizmoMatrix = pivotTransform.ToMatrix();

        // Setup ImGuizmo
        ImGuizmo.BeginFrame();
        var io = ImGui.GetIO();
        ImGuizmo.SetRect(0, 0, io.DisplaySize.X, io.DisplaySize.Y);
        ImGuizmo.SetOrthographic(false);
        ImGuizmo.AllowAxisFlip(false);
        ImGuizmo.SetDrawlist();
        ImGuizmo.Enable(true);

        var viewMatrixCopy = viewMatrix;

        // Track if we're starting a new drag
        bool isUsing = ImGuizmo.IsUsing();
        if (isUsing && _dragStartTransform == null)
        {
            _dragStartTransform = selectedBone.LastTransform;
            _trackingTransform = pivotTransform;
        }

        // Determine gizmo mode based on pivot mode
        var gizmoMode = _editorState.PivotMode == PivotMode.Local ? ImGuizmoMode.Local : ImGuizmoMode.World;

        // Draw rotate gizmo for bones (rotation is most common for posing)
        if (ImGuizmo.Manipulate(
            ref viewMatrixCopy,
            ref projectionMatrix,
            ImGuizmoOperation.Rotate,
            gizmoMode,
            ref gizmoMatrix))
        {
            var newPivotTransform = Transform.FromMatrix(gizmoMatrix);
            ApplyBoneGizmoTransform(selectedBone, pivotTransform, newPivotTransform, modelMatrix);
            _trackingTransform = newPivotTransform;
        }

        // Finish drag - create undo action
        if (!isUsing && _dragStartTransform != null)
        {
            var endTransform = selectedBone.LastTransform;
            if (_dragStartTransform.Value.Position != endTransform.Position ||
                _dragStartTransform.Value.Rotation != endTransform.Rotation ||
                _dragStartTransform.Value.Scale != endTransform.Scale)
            {
                var action = new TransformBoneAction(
                    _bonePosingService,
                    selectedBone,
                    _dragStartTransform.Value,
                    endTransform);
                _historyService.Record(action);
            }
            _dragStartTransform = null;
            _trackingTransform = null;
        }
    }

    private (Vector3 position, Quaternion rotation) CalculateBonePivot(IBone bone, Vector3 boneWorldPos, Matrix4x4 modelMatrix)
    {
        switch (_editorState.PivotMode)
        {
            case PivotMode.World:
                return (boneWorldPos, Quaternion.Identity);

            case PivotMode.Local:
            default:
                // Use bone's world rotation
                var boneWorldRot = Quaternion.CreateFromRotationMatrix(modelMatrix) * bone.LastTransform.Rotation;
                return (boneWorldPos, boneWorldRot);
        }
    }

    private void ApplyBoneGizmoTransform(IBone bone, Transform oldPivot, Transform newPivot, Matrix4x4 modelMatrix)
    {
        // Calculate rotation delta
        var rotationDelta = newPivot.Rotation * Quaternion.Inverse(oldPivot.Rotation);

        // Apply rotation to the bone
        _bonePosingService.ApplyRotation(bone, rotationDelta, propagate: true);
    }

    public override void PostDraw()
    {
        ImGuizmo.SetID(0);
        base.PostDraw();
    }
}
