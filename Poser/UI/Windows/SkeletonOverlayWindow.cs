using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Services;

namespace Poser.UI;

/// <summary>
/// Overlay window that draws skeleton bones on screen for bone selection.
/// Visual style based on Ktisis - simple dots with lines, hover popup for overlapping bones.
/// </summary>
public class SkeletonOverlayWindow : Window
{
    private readonly ICameraService _cameraService;
    private readonly SelectionSession _selection;
    private readonly SceneSession _scene;
    private readonly Game.Viewport.ViewportProjection _viewport;
    private readonly IEditorState _editorState;
    private readonly SkeletonOverlayPresentation _presentation;
    private readonly Application.Posing.IIkConfigurationPort _ikPort;

    // Configuration from settings
    private static SkeletonConfiguration Config => ConfigurationService.Instance.Config.Skeleton;

    private static float DotRadius => Config.BoneDotRadius;
    private static float LineThickness => Config.BoneLineThickness;
    private static float LineOpacity => Config.BoneLineOpacity;
    private static float LineOpacityWhileUsing => Config.BoneLineOpacityWhileUsing;
    private static float OctahedraWidth => Config.OctahedraWidth;

    private static uint BoneColor => Config.BoneColor;
    private static uint OutlineColor => Config.BoneOutlineColor;
    private static uint SelectedBoneColor => Config.SelectedBoneColor;
    private static uint ModifiedBoneColor => Config.ModifiedBoneColor;
    private static uint HoveredBoneColor => Config.HoveredBoneColor;
    private static uint IkChainColor => Config.IkChainColor;
    private static uint MirroredBoneColor => Config.MirroredBoneColor;

    private static bool ShowSkeletonLines => Config.ShowSkeletonLines;
    private static bool ShowNsfwBones =>
        ConfigurationService.Instance.Config.Display.ShowNsfwBones;

    // Bone display data
    private class BoneDisplayData
    {
        public string Name { get; init; } = "";
        public SelectionId Id { get; init; }
        public Vector2 ScreenPos { get; init; }
        public Vector2? ParentScreenPos { get; init; }
        public float CameraDistance { get; init; }
        public bool IsHovered { get; set; }
        public bool IsSelected { get; set; }
        public bool IsIkChain { get; init; }
        public bool IsMirrorPartner { get; set; }
    }

    private sealed class ActorDisplayData
    {
        public string Name { get; init; } = "";
        public SelectionId Id { get; init; }
        public Vector2 ScreenPos { get; init; }
        public float CameraDistance { get; init; }
        public bool IsHovered { get; set; }
    }

    // Hover list state (Ktisis-style)
    private List<BoneDisplayData>? _hoveredBones;
    private int _hoverIndex;
    private Vector2 _hoverAnchor;
    private SelectionId? _pressedWorldTarget;
    private PendingSelection? _pendingSelection;
    private const string HoverListOwnerId = "##skeleton-overlay-bones";

    private readonly record struct PendingSelection(
        SelectionId Id,
        Vector2 ReleasePoint,
        bool Additive,
        InteractionOwner Owner);

    public SkeletonOverlayWindow(
        SceneSession scene,
        Game.Viewport.ViewportProjection viewport,
        ICameraService cameraService,
        IEditorState editorState,
        SkeletonOverlayPresentation presentation,
        Application.Posing.IIkConfigurationPort ikPort)
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
        _scene = scene;
        _selection = scene.Selection;
        _viewport = viewport;
        _cameraService = cameraService;
        _editorState = editorState;
        _presentation = presentation;
        _ikPort = ikPort;

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
        var hoverListOwner = new InteractionOwner(
            HoverListOwnerId,
            InteractionLayer.OverlaySurface,
            int.MaxValue);
        bool listTravel = CanContinueIntoHoverList(
            mousePos, hoverListOwner);
        bool pointerBlocked = Interactive.PointerOccluded(
            InteractionOwner.World,
            mousePos);

