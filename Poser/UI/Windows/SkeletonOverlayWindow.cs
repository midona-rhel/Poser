using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Game.Bindings;
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
    private readonly StableBindingRegistry _bindings;

    // Configuration from settings
    private static SkeletonConfiguration Config => ConfigurationService.Instance.Config.Skeleton;

    private static float DotRadius => Config.BoneDotRadius;
    private static float LineThickness => Config.BoneLineThickness;
    private static float LineOpacity => Config.BoneLineOpacity;
    private static float LineOpacityWhileUsing => Config.BoneLineOpacityWhileUsing;
    private static float OctahedraWidth => Config.OctahedraWidth;

    private static uint BoneColor => Config.BoneColor;
    private static uint OutlineColor => Config.BoneOutlineColor;

    // While the stored color still equals its fresh-install default, the
    // selected/hovered family follows the live accent (theme + AccentIndex);
    // an explicit ColorWell override pins the stored value instead.
    private static uint SelectedBoneColor =>
        Config.SelectedBoneColor == SkeletonConfiguration.DefaultSelectedBoneColor
            ? ImGui.ColorConvertFloat4ToU32(Crystarium.ActiveTheme.Palette.Primary)
            : Config.SelectedBoneColor;
    private static uint ModifiedBoneColor => Config.ModifiedBoneColor;
    private static uint HoveredBoneColor =>
        Config.HoveredBoneColor == SkeletonConfiguration.DefaultHoveredBoneColor
            ? ImGui.ColorConvertFloat4ToU32(Vector4.Lerp(
                Crystarium.ActiveTheme.Palette.Primary, Vector4.One, 0.35f))
            : Config.HoveredBoneColor;
    private static uint IkChainColor => Config.IkChainColor;
    private static uint MirroredBoneColor => Config.MirroredBoneColor;

    private static bool ShowSkeletonLines => Config.ShowSkeletonLines;
    private static bool ShowNsfwBones =>
        ConfigurationService.Instance.Config.Display.ShowNsfwBones;

    // ── the per-frame display model ──────────────────────────────────────
    // One VALUE per drawn handle, held in buffers this window owns and
    // clears at the top of each frame. The rebuild itself is the design
    // (Ktisis draws from a fresh projection every frame); what the overlay
    // must not do is allocate the model, because posing is the tool's
    // hottest interactive state and a heap object per visible bone per
    // frame is hundreds of allocations a frame. Mutating passes therefore
    // reach the elements through CollectionsMarshal.AsSpan — a foreach over
    // a List of values hands out copies, and a hover written to a copy is a
    // hover lost.

    private struct BoneDisplayData
    {
        public string Name;
        public SelectionId Id;
        public Vector2 ScreenPos;
        public Vector2? ParentScreenPos;
        public float CameraDistance;
        public bool IsHovered;
        public bool IsSelected;
        public bool IsIkChain;
        public bool IsMirrorPartner;
    }

    private struct ActorDisplayData
    {
        public string Name;
        public SelectionId Id;
        public Vector2 ScreenPos;
        public float CameraDistance;
        public bool IsHovered;
    }

    /// <summary>One light's handle in the world. Lights carry no skeleton and
    /// no hierarchy, so a light is exactly one dot plus a small mark saying
    /// which way it faces.</summary>
    private struct LightDisplayData
    {
        public string Name;
        public SelectionId Id;
        public PoseTransform Transform;
        public Vector2 ScreenPos;
        public float CameraDistance;
        public bool IsSelected;
        public ILight? Live;
        public bool IsHovered;
    }

    private readonly HashSet<SelectionId> _selectedIds = new();
    private readonly List<BoneDisplayData> _bones = new();
    private readonly List<ActorDisplayData> _actors = new();
    private readonly List<LightDisplayData> _lights = new();
    private readonly Dictionary<BoneId, Vector2> _boneScreenPositions = new();
    private readonly Dictionary<BoneId, Vector3> _boneWorldPositions = new();
    private readonly List<BoneDisplayData> _hoverCandidates = new();

    // Hover list state (Ktisis-style). The frozen candidates outlive the
    // frame that found them, so this list is NOT one of the per-frame
    // buffers: it is rewritten only when the candidate set changes, and an
    // EMPTY list is what 'no cluster' means. Its labels are cached with it,
    // because the popup wants them as a list and rebuilding one per frame
    // would put back the allocation the buffers just removed.
    private readonly List<BoneDisplayData> _hoveredBones = new();
    private readonly List<string> _hoverLabels = new();
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
        Application.Posing.IIkConfigurationPort ikPort,
        StableBindingRegistry bindings,
        Dalamud.Plugin.Services.IPluginLog log)
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
        _bindings = bindings;
        _log = log;

        RespectCloseHotkey = false;
    }

    private readonly Dalamud.Plugin.Services.IPluginLog _log;

    public override void PreDraw()
    {
        base.PreDraw();
        ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero, ImGuiCond.Always);
        var io = ImGui.GetIO();
        Size = io.DisplaySize;
        SizeCondition = ImGuiCond.Always;
    }

    /// <summary>The titlebar Armature toggle. With the toggle Off the
    /// overlay still anchors the current selection: selected bones and
    /// selected actor origins stay visible on their own, so an edit made
    /// from the workspace never loses its on-screen anchor. Everything
    /// unselected stays hidden and non-interactive.</summary>
    public bool UserVisible { get; set; }

    private bool AnySelectionAnchor()
    {
        foreach (var id in _selection.Selected)
            if (id.Kind is SceneEntityKind.Bone
                or SceneEntityKind.Actor
                // A gaze point belongs to an actor: keep that actor's skeleton
                // anchored so aiming the gaze never blanks the dots under it.
                or SceneEntityKind.GazeTarget
                // A selected light anchors the overlay for the same reason an
                // actor does: its handle is the edit's on-screen anchor.
                or SceneEntityKind.Light)
                return true;
        return false;
    }

    public override void Draw()
    {
        // First line of the frame, before every gate: a left press ALWAYS
        // logs, so a missing line means this method never ran that frame.
        // Debug, not Information: these are standing breadcrumbs for the
        // world-click path, and every user click would otherwise spam the
        // Dalamud log forever.
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _log.Debug(
                $"[Overlay] frame-press mouse={ImGui.GetIO().MousePos} "
                + $"alt={ImGui.GetIO().KeyAlt}");
        try
        {
            DrawCore();
        }
        catch (Exception ex)
        {
            _log.Error($"[Overlay] draw failed: {ex}");
            throw;
        }
    }

    private void DrawCore()
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

        // The ARMATURE pass answers to the sidebar's opted-in bones (the
        // Skeleton node and the finer eyes) and the selection anchor. The
        // LIGHT pass does not: a light is invisible in the world and its
        // handle is the only route to it from the viewport, so it draws
        // whenever the scene holds lights — Ktisis and Brio both draw their
        // light handles unconditionally. Alt still hides everything.
        bool drawArmature = _presentation.AnyVisible || AnySelectionAnchor();

        var selectedIds = _selectedIds;
        selectedIds.Clear();
        foreach (var id in _selection.Selected)
            selectedIds.Add(id);
        var bones = _bones;
        var actors = _actors;
        var lights = _lights;
        bones.Clear();
        actors.Clear();
        lights.Clear();
        var cameraPosition = _cameraService.GetCameraPosition();

        // Lights are otherwise invisible in the world: without a handle there
        // is no way to select or even find one from the viewport.
        foreach (var light in _scene.Snapshot.Lights)
        {
            var lightSelectionId = SelectionId.ForLight(light.Id);
            if (!_presentation.IsHandleShown(lightSelectionId))
                continue;
            if (_viewport.GetLightTransform(light.Id) is not { } lightTransform ||
                !_cameraService.WorldToScreen(lightTransform.Position, out var lightScreen))
                continue;
            bool lightSelected = selectedIds.Contains(lightSelectionId);
            var resolved = _bindings.Resolve(light.Id);
            lights.Add(new LightDisplayData
            {
                Name = light.Name,
                Id = lightSelectionId,
                Transform = lightTransform,
                ScreenPos = viewportPos + lightScreen,
                CameraDistance = Vector3.Distance(
                    cameraPosition, lightTransform.Position),
                IsSelected = lightSelected,
                Live = resolved.Success ? resolved.Value : null,
            });
        }

        // A prop's handle is the one viewport route to SELECTING it — the
        // model itself takes no clicks — so props draw whenever the scene
        // holds them, the light rule. The actor-dot pipeline serves the
        // handle unchanged: a named dot that selects its SelectionId.
        foreach (var prop in _scene.Snapshot.Props)
        {
            var propSelectionId = SelectionId.ForProp(prop.Id);
            if (!_presentation.IsHandleShown(propSelectionId))
                continue;
            if (_viewport.GetPropTransform(prop.Id) is not { } propTransform ||
                !_cameraService.WorldToScreen(
                    propTransform.Position, out var propScreen))
                continue;
            actors.Add(new ActorDisplayData
            {
                Name = prop.Name,
                Id = propSelectionId,
                ScreenPos = viewportPos + propScreen,
                CameraDistance = Vector3.Distance(
                    cameraPosition, propTransform.Position),
            });
        }

        // Actor origin handles draw for EVERY actor, armature or not — the
        // sidebar's manip toggle is their one gate (user 2026-08-12): the
        // handle is how an actor is picked from the viewport at all.
        foreach (var actor in _scene.Snapshot.Actors)
        {
            var actorSelectionId = SelectionId.ForActor(actor.Id);
            if (_presentation.IsHandleShown(actorSelectionId) &&
                _viewport.GetActorTransform(actor.Id) is { } actorTransform &&
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
        }

        // Collect all bones that project to screen successfully — snapshot
        // descriptors give identity/hierarchy, the viewport projection gives
        // model-space facts, and the camera service projects to screen.
        if (drawArmature)
        foreach (var actor in _scene.Snapshot.Actors)
        {
            var actorSelectionId = SelectionId.ForActor(actor.Id);

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

            var boneScreenPositions = _boneScreenPositions;
            var boneWorldPositions = _boneWorldPositions;
            boneScreenPositions.Clear();
            boneWorldPositions.Clear();
            foreach (var bone in descriptors)
            {
                // Opted-in bones draw; a SELECTED bone draws regardless —
                // the anchor rule.
                bool shown = _presentation.IsVisible(bone.Id);
                if (bone.IsHidden
                    || (!shown && !selectedIds.Contains(
                        SelectionId.ForBone(bone.Id))))
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

        // No armature filter here anymore: every entry above was already
        // gated by its sidebar manip toggle at collection, and a masked
        // handle was never collected — so nothing hidden is interactive.

        var actorRadius = 8f * ImGuiHelpers.GlobalScale;
        foreach (ref var actor in CollectionsMarshal.AsSpan(actors))
            actor.IsHovered = !pointerBlocked
                && !listTravel
                && IsHoveringDot(actor.ScreenPos, actorRadius);
        foreach (ref var light in CollectionsMarshal.AsSpan(lights))
            light.IsHovered = !pointerBlocked
                && !listTravel
                && IsHoveringDot(light.ScreenPos, actorRadius);

        // Update hover state
        if (pointerBlocked)
        {
            foreach (ref var bone in CollectionsMarshal.AsSpan(bones))
                bone.IsHovered = false;
            _pressedWorldTarget = null;
        }
        else
        {
            UpdateHoverState(bones, mousePos);
        }

        CommitPendingSelection(bones, actors, lights);

        // Filter bones if ShowSelectedBonesOnly is enabled
        if (_editorState.ShowSelectedBonesOnly)
            bones.RemoveAll(NotSelectedOrHovered);

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

        DrawLights(drawList, viewportPos, lights, actorRadius);

        int hoveredActorIndex = NearestHovered(actors);
        int hoveredLightIndex = NearestHovered(lights);
        bool hasHoveredActor = hoveredActorIndex >= 0;
        bool hasHoveredLight = hoveredLightIndex >= 0;
        if (hasHoveredLight && !pointerBlocked)
        {
            var overlayMouse = ImGui.GetMousePos();
            Crystarium.HoverHelp.Preview("sow-light",
                overlayMouse - new Vector2(4f, 4f), overlayMouse + new Vector2(4f, 4f),
                $"{lights[hoveredLightIndex].Name} — light", animated: false);
        }
        else if (hasHoveredActor && !pointerBlocked)
        {
            var hoveredActor = actors[hoveredActorIndex];
            var overlayMouse = ImGui.GetMousePos();
            Crystarium.HoverHelp.Preview("sow-actor",
                overlayMouse - new Vector2(4f, 4f), overlayMouse + new Vector2(4f, 4f),
                hoveredActor.Id.Kind == SceneEntityKind.Prop
                    ? $"{hoveredActor.Name} — prop"
                    : $"{hoveredActor.Name} — actor transform",
                animated: false);
        }

        // Freeze the overlapping candidates and their anchor while the
        // pointer crosses into the explicit list.
        bool onFrozenCluster = listTravel && AnyHoveredIsFrozen(bones);
        UpdateHoveredBones(bones, mousePos, listTravel);
        bool hasWorldBone = !listTravel
            ? AnyHovered(bones)
            : onFrozenCluster;
        // A light handle sits in front of everything else it overlaps: it is
        // the only route to a light from the viewport, and a bone dot behind it
        // is still reachable from the sidebar.
        var worldTarget = hasHoveredLight
            ? lights[hoveredLightIndex].Id
            : hasHoveredActor
                ? actors[hoveredActorIndex].Id
                : hasWorldBone && _hoveredBones.Count > 0
                    ? _hoveredBones[_hoverIndex].Id
                    : (SelectionId?)null;
        // Dalamud routes every click ImGui has not claimed BEFORE the press
        // to the game — an unclaimed pointer means the press never reaches
        // ImGui at all (no IsMouseClicked, ever). Claiming on hover is what
        // makes a world dot clickable in the first place: the gizmo
        // overlay's exact contract, held through the press so the release
        // edge arrives too.
        if (worldTarget != null || _pressedWorldTarget != null)
        {
            io.WantCaptureMouse = true;
            ImGui.SetNextFrameWantCaptureMouse(true);
        }

        // Diagnostic breadcrumb for dead world clicks: one line per press
        // naming every gate that can swallow it.
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _log.Debug(
                $"[Overlay] press target={worldTarget?.ToString() ?? "none"} "
                + $"blocked={pointerBlocked} listTravel={listTravel} "
                + $"hasWorldBone={hasWorldBone} "
                + $"gizmo={Controls.GizmoPointerOwnership.Owned} "
                + $"hoverL={hasHoveredLight} hoverA={hasHoveredActor}");
        UpdateWorldPress(
            worldTarget,
            pointerBlocked || (listTravel && !hasWorldBone));
        if (_hoveredBones.Count > 0)
            DrawHoverList();
    }

    private const int HoverPadding = 6;

    /// <summary>The ShowSelectedBonesOnly filter as a cached predicate: a
    /// lambda written inline allocates a delegate every frame the mode is
    /// on.</summary>
    private static readonly Predicate<BoneDisplayData> NotSelectedOrHovered =
        bone => !(bone.IsSelected || bone.IsHovered);

    /// <summary>Index of the hovered handle NEAREST the camera, or -1.
    /// Ties keep the earlier entry, as the ordered query this replaces
    /// did.</summary>
    private static int NearestHovered(List<ActorDisplayData> actors)
    {
        int best = -1;
        float bestDistance = 0f;
        for (int i = 0; i < actors.Count; i++)
        {
            var actor = actors[i];
            if (!actor.IsHovered)
                continue;
            if (best < 0 || actor.CameraDistance < bestDistance)
            {
                best = i;
                bestDistance = actor.CameraDistance;
            }
        }
        return best;
    }

    private static int NearestHovered(List<LightDisplayData> lights)
    {
        int best = -1;
        float bestDistance = 0f;
        for (int i = 0; i < lights.Count; i++)
        {
            var light = lights[i];
            if (!light.IsHovered)
                continue;
            if (best < 0 || light.CameraDistance < bestDistance)
            {
                best = i;
                bestDistance = light.CameraDistance;
            }
        }
        return best;
    }

    private static bool AnyHovered(List<BoneDisplayData> bones)
    {
        for (int i = 0; i < bones.Count; i++)
            if (bones[i].IsHovered)
                return true;
        return false;
    }

    /// <summary>Whether any bone hovered THIS frame is one of the frozen
    /// candidates — the test that keeps a cluster alive while the pointer
    /// crosses into its list.</summary>
    private bool AnyHoveredIsFrozen(List<BoneDisplayData> bones)
    {
        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            if (!bone.IsHovered)
                continue;
            for (int j = 0; j < _hoveredBones.Count; j++)
                if (_hoveredBones[j].Id.Equals(bone.Id))
                    return true;
        }
        return false;
    }

    private void UpdateHoverState(List<BoneDisplayData> bones, Vector2 mousePos)
    {
        var radius = DotRadius * ImGuiHelpers.GlobalScale;
        var isOctahedraMode = _editorState.SkeletonViewMode == SkeletonViewMode.Octahedra;

        foreach (ref var bone in CollectionsMarshal.AsSpan(bones))
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
        if (keepFrozen && _hoveredBones.Count > 0)
            return;

        // Nearest first, by insertion: a candidate cluster is a handful of
        // overlapping dots, and inserting AFTER every equal distance keeps
        // the stable order the ordered query gave.
        var hovered = _hoverCandidates;
        hovered.Clear();
        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            if (!bone.IsHovered)
                continue;
            int at = hovered.Count;
            while (at > 0
                && hovered[at - 1].CameraDistance > bone.CameraDistance)
                at--;
            hovered.Insert(at, bone);
        }

        if (hovered.Count == 0)
        {
            if (keepFrozen)
                return;
            _hoveredBones.Clear();
            _hoverLabels.Clear();
            _hoverIndex = 0;
            return;
        }

        bool sameCandidates = _hoveredBones.Count == hovered.Count;
        for (int i = 0; sameCandidates && i < hovered.Count; i++)
            sameCandidates = _hoveredBones[i].Id.Equals(hovered[i].Id);
        if (!sameCandidates)
        {
            _hoveredBones.Clear();
            _hoverLabels.Clear();
            for (int i = 0; i < hovered.Count; i++)
            {
                _hoveredBones.Add(hovered[i]);
                _hoverLabels.Add(hovered[i].Name);
            }
            _hoverIndex = 0;
            _hoverAnchor = mousePos;
        }
    }

    private bool CanContinueIntoHoverList(
        Vector2 point,
        InteractionOwner owner)
    {
        if (_hoveredBones.Count == 0
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
        if (_hoveredBones.Count == 0
            || Controls.GizmoPointerOwnership.Owned)
            return;

        int clicked = Crystarium.FloatingSurface.HoverList(
            HoverListOwnerId,
            _hoverAnchor,
            _hoverLabels,
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
        IReadOnlyList<ActorDisplayData> actors,
        IReadOnlyList<LightDisplayData> lights)
    {
        if (_pendingSelection is not { } pending)
            return;
        _pendingSelection = null;
        bool stillPresent = false;
        for (int i = 0; !stillPresent && i < bones.Count; i++)
            stillPresent = bones[i].Id.Equals(pending.Id);
        for (int i = 0; !stillPresent && i < actors.Count; i++)
            stillPresent = actors[i].Id.Equals(pending.Id);
        for (int i = 0; !stillPresent && i < lights.Count; i++)
            stillPresent = lights[i].Id.Equals(pending.Id);
        bool releaseOccluded = Interactive.PointerOccluded(
            pending.Owner,
            pending.ReleasePoint);
        _log.Debug(
            $"[Overlay] commit {pending.Id} present={stillPresent} "
            + $"occluded={releaseOccluded}");
        if (!stillPresent || releaseOccluded)
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

        foreach (ref var bone in CollectionsMarshal.AsSpan(bones))
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

    // ── light handles and facing marks ───────────────────────────────────
    // A handle dot for every light plus one small directional mark. The mark
    // states FACING only: range, falloff and panel extents are the Light
    // tab's numbers, and drawing them in the world buried the handles.

    private void DrawLights(
        ImDrawListPtr drawList,
        Vector2 viewportPos,
        List<LightDisplayData> lights,
        float dotRadius)
    {
        foreach (var light in lights)
        {
            var color = LightColor(light);
            if (light.Live is { } live)
                DrawLightShape(drawList, viewportPos, light, live, color, dotRadius);

            float radius = light.IsSelected || light.IsHovered
                ? dotRadius + 2f
                : dotRadius;
            uint dot = light.IsSelected
                ? SelectedBoneColor
                : ImGui.ColorConvertFloat4ToU32(color);
            drawList.AddCircleFilled(light.ScreenPos, radius, dot, 20);
            drawList.AddCircle(
                light.ScreenPos, radius, OutlineColor, 20,
                2f * ImGuiHelpers.GlobalScale);
            // The inner ring reads as an aperture, which is what separates a
            // light handle from an actor's transform point at a glance.
            drawList.AddCircle(
                light.ScreenPos, radius * 0.45f, OutlineColor, 16,
                1f * ImGuiHelpers.GlobalScale);
        }
    }

    /// <summary>The light's own emission colour, tone-mapped the way the Light
    /// tab's colour well maps it — the native value is HDR and reaches far past
    /// white. An unresolved light falls back to the bone family.</summary>
    private static Vector4 LightColor(LightDisplayData light)
    {
        if (light.Live is not { } live)
            return ColorToVector(BoneColor) with { W = light.IsSelected ? 1f : 0.6f };
        var raw = live.Color;
        return new Vector4(
            MathF.Sqrt(MathF.Max(0f, raw.X) / 6f),
            MathF.Sqrt(MathF.Max(0f, raw.Y) / 6f),
            MathF.Sqrt(MathF.Max(0f, raw.Z) / 6f),
            light.IsSelected ? 1f : 0.6f);
    }

    private static Vector4 ColorToVector(uint color) => new(
        (color & 0xFF) / 255f,
        ((color >> 8) & 0xFF) / 255f,
        ((color >> 16) & 0xFF) / 255f,
        ((color >> 24) & 0xFF) / 255f);

    /// <summary>The screen size of a light's facing mark, before UI scale —
    /// the world gizmo's handles span 80px, so a mark at this size sits under
    /// them rather than competing with them.</summary>
    private const float LightMarkPixels = 34f;

    /// <summary>The world length that projects to <paramref name="pixels"/> at
    /// this position's depth. Measured rather than derived from matrix cells:
    /// project a unit offset perpendicular to the view direction, read off
    /// pixels-per-world-unit, and divide — the same derivation as
    /// <see cref="Controls.WorldGizmoProjection.WorldScale"/>. Marks built as
    /// multiples of this keep a constant perceived size at any distance, so a
    /// light's mark neither balloons up close nor vanishes far away. Zero when
    /// the position or its offset will not project.</summary>
    private float MarkWorldLength(Vector3 position, float pixels)
    {
        var fromCamera = position - _cameraService.GetCameraPosition();
        if (fromCamera.LengthSquared() < 1e-8f)
            return 0f;
        var view = Vector3.Normalize(fromCamera);
        var reference = MathF.Abs(Vector3.Dot(view, Vector3.UnitY)) > 0.99f
            ? Vector3.UnitX
            : Vector3.UnitY;
        var lateral = Vector3.Normalize(Vector3.Cross(view, reference));
        if (!_cameraService.WorldToScreen(position, out var origin) ||
            !_cameraService.WorldToScreen(position + lateral, out var offset))
            return 0f;
        float pixelsPerWorldUnit = Vector2.Distance(origin, offset);
        return pixelsPerWorldUnit < 1e-3f ? 0f : pixels / pixelsPerWorldUnit;
    }

    /// <summary>The per-kind facing mark: one simple stroke figure along the
    /// beam (+Z of the light's rotation), sized in screen pixels. It says which
    /// way the light points and — for a spot — how wide the throw opens, and
    /// deliberately says nothing about range: extents are the Light tab's
    /// numbers. Selection is emphasis on the same figure, never extra
    /// geometry.</summary>
    private void DrawLightShape(
        ImDrawListPtr drawList,
        Vector2 viewportPos,
        LightDisplayData light,
        ILight live,
        Vector4 color,
        float dotRadius)
    {
        bool selected = light.IsSelected;
        var position = light.Transform.Position;
        var rotation = light.Transform.Rotation;
        var localX = Vector3.Transform(Vector3.UnitX, rotation);
        var localY = Vector3.Transform(Vector3.UnitY, rotation);
        var localZ = Vector3.Transform(Vector3.UnitZ, rotation);
        float uiScale = ImGuiHelpers.GlobalScale;
        float length = MarkWorldLength(position, LightMarkPixels * uiScale);
        if (length <= 0f)
            return;
        // A light that is switched off still says which way it faces, quietly.
        var stroke = live.IsOn ? color : color with { W = color.W * 0.35f };
        float thickness = (selected ? 2.5f : 1.5f) * uiScale;

        switch (live.Kind)
        {
            case LightKind.Directional:
                DrawWorldArrow(
                    drawList, viewportPos, position, localZ, localX, localY,
                    length, thickness, stroke);
                break;
            case LightKind.Point:
                // Omnidirectional: there is no facing to indicate, so the
                // handle dot is the whole mark. Selection adds one ring around
                // it, in screen space with the dot it belongs to.
                if (selected)
                    drawList.AddCircle(
                        light.ScreenPos, dotRadius + 5f * uiScale,
                        ImGui.ColorConvertFloat4ToU32(stroke), 24, thickness);
                break;
            case LightKind.Spot:
                // Real cone ANGLE at a fixed perceived length: the width of the
                // throw belongs to the mark, the distance it carries does not.
                DrawWorldCone(
                    drawList, viewportPos, position, localX, localY, localZ,
                    0.5f * float.DegreesToRadians(live.SpotAngle),
                    length, thickness, stroke);
                break;
            case LightKind.Area:
            {
                // The panel's throw leans with its skew angles — Ktisis
                // composes AreaAngle into the facing before drawing, and a
                // skewed panel whose arrow ignored the skew would lie.
                var area = live.AreaAngle;
                var skewed = Quaternion.Normalize(
                    rotation * Quaternion.CreateFromYawPitchRoll(
                        float.DegreesToRadians(area.X),
                        float.DegreesToRadians(area.Y),
                        0f));
                var throwZ = Vector3.Transform(Vector3.UnitZ, skewed);
                // An arrow with a crossbar for the panel it leaves. The bar is
                // struck on both side axes so it never collapses edge-on.
                DrawWorldArrow(
                    drawList, viewportPos, position, throwZ, localX, localY,
                    length, thickness, stroke);
                var barX = Vector3.Normalize(localX) * (length * 0.35f);
                var barY = Vector3.Normalize(localY) * (length * 0.35f);
                DrawWorldLine(
                    drawList, viewportPos,
                    position - barX, position + barX, thickness, stroke);
                DrawWorldLine(
                    drawList, viewportPos,
                    position - barY, position + barY, thickness, stroke);
                break;
            }
        }
    }

    private void DrawWorldLine(
        ImDrawListPtr drawList, Vector2 viewportPos,
        Vector3 start, Vector3 end, float thickness, Vector4 color)
    {
        if (_cameraService.WorldToScreen(start, out var startScreen) &&
            _cameraService.WorldToScreen(end, out var endScreen))
            drawList.AddLine(
                viewportPos + startScreen,
                viewportPos + endScreen,
                ImGui.ColorConvertFloat4ToU32(color),
                thickness);
    }

    /// <summary>A shaft with four barbs, two per side axis: an arrow drawn on
    /// one axis alone collapses to a line from half the angles a free camera
    /// can take, and the facing mark has to read from all of them.</summary>
    private void DrawWorldArrow(
        ImDrawListPtr drawList, Vector2 viewportPos, Vector3 origin,
        Vector3 direction, Vector3 sideOne, Vector3 sideTwo, float length,
        float thickness, Vector4 color)
    {
        var axis = Vector3.Normalize(direction);
        var tip = origin + axis * length;
        DrawWorldLine(drawList, viewportPos, origin, tip, thickness, color);

        float barb = length * 0.3f;
        var shoulder = tip - axis * barb;
        var spreadOne = Vector3.Normalize(sideOne) * (barb * 0.6f);
        var spreadTwo = Vector3.Normalize(sideTwo) * (barb * 0.6f);
        DrawWorldLine(drawList, viewportPos, tip, shoulder + spreadOne, thickness, color);
        DrawWorldLine(drawList, viewportPos, tip, shoulder - spreadOne, thickness, color);
        DrawWorldLine(drawList, viewportPos, tip, shoulder + spreadTwo, thickness, color);
        DrawWorldLine(drawList, viewportPos, tip, shoulder - spreadTwo, thickness, color);
    }

    private void DrawWorldCircle(
        ImDrawListPtr drawList, Vector2 viewportPos, Vector3 center,
        Vector3 axisOne, Vector3 axisTwo, float radius, float thickness,
        Vector4 color)
    {
        const int segments = 32;
        for (int i = 0; i <= segments; i++)
        {
            float angle = (float)i / segments * MathF.Tau;
            var point = center +
                (MathF.Cos(angle) * axisOne + MathF.Sin(angle) * axisTwo) * radius;
            // A point behind the camera simply contributes nothing; the path
            // closes over the segment that stayed on screen.
            if (_cameraService.WorldToScreen(point, out var screen))
                drawList.PathLineTo(viewportPos + screen);
        }
        drawList.PathStroke(ImGui.ColorConvertFloat4ToU32(color), ImDrawFlags.None, thickness);
        drawList.PathClear();
    }

    /// <summary>An open wire cone: the rim circle at <paramref name="height"/>
    /// and four spokes back to the apex. Four spokes, because two would
    /// collapse to a single stroke from the angles a free camera takes.</summary>
    private void DrawWorldCone(
        ImDrawListPtr drawList, Vector2 viewportPos, Vector3 apex,
        Vector3 localX, Vector3 localY, Vector3 localZ, float angleRadians,
        float height, float thickness, Vector4 color)
    {
        const int spokes = 4;
        var rimCenter = apex + localZ * height;
        float radius = height * MathF.Tan(angleRadians);
        DrawWorldCircle(
            drawList, viewportPos, rimCenter, localX, localY, radius,
            thickness, color);

        for (int spoke = 0; spoke < spokes; spoke++)
        {
            float angle = (float)spoke / spokes * MathF.Tau;
            var rim = rimCenter +
                (MathF.Cos(angle) * localX + MathF.Sin(angle) * localY) * radius;
            DrawWorldLine(drawList, viewportPos, apex, rim, thickness, color);
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
