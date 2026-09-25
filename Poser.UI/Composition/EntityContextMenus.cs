using System;
using Poser.Application.Viewport;
using Poser.Application.Animation;
using Poser.Application.Gaze;
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

/// <summary>Entity menu targets, composition and disclosure; invokes shared application controls.</summary>
internal sealed partial class EntityContextMenus
{
    private readonly Application.Posing.IActorColliderCapture _actorColliderCapture;
    private readonly IActorSceneControl _actorControl;
    private readonly IAnimationPlayback _animation;
    private readonly BoneVisibilityPresetService _bonePresets;
    private readonly Application.Presentation.ICameraControl _cameraControl;
    private readonly CameraPane _cameraPane;
    private readonly Application.Posing.IPoseCommands _cleanPose;
    private readonly CompanionSection _companions;
    private readonly global::Poser.Config.ConfigurationService _configuration;
    private readonly global::Poser.Application.Scene.GroupSteps _groupSteps;
    private readonly global::Poser.Application.Scene.SceneGroups _groups;
    private readonly Application.Presentation.ILightControl _lightControl;
    private readonly LightPane _lightPane;
    private readonly Dalamud.Plugin.Services.IPluginLog _log;
    private readonly UserNotices _notices;
    private readonly Application.Presentation.ISceneObjectControl _objectControl;
    private readonly Application.Presentation.IOverlayControl _overlayControl;
    private readonly SkeletonOverlayPresentation _overlayPresentation;
    private readonly ReferenceImageSession _referenceImages;
    private readonly SceneSession _scene;
    private readonly ScenePane _scenePane;
    private readonly SelectionSession _selection;
    private readonly SidebarComposer _sidebar;
    private readonly PoseFileInspectorSection _poseFileSection;
    private readonly ISceneDuplication _duplication;
    private readonly Application.Selection.SelectionEntityCommands _entityCommands;
    private readonly EntityActions _entityActions;
    private readonly IScenePlaybackControl _playback;
    private readonly Application.Presentation.ICameraTargetControl _cameraTargets;
    private readonly Action _openSkeletonSettings;
    private readonly EntityRemovalDialog _removalDialog;
    private readonly Application.Transforms.ISelectionPlacement _placement;
    private ActorId? _ctxActorId;
    private bool _ctxOpenRequested;

    private BoneId? _ctxBoneId;

    private IReadOnlyList<BoneId>? _ctxBoneOverlayBones;

    private bool _boneCtxOpenRequested;

    private IReadOnlyList<BoneId>? _ctxOverlayBones;

    private string? _ctxOverlayMemoryKey;

    private bool _overlayCtxOpenRequested;

    // Bone-visibility presets: the menu applies them to one actor, the manager
    // owns the shared store. Both hold an id, never a descriptor.
    private ActorId? _presetActorId;

    private readonly List<ContextMenuItem> _bonePresetItems = [];

    private readonly List<Action?> _bonePresetActions = [];

    private bool _presetManagerOpen;

    private string _presetNameValue = "";

    private string? _presetSaveNote;