        // Holding Alt temporarily hides the skeleton dots for an unobstructed
        // view; the window stays open and interaction resumes on release.
        if (io.KeyAlt)
            return;

        var selectedIds = _selection.Selected.ToHashSet();
        var bones = new List<BoneDisplayData>();
        var actors = new List<ActorDisplayData>();
        var cameraPosition = _cameraService.GetCameraPosition();

        // Collect all bones that project to screen successfully — snapshot
        // descriptors give identity/hierarchy, the viewport projection gives
        // model-space facts, and the camera service projects to screen.
        foreach (var actor in _scene.Snapshot.Actors)
        {
            var actorSelectionId = SelectionId.ForActor(actor.Id);
            if (_viewport.GetActorTransform(actor.Id) is { } actorTransform &&
                _cameraService.WorldToScreen(actorTransform.Position, out var actorScreen))
            {
                actors.Add(new ActorDisplayData
                {
                    // Nickname / anonymous-mask aware, like every surface. The
                    // raw object-index suffix is stripped first so the mask and
                    // nickname lookups see the same name the shell shows.
                    Name = ConfigurationService.Instance.GetDisplayName(
                        actor.Id.LogicalId, StripObjectIndex(actor.Name)),
                    Id = actorSelectionId,
                    ScreenPos = viewportPos + actorScreen,
                    CameraDistance = Vector3.Distance(cameraPosition, actorTransform.Position),
                });
            }

            // The overlay projects every present slot skeleton; each slot has
            // its own model matrix (a weapon's draw object moves with the
            // hand, not the actor origin).
            foreach (var slotSkeleton in actor.Skeletons)
            {
            var descriptors = slotSkeleton.Bones;
            if (descriptors.Count == 0)
                continue;

            // The skeleton-matrix query refreshes/registers skeleton caches
            // inside the runtime boundary.
            if (_viewport.GetSkeletonModelMatrix(descriptors[0].Id) is not { } modelMatrix)
                continue;

            var armedIkBones = CollectArmedIkBones(descriptors);
            bool showNsfw = ShowNsfwBones;

            var boneScreenPositions = new Dictionary<BoneId, Vector2>();
            var boneWorldPositions = new Dictionary<BoneId, Vector3>();
            foreach (var bone in descriptors)
            {
                if (bone.IsHidden || !_presentation.IsVisible(bone.Id))
                    continue;
                if (!showNsfw && Core.BoneInfo.BoneInfoService.IsNsfw(bone.Id.CanonicalName))
                    continue;
                if (_viewport.GetBoneModelTransform(bone.Id) is not { } boneTransform)
                    continue;
                var worldPos = Vector3.Transform(boneTransform.Position, modelMatrix);
                if (!_cameraService.WorldToScreen(worldPos, out var screenPos))
                    continue;
                boneScreenPositions[bone.Id] = viewportPos + screenPos;
                boneWorldPositions[bone.Id] = worldPos;
            }

            foreach (var bone in descriptors)
            {
                if (!boneScreenPositions.TryGetValue(bone.Id, out var screenPos))
                    continue;

                Vector2? parentScreenPos = null;
                if (bone.Parent is { } parentId &&
                    boneScreenPositions.TryGetValue(parentId, out var psp))
                {
                    parentScreenPos = psp;
                }

                var selectionId = SelectionId.ForBone(bone.Id);
                bones.Add(new BoneDisplayData
                {
                    Name = bone.DisplayName,
                    Id = selectionId,
                    ScreenPos = screenPos,
                    ParentScreenPos = parentScreenPos,
                    CameraDistance = Vector3.Distance(cameraPosition, boneWorldPositions[bone.Id]),
                    IsSelected = selectedIds.Contains(selectionId),
                    IsIkChain = armedIkBones?.Contains(bone.Id.CanonicalName) == true
                });
            }
            }
        }

        if (_editorState.SymmetryMode == SymmetryMode.Mirror)
            MarkMirrorPartners(bones);

