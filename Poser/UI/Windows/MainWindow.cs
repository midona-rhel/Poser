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
using Poser.Game;
using Poser.Game.Bindings;
using Poser.Game.Transforms;
using Poser.Domain.Companions;
using Poser.Game.Posing;
using Poser.Services;
using Poser.UI.Controls;
using Poser.UI.Views;

namespace Poser.UI;

/// <summary>
/// Binds the single retained posing workspace: actor/bone tree, Pose content,
/// inspector rail, and transform chrome. Deferred feature routes do not live
/// behind dormant tabs or callbacks.
/// </summary>
public class MainWindow : Window
{
    // One minimum for every tab: the right column is always spent, either
    // on the Pose rail or on Animation content.
    private const float MinimumWidth = 1110f;
    private const float DefaultWidth = MinimumWidth + 50f;
    private const float DefaultHeight = 660f;
    private const float MinHeight = 520f;

    private readonly IGPoseService _gPoseService;
    private readonly IActorManager _actorManager;
    private readonly IActorSpawnService _spawnService;
    private readonly SceneSession _scene;
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;
    private readonly Application.Animation.AnimationSession _animation;
    private readonly SkeletonOverlayPresentation _overlayPresentation;
    private readonly BoneVisibilityPresetService _bonePresets;
    private readonly WorldAdoptionSource _worldAdoption;
    private readonly IGazeService _gazeService;
    private readonly global::Poser.Application.Integration.ActorIntegrationSession _integration;

    /// <summary>The reference-picture roster. The sidebar lists it and never
    /// owns it: a picture is not a scene entity — it needs no native
    /// signature, joins no journal, and is laid over the game rather than into
    /// the scene — so its rows restate the session's state and every verb goes
    /// back through the session.</summary>
    private readonly ReferenceImageSession _referenceImages;

    /// <summary>Every entity the shell adds or removes goes through this, so
    /// the act lands in the same history the transforms do.</summary>
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;
    private readonly UserNotices _notices;
    private readonly Dalamud.Plugin.Services.IPluginLog _log;
    private readonly global::Poser.Application.Scene.SceneGroups _groups;

    /// <summary>A group row's click target — selects the whole membership.
    /// </summary>
    private readonly record struct GroupRowTag(Guid Id);

    /// <summary>The outliner's ONE section, retained with its rows: the
    /// tree is the most expensive thing a frame can assemble, so it is
    /// rebuilt only when <see cref="BuildSidebar"/>'s gate flips and
    /// flag-refreshed in place on every other frame. One section because
    /// the root list is the USER'S order — group heads and entities of
    /// every kind interleave freely, so kind boundaries cannot own
    /// rows.</summary>
    private readonly ShellSidebarSection _sceneSection = new()
    {
        Title = "",
    };

    /// <summary>Rebuild scratch: the root-eligible entities handed to the
    /// order sync, retained to keep the cold path allocation-flat.</summary>
    private readonly List<SelectionId> _rootEntities = new();

    private readonly Game.Scene.SceneWorkflow _sceneWorkflow;

    /// <summary>Rebuilds this pending structure has waited through: the
    /// spawned entities bind within a publish or two, so a stage nothing
    /// ever resolves against is dropped rather than held forever.</summary>
    private int _pendingStructureAttempts;
    private readonly global::Poser.Services.ICameraService _gameCamera;
    private readonly Game.Viewport.ViewportProjection _viewportProjection;

    // actor context menu + rename modal: stable ids only; the lifetime
    // services still take legacy actors, so ids resolve per frame through the
    // binding registry and the pointer never persists in UI state.
    private ActorId? _ctxActorId;
    private bool _shellMenuOpenRequested;
    private Vector2 _shellMenuAnchor;
    /// <summary>The shell command menu's rows, retained: a warm frame only
    /// re-reads the gate below, and the rows are rewritten in place when — and
    /// only when — that gate flips. ContextMenuItem is a struct, so the menu
    /// costs one allocation for the lifetime of the window.</summary>
    private readonly ContextMenuItem[] _shellMenuItems =
        new ContextMenuItem[(int)ShellCommand.OpenSettings + 1];
    /// <summary>Whether the rows were last built with a posable target.</summary>
    private bool _shellMenuPoseTarget;
    private bool _shellMenuRowsBuilt;
    /// <summary>The split flags the rows were last built under, packed.</summary>
    private int _shellMenuLayoutState = -1;
    private bool _ctxOpenRequested;
    private BoneId? _ctxBoneId;
    private IReadOnlyList<BoneId>? _ctxBoneOverlayBones;
    private bool _boneCtxOpenRequested;
    private IReadOnlyList<BoneId>? _ctxOverlayBones;
    private string? _ctxOverlayMemoryKey;
    private bool _overlayCtxOpenRequested;
    private bool _renameOpen;
    private string _renameValue = "";
    private ActorId? _renameTarget;
    // Bone-visibility presets: the menu applies them to one actor, the manager
    // owns the shared store. Both hold an id, never a descriptor.
    private ActorId? _presetActorId;
    private readonly List<ContextMenuItem> _bonePresetItems = [];
    private readonly List<Action?> _bonePresetActions = [];
    private bool _presetManagerOpen;
    private string _presetNameValue = "";
    private string? _presetSaveNote;
    private readonly IEditorState _editorState;
    private readonly CleanTransformFacade _cleanTransforms;
    private readonly CleanPoseFacade _cleanPose;
    private readonly IBonePosingService _bonePosingService;

    private readonly PoseInspectorPane _poseInspector;
    private readonly AnimationPane _animationPane;
    private readonly AppearancePane _appearancePane;
    private readonly LightPane _lightPane;
    private readonly ILightingService _lightingService;
    private readonly CameraPane _cameraPane;
    private readonly IVirtualCameraService _cameraService;
    private readonly Crystarium.SearchPicker<global::Poser.UI.BoneChoice>
        _cameraTrackingBonePicker = new("camera-tracking-bones");
    private IReadOnlyList<global::Poser.UI.BoneChoice> _cameraBoneChoices =
        Array.Empty<global::Poser.UI.BoneChoice>();
    private CameraId? _cameraBonePickerCamera;
    private ActorId? _cameraBonePickerActor;
    private readonly EnvironmentPane _environmentPane;
    private readonly PoseLibraryPane _libraryPane;
    private readonly ScenePane _scenePane;
    private readonly PoseFileInspectorSection _poseFileSection;
    private readonly Game.Animation.AnimationCatalogLoader _animationCatalog;
    private readonly Game.Companions.CompanionCatalogLoader _companionCatalog;
    private readonly PoseRailPane _poseRail;
    private bool _collapsed;
    private float _savedHeight = DefaultHeight;
    private readonly HashSet<string> _collapsedNodes = new();
    private readonly HashSet<string> _knownCategoryNodes = new();
    private readonly HashSet<string> _knownActorNodes = new();
    private float _sidebarWidth = 280f;
    // Session-local like the width: the fold is working posture, not
    // configuration.
    private bool _sidebarCollapsed;
    private readonly AppShellViewModel _vm = new();

    /// <summary>The acceptance gate. A field initializer, not a dependency:
    /// it reads the config service's static instance exactly like the rename
    /// modal below does.</summary>
    private readonly FirstRunNoticeView _firstRunNotice = new()
    {
        OnOpenUrl = url => Dalamud.Utility.Util.OpenLink(url),
    };

    /// <summary>The per-frame shell view model, for the split-part windows —
    /// they are registered after this window, so a frame's model is already
    /// built when they read it.</summary>
    internal AppShellViewModel ShellVm => _vm;
    private string _activeTab = "Pose";

    /// <summary>Which selection strip the tab set belongs to, recorded by
    /// <see cref="BuildTabs"/> from the same switch that picks the strip.
    /// Joins the tab key in the content scroll identity: strips reuse labels
    /// ("Light"), and a same-labeled tab on another strip is another place.
    /// Library mode leaves it untouched, exactly as it leaves the tab.</summary>
    private string _activeStrip = "actor";

    /// <summary>The shell keeps workspace mode and entity selection mutually exclusive.</summary>
    private readonly ShellWorkspaceSelection _workspace;

    /// <summary>The workspace is showing the complete scene view — save, load, progress
    /// and recovery — instead of the selection's tabs. A mode exactly like the
    /// library's, and its alternative: a scene is not a property of whatever
    /// happens to be selected.</summary>

    /// <summary>The library's sidebar section and its one tab, both retained:
    /// they carry no per-frame data, so a warm frame restates them rather than
    /// minting them.</summary>

    /// <summary>The scene as a whole, seated at the very top of the tree: the
    /// thing everything below it belongs to. Like the library's and the
    /// environment's, its header is the affordance — there is one scene and
    /// nothing creates or destroys it — and it carries no rows.</summary>

    /// <summary>The scene's environment, seated above the actors. It is the one
    /// scene entity that is always there and there is only ever one of it, so
    /// the header is the affordance — exactly like the library's — rather than a
    /// header naming a lone row beneath it. Nothing creates or destroys it, so
    /// the section shows no plus and carries no rows.</summary>

    /// <summary>The one environment selection, minted once: it carries no
    /// per-scene data, so every frame's row and flag refresh restate it.
    /// </summary>
    private static readonly SelectionId EnvironmentSelection =
        SelectionId.ForEnvironment();

    /// <summary>Footer toggles for world-object adoption classes.</summary>
    private readonly (WorldAdoptionKind Kind, ShellWorldClass Entry)[]
        _worldClasses = BuildWorldClasses();

    private static (WorldAdoptionKind, ShellWorldClass)[] BuildWorldClasses()
    {
        var classes = WorldAdoptionClasses.All;
        var built = new (WorldAdoptionKind, ShellWorldClass)[classes.Length];
        for (int i = 0; i < classes.Length; i++)
            built[i] = (classes[i], WorldClassEntry(classes[i]));
        return built;
    }

    /// <summary>One class's glyph and its two hover cards. Minted once with the
    /// window: a warm footer frame states help, so it must not build the
    /// sentence.</summary>
    private static ShellWorldClass WorldClassEntry(WorldAdoptionKind kind) =>
        kind switch
        {
            WorldAdoptionKind.Light => new ShellWorldClass
            {
                Icon = TablerIcon.Bulb,
                Id = "##world-class-lights",
                ShowHelp =
                    "Mark the world's addable lights — click a mark to take "
                    + "it into the scene",
                HideHelp = "Stop marking the world's addable lights",
            },
            WorldAdoptionKind.WorldObject => new ShellWorldClass
            {
                // The kind's own mark, as everywhere (Square shipped and
                // was an imprecise leftover).
                Icon = TablerIcon.Plant,
                Id = "##world-class-objects",
                ShowHelp =
                    "Mark the map's own objects — click a mark to borrow it "
                    + "into the scene; releasing it puts it back",
                HideHelp = "Stop marking the map's own objects",
            },
            WorldAdoptionKind.Effect => new ShellWorldClass
            {
                Icon = TablerIcon.Fire,
                Id = "##world-class-effects",
                ShowHelp =
                    "Mark the world's playing effects — click a mark to "
                    + "borrow it into the scene; releasing it puts it back",
                HideHelp = "Stop marking the world's playing effects",
            },
            _ => new ShellWorldClass
            {
                Icon = TablerIcon.User,
                Id = "##world-class-actors",
                ShowHelp =
                    "Mark the world's addable actors — click a mark to clone "
                    + "it into the scene",
                HideHelp = "Stop marking the world's addable actors",
            },
        };

    /// <summary>The actor rows, with the snapshot facts a warm frame needs to
    /// restate their live flags without walking the scene again.</summary>
    private readonly List<ActorRowState> _actorRows = new();

    private readonly record struct ActorRowState(
        ShellSidebarRow Row,
        ActorId Id,
        string RawName,
        bool SnapshotHidden);

    /// <summary>Bone category → index into the rebuild's group list. Indexed by
    /// the enum itself, because the FindIndex predicate this replaces was one
    /// closure allocation per bone per frame.</summary>
    private readonly int[] _categorySlots =
        new int[(int)Core.BoneInfo.BoneCategory.Other + 1];

    // ── sidebar rebuild gate (see BuildSidebar) ─────────────────────────
    private bool _sidebarBuilt;
    private ulong _sidebarRevision;
    private string _sidebarFilter = "";
    private int _sidebarExpandVersion = -1;

    /// <summary>A gaze mode transition landed since the last rebuild. Gaze mode
    /// is not part of the scene revision and cannot be, so the one row it owns
    /// needs its own arming bit. Written from the gaze service's publishing
    /// thread — volatile, and nothing but the bit is touched there.</summary>
    private volatile bool _gazeDirty;

    /// <summary>Bumped by every disclosure toggle. The gate cannot observe
    /// <see cref="_collapsedNodes"/> directly — a set carries no version — and
    /// disclosure is the one non-scene input that changes the row count.
    /// </summary>
    private int _expandVersion;
    private int _sidebarGroupsRevision = -1;

    /// <summary>The ANONYMOUS GROUP's strip: two or more entities selected
    /// together get one Selection page — a group that was never created.
    /// </summary>
    private readonly ShellTab[] _multiselectTabs =
    [
        new() { Label = "Selection" },
    ];

    /// <summary>The selection-typed tab strip, retained like the library's —
    /// three fresh ShellTabs per frame were pure churn.</summary>
    private readonly ShellTab[] _selectionTabs =
    [
        new() { Label = "Pose" },
        new() { Label = "Animation" },
        new() { Label = "Appearance" },
    ];

    /// <summary>A spawned object's strip, the camera strip's sibling: while
    /// one is selected the single tab is its editor. It shares the label with
    /// the borrowed object's strip below — one word for one thing — and the
    /// selection decides which pane the label opens.</summary>
    private readonly ShellTab[] _propTabs =
    [
        new() { Label = "Object" },
    ];


    /// <summary>A light's whole tab strip, the environment strip's sibling:
    /// a light has no pose, animation or appearance, so while one is selected
    /// the tab set is the light editor, split the way the editor's own three
    /// concerns split — what it emits, what it casts, and where it is. Its
    /// "Light" label is shared with the environment's lighting tab — two
    /// strips, never both live — so DrawTabContent settles the two by
    /// selection, not by label.</summary>
    private readonly ShellTab[] _lightTabs =
    [
        new() { Label = "Light" },
    ];

    /// <summary>An overlay's tab strip, the prop strip's sibling: while a
    /// staged game-UI node is selected the one tab is its editor. An overlay
    /// has no world transform for the inspector rail to own, so its screen
    /// placement lives on the tab with everything else about it.</summary>
    private readonly ShellTab[] _overlayTabs =
    [
        new() { Label = "Overlay" },
    ];

    /// <summary>A borrowed map object's strip, the prop strip's sibling: while
    /// one is selected the single tab is its editor, and its transform lives on
    /// the inspector rail exactly as a prop's does.</summary>
    private readonly ShellTab[] _worldObjectTabs =
    [
        new() { Label = "Object" },
    ];

    /// <summary>A camera's tab strip, the light strip's sibling: while a
    /// camera is selected the one tab is the camera editor — the camera's
    /// offset and its bone tracking live on the inspector rail instead.
    /// </summary>
    private readonly ShellTab[] _cameraTabs =
    [
        new() { Label = "Camera" },
    ];


    /// <summary>The sections are stated in a fixed order — library, scene,
    /// environment, actors, objects, lights, cameras, overlays — so the actors
    /// section is index 3. Its header and the lights header are the only two
    /// whose plus creates anything; neither the scene nor the environment is
    /// ever created or destroyed.</summary>
    private const int ActorsSectionIndex = 0;

    /// <summary>Objects stand between the actors and the lights: scene
    /// furniture, spawned or borrowed.</summary>
    private const int PropsSectionIndex = 1;

    /// <summary>Lights stand under the actors they light.</summary>
    private const int LightsSectionIndex = 2;

    /// <summary>Cameras stand above the overlays: they look at everything in
    /// the world, and an overlay is not in the world.</summary>
    private const int CamerasSectionIndex = 3;

    /// <summary>Overlays close the scene's own list. They are the one entity
    /// that lives on the screen rather than in the scene, so they sit outside
    /// everything the camera can see.</summary>
    private const int OverlaysSectionIndex = 4;

    // Overlay settings live in their own rows; the keybind registry owns the
    // overlay window flag.

    public event Action? OnSettingsRequested;

    /// <summary>Raised by every creation affordance — the titlebar plus, the
    /// section header plusses, and the shell menu — with the pointer position
    /// the browser opens at and the tab that affordance answers for.</summary>
    public event Action<Vector2, SpawnBrowserTab>? OnSpawnBrowserRequested;


    /// <summary>The strip's Scene toggle: the window set flips the Scene
    /// window and answers its state through <see cref="GetSceneWindowOpen"/>.
    /// </summary>
    public event Action? OnSceneWindowToggleRequested;

    public Func<bool>? GetSceneWindowOpen { get; set; }

    /// <summary>The split inspector window: shown or hidden without
    /// merging it back.</summary>
    public event Action? OnInspectorWindowToggleRequested;
    public Func<bool>? GetInspectorWindowOpen { get; set; }

