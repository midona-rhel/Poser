using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Application.Scene;
using Poser.Application.Selection;
using Poser.Core;
using Poser.Domain.Identity;
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
    private readonly IGazeService _gazeService;

    /// <summary>Every entity the shell adds or removes goes through this, so
    /// the act lands in the same history the transforms do.</summary>
    private readonly Game.Scene.SceneLifecycleHistory _lifecycle;

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
    private bool _overlayCtxOpenRequested;
    private bool _renameOpen;
    private string _renameValue = "";
    private ActorId? _renameTarget;
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
    private readonly AppShellViewModel _vm = new();

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

    /// <summary>The workspace is showing the pose library instead of the
    /// selection's tabs. The SELECTION is untouched — the library applies to
    /// whatever actor was selected before the mode was entered.</summary>
    private bool _libraryMode;

    /// <summary>The workspace is showing the WHOLE SHOT — save, load, progress
    /// and recovery — instead of the selection's tabs. A mode exactly like the
    /// library's, and its alternative: a shot is not a property of whatever
    /// happens to be selected.</summary>
    private bool _sceneMode;

    /// <summary>The library's sidebar section and its one tab, both retained:
    /// they carry no per-frame data, so a warm frame restates them rather than
    /// minting them.</summary>
    private readonly ShellSidebarSection _librarySection = new()
    {
        Title = "LIBRARY",
        Selectable = true,
    };

    /// <summary>The scene's environment, seated above the actors. It is the one
    /// scene entity that is always there and there is only ever ONE of it, so
    /// the header IS the affordance — exactly like the library's — rather than a
    /// header naming a lone row beneath it. Nothing creates or destroys it, so
    /// the section shows no plus and carries no rows.</summary>
    private readonly ShellSidebarSection _environmentSection = new()
    {
        Title = "ENVIRONMENT",
        ShowPlus = false,
        Selectable = true,
    };

    /// <summary>The one environment selection, minted once: it carries no
    /// per-scene data, so every frame's row and flag refresh restate it.
    /// </summary>
    private static readonly SelectionId EnvironmentSelection =
        SelectionId.ForEnvironment();

    /// <summary>The scene's own sidebar section, retained with its rows: the
    /// tree is the most expensive thing a frame can assemble, so it is rebuilt
    /// only when <see cref="BuildSidebar"/>'s gate flips and refreshed in place
    /// on every other frame.</summary>
    private readonly ShellSidebarSection _actorsSection = new()
    {
        Title = "ACTORS",
        ShowPlus = true,
    };

    /// <summary>The props section, retained like ACTORS: flat rows, one per
    /// spawned prop, rebuilt behind the same gate (the scene revision carries
    /// a prop's spawn, destroy and visibility) and flag-refreshed on warm
    /// frames.</summary>
    private readonly ShellSidebarSection _propsSection = new()
    {
        Title = "PROPS",
        ShowPlus = true,
    };

    /// <summary>The lights section, retained like ACTORS. Lights are flat — a
    /// spawned light owns nothing beneath it — so its rows are one per light,
    /// rebuilt behind the same gate (the scene revision carries a light's
    /// spawn, rename, kind and on-state) and flag-refreshed on warm frames.
    /// </summary>
    private readonly ShellSidebarSection _lightsSection = new()
    {
        Title = "LIGHTS",
    };

    /// <summary>The cameras section, the lights section's twin: flat rows,
    /// one per virtual camera, rebuilt behind the same gate (the scene
    /// revision carries a camera's create, rename and live switch) and
    /// flag-refreshed on warm frames.</summary>
    private readonly ShellSidebarSection _camerasSection = new()
    {
        Title = "CAMERAS",
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
    /// disclosure is the one non-scene input that changes the row COUNT.
    /// </summary>
    private int _expandVersion;

    /// <summary>Library mode's tab strip: the library TYPES are the tabs —
    /// a lone "Library" tab controlled nothing. Positional against the
    /// pane's type indices.</summary>
    private readonly ShellTab[] _libraryTabs =
    [
        new() { Label = "Poses" },
        new() { Label = "Auto-saves" },
        new() { Label = "MCDF" },
    ];

    /// <summary>The shot workspace's one tab, retained like every other
    /// strip. Whole-shot save/load is a MODE, not a property of a selection,
    /// so it has its own strip rather than a tab on someone else's.</summary>
    private readonly ShellTab[] _sceneTabs =
    [
        new() { Label = SceneTabLabel },
    ];

    /// <summary>The shot strip's one label, and the tab-layout identity that
    /// goes with it.</summary>
    private const string SceneTabLabel = "Shot";

    /// <summary>The selection-typed tab strip, retained like the library's —
    /// three fresh ShellTabs per frame were pure churn.</summary>
    private readonly ShellTab[] _selectionTabs =
    [
        new() { Label = "Pose" },
        new() { Label = "Animation" },
        new() { Label = "Appearance" },
    ];

    /// <summary>A prop's strip, the camera strip's sibling: while a prop is
    /// selected the one tab IS the prop editor.</summary>
    private readonly ShellTab[] _propTabs =
    [
        new() { Label = "Prop" },
    ];

    /// <summary>The environment's own tab strip: selecting the environment
    /// swaps the whole strip, because none of the actor tabs mean anything for
    /// it. The environment carries eleven sections — one tab holding all of them
    /// was a scroll, not a workspace — so the strip splits them five ways.
    /// Positional against <see cref="EnvironmentTab"/>.</summary>
    private readonly ShellTab[] _environmentTabs =
    [
        new() { Label = "Weather" },
        new() { Label = "Sky" },
        new() { Label = "Light" },
        new() { Label = "Atmosphere" },
        new() { Label = "World" },
    ];

    /// <summary>A light's whole tab strip, the environment strip's sibling:
    /// a light has no pose, animation or appearance, so while one is selected
    /// the tab set IS the light editor, split the way the editor's own three
    /// concerns split — what it emits, what it casts, and where it is. Its
    /// "Light" label is SHARED with the environment's lighting tab — two
    /// strips, never both live — so DrawTabContent settles the two by
    /// selection, not by label.</summary>
    private readonly ShellTab[] _lightTabs =
    [
        new() { Label = "Light" },
        new() { Label = "Shadows" },
    ];

    /// <summary>A camera's tab strip, the light strip's sibling: while a
    /// camera is selected the one tab IS the camera editor — the camera's
    /// offset and its bone tracking live on the inspector rail instead.
    /// </summary>
    private readonly ShellTab[] _cameraTabs =
    [
        new() { Label = "Camera" },
    ];

    /// <summary>The library section is stated first, so its index is fixed.
    /// </summary>
    private const int LibrarySectionIndex = 0;

    /// <summary>The environment stands second, and its header is the second of
    /// the two selectable ones.</summary>
    private const int EnvironmentSectionIndex = 1;

    /// <summary>The sections are stated in a fixed order — library,
    /// environment, actors, lights — so the actors section is index 2. Its
    /// header and the lights header are the only two whose plus creates
    /// anything; the environment is never created or destroyed.</summary>
    private const int ActorsSectionIndex = 2;

    /// <summary>Props stand between the actors and the lights: scene
    /// furniture, owned by nobody.</summary>
    private const int PropsSectionIndex = 3;

    /// <summary>Lights stand under the actors they light.</summary>
    private const int LightsSectionIndex = 4;

    /// <summary>Cameras stand last: they look at everything above them.</summary>
    private const int CamerasSectionIndex = 5;

    /// <summary>Reports whether the skeleton overlay window is open (titlebar toggle state).</summary>
    public Func<bool>? GetSkeletonOverlayOn { get; set; }

    /// <summary>Raised when the titlebar skeleton-overlay toggle is clicked.</summary>
    public event Action<bool>? OnSkeletonOverlayToggled;

    public event Action? OnSettingsRequested;

    /// <summary>Raised by every creation affordance — the titlebar plus, the
    /// section header plusses, and the shell menu — with the pointer position
    /// the browser opens AT and the tab that affordance answers for.</summary>
    public event Action<Vector2, SpawnBrowserTab>? OnSpawnBrowserRequested;

    /// <summary>Pop out the main content, frozen to this actor. The window
    /// set answers by minting a <see cref="PopOutWindow"/>.</summary>
    public event Action<ActorId>? OnPopOutRequested;

    /// <summary>The strip's Scene toggle: the window set flips the Scene
    /// window and answers its state through <see cref="GetSceneWindowOpen"/>.
    /// </summary>
    public event Action? OnSceneWindowToggleRequested;

    public Func<bool>? GetSceneWindowOpen { get; set; }

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
        CompanionSection companions,
        SkeletonOverlayPresentation overlayPresentation,
        IGazeService gazeService,
        Game.Scene.SceneLifecycleHistory lifecycle,
        IEventBus eventBus)
        : base($"{PluginConstants.PluginName}###poser_main_window",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        Size = new Vector2(DefaultWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;
        // Construction predates the configuration read; PreDraw restates the
        // effective floor every frame anyway.
        SizeConstraints = ExpandedSizeConstraints(MinimumWidth);

        _gPoseService = gPoseService;
        _actorManager = actorManager;
        _scene = scene;
        _selection = scene.Selection;
        _bindings = bindings;
        _editorState = editorState;
        _cleanTransforms = cleanTransforms;
        _cleanPose = cleanPose;
        _bonePosingService = bonePosingService;

        _spawnService = spawnService;
        _propService = propService;
        _propsPane = propsPane;
        _companions = companions;
        _poseInspector = poseInspector;
        _animationPane = animationPane;
        _appearancePane = appearancePane;
        _lightPane = lightPane;
        _lightingService = lightingService;
        _cameraPane = cameraPane;
        _cameraService = cameraService;
        _environmentPane = environmentPane;
        _libraryPane = libraryPane;
        _scenePane = scenePane;
        // The library's "Add source…" and its empty state both mean the same
        // thing the titlebar gear does, so they travel the one settings route.
        _libraryPane.OnSettingsRequested += () => OnSettingsRequested?.Invoke();
        _poseFileSection = poseFileSection;
        // The import menus resolve their target actor through the same
        // binding registry the context menus use.
        _poseFileSection._resolveActor = id =>
            _bindings.Resolve(id) is { Success: true } resolved
                ? resolved.Value
                : null;
        _animation = animation;
        _overlayPresentation = overlayPresentation;
        _gazeService = gazeService;
        _lifecycle = lifecycle;
        // A gaze mode flip changes the sidebar's row SET (the gaze anchor row
        // exists only in Position mode) while bumping neither the scene
        // revision nor the disclosure version. The handler arms the cold path
        // and does nothing else: the publisher is not the draw thread.
        eventBus.Subscribe<GazeStateChangedEvent>(_ => _gazeDirty = true);
        _animationCatalog = animationCatalog;
        _companionCatalog = companionCatalog;
        _poseInspector.DrawMapInline = graphicalBonePane.DrawInline;
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
        _vm.OnGizmoOperation = i => _editorState.TransformTool = (TransformTool)i;
        _vm.OnGizmoSpace = i => _editorState.TransformOrientation = (TransformOrientation)i;
        _vm.OnRotationPivot = i => _editorState.RotationPivot = (Core.RotationPivot)i;
        _vm.OnSymmetry = i => _editorState.SymmetryMode = (SymmetryMode)i;
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
        // The switch's polarity is "physics simulating"; the service's is
        // "freeze requested". The request is booked against the SCENE, not
        // against whatever happens to be selected: the freeze is one
        // process-global patch, and a shell switch that only worked while an
        // animating actor was selected made a scene-wide control hostage to
        // the selection (user 2026-08-14).
        _vm.OnPhysics = on => _animation.SetScenePhysicsFrozen(!on);
        _vm.OnUndo = Undo;
        _vm.OnRedo = Redo;
        _vm.OnSkeletonOverlay = on => OnSkeletonOverlayToggled?.Invoke(on);
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
        _vm.OnPopOut = () =>
        {
            if (SelectedActorId() is { } popOut)
                OnPopOutRequested?.Invoke(popOut);
        };
        // The sidebar's add affordance. Every section plus opens the ONE
        // spawn browser, UNDER THAT PLUS, on that section's own tab — the
        // browser replaced the per-section mini choosers (user 2026-08-11:
        // "it should spawn where the user click, either the plus at the top
        // or the plus next to actors camera or lights"). The anchor is the
        // button's own bottom-left, not the pointer, so the surface stands in
        // one place per plus (user 2026-08-14).
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
        };
        // The LIBRARY and ENVIRONMENT headers are the selectable ones, so no
        // other index can arrive. The library is a MODE over an untouched
        // selection; the environment is a scene entity, so its header selects
        // exactly as a row does — leaving the library first, because the two are
        // alternatives in one workspace and the environment's own tab strip
        // cannot show through the library's.
        _vm.OnSectionSelected = index =>
        {
            if (index == LibrarySectionIndex)
                ShowLibrary();
            else if (index == EnvironmentSectionIndex)
            {
                ExitLibraryMode();
                ExitSceneMode();
                // There is exactly one environment, so range and toggle mean
                // nothing here: the header is a plain Select, never a modified
                // one.
                _selection.Select(EnvironmentSelection);
            }
        };
        _vm.OnSpawn = anchor =>
            OnSpawnBrowserRequested?.Invoke(anchor, SpawnBrowserTab.All);
        _vm.OnRowClicked = OnRowClicked;
        _vm.OnRowExpandToggled = row =>
        {
            // Disclosure is a structural change: the sidebar's row set is
            // rebuilt on the next frame because of this bump.
            _expandVersion++;
            // A merged category/bone row (e.g. the Root bone standing in for
            // the Root category) carries a selection Tag plus an ExpandKey.
            if (row.ExpandKey is { } expandKey && !_collapsedNodes.Add(expandKey))
                _collapsedNodes.Remove(expandKey);
            else if (row.ExpandKey == null && row.Tag is string key && !_collapsedNodes.Add(key))
                _collapsedNodes.Remove(key);
            else if (row.ExpandKey == null &&
                row.Tag is SelectionId { Kind: SceneEntityKind.Actor, Actor: { } rowActor })
            {
                var akey = "actor:" + rowActor.LogicalId;
                if (!_collapsedNodes.Add(akey)) _collapsedNodes.Remove(akey);
            }
        };
        _vm.OnSidebarResize = w => _sidebarWidth = w;
        _vm.OnRowContextMenu = row =>
        {
            if (row.Tag is SelectionId { Kind: SceneEntityKind.Actor, Actor: { } ctxActor })
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
            else if (row.OverlayBones != null)
            {
                _ctxOverlayBones = row.OverlayBones;
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
            if (_animation.IsPaused(actor))
                _animation.Resume(actor);
            else
                _animation.Pause(actor);
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
        _vm.OnLightVisibility = row =>
        {
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
        // The lock's inline seat, the live toggle's neighbour: protect or
        // release the shot without selecting the camera first.
        _vm.OnCameraLock = row =>
        {
            if (row.Tag is not SelectionId
                { Kind: SceneEntityKind.Camera, Camera: { } lockCameraId })
                return;
            var resolved = _bindings.Resolve(lockCameraId);
            if (!resolved.Success ||
                resolved.Value is not { IsValid: true } camera)
                return;
            camera.IsLocked = !camera.IsLocked;
            row.CameraLocked = camera.IsLocked;
        };
        _vm.OnOverlayVisibility = row =>
        {
            if (row.OverlayBones is not { } bones)
                return;
            _overlayPresentation.SetVisible(
                bones, !_overlayPresentation.AreVisible(bones));
        };
        _vm.IsOverlayVisible = _overlayPresentation.AreVisible;
        _vm.DrawContent = DrawTabContent;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        // ONE width for the whole shell: every tab keeps the inspector
        // rail, so navigating can never move the frame. Only collapse and
        // restore write Size. Split parts release their width: the floor
        // follows what is actually attached this frame.
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

        // The detach toggle's one-frame reseat: width sheds or regains the
        // sidebar column while the LEFT edge moves the same amount, so the
        // content and the inspector hold their screen position.
        if (_detachShift != 0 && !_collapsed)
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
        ImGui.PushStyleColor(ImGuiCol.Button, Crystarium.ActiveTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Crystarium.ActiveTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Crystarium.ActiveTheme.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Crystarium.ActiveTheme.SurfaceSunken);
        ImGui.PushStyleColor(ImGuiCol.Header, Crystarium.ActiveTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Crystarium.ActiveTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Crystarium.ActiveTheme.AccentActive);

        // The shell IS the window chrome — the ImGui window must contribute
        // nothing; the retained shell owns its padding and borders.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * ImGuiHelpers.GlobalScale);
    }

    private bool _restorePending;
    private float _lastWidth = DefaultWidth;
    private float _lastHeight = DefaultHeight;

    private static WindowSizeConstraints ExpandedSizeConstraints(float minimumWidth)
        => new()
        {
            MinimumSize = new Vector2(minimumWidth, MinHeight),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

    /// <summary>The width floor for what is attached THIS frame: the shared
    /// 1110px covers sidebar + content + rail; detached mode hands the
    /// sidebar's column back and keeps the rail.</summary>
    private float EffectiveMinimumWidth()
    {
        float minimum = MinimumWidth;
        if (Config.ConfigurationService.Instance.Config.UI.DetachedShell)
            minimum -= Crystarium.ActiveTheme.Shell.SidebarDefaultWidth;
        return minimum;
    }

    /// <summary>The window rect as of the last drawn frame — the detach
    /// orchestration reads it to seat the split windows where their parts
    /// stood.</summary>
    internal Vector2 LastPosition => _lastPosition;
    internal float LastWidth => _lastWidth;
    internal float LastHeight => _lastHeight;
    internal float LastSidebarWidth => _sidebarWidth;

    /// <summary>+1 detaching (shrink right past the departing sidebar), -1
    /// merging (grow back left). Applied for one frame by PreDraw so the
    /// CONTENT and the inspector hold their screen position through the
    /// toggle.</summary>
    internal void ApplyDetachShift(int direction) => _detachShift = direction;

    private int _detachShift;
    private bool _shiftApplied;
    private Vector2 _lastPosition;

    /// <summary>Detached mode only: the Inspector window closed FROM THE
    /// TOOLBAR (or its own X). The window object stays open — it still
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
    private string TitleEntity(SelectionId? primary)
    {
        if (_libraryMode)
            return "Library";
        if (_sceneMode)
            return "Shot";
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
            { Kind: SceneEntityKind.Camera } => "Camera",
            _ => "Poser",
        };
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
        ReconcilePendingSpawn();
        BuildViewModel();
        // Hidden Inspector: the frame still built everything the parts read,
        // and the menu/dialog pumps below still run — only the chassis and
        // its content stay undrawn.
        if (!_contentHidden)
            AppShellView.Draw(
                _vm, ImGui.GetWindowPos(), ImGui.GetWindowSize());
        DrawShellMenu();
        DrawLightMenu();
        DrawCameraMenu();
        DrawActorContextMenu();
        // Window-level: the attach picker outlives the context menu that
        // opened it.
        _companions.DrawPicker();
        // The expression row is drawn on the FACE surface (the pose rail and
        // the Expression workspace tab), which exists on every tab; its picker
        // is therefore pumped at the shell. A no-op on the frames the
        // animation pane already drew the surface for its own rows.
        _animationPane.DrawExpressionPicker();
        DrawBoneContextMenu();
        DrawOverlayContextMenu();
        DrawLightContextMenu();
        DrawCameraContextMenu();
        DrawRenameModal();
        DrawEntityRenameModal();
        // Both file-dialog pumps live at the shell, so a dialog opened from a
        // tab or a context menu survives whatever the user does to that
        // surface next.
        _appearancePane.DrawBrowsers();
        _lightPane.DrawBrowsers();
        _cameraPane.DrawBrowsers();
        _poseFileSection.DrawBrowsers();
        _scenePane.DrawBrowsers();
        // Unconditional, exactly like the dialog pumps: a library spawn binds
        // its actor frames later, and leaving library mode must not strand it.
        _libraryPane.Tick();
    }

    /// <summary>Puts the workspace into library mode. Openers only — a second
    /// request must not toggle a library the user is already looking at. The
    /// selection CLEARS: library and scene selection are exclusive (user
    /// 2026-08-09) — row clicks already exit the library, and entering it now
    /// releases the scene the same way.</summary>
    public void ShowLibrary()
    {
        ExitSceneMode();
        _libraryMode = true;
        _selection.Clear();
        // Both switches can happen from a sidebar click, which occurs while
        // AppShellView is already drawing: the viewport contract moves in the
        // same breath as the content selection, so the remainder of the frame
        // cannot render one mode through the other mode's layout path.
        ApplyTabLayout("Library");
    }

    private void ExitLibraryMode()
    {
        if (!_libraryMode)
            return;
        _libraryMode = false;
        _libraryPane.OnHidden();
        ApplyTabLayout(_activeTab);
    }

    /// <summary>Puts the workspace into shot mode. Openers only, exactly like
    /// the library's: a second request must not toggle a shot workspace the
    /// user is already looking at. The two modes are alternatives, so entering
    /// this one leaves the library.</summary>
    public void ShowSceneFiles()
    {
        ExitLibraryMode();
        _sceneMode = true;
        _scenePane.OnShown();
        ApplyTabLayout(SceneTabLabel);
    }

    private void ExitSceneMode()
    {
        if (!_sceneMode)
            return;
        _sceneMode = false;
        ApplyTabLayout(_activeTab);
    }

    public override void PostDraw()
    {
        ImGui.PopStyleVar(3);
        ImGui.PopStyleColor(11);
        base.PostDraw();
    }

    // ── view-model assembly (once per frame) ─────────────────────────────

    private void BuildViewModel()
    {
        var primary = _selection.Primary;

        _vm.GPoseActive = _gPoseService.IsGPosing;
        _vm.SidebarWidthPx = _sidebarWidth;
        _vm.Collapsed = _collapsed;
        _vm.Detached =
            Config.ConfigurationService.Instance.Config.UI.DetachedShell;
        _vm.TitleEntity = TitleEntity(primary);
        // The shell's retained per-row state is swept on structural change
        // only: an identical rescan publishes no new revision, so hover and
        // interaction identity survive every refresh that changed nothing.
        _vm.SceneRevision = _scene.Revision;
        // The inspector rail stays on BOTH tabs: bone selection and posing
        // remain available while animation plays, so the right column is
        // never reclaimed and the window width never depends on the tab.
        //
        // Pose owns a fixed outer viewport. Matrix scrolls only inside that
        // allocation; its nested ScrollRegion consumes the same physical
        // gutter the shell reserved, so mode changes cannot alter width.
        // Animation is a document and uses the shell's scroll.
        // Appearance has no pose rail; its content takes the released
        // width. The outer window size is untouched by tab changes.
        // Library mode's rail hosts the import options (user placement);
        // every other mode keeps the selection-typed rail.
        //
        // The delegate is stated even while collapsed: the shell's own
        // titlebar guard ignores it then, but a SPLIT inspector window keeps
        // hosting the rail through a collapse of the main window.
        _vm.DrawRail = _libraryMode
            ? _poseFileSection.DrawOptionsRail
            : _poseRail.Draw;

        _vm.GizmoOperation = (int)_editorState.TransformTool;
        _vm.GizmoSpace = (int)_editorState.TransformOrientation;
        _vm.RotationPivot = (int)_editorState.RotationPivot;
        _vm.SymmetryMode = (int)_editorState.SymmetryMode;
        // The pivot selector appears only where pivot choice changes the
        // active transform meaning: Rotate tool with a resolvable bone
        // selection. Parent needs a valid parent on the effective primary.
        // Both facts come from the shared resolver, which builds a dictionary
        // of the selected actor's WHOLE bone set — so they are re-derived only
        // when the resolver's own two inputs move. The tool is not part of that
        // key: it decides whether the facts are SHOWN, not what they are.
        RefreshPivotFacts();
        bool boneRotate = _editorState.TransformTool == TransformTool.Rotate &&
            _pivotPrimaryIsBone;
        _vm.RotationPivotEnabled = boneRotate;
        _vm.RotationPivotParentAvailable = boneRotate && _pivotParentAvailable;
        var toolbarActor = SelectedActorId();
        _vm.AnimationAvailable = toolbarActor is { } animActorId
            && _animation.IsSupported(animActorId);
        // The switch's polarity is "animation playing": ON unless Poser holds
        // a zero speed override on the selected actor.
        _vm.AnimationOn = toolbarActor is not { } animActor
            || _animation.OverridesFor(animActor).OverallSpeed is not 0f;
        // The freeze is one PROCESS-GLOBAL code patch, so the switch shows
        // the global state: a scene frozen by any actor's request reads
        // frozen from every selection, never "simulating" merely because
        // the selected actor wasn't the one who asked. It is live under
        // EVERY selection and under none, because nothing about the patch is
        // per-actor.
        _vm.PhysicsOn = !_animation.IsPhysicsFrozen;
        _vm.SkeletonOverlayOn = GetSkeletonOverlayOn?.Invoke() ?? false;
        _vm.CanUndo = _cleanTransforms.CanUndo;
        _vm.CanRedo = _cleanTransforms.CanRedo;
        // Pop-out follows the toolbar actor: any selection that resolves to
        // an actor can be frozen into its own content window.
        _vm.ShowPopOut = toolbarActor != null && !_libraryMode && !_sceneMode;
        // Entity creation has two entry points by design (approved shell): the
        // titlebar action and the ACTORS header. Both open the SAME surface,
        // the spawn browser (the LIGHTS and CAMERAS header pluses are the
        // exceptions: each makes its own kind at the pointer). References
        // stay absent (not disabled) in the browser until their runtime
        // entity type exists.
        _vm.ShowSpawn = true;
        _vm.ShowProject = false;

        BuildSidebar(primary);
        BuildTabs(primary);
        ApplyTabLayout(
            _libraryMode ? "Library"
            : _sceneMode ? SceneTabLabel
            : _activeTab);
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
    /// BOTH (the generations in the ids and the published revision), so the
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
            selected, _scene.Snapshot);
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
    /// resolution depends on selection ORDER (the first entry is the primary),
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
    /// Restates the sidebar. The row TREE is assembled only when the gate below
    /// flips; every other frame walks the retained rows and refreshes the flags
    /// that read live state, allocating nothing.
    ///
    /// <para>The gate is exactly the inputs that can change the row COUNT or
    /// ORDER: the published scene revision (the structural signature — actor
    /// set and generations, slot presence, bone counts), the search filter, and
    /// the disclosure version. Selection, actor visibility, pause state and
    /// library mode are per-row FLAGS: they are refreshed in place, so they
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
            _sidebarExpandVersion != _expandVersion ||
            !string.Equals(_sidebarFilter, filter, StringComparison.Ordinal))
        {
            _sidebarBuilt = true;
            // Cleared BEFORE the walk, so a transition that lands mid-rebuild
            // re-arms rather than being swallowed by the rebuild it raced.
            _gazeDirty = false;
            _sidebarRevision = _scene.Revision;
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
        // The library is a place in the sidebar, not a window: its header IS
        // the affordance, and it stands above the scene it poses.
        _vm.Sections.Add(_librarySection);
        // The environment stands above the actors: it is the one scene entity
        // that is always there, and — being a singleton — its HEADER is the
        // affordance, so the section carries no rows at all.
        _vm.Sections.Add(_environmentSection);
        _vm.Sections.Add(_actorsSection);
        // Lights stand under the actors they light; cameras close the list,
        // looking at everything above them.
        _vm.Sections.Add(_propsSection);
        _vm.Sections.Add(_lightsSection);
        _vm.Sections.Add(_camerasSection);
        _actorsSection.Rows.Clear();
        _propsSection.Rows.Clear();
        _lightsSection.Rows.Clear();
        _camerasSection.Rows.Clear();
        _actorRows.Clear();

        bool filtering = filter.Length > 0;

        var actors = _actorsSection;
        var snapshot = _scene.Snapshot.Actors;
        foreach (var actor in snapshot)
        {
            // An attached companion is drawn inside its owner's subtree; one
            // whose owner left the scene falls back to a root of its own.
            if (actor.OwnerActor is { } owner && ContainsActor(snapshot, owner))
                continue;
            AddActorRows(
                actors, actor, snapshot, filter, filtering,
                0, RootTreeLines, true);
        }

        // Props are flat like lights: one row per spawned prop, the header's
        // plus makes another, and the eye seat toggles draw visibility.
        foreach (var prop in _scene.Snapshot.Props)
        {
            if (filtering && !MatchesSidebarFilter(filter, prop.Name))
                continue;
            _propsSection.Rows.Add(new ShellSidebarRow
            {
                Label = prop.Name,
                Count = "",
                Icon = TablerIcon.Diamond,
                Tag = SelectionId.ForProp(prop.Id),
                LightActions = true,
                LightOn = prop.Visible,
            });
        }

        // Lights are flat: a spawned light owns nothing beneath it, so the
        // section is one row per light and the header's plus makes another.
        // A light's name, kind and on-state all participate in the scene
        // signature, so this walk sits behind the same gate as the tree.
        foreach (var light in _scene.Snapshot.Lights)
        {
            if (filtering && !MatchesSidebarFilter(filter, light.Name))
                continue;
            var lightSelectionId = SelectionId.ForLight(light.Id);
            _lightsSection.Rows.Add(new ShellSidebarRow
            {
                Label = light.Name,
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
                Tag = lightSelectionId,
                LightActions = true,
                LightOn = light.IsOn,
            });
        }

        // Cameras are flat like lights: one row per camera, the header's plus
        // makes another, and the row's one action makes it the live camera.
        foreach (var camera in _scene.Snapshot.Cameras)
        {
            if (filtering && !MatchesSidebarFilter(filter, camera.Name))
                continue;
            var cameraSelectionId = SelectionId.ForCamera(camera.Id);
            _camerasSection.Rows.Add(new ShellSidebarRow
            {
                Label = camera.Name,
                // The badge slot marks the session's own camera: the one
                // that cannot be destroyed and that live falls back to.
                Count = camera.IsDefault ? "Default" : "",
                Icon = camera.Kind == CameraKind.Free
                    ? TablerIcon.Video
                    : TablerIcon.Camera,
                Tag = cameraSelectionId,
                CameraActions = true,
                CameraLive = camera.IsLive,
            });
        }
    }

    /// <summary>The mark for one light KIND, shared by the sidebar rows and
    /// the LIGHTS header's type chooser: a kind means the same thing wherever
    /// it is shown, so it is drawn from one place.</summary>
    private static TablerIcon KindIcon(LightKind kind) => kind switch
    {
        LightKind.Directional => TablerIcon.Sun,
        LightKind.Point => TablerIcon.Bulb,
        LightKind.Area => TablerIcon.LightPanel,
        _ => TablerIcon.Spotlight,
    };

    /// <summary>
    /// The warm frame's entire sidebar cost: the retained rows' live flags.
    /// Nothing is created and no string is built — a display name that really
    /// changed re-arms the rebuild gate, and only while a filter is active,
    /// where the name decides whether the row is listed at all.
    /// </summary>
    private void RefreshSidebarFlags()
    {
        _librarySection.Active = _libraryMode;
        // The environment's header wears the selection, exactly as the row it
        // replaced did: both selectable headers state their own flag here.
        _environmentSection.Active = _selection.IsSelected(EnvironmentSelection);
        // Without the native lighting signatures a spawn is a silent no-op, so
        // the header's plus is absent rather than inert. The answer is a field
        // read, so it is restated here rather than gated.
        _lightsSection.ShowPlus = _lightingService.IsAvailable;
        // The camera plus follows the same rule, plus the GPose gate: virtual
        // cameras only exist inside a GPose session.
        _camerasSection.ShowPlus =
            _cameraService.IsAvailable && _gPoseService.IsGPosing;

        var cameraRows = _camerasSection.Rows;
        for (int i = 0; i < cameraRows.Count; i++)
        {
            var cameraRow = cameraRows[i];
            if (cameraRow.Tag is not SelectionId cameraSelection)
                continue;
            cameraRow.Active = _selection.IsSelected(cameraSelection);
            // The live and lock marks read the LIVE camera, not the
            // descriptor: the switch moves the scene signature, and waiting
            // for the republish would leave the glyphs behind the click.
            if (cameraSelection.Camera is { } rowCameraId &&
                _bindings.Resolve(rowCameraId) is
                    { Success: true, Value: { } liveCamera })
            {
                cameraRow.CameraLive = liveCamera.IsLive;
                cameraRow.CameraLocked = liveCamera.IsLocked;
            }
        }

        var propRows = _propsSection.Rows;
        for (int i = 0; i < propRows.Count; i++)
        {
            var propRow = propRows[i];
            if (propRow.Tag is not SelectionId propSelection)
                continue;
            propRow.Active = _selection.IsSelected(propSelection);
            // The eye reads the LIVE handle: visibility moves the scene
            // signature, and waiting for the republish would leave the glyph
            // behind the click that flipped it.
            if (propSelection.Prop is { } propId &&
                _bindings.Resolve(propId) is { Success: true, Value: { } prop })
                propRow.LightOn = prop.Visible;
        }

        var lightRows = _lightsSection.Rows;
        for (int i = 0; i < lightRows.Count; i++)
        {
            var lightRow = lightRows[i];
            if (lightRow.Tag is not SelectionId lightSelection)
                continue;
            lightRow.Active = _selection.IsSelected(lightSelection);
            // The eye reads the LIVE light, not the descriptor: IsOn moves the
            // scene signature, and waiting for that republish would leave the
            // glyph a frame or more behind the click that flipped it.
            if (lightSelection.Light is { } lightId &&
                _bindings.Resolve(lightId) is { Success: true, Value: { } light })
                lightRow.LightOn = light.IsOn;
        }

        var rows = _actorsSection.Rows;
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            // Category rows carry a string tag and own no selection state.
            if (row.Tag is SelectionId id)
                row.Active = _selection.IsSelected(id);
        }

        // The GAME's target, once per frame: its row's crosshair stands at
        // full opacity while every other actor's fades — the live camera's
        // treatment (user 2026-08-11).
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
            row.ActorPaused = _animation.IsPaused(state.Id);
            row.ActorTargeted = targetLineage == state.Id.LogicalId;

            string label = Config.ConfigurationService.Instance.GetDisplayName(
                state.Id.LogicalId, state.RawName);
            if (string.Equals(label, row.Label, StringComparison.Ordinal))
                continue;
            row.Label = label;
            // A rename can change what the filter matches, so the row SET has
            // to be derived again; unfiltered, the new label IS the whole
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

    /// <summary>
    /// One actor's subtree: owned companions first, then bone categories, then
    /// auxiliary slots. Depth and trunk flags are inherited, so an attached
    /// companion draws the same tree one level in and keeps its own subtree.
    /// </summary>
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
        var actorKey = "actor:" + actor.Id.LogicalId;
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
        // Category names are the Ktisis tree's — the same labels the rows
        // below will wear.
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
            ActorActions = true,
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

        // The gaze anchor is a child of the ACTOR, not of any skeleton: it
        // exists exactly while the gaze is a fixed world point, and it
        // stands above everything else because it is the one child the
        // world gizmo can grab. An actor that no longer resolves has no
        // live gaze to read, so it contributes no row.
        if (_bindings.Resolve(actor.Id) is { Success: true, Value: { } gazeActor } &&
            _gazeService.GetGazeState(gazeActor).Mode == GazeTargetMode.Position)
        {
            bool gazeLast = !companionsFollow && !categoriesFollow && !auxFollows;
            // Unlike actors and categories, this key is NOT seeded into
            // _collapsedNodes when it is first seen: a key the set does not
            // hold is an EXPANDED key, so the three aim points stand open
            // the moment the gaze becomes a world point. Only an explicit
            // chevron click puts the key in, and it survives from there.
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

        // The actor folds DIRECTLY into bone categories (no skeleton
        // node). The category set and its NESTING are Ktisis' own tree,
        // verbatim (user 2026-08-11); bones the tree does not claim close
        // the list under Other.
        if (categoriesFollow)
        {
            // Ordinals record the skeleton's own enumeration order: bones
            // list flat inside their category in THAT order, as Ktisis'
            // BindBones sorts by bone index.
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
            // Ktisis schema — keeps a home.
            var leftovers = new List<BoneDescriptor>();
            foreach (var (bone, _) in byName.Values)
                if (!claimed.Contains(bone.Id.CanonicalName)
                    && (!filtering || MatchesSidebarFilter(
                        filter, bone.DisplayName, bone.Id.CanonicalName)))
                    leftovers.Add(bone);
            if (leftovers.Count > 0)
                built.Add(new BuiltCategory(
                    "Other", "Other", leftovers, leftovers, []));

            // Ktisis' shape: ONE Skeleton node under the actor hosts the
            // categories, and its eye shows or hides the whole skeleton in
            // the overlay (user 2026-08-11) — the armature toggle's
            // replacement, per actor.
            if (built.Count > 0)
            {
                var skeletonKey = actorKey + "/skeleton";
                _knownCategoryNodes.Add(skeletonKey);
                bool skeletonExpanded =
                    filtering || !_collapsedNodes.Contains(skeletonKey);
                bool skeletonLast = !auxFollows;
                var allBoneIds = new BoneId[byName.Count];
                int i = 0;
                foreach (var (bone, _) in byName.Values)
                    allBoneIds[i++] = bone.Id;
                section.Rows.Add(new ShellSidebarRow
                {
                    Label = "Skeleton",
                    Count = "",
                    Icon = TablerIcon.Armature,
                    ForceIcon = true,
                    Depth = depth + 1,
                    HasChildren = true,
                    Expanded = skeletonExpanded,
                    IsLastChild = skeletonLast,
                    TreeLines = childLines,
                    Tag = skeletonKey,
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

    /// <summary>Every Ktisis category label, flattened once, for the filter
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

    /// <summary>One Ktisis category, pruned to what THIS skeleton carries and
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
        return new BuiltCategory(
            category.Id, category.Label, visible, all, children);
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

    /// <summary>Strips the redundant "IVCS " lead from a bone label shown
    /// under an IVCS category — the ancestry already says it (user
    /// 2026-08-11).</summary>
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

        // When a category contains a bone whose display name IS the category
        // name (Head → j_head "Head"), the two rows are redundant: the bone
        // becomes the category row. Its body selects the bone (Tag) while
        // its chevron toggles the category (ExpandKey).
        var mergedBone = category.AllBones.Find(
            bone => bone.DisplayName == category.Label);
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
                && _selection.IsSelected(SelectionId.ForBone(mergedBone.Id)),
            Tag = mergedBone != null
                ? SelectionId.ForBone(mergedBone.Id)
                : catKey,
            ExpandKey = mergedBone != null ? catKey : null,
            OverlayBones = overlayBones.ToArray(),
        });
        if (!expanded)
            return;

        var childLines = Descend(lines ?? [], isLast);
        var bones = mergedBone == null
            ? category.VisibleBones
            : category.VisibleBones.FindAll(
                bone => !bone.Id.Equals(mergedBone.Id));

        // Ktisis' own ordering, read from PoseBuilder: GROUPS sort before
        // bones (SkeletonNode.OrderByPriority), and bones bind FLAT in
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
    /// REAL parent/child bone hierarchy. Group rows are navigation-only;
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
                Tag = slotKey,
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
                // Every disclosure seeds COLLAPSED, hierarchy nodes included.
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

    /// <summary>Extended/IVCS bones are DISPLAY-suppressed while
    /// Display.ShowNsfwBones is off. Read live per build: the snapshot's own
    /// IsHidden and every selection path are untouched.</summary>
    private static bool IsBoneSuppressed(BoneDescriptor bone)
        => !Config.ConfigurationService.Instance.Config.Display.ShowNsfwBones
            && Core.BoneInfo.BoneInfoService.IsNsfw(bone.Id.CanonicalName);

    /// <summary>Nickname, else the anonymous mask when enabled, else the
    /// cleaned snapshot name — one stable-id display API for every surface,
    /// the pop-out windows included.</summary>
    internal static string ActorDisplayName(ActorDescriptor actor)
        => Config.ConfigurationService.Instance.GetDisplayName(
            actor.Id.LogicalId, DisplayName(actor.Name));

    /// <summary>Strips the raw object-index suffix ("Name (201)") for display.</summary>
    internal static string DisplayName(string name)
        => System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");

    private void BuildTabs(SelectionId? primary)
    {
        // Tabs are rebuilt each frame; the active one is preserved so a
        // selection change cannot silently throw the user back to Pose.
        _vm.Tabs.Clear();
        if (_libraryMode)
        {
            // The library types are the tabs; _activeTab is left untouched,
            // so leaving the library returns the tab the user was on.
            int type = _libraryPane.SelectedType;
            for (int i = 0; i < _libraryTabs.Length; i++)
            {
                _libraryTabs[i].Active = i == type;
                _vm.Tabs.Add(_libraryTabs[i]);
            }
            return;
        }
        if (_sceneMode)
        {
            // One tab: the shot workspace is a single page, and the strip is
            // what states the mode the user is in.
            _sceneTabs[0].Active = true;
            _vm.Tabs.Add(_sceneTabs[0]);
            return;
        }
        // The strip is a function of the SELECTION TYPE: the environment's
        // tabs are its own, a light's are its own, and nothing else shares
        // either — neither entity has a pose, an animation or an appearance.
        var (tabs, strip) = primary switch
        {
            { Kind: SceneEntityKind.Environment } => (_environmentTabs, "environment"),
            { Kind: SceneEntityKind.Light } => (_lightTabs, "light"),
            { Kind: SceneEntityKind.Camera } => (_cameraTabs, "camera"),
            { Kind: SceneEntityKind.Prop } => (_propTabs, "prop"),
            // Creatures share the actor strip: their skeleton poses, their
            // battle-chara body animates, and the Appearance pane hides the
            // humanoid-only sections itself.
            _ => (_selectionTabs, "actor"),
        };
        // Same-labeled tabs on DIFFERENT strips are different places: the
        // strip key joins the scroll identity in ApplyTabLayout, which runs
        // right after this method on the per-frame path.
        _activeStrip = strip;
        // The active tab is preserved WITHIN a strip, so a selection change
        // inside the actor set cannot silently throw the user back to Pose; a
        // strip that does not carry it falls to that strip's first tab.
        bool carried = false;
        for (int i = 0; i < tabs.Length; i++)
            carried |= tabs[i].Label == _activeTab;
        if (!carried)
            _activeTab = tabs[0].Label;
        for (int i = 0; i < tabs.Length; i++)
        {
            tabs[i].Active = tabs[i].Label == _activeTab;
            _vm.Tabs.Add(tabs[i]);
        }
    }

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
        // The bone total moves only with the scene's structure or with WHICH
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

    private void OnTabClicked(int index)
    {
        // In library mode the tabs are the library types; the selection-typed
        // tab set is untouched underneath.
        if (_libraryMode)
        {
            _libraryPane.SelectType(index);
            return;
        }
        // The shot workspace has one tab: clicking it is already where it goes.
        if (_sceneMode)
            return;
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
        // Scroll identity is per STRIP and TAB (audit R1): one shared id
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
        // is the same either way. WHICH pane draws it is decided by the
        // selection in DrawTabContent, never by this label.
        _vm.ContentUsesPage =
            tab is "Animation" or "Appearance" or "Prop" or "Light"
                or "Shadows"
                or "Camera"
                or "Weather" or "Sky" or "Atmosphere" or "World";
    }

    private void OnRowClicked(ShellSidebarRow row)
    {
        // Selecting anything in the scene is leaving the library or the shot
        // workspace: they are alternatives in one workspace.
        ExitLibraryMode();
        ExitSceneMode();
        if (row.Tag is string catKey2)
        {
            if (!_collapsedNodes.Add(catKey2)) _collapsedNodes.Remove(catKey2);
            _expandVersion++;
            return;
        }

        if (row.Tag is not SelectionId id) return;

        var io = ImGui.GetIO();
        if (io.KeyShift && _selection.Anchor is { } anchor)
        {
            // Range order follows the rows currently visible to the user;
            // collapsed and filtered-out entries are deliberately excluded.
            var displayOrder = new List<SelectionId>();
            foreach (var section in _vm.Sections)
                foreach (var visibleRow in section.Rows)
                    if (visibleRow.Tag is SelectionId visibleId)
                        displayOrder.Add(visibleId);
            _selection.SelectRange(anchor, id, displayOrder);
        }
        else if (io.KeyCtrl)
        {
            _selection.Toggle(id);
        }
        else
        {
            _selection.Select(id);
        }
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
        // The library is browsable without a resolvable actor — the apply
        // action is what needs one — so it precedes the GPose gate.
        if (_libraryMode)
        {
            _libraryPane.Draw(origin, size);
            return;
        }

        // The shot workspace precedes the GPose gate for the same reason the
        // library does: recovering a shot file is browsable out of GPose, and
        // the workflow itself refuses the operation without a live session.
        if (_sceneMode)
        {
            _scenePane.Draw(origin, size);
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

        // The prop tab stands only while a prop is selected — the label is
        // unique across every strip, so it is the whole dispatch, exactly
        // like the camera's.
        if (_activeTab == "Prop")
        {
            _propsPane.Draw(origin, size);
            return;
        }

        // The environment is answered by the SELECTION, not by the label: it
        // and a light both name a "Light" tab, and only the selected entity
        // says which pane that tab belongs to. Its strip is its own five tabs,
        // so every one of them lands here.
        if (_selection.Primary is { Kind: SceneEntityKind.Environment })
        {
            _environmentPane.Draw(origin, size, EnvironmentTabFor(_activeTab));
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

        if (_activeTab == "Shadows")
        {
            _lightPane.DrawShadows(origin, size);
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

    /// <summary>The environment strip's label as the pane's page identity.
    /// Positional against <see cref="_environmentTabs"/>; an unrecognised label
    /// falls to the strip's first tab, which is where BuildTabs would have put
    /// the user anyway.</summary>
    private static EnvironmentTab EnvironmentTabFor(string tab) => tab switch
    {
        "Sky" => EnvironmentTab.Sky,
        "Light" => EnvironmentTab.Light,
        "Atmosphere" => EnvironmentTab.Atmosphere,
        "World" => EnvironmentTab.World,
        _ => EnvironmentTab.Weather,
    };

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
    private readonly CompanionSection _companions;

    /// <summary>Selects a freshly spawned actor so the thing just created
    /// is the thing being edited. The scene has not rescanned yet, so the
    /// id is resolved on the next refresh rather than here.</summary>
    private void SelectSpawned(IActor? spawned)
    {
        if (spawned == null)
            return;
        _pendingSelectSpawned = spawned;
    }

    private IActor? _pendingSelectSpawned;
    private ILight? _pendingSelectSpawnedLight;

    /// <summary>Spawns one light of the chosen kind and arms it for selection.
    /// </summary>
    private void SpawnLight(LightKind kind)
    {
        if (_lifecycle.SpawnLight(kind) is { } spawned)
            _pendingSelectSpawnedLight = spawned;
    }

    /// <summary>The LIGHTS header's chooser, positional against
    /// <see cref="LightMenuKinds"/>. Retained: the rows carry no per-frame
    /// data, so a warm frame restates nothing.</summary>
    private static readonly ContextMenuItem[] LightMenuItems =
    [
        new("New spot light", TablerIcon.Spotlight),
        new("New point light", TablerIcon.Bulb),
        new("New area light", TablerIcon.LightPanel),
        new("New directional light", TablerIcon.Sun),
    ];

    private static readonly LightKind[] LightMenuKinds =
    [
        LightKind.Spot,
        LightKind.Point,
        LightKind.Area,
        LightKind.Directional,
    ];

    private bool _lightMenuOpenRequested;

    /// <summary>The LIGHTS header's plus: a light has four kinds and the kind
    /// decides which native is created, so the choice is asked for before the
    /// light exists rather than corrected on the Light tab afterwards.
    /// </summary>
    private void DrawLightMenu()
    {
        if (_lightMenuOpenRequested)
        {
            _lightMenuOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##lights-add", ImGui.GetMousePos(), LightMenuItems);
        }
        int clicked = Crystarium.FloatingMenu.Draw("##lights-add");
        if (clicked >= 0 && clicked < LightMenuKinds.Length)
            SpawnLight(LightMenuKinds[clicked]);
    }

    /// <summary>The CAMERAS header's chooser, positional against the switch
    /// in <see cref="DrawCameraMenu"/>. Retained like the light menu's rows.
    /// </summary>
    private static readonly ContextMenuItem[] CameraMenuItems =
    [
        new("New camera", TablerIcon.Camera),
        new("New free camera", TablerIcon.Video),
        new("New camera from file…", TablerIcon.Download),
    ];

    private bool _cameraMenuOpenRequested;

    /// <summary>The CAMERAS header's plus: a camera has two kinds and the
    /// kind decides how it drives the game view, so the choice is asked for
    /// before the camera exists — Brio's "New…" menu, the lights' idiom.
    /// </summary>
    private void DrawCameraMenu()
    {
        if (_cameraMenuOpenRequested)
        {
            _cameraMenuOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##cameras-add", ImGui.GetMousePos(), CameraMenuItems);
        }
        switch (Crystarium.FloatingMenu.Draw("##cameras-add"))
        {
            case 0:
                CreateCamera(Domain.Scene.CameraKind.Game);
                break;
            case 1:
                CreateCamera(Domain.Scene.CameraKind.Free);
                break;
            case 2:
                _cameraPane.OpenLoad();
                break;
        }
    }

    /// <summary>Creates one camera of the chosen kind and arms it for
    /// selection once the scene refresh has bound it.</summary>
    private void CreateCamera(Domain.Scene.CameraKind kind)
    {
        if (_lifecycle.CreateCamera(kind) is { } created)
            _cameraPane.SelectWhenBound(created);
    }

    /// <summary>Second half of <see cref="SelectSpawned"/> and
    /// <see cref="SpawnLight"/>: once the scene refresh has bound the new
    /// entity, select it and forget it.</summary>
    private void ReconcilePendingSpawn()
    {
        if (_pendingSelectSpawnedLight is { } spawnedLight &&
            _bindings.GetLightId(spawnedLight) is { } lightId)
        {
            _selection.Select(SelectionId.ForLight(lightId));
            _pendingSelectSpawnedLight = null;
        }

        if (_pendingSelectSpawned is not { } spawned)
            return;
        if (_bindings.GetActorId(spawned) is not { } id)
            return;
        _selection.Select(SelectionId.ForActor(id));
        _pendingSelectSpawned = null;
    }

    /// <summary>
    /// The shell's GROWABLE COMMAND LIST. Almost every action Poser offers is
    /// meant to land here eventually, so that a collapsed bottom-bar-only
    /// layout can still reach everything the chrome stops showing. One command
    /// is therefore ONE member here, ONE row in <see cref="BuildShellMenu"/>
    /// and ONE case in <see cref="InvokeShellCommand"/> — all three keyed by
    /// this member, never by a loose index. A separator is a member with a row
    /// and no case.
    /// </summary>
    private enum ShellCommand
    {
        ShowLibrary,
        ShowShot,
        SpawnActor,
        ImportPose,
        ExportPose,
        AutoSaves,
        LayoutSeparator,
        PopOutContent,
        ToggleDetached,
        SceneWindow,
        InspectorWindow,
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
            // A short command list, not a context menu: the shell menu takes the
            // width its own rows need rather than the canonical 260px surface.
            Crystarium.FloatingMenu.Open(
                "##shell-burger-menu",
                _shellMenuAnchor,
                _shellMenuItems,
                Crystarium.FloatingMenu.MeasureWidth(_shellMenuItems));
        }
        int clicked = Crystarium.FloatingMenu.Draw("##shell-burger-menu");
        if (clicked >= 0 && clicked < _shellMenuItems.Length)
            InvokeShellCommand((ShellCommand)clicked);
    }

    /// <summary>
    /// Restates the command rows into the retained array. The only per-frame
    /// work is the gate itself; the rows are rewritten when — and only when —
    /// a gate actually flips, so a warm frame writes nothing.
    /// </summary>
    private void BuildShellMenu()
    {
        // The pose-file commands follow the SELECTED actor: a shell-wide menu
        // has no right-clicked row to take a skeleton from. Same gate the actor
        // context menu applies to the same three commands.
        bool poseTarget = SelectedSkeleton() != null;
        var uiConfig = Config.ConfigurationService.Instance.Config.UI;
        bool sceneOpen = GetSceneWindowOpen?.Invoke() ?? true;
        int layoutState = (uiConfig.DetachedShell ? 1 : 0)
            | (sceneOpen ? 2 : 0)
            | (_contentHidden ? 4 : 0);
        if (_shellMenuRowsBuilt
            && poseTarget == _shellMenuPoseTarget
            && layoutState == _shellMenuLayoutState)
            return;
        _shellMenuRowsBuilt = true;
        _shellMenuPoseTarget = poseTarget;
        _shellMenuLayoutState = layoutState;

        _shellMenuItems[(int)ShellCommand.ShowLibrary] =
            new ContextMenuItem("Show library", TablerIcon.Photo);
        // The whole shot: one command, because save, load, progress and
        // recovery all live on the one page it opens.
        _shellMenuItems[(int)ShellCommand.ShowShot] =
            new ContextMenuItem("Save or load a shot", TablerIcon.Movie);
        _shellMenuItems[(int)ShellCommand.SpawnActor] =
            new ContextMenuItem("Spawn actor", TablerIcon.UserPlus);
        _shellMenuItems[(int)ShellCommand.ImportPose] =
            new ContextMenuItem(
                "Import pose", TablerIcon.Download, disabled: !poseTarget);
        _shellMenuItems[(int)ShellCommand.ExportPose] =
            new ContextMenuItem(
                "Export pose", TablerIcon.DeviceFloppy, disabled: !poseTarget);
        _shellMenuItems[(int)ShellCommand.AutoSaves] =
            new ContextMenuItem(
                "Auto-saves", TablerIcon.ArrowBackUp, disabled: !poseTarget);
        _shellMenuItems[(int)ShellCommand.LayoutSeparator] =
            ContextMenuItem.Separator;
        _shellMenuItems[(int)ShellCommand.PopOutContent] =
            new ContextMenuItem(
                "Pop out content", TablerIcon.ArrowsDiagonal,
                disabled: !poseTarget);
        _shellMenuItems[(int)ShellCommand.ToggleDetached] =
            new ContextMenuItem(
                uiConfig.DetachedShell ? "Merge the UI" : "Detach the UI",
                TablerIcon.LayoutPanel);
        // Detached mode's window roster: windows close and reopen from this
        // menu — the strip is the always-there surface carrying it.
        _shellMenuItems[(int)ShellCommand.SceneWindow] =
            new ContextMenuItem(
                sceneOpen ? "Close Scene window" : "Open Scene window",
                TablerIcon.LayoutPanel,
                disabled: !uiConfig.DetachedShell);
        _shellMenuItems[(int)ShellCommand.InspectorWindow] =
            new ContextMenuItem(
                _contentHidden
                    ? "Open Inspector window"
                    : "Close Inspector window",
                TablerIcon.Monitor,
                disabled: !uiConfig.DetachedShell);
        _shellMenuItems[(int)ShellCommand.SettingsSeparator] =
            ContextMenuItem.Separator;
        _shellMenuItems[(int)ShellCommand.OpenSettings] =
            new ContextMenuItem("Open settings", TablerIcon.Settings);
    }

    /// <summary>The ONE layout toggle. The window set orchestrates it — the
    /// flag flip, the part placement, this window's reseat — so the request
    /// only travels.</summary>
    public event Action? OnDetachToggleRequested;

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
            case ShellCommand.ShowShot:
                ShowSceneFiles();
                break;
            case ShellCommand.SpawnActor:
                // Reached FROM the burger menu, so the burger's own anchor is
                // this surface's seat too: the pointer is on a menu row that
                // is about to vanish.
                OnSpawnBrowserRequested?.Invoke(
                    _shellMenuAnchor, SpawnBrowserTab.All);
                break;
            // Import/Export open the Brio menus — the ONE import and export
            // surface; the file dialogs live inside them.
            case ShellCommand.ImportPose:
                if (SelectedSkeleton() != null)
                    _poseFileSection.RequestImportMenu(withPresets: true);
                break;
            case ShellCommand.ExportPose:
                if (SelectedSkeleton() != null)
                    _poseFileSection.RequestExportMenu();
                break;
            case ShellCommand.AutoSaves:
                if (SelectedSkeleton() is { } recoverSkeleton)
                    _poseFileSection.OpenAutoSaves(recoverSkeleton);
                break;
            case ShellCommand.PopOutContent:
                if (SelectedActorId() is { } popOut)
                    OnPopOutRequested?.Invoke(popOut);
                break;
            case ShellCommand.ToggleDetached:
                RequestDetachToggle();
                break;
            case ShellCommand.SceneWindow:
                OnSceneWindowToggleRequested?.Invoke();
                break;
            case ShellCommand.InspectorWindow:
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

        var items = new List<ContextMenuItem>
        {
            new("Set game target", TablerIcon.Crosshair),
            new(!_spawnService.IsVisible(actor) ? "Show" : "Hide", !_spawnService.IsVisible(actor) ? TablerIcon.Eye : TablerIcon.EyeOff),
            // The icon carries the VERB the row performs: resume wears play,
            // pause wears pause (user 2026-08-11).
            new(_animation.IsPaused(actorId) ? "Resume animation" : "Pause animation",
                _animation.IsPaused(actorId)
                    ? TablerIcon.PlayerPlay
                    : TablerIcon.PlayerPause),
            new("Rename", TablerIcon.Edit),
            new("Clone", TablerIcon.Stack2),
            ContextMenuItem.Separator,
            // The companion slot exists for riding a mount or carrying an
            // ornament — standalone creatures come from the spawn browser —
            // so its two verbs live here, out of every pane.
            new("Attach companion", TablerIcon.Paw,
                disabled: !_spawnService.HasCompanionSlot(actor),
                help: _spawnService.HasCompanionSlot(actor)
                    ? "Attach a minion, mount or ornament to this actor"
                    : "Only actors spawned with a companion slot can attach one"),
            new("Detach companion", TablerIcon.X,
                disabled: _spawnService.GetCompanionInfo(actor) is null),
        };
        var actions = new List<Action?>
        {
            () => _actorManager.SetGPoseTarget(actor),
            () => _spawnService.SetVisibility(actor, !_spawnService.IsVisible(actor)),
            () =>
            {
                if (_animation.IsPaused(actorId))
                    _animation.Resume(actorId);
                else
                    _animation.Pause(actorId);
            },
            () =>
            {
                _renameTarget = actorId;
                // Seeds what the UI SHOWS — nickname, else the mask while
                // anonymous mode is on. Prefilling the raw name would leak it.
                _renameValue = Config.ConfigurationService.Instance.GetDisplayName(
                    actorId.LogicalId, DisplayName(actor.Name));
                _renameOpen = true;
            },
            () =>
            {
                var clone = _lifecycle.SpawnActor(
                    $"Clone actor '{DisplayName(actor.Name)}'",
                    () => _spawnService.CloneActor(actor));
                if (clone != null && _bindings.GetActorId(clone) is { } cloneId)
                    _selection.Select(SelectionId.ForActor(cloneId));
            },
            null, // separator
            () =>
            {
                _companionCatalog.EnsureLoaded();
                _companions.OpenAttachPicker(actorId);
            },
            () => _spawnService.DestroyCompanion(actor),
        };

        // Pose files belong to the actor, not to whatever is selected, so the
        // actor itself is where they are reachable.
        items.Add(ContextMenuItem.Separator);
        items.Add(new ContextMenuItem(
            "Import pose", TablerIcon.Download, disabled: !actor.HasSkeleton));
        items.Add(new ContextMenuItem(
            "Export pose", TablerIcon.DeviceFloppy,
            disabled: !actor.HasSkeleton));
        items.Add(new ContextMenuItem(
            "Stash pose", TablerIcon.ArrowDown, disabled: !actor.HasSkeleton,
            help: "Save this actor's pose so you can apply it to another actor. Replaces whatever was stashed before."));
        items.Add(new ContextMenuItem(
            "Apply stashed pose", TablerIcon.ArrowBackUp,
            disabled: !actor.HasSkeleton || !_cleanPose.HasStash,
            help: _cleanPose.HasStash
                ? $"Apply the stashed pose to this actor. Stashed from {_cleanPose.StashedFrom} at {_cleanPose.StashedAt:HH:mm:ss} UTC."
                : "Nothing stashed yet"));
        actions.Add(null); // separator
        // Both rows open the Brio menus — the ONE import/export surface;
        // the file dialogs (and the actor-side presets) live inside them.
        actions.Add(() => _poseFileSection.RequestImportMenu(withPresets: true));
        actions.Add(() => _poseFileSection.RequestExportMenu());
        actions.Add(() => _cleanPose.Stash(
            actor,
            Config.ConfigurationService.Instance.GetDisplayName(
                actorId.LogicalId, DisplayName(actor.Name))));
        actions.Add(() => _cleanPose.ApplyStash(actor));

        if (_spawnService.IsSpawnedActor(actor))
        {
            items.Add(ContextMenuItem.Separator);
            items.Add(new ContextMenuItem("Despawn", TablerIcon.Trash, danger: true));
            actions.Add(null);
            actions.Add(() =>
            {
                _spawnService.DestroyActor(actor);
                _selection.Clear();
            });
        }

        if (_ctxOpenRequested)
        {
            _ctxOpenRequested = false;
            Crystarium.FloatingMenu.Open("##actor-ctx", ImGui.GetMousePos(), items.ToArray());
        }
        int clicked = Crystarium.FloatingMenu.Draw("##actor-ctx");
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
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
            new ContextMenuItem("Select parent", TablerIcon.ArrowUp, disabled: descriptor.Parent == null),
            new ContextMenuItem("Select children", TablerIcon.Sitemap, disabled: !hasChildren),
            new ContextMenuItem("Select mirrored bone", TablerIcon.ArrowsMove, disabled: mirror == null),
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
            Crystarium.FloatingMenu.Dismiss("##overlay-ctx");
            return;
        }
        bool visible = _overlayPresentation.AreVisible(bones);
        var items = new[]
        {
            new ContextMenuItem(
                visible ? "Hide category from overlay" : "Show category in overlay",
                visible ? TablerIcon.EyeOff : TablerIcon.Eye),
            new ContextMenuItem("Show only this category", TablerIcon.Crosshair),
            new ContextMenuItem("Show all categories", TablerIcon.Eye),
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
                _overlayPresentation.SetVisible(bones, !visible);
                break;
            case 1:
                _overlayPresentation.SetVisible(ownerBones, false);
                _overlayPresentation.SetVisible(bones, true);
                break;
            case 2:
                _overlayPresentation.SetVisible(ownerBones, true);
                break;
        }
    }

    // ── light / camera context menus ────────────────────────────────────

    private LightId? _ctxLightId;
    private bool _lightCtxOpenRequested;
    private CameraId? _ctxCameraId;
    private bool _cameraCtxOpenRequested;

    /// <summary>The entity rename modal's state: lights and cameras carry
    /// their name ON the entity, so one modal writes whichever apply hook the
    /// opening menu handed it — unlike the actor modal, which writes a
    /// nickname beside a name the game owns.</summary>
    private bool _entityRenameOpen;
    private string _entityRenameValue = "";
    private string _entityRenameTitle = "";
    private Action<string>? _entityRenameApply;

    /// <summary>Right-click light menu: the lifetime verbs the actor menu
    /// gives its rows, spoken in the light's vocabulary — the eye, the file,
    /// and the ownership-aware destroy/release the ACTIONS section makes.
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
            new("Clone", TablerIcon.Stack2),
            new("Save to file…", TablerIcon.DeviceFloppy),
            ContextMenuItem.Separator,
        };
        var actions = new List<Action?>
        {
            () => light.IsOn = !light.IsOn,
            () => OpenEntityRename(
                "Rename light", light.Name, next => light.Name = next),
            () => _lifecycle.CloneLight(light),
            () => _lightPane.OpenSave(light),
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

    /// <summary>Right-click camera menu: look-through and lock — the two
    /// verbs worth reaching without selecting — then the same lifetime set
    /// the light menu speaks. The default camera cannot be destroyed.
    /// </summary>
    private void DrawCameraContextMenu()
    {
        if (_ctxCameraId is not { } cameraId)
            return;
        var resolved = _bindings.Resolve(cameraId);
        if (!resolved.Success ||
            resolved.Value is not { IsValid: true } camera)
        {
            _ctxCameraId = null;
            Crystarium.FloatingMenu.Dismiss("##camera-ctx");
            return;
        }

        var items = new List<ContextMenuItem>
        {
            new(camera.IsLive
                    ? "Return to main camera"
                    : "Look through", TablerIcon.Video,
                disabled: camera.IsLive && camera.IsDefault),
            new(camera.IsLocked ? "Unlock" : "Lock",
                camera.IsLocked ? TablerIcon.LockOpen : TablerIcon.Lock),
            new("Rename", TablerIcon.Edit, disabled: camera.IsLocked),
            new("Clone", TablerIcon.Stack2),
            new("Save to file…", TablerIcon.DeviceFloppy),
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
            () => OpenEntityRename(
                "Rename camera", camera.Name, next => camera.Name = next),
            () =>
            {
                if (_lifecycle.CloneCamera(camera) is { } clone)
                    _cameraPane.SelectWhenBound(clone);
            },
            () => _cameraPane.OpenSave(camera),
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

    private void OpenEntityRename(
        string title, string current, Action<string> apply)
    {
        _entityRenameTitle = title;
        _entityRenameValue = current;
        _entityRenameApply = apply;
        _entityRenameOpen = true;
    }

    /// <summary>The light/camera rename modal. The apply hook captured the
    /// live entity at open; a stale entity write is a no-op on an invalid
    /// native, exactly as the pane's own name row would be.</summary>
    private void DrawEntityRenameModal()
    {
        if (!_entityRenameOpen || _entityRenameApply is not { } apply)
            return;
        Crystarium.Modal(
            "##rename-entity",
            _entityRenameOpen,
            next => _entityRenameOpen = next,
            _entityRenameTitle,
            () =>
        {
            Crystarium.TextInput(
                "##rename-entity-input", _entityRenameValue,
                next => _entityRenameValue = next);
            ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
            if (Crystarium.Button(
                    "Save",
                    variant: ButtonVariant.Primary,
                    id: "rename-entity-save"))
            {
                if (_entityRenameValue.Trim() is { Length: > 0 } trimmed)
                    apply(trimmed);
                _entityRenameOpen = false;
            }
            ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
            if (Crystarium.Button("Cancel", id: "rename-entity-cancel"))
                _entityRenameOpen = false;
        });
    }

    private void DrawRenameModal()
    {
        if (!_renameOpen || _renameTarget is not { } target) return;
        Crystarium.Modal(
            "##rename-actor",
            _renameOpen,
            next => _renameOpen = next,
            "Rename actor",
            () =>
        {
            Crystarium.TextInput(
                "##rename-input", _renameValue, next => _renameValue = next);
            ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
            if (Crystarium.Button(
                    "Save",
                    variant: ButtonVariant.Primary,
                    id: "rename-save"))
            {
                Config.ConfigurationService.Instance.SetNickname(target.LogicalId, _renameValue);
                _renameOpen = false;
            }
            ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
            if (Crystarium.Button("Clear", id: "rename-clear",
                help: "Remove the nickname and show the real name"))
            {
                Config.ConfigurationService.Instance.SetNickname(target.LogicalId, null);
                _renameOpen = false;
            }
        });
    }

}