    public EntityContextMenus(
        Application.Posing.IActorColliderCapture actorColliderCapture,
        IActorSceneControl actorControl,
        IAnimationPlayback animation,
        BoneVisibilityPresetService bonePresets,
        Application.Presentation.ICameraControl cameraControl,
        CameraPane cameraPane,
        Application.Posing.IPoseCommands cleanPose,
        CompanionSection companions,
        global::Poser.Config.ConfigurationService configuration,
        global::Poser.Application.Scene.GroupSteps groupSteps,
        global::Poser.Application.Scene.SceneGroups groups,
        Application.Presentation.ILightControl lightControl,
        LightPane lightPane,
        Dalamud.Plugin.Services.IPluginLog log,
        UserNotices notices,
        Application.Presentation.ISceneObjectControl objectControl,
        Application.Presentation.IOverlayControl overlayControl,
        SkeletonOverlayPresentation overlayPresentation,
        ReferenceImageSession referenceImages,
        SceneSession scene,
        ScenePane scenePane,
        SelectionSession selection,
        SidebarComposer sidebar,
        PoseFileInspectorSection poseFileSection,
        ISceneDuplication duplication,
        Application.Selection.SelectionEntityCommands entityCommands,
        EntityActions entityActions,
        IScenePlaybackControl playback,
        Application.Presentation.ICameraTargetControl cameraTargets,
        Controls.EntityNameModal names,
        Controls.IssueReportModal issueReport,
        Action openSkeletonSettings, EntityRemovalDialog removalDialog,
        Application.Transforms.ISelectionPlacement placement)
    {
        _actorColliderCapture = actorColliderCapture;
        _actorControl = actorControl;
        _animation = animation;
        _bonePresets = bonePresets;
        _cameraControl = cameraControl;
        _cameraPane = cameraPane;
        _cleanPose = cleanPose;
        _companions = companions;
        _configuration = configuration;
        _groupSteps = groupSteps;
        _groups = groups;
        _lightControl = lightControl;
        _lightPane = lightPane;
        _log = log;
        _notices = notices;
        _objectControl = objectControl;
        _overlayControl = overlayControl;
        _overlayPresentation = overlayPresentation;
        _referenceImages = referenceImages;
        _scene = scene;
        _scenePane = scenePane;
        _selection = selection;
        _sidebar = sidebar;
        _poseFileSection = poseFileSection;
        _duplication = duplication;
        _entityCommands = entityCommands;
        _entityActions = entityActions;
        _playback = playback;
        _cameraTargets = cameraTargets;
        _names = names;
        _issueReport = issueReport;
        _openSkeletonSettings = openSkeletonSettings;
        _removalDialog = removalDialog;
        _placement = placement;
    }

    public void OpenFor(ShellSidebarRow row)
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
    }

    private ActorDescriptor? ResolveActorDescriptor(ActorId id) =>
        _actorControl.Read(id) != null ? _scene.Snapshot.FindActor(id) : null;
    private bool? IsEntityVisible(SelectionId id) => _entityCommands.ReadVisibility(id);
    private int SetEntityVisible(SelectionId id, bool visible) => _entityCommands.SetVisibility([id], visible);
    private void SetSelectionVisible(bool visible) => _entityCommands.SetVisibility(_selection.Selected, visible);
    private bool? PlayingOf(SelectionId id) => _playback.ReadPlaying(id);
    private void SetSelectionPaused(bool paused)
    {
        foreach (var id in _selection.Selected) _playback.SetPlaying(id, !paused);
    }
    private async System.Threading.Tasks.Task<int?> RemoveEntitiesSafely(
        IReadOnlyList<SelectionId> ids, Action? onRemoved = null)
    {
        var removed = await _entityActions.Remove(ids);
        if (removed > 0) onRemoved?.Invoke();
        return removed;
    }
    private bool CanRecenterOnTracked(CameraId id) => _cameraTargets.Read(id)?.CanCenterTrackedActor == true;
    private void RecenterCameraOnTrackedActor(CameraId id)
    {
        var result = _cameraTargets.CenterTrackedActor(id);
        if (!result.Success) _notices.Refused(result.Detail ?? "The camera could not move.");
    }
    private void OpenEntityRename(string title, string current, Action<string> apply) =>
        _names.Open(title, current, apply);

    /// <summary>Character data is saved only for an owned actor: one
    /// Poser spawned, or the player's own character.</summary>
    private bool SaveOwnedActorEntry(ActorId actorId, string name)
    {
        if (ResolveActorDescriptor(actorId) is not { IsOwned: true })
        {
            _notices.Refused(
                "Only an actor you spawned or your own character can be saved to the library.");
            return false;
        }
        return _scenePane.SaveActorEntry(actorId.LogicalId, name);
    }

    /// <summary>Whether every actor among the members is owned; a group
    /// holding anyone else's actor saves without appearance.</summary>
    public bool AllActorsOwned(IReadOnlyList<SelectionId> members)
    {
        foreach (var member in members)
            if (member.Actor is { } actorId
                && ResolveActorDescriptor(actorId) is not { IsOwned: true })
                return false;
        return true;
    }


    private void MoveSelectionToCamera()
    {
        var result = _placement.MoveToCamera();
        if (!result.Success) _notices.Failed(result.Detail ?? "Move to camera was refused.");
    }

    private void DestroySelection() => _ = _entityActions.Remove(_selection.Selected.ToArray());
}
