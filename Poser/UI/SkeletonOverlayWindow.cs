using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Bindings.ImGuizmo;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// Overlay window that draws skeleton bones on screen for bone selection.
/// Visual style based on Ktisis - simple dots with lines, hover popup for overlapping bones.
/// </summary>
public class SkeletonOverlayWindow : Window
{
    private readonly IActorManager _actorManager;
    private readonly ICameraService _cameraService;
    private readonly ISkeletonService _skeletonService;
    private readonly IBonePosingService _bonePosingService;
    private readonly ISelectionService _selectionService;
    private readonly IEditorState _editorState;

    // Configuration (Ktisis-style values)
    private const float DotRadius = 3.4f;
    private const float LineThickness = 1.0f;
    private const float LineOpacity = 0.232f;
    private const float LineOpacityWhileUsing = 0.150f;
    private const float OctahedraWidth = 4f;

    // Colors (Ktisis defaults)
    private const uint BoneColor = 0xFFFF9F68; // Ktisis bone blue/orange (ABGR format)
    private const uint OutlineColor = 0xFF000000; // Black

    // Bone display data
    private class BoneDisplayData
    {
        public IBone Bone { get; init; } = null!;
        public Vector2 ScreenPos { get; init; }
        public Vector2? ParentScreenPos { get; init; }
        public float CameraDistance { get; init; }
        public bool IsHovered { get; set; }
        public bool IsSelected { get; set; }
    }

    // Hover list state (Ktisis-style)
    private List<BoneDisplayData>? _hoveredBones;
    private int _hoverIndex;

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
        var drawList = ImGui.GetBackgroundDrawList();
        var viewportPos = ImGui.GetMainViewport().Pos;
        var io = ImGui.GetIO();
        var mousePos = io.MousePos;

        var selectedBone = _selectionService.GetFirstSelected<IBone>();
        var bones = new List<BoneDisplayData>();

        // Collect all bones that project to screen successfully
        foreach (var actor in _actorManager.Actors)
        {
            var skeleton = _skeletonService.GetSkeleton(actor) as Skeleton;
            if (skeleton == null || !skeleton.IsValid)
                continue;

            skeleton.UpdateBoneTransforms();
            _bonePosingService.RegisterSkeletonForCacheUpdate(skeleton);

            var modelMatrix = skeleton.GetModelMatrix();
            var cameraPos = _cameraService.GetCameraPosition();

            // Collect screen positions
            var boneScreenPositions = new Dictionary<IBone, Vector2>();

            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsHiddenBone || !bone.IsVisible)
                    continue;

                var worldPos = Vector3.Transform(bone.LastTransform.Position, modelMatrix);

