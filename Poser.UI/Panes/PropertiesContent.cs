using System;
using System.Collections.Generic;
using System.Numerics;
using Poser.Application.Animation;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Application.Presentation;
using Poser.Application.Transforms;
using Poser.Config;
using Poser.Domain.Identity;
using Poser.Domain.Scene;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>Properties presentation shared by the workspace and pinned hosts.
/// Each host owns this graph; application commands and history remain shared.</summary>
public sealed partial class PropertiesContent
{
    public PropertiesContext Context { get; }
    private SelectionScope _selection => Context.Selection;
    private readonly SceneSession _scene;
    private readonly ConfigurationService _configuration;
    private readonly IGPoseService _gPoseService;
    private readonly ISceneObjectControl _objectControl;
    private readonly SceneGroups _groups;
    private readonly GroupSteps _groupSteps;
    private readonly ISelectionPlacement _placement;
    private readonly UserNotices _notices;
    private readonly EntityNameModal _names;
    private readonly ICameraTargetControl _cameraTargets;
    private readonly IAnimationCatalogLoader _animationCatalog;
    private readonly IAnimationPlayback _animation;
    private readonly PoseInspectorPane _poseInspector;
    private readonly AnimationPane _animationPane;
    private readonly AppearancePane _appearancePane;
    private readonly LightPane _lightPane;
    private readonly CameraPane _cameraPane;
    private readonly EnvironmentPane _environmentPane;
    private readonly ScenePane _scenePane;
    private readonly PropsPane _propsPane;
    private readonly WorldObjectsPane _worldObjectsPane;
    private readonly OverlayPane _overlayPane;
    private readonly PoseFileInspectorSection _poseFiles;
    private readonly GraphicalBonePane _map;
    private AppShellViewModel _vm = new();
    private string _activeTab = "Pose", _activeStrip = "actor";
    private int _contentMode;
    public string ActiveTab { get => _activeTab; set => _activeTab = value; }
    public int ContentMode { get => _contentMode; set => _contentMode = value; }
    public Func<ActorDescriptor, IReadOnlyList<BoneChoice>> BuildBoneChoices { get; set; } = _ => Array.Empty<BoneChoice>();
    private readonly int[] _multiCounts = new int[5];
    private readonly string[] _multiCountText = new string[5];
    private static readonly string[] MultiKindLabels = ["Actors", "Objects", "Lights", "Cameras", "Overlays"];
    private readonly ShellTab[] _multiselectTabs = [new() { Label = "Selection" }];
    private readonly ShellTab[] _selectionTabs = [new() { Label = "Pose" }, new() { Label = "Animation" }, new() { Label = "Actor" }];
    private readonly ShellTab[] _propTabs = [new() { Label = "Object" }];
    private readonly ShellTab[] _lightTabs = [new() { Label = "Light" }];
    private readonly ShellTab[] _cameraTabs = [new() { Label = "Camera" }];
    private readonly ShellTab[] _overlayTabs = [new() { Label = "Overlay" }];
    private readonly ShellTab[] _worldObjectTabs = [new() { Label = "Object" }];
    private readonly ShellTab[] _furnitureTabs = [new() { Label = "Furniture" }];
    private readonly ShellTab[] _environmentTabs = [new() { Label = "Lighting" }, new() { Label = "Sky" }, new() { Label = "Atmosphere" }, new() { Label = "World" }];
    private static readonly string[] CameraTrackingModeOptions = ["Follow", "Pan", "Follow and pan", "None"];
    private readonly Crystarium.SearchPicker<BoneChoice> _cameraTrackingBonePicker = new("camera-tracking-bones");
    private IReadOnlyList<BoneChoice> _cameraBoneChoices = Array.Empty<BoneChoice>();
    private CameraId? _cameraBonePickerCamera;
    private ActorId? _cameraBonePickerActor;

