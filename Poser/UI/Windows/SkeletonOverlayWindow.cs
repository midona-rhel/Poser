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
    private readonly WorldAdoptionSource _adoption;
    // Only for the inactive-actor fade: "active" can mean the GAME's target,
    // and the overlay has no other route to it.
    private readonly IActorManager _actorManager;

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
    private static bool LineToCircle => Config.SkeletonLineToCircle;
    private static bool HideSkeletonWhileDragging =>
        Config.HideSkeletonWhileDragging;
    private static bool ShowNsfwBones =>
        ConfigurationService.Instance.Config.Display.ShowNsfwBones;

    private static GizmoConfiguration GizmoConfig =>
        ConfigurationService.Instance.Config.Gizmo;

    /// <summary>Whether the configured hold modifier is down THIS frame. The
    /// unbound state is never "down", so the shipped configuration answers
    /// false without touching the IO flags.</summary>
    internal static bool HoldModifierDown(OverlayHoldModifier modifier)
    {
        if (modifier == OverlayHoldModifier.None)
            return false;
        var io = ImGui.GetIO();
        return modifier == OverlayHoldModifier.Ctrl ? io.KeyCtrl : io.KeyShift;
    }

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
        /// <summary>The owning actor's dimming multiplier — 1 unless the
        /// inactive-actor fade is on and this actor is not the active one.
        /// Held per handle because the draw passes iterate handles, not
        /// actors.</summary>
        public float Opacity;
    }

    private struct ActorDisplayData
    {
        public string Name;
        public SelectionId Id;
        public Vector2 ScreenPos;
        public float CameraDistance;
        public bool IsHovered;
        public float Opacity;
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

    /// <summary>One thing the world holds that the scene does not. It carries
    /// no SelectionId because it is not in the scene yet: its click adds it,
    /// and the ordinary handle it then gets is what selects it.</summary>
    private struct AdoptDisplayData
    {
        public string Name;
        public WorldAdoptionKind Kind;
        public int Candidate;
        public Vector2 ScreenPos;
        public float CameraDistance;
        public float Radius;
        public bool IsHovered;
    }

    private readonly HashSet<SelectionId> _selectedIds = new();
    private readonly List<BoneDisplayData> _bones = new();
    private readonly List<ActorDisplayData> _actors = new();
    private readonly List<LightDisplayData> _lights = new();
    private readonly List<AdoptDisplayData> _adopts = new();
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
    private WorldAdoptionCandidate? _pressedAdoptTarget;
    private PendingAdoption? _pendingAdoption;
    private const string HoverListOwnerId = "##skeleton-overlay-bones";

    private readonly record struct PendingSelection(
        SelectionId Id,
        Vector2 ReleasePoint,
        bool Additive,
        InteractionOwner Owner);

    /// <summary>An adoption a release asked for, committed on the NEXT frame
    /// against a freshly read listing — the selection path's rule, for the
    /// same reason: a release over something that has since gone must do
    /// nothing rather than adopt whatever took its place.</summary>
    private readonly record struct PendingAdoption(
        WorldAdoptionCandidate Candidate,
        Vector2 ReleasePoint);

    public SkeletonOverlayWindow(
        SceneSession scene,
        Game.Viewport.ViewportProjection viewport,
        ICameraService cameraService,
        IEditorState editorState,
        SkeletonOverlayPresentation presentation,
        Application.Posing.IIkConfigurationPort ikPort,
        StableBindingRegistry bindings,
        WorldAdoptionSource adoption,
        IActorManager actorManager,
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
        _adoption = adoption;
        _actorManager = actorManager;
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

    /// <summary>The toolbar's master overlay switch. With the toggle Off the
    /// overlay still anchors the current selection: selected bones and
    /// selected actor origins stay visible on their own, so an edit made
    /// from the workspace never loses its on-screen anchor. Everything
    /// unselected stays hidden and non-interactive.
    ///
    /// <para>It starts ON for each session (see <c>UiWindowSet</c>), so the
    /// sidebar's per-bone eyes are what normally decides, and this is how the
    /// whole armature is taken away at once.</para></summary>
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

        // Ahead of the Alt gate: the listing's cadence and the select that
        // finishes an adoption are bookkeeping, and holding Alt is a request
        // to see the world, not to suspend the scene.
        _adoption.Tick();

        // Holding Alt temporarily hides the skeleton dots for an unobstructed
        // view; the window stays open and interaction resumes on release.
        if (io.KeyAlt)
        {
            _pressedAdoptTarget = null;
            return;
        }

        // The ARMATURE pass answers to the sidebar's opted-in bones (the
        // Skeleton node and the finer eyes) and the selection anchor. The
        // LIGHT pass does not: a light is invisible in the world and its
        // handle is the only route to it from the viewport, so it draws
        // whenever the scene holds lights — Ktisis and Brio both draw their
        // light handles unconditionally. Alt still hides everything.
        bool drawArmature =
            (UserVisible && _presentation.AnyVisible) || AnySelectionAnchor();

        var selectedIds = _selectedIds;
        selectedIds.Clear();
        foreach (var id in _selection.Selected)
            selectedIds.Add(id);
        var bones = _bones;
        var actors = _actors;
        var lights = _lights;
        var adopts = _adopts;
        bones.Clear();
        actors.Clear();
        lights.Clear();
        adopts.Clear();
        var cameraPosition = _cameraService.GetCameraPosition();

        // Adoption handles: everything the world holds that the scene does
        // not. They sit UNDER the scene's own handles, in paint and in
        // pointer priority alike — what the scene already holds is what a
        // click over both is more likely to mean.
        var candidates = _adoption.Candidates;
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!_cameraService.WorldToScreen(candidate.Position, out var screen))
                continue;
            adopts.Add(new AdoptDisplayData
            {
                Name = candidate.Name,
                Kind = candidate.Kind,
                Candidate = i,
                ScreenPos = viewportPos + screen,
                CameraDistance = Vector3.Distance(
                    cameraPosition, candidate.Position),
                Radius = AdoptRadius(candidate.DistanceFromCamera),
            });
        }

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
                // A prop belongs to no actor, so the actor fade has nothing to
                // say about it.
                Opacity = 1f,
            });
        }

        // The dimming verdict is one question per frame — which lineage is
        // active — and one lookup per actor, so it is resolved before the two
        // actor passes rather than inside either.
        var activeLineage = ResolveActiveLineage();

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
                    Opacity = ActorOpacity(actor.Id, activeLineage),
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
            float armatureOpacity = ActorOpacity(actor.Id, activeLineage);

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

            var armedIkBones = CollectArmedIkBones(slotSkeleton.Id);
            bool showNsfw = ShowNsfwBones;

            var boneScreenPositions = _boneScreenPositions;
            var boneWorldPositions = _boneWorldPositions;
            boneScreenPositions.Clear();
            boneWorldPositions.Clear();
            foreach (var bone in descriptors)
            {
                // Opted-in bones draw while the master switch is on; a
                // SELECTED bone draws regardless — the anchor rule, which is
                // what stops the switch stranding an edit with no on-screen
                // handle.
                bool shown = UserVisible && _presentation.IsVisible(bone.Id);
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
                    IsIkChain = armedIkBones?.Contains(bone.Id.CanonicalName) == true,
                    Opacity = armatureOpacity,
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
        foreach (ref var adopt in CollectionsMarshal.AsSpan(adopts))
            adopt.IsHovered = !pointerBlocked
                && !listTravel
                && IsHoveringDot(adopt.ScreenPos, adopt.Radius);

        // Update hover state. Brio's Posing_DisableSkeleton — a held modifier
        // that leaves the dots painted and stops them answering the pointer,
        // so a gizmo handle underneath one can be grabbed — reads exactly like
        // an occluded pointer here, which is what makes it a one-line gate.
        bool dotsSuppressed =
            HoldModifierDown(GizmoConfig.DisableDotsModifier);
        if (pointerBlocked || dotsSuppressed)
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
        CommitPendingAdoption();

        // Filter bones if ShowSelectedBonesOnly is enabled
        if (_editorState.ShowSelectedBonesOnly)
            bones.RemoveAll(NotSelectedOrHovered);

        // Draw skeleton
        // The custom gizmo holds shared pointer ownership on hover AND
        // drag, so this single check covers both engagement states.
        var isGizmoActive = Controls.GizmoPointerOwnership.Owned;
        var lineOpacity = isGizmoActive ? LineOpacityWhileUsing : LineOpacity;
        // Brio's HideSkeletonWhenGizmoActive: the armature goes away for the
        // length of a drag rather than fading. Hover and press were resolved
        // above and stay resolved — the gizmo owns the pointer while it is
        // engaged anyway, so this is a paint decision only.
        bool paintArmature = !(isGizmoActive && HideSkeletonWhileDragging);

        // Under everything the scene owns; see the collection comment.
        DrawAdoptionHandles(drawList, adopts);

        if (paintArmature)
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
            if (actor.Opacity < 1f)
                color = SetAlpha(color, GetAlpha(color) * actor.Opacity);
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
        // An adoption handle answers only where no scene handle does.
        int hoveredAdoptIndex = hasHoveredActor || hasHoveredLight
            ? -1
            : NearestHovered(adopts);
        bool hasHoveredAdopt = hoveredAdoptIndex >= 0;
        // The world entity under the pointer wears the game's own mark, and
        // stops wearing it the moment the pointer leaves. Ktisis' rule, driven
        // from the same place: the hover is resolved once per frame and the
        // NULL case is stated on every frame it does not resolve
        // (SceneDraw.cs:84-87) — that null is what makes leaving clear it.
        var hoveredCandidates = _adoption.Candidates;
        int hoveredCandidateIndex = hasHoveredAdopt && !pointerBlocked
            ? adopts[hoveredAdoptIndex].Candidate
            : -1;
        _adoption.SetHovered(
            hoveredCandidateIndex >= 0 &&
            hoveredCandidateIndex < hoveredCandidates.Count
                ? hoveredCandidates[hoveredCandidateIndex]
                : null);
        if (hasHoveredAdopt && !pointerBlocked)
        {
            var adopt = adopts[hoveredAdoptIndex];
            var overlayMouse = ImGui.GetMousePos();
            // A hovered handle says its NAME and nothing else (user, round 4).
            // What kind of thing it is, the handle's own shape and colour
            // already say; a type suffix is the overlay describing itself
            // where the pointer asked what it was on.
            Crystarium.HoverHelp.Preview("sow-adopt",
                overlayMouse - new Vector2(4f, 4f),
                overlayMouse + new Vector2(4f, 4f),
                adopt.Name,
                animated: false);
        }
        if (hasHoveredLight && !pointerBlocked)
        {
            var overlayMouse = ImGui.GetMousePos();
            Crystarium.HoverHelp.Preview("sow-light",
                overlayMouse - new Vector2(4f, 4f), overlayMouse + new Vector2(4f, 4f),
                lights[hoveredLightIndex].Name, animated: false);
        }
        else if (hasHoveredActor && !pointerBlocked)
        {
            var hoveredActor = actors[hoveredActorIndex];
            var overlayMouse = ImGui.GetMousePos();
            Crystarium.HoverHelp.Preview("sow-actor",
                overlayMouse - new Vector2(4f, 4f), overlayMouse + new Vector2(4f, 4f),
                hoveredActor.Name,
                animated: false);
        }

        // Freeze the overlapping candidates and their anchor while the
        // pointer crosses into the explicit list.
        bool onFrozenCluster = listTravel && AnyHoveredIsFrozen(bones);
        UpdateHoveredBones(bones, mousePos, listTravel);
        bool hasWorldBone = !listTravel
            ? AnyHovered(bones)
            : onFrozenCluster;
        // The hover-list wheel. A notch steps the highlighted candidate and
        // wraps at both ends (CycleHoverIndex — Ktisis' step, single test per
        // side). It runs while the cluster is alive — over the dots OR
        // travelling into the list — and BEFORE the target is read below, so
        // the entry a release commits is the one the wheel just put under the
        // highlight. The wheel is claimed only when it actually moved the
        // highlight, so a single candidate leaves scrolling to whatever is
        // underneath.
        //
        // WHAT THE NOTCH COSTS is the one thing the references disagree on,
        // and it is the user's to choose (BonePickBehavior). Under Ktisis the
        // notch moves the highlight and nothing else. Under Brio the notch
        // SELECTS what it lands on (PosingOverlayWindow.DrawPopup:444-448
        // invokes the entry's OnClick on every wheel event), so the scene
        // selection walks the stack as the wheel turns. It is armed as a
        // pending selection rather than applied here so it goes through the
        // one commit path — same presence and occlusion tests as a click.
        if (_hoveredBones.Count > 1 && (hasWorldBone || listTravel)
            && io.MouseWheel != 0f)
        {
            _hoverIndex = CycleHoverIndex(
                _hoverIndex, _hoveredBones.Count, io.MouseWheel);
            if (Config.BonePickBehavior == BonePickBehavior.Brio)
                _pendingSelection = new PendingSelection(
                    _hoveredBones[_hoverIndex].Id,
                    ImGui.GetMousePos(),
                    io.KeyCtrl,
                    new InteractionOwner(
                        HoverListOwnerId,
                        InteractionLayer.OverlaySurface,
                        int.MaxValue));
            io.WantCaptureMouse = true;
            ImGui.SetNextFrameWantCaptureMouse(true);
        }
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
        var adoptTarget = hasHoveredAdopt
            ? candidates[adopts[hoveredAdoptIndex].Candidate]
            : (WorldAdoptionCandidate?)null;
        if (worldTarget != null || _pressedWorldTarget != null
            || adoptTarget != null || _pressedAdoptTarget != null)
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
            pointerBlocked || dotsSuppressed
                || (listTravel && !hasWorldBone));
        UpdateAdoptPress(adoptTarget, pointerBlocked || listTravel);
        if (_hoveredBones.Count > 0)
            DrawHoverList();
    }

    // ── inactive-actor dimming (Ktisis SceneDraw.GetOpacityMultiplier) ────

    /// <summary>
    /// The lineage the overlay treats as active this frame, or null when the
    /// fade is off or nothing qualifies. Ktisis' <c>ActiveState</c> reads as
    /// written: Target asks the game, Selection asks the app, and Both accepts
    /// either — so Both resolves the target FIRST and falls back to the
    /// selection, which is the only reading under which "both" can name one
    /// actor.
    /// </summary>
    private Guid? ResolveActiveLineage()
    {
        if (!Config.DimInactiveActors)
            return null;
        var source = Config.ActiveActorSource;
        if (source is ActiveActorSource.Target or ActiveActorSource.Both
            && _actors_GPoseTargetLineage() is { } targetLineage)
            return targetLineage;
        if (source is ActiveActorSource.Selection or ActiveActorSource.Both)
            return SelectedActorLineage();
        return null;
    }

    /// <summary>The GAME's target as a stable lineage, or null when there is
    /// none or it has no binding.</summary>
    private Guid? _actors_GPoseTargetLineage() =>
        _actorManager.GetGPoseTarget() is { } target
        && _bindings.GetActorId(target) is { } id
            ? id.LogicalId
            : null;

    /// <summary>The actor the current selection belongs to — a selected bone
    /// names its actor exactly as a selected actor does, so posing one actor
    /// keeps that actor lit.</summary>
    private Guid? SelectedActorLineage()
    {
        foreach (var id in _selection.Selected)
        {
            if (id.Bone is { } bone)
                return bone.Skeleton.Actor.LogicalId;
            if (id.Actor is { } actor
                && id.Kind is SceneEntityKind.Actor or SceneEntityKind.GazeTarget)
                return actor.LogicalId;
        }
        return null;
    }

    /// <summary>One actor's fade: full while it is the active one or while
    /// nothing is active, the configured multiplier otherwise.</summary>
    private static float ActorOpacity(ActorId actor, Guid? activeLineage) =>
        activeLineage is not { } active || actor.LogicalId == active
            ? 1f
            : Math.Clamp(Config.InactiveActorOpacity, 0f, 1f);

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

    private static int NearestHovered(List<AdoptDisplayData> adopts)
    {
        int best = -1;
        float bestDistance = 0f;
        for (int i = 0; i < adopts.Count; i++)
        {
            var adopt = adopts[i];
            if (!adopt.IsHovered)
                continue;
            if (best < 0 || adopt.CameraDistance < bestDistance)
            {
                best = i;
                bestDistance = adopt.CameraDistance;
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

    /// <summary>
    /// Ktisis' <c>ScrollIndex</c> step, exactly: the wheel notch is
    /// SUBTRACTED, so pushing the wheel away walks toward the front of the
    /// list, and an index that leaves either end lands on the opposite one
    /// rather than being clamped — a cluster of two is two entries the wheel
    /// alternates between, in both directions.
    ///
    /// <para>Ktisis' own wrap is a single test per side, so a burst that
    /// overshoots by more than one lands on the far end rather than walking
    /// modulo the count. That is reproduced, not corrected: the list is a
    /// handful of dots and "wheel hard, land at the end" is the behaviour a
    /// Ktisis user's hand already has.</para>
    ///
    /// <para>BOTH pick behaviours share this step, because the references
    /// agree on it: Brio's popup walks the same direction and wraps by the
    /// same single test per side
    /// (<c>PosingOverlayWindow.DrawPopup:428-442</c>). What they disagree
    /// about is what a notch COSTS, which is decided at the call site.</para>
    /// </summary>
    internal static int CycleHoverIndex(int index, int count, float wheel)
    {
        if (count <= 0)
            return 0;
        int next = index - (int)wheel;
        if (next >= count)
            return 0;
        if (next < 0)
            return count - 1;
        return next;
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

    // ── adoption handles ─────────────────────────────────────────────────
    // Ktisis' world overlay, in Poser's chrome: a hollow polygon for every
    // world thing the scene has not taken, one shape per class (SceneDraw.cs
    // draws a pentagon for an actor and a triangle for a light), shrinking
    // with distance and culled past the listing's range. Hollow and dim is the
    // whole visual argument — the scene's own handles are filled and accented,
    // so "not yours yet" reads before any tooltip does.

    /// <summary>The resting handle radius, before UI scale, for something
    /// standing next to the player; it falls to <see cref="AdoptFarScale"/> of
    /// that at the listing's range, so the far half of a listing reads as
    /// marks around whatever is close rather than a field of equal blots.
    /// </summary>
    private const float AdoptNearRadius = 7f;
    private const float AdoptFarScale = 0.6f;

    private static float AdoptRadius(float distanceFromPlayer)
    {
        float t = Math.Clamp(
            distanceFromPlayer / WorldAdoptionSource.RangeYalms, 0f, 1f);
        return AdoptNearRadius
            * float.Lerp(1f, AdoptFarScale, t)
            * ImGuiHelpers.GlobalScale;
    }

    /// <summary>Sides of the polygon that says which class a handle stands
    /// for: five for an actor, three for a light, four for a map object —
    /// Ktisis' own shapes (SceneDraw.cs:208 draws the map object as a 4-gon,
    /// :251 the actor as a 5-gon, :296 the light as a 3-gon).</summary>
    private static int AdoptSides(WorldAdoptionKind kind) => kind switch
    {
        WorldAdoptionKind.Light => 3,
        WorldAdoptionKind.WorldObject => 4,
        _ => 5,
    };

    private static void DrawAdoptionHandles(
        ImDrawListPtr drawList, List<AdoptDisplayData> adopts)
    {
        if (adopts.Count == 0)
            return;
        var theme = Crystarium.ActiveTheme;
        uint resting = ImGui.ColorConvertFloat4ToU32(theme.TextDim);
        uint engaged = ImGui.ColorConvertFloat4ToU32(theme.Accent);

        foreach (var adopt in adopts)
        {
            int sides = AdoptSides(adopt.Kind);
            float radius = adopt.IsHovered
                ? adopt.Radius + 2f * ImGuiHelpers.GlobalScale
                : adopt.Radius;
            uint stroke = adopt.IsHovered ? engaged : resting;
            // The fill is a hint of one, so the shape reads against a bright
            // background without ever looking like a scene handle.
            uint fill = SetAlpha(stroke, adopt.IsHovered ? 0.45f : 0.15f);

            drawList.AddCircleFilled(adopt.ScreenPos, radius, fill, sides);
            drawList.AddCircle(
                adopt.ScreenPos, radius + 1f * ImGuiHelpers.GlobalScale,
                OutlineColor, sides, 1f * ImGuiHelpers.GlobalScale);
            drawList.AddCircle(
                adopt.ScreenPos, radius, stroke, sides,
                (adopt.IsHovered ? 2f : 1.5f) * ImGuiHelpers.GlobalScale);
        }
    }

    /// <summary>The adoption press, tracked exactly like the world press: a
    /// click arms a target and only a release over that SAME target asks for
    /// it, so a drag off a handle cancels.</summary>
    private void UpdateAdoptPress(
        WorldAdoptionCandidate? target,
        bool pointerBlocked)
    {
        if (pointerBlocked || Controls.GizmoPointerOwnership.Owned)
        {
            _pressedAdoptTarget = null;
            return;
        }

        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            _pressedAdoptTarget = target;

        if (!ImGui.IsMouseReleased(ImGuiMouseButton.Left))
            return;
        if (_pressedAdoptTarget is { } pressed
            && target is { } released
            && SameCandidate(pressed, released))
        {
            _pendingAdoption = new PendingAdoption(
                released, ImGui.GetMousePos());
        }
        _pressedAdoptTarget = null;
    }

    /// <summary>Commits the release from the previous frame against the
    /// listing THIS frame read: a candidate that has gone is dropped, not
    /// adopted, and a release the pointer had already left is refused.</summary>
    private void CommitPendingAdoption()
    {
        if (_pendingAdoption is not { } pending)
            return;
        _pendingAdoption = null;
        bool stillListed = false;
        var candidates = _adoption.Candidates;
        for (int i = 0; !stillListed && i < candidates.Count; i++)
            stillListed = SameCandidate(candidates[i], pending.Candidate);
        bool releaseOccluded = Interactive.PointerOccluded(
            InteractionOwner.World, pending.ReleasePoint);
        _log.Debug(
            $"[Overlay] adopt {pending.Candidate.Kind} "
            + $"'{pending.Candidate.Name}' listed={stillListed} "
            + $"occluded={releaseOccluded}");
        if (!stillListed || releaseOccluded)
            return;
        _adoption.Adopt(pending.Candidate);
    }

    /// <summary>Identity of an adoption row: the listing key alone. Position
    /// and distance drift between passes and say nothing about which thing
    /// this is.</summary>
    private static bool SameCandidate(
        in WorldAdoptionCandidate left, in WorldAdoptionCandidate right) =>
        left.Kind == right.Kind
        && left.Actor.Equals(right.Actor)
        && left.Light.Handle == right.Light.Handle
        && left.WorldObject == right.WorldObject;

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
    /// exact skeleton. Null when no chain on the skeleton is enabled. ONE port
    /// read per skeleton per frame: since CCD can be armed on any bone, asking
    /// bone by bone would be a probe of the whole skeleton.</summary>
    private HashSet<string>? CollectArmedIkBones(Domain.Identity.SkeletonId skeleton)
    {
        HashSet<string>? names = null;
        foreach (var chain in _ikPort.Chains(skeleton))
        {
            if (!chain.Config.Enabled)
                continue;
            names ??= new HashSet<string>();
            foreach (var bone in chain.Bones)
                names.Add(bone);
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
        bool toCircle = LineToCircle;
        float radius = DotRadius;

        foreach (var bone in bones)
        {
            if (bone.ParentScreenPos == null) continue;
            // Ktisis style: bone color with opacity, times the owning actor's
            // inactive fade.
            var color = SetAlpha(BoneColor, opacity * bone.Opacity);
            var from = bone.ParentScreenPos.Value;
            var to = bone.ScreenPos;
            if (toCircle)
            {
                // Brio's SkeletonLineToCircle, its guard included: two dots
                // closer together than their combined diameter get NO
                // connector, because pulling both ends back past each other
                // would draw the segment inside out.
                float diameter = radius * 2f;
                if (Vector2.DistanceSquared(from, to) < diameter * diameter)
                    continue;
                var shortened = ShrinkToCircles(from, to, radius - 1f);
                from = shortened.From;
                to = shortened.To;
            }
            drawList.AddLine(from, to, color, LineThickness);
        }
    }

    /// <summary>Both ends of a segment pulled back by <paramref name="inset"/>
    /// along its own direction — Brio's <c>PointAlongLine</c> applied at each
    /// end, so a connector meets the two circles' edges instead of their
    /// centres. A degenerate segment is returned untouched.</summary>
    internal static (Vector2 From, Vector2 To) ShrinkToCircles(
        Vector2 from, Vector2 to, float inset)
    {
        var direction = to - from;
        float length = direction.Length();
        if (length < 1e-3f || inset <= 0f)
            return (from, to);
        var step = direction / length * inset;
        return (from + step, to - step);
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
            if (bone.Opacity < 1f)
                color = SetAlpha(color, GetAlpha(color) * bone.Opacity);

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

            float bodyOpacity = opacity * bone.Opacity;
            var fillColor = SetAlpha(color, GetAlpha(color) * 0.5f * bodyOpacity);
            var edgeColor = SetAlpha(color, bodyOpacity);

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
            if (bone.Opacity < 1f)
                color = SetAlpha(color, GetAlpha(color) * bone.Opacity);
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