                // Only include bones that successfully project to screen
                if (_cameraService.WorldToScreen(worldPos, out var screenPos))
                {
                    boneScreenPositions[bone] = viewportPos + screenPos;
                }
            }

            // Create display data
            foreach (var (bone, screenPos) in boneScreenPositions)
            {
                Vector2? parentScreenPos = null;
                if (bone.ParentBone != null && boneScreenPositions.TryGetValue(bone.ParentBone, out var psp))
                {
                    parentScreenPos = psp;
                }

                var worldPos = Vector3.Transform(bone.LastTransform.Position, modelMatrix);
                var dist = Vector3.Distance(cameraPos, worldPos);

                bones.Add(new BoneDisplayData
                {
                    Bone = bone,
                    ScreenPos = screenPos,
                    ParentScreenPos = parentScreenPos,
                    CameraDistance = dist,
                    IsSelected = selectedBone == bone
                });
            }
        }

        // Update hover state
        UpdateHoverState(bones, mousePos);

        // Filter bones if ShowSelectedBonesOnly is enabled
        if (_editorState.ShowSelectedBonesOnly)
        {
            bones = bones.Where(b => b.IsSelected || b.IsHovered).ToList();
        }

        // Draw skeleton
        var isGizmoActive = ImGuizmo.IsUsing();
        var lineOpacity = isGizmoActive ? LineOpacityWhileUsing : LineOpacity;

        switch (_editorState.SkeletonViewMode)
        {
            case SkeletonViewMode.Default:
                DrawLines(drawList, bones, lineOpacity);
                DrawDots(drawList, bones);
                break;
            case SkeletonViewMode.Octahedra:
                DrawOctahedra(drawList, bones, lineOpacity);
                DrawDots(drawList, bones);
                break;
            case SkeletonViewMode.Joints:
                DrawJoints(drawList, bones);
                break;
        }

        // Update hovered bones list and draw hover window (Ktisis style)
        UpdateHoveredBones(bones);
        if (DrawHoverWindow(out var clickedBone) && clickedBone != null)
        {
            _selectionService.Select(clickedBone);
        }
    }

    private const int HoverPadding = 6;

    private void UpdateHoverState(List<BoneDisplayData> bones, Vector2 mousePos)
    {
        var radius = DotRadius * ImGuiHelpers.GlobalScale;
        var isOctahedraMode = _editorState.SkeletonViewMode == SkeletonViewMode.Octahedra;

        foreach (var bone in bones)
        {
            // Ktisis uses IsMouseHoveringRect with padding
            var hoveredByDot = IsHoveringDot(bone.ScreenPos, radius);

            if (isOctahedraMode && bone.ParentScreenPos != null && !hoveredByDot)
            {
                bone.IsHovered = IsPointInOctahedra(mousePos, bone.ParentScreenPos.Value, bone.ScreenPos);
            }
            else
            {
                bone.IsHovered = hoveredByDot;
            }
        }
    }

    private static bool IsHoveringDot(Vector2 pos, float radius)
    {
        return ImGui.IsMouseHoveringRect(
            new Vector2(pos.X - radius - HoverPadding, pos.Y - radius - HoverPadding),
            new Vector2(pos.X + radius + HoverPadding, pos.Y + radius + HoverPadding)
        );
    }

    private bool IsPointInOctahedra(Vector2 point, Vector2 start, Vector2 end)
    {
        var direction = end - start;
        var length = direction.Length();
        if (length < 1f) return false;

        var mid = (start + end) / 2;
        var perpendicular = Vector2.Normalize(new Vector2(-direction.Y, direction.X));
        var width = OctahedraWidth * ImGuiHelpers.GlobalScale;

        var left = mid + perpendicular * width;
        var right = mid - perpendicular * width;

        return PointInTriangle(point, start, left, end) || PointInTriangle(point, start, right, end);
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        var v0 = c - a;
        var v1 = b - a;
        var v2 = p - a;

        var dot00 = Vector2.Dot(v0, v0);
        var dot01 = Vector2.Dot(v0, v1);
        var dot02 = Vector2.Dot(v0, v2);
        var dot11 = Vector2.Dot(v1, v1);
        var dot12 = Vector2.Dot(v1, v2);

        var denom = dot00 * dot11 - dot01 * dot01;
        if (Math.Abs(denom) < 0.0001f) return false;

        var invDenom = 1f / denom;
        var u = (dot11 * dot02 - dot01 * dot12) * invDenom;
        var v = (dot00 * dot12 - dot01 * dot02) * invDenom;

        return u >= 0 && v >= 0 && u + v <= 1;
    }

    private void UpdateHoveredBones(List<BoneDisplayData> bones)
    {
        // Get all hovered bones
        var hovered = bones.Where(b => b.IsHovered).ToList();

        if (hovered.Count == 0)
        {
            _hoveredBones = null;
            _hoverIndex = 0;
            return;
        }

        // Update hover list (sorted by distance like Ktisis)
        _hoveredBones = hovered.OrderBy(b => b.CameraDistance).ToList();

        // Reset index if out of bounds
        if (_hoverIndex >= _hoveredBones.Count)
            _hoverIndex = 0;
    }

    private bool DrawHoverWindow(out IBone? clicked)
    {
        clicked = null;
        if (_hoveredBones == null || _hoveredBones.Count == 0)
            return false;

        // Don't show when gizmo active (Ktisis check)
        if (ImGuizmo.IsUsing() || ImGuizmo.IsOver())
            return false;

        var begin = false;
        try
        {
            // Position window near mouse (Ktisis style - 20px to the right)
            var mousePos = ImGui.GetIO().MousePos;
            ImGui.SetNextWindowPos(mousePos + new Vector2(20f, 0));
            ImGui.SetNextWindowSize(new Vector2(-1, -1), ImGuiCond.Always);

            var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoFocusOnAppearing;
            begin = ImGui.Begin("##BoneHover", flags);
            if (begin)
            {
                // Handle mouse wheel input and clamp scroll index (Ktisis style)
                _hoverIndex -= (int)ImGui.GetIO().MouseWheel;
                if (_hoverIndex >= _hoveredBones.Count)
                    _hoverIndex = 0;
                else if (_hoverIndex < 0)
                    _hoverIndex = _hoveredBones.Count - 1;

                // Capture mouse input
                ImGui.SetNextFrameWantCaptureMouse(true);

                // Check for mouse click
                var isClick = ImGui.IsMouseReleased(ImGuiMouseButton.Left);

                for (int i = 0; i < _hoveredBones.Count; i++)
                {
                    var bone = _hoveredBones[i];
                    var isSelected = i == _hoverIndex;
                    ImGui.Selectable(bone.Bone.Name, isSelected);
                    if (isSelected && isClick)
                        clicked = bone.Bone;
                }
            }
        }
        finally
        {
            if (begin) ImGui.End();
        }

        return clicked != null;
    }

    private void DrawLines(ImDrawListPtr drawList, List<BoneDisplayData> bones, float opacity)
    {
        // Ktisis style: bone color with opacity
        var color = SetAlpha(BoneColor, opacity);

        foreach (var bone in bones)
        {
            if (bone.ParentScreenPos == null) continue;
            drawList.AddLine(bone.ParentScreenPos.Value, bone.ScreenPos, color, LineThickness);
        }
    }

    private void DrawDots(ImDrawListPtr drawList, List<BoneDisplayData> bones)
    {
        // Ktisis style: filled circle with bone color, black outline
        // Selected: radius +1, outline thickness 2.5
        // Normal: outline thickness 1.0
        var selectedColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.TabActive]);

        foreach (var bone in bones)
        {
            var radius = DotRadius;
            float outlineThickness;
            uint color;

            if (bone.IsSelected)
            {
                radius += 1.0f;
                outlineThickness = 2.5f;
                color = selectedColor;
            }
            else
            {
                outlineThickness = 1.0f;
                color = BoneColor;
            }

            // Filled circle
            drawList.AddCircleFilled(bone.ScreenPos, radius, color, 16);
            // Black outline
            drawList.AddCircle(bone.ScreenPos, radius, OutlineColor, 16, outlineThickness);
        }
    }

    private void DrawOctahedra(ImDrawListPtr drawList, List<BoneDisplayData> bones, float opacity)
    {
        var selectedColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.TabActive]);
        var hoveredColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.TabHovered]);
        var defaultColor = SetAlpha(BoneColor, 0.6f);

        foreach (var bone in bones)
        {
            if (bone.ParentScreenPos == null) continue;

            uint color;
            if (bone.IsSelected) color = selectedColor;
            else if (bone.IsHovered) color = hoveredColor;
            else color = defaultColor;

            var fillColor = SetAlpha(color, GetAlpha(color) * 0.5f * opacity);
            var edgeColor = SetAlpha(color, opacity);

            var start = bone.ParentScreenPos.Value;
            var end = bone.ScreenPos;
            var direction = end - start;
            var length = direction.Length();

            if (length < 1f) continue;

            var mid = (start + end) / 2;
            var perpendicular = Vector2.Normalize(new Vector2(-direction.Y, direction.X));
            var width = OctahedraWidth * ImGuiHelpers.GlobalScale;

            var left = mid + perpendicular * width;
            var right = mid - perpendicular * width;

            drawList.AddTriangleFilled(start, left, end, fillColor);
            drawList.AddTriangleFilled(start, right, end, fillColor);

            var lineThick = 1.5f * ImGuiHelpers.GlobalScale;
            drawList.AddLine(start, left, edgeColor, lineThick);
            drawList.AddLine(left, end, edgeColor, lineThick);
            drawList.AddLine(end, right, edgeColor, lineThick);
            drawList.AddLine(right, start, edgeColor, lineThick);
        }
    }

    private void DrawJoints(ImDrawListPtr drawList, List<BoneDisplayData> bones)
    {
        var selectedColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.TabActive]);
        var hoveredColor = ImGui.GetColorU32(ImGui.GetStyle().Colors[(int)ImGuiCol.TabHovered]);

        foreach (var bone in bones)
        {
            var radius = 8f * ImGuiHelpers.GlobalScale;
            var outlineThickness = 2f;

            uint color;
            if (bone.IsSelected)
            {
                color = selectedColor;
                radius += 2f * ImGuiHelpers.GlobalScale;
            }
            else if (bone.IsHovered)
            {
                color = hoveredColor;
            }
            else
            {
                color = BoneColor;
            }

            drawList.AddCircleFilled(bone.ScreenPos, radius, color, 16);
            drawList.AddCircle(bone.ScreenPos, radius, OutlineColor, 16, outlineThickness * ImGuiHelpers.GlobalScale);
        }
    }

    private static uint SetAlpha(uint color, float alpha)
    {
        var a = (uint)(Math.Clamp(alpha, 0f, 1f) * 255);
        return (color & 0x00FFFFFF) | (a << 24);
    }

    private static float GetAlpha(uint color)
    {
        return ((color >> 24) & 0xFF) / 255f;
    }
}