    public PropertiesContent(PropertiesContext context, SceneSession scene, ConfigurationService configuration,
        IGPoseService gPoseService, ISceneObjectControl objectControl, SceneGroups groups, GroupSteps groupSteps,
        ISelectionPlacement placement, UserNotices notices, EntityNameModal names,
        ICameraTargetControl cameraTargets, IAnimationCatalogLoader animationCatalog, IAnimationPlayback animation,
        PoseInspectorPane poseInspector, AnimationPane animationPane, AppearancePane appearancePane,
        LightPane lightPane, CameraPane cameraPane, EnvironmentPane environmentPane, ScenePane scenePane,
        PropsPane propsPane, WorldObjectsPane worldObjectsPane, OverlayPane overlayPane,
        PoseFileInspectorSection poseFiles, GraphicalBonePane map, EntityRemovalDialog removal)
    {
        Context = context; _scene = scene; _configuration = configuration; _gPoseService = gPoseService;
        _animation = animation;
        _objectControl = objectControl; _groups = groups; _groupSteps = groupSteps; _placement = placement;
        _notices = notices; _names = names; _cameraTargets = cameraTargets; _animationCatalog = animationCatalog;
        _poseInspector = poseInspector; _animationPane = animationPane; _appearancePane = appearancePane;
        _lightPane = lightPane; _cameraPane = cameraPane; _environmentPane = environmentPane; _scenePane = scenePane;
        _propsPane = propsPane; _worldObjectsPane = worldObjectsPane; _overlayPane = overlayPane;
        _poseFiles = poseFiles; _map = map;
        poseInspector.DrawMapInline = map.DrawInline;
        poseInspector.DrawExpressionRow = animationPane.DrawExpressionRow;
        poseInspector.BuildBoneChoices = actor => BuildBoneChoices(actor);
        lightPane.BuildBoneChoices = actor => BuildBoneChoices(actor);
        cameraPane.DrawTrackingActors = DrawCameraTrackingActors;
        poseInspector.GetMapMirror = () => map.SidesSwapped;
        poseInspector.SetMapMirror = on =>
        {
            map.SidesSwapped = on;
            configuration.Config.UI.MapMirrorSelection = on;
            configuration.Save();
        };
        poseInspector.GetSwapRotationXY = () => configuration.Config.UI.SwapRotationXY;
        propsPane.RequestDestroyAll = removal.ConfirmDestroyAllProps;
        lightPane.RequestDestroyAll = removal.ConfirmDestroyAllLights;
        cameraPane.RequestDestroyAll = removal.ConfirmDestroyAllCameras;
    }

    public void Bind(AppShellViewModel vm)
    {
        _vm = vm;
        vm.SectionDisclosure = new(_configuration.Config.UI.SectionDisclosure);
        vm.DrawContent = DrawTabContent;
        vm.OnTab = OnTabClicked;
        vm.DrawFooterMiddle = DrawFooterMiddle;
        vm.OnPhysics = on => _animation.SetScenePhysicsFrozen(!on);
        vm.OnAnimation = on =>
        {
            if (_selection.PrimaryActor is not { } actor) return;
            if (on) _animation.ClearSpeed(actor);
            else _animation.SetSpeed(actor, 0f);
        };
    }

    public void Refresh()
    {
        _map.SidesSwapped = _configuration.Config.UI.MapMirrorSelection;
        var actor = _selection.PrimaryActor;
        _vm.AnimationAvailable = actor is { } id && _animation.IsSupported(id);
        _vm.AnimationOn = actor is not { } playing || _animation.OverridesFor(playing).OverallSpeed is not 0f;
        _vm.PhysicsOn = !_animation.IsPhysicsFrozen;
        _vm.TitleEntity = _contentMode switch { 1 => "Environment", 2 => "Scene", _ => TitleEntity(_selection.Primary) };
        _vm.ContentKind = ContentKind(_selection.Primary);
        BuildTabs(_selection.Primary);
        ApplyTabLayout(_contentMode == 2 ? "Scene" : _activeTab);
    }

    public void ConfigureNavigation(Action openLibrary)
    {
        _scenePane.OpenLibrary = openLibrary;
        _poseFiles.OnLibraryRequested += openLibrary;
    }

    public void DrawFooterMiddle(Vector2 origin, Vector2 size)
    {
        if (_activeTab != "Pose" || (Context.IsPinned && !Context.IsAvailable)
            || _selection.PrimaryActor is not { } actorId) return;
        var slot = _selection.Primary?.Bone?.Slot ?? PoseSlot.Character;
        if (_scene.Snapshot.FindActor(actorId)?.GetSkeleton(slot) is { } skeleton)
            _poseInspector.DrawParentingBar(origin, size, skeleton);
    }

    public void DrawDialogs()
    {
        _animationPane.DrawExpressionPicker();
        _appearancePane.DrawBrowsers();
        _lightPane.DrawBrowsers();
        _cameraPane.DrawBrowsers();
        _poseFiles.DrawBrowsers();
        _scenePane.DrawBrowsers();
        _names.Draw();
    }

    private void OpenEntityRename(string title, string current, Action<string> apply) =>
        _names.Open(title, current, apply);
}