    public MainWindow(
        IGPoseService gPoseService,
        IActorManager actorManager,
        IBonePosingService bonePosingService,
        IActorSpawnService spawnService,
        SceneSession scene,
        StableBindingRegistry bindings,
        IEditorState editorState,
        CleanTransformFacade cleanTransforms,
        CleanPoseFacade cleanPose,
        PoseInspectorPane poseInspector,
        AnimationPane animationPane,
        AppearancePane appearancePane,
        LightPane lightPane,
        ILightingService lightingService,
        CameraPane cameraPane,
        IVirtualCameraService cameraService,
        EnvironmentPane environmentPane,
        PoseLibraryPane libraryPane,
        ScenePane scenePane,
        PoseFileInspectorSection poseFileSection,
        Application.Animation.AnimationSession animation,
        Game.Animation.AnimationCatalogLoader animationCatalog,
        Game.Companions.CompanionCatalogLoader companionCatalog,
        PoseRailPane poseRail,
        GraphicalBonePane graphicalBonePane,
        Game.PropSpawnService propService,
        PropsPane propsPane,
        WorldObjectsPane worldObjectsPane,
        Game.Overlays.OverlayNodeService overlayService,
        OverlayPane overlayPane,
        CompanionSection companions,
        SkeletonOverlayPresentation overlayPresentation,
        BoneVisibilityPresetService bonePresets,
        ReferenceImageSession referenceImages,
        WorldAdoptionSource worldAdoption,
        IGazeService gazeService,
        Game.Scene.SceneLifecycleHistory lifecycle,
        global::Poser.Application.Integration.ActorIntegrationSession integration,
        UserNotices notices,
        Dalamud.Plugin.Services.IPluginLog log,
        global::Poser.Application.Scene.SceneGroups groups,
        Controls.EntityNameModal names,
        Game.Scene.SceneWorkflow sceneWorkflow,
        global::Poser.Services.ICameraService gameCamera,
        Game.Viewport.ViewportProjection viewportProjection,
        IEventBus eventBus)
        : base($"{PluginConstants.PluginName}###poser_main_window",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        _vm.BranchLabel = BuildMetadata.Branch is "" or "unknown" or "detached" or "main" or "master"
            ? ""
            : BuildMetadata.Commit.Length == 0
                ? BuildMetadata.Branch
                : $"{BuildMetadata.Branch} · {BuildMetadata.Commit}";
        Size = new Vector2(DefaultWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;
        // Escape is the deselect chord, not the dismiss-the-workspace one —
        // the split parts and the pop-outs already said so, and losing the
        // whole shell mid-shoot to a stray Escape is the footgun the
        // references close through the same window route.
        RespectCloseHotkey = false;
        // Construction predates the configuration read; PreDraw restates the
        // effective floor every frame anyway.
        SizeConstraints = ExpandedSizeConstraints(MinimumWidth);

        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _scene = scene;
        _selection = scene.Selection;
        // One selection for the whole shell. The subscription lives as long as
        // this window does — both outlive every scene — so it is never torn
        // down; Dispose exists on the type for hosts that do own a lifetime.
        _workspace = new ShellWorkspaceSelection(_selection);
        _workspace.Left += OnWorkspaceLeft;
        _bindings = bindings;
        _names = names;
        _sceneWorkflow = sceneWorkflow;
        _editorState = editorState;
        _cleanTransforms = cleanTransforms;
        _cleanPose = cleanPose;
        _bonePosingService = bonePosingService;

        _spawnService = spawnService;
        _propService = propService;
        _propsPane = propsPane;
        _worldObjectsPane = worldObjectsPane;
        _overlayService = overlayService;
        _overlayPane = overlayPane;
        _companions = companions;
        _poseInspector = poseInspector;
        _animationPane = animationPane;
        _appearancePane = appearancePane;
        _lightPane = lightPane;
        _lightingService = lightingService;
        _cameraPane = cameraPane;
        _cameraPane.GetNativeTarget = _actorManager.GetGPoseTarget;
        // Camera tracking consumes this window's already-built actor/category
        // hierarchy; the shared row model keeps disclosure and identities in
        // lockstep with the sidebar instead of minting a second flat tree.
        _cameraPane.DrawTrackingActors = DrawCameraTrackingActors;
        _cameraService = cameraService;
        _environmentPane = environmentPane;
        _libraryPane = libraryPane;
        _scenePane = scenePane;
        // The Scene panel's way into the library workspace.
        _scenePane.OpenLibrary = ShowLibrary;
        // The library's "Add source…" and its empty state both mean the same
        // thing the titlebar gear does, so they travel the one settings route.
        _libraryPane.OnSettingsRequested += () => OnSettingsRequested?.Invoke();
        // Saving from the scenes tab is the scene workspace's own dialog: one
        // destination browser and one description field, wherever it is asked
        // for.
        // The library saves through the MODAL — name plus the appearance
        // choice — never the file-dialog detour.
        _libraryPane.OnSaveSceneRequested += () => _scenePane.RequestLibrarySave();
        _poseFileSection = poseFileSection;
        // The import menus resolve their target actor through the same
        // binding registry the context menus use.
        _poseFileSection._resolveActor = id =>
            _bindings.Resolve(id) is { Success: true } resolved
                ? resolved.Value
                : null;
        _animation = animation;
        _overlayPresentation = overlayPresentation;
        _bonePresets = bonePresets;
        _referenceImages = referenceImages;
        _worldAdoption = worldAdoption;
        _gazeService = gazeService;
        _integration = integration;
        _lifecycle = lifecycle;
        _notices = notices;
        _log = log;
        _groups = groups;
        _gameCamera = gameCamera;
        _viewportProjection = viewportProjection;
        // A gaze mode flip changes the sidebar's row set (the gaze anchor row
        // exists only in Position mode) while bumping neither the scene
        // revision nor the disclosure version. The handler arms the cold path
        // and does nothing else: the publisher is not the draw thread.
        eventBus.Subscribe<GazeStateChangedEvent>(_ => _gazeDirty = true);
        _animationCatalog = animationCatalog;
        _companionCatalog = companionCatalog;
        _poseInspector.DrawMapInline = graphicalBonePane.DrawInline;
        _poseInspector.BuildBoneChoices = BuildCameraBoneChoices;
        _poseInspector.DrawExpressionRow = animationPane.DrawExpressionRow;
        graphicalBonePane.SidesSwapped =
            Config.ConfigurationService.Instance.Config.UI.MapMirrorSelection;
        _poseInspector.GetMapMirror = () => graphicalBonePane.SidesSwapped;
        _poseInspector.SetMapMirror = on =>
        {
            graphicalBonePane.SidesSwapped = on;
            Config.ConfigurationService.Instance.Config.UI.MapMirrorSelection = on;
            Config.ConfigurationService.Instance.Save();
        };
        _poseInspector.GetSwapRotationXY = () =>
            Config.ConfigurationService.Instance.Config.UI.SwapRotationXY;
        _selection.Live.CompanionResolver = ResolveSiblingBone;
        _poseInspector.DescriptorDisplayName = ActorDisplayName;
        appearancePane.DisplayNameProvider = ActorDisplayName;
        // Transitional: the inspector still takes entity display lookups until
        // its own migration; route them through the lineage nickname store.
        _poseInspector.ActorDisplayNameProvider = actor =>
            _bindings.GetActorId(actor) is { } displayId
                ? Config.ConfigurationService.Instance.GetDisplayName(
                    displayId.LogicalId, DisplayName(actor.Name))
                : DisplayName(actor.Name);

        _poseRail = poseRail;
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
            var cameraResolved = _bindings.Resolve(recenterId);
            if (!cameraResolved.Success
                || cameraResolved.Value is not { IsValid: true } trackCamera)
                return;
            string trackLabel = FindActor(trackActorId.LogicalId) is { } tracked
                ? Config.ConfigurationService.Instance.GetDisplayName(
                    trackActorId.LogicalId, DisplayName(tracked.Name))
                : "Actor";
            _cameraPane.FollowActor(trackActorId, trackLabel, trackCamera);
        };
        _vm.OnGroupLock = row =>
        {
            if (row.Tag is GroupRowTag lockTag
                && _groups.Find(lockTag.Id) is { } lockGroup)
                _groups.SetLocked(lockTag.Id, !lockGroup.Locked);
        };
        _vm.OnGroupVisibility = row =>
        {
            if (row.Tag is GroupRowTag tag && _groups.Find(tag.Id) is { } group)
                SetGroupHidden(group, !group.Hidden);
        };
        _vm.OnGroupPause = row =>
        {
            if (row.Tag is GroupRowTag tag && _groups.Find(tag.Id) is { } group)
                SetGroupPaused(group, !group.Paused);
        };
        _vm.OnGizmoOperation = i => _editorState.TransformTool = (TransformTool)i;
        _vm.OnGizmoSpace = i => _editorState.TransformOrientation = (TransformOrientation)i;
        _vm.OnRotationPivot = i => _editorState.RotationPivot = (Core.RotationPivot)i;
        _vm.OnSymmetry = i =>
        {
            var mode = (SymmetryMode)i;
            var configuration =
                Config.ConfigurationService.Instance.Config;
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
                    Config.ConfigurationService.Instance.Save();
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
            if (Config.ConfigurationService.Instance.Config.UI.DetachedShell)
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
            else if (row.Tag is SelectionId { Kind: SceneEntityKind.Bone, Bone: { } ctxBone })
            {
                _ctxBoneId = ctxBone;
                _ctxBoneOverlayBones = row.OverlayBones;
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
                _ctxOverlayBones = row.OverlayBones;
                _ctxOverlayMemoryKey = row.OverlayMemoryKey;
                _overlayCtxOpenRequested = true;
            }
        };
        _vm.OnActorTarget = row =>
        {
            if (ResolveActorRow(row) is { } actor)
                _actorManager.SetGPoseTarget(actor);
        };
        _vm.OnActorVisibility = row =>
        {
            if (ResolveActorRow(row) is { } actor)
                _spawnService.SetVisibility(actor, !_spawnService.IsVisible(actor));
        };
        _vm.OnActorPause = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.Actor, Actor: { } actor })
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
        _vm.IsHandleShown = row =>
            row.Tag is not SelectionId handleId
            || _overlayPresentation.IsHandleShown(handleId);
        _vm.OnHandleToggle = row =>
        {
            if (row.Tag is SelectionId handleId)
                _overlayPresentation.ToggleHandle(handleId);
        };
        // The effect row's pause seat: the same freeze the properties
        // page states, reachable without selecting first.
        _vm.OnRowPause = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.WorldObject, WorldObject: { } pausedId })
                return;
            var paused = _bindings.Resolve(pausedId);
            if (!paused.Success ||
                paused.Value is not { IsValid: true } handle)
                return;
            if (!handle.IsVfx)
                return;
            handle.VfxPaused = !handle.VfxPaused;
            row.Paused = handle.VfxPaused;
        };
        // The scenery row's sun/moon seat: the same night state the
        // properties page switches.
        _vm.OnRowNight = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.WorldObject, WorldObject: { } nightId })
                return;
            var night = _bindings.Resolve(nightId);
            if (!night.Success ||
                night.Value is not { IsValid: true, IsVfx: false } handle)
                return;
            handle.NightState = !handle.NightState;
            row.Night = handle.NightState;
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
                var prop = _bindings.Resolve(propId);
                if (!prop.Success || prop.Value is not { IsValid: true } handle)
                    return;
                handle.Visible = !handle.Visible;
                row.LightOn = handle.Visible;
                return;
            }
            // An overlay row wears the same eye seat as a prop's: its toggle
            // is whether the node is drawn.
            if (row.Tag is SelectionId
                { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId })
            {
                var overlay = _bindings.Resolve(overlayId);
                if (!overlay.Success ||
                    overlay.Value is not { IsValid: true } node)
                    return;
                node.Visible = !node.Visible;
                row.LightOn = node.Visible;
                return;
            }
            // A borrowed map object wears the same eye seat as a prop's: its
            // toggle is whether the map draws it. The release restores the
            // captured state.
            if (row.Tag is SelectionId
                { Kind: SceneEntityKind.WorldObject, WorldObject: { } worldObjectId })
            {
                var worldObject = _bindings.Resolve(worldObjectId);
                if (!worldObject.Success ||
                    worldObject.Value is not { IsValid: true } claim)
                    return;
                claim.Visible = !claim.Visible;
                row.LightOn = claim.Visible;
                return;
            }
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.Light, Light: { } lightId })
                return;
            var resolved = _bindings.Resolve(lightId);
            if (!resolved.Success || resolved.Value is not { IsValid: true } light)
                return;
            light.IsOn = !light.IsOn;
            row.LightOn = light.IsOn;
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
            var resolved = _bindings.Resolve(rowCameraId);
            if (!resolved.Success ||
                resolved.Value is not { IsValid: true } camera)
                return;
            if (!camera.IsLive)
            {
                _cameraService.SetLive(camera);
            }
            else if (!camera.IsDefault)
            {
                foreach (var candidate in _cameraService.Cameras)
                {
                    if (candidate.IsDefault)
                    {
                        _cameraService.SetLive(candidate);
                        break;
                    }
                }
            }
            row.CameraLive = camera.IsLive;
        };
        _vm.OnCameraLock = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.Camera, Camera: { } rowCameraId })
                return;
            var resolved = _bindings.Resolve(rowCameraId);
            if (!resolved.Success ||
                resolved.Value is not { IsValid: true } camera ||
                _bindings.GetCameraId(camera) != rowCameraId)
                return;
            camera.IsLocked = !camera.IsLocked;
            row.CameraLocked = camera.IsLocked;
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

    public override void PreDraw()
    {
        base.PreDraw();
        PumpGroupCopies();

        // Keep one shell width across tabs; detached parts release their width.
        float minimumWidth = EffectiveMinimumWidth();
        SizeConstraints = _collapsed
            ? new WindowSizeConstraints
            {
                MinimumSize = new Vector2(minimumWidth, AppShellView.CollapsedBarHeight),
                MaximumSize = new Vector2(float.MaxValue, AppShellView.CollapsedBarHeight),
            }
            : ExpandedSizeConstraints(minimumWidth);

        // Hidden Inspector (detached, closed from the strip): an inputless
        // pixel that keeps building frames for the parts, restored to its
        // last real size when the strip reopens it.
        if (_contentHidden)
        {
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(1f, 1f),
                MaximumSize = new Vector2(1f, 1f),
            };
            Size = new Vector2(1f, 1f);
            SizeCondition = ImGuiCond.Always;
            PushShellStyles();
            return;
        }
        if (_contentRestorePending)
        {
            _contentRestorePending = false;
            SizeConstraints =
                ExpandedSizeConstraints(EffectiveMinimumWidth());
            Size = _hiddenRestoreSize;
            SizeCondition = ImGuiCond.Always;
            PushShellStyles();
            return;
        }

        // The split toggle's one-frame reseat: the RIGHT edge sheds or
        // regains the rail; the left edge holds. It joins the size CHAIN —
        // a standalone block here was reset to FirstUseEver by the chain's
        // final else before the size ever applied.
        if (_railShift != 0 && !_collapsed)
        {
            if (_shiftApplied)
            {
                Position = null;
                _shiftApplied = false;
            }
            Size = new Vector2(
                _lastWidth - _railShift * Views.AppShellView.RailWidth,
                _lastHeight);
            SizeCondition = ImGuiCond.Always;
            _railShift = 0;
        }
        // The detach toggle's one-frame reseat: width sheds or regains the
        // sidebar column while the left edge moves the same amount, so the
        // content and the inspector hold their screen position.
        else if (_detachShift != 0 && !_collapsed)
        {
            float gs = ImGuiHelpers.GlobalScale;
            Position = new Vector2(
                _lastPosition.X + _detachShift * _sidebarWidth * gs,
                _lastPosition.Y);
            PositionCondition = ImGuiCond.Always;
            Size = new Vector2(
                _lastWidth - _detachShift * _sidebarWidth, _lastHeight);
            SizeCondition = ImGuiCond.Always;
            _detachShift = 0;
            _shiftApplied = true;
        }
        else
        {
            if (_shiftApplied)
            {
                Position = null;
                _shiftApplied = false;
            }
            _detachShift = 0;

            // Collapse and restore go through the Dalamud window size system;
            // ImGui.SetWindowSize inside Draw loses to it.
            if (_collapsed)
            {
                Size = new Vector2(_lastWidth, AppShellView.CollapsedBarHeight);
                SizeCondition = ImGuiCond.Always;
            }
            else if (_restorePending)
            {
                Size = new Vector2(_lastWidth, _savedHeight);
                SizeCondition = ImGuiCond.Always;
                _restorePending = false;
            }
            else
            {
                SizeCondition = ImGuiCond.FirstUseEver;
            }
        }

        PushShellStyles();
    }

    /// <summary>The shell draws its own chassis; keep child regions
    /// transparent and give the hosted legacy panes the themed widget colors
    /// they expect. Every PreDraw path ends here — PostDraw pops
    /// unconditionally.</summary>
    private static void PushShellStyles()
    {
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Crystarium.ActiveTheme.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Crystarium.ActiveTheme.TextDim);
        ImGui.PushStyleColor(ImGuiCol.Border, Crystarium.ActiveTheme.Border);
        // Resize feedback — the grip and the lit border edge — is the
        // theme's accent, never Dalamud's global highlight.
        ImGui.PushStyleColor(ImGuiCol.ResizeGripHovered, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.ResizeGripActive, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.SeparatorHovered, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.SeparatorActive, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.Button, Crystarium.ActiveTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Crystarium.ActiveTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Crystarium.ActiveTheme.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Crystarium.ActiveTheme.SurfaceSunken);
        ImGui.PushStyleColor(ImGuiCol.Header, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Crystarium.ActiveTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Crystarium.ActiveTheme.AccentActive);

        // The shell is the window chrome — the ImGui window must contribute
        // nothing; the retained shell owns its padding and borders.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * ImGuiHelpers.GlobalScale);
    }

    private bool _restorePending;
    private float _lastWidth = DefaultWidth;

    /// <summary>The last primary the inspector-mode snap saw — a NEW
    /// selection snaps the inspector back to the Target panel.</summary>
    private Domain.Identity.SelectionId? _lastPrimaryForMode;

    /// <summary>The environment strip: five pages under the Environment
    /// content mode. Positional against EnvironmentTabFor.</summary>
    private readonly ShellTab[] _environmentTabs =
    [
        new() { Label = "Lighting" },
        new() { Label = "Sky" },
        new() { Label = "Atmosphere" },
        new() { Label = "World" },
    ];

    /// <summary>This frame's content-mode SNAPSHOT: the selector writes
    /// config only, and tabs, layout, and content all read this value —
    /// one coherent frame, no one-frame settle when the mode flips
    /// mid-draw.</summary>
    private int _contentMode;

    private float _lastHeight = DefaultHeight;

    private static WindowSizeConstraints ExpandedSizeConstraints(float minimumWidth)
        => new()
        {
            MinimumSize = new Vector2(minimumWidth, MinHeight),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

    /// <summary>The width floor for what is attached this frame: the shared
    /// 1110px covers sidebar + content + rail; detached mode hands the
    /// sidebar's column back and keeps the rail.</summary>
    private float EffectiveMinimumWidth()
    {
        float minimum = MinimumWidth;
        var ui = Config.ConfigurationService.Instance.Config.UI;
        if (ui.DetachedShell)
            minimum -= Crystarium.ActiveTheme.Shell.SidebarDefaultWidth;
        // A split inspector hands the rail's column back too.
        if (ui.SplitInspector)
            minimum -= Views.AppShellView.RailWidth;
        return minimum;
    }

    /// <summary>The window rect as of the last drawn frame — the detach
    /// orchestration reads it to seat the split windows where their parts
    /// stood.</summary>
    internal Vector2 LastPosition => _lastPosition;
    internal float LastHeight => _lastHeight;

    /// <summary>Where the rail's top-left stands on screen — the split
    /// window's seat at the split moment.</summary>
    internal Vector2 RailSeatScreen => new(
        _lastPosition.X + (_lastWidth - Views.AppShellView.RailWidth)
            * Dalamud.Interface.Utility.ImGuiHelpers.GlobalScale,
        _lastPosition.Y);
    internal float LastSidebarWidth => _sidebarWidth;

    /// <summary>+1 detaching (shrink right past the departing sidebar), -1
    /// merging (grow back left). Applied for one frame by PreDraw so the
    /// content and the inspector hold their screen position through the
    /// toggle.</summary>
    internal void ApplyDetachShift(int direction) => _detachShift = direction;

    /// <summary>+1 splitting (the right edge sheds the departing rail),
    /// -1 merging (it grows back). One frame, PreDraw, the detach shift's
    /// twin — the left edge holds, so the content keeps its place.</summary>
    internal void ApplyRailShift(int direction) => _railShift = direction;

    private int _railShift;

    private SelectionId? _lookThroughApplied;

    private int _detachShift;
    private bool _shiftApplied;
    private Vector2 _lastPosition;

    /// <summary>Detached mode only: the Inspector window closed from the
    /// toolbar (or its own X). The window object stays open — it still
    /// builds the frame's view model and pumps the menus and dialogs the
    /// whole shell shares — but it shrinks to an inputless pixel and draws
    /// no chassis until the strip reopens it.</summary>
    internal bool ContentHidden
    {
        get => _contentHidden;
        set
        {
            if (_contentHidden == value)
                return;
            _contentHidden = value;
            if (value)
            {
                _hiddenRestoreSize = new Vector2(_lastWidth, _lastHeight);
                Flags |= ImGuiWindowFlags.NoInputs;
            }
            else
            {
                Flags &= ~ImGuiWindowFlags.NoInputs;
                _contentRestorePending = true;
            }
        }
    }

    private bool _contentHidden;
    private bool _contentRestorePending;
    private Vector2 _hiddenRestoreSize;

    /// <summary>The title cell's subject: the library mode, else the selected
    /// entity by kind, else the plain product name. Actor names travel the
    /// masked display route like every other surface.</summary>
    /// <summary>The KIND label leading the tab band: what the content
    /// side is showing.</summary>
    private string ContentKind(SelectionId? primary)
    {
        // ALWAYS the selected object's kind — the segment names what
        // Target would show, whichever panel is active. A multiselect IS
        // its own kind: the anonymous group.
        if (global::Poser.Application.Selection.EntitySelection.IsMultiEntity(
                _selection.Selected))
            return "Selection";
        return primary switch
        {
            { Kind: SceneEntityKind.Actor or SceneEntityKind.Bone
                or SceneEntityKind.GazeTarget } => "Actor",
            { Kind: SceneEntityKind.Prop or SceneEntityKind.WorldObject }
                => "Object",
            { Kind: SceneEntityKind.Camera } => "Camera",
            { Kind: SceneEntityKind.Light } => "Light",
            { Kind: SceneEntityKind.Overlay } => "Overlay",
            _ => "",
        };
    }

    /// <summary>The environment strip's label as the pane's page.
    /// Positional against <see cref="_environmentTabs"/>.</summary>
    private static EnvironmentTab EnvironmentTabFor(string tab) => tab switch
    {
        "Sky" => EnvironmentTab.Sky,
        "Atmosphere" => EnvironmentTab.Atmosphere,
        "World" => EnvironmentTab.World,
        _ => EnvironmentTab.Lighting,
    };

    private int _multiTitleCount;
    private string _multiTitle = string.Empty;

    private string TitleEntity(SelectionId? primary)
    {
        int entities = global::Poser.Application.Selection.EntitySelection
            .CountEntities(_selection.Selected);
        if (entities >= 2)
        {
            // A selection that IS a named group wears the group's name.
            if (_groups.ActiveSelection(_selection.Selected) is { } group)
                return group.Name;
            if (_multiTitleCount != entities)
            {
                _multiTitleCount = entities;
                _multiTitle = $"{entities} selected";
            }
            return _multiTitle;
        }
        return primary switch
        {
            { Kind: SceneEntityKind.Actor or SceneEntityKind.GazeTarget,
                Actor: { } actorId } =>
                FindActor(actorId.LogicalId) is { } actor
                    ? ActorDisplayName(actor)
                    : "Poser",
            { Kind: SceneEntityKind.Bone, Bone: { } boneId } =>
                FindActor(boneId.Skeleton.Actor.LogicalId) is { } owner
                    ? ActorDisplayName(owner)
                    : "Poser",
            { Kind: SceneEntityKind.Environment } => "Environment",
            { Kind: SceneEntityKind.Light } => LightTitle(primary.Value),
            // The titlebar says the THING's name — "Balloon 1", never the
            // kind label (ruled 2026-08-31).
            { Kind: SceneEntityKind.Camera } =>
                EntityTitle(primary.Value, "Camera"),
            { Kind: SceneEntityKind.Prop } =>
                EntityTitle(primary.Value, "Object"),
            { Kind: SceneEntityKind.WorldObject } =>
                EntityTitle(primary.Value, "Object"),
            { Kind: SceneEntityKind.Overlay } =>
                EntityTitle(primary.Value, "Overlay"),
            // The empty state SAYS so, in the titlebar too.
            null => "Nothing selected",
            _ => "Poser",
        };
    }

    /// <summary>The selected entity's own name from the snapshot, by kind;
    /// the kind label only when the snapshot no longer holds it.</summary>
    private string EntityTitle(SelectionId id, string fallback)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                foreach (var camera in _scene.Snapshot.Cameras)
                    if (camera.Id.Equals(cameraId))
                        return camera.Name;
                break;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                foreach (var prop in _scene.Snapshot.Props)
                    if (prop.Id.Equals(propId))
                        return prop.Name;
                break;
            case { Kind: SceneEntityKind.WorldObject,
                WorldObject: { } worldId }:
                foreach (var worldObject in _scene.Snapshot.WorldObjects)
                    if (worldObject.Id.Equals(worldId))
                        return worldObject.Name;
                break;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                foreach (var overlay in _scene.Snapshot.Overlays)
                    if (overlay.Id.Equals(overlayId))
                        return overlay.Name;
                break;
        }
        return fallback;
    }

    private string LightTitle(SelectionId id)
    {
        foreach (var light in _scene.Snapshot.Lights)
            if (id.Light is { } lightId && light.Id.Equals(lightId))
                return light.Name;
        return "Light";
    }

    public override void Draw()
    {
        float gs = ImGuiHelpers.GlobalScale;
        _lastWidth = ImGui.GetWindowSize().X / gs;
        _lastHeight = ImGui.GetWindowSize().Y / gs;
        _lastPosition = ImGui.GetWindowPos();
        _overlayPresentation.Reconcile(_scene.Snapshot);
        BuildViewModel();
        // Hidden Inspector: the frame still built everything the parts read,
        // and the menu/dialog pumps below still run — only the chassis and
        // its content stay undrawn.
        // A drag held on the shell's own control keeps the window drawing
        // through the fade, invisible, so the held item is not torn away.
        if (!_contentHidden
            && (!Controls.ManipulationHide.Hidden || Controls.ManipulationDrag.ShellHeld))
        {
            using var manipulationFade = Controls.ManipulationHide.FadeScope();
            AppShellView.Draw(
                _vm, ImGui.GetWindowPos(), ImGui.GetWindowSize());
        }
        DrawShellMenu();
        DrawActorContextMenu();
        // Window-level: the attach picker outlives the context menu that
        // opened it.
        _companions.DrawPicker();
        // The expression row is drawn on the face surface (the pose rail and
        // the Expression workspace tab), which exists on every tab; its picker
        // is therefore pumped at the shell. A no-op on the frames the
        // animation pane already drew the surface for its own rows.
        _animationPane.DrawExpressionPicker();
        DrawBoneContextMenu();
        DrawOverlayContextMenu();
        DrawOverlayNodeContextMenu();
        DrawReferenceImageContextMenu();
        DrawLightContextMenu();
        DrawCameraContextMenu();
        DrawPropContextMenu();
        DrawWorldObjectContextMenu();
        DrawGroupContextMenu();
        DrawSelectionContextMenu();
        DrawRenameModal();
        DrawEntityRenameModal();
        DrawBonePresetManager();
        // Both file-dialog pumps live at the shell, so a dialog opened from a
        // tab or a context menu survives subsequent selection changes.
        // surface next.
        _appearancePane.DrawBrowsers();
        _lightPane.DrawBrowsers();
        _cameraPane.DrawBrowsers();
        _poseFileSection.DrawBrowsers();
        _scenePane.DrawBrowsers();
        // An overlay created from the spawn browser binds a frame or more
        // later, with nothing selected and its pane therefore undrawn; the
        // pending select has to be pumped from the shell or it never lands.
        _overlayPane.Tick();
        // Unconditional, exactly like the dialog pumps: a library spawn binds
        // its actor frames later, and leaving library mode must not strand it.
        _libraryPane.Tick();
        // The companion catalog PREBUILDS from the shell's first frame — a
        // background walk over three sheets — so the attach picker never
        // opens against a catalog still building.
        _companionCatalog.EnsureLoaded();
        // Last, and over everything: until the notice is accepted the shell
        // has drawn a workspace that is visible but not interactive.
        _firstRunNotice.Draw();
    }

    /// <summary>The library is its OWN window — it never replaces the
    /// properties window. Raised for the window set to open it.</summary>
    public event Action? OnLibraryWindowRequested;

    public void ShowLibrary() => OnLibraryWindowRequested?.Invoke();

    /// <summary>The library window's seams: it draws from the same panes
    /// the shell owns.</summary>
    internal PoseLibraryPane LibraryPane => _libraryPane;
    internal PoseFileInspectorSection PoseFiles => _poseFileSection;
    internal ScenePane Scene => _scenePane;

    /// <summary>
    /// The one mode has been left — by an opener, or by any surface selecting
    /// an entity. Restates nothing beyond the outgoing pane's own hidden
    /// notice: leaving is never the last thing a caller does, and a resync
    /// here would resolve the strip against the selection mid-change — the
    /// outgoing one, or none at all — and settle <see cref="_activeTab"/> onto
    /// that strip's first tab, which is precisely the tab that leaving a mode
    /// promises to give back. Every caller resyncs once, after every change it
    /// is going to make.
    /// </summary>
    private void OnWorkspaceLeft(ShellWorkspace left)
    {
        // The library became its own window; no workspace mode remains
        // that owes a leave-notice.
        _ = left;
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(15);
        base.PostDraw();
    }

    // ── view-model assembly (once per frame) ─────────────────────────────

    private void BuildViewModel()
    {
        var primary = _selection.Primary;

        _vm.GPoseActive = _gPoseService.IsGPosing;
        // Look-through-on-select (option): a camera ARRIVING as the primary
        // selection becomes the live camera — once per arrival, so live can
        // still be switched away while the camera stays selected.
        if (primary is { Kind: SceneEntityKind.Camera, Camera: { } lookId })
        {
            if (!Equals(_lookThroughApplied, primary)
                && Config.ConfigurationService.Instance.Config.Camera
                    .LookThroughSelectedCamera)
            {
                _lookThroughApplied = primary;
                if (_bindings.Resolve(lookId) is
                    { Success: true, Value: { IsValid: true, IsLive: false } cam })
                    _cameraService.SetLive(cam);
            }
        }
        else
        {
            _lookThroughApplied = null;
        }
        _vm.SidebarWidthPx = _sidebarWidth;
        _vm.OnLibrary = _openLibrary ??= ShowLibrary;
        _vm.Collapsed = _collapsed;
        _vm.SidebarCollapsed = _sidebarCollapsed;
        _vm.InspectorSplit =
            Config.ConfigurationService.Instance.Config.UI.SplitInspector;
        _vm.Detached =
            Config.ConfigurationService.Instance.Config.UI.DetachedShell;
        _vm.TitleEntity = TitleEntity(primary);
        _vm.ContentKind = ContentKind(primary);
        // The shell's retained per-row state is swept on structural change
        // only: an identical rescan publishes no new revision, so hover and
        // interaction identity survive every refresh that changed nothing.
        _vm.SceneRevision = _scene.Revision;
        // The inspector rail stays on both tabs: bone selection and posing
        // remain available while animation plays, so the right column is
        // never reclaimed and the window width never depends on the tab.
        //
        // Pose owns a fixed outer viewport. Matrix scrolls only inside that
        // allocation; its nested ScrollRegion consumes the same physical
        // gutter the shell reserved, so mode changes cannot alter width.
        // Animation is a document and uses the shell's scroll.
        // Appearance has no pose rail; its content takes the released
        // width. The outer window size is untouched by tab changes.
        // Library mode's rail hosts the import options;
        // every other mode keeps the selection-typed rail.
        //
        // The delegate is stated even while collapsed: the shell's own
        // titlebar guard ignores it then, but a split inspector window keeps
        // hosting the rail through a collapse of the main window.
        // The inspector is PER TAB in the library: pose-import options
        // where poses apply; scene options where scenes load; the entry
        // inspector on objects; and NO rail on MCDFs. Everywhere else the
        // inspector is a THREE-PANEL column — the selected target, the
        // environment, or the scene, chosen by the selector band — and
        // selecting any entity snaps back to the target panel (you
        // selected it to inspect it).
        var railConfig = Config.ConfigurationService.Instance.Config.UI;
        if (primary is { } primaryNow && primaryNow != _lastPrimaryForMode)
        {
            if (_lastPrimaryForMode is not null && railConfig.InspectorMode != 0)
            {
                railConfig.InspectorMode = 0;
                Config.ConfigurationService.Instance.Save();
            }
            _lastPrimaryForMode = primaryNow;
        }
        {
            // The INSPECTOR is only ever the selected object. The mode
            // selector swaps the CONTENT side: the selection's tabs, the
            // environment page, or the scene page.
            _contentMode = railConfig.InspectorMode;
            _vm.InspectorMode = _contentMode;
            _vm.OnInspectorMode = next =>
            {
                Config.ConfigurationService.Instance.Config
                    .UI.InspectorMode = next;
                Config.ConfigurationService.Instance.Save();
            };
            _vm.DrawRail = _poseRail.Draw;
        }

        _vm.GizmoOperation = (int)_editorState.TransformTool;
        _vm.GizmoSpace = (int)_editorState.TransformOrientation;
        _vm.RotationPivot = (int)_editorState.RotationPivot;
        // The seg DESCRIBES the primary selected bone — its effective
        // mode through the one three-tier rule — and the global otherwise.
        var symmetryConfig =
            Config.ConfigurationService.Instance.Config;
        var primaryBone = _scene.Selection.Primary?.Bone;
        _vm.SymmetryMode = primaryBone is { } describedBone
            ? (int)Core.BoneSymmetry.EffectiveMode(
                symmetryConfig.PerBoneSymmetry,
                symmetryConfig.BoneSymmetryOverrides,
                symmetryConfig.AutoLinkPairedBones,
                _editorState.SymmetryMode,
                describedBone.CanonicalName)
            : (int)_editorState.SymmetryMode;
        // The pivot selector appears only where pivot choice changes the
        // active transform meaning: Rotate tool with a resolvable bone
        // selection. Parent needs a valid parent on the effective primary.
        // Both facts come from the shared resolver, which builds a dictionary
        // of the selected actor's complete bone set — so they are re-derived only
        // when the resolver's own two inputs move. The tool is not part of that
        // key: it decides whether the facts are shown, not what they are.
        RefreshPivotFacts();
        bool boneRotate = _editorState.TransformTool == TransformTool.Rotate &&
            _pivotPrimaryIsBone;
        _vm.RotationPivotEnabled = boneRotate;
        _vm.RotationPivotParentAvailable = boneRotate && _pivotParentAvailable;
        var toolbarActor = SelectedActorId();
        _vm.AnimationAvailable = toolbarActor is { } animActorId
            && _animation.IsSupported(animActorId);
        // The switch's polarity is "animation playing": on unless Poser holds
        // a zero speed override on the selected actor.
        _vm.AnimationOn = toolbarActor is not { } animActor
            || _animation.OverridesFor(animActor).OverallSpeed is not 0f;
        // The freeze is one process-global code patch held by the scene, so
        // the switch shows the global state and is live under every selection
        // and under none: nothing about the patch is per-actor.
        _vm.PhysicsOn = !_animation.IsPhysicsFrozen;
        // The sibling-link mode's second half. Co-selection reaches every
        // _l/_r pair; the same-delta catalog (both eyes, the Viera ear-variant
        // chains) pairs bones that are not _l/_r counterparts and cannot be
        // reached that way, so the one switch arms both. Read here rather than
        // wired once, so a Settings change takes effect on the next frame.
        _bonePosingService.LinkedBonesEnabled =
            Config.ConfigurationService.Instance.Config.LinkSiblingBones;
        _vm.CanUndo = _cleanTransforms.CanUndo;
        _vm.CanRedo = _cleanTransforms.CanRedo;
        _vm.UndoDescription = _cleanTransforms.UndoDescription;
        _vm.RedoDescription = _cleanTransforms.RedoDescription;
        // Entity creation has two entry points by design (approved shell): the
        // titlebar action and a section header's plus. Every one of them opens
        // the same surface, the spawn browser — the lights and cameras pluses
        // once made their own kind from a menu of their own, and no longer do.
        // References stay absent (not disabled) in the browser until their
        // runtime entity type exists.
        _vm.ShowSpawn = true;
        _vm.ShowProject = false;

        BuildSidebar(primary);
        BuildTabs(primary);
        ApplyTabLayout(_contentMode
            switch { 1 => _activeTab, 2 => "Scene", _ => _activeTab });
        BuildStatus(primary);
    }

    // ── effective-selection pivot facts, re-derived only on change ───────
    private readonly List<SelectionId> _pivotKey = new();
    private ulong _pivotRevision;
    private bool _pivotPrimed;
    private bool _pivotPrimaryIsBone;
    private bool _pivotParentAvailable;

    /// <summary>
    /// Restates the two facts the pivot selector needs from the effective
    /// transform selection. The resolver reads exactly two things — the ordered
    /// selection and the scene snapshot — so those two are the whole key, and a
    /// frame that changes neither does no work. A redraw or a slot rebind moves
    /// both (the generations in the ids and the published revision), so the
    /// facts are current on the first frame drawn after one.
    /// </summary>
    private void RefreshPivotFacts()
    {
        var selected = _selection.Selected;
        if (_pivotPrimed &&
            _pivotRevision == _scene.Revision &&
            SameSelection(_pivotKey, selected))
            return;

        _pivotPrimed = true;
        _pivotRevision = _scene.Revision;
        _pivotKey.Clear();
        _pivotKey.AddRange(selected);

        var effective = Application.Transforms.TransformTargetResolver.Resolve(
            selected, _scene.Snapshot,
            id => _groups.IsLockedChild(id, selected));
        _pivotPrimaryIsBone =
            effective is { Primary.Kind: Domain.Identity.TransformTargetKind.Bone };
        _pivotParentAvailable = false;
        if (!_pivotPrimaryIsBone ||
            effective!.Primary.Bone is not { } effectiveBone)
            return;

        foreach (var actor in _scene.Snapshot.Actors)
        {
            if (actor.Id.LogicalId != effectiveBone.Skeleton.Actor.LogicalId ||
                actor.GetSkeleton(effectiveBone.Slot) is not { } skeleton)
                continue;
            foreach (var bone in skeleton.Bones)
            {
                if (!bone.Id.Equals(effectiveBone))
                    continue;
                _pivotParentAvailable = bone.Parent != null;
                break;
            }
            break;
        }
    }

    /// <summary>Ordered element-wise compare against the retained key. The
    /// resolution depends on selection order (the first entry is the primary),
    /// so a count or set comparison would not be sound.</summary>
    private static bool SameSelection(
        List<SelectionId> cached,
        IReadOnlyList<SelectionId> current)
    {
        if (cached.Count != current.Count)
            return false;
        for (int i = 0; i < cached.Count; i++)
            if (cached[i] != current[i])
                return false;
        return true;
    }

    private void Undo()
    {
        if (_cleanTransforms.CanUndo)
            _cleanTransforms.Undo();
    }

    private void Redo()
    {
        if (_cleanTransforms.CanRedo)
            _cleanTransforms.Redo();
    }

    private static readonly bool[] RootTreeLines = Array.Empty<bool>();

    /// <summary>
    /// Restates the sidebar. The row tree is assembled only when the gate below
    /// flips; every other frame walks the retained rows and refreshes the flags
    /// that read live state, allocating nothing.
    ///
    /// <para>The gate is exactly the inputs that can change the row count or
    /// order: the published scene revision (the structural signature — actor
    /// set and generations, slot presence, bone counts), the search filter, and
    /// the disclosure version. Selection, actor visibility, pause state and
    /// library mode are per-row flags: they are refreshed in place, so they
    /// still land on the frame they change. A display name is a flag too,
    /// except while filtering, where it can change what matches — that case
    /// re-arms the gate.</para>
    /// </summary>
    private void BuildSidebar(SelectionId? primary)
    {
        // Trim hands back the same instance when there is nothing to trim, so
        // the common (unfiltered) frame builds no string here.
        string filter = _vm.SidebarSearch.Trim();
        if (!_sidebarBuilt ||
            _gazeDirty ||
            _sidebarRevision != _scene.Revision ||
            _sidebarGroupsRevision != _groups.Revision ||
            _sidebarExpandVersion != _expandVersion ||
            !string.Equals(_sidebarFilter, filter, StringComparison.Ordinal))
        {
            _sidebarBuilt = true;
            // Cleared before the walk, so a transition that lands mid-rebuild
            // re-arms rather than being swallowed by the rebuild it raced.
            _gazeDirty = false;
            _sidebarRevision = _scene.Revision;
            _sidebarGroupsRevision = _groups.Revision;
            _sidebarExpandVersion = _expandVersion;
            _sidebarFilter = filter;
            RebuildSidebar(filter);
        }

        RefreshSidebarFlags();
    }

    /// <summary>The gaze node's three aim points, in the order the gaze pane
    /// itself lists them. Static because the set is fixed: a gaze always has
    /// exactly these three parts, so no actor mints its own copy.</summary>
    private static readonly (string Label, string Icon, GazePart Part)[] GazeParts =
    {
        ("Eyes", "eye", GazePart.Eyes),
        ("Head", "head", GazePart.Head),
        ("Body", "body", GazePart.Body),
    };

    /// <summary>
    /// The cold path: the whole actor/bone tree. Everything here is discarded
    /// and restated wholesale, so it runs only behind
    /// <see cref="BuildSidebar"/>'s gate.
    /// </summary>
    private void RebuildSidebar(string filter)
    {
        _vm.Sections.Clear();
        // The sidebar is the OUTLINER — world things only, ONE list. The
        // library, the scene, and the environment left it in the
        // inspector-mode redesign: the first two are inspector panels,
        // the library is its own workspace.
        _vm.Sections.Add(_sceneSection);
        _sceneSection.Rows.Clear();
        _actorRows.Clear();

        bool filtering = filter.Length > 0;
        var snapshot = _scene.Snapshot.Actors;

        // Members the scene no longer holds leave their groups first;
        // then the root order reconciles against everything root-eligible
        // — every ungrouped entity, attached companions excepted (they
        // draw inside their owner's subtree). The eligibility walk runs
        // UNFILTERED: the filter decides what renders, never what holds a
        // seat.
        // A completed load staged the document's structure; the spawned
        // entities bind on the snapshot publish this rebuild reads, so
        // groups and order rebuild here — the stage clears once anything
        // resolves, or after enough rebuilds that it never will.
        RestorePendingStructure();

        _groups.Prune(id => SceneContains(id));
        _rootEntities.Clear();
        // The eligibility order seats what has no slot yet, so it IS the
        // initial order: cameras first, then actors, then the rest.
        foreach (var camera in _scene.Snapshot.Cameras)
        {
            var cameraId = SelectionId.ForCamera(camera.Id);
            if (_groups.GroupOf(cameraId) == null)
                _rootEntities.Add(cameraId);
        }
        foreach (var actor in snapshot)
        {
            // An attached companion is drawn inside its owner's subtree; one
            // whose owner left the scene falls back to a root of its own.
            if (actor.OwnerActor is { } owner && ContainsActor(snapshot, owner))
                continue;
            var id = SelectionId.ForActor(actor.Id);
            if (_groups.GroupOf(id) == null)
                _rootEntities.Add(id);
        }
        foreach (var prop in _scene.Snapshot.Props)
        {
            var id = SelectionId.ForProp(prop.Id);
            if (_groups.GroupOf(id) == null)
                _rootEntities.Add(id);
        }
        foreach (var worldObject in _scene.Snapshot.WorldObjects)
        {
            var id = SelectionId.ForWorldObject(worldObject.Id);
            if (_groups.GroupOf(id) == null)
                _rootEntities.Add(id);
        }
        foreach (var light in _scene.Snapshot.Lights)
        {
            var id = SelectionId.ForLight(light.Id);
            if (_groups.GroupOf(id) == null)
                _rootEntities.Add(id);
        }
        foreach (var overlay in _scene.Snapshot.Overlays)
        {
            var id = SelectionId.ForOverlay(overlay.Id);
            if (_groups.GroupOf(id) == null)
                _rootEntities.Add(id);
        }
        var order = _groups.SyncRoot(_rootEntities);

        // The USER'S order, kinds interleaved: a group head lists as a
        // folder row with its members nested one level in; every other
        // slot renders through the same constructions its grouped twin
        // uses.
        for (int s = 0; s < order.Count; s++)
        {
            var slot = order[s];
            if (!slot.IsGroup)
            {
                if (slot.Entity is { } entityId)
                    AddRootEntityRow(entityId, snapshot, filter, filtering);
                continue;
            }
            if (_groups.Find(slot.GroupId) is not { } group)
                continue;
            AddGroupRows(group, 0, snapshot, filter, filtering);
        }

        // A reference picture is an overlay by the same test the nodes are —
        // it is laid over the game rather than into the scene — so it closes
        // the list. It is not a scene entity: it carries no SelectionId,
        // holds no seat in the order, and its Tag is the session instance
        // itself, which is what every verb below dispatches on.
        AppendReferenceImageRows(filter, filtering);
    }

    /// <summary>Rebuilds a loaded document's groups and root order over
    /// the freshly spawned entities. Tokens resolve through the SAME
    /// binding registry the snapshot published from, so whatever this
    /// rebuild can see, this can name; members that never spawned are
    /// skipped by omission, and a group thinned below two dissolves
    /// exactly as it does live.</summary>
    private void RestorePendingStructure()
    {
        if (_sceneWorkflow.PendingSceneStructure is not { } pending)
            return;

        SelectionId? Resolve(global::Poser.Files.SceneStructureRef reference)
        {
            if (!pending.Tokens.TryGetValue(reference.Key, out var token))
                return null;
            return token switch
            {
                IActor actor => _bindings.GetActorId(actor) is { } actorId
                    ? SelectionId.ForActor(actorId)
                    : null,
                Game.PropHandle prop =>
                    _bindings.GetPropId(prop) is { } propId
                        ? SelectionId.ForProp(propId)
                        : null,
                Game.Overlays.OverlayNodeHandle node =>
                    _bindings.GetOverlayId(node) is { } overlayId
                        ? SelectionId.ForOverlay(overlayId)
                        : null,
                Game.WorldObjects.AdoptedWorldObject worldObject =>
                    _bindings.GetWorldObjectId(worldObject) is { } worldId
                        ? SelectionId.ForWorldObject(worldId)
                        : null,
                ILight light => _bindings.GetLightId(light) is { } lightId
                    ? SelectionId.ForLight(lightId)
                    : null,
                IVirtualCamera camera =>
                    _bindings.GetCameraId(camera) is { } cameraId
                        ? SelectionId.ForCamera(cameraId)
                        : null,
                _ => null,
            };
        }

        bool anyResolved = false;
        var groupIds = new Dictionary<Guid, Guid>();
        foreach (var entry in pending.Groups)
        {
            var members = new List<SelectionId>();
            foreach (var member in entry.Members)
                if (Resolve(member) is { } id)
                    members.Add(id);
            if (members.Count >= 2
                && _groups.Create(entry.Name, members) is { } made)
            {
                groupIds[entry.Key] = made.Id;
                anyResolved = true;
            }
        }
        // Nesting, then locks: a lock refuses the nest, and the parent
        // must exist before its child asks.
        foreach (var entry in pending.Groups)
            if (entry.Parent is { } parentKey
                && groupIds.TryGetValue(entry.Key, out var childId)
                && groupIds.TryGetValue(parentKey, out var parentId))
                _groups.Nest(childId, parentId);
        if (pending.RootOrder is { } orderRefs)
        {
            var slots =
                new List<global::Poser.Application.Scene.RootSlot>();
            foreach (var reference in orderRefs)
            {
                if (string.Equals(
                        reference.Kind, "group", StringComparison.Ordinal))
                {
                    if (groupIds.TryGetValue(reference.Key, out var groupId))
                        slots.Add(global::Poser.Application.Scene.RootSlot
                            .ForGroup(groupId));
                }
                else if (Resolve(reference) is { } id)
                    slots.Add(
                        global::Poser.Application.Scene.RootSlot.For(id));
            }
            if (slots.Count > 0)
            {
                _groups.RestoreOrder(slots);
                anyResolved = true;
            }
        }

        if (anyResolved || ++_pendingStructureAttempts > 30)
        {
            _pendingStructureAttempts = 0;
            _sceneWorkflow.ClearPendingStructure();
        }
    }

    /// <summary>One root entity's row(s) at depth 0 — the kind dispatch
    /// the old per-kind walks did, driven by the root order instead. The
    /// filter applies here, per row, exactly as those walks applied
    /// it.</summary>
    private void AddRootEntityRow(
        SelectionId id,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter,
        bool filtering)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                foreach (var actor in snapshot)
                    if (actor.Id.Equals(actorId))
                    {
                        AddActorRows(
                            _sceneSection, actor, snapshot, filter, filtering,
                            0, RootTreeLines, true);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                foreach (var prop in _scene.Snapshot.Props)
                    if (prop.Id.Equals(propId))
                    {
                        if (!filtering || MatchesSidebarFilter(filter, prop.Name))
                            _sceneSection.Rows.Add(PropRow(prop, 0));
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } worldId }:
                foreach (var worldObject in _scene.Snapshot.WorldObjects)
                    if (worldObject.Id.Equals(worldId))
                    {
                        if (!filtering
                            || MatchesSidebarFilter(filter, worldObject.Name))
                            _sceneSection.Rows.Add(WorldObjectRow(worldObject, 0));
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                foreach (var light in _scene.Snapshot.Lights)
                    if (light.Id.Equals(lightId))
                    {
                        if (!filtering || MatchesSidebarFilter(filter, light.Name))
                            _sceneSection.Rows.Add(LightRow(light, 0));
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                foreach (var camera in _scene.Snapshot.Cameras)
                    if (camera.Id.Equals(cameraId))
                    {
                        if (!filtering || MatchesSidebarFilter(filter, camera.Name))
                            _sceneSection.Rows.Add(CameraRow(camera, 0));
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                foreach (var overlay in _scene.Snapshot.Overlays)
                    if (overlay.Id.Equals(overlayId))
                    {
                        if (!filtering
                            || MatchesSidebarFilter(filter, overlay.Name))
                            _sceneSection.Rows.Add(OverlayRow(overlay, 0));
                        return;
                    }
                return;
        }
    }

    /// <summary>The mark for one overlay kind. A dialogue panel, a bubble and
    /// a status line are three different things on screen, so they are three
    /// different marks in the tree.</summary>
    private static TablerIcon OverlayIcon(
        OverlayNodeKind kind) => kind switch
    {
        OverlayNodeKind.Balloon =>
            TablerIcon.MessageCircle,
        OverlayNodeKind.Status => TablerIcon.Star,
        _ => TablerIcon.Message,
    };

    /// <summary>The mark for one light kind, shared by the sidebar rows and
    /// the lights header's type chooser: a kind means the same thing wherever
    /// it is shown, so it is drawn from one place.</summary>
    private static TablerIcon KindIcon(LightKind kind) => kind switch
    {
        LightKind.Directional => TablerIcon.Sun,
        LightKind.Point => TablerIcon.Bulb,
        LightKind.Area => TablerIcon.LightPanel,
        _ => TablerIcon.Spotlight,
    };

    /// <summary>
    /// The reference pictures, as overlays rows. The label is the file stem,
    /// deduped: the roster mints identity per add precisely so the same sheet
    /// can be placed twice, and two rows reading "sketch" would be two rows
    /// naming nothing. The second and later occurrences carry an ordinal, so
    /// the first one keeps the plain name.
    /// </summary>
    private void AppendReferenceImageRows(string filter, bool filtering)
    {
        var images = _referenceImages.Instances;
        if (images.Count == 0)
            return;
        _referenceStemCounts.Clear();
        for (int i = 0; i < images.Count; i++)
        {
            var image = images[i];
            string stem = image.Name;
            _referenceStemCounts.TryGetValue(stem, out int seen);
            _referenceStemCounts[stem] = seen + 1;
            string label = seen == 0
                ? stem
                : $"{stem} ({(seen + 1).ToString(CultureInfo.InvariantCulture)})";
            // The filter reads the displayed label.
            if (filtering && !MatchesSidebarFilter(filter, label))
                continue;
            _sceneSection.Rows.Add(new ShellSidebarRow
            {
                Label = label,
                Count = "",
                Icon = TablerIcon.Photo,
                Tag = image,
                LightActions = true,
                LightOn = !ReferenceImageSession.IsHidden(image),
            });
        }
    }

    /// <summary>Scratch for the stem dedupe; a sidebar rebuild must not mint a
    /// dictionary to count names.</summary>
    private readonly Dictionary<string, int> _referenceStemCounts = new();

    /// <summary>Flips one world class's handles. The glyph's own flag is
    /// restated immediately so it lights with the click rather than on the
    /// next refresh.</summary>
    private void ToggleWorldClass(int index)
    {
        if (index < 0 || index >= _worldClasses.Length)
            return;
        var (kind, entry) = _worldClasses[index];
        _worldAdoption.SetShown(kind, !_worldAdoption.IsShown(kind));
        entry.On = _worldAdoption.IsShown(kind);
    }

    /// <summary>
    /// The warm frame's entire sidebar cost: the retained rows' live flags.
    /// Nothing is created and no string is built — a display name that really
    /// changed re-arms the rebuild gate, and only while a filter is active,
    /// where the name decides whether the row is listed at all.
    /// </summary>
    private void RefreshSidebarFlags()
    {
        // The class glyphs read the current adoption source for the same reason
        // every other action glyph does: waiting for a republish would leave
        // the glyph behind the click that flipped it.
        foreach (var (kind, entry) in _worldClasses)
            entry.On = _worldAdoption.IsShown(kind);

        // ONE walk over the one section, dispatching on the tag. Every
        // state glyph reads the live object, never the descriptor: the
        // change moves the scene signature, and waiting for the republish
        // would leave the glyph behind the click that flipped it.
        //
        // The head and its children never light together: while the
        // selection IS the group, only the head row wears the pill — the
        // one exception is actor bones, whose dual highlight is the
        // posing tree's own rule.
        var matchedGroup = _groups.ActiveSelection(_selection.Selected);
        var rows = _sceneSection.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // A reference row carries the session instance, not a selection.
            // Its eye restates the session's own answer, live.
            if (row.Tag is ReferenceImageInstance rowImage)
            {
                row.LightOn = !ReferenceImageSession.IsHidden(rowImage);
                continue;
            }
            if (row.Tag is GroupRowTag tag)
            {
                row.Active = matchedGroup?.Id == tag.Id;
                continue;
            }
            // Category rows carry a string tag and own no selection state.
            if (row.Tag is not SelectionId id)
                continue;
            row.Active = row.GroupMember
                ? matchedGroup == null && _selection.IsSelected(id)
                : _selection.IsSelected(id);
            if (id.Camera is { } rowCameraId &&
                _bindings.Resolve(rowCameraId) is
                    { Success: true, Value: { } liveCamera })
            {
                row.CameraLive = liveCamera.IsLive;
                row.CameraLocked = liveCamera.IsLocked;
                // The seat retargets tracking onto the SELECTED actor —
                // it has work exactly when an actor is selected.
                row.CameraCanRecenter = SelectedActorRef() != null;
            }
            else if (id.Overlay is { } overlayId &&
                _bindings.Resolve(overlayId) is
                    { Success: true, Value: { } liveOverlay })
                row.LightOn = liveOverlay.Visible;
            else if (id.Prop is { } propId &&
                _bindings.Resolve(propId) is { Success: true, Value: { } prop })
                row.LightOn = prop.Visible;
            else if (id.WorldObject is { } borrowedId &&
                _bindings.Resolve(borrowedId) is
                    { Success: true, Value: { } borrowed })
                row.LightOn = borrowed.Visible;
            else if (id.Light is { } lightId &&
                _bindings.Resolve(lightId) is { Success: true, Value: { } light })
                row.LightOn = light.IsOn;
        }

        // The game's target, once per frame: its row's crosshair stands at
        // full opacity while every other actor's fades — the live camera's
        // treatment.
        Guid? targetLineage =
            _actorManager.GetGPoseTarget() is { } gposeTarget
                && _bindings.GetActorId(gposeTarget) is { } gposeTargetId
                ? gposeTargetId.LogicalId
                : null;
        for (int a = 0; a < _actorRows.Count; a++)
        {
            var state = _actorRows[a];
            var row = state.Row;
            var resolved = _bindings.Resolve(state.Id);
            row.ActorVisible = resolved.Success
                ? _spawnService.IsVisible(resolved.Value!)
                : !state.SnapshotHidden;
            // Pause offers while ANYTHING moves; Resume otherwise —
            // pause stops the entire stack, play overrides every
            // individual hold (ruled 2026-09-01).
            row.ActorPaused = !_animation.AnyPlaying(state.Id);
            row.ActorTargeted = targetLineage == state.Id.LogicalId;

            string label = Config.ConfigurationService.Instance.GetDisplayName(
                state.Id.LogicalId, state.RawName);
            if (string.Equals(label, row.Label, StringComparison.Ordinal))
                continue;
            row.Label = label;
            // A rename can change what the filter matches, so the row set has
            // to be derived again; unfiltered, the new label is the whole
            // change and the row already carries it.
            if (_sidebarFilter.Length > 0)
                _sidebarBuilt = false;
        }
    }

    private static bool ContainsActor(
        IReadOnlyList<ActorDescriptor> snapshot,
        ActorId id)
    {
        foreach (var actor in snapshot)
            if (actor.Id.Equals(id))
                return true;
        return false;
    }

    private static bool IsOwnedBy(ActorDescriptor candidate, ActorDescriptor owner)
        => candidate.IsCompanion
            && candidate.OwnerActor is { } link
            && link.Equals(owner.Id)
            && !candidate.Id.Equals(owner.Id);

    /// <summary>Trunk flags for the children of a row: the row's own ancestor
    /// flags plus one for the row itself, set when siblings still follow it.</summary>
    private static bool[] Descend(bool[] lines, bool isLast)
    {
        var descended = new bool[lines.Length + 1];
        Array.Copy(lines, descended, lines.Length);
        descended[lines.Length] = !isLast;
        return descended;
    }

    private static readonly string[] CameraTrackingModeOptions =
        ["Follow", "Pan", "Follow and pan", "None"];

    /// <summary>One exact bone in the flat tracking picker.</summary>

    /// <summary>Draws one exact actor and its flat concrete-bone picker.</summary>
    private void DrawCameraTrackingActors(
        Crystarium.FormScope form, IVirtualCamera camera)
    {
        if (_bindings.GetCameraId(camera) is not { } cameraId)
        {
            form.Status("Tracking is unavailable for this camera.");
            return;
        }

        var actor = ReconcileCameraTrackingActor(cameraId, camera);
        bool locked = camera.IsLocked;
        form.Switch(
            "Tracking",
            camera.IsTracking,
            value => camera.IsTracking = value,
            help: "Keep the tracked bones in view every frame",
            disabled: locked);
        form.Dropdown(
            "Mode",
            CameraTrackingModeOptions,
            (int)camera.TrackingMode,
            selected => camera.TrackingMode = (CameraTrackingMode)selected,
            disabled: locked,
            help: "Follow moves the camera with the bones, Pan swings the "
                + "view onto them, Follow and pan blends both");

        form.Actions(
            string.Empty,
            actions =>
            {
                actions.Button(
                    "Select bones",
                    () =>
                    {
                        if (actor != null)
                            OpenCameraBonePicker(cameraId, actor.Id, camera);
                    },
                    style: ControlStyle.Workspace with { Width = UiWidth.Fill },
                    disabled: locked || actor == null,
                    help: actor == null
                        ? "Choose an actor first"
                        : $"Choose exact bones on {ActorDisplayName(actor)}",
                    id: "camera-track-select-bones");
                // Picking in the view: a click takes a bone, Ctrl-click
                // keeps adding. Another actor's bone moves the tracking
                // to that actor, as the list does.
                actions.IconButton(
                    TablerIcon.Crosshair,
                    () => global::Poser.UI.Controls.BonePick.Begin(
                        multi: true,
                        bone =>
                        {
                            if (ResolveExactCamera(cameraId, camera) && !camera.IsLocked)
                                _cameraPane.ToggleTrackedBone(camera, bone);
                        },
                        onlyActor: actor?.Id),
                    disabled: locked,
                    help: actor == null
                        ? "Pick bones in the view"
                        : "Pick bones in the view on this actor");
            });

        PumpCameraBonePicker(cameraId, camera, actor);
    }

    /// <summary>Prunes stale and mixed tracking state, then resolves the one
    /// exact actor that currently owns tracking.</summary>
    private ActorDescriptor? ReconcileCameraTrackingActor(
        CameraId cameraId, IVirtualCamera camera)
    {
        if (!ResolveExactCamera(cameraId, camera))
            return null;
        if (camera.IsTargetLocked && camera.TargetActorId is null)
            _cameraService.ClearTargetActor(camera);

        ActorId? trackedOwner = null;
        for (int i = camera.TrackedBones.Count - 1; i >= 0; i--)
        {
            var tracked = camera.TrackedBones[i];
            if (_bindings.GetBoneId(tracked) is not { } boneId ||
                _bindings.Resolve(boneId) is not
                    { Success: true, Value: { } current } ||
                !ReferenceEquals(current, tracked))
            {
                camera.TrackedBones.RemoveAt(i);
                continue;
            }
            trackedOwner ??= boneId.Skeleton.Actor;
            if (trackedOwner != boneId.Skeleton.Actor)
                camera.TrackedBones.RemoveAt(i);
        }

        if (camera.TargetActorId is { } targetId)
        {
            if (!TryResolveExactActor(targetId, out var targetActor) ||
                !ReferenceEquals(camera.TargetActor, targetActor) ||
                ResolveActorDescriptor(targetId) is not { } targetDescriptor)
            {
                _cameraService.ClearTargetActor(camera);
            }
            else
            {
                if (trackedOwner is { } owner && owner != targetId)
                    camera.TrackedBones.Clear();
                return targetDescriptor;
            }
        }

        if (_actorManager.GetGPoseTarget() is not { } native ||
            _bindings.GetActorId(native) is not { } nativeId ||
            !TryResolveExactActor(nativeId, out var exactNative) ||
            !ReferenceEquals(native, exactNative) ||
            ResolveActorDescriptor(nativeId) is not { } nativeDescriptor)
        {
            camera.TrackedBones.Clear();
            return null;
        }
        if (trackedOwner is { } trackedId && trackedId != nativeId)
            camera.TrackedBones.Clear();
        return nativeDescriptor;
    }

    /// <summary>Resolves the exact explicit target, then the current native
    /// game target. Display names never recover identity.</summary>
    private ActorDescriptor? ResolveCameraTrackedActor(IVirtualCamera camera)
    {
        if (camera.TargetActorId is { } targetId)
        {
            if (TryResolveExactActor(targetId, out var target) &&
                ReferenceEquals(camera.TargetActor, target))
                return ResolveActorDescriptor(targetId);
            return null;
        }

        if (_actorManager.GetGPoseTarget() is not { } native ||
            _bindings.GetActorId(native) is not { } nativeId ||
            !TryResolveExactActor(nativeId, out var exactNative) ||
            !ReferenceEquals(native, exactNative))
            return null;
        return ResolveActorDescriptor(nativeId);
    }

    private void PumpCameraBonePicker(
        CameraId cameraId,
        IVirtualCamera camera,
        ActorDescriptor? currentActor)
    {
        if (_cameraBonePickerCamera == cameraId &&
            _cameraBonePickerActor is { } actorId &&
            currentActor?.Id == actorId &&
            ResolveActorDescriptor(actorId) is { } actor)
        {
            _cameraBoneChoices = BuildCameraBoneChoices(actor);
            _cameraTrackingBonePicker.UpdateItems(_cameraBoneChoices);
            _cameraTrackingBonePicker.UpdateSelection(
                TrackedBoneKeys(camera, actorId));
        }
        else if (_cameraTrackingBonePicker.IsOpen)
        {
            _cameraTrackingBonePicker.UpdateItems(
                Array.Empty<global::Poser.UI.BoneChoice>());
            _cameraTrackingBonePicker.UpdateSelection(
                new HashSet<string>(StringComparer.Ordinal));
        }
        _cameraTrackingBonePicker.Draw();
    }

    private void OpenCameraBonePicker(
        CameraId cameraId, ActorId actorId, IVirtualCamera camera)
    {
        if (!ResolveExactCamera(cameraId, camera) || camera.IsLocked ||
            ReconcileCameraTrackingActor(cameraId, camera)?.Id != actorId ||
            ResolveActorDescriptor(actorId) is not { } actor)
            return;
        _cameraBonePickerCamera = cameraId;
        _cameraBonePickerActor = actorId;
        _cameraBoneChoices = BuildCameraBoneChoices(actor);
        var options = new PickerOptions<global::Poser.UI.BoneChoice>
        {
            Query = CameraBoneSearch,
            Badge = choice => choice.Badge,
        };
        _cameraTrackingBonePicker.OpenMulti(
            $"camera-tracking-bones:{cameraId}:{actorId}",
            ActorDisplayName(actor),
            _cameraBoneChoices,
            choice => choice.Label,
            choice => choice.Key,
            TrackedBoneKeys(camera, actorId),
            (choice, _) => ToggleCameraTrackedBone(
                cameraId, actorId, choice, camera),
            options: in options);
    }

    private void ToggleCameraTrackedBone(
        CameraId cameraId,
        ActorId actorId,
        global::Poser.UI.BoneChoice choice,
        IVirtualCamera camera)
    {
        var boneId = choice.BoneId;
        if (boneId.Skeleton.Actor != actorId || camera.IsLocked ||
            _selection.Primary is not
                { Kind: SceneEntityKind.Camera, Camera: { } selectedCamera }
            || selectedCamera != cameraId ||
            !ResolveExactCamera(cameraId, camera) ||
            ReconcileCameraTrackingActor(cameraId, camera)?.Id != actorId)
            return;
        _cameraPane.ToggleTrackedBone(camera, boneId);
    }

    private bool ResolveExactCamera(CameraId cameraId, IVirtualCamera camera)
    {
        var resolved = _bindings.Resolve(cameraId);
        return resolved.Success && ReferenceEquals(resolved.Value, camera)
            && _bindings.GetCameraId(camera) == cameraId;
    }

    private bool TryResolveExactActor(ActorId actorId, out IActor actor)
    {
        var resolved = _bindings.Resolve(actorId);
        if (resolved.Success && resolved.Value is { } exact &&
            _bindings.GetActorId(exact) == actorId)
        {
            actor = exact;
            return true;
        }
        actor = null!;
        return false;
    }

    private bool ResolveExactActor(ActorId actorId) =>
        TryResolveExactActor(actorId, out _);

    private ActorDescriptor? ResolveActorDescriptor(ActorId actorId) =>
        ResolveExactActor(actorId)
            ? _scene.Snapshot.Actors.FirstOrDefault(actor => actor.Id == actorId)
            : null;

    private HashSet<string> TrackedBoneKeys(
        IVirtualCamera camera, ActorId actorId) =>
        camera.TrackedBones
            .Select(_bindings.GetBoneId)
            .Where(id => id is { } boneId &&
                boneId.Skeleton.Actor == actorId)
            .Select(id => id!.Value.ToString())
            .ToHashSet(StringComparer.Ordinal);

    private IReadOnlyList<global::Poser.UI.BoneChoice> BuildCameraBoneChoices(
        ActorDescriptor actor)
    {
        var rows = new List<global::Poser.UI.BoneChoice>();
        var skeleton = actor.CharacterSkeleton;
        if (skeleton != null)
        {
            var byName = new Dictionary<string,
                (BoneDescriptor Bone, int Ordinal)>(StringComparer.Ordinal);
            int ordinal = 0;
            foreach (var bone in skeleton.Bones)
                if (!bone.IsHidden && !IsBoneSuppressed(bone))
                    byName[bone.Id.CanonicalName] = (bone, ordinal++);
            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var categories = new List<BuiltCategory>();
            foreach (var root in Core.BoneInfo.KtisisBoneCategories.Roots)
                if (BuildKtisisCategory(
                        root, byName, claimed, string.Empty, filtering: false)
                    is { } category)
                    categories.Add(category);
            var leftovers = byName.Values
                .Where(entry => !claimed.Contains(entry.Bone.Id.CanonicalName))
                .OrderBy(entry => entry.Ordinal)
                .Select(entry => entry.Bone)
                .ToList();
            if (leftovers.Count > 0)
                categories.Add(new BuiltCategory(
                    "Other", "Other", leftovers, leftovers, []));
            foreach (var category in categories)
                AddCameraCategoryBones(rows, category, []);
        }

        foreach (var auxiliary in actor.Skeletons.Where(value =>
            value.Id.Slot != PoseSlot.Character))
        {
            string label = SlotLabel(auxiliary.Id.Slot);
            foreach (var bone in auxiliary.Bones)
            {
                if (bone.IsHidden || IsBoneSuppressed(bone))
                    continue;
                rows.Add(new global::Poser.UI.BoneChoice(
                    bone.Id.ToString(),
                    bone.DisplayName,
                    $"{label} {bone.DisplayName} {bone.Id.CanonicalName}",
                    bone.Id,
                    label));
            }
        }
        return rows;
    }

    private static void AddCameraCategoryBones(
        List<global::Poser.UI.BoneChoice> rows,
        BuiltCategory category,
        string[] ancestors)
    {
        var contexts = new string[ancestors.Length + 1];
        Array.Copy(ancestors, contexts, ancestors.Length);
        contexts[^1] = category.Label;
        foreach (var child in category.Children)
            AddCameraCategoryBones(rows, child, contexts);
        string searchContext = string.Join(' ', contexts);
        foreach (var bone in category.VisibleBones)
            rows.Add(new global::Poser.UI.BoneChoice(
                bone.Id.ToString(),
                bone.DisplayName,
                $"{searchContext} {bone.DisplayName} "
                    + bone.Id.CanonicalName,
                bone.Id,
                category.Label));
    }

    private IReadOnlyList<global::Poser.UI.BoneChoice> CameraBoneSearch(string query) =>
        query.Length == 0
            ? _cameraBoneChoices
            : _cameraBoneChoices.Where(choice => choice.SearchText.Contains(
                query, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// One actor's subtree: owned companions first, then bone categories, then
    /// auxiliary slots. Depth and trunk flags are inherited, so an attached
    /// companion draws the same tree one level in and keeps its own subtree.
    /// </summary>
    private ShellSidebarRow PropRow(PropDescriptor prop, int depth) => new()
    {
        Label = prop.Name,
        Draggable = true,
        Count = "",
        Icon = TablerIcon.Moneybag,
        Depth = depth,
        ForceIcon = depth > 0,
        Tag = SelectionId.ForProp(prop.Id),
        LightActions = true,
        LightOn = prop.Visible,
    };

    private ShellSidebarRow WorldObjectRow(
        WorldObjectDescriptor worldObject, int depth)
    {
        bool isVfx = worldObject.Path.EndsWith(
            ".avfx", StringComparison.OrdinalIgnoreCase);
        return new ShellSidebarRow
        {
            Label = worldObject.Name,
            Draggable = true,
            Count = "",
            // World objects wear the plant row mark; a VFX burns instead.
            Icon = isVfx ? TablerIcon.Fire : TablerIcon.Plant,
            Depth = depth,
            ForceIcon = depth > 0,
            Tag = SelectionId.ForWorldObject(worldObject.Id),
            LightActions = true,
            LightOn = worldObject.Visible,
            // Effects play and pause; scenery switches day and night
            // (its animation pause, borrowed scenery only, lives on the
            // properties page).
            PauseAction = isVfx,
            Paused = worldObject.VfxPaused,
            NightAction = !isVfx,
            Night = worldObject.Night,
        };
    }

    private ShellSidebarRow LightRow(LightDescriptor light, int depth) => new()
    {
        Label = light.Name,
        // A bone-attached light rides its bone — its place is not the
        // user's to move.
        Draggable = light.AttachedBone == null,
        Count = "",
        // Ownership outranks kind in the mark: a borrowed light is
        // released rather than destroyed, and the row has to say so
        // before the light is ever selected.
        Icon = light.Ownership switch
        {
            LightOwnership.GPose => TablerIcon.Camera,
            LightOwnership.World => TablerIcon.BuildingStore,
            _ => KindIcon(light.Kind),
        },
        Depth = depth,
        ForceIcon = depth > 0,
        Tag = SelectionId.ForLight(light.Id),
        LightActions = true,
        LightOn = light.IsOn,
    };

    private ShellSidebarRow CameraRow(CameraDescriptor camera, int depth) => new()
    {
        Label = camera.Name,
        Draggable = true,
        CameraMark = camera.IsDefault
            ? "M"
            : camera.Kind == CameraKind.Free ? "F" : "C",
        Icon = camera.Kind == CameraKind.Free
            ? TablerIcon.Video
            : TablerIcon.Camera,
        Depth = depth,
        ForceIcon = depth > 0,
        Tag = SelectionId.ForCamera(camera.Id),
        CameraActions = true,
        CameraLive = camera.IsLive,
        CameraLocked = camera.IsLocked,
    };

    private ShellSidebarRow OverlayRow(
        OverlayDescriptor overlay, int depth) => new()
    {
        Label = overlay.Name,
        Draggable = true,
        Count = "",
        Icon = OverlayIcon(overlay.Kind),
        Depth = depth,
        ForceIcon = depth > 0,
        Tag = SelectionId.ForOverlay(overlay.Id),
        LightActions = true,
        LightOn = overlay.Visible,
    };

    /// <summary>One grouped member's row(s), nested one level in — the
    /// SAME constructions the kind walks use, so a grouped row never
    /// drifts from its ungrouped twin.</summary>
    /// <summary>A group head at <paramref name="depth"/>, its members one
    /// level in, then its subgroups the same way — to
    /// <see cref="global::Poser.Application.Scene.SceneGroups.MaxDepth"/>.</summary>
    private void AddGroupRows(
        global::Poser.Application.Scene.SceneGroup group,
        int depth,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter,
        bool filtering,
        bool[]? lines = null,
        bool isLast = true)
    {
        string key = "group:" + group.Id;
        bool expanded = filtering || !_collapsedNodes.Contains(key);
        bool locked = _groups.IsLocked(group);
        lines ??= RootTreeLines;
        _sceneSection.Rows.Add(new ShellSidebarRow
        {
            Label = group.Name,
            Icon = TablerIcon.Folder,
            ForceIcon = true,
            Draggable = !locked,
            DropContainer = !locked,
            GroupActions = true,
            GroupLocked = group.Locked,
            GroupHidden = group.Hidden,
            GroupPaused = group.Paused,
            HasChildren = group.ItemCount > 0,
            Depth = depth,
            IsLastChild = isLast,
            TreeLines = lines,
            ExpandKey = key,
            Expanded = expanded,
            Tag = new GroupRowTag(group.Id),
        });
        if (!expanded)
            return;
        // The branch lines below this head: a trunk continues at this
        // level while a later sibling follows the group.
        // Index k of the lines is level k; a root head's children still
        // descend one level (index 0 is the root and draws nothing), or
        // every trunk below sits one level too far left.
        var childLines = Descend(lines, isLast);
        int memberStart = _sceneSection.Rows.Count;
        for (int m = 0; m < group.Members.Count; m++)
            AddGroupMemberRow(
                group.Members[m], snapshot, filter, filtering,
                isLast: m == group.Members.Count - 1 && group.Children.Count == 0,
                depth: depth + 1,
                lines: childLines);
        for (int r = memberStart; r < _sceneSection.Rows.Count; r++)
        {
            _sceneSection.Rows[r].GroupMember = true;
            if (locked)
                _sceneSection.Rows[r].Draggable = false;
        }
        for (int c = 0; c < group.Children.Count; c++)
            if (_groups.Find(group.Children[c]) is { } child)
                AddGroupRows(
                    child, depth + 1, snapshot, filter, filtering,
                    childLines, isLast: c == group.Children.Count - 1);
    }

    private void AddGroupMemberRow(
        SelectionId member,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter,
        bool filtering,
        bool isLast,
        int depth = 1,
        bool[]? lines = null)
    {
        lines ??= RootTreeLines;
        switch (member)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                foreach (var actor in snapshot)
                    if (actor.Id.Equals(actorId))
                    {
                        AddActorRows(
                            _sceneSection, actor, snapshot, filter,
                            filtering, depth, lines, isLast);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                foreach (var prop in _scene.Snapshot.Props)
                    if (prop.Id.Equals(propId))
                    {
                        var row = PropRow(prop, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } worldId }:
                foreach (var worldObject in _scene.Snapshot.WorldObjects)
                    if (worldObject.Id.Equals(worldId))
                    {
                        var row = WorldObjectRow(worldObject, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                foreach (var light in _scene.Snapshot.Lights)
                    if (light.Id.Equals(lightId))
                    {
                        var row = LightRow(light, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                foreach (var camera in _scene.Snapshot.Cameras)
                    if (camera.Id.Equals(cameraId))
                    {
                        var row = CameraRow(camera, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                foreach (var overlay in _scene.Snapshot.Overlays)
                    if (overlay.Id.Equals(overlayId))
                    {
                        var row = OverlayRow(overlay, depth);
                        row.IsLastChild = isLast;
                        row.TreeLines = lines;
                        _sceneSection.Rows.Add(row);
                        return;
                    }
                return;
        }
    }

    /// <summary>Whether the scene still holds the entity — the groups'
    /// prune probe.</summary>
    private bool SceneContains(SelectionId id)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                foreach (var actor in _scene.Snapshot.Actors)
                    if (actor.Id.Equals(actorId))
                        return true;
                return false;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                foreach (var prop in _scene.Snapshot.Props)
                    if (prop.Id.Equals(propId))
                        return true;
                return false;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } worldId }:
                foreach (var worldObject in _scene.Snapshot.WorldObjects)
                    if (worldObject.Id.Equals(worldId))
                        return true;
                return false;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                foreach (var light in _scene.Snapshot.Lights)
                    if (light.Id.Equals(lightId))
                        return true;
                return false;
            case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                foreach (var camera in _scene.Snapshot.Cameras)
                    if (camera.Id.Equals(cameraId))
                        return true;
                return false;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                foreach (var overlay in _scene.Snapshot.Overlays)
                    if (overlay.Id.Equals(overlayId))
                        return true;
                return false;
            default:
                return false;
        }
    }

    private void AddActorRows(
        ShellSidebarSection section,
        ActorDescriptor actor,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter,
        bool filtering,
        int depth,
        bool[] lines,
        bool isLast)
    {
        // Generation is part of the disclosure identity: a replacement actor
        // must not inherit the old generation's expanded/collapsed state.
        var actorKey = "actor:" + actor.Id;
        // The snapshot's raw name is fixed until the next revision, so the
        // object-index strip runs here and the warm-frame label refresh is
        // a pair of dictionary lookups.
        string rawName = DisplayName(actor.Name);
        string actorLabel = Config.ConfigurationService.Instance.GetDisplayName(
            actor.Id.LogicalId, rawName);

        List<ActorDescriptor>? companions = null;
        foreach (var candidate in snapshot)
        {
            if (IsOwnedBy(candidate, actor))
                (companions ??= new List<ActorDescriptor>()).Add(candidate);
        }

        var groups = new List<(Core.BoneInfo.BoneCategory Cat, List<BoneDescriptor> Bones)>();
        var skeleton = actor.CharacterSkeleton;
        if (skeleton != null)
        {
            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsHidden || IsBoneSuppressed(bone)) continue;
                var cat = Core.BoneInfo.BoneInfoService.GetCategory(bone.Id.CanonicalName);
                var slot = groups.FindIndex(g => g.Cat == cat);
                if (slot < 0) { groups.Add((cat, new List<BoneDescriptor>())); slot = groups.Count - 1; }
                groups[slot].Bones.Add(bone);
            }
            groups.Sort((a, b) => ((int)a.Cat).CompareTo((int)b.Cat));
        }

        // Present auxiliary slots become one additional group each under
        // the same actor row (slots are never separate actors).
        var auxSkeletons = actor.Skeletons
            .Where(s => s.Id.Slot != Domain.Identity.PoseSlot.Character)
            .OrderBy(s => (int)s.Id.Slot)
            .ToList();

        bool actorMatches = MatchesSidebarFilter(filter, actorLabel, actor.Name);
        // Category labels match the rows emitted below.
        bool hasMatchingBone = groups.Exists(group =>
            group.Bones.Exists(bone => MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName)))
            || (groups.Count > 0 && KtisisCategoryLabelMatches(filter));
        bool hasMatchingAux = auxSkeletons.Exists(aux =>
            MatchesSidebarFilter(filter, SlotLabel(aux.Id.Slot))
            || aux.Bones.Any(bone => !bone.IsHidden && !IsBoneSuppressed(bone) &&
                MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName)));
        var shownCompanions = companions;
        if (filtering && companions != null)
            shownCompanions = companions.FindAll(
                companion => ActorSubtreeMatches(companion, snapshot, filter));
        if (filtering && !actorMatches && !hasMatchingBone && !hasMatchingAux
            && (shownCompanions == null || shownCompanions.Count == 0))
            return;

        // Actor roots first appear collapsed; lineage keys survive
        // refreshes, so a scene refresh cannot reset existing disclosure.
        // Only explicit disclosure clicks expand — external bone selection
        // (map, matrix, overlay, gizmo) never changes tree disclosure.
        if (_knownActorNodes.Add(actorKey))
            _collapsedNodes.Add(actorKey);
        bool expanded = filtering || !_collapsedNodes.Contains(actorKey);
        var actorSelectionId = SelectionId.ForActor(actor.Id);
        var actorRow = new ShellSidebarRow
        {
            Label = actorLabel,
            Count = "",
            Icon = SidebarActorIcon(actor),
            Depth = depth,
            ForceIcon = depth > 0,
            // The disclosure affordance is permanent; an unresolved
            // skeleton only disables it until the snapshot exposes bones.
            HasChildren = true,
            ExpanderDisabled = skeleton == null,
            Expanded = expanded,
            IsLastChild = isLast,
            TreeLines = lines,
            Tag = actorSelectionId,
            ExpandKey = actorKey,
            ActorActions = true,
            // An attached companion rides its owner — not the user's to
            // move while attached.
            Draggable = actor.OwnerActor == null,
        };
        section.Rows.Add(actorRow);
        // Selection, visibility, pause and the display name are stated by
        // the flag refresh — including for this frame.
        _actorRows.Add(new ActorRowState(
            actorRow, actor.Id, rawName, actor.IsHidden));
        if (!expanded)
            return;

        bool companionsFollow = shownCompanions is { Count: > 0 };
        bool categoriesFollow = skeleton != null && (!filtering || hasMatchingBone);
        bool auxFollows = auxSkeletons.Count > 0 && (!filtering || hasMatchingAux);
        var childLines = Descend(lines, isLast);

        // A fixed-position gaze anchor is an actor child and is shown only
        // while its actor binding resolves.
        if (_bindings.Resolve(actor.Id) is { Success: true, Value: { } gazeActor } &&
            _gazeService.GetGazeState(gazeActor).Mode == GazeTargetMode.Position)
        {
            bool gazeLast = !companionsFollow && !categoriesFollow && !auxFollows;
            // Gaze rows start expanded; explicit disclosure clicks persist in
            // the same collapsed-node set as other hierarchy rows.
            var gazeKey = actorKey + "/gaze";
            bool gazeExpanded = filtering || !_collapsedNodes.Contains(gazeKey);
            section.Rows.Add(new ShellSidebarRow
            {
                Label = "Gaze control",
                Count = "",
                Depth = depth + 1,
                IconName = "eye",
                ForceIcon = true,
                // Like a merged category/bone row: the body still selects
                // the shared anchor (Tag) while the chevron toggles the
                // string key (ExpandKey).
                HasChildren = true,
                Expanded = gazeExpanded,
                IsLastChild = gazeLast,
                TreeLines = childLines,
                Tag = SelectionId.ForGazeTarget(actor.Id),
                ExpandKey = gazeKey,
            });
            // The gaze is three points, not one: eyes, head and body each
            // carry their own target, and each is separately selectable so
            // the world gizmo can grab one part alone.
            if (gazeExpanded)
            {
                var partLines = Descend(childLines, gazeLast);
                for (int p = 0; p < GazeParts.Length; p++)
                {
                    var (partLabel, partIcon, part) = GazeParts[p];
                    var partId = SelectionId.ForGazeTarget(actor.Id, part);
                    section.Rows.Add(new ShellSidebarRow
                    {
                        Label = partLabel,
                        Count = "",
                        Depth = depth + 2,
                        IconName = partIcon,
                        ForceIcon = true,
                        HasChildren = false,
                        IsLastChild = p == GazeParts.Length - 1,
                        TreeLines = partLines,
                        Active = _selection.IsSelected(partId),
                        Tag = partId,
                    });
                }
            }
        }

        // Attached companions lead the subtree: they are actors, and actors
        // read before the owner's own bones.
        if (shownCompanions != null)
        {
            for (int c = 0; c < shownCompanions.Count; c++)
                AddActorRows(
                    section, shownCompanions[c], snapshot, filter, filtering,
                    depth + 1, childLines,
                    c == shownCompanions.Count - 1
                        && !categoriesFollow && !auxFollows);
        }

        // The actor expands into nested bone categories; unclaimed bones use
        // the Other group.
        if (categoriesFollow)
        {
            // Preserve the skeleton enumeration order within each category.
            var byName = new Dictionary<string, (BoneDescriptor Bone, int Ordinal)>(
                StringComparer.Ordinal);
            int ordinal = 0;
            foreach (var (_, bones) in groups)
                foreach (var bone in bones)
                    byName[bone.Id.CanonicalName] = (bone, ordinal++);

            var claimed = new HashSet<string>(StringComparer.Ordinal);
            var built = new List<BuiltCategory>();
            foreach (var rootCategory in Core.BoneInfo.KtisisBoneCategories.Roots)
                if (BuildKtisisCategory(
                        rootCategory, byName, claimed, filter, filtering)
                    is { } presentRoot)
                    built.Add(presentRoot);

            // Whatever the tree left unclaimed — modded bones outside the
            // Unclaimed schema bones keep a home.
            var leftovers = new List<BoneDescriptor>();
            foreach (var (bone, _) in byName.Values)
                if (!claimed.Contains(bone.Id.CanonicalName)
                    && (!filtering || MatchesSidebarFilter(
                        filter, bone.DisplayName, bone.Id.CanonicalName)))
                    leftovers.Add(bone);
            if (leftovers.Count > 0)
                built.Add(new BuiltCategory(
                    "Other", "Other", leftovers, leftovers, []));

            // One skeleton row hosts the categories and their overlay state.
            if (built.Count > 0)
            {
                var skeletonKey = actorKey + "/skeleton";
                // The skeleton starts folded like the actor above it;
                // only a disclosure click, or the tree verbs, open it.
                if (_knownCategoryNodes.Add(skeletonKey))
                    _collapsedNodes.Add(skeletonKey);
                bool skeletonExpanded =
                    filtering || !_collapsedNodes.Contains(skeletonKey);
                bool skeletonLast = !auxFollows;
                var abdomen = ResolveCharacterRootBone(skeleton!.Bones);
                var allBoneIds = new BoneId[byName.Count];
                int i = 0;
                foreach (var (bone, _) in byName.Values)
                    allBoneIds[i++] = bone.Id;
                section.Rows.Add(new ShellSidebarRow
                {
                    Label = "Skeleton",
                    Count = "",
                    Icon = TablerIcon.Walk,
                    ForceIcon = true,
                    Depth = depth + 1,
                    HasChildren = true,
                    Expanded = skeletonExpanded,
                    IsLastChild = skeletonLast,
                    TreeLines = childLines,
                    Active = abdomen != null
                        && _selection.IsSelected(
                            SelectionId.ForBone(abdomen.Id)),
                    Tag = abdomen is { } rootBone
                        ? SelectionId.ForBone(rootBone.Id)
                        : null,
                    ExpandKey = skeletonKey,
                    OverlayMemoryKey = skeletonKey,
                    OverlayBones = allBoneIds,
                });
                if (skeletonExpanded)
                {
                    var categoryLines = Descend(childLines, skeletonLast);
                    for (int g = 0; g < built.Count; g++)
                        EmitKtisisCategory(
                            section, built[g], skeletonKey, depth + 2,
                            categoryLines,
                            g == built.Count - 1, filtering);
                }
            }
        }

        if (!filtering || hasMatchingAux)
            AddAuxiliarySlotGroups(
                section, actorKey, auxSkeletons, filter, filtering,
                depth + 1, childLines);
    }

    /// <summary>Every category label, flattened once, for the filter
    /// oracle: a query naming any category keeps the actor visible.</summary>
    private static string[]? _ktisisLabels;

    private static bool KtisisCategoryLabelMatches(string filter)
    {
        if (_ktisisLabels == null)
        {
            var labels = new List<string>();
            void Walk(Core.BoneInfo.KtisisBoneCategory category)
            {
                labels.Add(category.Label);
                foreach (var child in category.Children)
                    Walk(child);
            }
            foreach (var root in Core.BoneInfo.KtisisBoneCategories.Roots)
                Walk(root);
            _ktisisLabels = labels.ToArray();
        }
        foreach (var label in _ktisisLabels)
            if (MatchesSidebarFilter(filter, label))
                return true;
        return false;
    }

    /// <summary>One category, pruned to what the skeleton carries and
    /// what the filter keeps: its own present bones (all of them when the
    /// category label matched, the matching ones otherwise) and its surviving
    /// children. Null when nothing below survives.</summary>
    private sealed record BuiltCategory(
        string Id,
        string Label,
        List<BoneDescriptor> VisibleBones,
        List<BoneDescriptor> AllBones,
        List<BuiltCategory> Children);

    private BuiltCategory? BuildKtisisCategory(
        Core.BoneInfo.KtisisBoneCategory category,
        Dictionary<string, (BoneDescriptor Bone, int Ordinal)> byName,
        HashSet<string> claimed,
        string filter,
        bool filtering)
    {
        var claimedHere = new List<(BoneDescriptor Bone, int Ordinal)>();
        foreach (var name in category.Bones)
            if (byName.TryGetValue(name, out var entry) && claimed.Add(name))
                claimedHere.Add(entry);
        claimedHere.Sort(static (a, b) => a.Ordinal - b.Ordinal);
        var all = new List<BoneDescriptor>(claimedHere.Count);
        foreach (var (bone, _) in claimedHere)
            all.Add(bone);

        bool categoryMatches = filtering
            && MatchesSidebarFilter(filter, category.Label, category.Id);
        var visible = !filtering || categoryMatches
            ? all
            : all.FindAll(bone => MatchesSidebarFilter(
                filter, bone.DisplayName, bone.Id.CanonicalName));

        var children = new List<BuiltCategory>();
        foreach (var child in category.Children)
            if (BuildKtisisCategory(child, byName, claimed, filter, filtering)
                is { } present)
                children.Add(present);

        // A pruned node: nothing of this skeleton lives here, or the filter
        // kept none of it.
        if (children.Count == 0
            && (filtering ? visible.Count == 0 : all.Count == 0))
            return null;
        var built = new BuiltCategory(
            category.Id, category.Label, visible, all, children);
        RehomeWrist(built);
        return built;
    }

    /// <summary>All bone ids under a built category, for the row's overlay
    /// eye.</summary>
    private static void CollectCategoryBones(
        BuiltCategory category, List<BoneId> into)
    {
        foreach (var bone in category.AllBones)
            into.Add(bone.Id);
        foreach (var child in category.Children)
            CollectCategoryBones(child, into);
    }

    /// <summary>Returns the root bone name for a category.</summary>
    private static string? CategoryRootBone(string categoryId) => categoryId switch
    {
        "Head" => "j_kao",
        "Spine" => "j_kosi",
        "LeftArm" => "j_ude_a_l",
        "RightArm" => "j_ude_a_r",
        "LeftHand" => "j_te_l",
        "RightHand" => "j_te_r",
        "LeftLeg" => "j_asi_a_l",
        "RightLeg" => "j_asi_a_r",
        "Tail" => "n_sippo_a",
        "Hair" => "j_kami_a",
        "LeftEye" => "j_f_eye_l",
        "RightEye" => "j_f_eye_r",
        "Mouth" => "j_ago",
        _ => null,
    };

    internal static BoneDescriptor? ResolveCategoryBone(
        string categoryId,
        IReadOnlyList<BoneDescriptor> bones)
    {
        var rootName = CategoryRootBone(categoryId);
        return rootName == null
            ? null
            : bones.FirstOrDefault(
                bone => string.Equals(
                    bone.Id.CanonicalName, rootName,
                    StringComparison.Ordinal));
    }

    internal static BoneDescriptor? ResolveCharacterRootBone(
        IReadOnlyList<BoneDescriptor> bones) =>
        bones.FirstOrDefault(
            bone => string.Equals(
                bone.Id.CanonicalName, "n_hara",
                StringComparison.Ordinal));

    internal static BoneId[] NonOverlappingBoneTargets(
        IReadOnlyList<BoneDescriptor> candidates)
    {
        var parents = candidates.ToDictionary(
            bone => bone.Id, bone => bone.Parent);
        var selected = candidates.Select(bone => bone.Id).ToHashSet();
        return candidates
            .Where(bone =>
            {
                var parent = bone.Parent;
                while (parent is { } ancestor)
                {
                    if (selected.Contains(ancestor))
                        return false;
                    if (!parents.TryGetValue(ancestor, out parent))
                        break;
                }
                return true;
            })
            .Select(bone => bone.Id)
            .Distinct()
            .ToArray();
    }

    private static void RehomeWrist(BuiltCategory category)
    {
        var wristName = category.Id switch
        {
            "LeftArm" => "n_hte_l",
            "RightArm" => "n_hte_r",
            _ => null,
        };
        if (wristName == null)
            return;

        var hand = category.Children.Find(child =>
            child.Id is "LeftHand" or "RightHand");
        if (hand == null)
            return;

        MoveWrist(hand.AllBones, category.AllBones, wristName);
        MoveWrist(hand.VisibleBones, category.VisibleBones, wristName);
    }

    private static void MoveWrist(
        List<BoneDescriptor> from,
        List<BoneDescriptor> to,
        string wristName)
    {
        var wrist = from.Find(bone => string.Equals(
            bone.Id.CanonicalName, wristName, StringComparison.Ordinal));
        if (wrist == null)
            return;
        from.Remove(wrist);
        to.Add(wrist);
    }

    private static BoneId[] ResolveGroupSelectionBones(BuiltCategory category)
    {
        var candidates = new List<BoneDescriptor>();
        void Collect(BuiltCategory current)
        {
            candidates.AddRange(current.AllBones);
            foreach (var child in current.Children)
                Collect(child);
        }
        Collect(category);
        return NonOverlappingBoneTargets(candidates);
    }

    /// <summary>Removes the redundant prefix from an IVCS bone label.</summary>
    private static string PruneIvcsLead(string label) =>
        label.StartsWith("IVCS ", StringComparison.Ordinal)
            ? label["IVCS ".Length..]
            : label;

    private void EmitKtisisCategory(
        ShellSidebarSection section,
        BuiltCategory category,
        string parentKey,
        int depth,
        bool[]? lines,
        bool isLast,
        bool filtering,
        bool underIvcs = false)
    {
        // A child category under an IVCS ancestor drops its own "IVCS" too:
        // "Genitals IVCS > Penis", not "> Penis IVCS".
        string categoryLabel = underIvcs
            ? category.Label
                .Replace(" IVCS", "", StringComparison.Ordinal)
                .Replace("IVCS ", "", StringComparison.Ordinal)
            : category.Label;
        underIvcs = underIvcs
            || category.Label.Contains("IVCS", StringComparison.Ordinal);
        var catKey = parentKey + "/kcat:" + category.Id;
        if (_knownCategoryNodes.Add(catKey))
            _collapsedNodes.Add(catKey);
        bool expanded = filtering || !_collapsedNodes.Contains(catKey);
        var overlayBones = new List<BoneId>();
        CollectCategoryBones(category, overlayBones);

        var mergedBone = ResolveCategoryBone(
            category.Id, category.AllBones);
        var selectionIds = mergedBone == null
            ? ResolveGroupSelectionBones(category)
            : [];
        section.Rows.Add(new ShellSidebarRow
        {
            Label = categoryLabel,
            Count = "",
            Depth = depth,
            HasChildren = true,
            Expanded = expanded,
            IsLastChild = isLast,
            TreeLines = lines,
            Active = mergedBone != null
                ? _selection.IsSelected(SelectionId.ForBone(mergedBone.Id))
                : selectionIds.Any(id =>
                    _selection.IsSelected(SelectionId.ForBone(id))),
            Tag = mergedBone != null
                ? SelectionId.ForBone(mergedBone.Id)
                : selectionIds.Length > 0
                    ? SelectionId.ForBoneGroup(
                        selectionIds[0].Skeleton.Actor, category.Id)
                    : catKey,
            ExpandKey = catKey,
            OverlayMemoryKey = catKey,
            SelectionBones = mergedBone == null && selectionIds.Length > 0
                ? selectionIds
                : null,
            OverlayBones = overlayBones.ToArray(),
        });
        if (!expanded)
            return;

        var childLines = Descend(lines ?? [], isLast);
        var bones = mergedBone == null
            ? category.VisibleBones
            : category.VisibleBones.FindAll(
                bone => !bone.Id.Equals(mergedBone.Id));

        // Preserve category ordering from the pose builder.
        // bones (SkeletonNode.OrderByPriority), and bones bind flat in
        // skeleton index order (BindBones: SortPriority = base + BoneIndex).
        for (int c = 0; c < category.Children.Count; c++)
            EmitKtisisCategory(
                section, category.Children[c], catKey, depth + 1, childLines,
                c == category.Children.Count - 1 && bones.Count == 0,
                filtering, underIvcs);

        for (int b = 0; b < bones.Count; b++)
        {
            var boneSelectionId = SelectionId.ForBone(bones[b].Id);
            section.Rows.Add(new ShellSidebarRow
            {
                Label = underIvcs
                    ? PruneIvcsLead(bones[b].DisplayName)
                    : bones[b].DisplayName,
                Count = "",
                Depth = depth + 1,
                IsLastChild = b == bones.Count - 1,
                TreeLines = childLines,
                Active = _selection.IsSelected(boneSelectionId),
                Tag = boneSelectionId,
                OverlayBones = new[] { bones[b].Id },
            });
        }
    }

    /// <summary>Whether an actor, any of its bones or slots, or any actor
    /// attached to it satisfies the sidebar filter.</summary>
    private bool ActorSubtreeMatches(
        ActorDescriptor actor,
        IReadOnlyList<ActorDescriptor> snapshot,
        string filter)
    {
        if (MatchesSidebarFilter(filter, ActorDisplayName(actor), actor.Name))
            return true;

        foreach (var skeleton in actor.Skeletons)
        {
            bool character = skeleton.Id.Slot == Domain.Identity.PoseSlot.Character;
            if (!character && MatchesSidebarFilter(filter, SlotLabel(skeleton.Id.Slot)))
                return true;
            foreach (var bone in skeleton.Bones)
            {
                if (bone.IsHidden || IsBoneSuppressed(bone)) continue;
                if (MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName))
                    return true;
                if (!character) continue;
                var cat = Core.BoneInfo.BoneInfoService.GetCategory(bone.Id.CanonicalName);
                if (MatchesSidebarFilter(
                        filter,
                        Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(cat),
                        cat.ToString()))
                    return true;
            }
        }

        foreach (var candidate in snapshot)
        {
            if (IsOwnedBy(candidate, actor)
                && ActorSubtreeMatches(candidate, snapshot, filter))
                return true;
        }
        return false;
    }

    private static string SlotLabel(Domain.Identity.PoseSlot slot) => slot switch
    {
        Domain.Identity.PoseSlot.MainHand => "Main Hand",
        Domain.Identity.PoseSlot.OffHand => "Off Hand",
        Domain.Identity.PoseSlot.Prop => "Prop",
        Domain.Identity.PoseSlot.Ornament => "Ornament",
        _ => slot.ToString(),
    };

    /// <summary>
    /// One collapsed group per present auxiliary slot showing that slot's
    /// real parent/child bone hierarchy. Group rows are navigation-only;
    /// bone rows carry exact slot-qualified stable ids, and a filtered view
    /// lists matching bones flat without persisting disclosure.
    /// </summary>
    private void AddAuxiliarySlotGroups(
        ShellSidebarSection section,
        string actorKey,
        List<SkeletonDescriptor> auxSkeletons,
        string filter,
        bool filtering,
        int depth,
        bool[] lines)
    {
        var shown = new List<(SkeletonDescriptor Aux, List<BoneDescriptor> Visible, List<BoneDescriptor> Matching, bool GroupMatches)>();
        foreach (var aux in auxSkeletons)
        {
            var visible = aux.Bones
                .Where(bone => !bone.IsHidden && !IsBoneSuppressed(bone))
                .ToList();
            if (visible.Count == 0)
                continue;
            bool groupMatches = MatchesSidebarFilter(filter, SlotLabel(aux.Id.Slot));
            var matching = filtering && !groupMatches
                ? visible.FindAll(bone => MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName))
                : visible;
            if (filtering && !groupMatches && matching.Count == 0)
                continue;
            shown.Add((aux, visible, matching, groupMatches));
        }

        for (int a = 0; a < shown.Count; a++)
        {
            var (aux, visible, matching, groupMatches) = shown[a];
            string slotLabel = SlotLabel(aux.Id.Slot);
            var slotKey = actorKey + "/slot:" + aux.Id.Slot;
            if (_knownCategoryNodes.Add(slotKey))
                _collapsedNodes.Add(slotKey);
            bool slotExpanded = filtering || !_collapsedNodes.Contains(slotKey);
            bool groupLast = a == shown.Count - 1;
            section.Rows.Add(new ShellSidebarRow
            {
                Label = slotLabel,
                Count = "",
                Depth = depth,
                HasChildren = true,
                Expanded = slotExpanded,
                IsLastChild = groupLast,
                TreeLines = lines,
                ExpandKey = slotKey,
                OverlayMemoryKey = slotKey,
                OverlayBones = visible.Select(bone => bone.Id).ToArray(),
            });
            if (!slotExpanded)
                continue;

            var slotLines = Descend(lines, groupLast);
            if (filtering && !groupMatches)
            {
                // Temporary filtered reveal: matching bones flat.
                for (int b = 0; b < matching.Count; b++)
                    section.Rows.Add(BoneRow(
                        matching[b], depth + 1, b == matching.Count - 1,
                        slotLines, hasChildren: false,
                        expanded: false, expandKey: null));
                continue;
            }

            // Real hierarchy: children map from slot-qualified parent ids;
            // parent traversal never leaves this slot's descriptor set.
            var inSlot = visible.ToDictionary(bone => bone.Id);
            var children = new Dictionary<BoneId, List<BoneDescriptor>>();
            var roots = new List<BoneDescriptor>();
            foreach (var bone in visible)
            {
                if (bone.Parent is { } parent && inSlot.ContainsKey(parent))
                {
                    if (!children.TryGetValue(parent, out var list))
                        children[parent] = list = new List<BoneDescriptor>();
                    list.Add(bone);
                }
                else
                {
                    roots.Add(bone);
                }
            }

            void Emit(BoneDescriptor bone, int boneDepth, bool isLast, bool[] boneLines)
            {
                bool hasKids = children.ContainsKey(bone.Id);
                var boneKey = slotKey + "/bone:" + bone.Id.PartialId + ":" + bone.Id.BoneIndex;
                // Every disclosure seeds collapsed, hierarchy nodes included.
                if (hasKids && _knownCategoryNodes.Add(boneKey))
                    _collapsedNodes.Add(boneKey);
                bool boneExpanded = !_collapsedNodes.Contains(boneKey);
                section.Rows.Add(BoneRow(
                    bone, boneDepth, isLast, boneLines,
                    hasKids, boneExpanded, hasKids ? boneKey : null));
                if (!hasKids || !boneExpanded)
                    return;
                var kids = children[bone.Id];
                var kidLines = Descend(boneLines, isLast);
                for (int k = 0; k < kids.Count; k++)
                    Emit(kids[k], boneDepth + 1, k == kids.Count - 1, kidLines);
            }

            for (int r = 0; r < roots.Count; r++)
                Emit(roots[r], depth + 1, r == roots.Count - 1, slotLines);
        }
    }

    private ShellSidebarRow BoneRow(
        BoneDescriptor bone,
        int depth,
        bool isLast,
        bool[] lines,
        bool hasChildren,
        bool expanded,
        string? expandKey)
    {
        var selectionId = SelectionId.ForBone(bone.Id);
        return new ShellSidebarRow
        {
            Label = bone.DisplayName,
            Count = "",
            Depth = depth,
            HasChildren = hasChildren,
            Expanded = expanded,
            IsLastChild = isLast,
            TreeLines = lines,
            Active = _selection.IsSelected(selectionId),
            Tag = selectionId,
            ExpandKey = expandKey,
            OverlayMemoryKey = "bone:" + bone.Id,
            OverlayBones = new[] { bone.Id },
        };
    }

    private static bool MatchesSidebarFilter(string filter, params string?[] values)
    {
        if (filter.Length == 0) return true;
        foreach (var value in values)
            if (!string.IsNullOrEmpty(value) && value.Contains(filter, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>Extended/IVCS bones are display-suppressed while
    /// Display.ShowNsfwBones is off. Read live per build: the snapshot's own
    /// IsHidden and every selection path are untouched.</summary>
    private static bool IsBoneSuppressed(BoneDescriptor bone)
        => !Config.ConfigurationService.Instance.Config.Display.ShowNsfwBones
            && Core.BoneInfo.BoneInfoService.IsNsfw(bone.Id.CanonicalName);

    /// <summary>
    /// The <c>_l</c>/<c>_r</c> counterpart the sibling-link mode co-selects,
    /// or null when the mode is off or the bone has none. Resolution never
    /// leaves the bone's own skeleton or partial: a name alone matches across
    /// slots, and pairing a character hand with a weapon bone of the same
    /// name would be a different bone entirely.
    /// </summary>
    private SelectionId? ResolveSiblingBone(SelectionId id)
    {
        if (!Config.ConfigurationService.Instance.Config.LinkSiblingBones ||
            id is not { Kind: SceneEntityKind.Bone, Bone: { } bone })
            return null;

        string name = bone.CanonicalName;
        string partner =
            name.EndsWith("_l", StringComparison.Ordinal)
                ? string.Concat(name.AsSpan(0, name.Length - 2), "_r")
                : name.EndsWith("_r", StringComparison.Ordinal)
                    ? string.Concat(name.AsSpan(0, name.Length - 2), "_l")
                    : string.Empty;
        if (partner.Length == 0)
            return null;

        foreach (var actor in _scene.Snapshot.Actors)
        {
            if (actor.Id.LogicalId != bone.Skeleton.Actor.LogicalId)
                continue;
            foreach (var skeleton in actor.Skeletons)
            {
                if (skeleton.Id != bone.Skeleton)
                    continue;
                foreach (var candidate in skeleton.Bones)
                {
                    if (candidate.Id.PartialId == bone.PartialId &&
                        string.Equals(
                            candidate.Id.CanonicalName,
                            partner,
                            StringComparison.Ordinal))
                        return SelectionId.ForBone(candidate.Id);
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>Nickname, else the anonymous mask when enabled, else the
    /// cleaned snapshot name — one stable-id display API for every surface,
    /// the pop-out windows included.</summary>
    internal static string ActorDisplayName(ActorDescriptor actor)
        => Config.ConfigurationService.Instance.GetDisplayName(
            actor.Id.LogicalId, DisplayName(actor.Name));

    /// <summary>Strips the raw object-index suffix ("Name (201)") for display.</summary>
    internal static string DisplayName(string name)
        => System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");

    private Action? _openLibrary;

    private void BuildTabs(SelectionId? primary)
    {
        // Tabs are rebuilt each frame; the active one is preserved so a
        // selection change cannot silently return to Pose.
        _vm.Tabs.Clear();
        int contentMode = _contentMode;
        if (contentMode == 1)
        {
            // The environment is big enough to earn its strip: five
            // pages, exactly the split it had as a selection.
            _activeStrip = "environment";
            bool held = false;
            for (int i = 0; i < _environmentTabs.Length; i++)
                held |= _environmentTabs[i].Label == _activeTab;
            if (!held)
                _activeTab = "Lighting";
            for (int i = 0; i < _environmentTabs.Length; i++)
            {
                _environmentTabs[i].Active =
                    _environmentTabs[i].Label == _activeTab;
                _vm.Tabs.Add(_environmentTabs[i]);
            }
            return;
        }
        if (contentMode == 2)
        {
            // The scene page is one page: no tabs, the selector's own
            // Scene segment is its identity.
            _activeStrip = "scene";
            return;
        }
        var tabs = SyncStripAndTab(primary);
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i].Active = tabs[i].Label == _activeTab;
            _vm.Tabs.Add(tabs[i]);
        }
    }

    /// <summary>
    /// Resolves the strip a selection answers for and settles
    /// <see cref="_activeStrip"/> and <see cref="_activeTab"/> onto it,
    /// returning that strip's tabs. Separated from <see cref="BuildTabs"/>
    /// because a selection can also change mid-frame, from a sidebar row the
    /// shell is already drawing, and the viewport contract has to move with
    /// it (see <see cref="ResyncTabLayout"/>).
    /// </summary>
    private ShellTab[] SyncStripAndTab(SelectionId? primary)
    {
        // The ANONYMOUS GROUP first: two or more entities together answer
        // with ONE Selection page, whatever their kinds — the multiselect
        // is a group that was never created.
        if (global::Poser.Application.Selection.EntitySelection.IsMultiEntity(
                _selection.Selected))
        {
            _activeStrip = "multi";
            _activeTab = "Selection";
            return _multiselectTabs;
        }
        // NOTHING selected: no strip and no tabs — the content side says
        // so instead of showing an ownerless actor page.
        if (primary == null)
        {
            _activeStrip = "none";
            _activeTab = string.Empty;
            return [];
        }
        // The strip is a function of the selection type: the environment's
        // tabs are its own, a light's are its own, and nothing else shares
        // either — neither entity has a pose, an animation or an appearance.
        var (tabs, strip) = primary switch
        {
            { Kind: SceneEntityKind.Light } => (_lightTabs, "light"),
            { Kind: SceneEntityKind.Camera } => (_cameraTabs, "camera"),
            { Kind: SceneEntityKind.Prop } => (_propTabs, "prop"),
            { Kind: SceneEntityKind.Overlay } => (_overlayTabs, "overlay"),
            { Kind: SceneEntityKind.WorldObject } =>
                (_worldObjectTabs, "world-object"),
            // Creatures share the actor strip: their skeleton poses, their
            // battle-chara body animates, and the Appearance pane hides the
            // humanoid-only sections itself.
            _ => (_selectionTabs, "actor"),
        };
        // Same-labeled tabs on different strips are different places: the
        // strip key joins the scroll identity in ApplyTabLayout.
        _activeStrip = strip;
        // The active tab is preserved within a strip, so a selection change
        // inside the actor set cannot silently return to Pose; a
        // strip that does not carry it falls to that strip's first tab.
        bool carried = false;
        for (int i = 0; i < tabs.Length; i++)
            carried |= tabs[i].Label == _activeTab;
        if (!carried)
            _activeTab = tabs[0].Label;
        return tabs;
    }

    /// <summary>
    /// Rebuilds the tab and viewport layout after a mid-frame selection change.
    /// The second build keeps the active selection and tab contract coherent.
    /// </summary>
    private void ResyncTabLayout()
    {
        // Rebuild the tab rows and viewport contract together.
        BuildTabs(_selection.Primary);
        ApplyTabLayout(_contentMode
            switch { 1 => _activeTab, 2 => "Scene", _ => _activeTab });
    }

    /// <summary>The two mode strips. A mode is a strip like an entity type is
    /// — it has its own tabs — so it owns its own scroll identity: entering
    /// the library from an actor and from a light must land on one library,
    /// not on two with separate scroll memories.</summary>

    // ── status bar, restated only when its numbers move ─────────────────
    private int _statusActorCount = -1;
    private int _statusBones;
    private int _statusFps = -1;
    private ulong _statusRevision;
    private bool _statusPrimed;
    private ActorId? _statusBoneActor;

    private void BuildStatus(SelectionId? primary)
    {
        int actorCount = _scene.Snapshot.Actors.Count;
        if (actorCount != _statusActorCount)
        {
            _statusActorCount = actorCount;
            _vm.StatusLeft = actorCount == 1 ? "1 actor" : $"{actorCount} actors";
        }

        ActorId? statusActor = primary switch
        {
            { Kind: SceneEntityKind.Bone, Bone: { } bone } => bone.Skeleton.Actor,
            { Kind: SceneEntityKind.Actor, Actor: { } actorId } => actorId,
            // A gaze anchor counts as its owning actor, exactly like a bone.
            { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeOwner } => gazeOwner,
            _ => null,
        };
        // The bone total moves only with the scene's structure or with which
        // actor is selected — never with the frame.
        if (!_statusPrimed ||
            _statusRevision != _scene.Revision ||
            _statusBoneActor != statusActor)
        {
            _statusPrimed = true;
            _statusRevision = _scene.Revision;
            _statusBoneActor = statusActor;
            int bones = 0;
            if (statusActor is { } owner && FindActor(owner.LogicalId) is { } descriptor)
                foreach (var skeleton in descriptor.Skeletons)
                    bones += skeleton.Bones.Count;
            _statusBones = bones;
            // Restate the right-hand string with the new count.
            _statusFps = -1;
        }

        int fps = (int)MathF.Round(
            ImGui.GetIO().Framerate, MidpointRounding.AwayFromZero);
        if (fps == _statusFps)
            return;
        _statusFps = fps;
        _vm.StatusRight = _statusBones > 0
            ? $"{_statusBones} bones · {fps} fps"
            : $"{fps} fps";
    }

    private ActorDescriptor? FindActor(Guid lineage)
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.LogicalId == lineage)
                return actor;
        return null;
    }

    /// <summary>Catalog spawns carry their spawn kind's icon; slot
    /// companions keep the paw; everything else is a person.</summary>
    private TablerIcon SidebarActorIcon(ActorDescriptor actor)
    {
        var resolved = _bindings.Resolve(actor.Id);
        var kind = resolved.Success && resolved.Value is { } live
            ? _spawnService.GetSpawnedKind(live)
            : null;
        return kind switch
        {
            CompanionKind.Companion => TablerIcon.Paw,
            CompanionKind.Mount => TablerIcon.Horse,
            CompanionKind.Ornament => TablerIcon.Diamond,
            _ => actor.IsCompanion ? TablerIcon.Paw : TablerIcon.User,
        };
    }

    // ── shell callbacks ──────────────────────────────────────────────────

    /// <summary>
    /// Steps the tab strip by <paramref name="delta"/>, wrapping. It goes
    /// through the click path rather than moving <see cref="_activeTab"/>
    /// itself: the click is what also settles the viewport contract, and a
    /// keyboard step that skipped it would render one tab through another
    /// tab's layout for a frame. Whatever the strip currently holds is what
    /// steps — the library's types in library mode, the selection's tabs
    /// otherwise.
    /// </summary>
    public void CycleTab(int delta)
    {
        int count = _vm.Tabs.Count;
        if (count == 0)
            return;
        int active = 0;
        for (int i = 0; i < count; i++)
        {
            if (!_vm.Tabs[i].Active)
                continue;
            active = i;
            break;
        }
        OnTabClicked(((active + delta) % count + count) % count);
    }

    private void OnTabClicked(int index)
    {
        if (index < 0 || index >= _vm.Tabs.Count) return;
        var label = _vm.Tabs[index].Label;

        _activeTab = label;
        for (int i = 0; i < _vm.Tabs.Count; i++)
            _vm.Tabs[i].Active = i == index;

        // The click occurs while AppShellView is already drawing. Update the
        // viewport contract in the same callback as the content selection so
        // the remainder of this frame cannot render one tab through another
        // tab's layout path.
        ApplyTabLayout(label);
    }

    /// <summary>The (strip, tab) pair whose scroll identity
    /// <see cref="AppShellViewModel.ContentScrollId"/> currently carries; the
    /// id string is minted only when the pair moves.</summary>
    private string _scrollIdStrip = "";
    private string _scrollIdTab = "";

    private void ApplyTabLayout(string tab)
    {
        // Scroll identity is per strip and tab: one shared id
        // would carry the previous tab's scroll offset and extent into the
        // next tab's first frame, and strips reuse labels ("Light" on the
        // light strip vs the environment strip), so the label alone would
        // still share scroll memory across strips. Minted on switch only —
        // this method also runs on the warm per-frame path.
        if (!string.Equals(_scrollIdTab, tab, StringComparison.Ordinal) ||
            !string.Equals(_scrollIdStrip, _activeStrip, StringComparison.Ordinal))
        {
            _scrollIdStrip = _activeStrip;
            _scrollIdTab = tab;
            _vm.ContentScrollId =
                AppShellViewModel.ContentScrollIdFor(_activeStrip, tab);
        }
        // The library paints its own bands and rules, so it takes the
        // viewport wall to wall; Pose keeps the shell-inset fixed viewport.
        _vm.ContentFlush = tab is "Library";
        _vm.ContentOwnsViewport = tab is "Pose";
        // Every environment tab is a PageForm, as the one it replaced was.
        // "Light" is deliberately shared: it is a light's whole editor and the
        // environment's lighting tab, and both are pages, so the layout answer
        // is the same either way. Which pane draws it is decided by the
        // selection in DrawTabContent, never by this label.
        // The scene workspace is a Page like the rest of them; it was the one
        // page missing from this list, so the shell was insetting it a second
        // time on top of the Page's own.
        _vm.ContentUsesPage =
            tab is "Animation" or "Appearance" or "Object" or "Light"
                or "Environment" or "Scene" or "Selection"
                or "Lighting" or "Sky" or "Atmosphere" or "World"
                or "Camera"
                or "Scene"
;
    }

    /// <summary>
    /// One row click, then one layout resync — after every change the click
    /// makes, never between them. The strip a frame draws is a function of
    /// the selection, so the mode exits below must not restate the layout on
    /// their way out: they would resolve it against the outgoing selection
    /// (the library clears it entirely) and settle the active tab onto that
    /// strip's first tab, losing the prior tab before entering the
    /// mode — the promise <see cref="BuildTabs"/> makes for the library.
    /// </summary>
    private void OnRowClicked(ShellSidebarRow row)
    {
        ApplyRowClick(row);
        // The row was clicked while the shell is already drawing, and the tab
        // strip is a function of the selection's type: without this the rest
        // of the frame renders the incoming pane through the outgoing strip's
        // viewport contract, and draws the outgoing strip's labels with it.
        ResyncTabLayout();
    }

    private void ApplyRowClick(ShellSidebarRow row)
    {
        // A reference picture is not in the scene, so there is nothing to
        // select: the row's body raises its window instead, and shows it first
        // if the eye had set it aside — a click that focuses something the
        // hidden window would read as a no-op.
        if (row.Tag is ReferenceImageInstance clickedImage)
        {
            _referenceImages.SetHidden(clickedImage, false);
            row.LightOn = true;
            ImGui.SetWindowFocus(
                ReferenceImageWindow.WindowNameFor(clickedImage));
            return;
        }

        // Touching anything in the scene tree is leaving the library or the
        // scene workspace: they are alternatives in one workspace. A selecting
        // click leaves through the selection itself; a bare category
        // disclosure selects nothing, so the tree still states it here.
        _workspace.Leave();
        // A group row selects its whole MEMBERSHIP — the anonymous-group
        // machinery does the rest. Ctrl adds the members instead.
        if (row.Tag is GroupRowTag groupTag)
        {
            if (_groups.Find(groupTag.Id) is not { } group)
                return;
            var everything = new List<SelectionId>(_groups.Descendants(group));
            if (everything.Count == 0)
                return;
            var io2 = ImGui.GetIO();
            // A group row's members live one level down; they join a
            // multi-selection only when it already sits at that level.
            if (io2.KeyCtrl && SelectionParentIs(group.Id))
            {
                foreach (var member in everything)
                    _selection.Add(member);
            }
            else
            {
                _selection.Select(everything[0]);
                for (int i = 1; i < everything.Count; i++)
                    _selection.Add(everything[i]);
                // The HEAD click alone makes the selection "the group" —
                // hand-selecting every member stays a member selection.
                _groups.ActiveGroupId = group.Id;
            }
            return;
        }
        if (row.Tag is not SelectionId id) return;

        var io = ImGui.GetIO();
        if (row.SelectionBones is { Count: > 0 }
            && id.Kind == SceneEntityKind.Bone
            && id.Bone is null)
        {
            if (io.KeyCtrl)
            {
                foreach (var bone in row.SelectionBones)
                    _selection.Toggle(SelectionId.ForBone(bone));
            }
            else
            {
                _selection.Select(SelectionId.ForBone(row.SelectionBones[0]));
                for (int i = 1; i < row.SelectionBones.Count; i++)
                    _selection.Add(SelectionId.ForBone(row.SelectionBones[i]));
            }
            return;
        }
        // Multi-selection keeps ONE parent — the anchor's: root things
        // with root things, a group's members with each other. A shift or
        // ctrl click on another level starts over there.
        Guid? clickedParent = _groups.GroupOf(id)?.Id;
        if (io.KeyShift && _selection.Anchor is { } anchor
            && SelectionParentIs(clickedParent))
        {
            var displayOrder = new List<SelectionId>();
            foreach (var section in _vm.Sections)
                foreach (var visibleRow in section.Rows)
                    if (visibleRow.Tag is SelectionId visibleId
                        && _groups.GroupOf(visibleId)?.Id == clickedParent)
                        displayOrder.Add(visibleId);
            _selection.SelectRange(anchor, id, displayOrder);
        }
        else if (io.KeyCtrl && SelectionParentIs(clickedParent))
        {
            _selection.Toggle(id);
        }
        else
        {
            _selection.Select(id);
        }
    }

    // ── the multiselect page: the anonymous group ────────────────────────

    /// <summary>Per-kind counts, minted only when they change — a warm
    /// frame restates the same strings.</summary>
    private readonly int[] _multiCounts = new int[5];
    private readonly string[] _multiCountText = new string[5];
    private static readonly string[] MultiKindLabels =
        ["Actors", "Objects", "Lights", "Cameras", "Overlays"];

    private void DrawMultiselectPage(Vector2 origin, Vector2 size)
    {
        Span<int> counts = stackalloc int[5];
        foreach (var id in _selection.Selected)
        {
            int slot = id.Kind switch
            {
                SceneEntityKind.Actor => 0,
                SceneEntityKind.Prop or SceneEntityKind.WorldObject => 1,
                SceneEntityKind.Light => 2,
                SceneEntityKind.Camera => 3,
                SceneEntityKind.Overlay => 4,
                _ => -1,
            };
            if (slot >= 0)
                counts[slot]++;
        }
        for (int i = 0; i < 5; i++)
        {
            if (_multiCounts[i] == counts[i] && _multiCountText[i] != null)
                continue;
            _multiCounts[i] = counts[i];
            _multiCountText[i] = counts[i].ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        var matched = _groups.ActiveSelection(_selection.Selected);
        Crystarium.Page("multiselect-page", origin, size, page =>
        {
            // The title is STABLE: the group's name lives in the field
            // below, never in the section header — a header that renamed
            // with each keystroke changed the field's identity and threw
            // the keyboard back to the game after one character.
            page.Section(matched != null ? "Group" : "Selection", form =>
            {
                if (matched is { } named)
                    form.TextInput("Name", named.Name,
                        value => _groups.Rename(named.Id, value));
                for (int i = 0; i < 5; i++)
                    if (_multiCounts[i] > 0)
                        form.ReadOnly(MultiKindLabels[i], _multiCountText[i]);
                form.Actions(string.Empty, actions =>
                {
                    if (matched is { } group)
                    {
                        actions.Button("Save to library",
                            () => OpenEntityRename(
                                "Save group to library", group.Name,
                                name => _scenePane.SaveGroupEntry(
                                    group.Members, name, AllActorsOwned(group.Members))));
                        actions.Button("Ungroup",
                            () => DissolveGroup(group.Id));
                    }
                    else
                    {
                        actions.Button("Group…",
                            () => OpenEntityRename(
                                "Name the group",
                                $"Group {_groups.All.Count + 1}",
                                name => _groups.Create(
                                    name, _selection.Selected)));
                    }
                    actions.Button("Move to camera", MoveSelectionToCamera);
                    actions.Button("Deselect", () => _selection.Clear());
                });
            }, divider: false);
        });
    }

    /// <summary>One undoable translate: the whole selection moves so its
    /// centroid lands in front of the camera, every member keeping its
    /// offset from the others.</summary>
    private void MoveSelectionToCamera()
    {
        var resolved = global::Poser.Application.Transforms.TransformTargetResolver
            .Resolve(
                _selection.Selected, _scene.Snapshot, _groups.IsLockedMember);
        if (resolved is not { } selection)
        {
            _notices.Failed("Nothing movable is selected.");
            return;
        }
        var sum = System.Numerics.Vector3.Zero;
        int counted = 0;
        foreach (var target in selection.Targets)
        {
            var pose =
                target is { Kind: TransformTargetKind.Actor, Actor: { } actor }
                    ? _viewportProjection.GetActorTransform(actor)
                    : _viewportProjection.GetModelTransform(target);
            if (pose is not { } position)
                continue;
            sum += position.Position;
            counted++;
        }
        if (counted == 0)
        {
            _notices.Failed("Nothing movable is selected.");
            return;
        }
        var centroid = sum / counted;
        var look = _gameCamera.GetLookDirection();
        if (look.LengthSquared() < 1e-6f)
            look = System.Numerics.Vector3.UnitZ;
        var goal = _gameCamera.GetCameraPosition()
            + System.Numerics.Vector3.Normalize(look) * 2.5f;
        var begin = _cleanTransforms.Begin(
            selection.Targets,
            global::Poser.Domain.Transforms.TransformOperation.Translate,
            global::Poser.Domain.Transforms.TransformSpace.World,
            description: "Move to camera");
        if (!begin.Success || begin.GestureId is not { } gestureId)
        {
            _notices.Failed(
                $"Move to camera: {begin.Detail ?? "refused"}.");
            return;
        }
        _cleanTransforms.Update(gestureId,
            new global::Poser.Domain.Transforms.TransformDelta(
                goal - centroid,
                System.Numerics.Quaternion.Identity,
                System.Numerics.Vector3.One));
        _cleanTransforms.Commit(gestureId);
    }

    /// <summary>A tree drag released. The root list is the USER'S order —
    /// any entity or group head re-seats at the caret, kinds interleaved.
    /// Group structure rides the same gesture: INTO a head joins, beside a
    /// member inserts there, open space just leaves the group. Dragging a
    /// selected row carries the whole entity selection.</summary>
    private void OnRowDropped(
        ShellSidebarRow dragged,
        ShellSidebarRow? target,
        RowDropPosition position)
    {
        int pointerLevel = _vm.DropLevel;
        (target, position) = ResolveDropLevel(target, position);
        _log.Debug(
            $"Sidebar drop: {DescribeRow(dragged)} -> {(target == null ? "nothing" : DescribeRow(target))} "
            + $"{position} at level {pointerLevel}");
        // A group head re-seats among the root slots like anything else;
        // open space is the end of the list.
        if (dragged.Tag is GroupRowTag draggedGroup)
        {
            DropGroup(draggedGroup.Id, target, position);
            return;
        }

        if (dragged.Tag is not SelectionId draggedId
            || !global::Poser.Application.Selection.EntitySelection
                .IsEntity(draggedId.Kind))
            return;
        var moved = new List<SelectionId>();
        if (_selection.IsSelected(draggedId))
        {
            foreach (var id in _selection.Selected)
                if (global::Poser.Application.Selection.EntitySelection
                        .IsEntity(id.Kind))
                    moved.Add(id);
        }
        if (moved.Count == 0)
            moved.Add(draggedId);

        // Into a group's head: append in drag order.
        if (target?.Tag is GroupRowTag intoGroup
            && position == RowDropPosition.Into)
        {
            foreach (var id in moved)
            {
                LeaveGroupOverrides(id);
                _groups.AddMember(intoGroup.Id, id);
                JoinGroupOverrides(id);
            }
            return;
        }

        // Beside a grouped member: insert at its place in that group.
        if (target?.Tag is SelectionId targetId
            && _groups.GroupOf(targetId) is { } host)
        {
            int index = host.Members.IndexOf(targetId);
            if (position == RowDropPosition.After)
                index++;
            foreach (var id in moved)
            {
                LeaveGroupOverrides(id);
                _groups.AddMember(host.Id, id, index);
                JoinGroupOverrides(id);
                index = host.Members.IndexOf(id) + 1;
            }
            return;
        }

        // Beside a nested group: the dragged rows join that group's parent.
        if (target?.Tag is GroupRowTag besideGroup
            && position is RowDropPosition.Before or RowDropPosition.After
            && _groups.Find(besideGroup.Id) is { ParentId: { } parentId })
        {
            foreach (var id in moved)
            {
                LeaveGroupOverrides(id);
                _groups.AddMember(parentId, id);
                JoinGroupOverrides(id);
            }
            return;
        }

        // A root seam: the dragged rows leave any group and re-seat at
        // the caret, in carry order.
        if (target != null
            && position is RowDropPosition.Before or RowDropPosition.After
            && RootSlotOf(target) is { } anchor)
        {
            bool after = position == RowDropPosition.After;
            foreach (var id in moved)
            {
                LeaveGroupOverrides(id);
                _groups.RemoveMember(id);
                _groups.MoveRoot(RootSlot.For(id), anchor, after);
                anchor = RootSlot.For(id);
                after = true;
            }
            return;
        }

        // Open space: the end of the root list, leaving any group — the
        // caret at the tree's tail marks exactly this.
        foreach (var id in moved)
        {
            LeaveGroupOverrides(id);
            _groups.RemoveMember(id);
            _groups.MoveRootToEnd(RootSlot.For(id));
        }
    }

    /// <summary>Folds or opens the tree: the root row alone, or the root
    /// and everything keyed beneath it.</summary>
    private void SetTreeCollapsed(string root, bool collapsed, bool subtree)
    {
        void Set(string key)
        {
            if (collapsed)
                _collapsedNodes.Add(key);
            else
                _collapsedNodes.Remove(key);
        }
        Set(root);
        if (!subtree)
            return;
        IEnumerable<string> keys = _knownActorNodes.Concat(_knownCategoryNodes);
        foreach (var key in keys.ToArray())
            if (key.StartsWith(root + "/", StringComparison.Ordinal))
                Set(key);
    }

    /// <summary>The root slot a drop row stands for: a group head or a
    /// grouped member answers its group's slot, an ungrouped entity its
    /// own. Rows with no root stake — bones, categories, reference
    /// images, attached rows — answer null and the drop is a no-op.</summary>
    private string DescribeRow(ShellSidebarRow row) => row.Tag switch
    {
        GroupRowTag tag => $"group '{row.Label}' ({tag.Id.ToString()[..8]}, depth {row.Depth})",
        SelectionId id => $"{id.Kind} '{row.Label}' (depth {row.Depth})",
        _ => $"'{row.Label}'",
    };

    /// <summary>The pointer's indent decides the level at a seam. Right of
    /// a group head's indent, "after" it means "first inside it"; left of a
    /// group's last row's indent, "after" it means "after the group" — one
    /// level out per 20px, so a drag can climb out of nested groups in one
    /// motion. Returns the row to act on and the position against it.</summary>
    private (ShellSidebarRow? Target, RowDropPosition Position) ResolveDropLevel(
        ShellSidebarRow? target, RowDropPosition position)
    {
        int level = _vm.DropLevel;
        _vm.DropLevel = -1;
        if (target == null || level < 0
            || position is not (RowDropPosition.Before or RowDropPosition.After))
            return (target, position);
        if (level > target.Depth)
        {
            if (position == RowDropPosition.After && target.Tag is GroupRowTag)
                return (target, RowDropPosition.Into);
            return (target, position);
        }
        if (level >= target.Depth || position != RowDropPosition.After)
            return (target, position);
        // Climb: the group at the pointer's level that contains this row.
        var host = HostGroupOf(target);
        var climbed = host;
        int depth = target.Depth - 1;
        while (climbed != null && depth > level)
        {
            climbed = _groups.ParentOf(climbed);
            depth--;
        }
        if (climbed == null || host == null)
            return (target, position);
        var stand = new ShellSidebarRow { Depth = depth, Tag = new GroupRowTag(climbed.Id) };
        return (stand, RowDropPosition.After);
    }

    /// <summary>A dragged group: onto a group head it nests there; beside
    /// a nested row it becomes a sibling in that row's group; beside a root
    /// row or into nothing it comes out to the root order. A nest past the
    /// depth limit is refused by name and nothing moves.</summary>
    private void DropGroup(Guid groupId, ShellSidebarRow? target, RowDropPosition position)
    {
        if (target?.Tag is GroupRowTag intoGroup && position == RowDropPosition.Into)
        {
            if (_groups.CanNest(groupId, intoGroup.Id, out var reason))
                _log.Debug($"Sidebar drop: nest -> {_groups.Nest(groupId, intoGroup.Id)}");
            else
                _notices.Failed($"Group not moved: {reason}");
            return;
        }
        // Beside a row that lives inside a group: a sibling there.
        if (target != null && position is RowDropPosition.Before or RowDropPosition.After
            && HostGroupOf(target) is { } host)
        {
            if (!_groups.CanNest(groupId, host.Id, out var reason))
            {
                _notices.Failed($"Group not moved: {reason}");
                return;
            }
            int index = target.Tag is GroupRowTag sibling ? host.Children.IndexOf(sibling.Id) : -1;
            if (index >= 0 && position == RowDropPosition.After)
                index++;
            _groups.Nest(groupId, host.Id, index);
            return;
        }
        // The root order.
        var slot = RootSlot.ForGroup(groupId);
        if (target != null && position is RowDropPosition.Before or RowDropPosition.After
            && RootSlotOf(target) is { } anchor)
        {
            if (_groups.Find(groupId) is { ParentId: not null })
                _groups.Unnest(groupId, anchor, position == RowDropPosition.After);
            else
                _groups.MoveRoot(slot, anchor, position == RowDropPosition.After);
            return;
        }
        if (_groups.Find(groupId) is { ParentId: not null })
            _groups.Unnest(groupId);
        else
            _groups.MoveRootToEnd(slot);
    }

    /// <summary>The group a row sits INSIDE: a member's group, or a nested
    /// group's parent. Null for anything at the root.</summary>
    private global::Poser.Application.Scene.SceneGroup? HostGroupOf(ShellSidebarRow row)
    {
        if (row.Tag is GroupRowTag tag)
            return _groups.Find(tag.Id) is { } group ? _groups.ParentOf(group) : null;
        if (row.Tag is SelectionId id)
            return _groups.GroupOf(id);
        return null;
    }

    private RootSlot? RootSlotOf(ShellSidebarRow row)
    {
        if (row.Tag is GroupRowTag tag)
            return _groups.Find(tag.Id) is { } group
                ? RootSlot.ForGroup(_groups.RootOf(group).Id)
                : null;
        if (row.Tag is not SelectionId id
            || !global::Poser.Application.Selection.EntitySelection
                .IsEntity(id.Kind))
            return null;
        if (_groups.GroupOf(id) is { } host)
            return RootSlot.ForGroup(_groups.RootOf(host).Id);
        return RootSlot.For(id);
    }

    /// <summary>The drag ghost's text: a dragged row that rides with the
    /// entity multiselect announces the whole cargo, not just itself.</summary>
    private string DragGhostFor(ShellSidebarRow row)
    {
        if (row.Tag is not SelectionId id
            || !global::Poser.Application.Selection.EntitySelection
                .IsEntity(id.Kind)
            || !_selection.IsSelected(id))
            return row.Label;
        int entities = global::Poser.Application.Selection.EntitySelection
            .CountEntities(_selection.Selected);
        if (entities < 2)
            return row.Label;
        if (_multiTitleCount != entities)
        {
            _multiTitleCount = entities;
            _multiTitle = $"{entities} selected";
        }
        return _multiTitle;
    }

    private IActor? ResolveActorRow(ShellSidebarRow row)
    {
        if (row.Tag is not SelectionId
            { Kind: SceneEntityKind.Actor, Actor: { } actorId })
            return null;
        var resolved = _bindings.Resolve(actorId);
        return resolved.Success ? resolved.Value : null;
    }

    // ── typed tab content hosted inside the shell ──────────────────────

    private void DrawTabContent(Vector2 origin, Vector2 size)
    {
        int pageMode = _contentMode;
        if (pageMode == 1)
        {
            _environmentPane.Draw(origin, size, EnvironmentTabFor(_activeTab));
            return;
        }
        if (pageMode == 2)
        {
            // Scene recovery is browsable out of GPose; the workflow
            // itself refuses what needs a live session.
            _scenePane.DrawPage(origin, size);
            return;
        }

        if (!_gPoseService.IsGPosing)
        {
            Crystarium.TextAt(origin + new Vector2(0f, 8f) * ImGuiHelpers.GlobalScale, "Enter GPose to start posing.", new TextStyle { Size = Crystarium.ActiveTheme.Typography.LabelSize, Color = Crystarium.ActiveTheme.FormHint });
            return;
        }

        ImGui.SetCursorScreenPos(origin);
        // Inspector-owned selection state drives IK and must be current even
        // when another tab owns the centre pane.
        _poseInspector.SetSelection(_selection.Primary);

        // The properties panel's empty state: one centred line.
        if (_selection.Primary == null
            && !global::Poser.Application.Selection.EntitySelection
                .IsMultiEntity(_selection.Selected))
        {
            var emptyStyle = new TextStyle
            {
                Size = Crystarium.ActiveTheme.Typography.LabelSize,
                Color = Crystarium.ActiveTheme.FormHint,
            };
            var measured = Crystarium.MeasureText(
                "Nothing selected", emptyStyle);
            Crystarium.TextAt(
                origin + (size - measured) * 0.5f,
                "Nothing selected", emptyStyle);
            return;
        }

        if (_activeTab == "Selection")
        {
            DrawMultiselectPage(origin, size);
            return;
        }
        if (_activeTab == "Animation")
        {
            _animationCatalog.EnsureLoaded();
            _animationPane.Draw(origin, size);
            return;
        }

        if (_activeTab == "Appearance")
        {
            _appearancePane.Draw(origin, size);
            return;
        }

        // The overlay tab stands only while an overlay is selected — the label
        // is unique across every strip, so it is the whole dispatch.
        if (_activeTab == "Overlay")
        {
            _overlayPane.Draw(origin, size);
            return;
        }

        // Both kinds of object name the same tab, because they share one
        // word for them. Which pane it opens is the selection's answer, never
        // the label's — the same rule "Light" already lives under.
        if (_activeTab == "Object")
        {
            if (_selection.Primary is { Kind: SceneEntityKind.WorldObject })
                _worldObjectsPane.Draw(origin, size);
            else
                _propsPane.Draw(origin, size);
            return;
        }

        // The three light tabs only ever stand while a light is selected: the
        // strip that carries them is chosen by the selection kind, and a strip
        // that does not carry the active label drops back to its own first tab.
        if (_activeTab == "Light")
        {
            _lightPane.DrawLight(origin, size);
            return;
        }

        // The camera tab stands only while a camera is selected — the label
        // is unique across every strip, so it is the whole dispatch, exactly
        // like the light's.
        if (_activeTab == "Camera")
        {
            _cameraPane.DrawCamera(origin, size);
            return;
        }

        _poseInspector.Draw(origin, size);
    }


    private ActorId? SelectedActorId() =>
        _selection.Primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
            { Kind: SceneEntityKind.Bone, Bone: { } bone } =>
                bone.Skeleton.Actor,
            // A gaze anchor is still the actor's; the toolbar stays live on it.
            { Kind: SceneEntityKind.GazeTarget, Actor: { } gazeOwner } => gazeOwner,
            _ => null,
        };

    private readonly Game.PropSpawnService _propService;
    private readonly PropsPane _propsPane;
    private readonly WorldObjectsPane _worldObjectsPane;
    private readonly Game.Overlays.OverlayNodeService _overlayService;
    private readonly OverlayPane _overlayPane;
    private readonly CompanionSection _companions;

    /// <summary>Commands shown in the shell menu.</summary>
    internal enum ShellCommand
    {
        ShowLibrary,
        OpenSpawn,
        Pose,
        Scene,
        LayoutSeparator,
        PropertiesPanel,
        Sidebar,
        Inspector,
        SettingsSeparator,
        OpenSettings,
    }

    /// <summary>The titlebar burger menu, anchored under its own button.</summary>
    private void DrawShellMenu()
    {
        BuildShellMenu();
        if (_shellMenuOpenRequested)
        {
            _shellMenuOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##shell-burger-menu",
                _shellMenuAnchor,
                _shellMenuItems,
                Crystarium.FloatingMenu.MeasureWidth(_shellMenuItems));
        }
        int clicked = Crystarium.FloatingMenu.Draw("##shell-burger-menu");
        if (clicked >= 0 && clicked < _shellMenuItems.Length)
            InvokeShellCommand((ShellCommand)clicked);
        int subClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0
            && subParent < _shellMenuItems.Length)
            InvokeShellSubmenu((ShellCommand)subParent, subClicked);
    }

    /// <summary>Updates the shell menu when its visible state changes.</summary>
    private void BuildShellMenu()
    {
        // Pose-file commands need the selected actor's skeleton.
        bool poseTarget = SelectedSkeleton() != null;
        var uiConfig = Config.ConfigurationService.Instance.Config.UI;
        bool sceneOpen = GetSceneWindowOpen?.Invoke() ?? true;
        bool inspectorOpen = GetInspectorWindowOpen?.Invoke() ?? true;
        int layoutState = (uiConfig.DetachedShell ? 1 : 0)
            | (sceneOpen ? 2 : 0)
            | (_contentHidden ? 4 : 0)
            | (uiConfig.SplitInspector ? 8 : 0)
            | (inspectorOpen ? 16 : 0);
        if (_shellMenuRowsBuilt
            && poseTarget == _shellMenuPoseTarget
            && layoutState == _shellMenuLayoutState)
            return;
        _shellMenuRowsBuilt = true;
        _shellMenuPoseTarget = poseTarget;
        _shellMenuLayoutState = layoutState;

        FillShellMenuItems(
            _shellMenuItems,
            poseTarget,
            uiConfig.DetachedShell,
            sceneOpen,
            _contentHidden,
            uiConfig.SplitInspector,
            inspectorOpen);
    }

    /// <summary>Fills the shell menu rows for the current UI state.</summary>
    internal static void FillShellMenuItems(
        Span<ContextMenuItem> items,
        bool poseTarget,
        bool detachedShell,
        bool sceneOpen,
        bool contentHidden,
        bool splitInspector = false,
        bool inspectorOpen = true)
    {
        items[(int)ShellCommand.ShowLibrary] =
            new ContextMenuItem("Show library", TablerIcon.Book);
        items[(int)ShellCommand.OpenSpawn] =
            new ContextMenuItem("Open the spawn menu", TablerIcon.Plus);
        items[(int)ShellCommand.Pose] =
            new ContextMenuItem(
                "Pose", TablerIcon.Walk, disabled: !poseTarget,
                submenuItems:
                [
                    new ContextMenuItem("Import", TablerIcon.Download),
                    new ContextMenuItem("Export", TablerIcon.Upload),
                    new ContextMenuItem("Auto-saves", TablerIcon.DeviceFloppy),
                ]);
        items[(int)ShellCommand.Scene] =
            new ContextMenuItem(
                "Scene", TablerIcon.Movie,
                submenuItems:
                [
                    new ContextMenuItem("Save", TablerIcon.DeviceFloppy),
                ]);
        items[(int)ShellCommand.LayoutSeparator] = ContextMenuItem.Separator;
        // The properties panel is the main window's own content: it opens
        // and closes, and only while the sidebar lives apart from it.
        items[(int)ShellCommand.PropertiesPanel] =
            new ContextMenuItem(
                contentHidden
                    ? "Open the properties panel"
                    : "Close the properties panel",
                contentHidden ? TablerIcon.LayoutPanel : TablerIcon.X,
                disabled: !detachedShell);
        items[(int)ShellCommand.Sidebar] =
            new ContextMenuItem(
                "Sidebar", TablerIcon.LayoutSidebarLeft,
                submenuItems: PanelVerbs(
                    TablerIcon.LayoutSidebarLeft,
                    attached: !detachedShell, open: sceneOpen));
        items[(int)ShellCommand.Inspector] =
            new ContextMenuItem(
                "Inspector", TablerIcon.LayoutSidebarRight,
                submenuItems: PanelVerbs(
                    TablerIcon.LayoutSidebarRight,
                    attached: !splitInspector, open: inspectorOpen));
        items[(int)ShellCommand.SettingsSeparator] = ContextMenuItem.Separator;
        items[(int)ShellCommand.OpenSettings] =
            new ContextMenuItem("Open settings", TablerIcon.Settings);
    }

    /// <summary>One panel's verbs: Attach or Detach by its state, then
    /// Open or Close — which only a detached panel can do.</summary>
    private static ContextMenuItem[] PanelVerbs(
        TablerIcon glyph, bool attached, bool open) =>
    [
        new ContextMenuItem(attached ? "Detach" : "Attach", glyph),
        open
            ? new ContextMenuItem("Close", TablerIcon.X, disabled: attached)
            : new ContextMenuItem("Open", glyph, disabled: attached),
    ];

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
    private bool AllActorsOwned(IReadOnlyList<SelectionId> members)
    {
        foreach (var member in members)
            if (member.Actor is { } actorId
                && ResolveActorDescriptor(actorId) is not { IsOwned: true })
                return false;
        return true;
    }

    /// <summary>What the active pane keeps in the content footer between
    /// the two attach seats.</summary>
    private void DrawFooterMiddle(Vector2 origin, Vector2 size)
    {
        if (_activeTab == "Pose" && SelectedSkeleton() is { } skeleton)
            _poseInspector.DrawParentingBar(origin, size, skeleton);
    }

    /// <summary>Runs one row of a burger submenu, routed by its parent.</summary>
    private void InvokeShellSubmenu(ShellCommand parent, int index)
    {
        switch (parent)
        {
            case ShellCommand.Pose:
                if (SelectedSkeleton() is not { } skeleton)
                    return;
                switch (index)
                {
                    case 0:
                        _poseFileSection.RequestImportMenu(withPresets: true);
                        break;
                    case 1:
                        _poseFileSection.RequestExportMenu();
                        break;
                    case 2:
                        _poseFileSection.OpenAutoSaves(skeleton);
                        break;
                }
                break;
            case ShellCommand.Scene:
                if (index == 0)
                    _scenePane.RequestLibrarySave();
                break;
            case ShellCommand.Sidebar:
                if (index == 0)
                    RequestDetachToggle();
                else
                    OnSceneWindowToggleRequested?.Invoke();
                break;
            case ShellCommand.Inspector:
                if (index == 0)
                    OnInspectorSplitToggleRequested?.Invoke();
                else
                    OnInspectorWindowToggleRequested?.Invoke();
                break;
        }
    }

    /// <summary>Requests the shell layout toggle.</summary>
    public event Action? OnDetachToggleRequested;

    /// <summary>Requests the inspector split toggle.</summary>
    public event Action? OnInspectorSplitToggleRequested;

    internal void RequestDetachToggle() => OnDetachToggleRequested?.Invoke();

    /// <summary>Runs one command. The skeleton is resolved at invocation, not
    /// captured at build: the row array outlives every selection it was built
    /// under.</summary>
    private void InvokeShellCommand(ShellCommand command)
    {
        switch (command)
        {
            case ShellCommand.ShowLibrary:
                ShowLibrary();
                break;
            case ShellCommand.OpenSpawn:
                // The menu anchor is also the spawn browser's anchor.
                OnSpawnBrowserRequested?.Invoke(
                    _shellMenuAnchor, SpawnBrowserTab.All);
                break;
            case ShellCommand.PropertiesPanel:
                ContentHidden = !ContentHidden;
                break;
            case ShellCommand.OpenSettings:
                OnSettingsRequested?.Invoke();
                break;
        }
    }

    /// <summary>The selected actor's skeleton, or null when nothing posable is
    /// selected or its binding no longer resolves.</summary>
    private ISkeleton? SelectedSkeleton()
    {
        if (SelectedActorId() is not { } actorId)
            return null;
        var resolved = _bindings.Resolve(actorId);
        return resolved.Success ? resolved.Value?.Skeleton : null;
    }

    /// <summary>Right-click actor menu: the lifetime actions that were stranded
    /// without a sidebar affordance (target / visibility / rename / clone / companion / despawn).
    /// The menu state is a stable ActorId; the legacy lifetime services still
    /// take live actors, so the id resolves through the binding registry for
    /// the duration of one frame and is dropped when resolution fails.</summary>
    private void DrawActorContextMenu()
    {
        if (_ctxActorId is not { } actorId)
            return;
        var resolved = _bindings.Resolve(actorId);
        if (!resolved.Success)
        {
            _ctxActorId = null;
            Crystarium.FloatingMenu.Dismiss("##actor-ctx");
            return;
        }
        var actor = resolved.Value!;
        // A companion rides its owner's slot and cannot be copied on its
        // own (Brio ActorLifetimeCapability.CanClone).
        bool companion = ResolveActorDescriptor(actorId) is { IsCompanion: true };

        var items = new List<ContextMenuItem>
        {
            new("Set game target", TablerIcon.Crosshair),
            new("Center camera on actor", TablerIcon.Crosshair),
            new(!_spawnService.IsVisible(actor) ? "Show" : "Hide", !_spawnService.IsVisible(actor) ? TablerIcon.Eye : TablerIcon.EyeOff),
            // The icon carries the verb the row performs: resume wears play,
            // pause wears pause.
            new(!_animation.AnyPlaying(actorId) ? "Play" : "Pause",
                !_animation.AnyPlaying(actorId)
                    ? TablerIcon.PlayerPlay
                    : TablerIcon.PlayerPause),
            new("Rename", TablerIcon.Edit),
            new("Duplicate", TablerIcon.Copy,
                disabled: companion,
                submenuItems: companion ? null : DuplicateSubmenu(actor.HasSkeleton)),
            new("Save to library", TablerIcon.Library,
                disabled: !actor.HasSkeleton),
            new("Expand", TablerIcon.SquarePlus),
            new("Collapse", TablerIcon.SquareMinus),
            new("All", TablerIcon.Copy,
                submenuItems:
                [
                    new ContextMenuItem("Expand all", TablerIcon.Copy),
                    new ContextMenuItem("Collapse all", TablerIcon.Copy),
                ]),
            ContextMenuItem.Separator,
            // The companion slot exists for riding a mount or carrying an
            // ornament — standalone creatures come from the spawn browser —
            // so its verbs live here, out of every pane, as ONE submenu.
            new("Companion", TablerIcon.Paw,
                disabled: !_spawnService.HasCompanionSlot(actor),
                help: _spawnService.HasCompanionSlot(actor)
                    ? "Attach or detach a minion, mount or ornament"
                    : "Only actors spawned with a companion slot can attach one",
                submenuItems: _spawnService.HasCompanionSlot(actor)
                    ?
                    [
                        new ContextMenuItem("Attach", TablerIcon.UserPlus),
                        new ContextMenuItem("Detach", TablerIcon.UserMinus,
                            disabled:
                                _spawnService.GetCompanionInfo(actor) is null),
                    ]
                    : null),
        };
        var companionActions = new List<Action?>
        {
            () =>
            {
                _companionCatalog.EnsureLoaded();
                _companions.OpenAttachPicker(actorId);
            },
            () => _spawnService.DestroyCompanion(actor),
        };
        var actions = new List<Action?>
        {
            () => _actorManager.SetGPoseTarget(actor),
            () => _cameraPane.CenterOnActor(actorId),
            () => _spawnService.SetVisibility(actor, !_spawnService.IsVisible(actor)),
            () =>
            {
                if (_animation.AnyPlaying(actorId))
                    _animation.Pause(actorId);
                else
                    _animation.Resume(actorId);
            },
            () =>
            {
                _renameTarget = actorId;
                // Seeds what the UI shows — nickname, else the mask while
                // anonymous mode is on. Prefilling the raw name would leak it.
                _renameValue = Config.ConfigurationService.Instance.GetDisplayName(
                    actorId.LogicalId, DisplayName(actor.Name));
                _renameOpen = true;
            },
            null, // Duplicate — child clicks are read separately.
            () => OpenEntityRename(
                "Save actor to library",
                Config.ConfigurationService.Instance.GetDisplayName(
                    actorId.LogicalId, DisplayName(actor.Name)),
                name => SaveOwnedActorEntry(actorId, name)),
            () => SetTreeCollapsed("actor:" + actorId, false, subtree: false),
            () => SetTreeCollapsed("actor:" + actorId, true, subtree: false),
            null, // All — child clicks are read separately.
            null, // separator
            null, // Companion — child clicks are read separately.
        };

        // Bone presets belong to this actor.
        items.Add(ContextMenuItem.Separator);
        items.Add(new ContextMenuItem(
            "Bone presets", TablerIcon.Eye,
            disabled: !actor.HasSkeleton,
            help: "Named sets of which bones this actor shows in the overlay",
            submenuItems: actor.HasSkeleton
                ? BuildBonePresetSubmenu(actorId)
                : null));
        actions.Add(null); // separator
        actions.Add(null); // Child clicks are read separately.

        items.Add(ContextMenuItem.Separator);
        actions.Add(null); // separator
        bool hasStash = _cleanPose.HasStash;
        items.Add(new ContextMenuItem(
            "Pose", TablerIcon.Walk,
            disabled: !actor.HasSkeleton,
            help: actor.HasSkeleton
                ? "Import, export or stash this actor's pose"
                : "Needs a loaded skeleton",
            submenuItems: actor.HasSkeleton
                ? hasStash
                    ?
                    [
                        new ContextMenuItem("Import", TablerIcon.Download),
                        new ContextMenuItem(
                            "Import from file", TablerIcon.FileText),
                        new ContextMenuItem("Export", TablerIcon.Upload),
                        new ContextMenuItem("Stash", TablerIcon.Stack2),
                        new ContextMenuItem(
                            "Apply stashed", TablerIcon.ArrowBackUp),
                    ]
                    :
                    // No stash, no row: a menu never holds an empty seat.
                    [
                        new ContextMenuItem("Import", TablerIcon.Download),
                        new ContextMenuItem(
                            "Import from file", TablerIcon.FileText),
                        new ContextMenuItem("Export", TablerIcon.Upload),
                        new ContextMenuItem("Stash", TablerIcon.Stack2),
                    ]
                : null));
        actions.Add(null); // Pose — child clicks are read separately.
        var poseActions = new List<Action?>
        {
            () => _poseFileSection.RequestImportMenu(withPresets: true),
            () =>
            {
                if (actor.HasSkeleton)
                    _poseFileSection.OpenImportFromFile(actor.Skeleton);
            },
            () => _poseFileSection.RequestExportMenu(),
            () => _cleanPose.Stash(
                actor,
                Config.ConfigurationService.Instance.GetDisplayName(
                    actorId.LogicalId, DisplayName(actor.Name))),
            () => _cleanPose.ApplyStash(actor),
        };

        // ONE verb for every actor, Brio's: Destroy
        // (Brio ActorLifetimeWidget.cs:82 — the same word whoever spawned
        // the actor, your own clone included). The row appears only when the
        // service would admit it right now — an actor it must refuse (a
        // companion child, a stale wrapper) gets no row rather than a row
        // that refuses.
        if (_spawnService.IsSpawnedActor(actor)
            || _spawnService.RemovalRefusal(actor) is null)
        {
            items.Add(ContextMenuItem.Separator);
            items.Add(new ContextMenuItem("Destroy", TablerIcon.Trash, danger: true));
            actions.Add(null);
            actions.Add(() =>
            {
                string name = DisplayName(actor.Name);
                // Through the seam, exactly as Clone is: spawning an actor
                // is a history step; destroying is undoable only when Poser
                // spawned it and can respawn it.
                if (_lifecycle.DespawnActor(actor))
                {
                    // Drop the whole selection lineage — the actor, its
                    // bones, its bone groups — not every selection the user
                    // holds.
                    _selection.RemoveActorLineage(actorId.LogicalId);
                    _notices.Done($"Destroyed '{name}'.");
                }
                else
                {
                    _notices.Failed($"'{name}' could not be destroyed.");
                }
            });
        }

        if (_ctxOpenRequested)
        {
            _ctxOpenRequested = false;
            Crystarium.FloatingMenu.Open("##actor-ctx", ImGui.GetMousePos(), items.ToArray());
        }
        // The preset rows show live checks: the menu takes this frame's
        // rows so a toggle shows at once while the menu stays open.
        Crystarium.FloatingMenu.Refresh("##actor-ctx", items.ToArray());
        int clicked = Crystarium.FloatingMenu.Draw("##actor-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
        // Three submenus share the menu; the click routes by its parent
        // row's label.
        int subClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Count)
        {
            var submenu = items[subParent].Label switch
            {
                "Bone presets" => _bonePresetActions,
                "Companion" => companionActions,
                "Pose" => poseActions,
                "Duplicate" => new List<Action?>
                {
                    () => Duplicate(actor),
                    () => DuplicateWithPose(actor),
                },
                "All" => new List<Action?>
                {
                    () => SetTreeCollapsed("actor:" + actorId, false, subtree: true),
                    () => SetTreeCollapsed("actor:" + actorId, true, subtree: true),
                },
                _ => null,
            };
            if (submenu != null && subClicked < submenu.Count)
                submenu[subClicked]?.Invoke();
        }
    }

    private ContextMenuItem[] BuildBonePresetSubmenu(ActorId actorId)
    {
        if (FindActor(actorId.LogicalId) is not { } actor)
            return Array.Empty<ContextMenuItem>();
        _presetActorId = actorId;
        _bonePresetItems.Clear();
        _bonePresetActions.Clear();
        var presets = _bonePresets.Presets;
        if (presets.Count == 0)
        {
            _bonePresetItems.Add(new ContextMenuItem(
                "No presets yet", TablerIcon.Circle, disabled: true,
                help: "Show the bones you want, then save them as a preset"));
            _bonePresetActions.Add(null);
        }
        foreach (var preset in presets)
        {
            var name = preset.Name;
            _bonePresetItems.Add(new ContextMenuItem(
                name,
                _bonePresets.IsApplied(actor, name)
                    ? TablerIcon.CircleDot
                    : TablerIcon.Circle,
                keepOpen: true,
                help: $"{preset.Bones.Count} bones"));
            _bonePresetActions.Add(() => _bonePresets.Toggle(actor, name));
        }

        _bonePresetItems.Add(ContextMenuItem.Separator);
        _bonePresetActions.Add(null);
        _bonePresetItems.Add(new ContextMenuItem(
            "Show uncovered bones", TablerIcon.Crosshair,
            disabled: presets.Count == 0,
            help: "Hide everything the presets claim and show the rest"));
        _bonePresetActions.Add(() => _bonePresets.ToggleOther(actor));
        _bonePresetItems.Add(new ContextMenuItem(
            "Hide every bone", TablerIcon.EyeOff,
            help: "Take this actor's overlay back to nothing"));
        _bonePresetActions.Add(() => _bonePresets.Clear(actor));
        _bonePresetItems.Add(ContextMenuItem.Separator);
        _bonePresetActions.Add(null);
        _bonePresetItems.Add(new ContextMenuItem(
            "Manage presets", TablerIcon.Edit,
            help: "Save what this actor shows as a new preset, or delete one"));
        _bonePresetActions.Add(() =>
        {
            _presetNameValue = string.Empty;
            _presetSaveNote = null;
            _presetManagerOpen = true;
        });
        return _bonePresetItems.ToArray();
    }

    /// <summary>The preset store, which is shared by every actor: create one
    /// from what the menu's actor currently shows, or delete one. These
    /// operations apply immediately and remain outside Settings.</summary>
    private void DrawBonePresetManager()
    {
        if (!_presetManagerOpen)
            return;
        var actor = _presetActorId is { } id ? FindActor(id.LogicalId) : null;
        float gap = 8f * ImGuiHelpers.GlobalScale;
        Crystarium.Modal(
            "##bone-presets-manage",
            _presetManagerOpen,
            next => _presetManagerOpen = next,
            "Bone visibility presets",
            () =>
        {
            Crystarium.TextInput(
                "##bone-preset-name",
                _presetNameValue,
                next => _presetNameValue = next,
                placeholder: "New preset name");
            ImGui.Dummy(new Vector2(0f, gap));
            if (Crystarium.Button(
                    "Save what this actor shows",
                    variant: ButtonVariant.Primary,
                    id: "bone-preset-save",
                    disabled: actor == null,
                    help: "Store every bone currently shown in the overlay under that name"))
            {
                _presetSaveNote =
                    _bonePresets.SaveCurrent(_presetNameValue, actor!);
                if (_presetSaveNote == null)
                    _presetNameValue = string.Empty;
            }
            if (_presetSaveNote is { Length: > 0 } note)
                Crystarium.Text(note);

            ImGui.Dummy(new Vector2(0f, gap));
            var presets = _bonePresets.Presets;
            if (presets.Count == 0)
            {
                Crystarium.Text("No presets stored yet.");
                return;
            }
            string? doomed = null;
            for (int i = 0; i < presets.Count; i++)
            {
                var preset = presets[i];
                var name = preset.Name;
                if (Crystarium.IconButton(
                        TablerIcon.Trash,
                        id: $"bone-preset-delete-{i}",
                        help: $"Delete '{name}'"))
                    doomed = name;
                ImGui.SameLine(0f, gap);
                Crystarium.Text($"{name} — {preset.Bones.Count} bones");
            }
            if (doomed != null)
            {
                _bonePresets.Delete(doomed);
                _presetSaveNote = null;
            }
        });
    }

    /// <summary>
    /// Right-click bone menu for hierarchy navigation and bone-local
    /// operations. Hierarchy facts come from the scene snapshot; selection and
    /// pose commands dispatch stable ids only.
    /// </summary>
    private void DrawBoneContextMenu()
    {
        if (_ctxBoneId is not { } boneId)
            return;

        var owner = FindActor(boneId.Skeleton.Actor.LogicalId);
        var bones = owner?.GetSkeleton(boneId.Slot)?.Bones;
        var descriptor = bones?.FirstOrDefault(candidate => candidate.Id.Equals(boneId));
        if (bones == null || descriptor == null)
        {
            _ctxBoneId = null;
            _ctxBoneOverlayBones = null;
            Crystarium.FloatingMenu.Dismiss("##bone-ctx");
            return;
        }

        var mirrorName = _bonePosingService.GetMirrorBoneName(boneId.CanonicalName);
        var mirror = mirrorName == null
            ? null
            : bones.FirstOrDefault(candidate =>
                candidate.Id.CanonicalName == mirrorName &&
                candidate.Id.PartialId == boneId.PartialId);
        bool hasChildren = bones.Any(candidate => candidate.Parent?.Equals(boneId) == true);

        var overlayBones = _ctxBoneOverlayBones ?? new[] { boneId };
        bool overlayVisible =
            _overlayPresentation.AreVisible(overlayBones);
        var items = new[]
        {
            new ContextMenuItem("Select parent", TablerIcon.SelectParent, disabled: descriptor.Parent == null),
            new ContextMenuItem("Select children", TablerIcon.SelectChildren, disabled: !hasChildren),
            new ContextMenuItem("Select mirrored bone", TablerIcon.SelectMirror, disabled: mirror == null),
            new ContextMenuItem(
                overlayVisible
                    ? "Hide from overlay"
                    : "Show in overlay",
                overlayVisible
                    ? TablerIcon.EyeOff
                    : TablerIcon.Eye),
            ContextMenuItem.Separator,
            new ContextMenuItem("Flip bone", TablerIcon.Rotate),
            new ContextMenuItem("Reset bone", TablerIcon.Refresh, danger: true),
        };

        if (_boneCtxOpenRequested)
        {
            _boneCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open("##bone-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##bone-ctx");
        switch (clicked)
        {
            case 0 when descriptor.Parent is { } parent:
                _selection.Select(SelectionId.ForBone(parent));
                break;
            case 1:
            {
                _selection.Select(SelectionId.ForBone(boneId));
                var byId = bones.ToDictionary(candidate => candidate.Id);
                foreach (var candidate in bones)
                {
                    for (var parent = candidate.Parent;
                         parent is { } parentId;
                         parent = byId.TryGetValue(parentId, out var parentDescriptor)
                             ? parentDescriptor.Parent
                             : null)
                    {
                        if (!parentId.Equals(boneId))
                            continue;
                        _selection.Add(SelectionId.ForBone(candidate.Id));
                        break;
                    }
                }
                break;
            }
            case 2 when mirror != null:
                _selection.Select(SelectionId.ForBone(mirror.Id));
                break;
            case 3:
                _overlayPresentation.SetVisible(
                    overlayBones,
                    !overlayVisible);
                break;
            case 5:
                _cleanPose.FlipBone(
                    TransformTargetId.ForBone(boneId),
                    descriptor.DisplayName);
                break;
            case 6:
                _cleanPose.ResetBone(
                    TransformTargetId.ForBone(boneId),
                    descriptor.DisplayName);
                break;
        }
    }

    private ReferenceImageInstance? _ctxReferenceImage;
    private bool _referenceCtxOpenRequested;

    /// <summary>
    /// A reference picture's verbs, in the overlay-node rows' family: the eye's
    /// own verb, the rename every named thing in the tree carries, a second
    /// placement, and the close. No transform verbs and no journal entry — a
    /// picture is not in the scene, so there is nothing for undo to restore it
    /// to and nothing to isolate it from.
    /// </summary>
    private void DrawReferenceImageContextMenu()
    {
        if (_ctxReferenceImage is not { } image)
            return;
        // A picture closed from its own bar while the menu is up leaves the
        // roster; the menu goes with it rather than acting on a dead entry.
        if (!_referenceImages.Instances.Contains(image))
        {
            _ctxReferenceImage = null;
            Crystarium.FloatingMenu.Dismiss("##reference-ctx");
            return;
        }
        bool hidden = ReferenceImageSession.IsHidden(image);
        var items = new[]
        {
            new ContextMenuItem(
                hidden ? "Show" : "Hide",
                hidden ? TablerIcon.Eye : TablerIcon.EyeOff),
            new ContextMenuItem("Rename", TablerIcon.Edit),
            new ContextMenuItem("Duplicate", TablerIcon.Copy),
            new ContextMenuItem("Remove", TablerIcon.Trash),
        };
        if (_referenceCtxOpenRequested)
        {
            _referenceCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##reference-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##reference-ctx");
        if (clicked < 0)
            return;
        switch (clicked)
        {
            case 0:
                _referenceImages.SetHidden(image, !hidden);
                break;
            case 1:
                OpenEntityRename(
                    "Rename reference image",
                    image.Name,
                    next => image.Entry.Name = next);
                break;
            case 2:
                _referenceImages.Duplicate(image);
                break;
            case 3:
                _referenceImages.Close(image);
                break;
        }
        _ctxReferenceImage = null;
    }

    private OverlayId? _ctxOverlayNodeId;
    private bool _overlayNodeCtxOpenRequested;

    /// <summary>Right-click menu for a staged overlay NODE (balloon, talk,
    /// status) — distinct from the bone-category overlay menu below. The
    /// same lifetime family the light menu speaks, in the overlay's
    /// vocabulary; the pane's own Duplicate is reused so one duplication
    /// rule answers everywhere.</summary>
    private void DrawOverlayNodeContextMenu()
    {
        if (_ctxOverlayNodeId is not { } overlayId)
            return;
        var resolved = _bindings.Resolve(overlayId);
        if (!resolved.Success || resolved.Value is not { } node)
        {
            _ctxOverlayNodeId = null;
            Crystarium.FloatingMenu.Dismiss("##overlay-node-ctx");
            return;
        }
        var items = new[]
        {
            new ContextMenuItem(node.Visible ? "Hide" : "Show",
                node.Visible ? TablerIcon.EyeOff : TablerIcon.Eye),
            new ContextMenuItem("Rename", TablerIcon.Edit),
            new ContextMenuItem("Duplicate", TablerIcon.Copy),
            new ContextMenuItem("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
            new ContextMenuItem("Destroy", TablerIcon.Trash, danger: true),
        };
        var actions = new Action?[]
        {
            () => node.Visible = !node.Visible,
            () => OpenEntityRename(
                "Rename overlay", node.Name, next => node.Name = next),
            () => _overlayPane.Duplicate(node),
            () => OpenEntityRename(
                "Save overlay to library", node.State.Name,
                name => _scenePane.SaveOverlayEntry(
                    overlayId.LogicalId, name)),
            null, // separator
            () =>
            {
                _lifecycle.DestroyOverlay(node);
                _selection.Clear();
            },
        };
        if (_overlayNodeCtxOpenRequested)
        {
            _overlayNodeCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##overlay-node-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##overlay-node-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
    }

    private void DrawOverlayContextMenu()
    {
        if (_ctxOverlayBones is not { } bones)
            return;
        var owner = _scene.Snapshot.Actors.FirstOrDefault(actor =>
            actor.Skeletons.Any(skeleton =>
                skeleton.Bones.Any(candidate =>
                    bones.Contains(candidate.Id))));
        if (owner == null)
        {
            _ctxOverlayBones = null;
            _ctxOverlayMemoryKey = null;
            Crystarium.FloatingMenu.Dismiss("##overlay-ctx");
            return;
        }
        var state = _overlayPresentation.Resolve(bones);
        var items = new[]
        {
            new ContextMenuItem(
                state switch
                {
                    OverlayVisibility.None => "Show category in overlay",
                    _ => "Hide the currently shown bones",
                },
                state == OverlayVisibility.None
                    ? TablerIcon.Eye
                    : TablerIcon.EyeOff),
            new ContextMenuItem("Show only this category", TablerIcon.Crosshair),
            new ContextMenuItem("Show all of this actor", TablerIcon.Eye),
            new ContextMenuItem("Hide all of this actor", TablerIcon.EyeOff),
        };
        if (_overlayCtxOpenRequested)
        {
            _overlayCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##overlay-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##overlay-ctx");
        if (clicked < 0)
            return;
        // Isolate/show-all operate on the owning actor's bones only, so other
        // actors' overlay masks are untouched.
        var ownerBones = owner.Skeletons
            .SelectMany(skeleton => skeleton.Bones)
            .Select(candidate => candidate.Id)
            .ToArray();
        switch (clicked)
        {
            case 0:
                if (_ctxOverlayMemoryKey is { } memoryKey)
                    _overlayPresentation.ToggleVisibleWithMemory(
                        memoryKey, bones);
                else
                    _overlayPresentation.SetVisible(
                        bones, state == OverlayVisibility.None);
                break;
            case 1:
                _overlayPresentation.SetVisible(ownerBones, false);
                _overlayPresentation.SetVisible(bones, true);
                break;
            case 2:
                _overlayPresentation.SetVisible(ownerBones, true);
                break;
            case 3:
                _overlayPresentation.SetVisible(ownerBones, false);
                break;
        }
        _ctxOverlayBones = null;
        _ctxOverlayMemoryKey = null;
    }

    // ── light / camera / prop context menus ─────────────────────────────

    private LightId? _ctxLightId;
    private bool _lightCtxOpenRequested;
    private CameraId? _ctxCameraId;
    private bool _cameraCtxOpenRequested;
    private PropId? _ctxPropId;
    private bool _propCtxOpenRequested;

    /// <summary>THE naming prompt, shared with every pane: lights,
    /// cameras and props carry their name on the entity, so one modal
    /// writes whichever apply hook the opener handed it — unlike the
    /// actor modal, which writes a nickname beside a name the game
    /// owns.</summary>
    private readonly Controls.EntityNameModal _names;

    /// <summary>Right-click light menu: the lifetime verbs the actor menu
    /// gives its rows, spoken in the light's vocabulary — the eye, the file,
    /// and the ownership-aware destroy/release the actions section makes.
    /// </summary>
    private void DrawLightContextMenu()
    {
        if (_ctxLightId is not { } lightId)
            return;
        var resolved = _bindings.Resolve(lightId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } light)
        {
            _ctxLightId = null;
            Crystarium.FloatingMenu.Dismiss("##light-ctx");
            return;
        }

        var items = new List<ContextMenuItem>
        {
            new(light.IsOn ? "Switch off" : "Switch on",
                light.IsOn ? TablerIcon.EyeOff : TablerIcon.Eye),
            new("Rename", TablerIcon.Edit),
            new("Duplicate", TablerIcon.Copy),
            new("Save to file…", TablerIcon.DeviceFloppy),
            new("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
        };
        var actions = new List<Action?>
        {
            () => light.IsOn = !light.IsOn,
            () => OpenEntityRename(
                "Rename light", light.Name, next => light.Name = next),
            () => _lifecycle.CloneLight(light),
            () => _lightPane.OpenSave(light),
            // The library save asks for the entry's NAME first — the same
            // modal renames use, with the light's name as the start.
            () => OpenEntityRename(
                "Save light to library", light.Name,
                name => _scenePane.SaveLightEntry(lightId.LogicalId, name)),
            null, // separator
        };
        if (light.Ownership == LightOwnership.Spawned)
        {
            items.Add(new ContextMenuItem(
                "Destroy", TablerIcon.Trash, danger: true));
            actions.Add(() =>
            {
                _lifecycle.DestroyLight(light);
                _selection.Clear();
            });
        }
        else
        {
            items.Add(new ContextMenuItem("Release", TablerIcon.X));
            actions.Add(() =>
            {
                _lightingService.ReleaseLight(light);
                _selection.Clear();
            });
        }

        if (_lightCtxOpenRequested)
        {
            _lightCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##light-ctx", ImGui.GetMousePos(), items.ToArray());
        }
        int clicked = Crystarium.FloatingMenu.Draw("##light-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
    }

    /// <summary>
    /// Right-click prop menu: the same lifetime family the light menu speaks,
    /// in the prop's vocabulary. A prop was the one entity row whose right
    /// click did nothing at all, while actors, bones, categories, lights and
    /// cameras all answered.
    ///
    /// <para>There is no "Save to file…" row because a prop has no document
    /// of its own — its whole identity is the model triple, which the scene
    /// file carries. Every lifetime verb goes through the history seam, so a
    /// clone and destroy use the same history seam as light actions.</para>
    /// </summary>
    private void DrawPropContextMenu()
    {
        if (_ctxPropId is not { } propId)
            return;
        var resolved = _bindings.Resolve(propId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } prop)
        {
            _ctxPropId = null;
            Crystarium.FloatingMenu.Dismiss("##prop-ctx");
            return;
        }

        var items = new ContextMenuItem[]
        {
            new(prop.Visible ? "Hide" : "Show",
                prop.Visible ? TablerIcon.EyeOff : TablerIcon.Eye),
            new("Rename", TablerIcon.Edit),
            new("Duplicate", TablerIcon.Copy),
            new("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
            new("Destroy", TablerIcon.Trash, danger: true),
        };
        var actions = new Action?[]
        {
            () => prop.Visible = !prop.Visible,
            () => OpenEntityRename(
                "Rename object", prop.Name, next => prop.Name = next),
            () =>
            {
                if (_lifecycle.CloneProp(prop) is Game.PropHandle clone &&
                    _bindings.GetPropId(clone) is { } cloneId)
                    _selection.Select(SelectionId.ForProp(cloneId));
            },
            () => OpenEntityRename(
                "Save prop to library", prop.Name,
                name => _scenePane.SavePropEntry(propId.LogicalId, name)),
            null, // separator
            () =>
            {
                _lifecycle.DestroyProp(prop);
                _selection.Clear();
            },
        };

        if (_propCtxOpenRequested)
        {
            _propCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##prop-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##prop-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
    }

    /// <summary>Right-click camera menu for live, framing, file, and lifetime
    /// actions. The default camera cannot be destroyed.
    /// </summary>
    private void DrawCameraContextMenu()
    {
        if (_ctxCameraId is not { } cameraId)
            return;
        var resolved = _bindings.Resolve(cameraId);
        if (!resolved.Success ||
            resolved.Value is not { IsValid: true } camera ||
            _bindings.GetCameraId(camera) != cameraId)
        {
            _ctxCameraId = null;
            Crystarium.FloatingMenu.Dismiss("##camera-ctx");
            return;
        }

        bool canRecenterTracked = CanRecenterOnTracked(camera);
        var items = new List<ContextMenuItem>
        {
            new(camera.IsLive
                    ? "Return to main camera"
                    : "Look through", TablerIcon.Video,
                disabled: camera.IsLive && camera.IsDefault),
            new(camera.IsLocked ? "Unlock" : "Lock",
                camera.IsLocked ? TablerIcon.LockOpen : TablerIcon.Lock),
            new("Look at tracked actor", TablerIcon.Crosshair,
                disabled: !canRecenterTracked,
                help: "Swing the camera back onto whoever it tracks"),
            new("Rename", TablerIcon.Edit, disabled: camera.IsLocked),
            new("Duplicate", TablerIcon.Copy),
            new("Save to file…", TablerIcon.DeviceFloppy),
            new("Save to library", TablerIcon.Library),
            new("Reset transform", TablerIcon.Refresh,
                disabled: camera.IsLocked || !_cameraService.IsAvailable),
            new("Reset properties", TablerIcon.Refresh,
                disabled: camera.IsLocked),
        };
        var actions = new List<Action?>
        {
            () =>
            {
                if (!camera.IsLive)
                {
                    _cameraService.SetLive(camera);
                    return;
                }
                foreach (var candidate in _cameraService.Cameras)
                {
                    if (candidate.IsDefault)
                    {
                        _cameraService.SetLive(candidate);
                        break;
                    }
                }
            },
            () => camera.IsLocked = !camera.IsLocked,
            () => RecenterCameraOnTrackedActor(cameraId),
            () => OpenEntityRename(
                "Rename camera", camera.Name, next => camera.Name = next),
            () =>
            {
                if (_lifecycle.CloneCamera(camera) is { } clone)
                    _cameraPane.SelectWhenBound(clone);
            },
            () => _cameraPane.OpenSave(camera),
            () => OpenEntityRename(
                "Save camera to library", camera.Name,
                name => _scenePane.SaveCameraEntry(cameraId.LogicalId, name)),
            () => _cameraPane.ResetCameraTransform(cameraId),
            () => camera.ResetProperties(),
        };
        if (!camera.IsDefault)
        {
            items.Add(ContextMenuItem.Separator);
            items.Add(new ContextMenuItem(
                "Destroy", TablerIcon.Trash, danger: true));
            actions.Add(null);
            actions.Add(() =>
            {
                _lifecycle.DestroyCamera(camera);
                _selection.Clear();
            });
        }

        if (_cameraCtxOpenRequested)
        {
            _cameraCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##camera-ctx", ImGui.GetMousePos(), items.ToArray());
        }
        int clicked = Crystarium.FloatingMenu.Draw("##camera-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
    }

    // ── world-object / group / selection context menus ──────────────────

    private WorldObjectId? _ctxWorldObjectId;
    private bool _worldObjectCtxOpenRequested;
    private Guid? _ctxGroupId;
    private bool _groupCtxOpenRequested;
    private bool _selectionCtxOpenRequested;

    /// <summary>Right-click borrowed-object menu: the eye, the user's own
    /// name over the map's model, and Release — never Destroy, because the
    /// map owns the thing and gets it back where it stood.</summary>
    private void DrawWorldObjectContextMenu()
    {
        if (_ctxWorldObjectId is not { } worldObjectId)
            return;
        var resolved = _bindings.Resolve(worldObjectId);
        if (!resolved.Success || resolved.Value is not { IsValid: true } worldObject)
        {
            _ctxWorldObjectId = null;
            Crystarium.FloatingMenu.Dismiss("##world-object-ctx");
            return;
        }
        var items = new[]
        {
            new ContextMenuItem(worldObject.Visible ? "Hide" : "Show",
                worldObject.Visible ? TablerIcon.EyeOff : TablerIcon.Eye),
            new ContextMenuItem("Rename", TablerIcon.Edit),
            new ContextMenuItem("Duplicate", TablerIcon.Copy),
            new ContextMenuItem("Save to library", TablerIcon.Library),
            ContextMenuItem.Separator,
            // A spawned object is Poser's own and DESTROYS; a borrowed
            // one is the map's and goes back where it stood.
            worldObject.Spawned
                ? new ContextMenuItem("Destroy", TablerIcon.Trash,
                    danger: true)
                : new ContextMenuItem("Release", TablerIcon.X),
        };
        var actions = new Action?[]
        {
            () => worldObject.Visible = !worldObject.Visible,
            () => OpenEntityRename(
                "Rename object", worldObject.Name,
                next => worldObject.Name = next),
            () =>
            {
                if (DuplicateWorldObject(worldObject) is { } copy
                    && _bindings.GetWorldObjectId(copy) is { } copyId)
                    _selection.Select(SelectionId.ForWorldObject(copyId));
            },
            () => OpenEntityRename(
                "Save object to library", worldObject.Name,
                name => _scenePane.SaveWorldObjectEntry(
                    worldObjectId.LogicalId, name)),
            null, // separator
            () =>
            {
                _lifecycle.ReleaseWorldObject(worldObject);
                _selection.Clear();
            },
        };
        if (_worldObjectCtxOpenRequested)
        {
            _worldObjectCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##world-object-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##world-object-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
    }

    /// <summary>Right-click group-head menu: the structure verbs. The
    /// selection verbs live one click away — the head's left click IS the
    /// member selection, whose own menu then answers.</summary>
    private void DrawGroupContextMenu()
    {
        if (_ctxGroupId is not { } groupId)
            return;
        if (_groups.Find(groupId) is not { } group)
        {
            _ctxGroupId = null;
            Crystarium.FloatingMenu.Dismiss("##group-ctx");
            return;
        }
        bool locked = group.Locked;
        // The gates read as the group's own state: closed shows the verb
        // that opens it. A closed gate anywhere above still wins.
        var items = new[]
        {
            new ContextMenuItem("Rename", TablerIcon.Edit),
            new ContextMenuItem("Duplicate", TablerIcon.Copy,
                submenuItems: DuplicateSubmenu(posable: true)),
            new ContextMenuItem("Save to library", TablerIcon.Library),
            new ContextMenuItem(locked ? "Unlock" : "Lock",
                locked ? TablerIcon.LockOpen : TablerIcon.Lock),
            ContextMenuItem.Separator,
            new ContextMenuItem(group.Hidden ? "Show" : "Hide",
                group.Hidden ? TablerIcon.Eye : TablerIcon.EyeOff),
            new ContextMenuItem(group.Paused ? "Play" : "Pause",
                group.Paused ? TablerIcon.PlayerPlay : TablerIcon.PlayerPause),
            new ContextMenuItem(group.Night ? "Day" : "Night",
                group.Night ? TablerIcon.Sun : TablerIcon.Moon),
            ContextMenuItem.Separator,
            new ContextMenuItem("Ungroup", TablerIcon.X),
            ContextMenuItem.Separator,
            new ContextMenuItem("Destroy", TablerIcon.Trash, danger: true),
        };
        var actions = new Action?[]
        {
            () => OpenEntityRename(
                "Rename group", group.Name,
                next => _groups.Rename(groupId, next)),
            null, // Duplicate — child clicks are read separately.
            () => OpenEntityRename(
                "Save group to library", group.Name,
                name => _scenePane.SaveGroupEntry(
                    group.Members, name, AllActorsOwned(group.Members))),
            () => _groups.SetLocked(groupId, !group.Locked),
            null, // separator
            () => SetGroupHidden(group, !group.Hidden),
            () => SetGroupPaused(group, !group.Paused),
            () => SetGroupNight(group, !group.Night),
            null, // separator
            () => DissolveGroup(groupId),
            null, // separator
            // The members go through each kind's own lifetime seam; the
            // emptied group dissolves through the scene prune.
            () => DestroyEntities(group.Members.ToArray()),
        };
        if (_groupCtxOpenRequested)
        {
            _groupCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##group-ctx", ImGui.GetMousePos(), items);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##group-ctx");
        if (clicked >= 0 && clicked < actions.Length)
            actions[clicked]?.Invoke();
        int subClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Length
            && items[subParent].Label == "Duplicate")
            DuplicateGroup(group, withPose: subClicked == 1);
    }

    /// <summary>Right-click on any row of a multi-entity selection: one
    /// menu for the WHOLE selection, every verb dispatching per kind
    /// through the same plumbing the single menus use. A kind a verb
    /// cannot reach is skipped, never refused; verbs no selected kind
    /// answers disable in place.</summary>
    private void DrawSelectionContextMenu()
    {
        int entities = global::Poser.Application.Selection.EntitySelection
            .CountEntities(_selection.Selected);
        if (entities < 2)
        {
            Crystarium.FloatingMenu.Dismiss("##selection-ctx");
            return;
        }

        // Hide/Show and Pause/Play drive the set to ONE state: any
        // visible member means Hide, anything running means Pause. The
        // pause verb exists only when something in the set animates.
        bool anyVisible = false, anyAnimated = false, anyRunning = false;
        bool anyActor = false;
        foreach (var id in _selection.Selected)
        {
            if (PlayingOf(id) is { } playing)
            {
                anyAnimated = true;
                anyRunning |= playing;
            }
            switch (id)
            {
                case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                    anyActor = true;
                    if (_bindings.Resolve(actorId) is
                            { Success: true, Value: { } actor }
                        && _spawnService.IsVisible(actor))
                        anyVisible = true;
                    break;
                case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                    if (_bindings.Resolve(lightId) is
                            { Success: true, Value: { IsOn: true } })
                        anyVisible = true;
                    break;
                case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                    if (_bindings.Resolve(propId) is
                            { Success: true, Value: { Visible: true } })
                        anyVisible = true;
                    break;
                case { Kind: SceneEntityKind.WorldObject,
                        WorldObject: { } borrowedId }:
                    if (_bindings.Resolve(borrowedId) is
                            { Success: true, Value: { Visible: true } })
                        anyVisible = true;
                    break;
                case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                    if (_bindings.Resolve(overlayId) is
                            { Success: true, Value: { Visible: true } })
                        anyVisible = true;
                    break;
            }
        }

        var matched = _groups.ActiveSelection(_selection.Selected);
        // With an actor in the set, Duplicate opens the plain/posed
        // choice; without one there is nothing to pose.
        var items = new List<ContextMenuItem>
        {
            new("Duplicate", TablerIcon.Copy,
                submenuItems: anyActor ? DuplicateSubmenu(posable: true) : null),
            new(anyVisible ? "Hide" : "Show",
                anyVisible ? TablerIcon.EyeOff : TablerIcon.Eye),
        };
        var actions = new List<Action?>
        {
            anyActor ? null : () => DuplicateSelection(withPose: false),
            () => SetSelectionVisible(!anyVisible),
        };
        if (anyAnimated)
        {
            items.Add(new ContextMenuItem(anyRunning ? "Pause" : "Play",
                anyRunning ? TablerIcon.PlayerPause : TablerIcon.PlayerPlay));
            actions.Add(() => SetSelectionPaused(anyRunning));
        }
        items.Add(new ContextMenuItem("Move to camera", TablerIcon.Crosshair));
        actions.Add(MoveSelectionToCamera);
        items.Add(ContextMenuItem.Separator);
        actions.Add(null);
        if (matched != null)
        {
            items.Add(new ContextMenuItem(
                "Save to library", TablerIcon.Library));
            actions.Add(() => OpenEntityRename(
                "Save group to library", matched.Name,
                name => _scenePane.SaveGroupEntry(
                    matched.Members, name, AllActorsOwned(matched.Members))));
            items.Add(new ContextMenuItem("Ungroup", TablerIcon.X));
            actions.Add(() => DissolveGroup(matched.Id));
        }
        else
        {
            items.Add(new ContextMenuItem("Group…", TablerIcon.Folder));
            actions.Add(() => OpenEntityRename(
                "Name the group",
                $"Group {_groups.All.Count + 1}",
                name => _groups.Create(name, _selection.Selected)));
        }
        items.Add(new ContextMenuItem("Deselect", TablerIcon.X));
        actions.Add(() => _selection.Clear());
        items.Add(ContextMenuItem.Separator);
        actions.Add(null);
        items.Add(new ContextMenuItem("Destroy", TablerIcon.Trash,
            danger: true));
        actions.Add(DestroySelection);
        if (_selectionCtxOpenRequested)
        {
            _selectionCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##selection-ctx", ImGui.GetMousePos(), items.ToArray());
        }
        int clicked = Crystarium.FloatingMenu.Draw("##selection-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
        int subClicked = Crystarium.FloatingMenu.ConsumeSubmenuClick(
            out int subParent);
        if (subClicked >= 0 && subParent >= 0 && subParent < items.Count
            && items[subParent].Label == "Duplicate")
            DuplicateSelection(withPose: subClicked == 1);
    }

    /// <summary>Duplicates the selection: a whole group as a new group,
    /// otherwise each entity by its own kind through the same history-
    /// seamed calls the single menus use. Borrowed objects with no model
    /// have no copy; the selection stays on the ORIGINALS — the copies'
    /// bindings land on the scene's own refresh.</summary>
    private void DuplicateSelection(bool withPose)
    {
        if (_groups.ActiveSelection(_selection.Selected) is { } whole)
        {
            DuplicateGroup(whole, withPose);
            return;
        }
        foreach (var id in _selection.Selected.ToArray())
            DuplicateEntity(id, withPose);
    }

    private static ContextMenuItem[] DuplicateSubmenu(bool posable) =>
    [
        new ContextMenuItem("Duplicate", TablerIcon.Copy),
        new ContextMenuItem("Duplicate with pose", TablerIcon.Stack2,
            disabled: !posable),
    ];

    /// <summary>One entity's copy, by kind; the live copy, or null when
    /// the kind has none or the copy failed.</summary>
    private object? DuplicateEntity(SelectionId id, bool withPose)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                return _bindings.Resolve(actorId) is { Success: true, Value: { } actor }
                    ? DuplicateActor(actor, withPose)
                    : null;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                return _bindings.Resolve(lightId) is { Success: true, Value: { IsValid: true } light }
                    ? _lifecycle.CloneLight(light)
                    : null;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                return _bindings.Resolve(propId) is { Success: true, Value: { IsValid: true } prop }
                    ? _lifecycle.CloneProp(prop)
                    : null;
            case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                return _bindings.Resolve(cameraId) is { Success: true, Value: { IsValid: true } camera }
                    ? _lifecycle.CloneCamera(camera)
                    : null;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                return _bindings.Resolve(overlayId) is { Success: true, Value: { } node }
                    ? _overlayPane.Duplicate(node)
                    : null;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }:
                return _bindings.Resolve(objectId) is { Success: true, Value: { IsValid: true } worldObject }
                    ? DuplicateWorldObject(worldObject)
                    : null;
            default:
                return null;
        }
    }

    /// <summary>A spawned copy of a world object: the same model at the
    /// same place with the same dressing. A borrowed object whose model
    /// never loaded states its address as the path and has nothing to
    /// copy from.</summary>
    private Game.WorldObjects.AdoptedWorldObject? DuplicateWorldObject(
        Game.WorldObjects.AdoptedWorldObject source)
    {
        if (!source.Path.Contains('/'))
        {
            _notices.Failed($"'{source.Name}' has no model to copy.");
            return null;
        }
        if (_lifecycle.SpawnWorldObject(source.Path, source.Transform, source.Visible)
            is not Game.WorldObjects.AdoptedWorldObject copy)
            return null;
        copy.Name = source.Name;
        copy.Opacity = source.Opacity;
        copy.Tint = source.Tint;
        if (source.IsVfx)
        {
            copy.LoopVfx = source.LoopVfx;
            copy.VfxSpeed = source.VfxSpeed;
            copy.VfxIntensity = source.VfxIntensity;
            copy.VfxPaused = source.VfxPaused;
        }
        else
            copy.NightState = source.NightState;
        return copy;
    }

    // ── duplicating groups ───────────────────────────────────────────────
    // The copies spawn at once; their bindings land on the scene's own
    // refresh, so the group is assembled from the pump once every copy
    // has an id (or patience runs out and what did bind is grouped).

    private sealed class GroupCopy
    {
        public string Name = "";
        public bool Hidden, Paused, Night;
        public readonly List<object> Members = new();
        public readonly List<GroupCopy> Children = new();
        public Guid? Parent;
        public int Index = -1;
        public global::Poser.Application.Scene.RootSlot? Anchor;
        public int Frames;
    }

    private readonly List<GroupCopy> _groupCopies = new();
    private const int GroupCopyPatience = 120;

    /// <summary>Copies the group and everything beneath it into a new
    /// group of the same name, seated right after the original at the
    /// same level, gates and all.</summary>
    private void DuplicateGroup(global::Poser.Application.Scene.SceneGroup group, bool withPose)
    {
        var copy = CopyGroupTree(group, withPose);
        copy.Parent = group.ParentId;
        if (group.ParentId is { } parentId && _groups.Find(parentId) is { } parent)
            copy.Index = parent.Children.IndexOf(group.Id) + 1;
        else
            copy.Anchor = global::Poser.Application.Scene.RootSlot.ForGroup(group.Id);
        _groupCopies.Add(copy);
    }

    private GroupCopy CopyGroupTree(global::Poser.Application.Scene.SceneGroup group, bool withPose)
    {
        var copy = new GroupCopy
        {
            Name = group.Name,
            Hidden = group.Hidden,
            Paused = group.Paused,
            Night = group.Night,
        };
        foreach (var member in group.Members)
            if (DuplicateEntity(member, withPose) is { } made)
                copy.Members.Add(made);
        foreach (var childId in group.Children)
            if (_groups.Find(childId) is { } child)
                copy.Children.Add(CopyGroupTree(child, withPose));
        return copy;
    }

    private void PumpGroupCopies()
    {
        for (int i = _groupCopies.Count - 1; i >= 0; i--)
        {
            var copy = _groupCopies[i];
            if (!CopyBound(copy) && ++copy.Frames < GroupCopyPatience)
                continue;
            _groupCopies.RemoveAt(i);
            if (RealizeGroupCopy(copy) is not { } made)
            {
                _notices.Failed($"'{copy.Name}' could not be duplicated: nothing in it copied.");
                continue;
            }
            if (copy.Parent is { } parentId && _groups.Find(parentId) != null)
                _groups.Nest(made.Id, parentId, copy.Index);
            else if (copy.Anchor is { } anchor)
                _groups.MoveRoot(
                    global::Poser.Application.Scene.RootSlot.ForGroup(made.Id), anchor, after: true);
        }
    }

    private bool CopyBound(GroupCopy copy)
    {
        foreach (var member in copy.Members)
            if (IdOfLive(member) == null)
                return false;
        foreach (var child in copy.Children)
            if (!CopyBound(child))
                return false;
        return true;
    }

    private global::Poser.Application.Scene.SceneGroup? RealizeGroupCopy(GroupCopy copy)
    {
        var ids = new List<SelectionId>();
        foreach (var member in copy.Members)
            if (IdOfLive(member) is { } id)
                ids.Add(id);
        var children = new List<global::Poser.Application.Scene.SceneGroup>();
        foreach (var child in copy.Children)
            if (RealizeGroupCopy(child) is { } made)
                children.Add(made);
        if (ids.Count + children.Count == 0)
            return null;
        var group = _groups.Create(copy.Name, ids, allowThin: true);
        if (group == null)
            return null;
        foreach (var child in children)
            _groups.Nest(child.Id, group.Id);
        if (copy.Hidden)
            SetGroupHidden(group, true);
        if (copy.Paused)
            SetGroupPaused(group, true);
        if (copy.Night)
            SetGroupNight(group, true);
        return group;
    }

    /// <summary>A live entity's selection id once the scene has bound it.</summary>
    private SelectionId? IdOfLive(object live) => live switch
    {
        IActor actor => _bindings.GetActorId(actor) is { } a ? SelectionId.ForActor(a) : null,
        ILight light => _bindings.GetLightId(light) is { } l ? SelectionId.ForLight(l) : null,
        Game.PropHandle prop => _bindings.GetPropId(prop) is { } p ? SelectionId.ForProp(p) : null,
        IVirtualCamera camera => _bindings.GetCameraId(camera) is { } c ? SelectionId.ForCamera(c) : null,
        Game.Overlays.OverlayNodeHandle node => _bindings.GetOverlayId(node) is { } o ? SelectionId.ForOverlay(o) : null,
        Game.WorldObjects.AdoptedWorldObject worldObject =>
            _bindings.GetWorldObjectId(worldObject) is { } w ? SelectionId.ForWorldObject(w) : null,
        _ => null,
    };

    // ── group gates: closed hides, pauses or benights everything beneath
    // and remembers each member's own state; open gives it back — unless
    // a gate further up is still closed ──────────────────────────────────

    /// <summary>Ungrouping opens every gate first so each member gets its
    /// own state back.</summary>
    private void DissolveGroup(Guid id)
    {
        if (_groups.Find(id) is { } group)
        {
            SetGroupHidden(group, false);
            SetGroupPaused(group, false);
            SetGroupNight(group, false);
        }
        _groups.Dissolve(id);
    }

    private bool UnderClosedGate(SelectionId member, Func<global::Poser.Application.Scene.SceneGroup, bool> closed)
    {
        if (_groups.GroupOf(member) is not { } own)
            return false;
        if (closed(own))
            return true;
        foreach (var ancestor in _groups.Ancestors(own))
            if (closed(ancestor))
                return true;
        return false;
    }

    /// <summary>One gate's mechanics, shared by the three: closing reads
    /// and remembers each member's own state and imposes the gate's;
    /// opening gives the remembered state back to every member no other
    /// closed gate still covers.</summary>
    private void SetGate(
        global::Poser.Application.Scene.SceneGroup group,
        bool close,
        Dictionary<SelectionId, bool> remembered,
        Func<global::Poser.Application.Scene.SceneGroup, bool> closedOn,
        Func<SelectionId, bool?> read,
        Action<SelectionId, bool> write,
        bool imposed)
    {
        if (close)
        {
            foreach (var member in _groups.Descendants(group))
            {
                if (read(member) is not { } own)
                    continue;
                if (!remembered.ContainsKey(member))
                    remembered[member] = own;
                write(member, imposed);
            }
        }
        else
        {
            foreach (var (member, own) in remembered)
                if (!UnderClosedGate(member, closedOn))
                    write(member, own);
            remembered.Clear();
        }
        _groups.Touch();
    }

    private void SetGroupHidden(global::Poser.Application.Scene.SceneGroup group, bool hidden)
    {
        if (group.Hidden == hidden)
            return;
        group.Hidden = hidden;
        SetGate(group, hidden, group.RememberedVisible, g => g.Hidden,
            IsEntityVisible, SetEntityVisible, imposed: false);
    }

    private void SetGroupPaused(global::Poser.Application.Scene.SceneGroup group, bool paused)
    {
        if (group.Paused == paused)
            return;
        group.Paused = paused;
        SetGate(group, paused, group.RememberedPlaying, g => g.Paused,
            PlayingOf, SetPlaying, imposed: false);
    }

    private void SetGroupNight(global::Poser.Application.Scene.SceneGroup group, bool night)
    {
        if (group.Night == night)
            return;
        group.Night = night;
        SetGate(group, night, group.RememberedNight, g => g.Night,
            NightOf, SetNight, imposed: true);
    }

    /// <summary>A member joining under closed gates takes each gate's
    /// state from the outermost closed group, which remembers its own.</summary>
    private void JoinGroupOverrides(SelectionId member)
    {
        if (_groups.GroupOf(member) is not { } home)
            return;
        var chain = new List<global::Poser.Application.Scene.SceneGroup> { home };
        chain.AddRange(_groups.Ancestors(home));
        global::Poser.Application.Scene.SceneGroup? hiding = null, pausing = null, benighting = null;
        foreach (var group in chain)
        {
            if (group.Hidden)
                hiding = group;
            if (group.Paused)
                pausing = group;
            if (group.Night)
                benighting = group;
        }
        if (hiding != null && IsEntityVisible(member) is { } visible)
        {
            hiding.RememberedVisible[member] = visible;
            SetEntityVisible(member, false);
        }
        if (pausing != null && PlayingOf(member) is { } playing)
        {
            pausing.RememberedPlaying[member] = playing;
            SetPlaying(member, false);
        }
        if (benighting != null && NightOf(member) is { } night)
        {
            benighting.RememberedNight[member] = night;
            SetNight(member, true);
        }
    }

    /// <summary>A member leaving its group gets its own state back from
    /// whichever group remembered it.</summary>
    private void LeaveGroupOverrides(SelectionId member)
    {
        if (_groups.GroupOf(member) is not { } own)
            return;
        var chain = new List<global::Poser.Application.Scene.SceneGroup> { own };
        chain.AddRange(_groups.Ancestors(own));
        foreach (var group in chain)
        {
            if (group.RememberedVisible.Remove(member, out var visible))
                SetEntityVisible(member, visible);
            if (group.RememberedPlaying.Remove(member, out var playing))
                SetPlaying(member, playing);
            if (group.RememberedNight.Remove(member, out var night))
                SetNight(member, night);
        }
    }

    /// <summary>Whether this entity is playing its animation: an actor's
    /// timeline, an effect's playback, borrowed scenery's animation. Null
    /// for kinds that do not animate, spawned scenery included.</summary>
    private bool? PlayingOf(SelectionId id)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                return _animation.AnyPlaying(actorId);
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }:
                if (_bindings.Resolve(objectId) is not
                        { Success: true, Value: { IsValid: true } handle })
                    return null;
                if (handle.IsVfx)
                    return !handle.VfxPaused;
                return handle.Spawned ? null : !handle.AnimationPaused;
            default:
                return null;
        }
    }

    private void SetPlaying(SelectionId id, bool playing)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                if (playing)
                    _animation.Resume(actorId);
                else
                    _animation.Pause(actorId);
                break;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }:
                if (_bindings.Resolve(objectId) is not
                        { Success: true, Value: { IsValid: true } handle })
                    return;
                if (handle.IsVfx)
                    handle.VfxPaused = !playing;
                else if (!handle.Spawned)
                    handle.AnimationPaused = !playing;
                break;
        }
    }

    /// <summary>Scenery's night state; null for everything else.</summary>
    private bool? NightOf(SelectionId id) =>
        id is { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }
        && _bindings.Resolve(objectId) is
            { Success: true, Value: { IsValid: true, IsVfx: false } handle }
            ? handle.NightState
            : null;

    private void SetNight(SelectionId id, bool night)
    {
        if (id is { Kind: SceneEntityKind.WorldObject, WorldObject: { } objectId }
            && _bindings.Resolve(objectId) is
                { Success: true, Value: { IsValid: true, IsVfx: false } handle })
            handle.NightState = night;
    }

    private bool? IsEntityVisible(SelectionId id)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                return _bindings.Resolve(actorId) is { Success: true, Value: { } actor }
                    ? _spawnService.IsVisible(actor) : null;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                return _bindings.Resolve(lightId) is { Success: true, Value: { IsValid: true } light }
                    ? light.IsOn : null;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                return _bindings.Resolve(propId) is { Success: true, Value: { IsValid: true } prop }
                    ? prop.Visible : null;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } borrowedId }:
                return _bindings.Resolve(borrowedId) is { Success: true, Value: { IsValid: true } borrowed }
                    ? borrowed.Visible : null;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                return _bindings.Resolve(overlayId) is { Success: true, Value: { } node }
                    ? node.Visible : null;
            default:
                return null;
        }
    }

    private void SetEntityVisible(SelectionId id, bool visible)
    {
        switch (id)
        {
            case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                if (_bindings.Resolve(actorId) is { Success: true, Value: { } actor })
                    _spawnService.SetVisibility(actor, visible);
                break;
            case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                if (_bindings.Resolve(lightId) is { Success: true, Value: { IsValid: true } light })
                    light.IsOn = visible;
                break;
            case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                if (_bindings.Resolve(propId) is { Success: true, Value: { IsValid: true } prop })
                    prop.Visible = visible;
                break;
            case { Kind: SceneEntityKind.WorldObject, WorldObject: { } borrowedId }:
                if (_bindings.Resolve(borrowedId) is { Success: true, Value: { IsValid: true } borrowed })
                    borrowed.Visible = visible;
                break;
            case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                if (_bindings.Resolve(overlayId) is { Success: true, Value: { } node })
                    node.Visible = visible;
                break;
        }
    }

    private void SetSelectionVisible(bool visible)
    {
        foreach (var id in _selection.Selected)
        {
            switch (id)
            {
                case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                    if (_bindings.Resolve(actorId) is
                            { Success: true, Value: { } actor })
                        _spawnService.SetVisibility(actor, visible);
                    break;
                case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                    if (_bindings.Resolve(lightId) is
                            { Success: true, Value: { IsValid: true } light })
                        light.IsOn = visible;
                    break;
                case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                    if (_bindings.Resolve(propId) is
                            { Success: true, Value: { IsValid: true } prop })
                        prop.Visible = visible;
                    break;
                case { Kind: SceneEntityKind.WorldObject,
                        WorldObject: { } borrowedId }:
                    if (_bindings.Resolve(borrowedId) is
                            { Success: true, Value: { IsValid: true } borrowed })
                        borrowed.Visible = visible;
                    break;
                case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                    if (_bindings.Resolve(overlayId) is
                            { Success: true, Value: { } node })
                        node.Visible = visible;
                    break;
            }
        }
    }

    /// <summary>One animation state for every selected actor.</summary>
    private void SetSelectionPaused(bool paused)
    {
        foreach (var id in _selection.Selected)
            SetPlaying(id, !paused);
    }

    /// <summary>Destroys the whole selection, each kind through its own
    /// lifetime seam: actors despawn where the service admits it, spawned
    /// lights destroy while borrowed ones release, the default camera
    /// stays, borrowed objects go back to the map.</summary>
    private void DestroySelection()
    {
        DestroyEntities(_selection.Selected.ToArray());
        _selection.Clear();
    }

    private void DestroyEntities(IReadOnlyList<SelectionId> ids)
    {
        foreach (var id in ids)
        {
            // A locked group keeps its members standing.
            if (_groups.IsLockedMember(id))
                continue;
            switch (id)
            {
                case { Kind: SceneEntityKind.Actor, Actor: { } actorId }:
                    if (_bindings.Resolve(actorId) is
                            { Success: true, Value: { } actor }
                        && (_spawnService.IsSpawnedActor(actor)
                            || _spawnService.RemovalRefusal(actor) is null)
                        && _lifecycle.DespawnActor(actor))
                        _selection.RemoveActorLineage(actorId.LogicalId);
                    break;
                case { Kind: SceneEntityKind.Light, Light: { } lightId }:
                    if (_bindings.Resolve(lightId) is
                            { Success: true, Value: { IsValid: true } light })
                    {
                        if (light.Ownership == LightOwnership.Spawned)
                            _lifecycle.DestroyLight(light);
                        else
                            _lightingService.ReleaseLight(light);
                    }
                    break;
                case { Kind: SceneEntityKind.Prop, Prop: { } propId }:
                    if (_bindings.Resolve(propId) is
                            { Success: true, Value: { IsValid: true } prop })
                        _lifecycle.DestroyProp(prop);
                    break;
                case { Kind: SceneEntityKind.Camera, Camera: { } cameraId }:
                    if (_bindings.Resolve(cameraId) is
                            { Success: true, Value: { IsValid: true } camera }
                        && !camera.IsDefault)
                        _lifecycle.DestroyCamera(camera);
                    break;
                case { Kind: SceneEntityKind.Overlay, Overlay: { } overlayId }:
                    if (_bindings.Resolve(overlayId) is
                            { Success: true, Value: { } node })
                        _lifecycle.DestroyOverlay(node);
                    break;
                case { Kind: SceneEntityKind.WorldObject,
                        WorldObject: { } borrowedId }:
                    if (_bindings.Resolve(borrowedId) is
                            { Success: true, Value: { IsValid: true } borrowed })
                        _lifecycle.ReleaseWorldObject(borrowed);
                    break;
            }
        }
    }

    /// <summary>The selection's actor, if any — the recenter seat's
    /// target.</summary>
    private SelectionId? SelectedActorRef()
    {
        foreach (var id in _selection.Selected)
            if (id is { Kind: SceneEntityKind.Actor })
                return id;
        return null;
    }

    /// <summary>Whether the LOOK-AT verb has anything to do — the context
    /// menu's "Look at tracked actor", distinct from the row seat's
    /// Brio-style retarget.</summary>
    private bool CanRecenterOnTracked(IVirtualCamera camera)
    {
        if (!_cameraService.IsAvailable || camera.IsLocked || !camera.IsLive
            || camera.Kind == CameraKind.Free || camera.FixedPosition != null)
            return false;
        return ResolveCameraTrackedActor(camera) is { } tracked
            && TryResolveExactActor(tracked.Id, out var exact)
            && _spawnService.IsVisible(exact);
    }

    private void RecenterCameraOnTrackedActor(CameraId cameraId)
    {
        var resolved = _bindings.Resolve(cameraId);
        if (!resolved.Success ||
            resolved.Value is not { IsValid: true } camera ||
            _bindings.GetCameraId(camera) != cameraId ||
            !_cameraService.IsAvailable || camera.IsLocked || !camera.IsLive ||
            camera.Kind == CameraKind.Free || camera.FixedPosition != null)
        {
            return;
        }
        var actor = ResolveCameraTrackedActor(camera);
        if (actor == null || !TryResolveExactActor(actor.Id, out var exact) ||
            !_spawnService.IsVisible(exact))
        {
            return;
        }
        _cameraPane.CenterOnActor(actor.Id);
    }

    /// <summary>The plain duplicate: the drawn appearance and the source's
    /// Penumbra collection, idling. No Customize+ (decision 2026-09-02).</summary>
    private void Duplicate(IActor actor)
    {
        if (DuplicateActor(actor, withPose: false) is { } clone
            && _bindings.GetActorId(clone) is { } cloneId)
            _selection.Select(SelectionId.ForActor(cloneId));
    }

    private void DuplicateWithPose(IActor actor)
    {
        if (DuplicateActor(actor, withPose: true) is { } clone
            && _bindings.GetActorId(clone) is { } cloneId)
            _selection.Select(SelectionId.ForActor(cloneId));
    }

    /// <summary>The copy itself, plain or posed; posed falls back to plain
    /// for an actor with no skeleton to read.</summary>
    private IActor? DuplicateActor(IActor actor, bool withPose)
    {
        if (!withPose || !actor.HasSkeleton)
            return _lifecycle.SpawnActor(
                $"Duplicate actor '{DisplayName(actor.Name)}'",
                () => CloneWearingCollection(actor));
        return DuplicateActorWithPose(actor);
    }

    /// <summary>The posed duplicate: spawned wearing the collection, restored
    /// to the source's pose and place once posable, frozen, and its gaze
    /// frozen with it — a duplicate never animates and never tracks. No
    /// Customize+: the captured bones already carry it.</summary>
    private IActor? DuplicateActorWithPose(IActor actor)
    {
        var clone = _lifecycle.SpawnActorWithPose(
            $"Duplicate actor '{DisplayName(actor.Name)}' with pose",
            () => CloneWearingCollection(actor),
            actor);
        if (clone == null || _bindings.GetActorId(clone) is not { } cloneId)
            return clone;
        _animation.Pause(cloneId);
        // Before the first draw: a copy that once engaged the camera look-at
        // and was then paused froze mid blend-out, head off its neck
        // (2026-09-02). Detached from the start, nothing ever engages.
        FreezeGaze(clone);
        return clone;
    }

    /// <summary>The seed copy plus what the built body needs again: the
    /// drawn look and the equipment visibility flags once posable. The
    /// Penumbra collection is the spawn service's own inherit. Customize+ is
    /// never applied: the posed duplicate carries the shape in its bone
    /// scales and translations, the plain one idles as the game draws it.</summary>
    private IActor? CloneWearingCollection(IActor source)
    {
        var clone = _spawnService.CloneActor(source);
        if (clone == null)
            return null;
        _lifecycle.WhenPosable(clone, c =>
        {
            _spawnService.CopyDrawnAppearance(source, c);
            _spawnService.CopyEquipmentVisibility(source, c);
        });
        return clone;
    }

    /// <summary>No gaze at all: the copy's eyes, head and body stay on
    /// the pose. Freezing the parts only pinned where they looked, and the
    /// game's loop kept turning the head after the camera.</summary>
    private void FreezeGaze(IActor copy)
    {
        var mode = _gazeService.SetGazeMode(copy, GazeTargetMode.Detached);
        if (!mode.Success)
            _log.Warning($"Duplicate: the gaze could not be detached: {mode.Detail}");
    }

    /// <summary>Whether the current selection is empty or every selected
    /// entity has <paramref name="parent"/> as its group (null = root).</summary>
    private bool SelectionParentIs(Guid? parent)
    {
        foreach (var selected in _selection.Selected)
            if (_groups.GroupOf(selected)?.Id != parent)
                return false;
        return true;
    }

    private void OpenEntityRename(
        string title, string current, Action<string> apply) =>
        _names.Open(title, current, apply);

    /// <summary>The light/camera rename modal. The apply hook captured the
    /// live entity at open; a stale entity write is a no-op on an invalid
    /// native, exactly as the pane's own name row would be.</summary>
    private void DrawEntityRenameModal() => _names.Draw();

    /// <summary>One text input between the two bars: header 44 + padded
    /// input row + footer 44.</summary>
    private const float NamePromptHeight = 152f;

    private void DrawRenameModal()
    {
        if (!_renameOpen || _renameTarget is not { } target) return;
        Crystarium.Modal(
            "##rename-actor",
            _renameOpen,
            next => _renameOpen = next,
            "Rename actor",
            height: NamePromptHeight,
            body: () => Crystarium.TextInput(
                "##rename-input", _renameValue, next => _renameValue = next),
            footer: () =>
        {
            bool submit =
                ImGui.IsKeyPressed(ImGuiKey.Enter, repeat: false) ||
                ImGui.IsKeyPressed(ImGuiKey.KeypadEnter, repeat: false);
            if (Crystarium.Button("Clear", id: "rename-clear",
                help: "Remove the nickname and show the real name"))
            {
                Config.ConfigurationService.Instance.SetNickname(target.LogicalId, null);
                _renameOpen = false;
            }
            ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
            if (Crystarium.Button(
                    "Save",
                    variant: ButtonVariant.Primary,
                    id: "rename-save") || submit)
            {
                Config.ConfigurationService.Instance.SetNickname(target.LogicalId, _renameValue);
                _renameOpen = false;
            }
        });
    }

}
