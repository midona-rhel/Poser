using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// Overlay window that draws skeleton bones on screen for bone selection.
/// Gizmo manipulation is handled by GizmoOverlayWindow.
/// </summary>
public class SkeletonOverlayWindow : Window
{
    private readonly IActorManager _actorManager;
    private readonly ICameraService _cameraService;
    private readonly ISkeletonService _skeletonService;
    private readonly IBonePosingService _bonePosingService;
    private readonly IEditorState _editorState;

    // Configuration
    private const float BoneCircleSize = 4f;
    private const float SelectedBoneCircleSize = 6f;
    private const float LineThickness = 1.5f;

    // Overlap detection (as fraction of screen height)
    private const float OverlapThreshold = 0.01f; // 1% of screen height - bones closer than this overlap

    // Context menu state for overlapping bone selection
    private List<BoneDisplayData>? _contextMenuBones;
    private int _contextMenuIndex;
    private Vector2 _contextMenuPos;

    // Colors
    private static readonly Vector4 LineColorVec = new(1.0f, 1.0f, 1.0f, 0.5f);
    private static readonly Vector4 DotColorVec = new(1.0f, 1.0f, 1.0f, 0.9f);
    private static readonly Vector4 DotOutlineColorVec = new(0.0f, 0.0f, 0.0f, 0.8f);
    private static readonly Vector4 HoveredDotColorVec = new(1.0f, 0.9f, 0.5f, 1.0f);
    private static readonly Vector4 SelectedDotColorVec = new(0.3f, 0.7f, 1.0f, 1.0f);
    private static readonly Vector4 ModifiedDotColorVec = new(1.0f, 0.5f, 0.3f, 1.0f);
    private static readonly Vector4 TextColorVec = new(1.0f, 1.0f, 1.0f, 1.0f);
    private static readonly Vector4 TextOutlineColorVec = new(0.0f, 0.0f, 0.0f, 1.0f);

    // Bone display data
    private class BoneDisplayData
    {
        public IBone Bone { get; init; } = null!;
        public Vector2 OriginalScreenPosition { get; init; }
        public Vector2 DisplayPosition { get; set; }
        public Vector2? ParentDisplayPosition { get; set; }
        public float Size { get; init; }
        public bool IsHovered { get; set; }
        public bool IsSelected { get; set; }
        public bool IsModified { get; set; }
        public int HierarchyDepth { get; init; }
    }

    public SkeletonOverlayWindow(
        IActorManager actorManager,
        ICameraService cameraService,
        ISkeletonService skeletonService,
        IBonePosingService bonePosingService,
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
    }

    public override void Draw()
    {
        var drawList = ImGui.GetWindowDrawList();
        var viewportPos = ImGui.GetMainViewport().Pos;
        var io = ImGui.GetIO();
        var screenHeight = io.DisplaySize.Y;
        var mousePos = io.MousePos;

        var bones = new List<BoneDisplayData>();

        // Collect all bones and their screen positions from actors with visible skeletons
        foreach (var actor in _actorManager.Actors)
        {
            var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
            if (skeleton == null || !skeleton.IsValid)
                continue;

            // Show skeleton overlay only when edit mode is enabled
            if (!actor.IsEditMode)
                continue;

            // Update bone transforms from game memory before drawing
            skeleton.UpdateBoneTransforms();

            // Register skeleton for cache updates in FinalizeSkeletons hook
            _bonePosingService.RegisterSkeletonForCacheUpdate(skeleton);

            // Get model matrix for world-space conversion
            var modelMatrix = skeleton.GetModelMatrix();

            // First pass: collect original screen positions
            var boneScreenPositions = new Dictionary<IBone, Vector2>();
            var boneDepths = new Dictionary<IBone, int>();

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
                    boneDepths[bone] = GetHierarchyDepth(bone);
                }
            }

