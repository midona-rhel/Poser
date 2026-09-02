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
public partial class MainWindow : Window
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
            string trackLabel = _scene.Snapshot.FindActor(trackActorId.LogicalId) is { } tracked
                ? ActorNames.Display(tracked)
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

    private ActorId? SelectedActorId() => _selection.PrimaryActor;

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

    private void OpenEntityRename(
        string title, string current, Action<string> apply) =>
        _names.Open(title, current, apply);

    /// <summary>The light/camera rename modal. The apply hook captured the
    /// live entity at open; a stale entity write is a no-op on an invalid
    /// native, exactly as the pane's own name row would be.</summary>
    private void DrawEntityRenameModal() => _names.Draw();
}
