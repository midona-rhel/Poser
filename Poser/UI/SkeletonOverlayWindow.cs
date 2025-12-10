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
    private readonly ISelectionService _selectionService;
    private readonly IEditorState _editorState;

    // Configuration
    private const float BoneCircleSize = 4f;
    private const float SelectedBoneCircleSize = 6f;
    private const float JointCircleSize = 8f;
    private const float LineThickness = 1.5f;
    private const float OctahedraWidth = 4f;
    private const float PivotPointSize = 8f;
    private const float PivotPointSelectedSize = 10f;

    // Overlap detection (as fraction of screen height)
    private const float OverlapThreshold = 0.01f; // 1% of screen height - bones closer than this overlap

    // Context menu state for overlapping bone selection
    private List<BoneDisplayData>? _contextMenuBones;
    private int _contextMenuIndex;
    private Vector2 _contextMenuPos;

    // Static colors (line/outline)
    private static readonly Vector4 LineColorVec = new(1.0f, 1.0f, 1.0f, 0.5f);
    private static readonly Vector4 DotColorVec = new(1.0f, 1.0f, 1.0f, 0.9f);
    private static readonly Vector4 DotOutlineColorVec = new(0.0f, 0.0f, 0.0f, 0.8f);
    private static readonly Vector4 TextColorVec = new(1.0f, 1.0f, 1.0f, 1.0f);
    private static readonly Vector4 TextOutlineColorVec = new(0.0f, 0.0f, 0.0f, 1.0f);
    private static readonly Vector4 PivotPointColorVec = new(1.0f, 0.5f, 0.0f, 0.9f); // Orange
    private static readonly Vector4 PivotPointSelectedColorVec = new(1.0f, 0.8f, 0.0f, 1.0f); // Yellow-orange

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
        public bool IsSymmetryPair { get; set; }
        public int HierarchyDepth { get; init; }
    }

    public SkeletonOverlayWindow(
        IActorManager actorManager,
        ICameraService cameraService,
        ISkeletonService skeletonService,
        IBonePosingService bonePosingService,
        ISelectionService selectionService,
        IEditorState editorState)
        : base("##poser_skeleton_overlay",
            ImGuiWindowFlags.NoBackground |
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoMove |
            ImGuiWindowFlags.NoScrollbar |
            ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoBringToFrontOnFocus)
    {
        _actorManager = actorManager;
        _cameraService = cameraService;
        _skeletonService = skeletonService;
        _bonePosingService = bonePosingService;
        _selectionService = selectionService;
        _editorState = editorState;

        RespectCloseHotkey = false;
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
    /// Inverts hue of a color while keeping same brightness.
    /// </summary>
    private static Vector4 GetInverseColor(Vector4 color)
    {
        // Simple complementary color: invert RGB components
        // To maintain brightness, we use (1-R, 1-G, 1-B) but adjust to keep luminance similar
        float luminance = 0.299f * color.X + 0.587f * color.Y + 0.114f * color.Z;
        var inverted = new Vector4(1f - color.X, 1f - color.Y, 1f - color.Z, color.W);
        float invertedLuminance = 0.299f * inverted.X + 0.587f * inverted.Y + 0.114f * inverted.Z;

        // Scale to match original luminance
        if (invertedLuminance > 0.001f)
        {
            float scale = luminance / invertedLuminance;
            inverted.X = Math.Min(1f, inverted.X * scale);
            inverted.Y = Math.Min(1f, inverted.Y * scale);
            inverted.Z = Math.Min(1f, inverted.Z * scale);
        }

        return inverted;
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
        // Use background draw list so skeleton renders behind all other windows
        var drawList = ImGui.GetBackgroundDrawList();
        var viewportPos = ImGui.GetMainViewport().Pos;
        var io = ImGui.GetIO();
        var screenHeight = io.DisplaySize.Y;
        var mousePos = io.MousePos;

        var selectedBone = _selectionService.GetFirstSelected<IBone>();
        var bones = new List<BoneDisplayData>();

        // Build set of selected bone names for symmetry pair detection
        var selectedBoneNames = new HashSet<string>();
        if (selectedBone != null)
        {
            selectedBoneNames.Add(selectedBone.BoneName);
        }

        // Build set of symmetry pair names
        var symmetryPairNames = new HashSet<string>();
        if (_editorState.SymmetryMode != SymmetryMode.Off)
        {
            foreach (var name in selectedBoneNames)
            {
                var pairedName = GetPairedBoneName(name);
                if (pairedName != null)
                {
                    symmetryPairNames.Add(pairedName);
                }
            }
        }

        // Collect all bones and their screen positions from actors with visible skeletons
        foreach (var actor in _actorManager.Actors)
        {
            var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
            if (skeleton == null || !skeleton.IsValid)
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
                // Skip hidden bones (internal) and invisible bones (user toggled off)
                if (bone.IsHiddenBone || !bone.IsVisible)
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
                var isSelected = selectedBone == bone;
                var isSymmetryPair = symmetryPairNames.Contains(bone.BoneName);

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
                    IsSymmetryPair = isSymmetryPair,
                    HierarchyDepth = boneDepths[bone]
                });
            }
        }

        // Update hover state
        UpdateHoverState(bones, mousePos);

        // Filter bones if ShowSelectedBonesOnly is enabled
        if (_editorState.ShowSelectedBonesOnly)
        {
            bones = bones.Where(b => b.IsSelected || b.IsSymmetryPair || b.IsHovered).ToList();
        }

        // Handle right-click context menu for overlapping bones
        HandleBoneContextMenu(bones, mousePos, screenHeight);

        // Draw based on view mode
        switch (_editorState.SkeletonViewMode)
        {
            case SkeletonViewMode.Dots:
                DrawBoneLines(drawList, bones);
                DrawBoneDots(drawList, bones);
                break;
            case SkeletonViewMode.Octahedra:
                DrawBoneOctahedra(drawList, bones);
                DrawBoneDots(drawList, bones);
                break;
            case SkeletonViewMode.Joints:
                DrawBoneJoints(drawList, bones);
                break;
        }

        // Draw bone name tooltip for hovered bone (not if context menu open)
        if (_contextMenuBones == null)
        {
            DrawBoneTooltip(drawList, bones);
        }

        // Draw context menu if open
        DrawBoneContextMenu(drawList);

        // Draw pivot points
        DrawPivotPoints(drawList, viewportPos, mousePos);
    }

    private void DrawPivotPoints(ImDrawListPtr drawList, Vector2 viewportPos, Vector2 mousePos)
    {
        var pivotPoints = _editorState.PivotPoints;
        if (pivotPoints.Count == 0)
            return;

        var pivotColor = ImGui.GetColorU32(PivotPointColorVec);
        var pivotSelectedColor = ImGui.GetColorU32(PivotPointSelectedColorVec);
        var outlineColor = ImGui.GetColorU32(DotOutlineColorVec);

        foreach (var pivot in pivotPoints)
        {
            // Convert world position to screen
            if (!_cameraService.WorldToScreen(pivot.WorldPosition, out var screenPos))
                continue;

            var finalScreenPos = viewportPos + screenPos;
            var isSelected = pivot == _editorState.OrbitTarget;
            var size = (isSelected ? PivotPointSelectedSize : PivotPointSize) * ImGuiHelpers.GlobalScale;

            // Check hover
            var isHovered = Vector2.Distance(finalScreenPos, mousePos) <= size + 2f;

            // Handle click
            if (isHovered)
            {
                ImGui.SetNextFrameWantCaptureMouse(true);

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    _editorState.OrbitTarget = pivot;
                    _editorState.TransformPivot = TransformPivot.Target;
                }
                else if (ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                {
                    // Right-click to delete
                    _editorState.DeletePivotPoint(pivot);
                    return; // Exit since collection changed
                }
            }

            // Draw pivot point as diamond/cross shape
            var color = isSelected ? pivotSelectedColor : (isHovered ? pivotSelectedColor : pivotColor);

            // Diamond shape
            var halfSize = size * 0.7f;
            var top = finalScreenPos + new Vector2(0, -halfSize);
            var right = finalScreenPos + new Vector2(halfSize, 0);
            var bottom = finalScreenPos + new Vector2(0, halfSize);
            var left = finalScreenPos + new Vector2(-halfSize, 0);

            // Filled diamond
            drawList.AddQuadFilled(top, right, bottom, left, color);

            // Outline
            drawList.AddQuad(top, right, bottom, left, outlineColor, 2f * ImGuiHelpers.GlobalScale);

            // Draw line to parent bone if parented
            if (pivot.ParentBone != null)
            {
                var parentWorldPos = pivot.ParentBone.LastTransform.Position;

                // Need to transform through model matrix - find the skeleton
                foreach (var actor in _actorManager.Actors)
                {
                    var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
                    if (skeleton == null || !skeleton.IsValid)
                        continue;

                    if (skeleton.Bones.Contains(pivot.ParentBone))
                    {
                        var modelMatrix = skeleton.GetModelMatrix();
                        var parentWorld = Vector3.Transform(parentWorldPos, modelMatrix);

                        if (_cameraService.WorldToScreen(parentWorld, out var parentScreenPos))
                        {
                            var parentFinalPos = viewportPos + parentScreenPos;
                            var lineColor = ImGui.GetColorU32(PivotPointColorVec with { W = 0.5f });
                            drawList.AddLine(parentFinalPos, finalScreenPos, lineColor, 1.5f * ImGuiHelpers.GlobalScale);
                        }
                        break;
                    }
                }
            }

            // Draw name tooltip if hovered
            if (isHovered)
            {
                var textPos = finalScreenPos + new Vector2(size + 4f, -8f);
                var textColor = ImGui.GetColorU32(TextColorVec);
                var textOutline = ImGui.GetColorU32(TextOutlineColorVec);
                var name = pivot.Name;

                // Outline
                drawList.AddText(textPos + new Vector2(-1, 0), textOutline, name);
                drawList.AddText(textPos + new Vector2(1, 0), textOutline, name);
                drawList.AddText(textPos + new Vector2(0, -1), textOutline, name);
                drawList.AddText(textPos + new Vector2(0, 1), textOutline, name);

                // Text
                drawList.AddText(textPos, textColor, name);
            }
        }
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
            // Capture mouse while context menu is open
            ImGui.SetNextFrameWantCaptureMouse(true);

            // Scroll to change selection
            if (io.MouseWheel != 0)
            {
                _contextMenuIndex -= (int)io.MouseWheel;
                _contextMenuIndex = Math.Clamp(_contextMenuIndex, 0, _contextMenuBones.Count - 1);
            }

            // Left-click to confirm selection
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                var selectedBoneData = _contextMenuBones[_contextMenuIndex];
                _selectionService.Select(selectedBoneData.Bone);
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

            return; // Don't process new clicks while menu is open
        }

        // Find bone under cursor using manual distance check (NoInputs prevents IsMouseHoveringRect)
        BoneDisplayData? hoveredBone = null;
        foreach (var bone in bones)
        {
            var radius = (bone.Size + 2f) * ImGuiHelpers.GlobalScale;
            var dist = Vector2.Distance(bone.DisplayPosition, mousePos);

            if (dist <= radius)
            {
                hoveredBone = bone;
                break;
            }
        }

        // If hovering a bone, capture mouse for next frame and handle clicks
        if (hoveredBone != null)
        {
            // Tell ImGui we want mouse input next frame (prevents click-through)
            ImGui.SetNextFrameWantCaptureMouse(true);

            // Left-click to select
            if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                // Find all overlapping bones at this position
                var overlapping = new List<BoneDisplayData> { hoveredBone };
                foreach (var other in bones)
                {
                    if (other.Bone == hoveredBone.Bone)
                        continue;

                    if (Vector2.Distance(hoveredBone.OriginalScreenPosition, other.OriginalScreenPosition) < overlapThreshold)
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
                    _selectionService.Select(hoveredBone.Bone);
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
            var dotIndicatorColor = bone.IsSelected
                ? ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.TabActive])
                : ImGui.GetColorU32(DotColorVec);
            drawList.AddCircleFilled(
                itemPos + new Vector2(4f, lineHeight / 2) * ImGuiHelpers.GlobalScale,
                3f * ImGuiHelpers.GlobalScale, dotIndicatorColor);

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

        // Get colors from ImGui style (tab colors)
        var selectedColorVec = ImGui.GetStyle().Colors[(int)ImGuiCol.TabActive];
        var hoveredColorVec = ImGui.GetStyle().Colors[(int)ImGuiCol.TabHovered];
        var symmetryColorVec = GetInverseColor(selectedColorVec);

        var selectedColor = ImGui.GetColorU32(selectedColorVec);
        var hoveredColor = ImGui.GetColorU32(hoveredColorVec);
        var symmetryColor = ImGui.GetColorU32(symmetryColorVec);

        foreach (var bone in bones)
        {
            var scaledSize = bone.Size * ImGuiHelpers.GlobalScale;

            // Determine color based on state (priority: selected > hovered > symmetry > default)
            uint color;
            if (bone.IsSelected)
                color = selectedColor;
            else if (bone.IsHovered)
                color = hoveredColor;
            else if (bone.IsSymmetryPair)
                color = symmetryColor;
            else
                color = dotColor;

            // Draw outline
            drawList.AddCircle(bone.DisplayPosition, scaledSize + 1, outlineColor, 12, 1.5f * ImGuiHelpers.GlobalScale);

            // Draw filled circle for selected/hovered/symmetry, outline for others
            if (bone.IsSelected || bone.IsHovered || bone.IsSymmetryPair)
                drawList.AddCircleFilled(bone.DisplayPosition, scaledSize, color, 12);
            else
                drawList.AddCircle(bone.DisplayPosition, scaledSize, color, 12, 1.5f * ImGuiHelpers.GlobalScale);
        }
    }

    private void DrawBoneOctahedra(ImDrawListPtr drawList, List<BoneDisplayData> bones)
    {
        var outlineColor = ImGui.GetColorU32(DotOutlineColorVec);

        // Get colors from ImGui style (tab colors)
        var selectedColorVec = ImGui.GetStyle().Colors[(int)ImGuiCol.TabActive];
        var hoveredColorVec = ImGui.GetStyle().Colors[(int)ImGuiCol.TabHovered];
        var symmetryColorVec = GetInverseColor(selectedColorVec);
        var defaultColorVec = DotColorVec with { W = 0.6f }; // Semi-transparent white

        foreach (var bone in bones)
        {
            if (bone.ParentDisplayPosition == null)
                continue;

            // Determine color based on state
            Vector4 colorVec;
            if (bone.IsSelected)
                colorVec = selectedColorVec;
            else if (bone.IsHovered)
                colorVec = hoveredColorVec;
            else if (bone.IsSymmetryPair)
                colorVec = symmetryColorVec;
            else
                colorVec = defaultColorVec;

            var fillColor = ImGui.GetColorU32(colorVec with { W = colorVec.W * 0.5f });
            var edgeColor = ImGui.GetColorU32(colorVec);

            // Draw octahedron from parent to bone (diamond shape)
            var start = bone.ParentDisplayPosition.Value;
            var end = bone.DisplayPosition;
            var direction = end - start;
            var length = direction.Length();

            if (length < 1f) continue;

            var mid = (start + end) / 2;
            var perpendicular = Vector2.Normalize(new Vector2(-direction.Y, direction.X));
            var width = OctahedraWidth * ImGuiHelpers.GlobalScale;

            var left = mid + perpendicular * width;
            var right = mid - perpendicular * width;

            // Draw filled triangles (diamond shape)
            drawList.AddTriangleFilled(start, left, end, fillColor);
            drawList.AddTriangleFilled(start, right, end, fillColor);

            // Draw outline
            drawList.AddLine(start, left, edgeColor, 1.5f * ImGuiHelpers.GlobalScale);
            drawList.AddLine(left, end, edgeColor, 1.5f * ImGuiHelpers.GlobalScale);
            drawList.AddLine(end, right, edgeColor, 1.5f * ImGuiHelpers.GlobalScale);
            drawList.AddLine(right, start, edgeColor, 1.5f * ImGuiHelpers.GlobalScale);
        }
    }

    private void DrawBoneJoints(ImDrawListPtr drawList, List<BoneDisplayData> bones)
    {
        var outlineColor = ImGui.GetColorU32(DotOutlineColorVec);

        // Get colors from ImGui style (tab colors)
        var selectedColorVec = ImGui.GetStyle().Colors[(int)ImGuiCol.TabActive];
        var hoveredColorVec = ImGui.GetStyle().Colors[(int)ImGuiCol.TabHovered];
        var symmetryColorVec = GetInverseColor(selectedColorVec);

        var selectedColor = ImGui.GetColorU32(selectedColorVec);
        var hoveredColor = ImGui.GetColorU32(hoveredColorVec);
        var symmetryColor = ImGui.GetColorU32(symmetryColorVec);
        var dotColor = ImGui.GetColorU32(DotColorVec);

        foreach (var bone in bones)
        {
            // Use larger joint size
            var scaledSize = JointCircleSize * ImGuiHelpers.GlobalScale;
            if (bone.IsSelected)
                scaledSize = (JointCircleSize + 2f) * ImGuiHelpers.GlobalScale;

            // Determine color based on state
            uint color;
            if (bone.IsSelected)
                color = selectedColor;
            else if (bone.IsHovered)
                color = hoveredColor;
            else if (bone.IsSymmetryPair)
                color = symmetryColor;
            else
                color = dotColor;

            // Draw outline
            drawList.AddCircle(bone.DisplayPosition, scaledSize + 1, outlineColor, 16, 2f * ImGuiHelpers.GlobalScale);

            // Draw filled circle (always filled for joints mode)
            drawList.AddCircleFilled(bone.DisplayPosition, scaledSize, color, 16);
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