            // Create display data for each bone
            foreach (var (bone, screenPos) in boneScreenPositions)
            {
                var isSelected = _editorState.SelectedBone == bone;
                var isModified = _bonePosingService.HasModifications(bone);

                Vector2? parentScreenPos = null;
                if (bone.ParentBone != null && boneScreenPositions.TryGetValue(bone.ParentBone, out var parentPos))
                {
                    parentScreenPos = parentPos;
                }

                bones.Add(new BoneDisplayData
                {
                    Bone = bone,
                    OriginalScreenPosition = screenPos,
                    DisplayPosition = screenPos,
                    ParentDisplayPosition = parentScreenPos,
                    Size = isSelected ? SelectedBoneCircleSize : BoneCircleSize,
                    IsSelected = isSelected,
                    IsModified = isModified,
                    HierarchyDepth = boneDepths[bone]
                });
            }
        }

        // Update hover state
        UpdateHoverState(bones, mousePos);

        // Handle right-click context menu for overlapping bones
        HandleBoneContextMenu(bones, mousePos, screenHeight);

        // Draw lines first (behind dots)
        DrawBoneLines(drawList, bones);

        // Draw dots on top
        DrawBoneDots(drawList, bones);

        // Draw bone name tooltip for hovered bone (not if context menu open)
        if (_contextMenuBones == null)
        {
            DrawBoneTooltip(drawList, bones);
        }

        // Draw context menu if open
        DrawBoneContextMenu(drawList);
    }

    private int GetHierarchyDepth(IBone bone)
    {
        int depth = 0;
        var current = bone.ParentBone;
        while (current != null)
        {
            depth++;
            current = current.ParentBone;
        }
        return depth;
    }

    private void HandleBoneContextMenu(List<BoneDisplayData> bones, Vector2 mousePos, float screenHeight)
    {
        var overlapThreshold = screenHeight * OverlapThreshold;
        var io = ImGui.GetIO();

        // Handle context menu interactions
        if (_contextMenuBones != null)
        {
            // Scroll to change selection
            if (io.MouseWheel != 0)
            {
                _contextMenuIndex -= (int)io.MouseWheel;
                _contextMenuIndex = Math.Clamp(_contextMenuIndex, 0, _contextMenuBones.Count - 1);
            }

            // Left-click to confirm selection
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var selectedBone = _contextMenuBones[_contextMenuIndex];
                _editorState.Select(selectedBone.Bone);
                _contextMenuBones = null;
                return;
            }

            // Right-click elsewhere or Escape to close
            if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            {
                _contextMenuBones = null;
                return;
            }

            // Close if mouse moved far from menu
            if (Vector2.Distance(mousePos, _contextMenuPos) > screenHeight * 0.15f)
            {
                _contextMenuBones = null;
                return;
            }

            return; // Don't process new right-clicks while menu is open
        }

        // Left-click to select bones
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            // Find bone under cursor
            BoneDisplayData? clickedBone = null;
            foreach (var bone in bones)
            {
                var dist = Vector2.Distance(bone.DisplayPosition, mousePos);
                if (dist <= (bone.Size + 2f) * ImGuiHelpers.GlobalScale)
                {
                    clickedBone = bone;
                    break;
                }
            }

            if (clickedBone != null)
            {
                // Find all overlapping bones at this position
                var overlapping = new List<BoneDisplayData> { clickedBone };
                foreach (var other in bones)
                {
                    if (other.Bone == clickedBone.Bone)
                        continue;

                    if (Vector2.Distance(clickedBone.OriginalScreenPosition, other.OriginalScreenPosition) < overlapThreshold)
                    {
                        overlapping.Add(other);
                    }
                }

                if (overlapping.Count > 1)
                {
                    // Multiple bones - open context menu
                    _contextMenuBones = overlapping.OrderBy(b => b.HierarchyDepth).ThenBy(b => b.Bone.Name).ToList();
                    _contextMenuIndex = 0;
                    _contextMenuPos = mousePos;
                }
                else
                {
                    // Single bone - select directly
                    _editorState.Select(clickedBone.Bone);
                }
            }
        }
    }

    private void DrawBoneContextMenu(ImDrawListPtr drawList)
    {
        if (_contextMenuBones == null || _contextMenuBones.Count == 0)
            return;

        var padding = 8f * ImGuiHelpers.GlobalScale;
        var lineHeight = ImGui.GetTextLineHeightWithSpacing();
        var maxWidth = 0f;

        foreach (var bone in _contextMenuBones)
        {
            var textSize = ImGui.CalcTextSize(bone.Bone.Name);
            maxWidth = Math.Max(maxWidth, textSize.X);
        }

        var menuSize = new Vector2(maxWidth + padding * 2 + 20f * ImGuiHelpers.GlobalScale, _contextMenuBones.Count * lineHeight + padding * 2);
        var menuPos = _contextMenuPos + new Vector2(10f, -menuSize.Y / 2) * ImGuiHelpers.GlobalScale;

        // Background
        var bgColor = ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 0.95f));
        var borderColor = ImGui.GetColorU32(new Vector4(0.4f, 0.4f, 0.4f, 1f));
        drawList.AddRectFilled(menuPos, menuPos + menuSize, bgColor, 4f);
        drawList.AddRect(menuPos, menuPos + menuSize, borderColor, 4f);

        // Items
        var itemPos = menuPos + new Vector2(padding, padding);

        for (int i = 0; i < _contextMenuBones.Count; i++)
        {
            var bone = _contextMenuBones[i];
            var isHighlighted = i == _contextMenuIndex;

            // Highlight selected item
            if (isHighlighted)
            {
                var highlightColor = ImGui.GetColorU32(new Vector4(0.3f, 0.5f, 0.8f, 0.6f));
                drawList.AddRectFilled(
                    itemPos - new Vector2(padding / 2, 0),
                    itemPos + new Vector2(menuSize.X - padding * 1.5f, lineHeight),
                    highlightColor, 2f);
            }

            // Dot indicator
            var dotColor = bone.IsModified
                ? ImGui.GetColorU32(ModifiedDotColorVec)
                : ImGui.GetColorU32(DotColorVec);
            drawList.AddCircleFilled(
                itemPos + new Vector2(4f, lineHeight / 2) * ImGuiHelpers.GlobalScale,
                3f * ImGuiHelpers.GlobalScale, dotColor);

            // Bone name
            var textColor = isHighlighted
                ? ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f))
                : ImGui.GetColorU32(new Vector4(0.8f, 0.8f, 0.8f, 1f));
            drawList.AddText(itemPos + new Vector2(14f, 0) * ImGuiHelpers.GlobalScale, textColor, bone.Bone.Name);

            itemPos.Y += lineHeight;
        }

        // Hint text
        var hintText = "Scroll to select, Left-click to confirm";
        var hintSize = ImGui.CalcTextSize(hintText);
        var hintPos = menuPos + new Vector2((menuSize.X - hintSize.X) / 2, menuSize.Y + 4f * ImGuiHelpers.GlobalScale);
        drawList.AddText(hintPos, ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)), hintText);
    }

    private void UpdateHoverState(List<BoneDisplayData> bones, Vector2 mousePos)
    {
        foreach (var bone in bones)
        {
            var scaledSize = (bone.Size + 2f) * ImGuiHelpers.GlobalScale;
            var distSq = Vector2.DistanceSquared(mousePos, bone.DisplayPosition);

            bone.IsHovered = distSq <= scaledSize * scaledSize;
        }
    }

    private void DrawBoneLines(ImDrawListPtr drawList, List<BoneDisplayData> bones)
    {
        var lineColor = ImGui.GetColorU32(LineColorVec);

        foreach (var bone in bones)
        {
            if (bone.ParentDisplayPosition == null)
                continue;

            var scaledThickness = LineThickness * ImGuiHelpers.GlobalScale;
            drawList.AddLine(bone.ParentDisplayPosition.Value, bone.DisplayPosition, lineColor, scaledThickness);
        }
    }

    private void DrawBoneDots(ImDrawListPtr drawList, List<BoneDisplayData> bones)
    {
        var dotColor = ImGui.GetColorU32(DotColorVec);
        var outlineColor = ImGui.GetColorU32(DotOutlineColorVec);
        var hoveredColor = ImGui.GetColorU32(HoveredDotColorVec);
        var selectedColor = ImGui.GetColorU32(SelectedDotColorVec);
        var modifiedColor = ImGui.GetColorU32(ModifiedDotColorVec);

        foreach (var bone in bones)
        {
            var scaledSize = bone.Size * ImGuiHelpers.GlobalScale;

            // Determine color based on state
            uint color;
            if (bone.IsSelected)
                color = selectedColor;
            else if (bone.IsHovered)
                color = hoveredColor;
            else if (bone.IsModified)
                color = modifiedColor;
            else
                color = dotColor;

            // Draw outline
            drawList.AddCircle(bone.DisplayPosition, scaledSize + 1, outlineColor, 12, 1.5f * ImGuiHelpers.GlobalScale);

            // Draw filled circle for selected/hovered, outline for others
            if (bone.IsSelected || bone.IsHovered)
                drawList.AddCircleFilled(bone.DisplayPosition, scaledSize, color, 12);
            else
                drawList.AddCircle(bone.DisplayPosition, scaledSize, color, 12, 1.5f * ImGuiHelpers.GlobalScale);
        }
    }

    private void DrawBoneTooltip(ImDrawListPtr drawList, List<BoneDisplayData> bones)
    {
        // Find hovered bone
        BoneDisplayData? hoveredBone = null;
        foreach (var bone in bones)
        {
            if (bone.IsHovered)
            {
                hoveredBone = bone;
                break;
            }
        }

        if (hoveredBone == null)
            return;

        var boneName = hoveredBone.Bone.Name;
        var textPos = hoveredBone.DisplayPosition + new Vector2(10f, -8f) * ImGuiHelpers.GlobalScale;

        // Draw text with black outline (draw black text offset in 4 directions, then white on top)
        var textColor = ImGui.GetColorU32(TextColorVec);
        var outlineColor = ImGui.GetColorU32(TextOutlineColorVec);
        var outlineOffset = 1f * ImGuiHelpers.GlobalScale;

        // Outline (8 directions for better coverage)
        drawList.AddText(textPos + new Vector2(-outlineOffset, 0), outlineColor, boneName);
        drawList.AddText(textPos + new Vector2(outlineOffset, 0), outlineColor, boneName);
        drawList.AddText(textPos + new Vector2(0, -outlineOffset), outlineColor, boneName);
        drawList.AddText(textPos + new Vector2(0, outlineOffset), outlineColor, boneName);
        drawList.AddText(textPos + new Vector2(-outlineOffset, -outlineOffset), outlineColor, boneName);
        drawList.AddText(textPos + new Vector2(outlineOffset, -outlineOffset), outlineColor, boneName);
        drawList.AddText(textPos + new Vector2(-outlineOffset, outlineOffset), outlineColor, boneName);
        drawList.AddText(textPos + new Vector2(outlineOffset, outlineOffset), outlineColor, boneName);

        // Main text
        drawList.AddText(textPos, textColor, boneName);
    }
}