        var actorRadius = 8f * ImGuiHelpers.GlobalScale;
        foreach (var actor in actors)
            actor.IsHovered = !pointerBlocked
                && !listTravel
                && IsHoveringDot(actor.ScreenPos, actorRadius);

        // Update hover state
        if (pointerBlocked)
        {
            foreach (var bone in bones)
                bone.IsHovered = false;
            _pressedWorldTarget = null;
        }
        else
        {
            UpdateHoverState(bones, mousePos);
        }

        CommitPendingSelection(bones, actors);

        // Filter bones if ShowSelectedBonesOnly is enabled
        if (_editorState.ShowSelectedBonesOnly)
        {
            bones = bones.Where(b => b.IsSelected || b.IsHovered).ToList();
        }

        // Draw skeleton
        // The custom gizmo holds shared pointer ownership on hover AND
        // drag, so this single check covers both engagement states.
        var isGizmoActive = Controls.GizmoPointerOwnership.Owned;
        var lineOpacity = isGizmoActive ? LineOpacityWhileUsing : LineOpacity;

        switch (_editorState.SkeletonViewMode)
        {
            case SkeletonViewMode.Default:
                // Octahedra/Joints have no connector lines to suppress: their
                // bodies ARE the bones.
                if (ShowSkeletonLines)
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

        // Brio-style model-transform point at each actor origin. This is the
        // world-space route back from bone posing to whole-actor selection.
        foreach (var actor in actors)
        {
            bool selected = _selection.IsSelected(actor.Id);
            uint color = selected ? SelectedBoneColor : BoneColor;
            float radius = selected || actor.IsHovered ? actorRadius + 2f : actorRadius;
            drawList.AddCircleFilled(actor.ScreenPos, radius, color, 20);
            drawList.AddCircle(actor.ScreenPos, radius, OutlineColor, 20, 2f * ImGuiHelpers.GlobalScale);
            drawList.AddCircle(actor.ScreenPos, radius * 0.45f, OutlineColor, 16, 1f * ImGuiHelpers.GlobalScale);
        }

        var hoveredActor = actors
            .Where(actor => actor.IsHovered)
            .OrderBy(actor => actor.CameraDistance)
            .FirstOrDefault();
        if (hoveredActor != null && !pointerBlocked)
        {
            var overlayMouse = ImGui.GetMousePos();
            Crystarium.HoverHelp.Preview("sow-actor",
                overlayMouse - new Vector2(4f, 4f), overlayMouse + new Vector2(4f, 4f),
                $"{hoveredActor.Name} — actor transform");
        }

        // Freeze the overlapping candidates and their anchor while the
        // pointer crosses into the explicit list.
        bool onFrozenCluster = listTravel
            && _hoveredBones is { } frozen
            && bones.Any(bone => bone.IsHovered
                && frozen.Any(candidate => candidate.Id.Equals(bone.Id)));
        UpdateHoveredBones(bones, mousePos, listTravel);
        bool hasWorldBone = !listTravel
            ? bones.Any(bone => bone.IsHovered)
            : onFrozenCluster;
        var worldTarget = hoveredActor?.Id
            ?? (hasWorldBone && _hoveredBones is { Count: > 0 }
                ? _hoveredBones[_hoverIndex].Id
                : (SelectionId?)null);
        UpdateWorldPress(
            worldTarget,
            pointerBlocked || (listTravel && !hasWorldBone));
        if (_hoveredBones is { Count: > 0 })
            DrawHoverList();
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

    private void UpdateHoveredBones(
        List<BoneDisplayData> bones,
        Vector2 mousePos,
        bool keepFrozen)
    {
        if (keepFrozen && _hoveredBones is { Count: > 0 })
            return;
        var hovered = bones
            .Where(bone => bone.IsHovered)
            .OrderBy(bone => bone.CameraDistance)
            .ToList();

        if (hovered.Count == 0)
        {
            if (keepFrozen)
                return;
            _hoveredBones = null;
            _hoverIndex = 0;
            return;
        }

        bool sameCandidates = _hoveredBones != null
            && _hoveredBones.Count == hovered.Count
            && !_hoveredBones.Where(
                (bone, index) => !bone.Id.Equals(hovered[index].Id)).Any();
        if (!sameCandidates)
        {
            _hoveredBones = hovered;
            _hoverIndex = 0;
            _hoverAnchor = mousePos;
        }
    }

    private bool CanContinueIntoHoverList(
        Vector2 point,
        InteractionOwner owner)
    {
        if (_hoveredBones is not { Count: > 0 }
            || !Interactive.TryGetOwnerBounds(
                HoverListOwnerId,
                out var listMin,
                out var listMax)
            || Interactive.PointerOccluded(owner, point))
            return false;
        float padding = HoverPadding * ImGuiHelpers.GlobalScale;
        var bridgeMin = Vector2.Min(_hoverAnchor, listMin)
            - new Vector2(padding);
        var bridgeMax = Vector2.Max(_hoverAnchor, listMax)
            + new Vector2(padding);
        return point.X >= bridgeMin.X && point.X < bridgeMax.X
            && point.Y >= bridgeMin.Y && point.Y < bridgeMax.Y;
    }

    private void UpdateWorldPress(
        SelectionId? target,
        bool pointerBlocked)
    {
        if (pointerBlocked || Controls.GizmoPointerOwnership.Owned)
        {
            _pressedWorldTarget = null;
            return;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _pressedWorldTarget = target;

        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return;
        if (_pressedWorldTarget is { } pressed
            && target is { } released
            && pressed.Equals(released))
        {
            _pendingSelection = new PendingSelection(
                released,
                ImGui.GetMousePos(),
                ImGui.GetIO().KeyCtrl,
                InteractionOwner.World);
        }
        _pressedWorldTarget = null;
    }

    private void DrawHoverList()
    {
        if (_hoveredBones == null || _hoveredBones.Count == 0
            || Controls.GizmoPointerOwnership.Owned)
            return;

        var labels = _hoveredBones.Select(bone => bone.Name).ToArray();
        int clicked = Crystarium.FloatingSurface.HoverList(
            HoverListOwnerId,
            _hoverAnchor,
            labels,
            _hoverIndex,
            InteractionLayer.OverlaySurface);
        if (clicked < 0 || clicked >= _hoveredBones.Count)
            return;
        _hoverIndex = clicked;
        _pendingSelection = new PendingSelection(
            _hoveredBones[clicked].Id,
            ImGui.GetMousePos(),
            ImGui.GetIO().KeyCtrl,
            new InteractionOwner(
                HoverListOwnerId,
                InteractionLayer.OverlaySurface,
                int.MaxValue));
    }

    private void CommitPendingSelection(
        IReadOnlyList<BoneDisplayData> bones,
        IReadOnlyList<ActorDisplayData> actors)
    {
        if (_pendingSelection is not { } pending)
            return;
        _pendingSelection = null;
        bool stillPresent = bones.Any(bone => bone.Id.Equals(pending.Id))
            || actors.Any(actor => actor.Id.Equals(pending.Id));
        if (!stillPresent
            || Interactive.PointerOccluded(
                pending.Owner,
                pending.ReleasePoint))
            return;
        if (pending.Additive)
            _selection.Toggle(pending.Id);
        else
            _selection.Select(pending.Id);
    }

    /// <summary>Strips the raw object-index suffix ("Name (201)"), matching
    /// the shell's display rule.</summary>
    private static string StripObjectIndex(string name)
        => System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");

    /// <summary>Canonical names of every member of an ARMED IK chain on this
    /// exact skeleton — endpoint, both joints, and the optional twists. Null
    /// when no chain on the skeleton is enabled.</summary>
    private HashSet<string>? CollectArmedIkBones(
        IReadOnlyList<Domain.Scene.BoneDescriptor> descriptors)
    {
        HashSet<string>? names = null;
        foreach (var bone in descriptors)
        {
            // Only endpoints carry configuration, so at most four port reads
            // per skeleton per frame.
            var definition = Domain.Posing.IkChains.ForEndpoint(bone.Id.CanonicalName);
            if (definition == null)
                continue;
            if (_ikPort.Get(TransformTargetId.ForBone(bone.Id)) is not { Enabled: true })
                continue;

            names ??= new HashSet<string>();
            names.Add(bone.Id.CanonicalName);
            names.Add(definition.Endpoint);
            names.Add(definition.FirstJoint);
            names.Add(definition.SecondJoint);
            if (definition.FirstTwist != null)
                names.Add(definition.FirstTwist);
            if (definition.SecondTwist != null)
                names.Add(definition.SecondTwist);
        }
        return names;
    }

    /// <summary>Flags the opposite-side partners of the selected bones so
    /// Mirror symmetry shows what a transform will also move. Partners are
    /// matched inside the selected bone's own skeleton, never across actors.</summary>
    private static void MarkMirrorPartners(List<BoneDisplayData> bones)
    {
        HashSet<(SkeletonId, string)>? partners = null;
        foreach (var bone in bones)
        {
            if (!bone.IsSelected || bone.Id.Bone is not { } boneId)
                continue;
            if (Core.PoseMath.GetMirrorBoneName(boneId.CanonicalName) is not { } mirror)
                continue;
            partners ??= new HashSet<(SkeletonId, string)>();
            partners.Add((boneId.Skeleton, mirror));
        }
        if (partners == null)
            return;

        foreach (var bone in bones)
        {
            if (bone.IsSelected || bone.Id.Bone is not { } boneId)
                continue;
            bone.IsMirrorPartner = partners.Contains(
                (boneId.Skeleton, boneId.CanonicalName));
        }
    }

    /// <summary>The one color priority for bones: Selected > Hovered > IK >
    /// mirror partner > fallback. Hover is opt-in because the dot layer leaves
    /// hover feedback to the hover list.</summary>
    private static uint ResolveBoneColor(BoneDisplayData bone, bool useHover, uint fallback)
    {
        if (bone.IsSelected)
            return SelectedBoneColor;
        if (useHover && bone.IsHovered)
            return HoveredBoneColor;
        if (bone.IsIkChain)
            return IkChainColor;
        if (bone.IsMirrorPartner)
            return MirroredBoneColor;
        return fallback;
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

        foreach (var bone in bones)
        {
            var radius = DotRadius;
            float outlineThickness;
            var color = ResolveBoneColor(bone, useHover: false, BoneColor);

            if (bone.IsSelected)
            {
                radius += 1.0f;
                outlineThickness = 2.5f;
            }
            else
            {
                outlineThickness = 1.0f;
            }

            // Filled circle
            drawList.AddCircleFilled(bone.ScreenPos, radius, color, 16);
            // Black outline
            drawList.AddCircle(bone.ScreenPos, radius, OutlineColor, 16, outlineThickness);
        }
    }

    private void DrawOctahedra(ImDrawListPtr drawList, List<BoneDisplayData> bones, float opacity)
    {
        foreach (var bone in bones)
        {
            if (bone.ParentScreenPos == null) continue;

            var color = ResolveBoneColor(bone, useHover: true, BoneColor);
            // Everything that is neither selected nor hovered stays faded,
            // tinted or not.
            if (!bone.IsSelected && !bone.IsHovered)
                color = SetAlpha(color, 0.6f);

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
        foreach (var bone in bones)
        {
            var radius = 8f * ImGuiHelpers.GlobalScale;
            var outlineThickness = 2f;

            var color = ResolveBoneColor(bone, useHover: true, BoneColor);
            if (bone.IsSelected)
                radius += 2f * ImGuiHelpers.GlobalScale;

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
