using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
using Poser.Domain.Presentation;
using Poser.Domain.Scene;
using Poser.Domain.Transforms;
using Poser.Entities;
using Poser.Domain.Companions;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>The shell wiring: every delegate the view model and the panes
/// call back into the window through, set once at construction.</summary>
public partial class MainWindow
{
    private void WireShell(GraphicalBonePane graphicalBonePane, AnimationPane animationPane)
    {
        _poseInspector.DrawMapInline = graphicalBonePane.DrawInline;
        _poseInspector.BuildBoneChoices = BuildCameraBoneChoices;
        _poseInspector.DrawExpressionRow = animationPane.DrawExpressionRow;
        graphicalBonePane.SidesSwapped =
            _configuration.Config.UI.MapMirrorSelection;
        _poseInspector.GetMapMirror = () => graphicalBonePane.SidesSwapped;
        _poseInspector.SetMapMirror = on =>
        {
            graphicalBonePane.SidesSwapped = on;
            _configuration.Config.UI.MapMirrorSelection = on;
            _configuration.Save();
        };
        _poseInspector.GetSwapRotationXY = () =>
            _configuration.Config.UI.SwapRotationXY;
        _selection.Live.CompanionResolver = ResolveSiblingBone;
        _vm.OnCollapse = collapsed =>
        {
            if (collapsed) _savedHeight = ImGui.GetWindowSize().Y / ImGuiHelpers.GlobalScale;
            else _restorePending = true;
            _collapsed = collapsed;
        };
        // Static shell wiring (rebuilt data lives in BuildViewModel each frame).
        _vm.OnTab = OnTabClicked;
        _vm.OnRowDrop = OnRowDropped;
        // A click on the tree's open space drops the whole selection.
        _vm.OnEmptyClick = () => _selection.Clear();
        _vm.DragGhostText = DragGhostFor;
        // Brio's Bullseye (CameraEditor.cs recenter_on_selected): the seat
        // RETARGETS this camera's tracking onto the currently selected
        // actor, aim offset corrected to the drawn body — it never merely
        // swings the camera.
        _vm.OnCameraRecenter = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.Camera, Camera: { } recenterId })
                return;
            if (SelectedActorRef() is not { Actor: { } trackActorId })
                return;
            string trackLabel = _scene.Snapshot.FindActor(trackActorId.LogicalId) is { } tracked
                ? ActorNames.Display(_configuration, tracked)
                : "Actor";
            _cameraPane.FollowActor(trackActorId, trackLabel, recenterId);
        };
        _vm.OnGroupLock = row =>
        {
            if (row.Tag is GroupRowTag lockTag
                && _groups.Find(lockTag.Id) is { } lockGroup)
                _groupSteps.SetLocked(lockTag.Id, !lockGroup.Locked);
        };
        _vm.OnGroupVisibility = row =>
        {
            if (row.Tag is GroupRowTag tag && _groups.Find(tag.Id) is { } group)
                _groupSteps.SetHidden(group, !group.Hidden);
        };
        _vm.OnGroupPause = row =>
        {
            if (row.Tag is GroupRowTag tag && _groups.Find(tag.Id) is { } group)
                _groupSteps.SetPaused(group, !group.Paused);
        };
        _vm.OnGizmoOperation = i => _editorState.TransformTool = (TransformTool)i;
        _vm.OnGizmoSpace = i => _editorState.TransformOrientation = (TransformOrientation)i;
        _vm.OnRotationPivot = i => _editorState.RotationPivot = (Core.RotationPivot)i;
        _vm.OnSymmetry = i =>
        {
            var mode = (SymmetryMode)i;
            var configuration =
                _configuration.Config;
            // With the per-bone sheet on, the toolbar EDITS the selected
            // bones' own stated mode — clicking their stated value again
            // clears it back to the toolbar's global. No bones selected
            // (or sheet off) edits the global, as ever.
            if (configuration.PerBoneSymmetry)
            {
                bool wroteAny = false;
                foreach (var member in _scene.Selection.Selected)
                {
                    if (member.Bone is not { } stated)
                        continue;
                    wroteAny = true;
                    if (configuration.BoneSymmetryOverrides.TryGetValue(
                            stated.CanonicalName, out var current)
                        && current == mode)
                        configuration.BoneSymmetryOverrides.Remove(
                            stated.CanonicalName);
                    else
                        configuration.BoneSymmetryOverrides[
                            stated.CanonicalName] = mode;
                }
                if (wroteAny)
                {
                    _configuration.Save();
                    return;
                }
            }
            _editorState.SymmetryMode = mode;
        };
        // The switch's polarity is "animation playing"; off writes a zero
        // speed override, on drops the override back to game speed.
        _vm.OnAnimation = on =>
        {
            if (SelectedActorId() is { } actor)
            {
                if (on) _animation.ClearSpeed(actor);
                else _animation.SetSpeed(actor, 0f);
            }
        };
        // Physics freeze is process-global and independent of selection.
        _vm.OnPhysics = on => _animation.SetScenePhysicsFrozen(!on);
        // The footer's class glyphs are minted once and restated in place; the
        // list never changes shape, so the shell never rebuilds it.
        foreach (var (_, entry) in _worldClasses)
            _vm.WorldClasses.Add(entry);
        _vm.OnWorldClassToggle = ToggleWorldClass;
        _vm.OnUndo = Undo;
        _vm.OnRedo = Redo;
        _vm.OnSettings = () => OnSettingsRequested?.Invoke();
        _vm.OnBurger = anchor =>
        {
            _shellMenuAnchor = anchor;
            _shellMenuOpenRequested = true;
        };
        // Detached: the X closes just this Inspector window — the strip
        // reopens it. Attached: the X hides the whole UI as ever.
        _vm.OnHideUi = () =>
        {
            if (_configuration.Config.UI.DetachedShell)
                ContentHidden = true;
            else
                IsOpen = false;
        };
        _vm.OnSidebarAttachToggle = RequestDetachToggle;
        _vm.OnInspectorAttachToggle =
            () => OnInspectorSplitToggleRequested?.Invoke();
        _vm.DrawFooterMiddle = DrawFooterMiddle;
        // Each section plus opens the shared spawn browser on that section's
        // tab, anchored to the button that opened it.
        _vm.OnSectionPlus = (index, anchor) =>
        {
            if (index == PropsSectionIndex)
                OnSpawnBrowserRequested?.Invoke(anchor, SpawnBrowserTab.Props);
            else if (index == LightsSectionIndex)
                OnSpawnBrowserRequested?.Invoke(anchor, SpawnBrowserTab.Lights);
            else if (index == CamerasSectionIndex)
                OnSpawnBrowserRequested?.Invoke(anchor, SpawnBrowserTab.Cameras);
            else if (index == ActorsSectionIndex)
                OnSpawnBrowserRequested?.Invoke(anchor, SpawnBrowserTab.Actors);
            else if (index == OverlaysSectionIndex)
                OnSpawnBrowserRequested?.Invoke(
                    anchor, SpawnBrowserTab.Overlays);
        };
        // The library, scene and environment headers are the selectable ones,
        // so no other index can arrive. The library and the scene workspace are
        // modes over an untouched selection, and their openers already restate
        // the layout, so those two branches do nothing else here. The
        // environment is a scene entity, so its header selects exactly as a row
        // does — leaving both modes first, because they are alternatives in one
        // workspace and the environment's own tab strip cannot show through
        // theirs — and it carries the one resync those exits do not make.
        _vm.OnSectionSelected = index =>
        {
        };
        _vm.OnSpawn = anchor =>
            OnSpawnBrowserRequested?.Invoke(anchor, SpawnBrowserTab.All);
        _vm.OnRowClicked = OnRowClicked;
        _vm.OnRowExpandToggled = row =>
        {
            if (row.ExpandKey is not { } expandKey)
                return;
            _expandVersion++;
            if (!_collapsedNodes.Add(expandKey))
                _collapsedNodes.Remove(expandKey);
        };
        _vm.OnSidebarResize = w => _sidebarWidth = w;
        _vm.OnSidebarCollapse = v => _sidebarCollapsed = v;
        _vm.OnRowContextMenu = row =>
        {
            // A right-click on a row that RIDES the multi-entity selection
            // opens the selection's own menu — the verbs speak for the
            // whole carry, exactly as a drag does. An unselected row keeps
            // its single menu.
            if (row.Tag is SelectionId ctxMember
                && global::Poser.Application.Selection.EntitySelection
                    .IsEntity(ctxMember.Kind)
                && _selection.IsSelected(ctxMember)
                && global::Poser.Application.Selection.EntitySelection
                    .CountEntities(_selection.Selected) >= 2)
            {
                _selectionCtxOpenRequested = true;
            }
            else if (row.Tag is GroupRowTag ctxGroup)
            {
                _ctxGroupId = ctxGroup.Id;
                _groupCtxOpenRequested = true;
            }
            else if (row.Tag is SelectionId
                { Kind: SceneEntityKind.WorldObject, WorldObject: { } ctxWorld })
            {
                _ctxWorldObjectId = ctxWorld;
                _worldObjectCtxOpenRequested = true;
            }
            else if (row.Tag is SelectionId { Kind: SceneEntityKind.Actor, Actor: { } ctxActor })
            {
                _ctxActorId = ctxActor;
                _ctxOpenRequested = true;
            }
            else if (row.SkeletonContext is { } ctxSkeleton)
            {
                _ctxBranchSkeleton = ctxSkeleton;
                _ctxBranchLabel = row.Label;
                _ctxBranchExpandKey = row.ExpandKey;
                _ctxOverlayBones = row.OverlayBones;
                _ctxOverlayMemoryKey = row.OverlayMemoryKey;
                _overlayCtxOpenRequested = true;
            }
            else if (row.Tag is SelectionId { Kind: SceneEntityKind.Bone, Bone: { } ctxBone })
            {
                _ctxBoneId = ctxBone;
                _ctxBoneOverlayBones = row.OverlayBones;
                _ctxBoneExpandKey = row.HasChildren ? row.ExpandKey : null;
                _boneCtxOpenRequested = true;
            }
            else if (row.Tag is SelectionId
                { Kind: SceneEntityKind.Light, Light: { } ctxLight })
            {
                _ctxLightId = ctxLight;
                _lightCtxOpenRequested = true;
            }
            else if (row.Tag is SelectionId
                { Kind: SceneEntityKind.Camera, Camera: { } ctxCamera })
            {
                _ctxCameraId = ctxCamera;
                _cameraCtxOpenRequested = true;
            }
            else if (row.Tag is SelectionId
                { Kind: SceneEntityKind.Prop, Prop: { } ctxProp })
            {
                _ctxPropId = ctxProp;
                _propCtxOpenRequested = true;
            }
            else if (row.Tag is SelectionId
                { Kind: SceneEntityKind.Overlay, Overlay: { } ctxOverlayNode })
            {
                _ctxOverlayNodeId = ctxOverlayNode;
                _overlayNodeCtxOpenRequested = true;
            }
            else if (row.Tag is ReferenceImageInstance ctxImage)
            {
                _ctxReferenceImage = ctxImage;
                _referenceCtxOpenRequested = true;
            }
            else if (row.OverlayBones != null)
            {
                _ctxBranchSkeleton = null;
                _ctxBranchLabel = row.Label;
                _ctxBranchExpandKey = row.ExpandKey;
                _ctxOverlayBones = row.OverlayBones;
                _ctxOverlayMemoryKey = row.OverlayMemoryKey;
                _overlayCtxOpenRequested = true;
            }
        };
        _vm.OnActorTarget = row =>
        {
            if (ResolveActorRow(row) is { } actor)
                _actorControl.SetGameTarget(actor.Id);
        };
        _vm.OnActorVisibility = row =>
        {
            if (row.Tag is SelectionId { Actor: { } actorId }
                && ResolveActorRow(row) is { } actor)
                SetEntityVisible(SelectionId.ForActor(actorId),
                    !(IsEntityVisible(SelectionId.ForActor(actorId)) ?? false));
        };
        _vm.OnActorPause = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.Actor, Actor: { } actor })
                return;
            if (!_animation.IsSupported(actor))
                return;
            if (_animation.AnyPlaying(actor))
                _animation.Pause(actor);
            else
                _animation.Resume(actor);
        };
        // The light's own on/off, reachable without selecting it first —
        // the same reach the actor eye has. IsOn participates in the scene
        // signature, so the toggle republishes the scene on the next refresh;
        // the warm-frame flag restate lands the eye's new state immediately.
        // The manip-handle toggle every entity row leads with: purely a
        // presentation mask over the world overlay, read live like the
        // overlay eyes.
        _vm.IsHandleShown = row => row.Tag switch
        {
            SelectionId handleId => _overlayPresentation.IsHandleShown(handleId),
            GroupRowTag group => _overlayPresentation.IsHandleShown(group.Id),
            _ => true,
        };
        _vm.OnHandleToggle = row =>
        {
            if (row.Tag is SelectionId handleId)
                _overlayPresentation.ToggleHandle(handleId);
            else if (row.Tag is GroupRowTag group)
                _overlayPresentation.ToggleHandle(group.Id);
        };
        // The effect row's pause seat: the same freeze the properties
        // page states, reachable without selecting first.
        _vm.OnRowPause = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.WorldObject, WorldObject: { } pausedId })
                return;
            if (_objectControl.Read(pausedId) is not { } handle)
                return;
            if (!handle.IsVfx)
                return;
            _objectControl.SetVfxPaused(pausedId, !handle.VfxPaused);
            row.Paused = _objectControl.Read(pausedId)?.VfxPaused ?? row.Paused;
        };
        // The scenery row's sun/moon seat: the same night state the
        // properties page switches.
        _vm.OnRowNight = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.WorldObject, WorldObject: { } nightId })
                return;
            if (_objectControl.Read(nightId) is not { IsVfx: false } handle)
                return;
            _objectControl.SetNightState(nightId, !handle.NightState);
            row.Night = _objectControl.Read(nightId)?.NightState ?? row.Night;
        };
        _vm.OnColliderCollision = row =>
        {
            if (row.Tag is SelectionId { Overlay: { } id }
                && _overlayControl.Read(id) is { State.Collider: { } collider })
            {
                _overlayControl.SetCollisionEnabled(id, !collider.Enabled);
                row.CollisionEnabled = !collider.Enabled;
            }
        };
        _vm.OnColliderLock = row =>
        {
            if (row.Tag is SelectionId { Overlay: { } id }
                && _overlayControl.Read(id) is { State.Collider: { } collider })
            {
                _overlayControl.SetColliderLocked(id, !collider.Locked);
                row.ColliderLocked = !collider.Locked;
            }
        };
        _vm.OnLightVisibility = row =>
        {
            // A reference picture wears the same eye seat: its toggle is
            // whether the window stands. Hidden is not closed — the entry, its
            // placement and its opacity all survive, which is what makes this
            // a toggle rather than a delete.
            if (row.Tag is ReferenceImageInstance eyeImage)
            {
                bool nextShown = ReferenceImageSession.IsHidden(eyeImage);
                _referenceImages.SetHidden(eyeImage, !nextShown);
                row.LightOn = nextShown;
                return;
            }
            // A prop row wears the same eye seat: its toggle is draw
            // visibility rather than a light's on-state.
            if (row.Tag is SelectionId
                { Kind: SceneEntityKind.Prop, Prop: { } propId })
            {
                bool next = !(IsEntityVisible(SelectionId.ForProp(propId)) ?? false);
                if (SetEntityVisible(SelectionId.ForProp(propId), next) > 0)
                    row.LightOn = next;
                return;
            }
            // An overlay row wears the same eye seat as a prop's: its toggle
            // is whether the node is drawn.
            if (row.Tag is SelectionId
                { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId })
            {
                bool next = !(IsEntityVisible(SelectionId.ForOverlay(overlayId)) ?? false);
                if (SetEntityVisible(SelectionId.ForOverlay(overlayId), next) > 0)
                    row.LightOn = next;
                return;
            }
            // A borrowed map object wears the same eye seat as a prop's: its
            // toggle is whether the map draws it. The release restores the
            // captured state.
            if (row.Tag is SelectionId
                { Kind: SceneEntityKind.WorldObject, WorldObject: { } worldObjectId })
            {
                bool next = !(IsEntityVisible(SelectionId.ForWorldObject(worldObjectId)) ?? false);
                if (SetEntityVisible(SelectionId.ForWorldObject(worldObjectId), next) > 0)
                    row.LightOn = next;
                return;
            }
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.Light, Light: { } lightId })
                return;
            bool visible = IsEntityVisible(SelectionId.ForLight(lightId)) ?? false;
            if (SetEntityVisible(SelectionId.ForLight(lightId), !visible) > 0)
                row.LightOn = !visible;
        };
        // The camera's inline verb, reachable without selecting it first:
        // make this the live camera, or step the live one back to the main
        // camera. Liveness participates in the scene signature, so the toggle
        // republishes on the next refresh; the warm-frame flag restate lands
        // the glyph's new state immediately.
        _vm.OnCameraLive = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.Camera, Camera: { } rowCameraId })
                return;
            if (_cameraControl.Read(rowCameraId) is not { } camera)
                return;
            _cameraControl.SetLive(rowCameraId, !camera.IsLive);
            row.CameraLive = _cameraControl.Read(rowCameraId)?.IsLive ?? row.CameraLive;
        };
        _vm.OnCameraLock = row =>
        {
            if (row.Tag is not SelectionId { Camera: { } rowCameraId }
                || _cameraControl.Read(rowCameraId) is not { } camera)
                return;
            _cameraControl.SetLocked(rowCameraId, !camera.IsLocked);
            row.CameraLocked = _cameraControl.Read(rowCameraId)?.IsLocked ?? row.CameraLocked;
        };
        _vm.OnOverlayVisibility = row =>
        {
            if (row.OverlayBones is not { } bones)
                return;
            if (row.OverlayMemoryKey is { } key)
                _overlayPresentation.ToggleVisibleWithMemory(key, bones);
            else
                _overlayPresentation.SetVisible(
                    bones, !_overlayPresentation.AreVisible(bones));
        };
        _vm.OverlayVisibilityOf =
            bones => (int)_overlayPresentation.Resolve(bones);
        _vm.DrawContent = DrawTabContent;
    }
}
