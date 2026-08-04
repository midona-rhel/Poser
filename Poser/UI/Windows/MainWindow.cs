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
    private readonly PoseLibraryPane _libraryPane;
    private readonly PoseFileInspectorSection _poseFileSection;
    private readonly Game.Animation.AnimationCatalogLoader _animationCatalog;
    private readonly PoseRailPane _poseRail;
    private bool _collapsed;
    private float _savedHeight = DefaultHeight;
    private readonly HashSet<string> _collapsedNodes = new();
    private readonly HashSet<string> _knownCategoryNodes = new();
    private readonly HashSet<string> _knownActorNodes = new();
    private float _sidebarWidth = 280f;
    private readonly AppShellViewModel _vm = new();
    private string _activeTab = "Pose";

    /// <summary>The workspace is showing the pose library instead of the
    /// selection's tabs. The SELECTION is untouched — the library applies to
    /// whatever actor was selected before the mode was entered.</summary>
    private bool _libraryMode;

    /// <summary>The library's sidebar section and its one tab, both retained:
    /// they carry no per-frame data, so a warm frame restates them rather than
    /// minting them.</summary>
    private readonly ShellSidebarSection _librarySection = new()
    {
        Title = "LIBRARY",
        Selectable = true,
    };

    private readonly ShellTab _libraryTab = new()
    {
        Label = "Library",
        Active = true,
    };

    /// <summary>The library section is stated first, so its index is fixed.
    /// </summary>
    private const int LibrarySectionIndex = 0;

    /// <summary>Reports whether the skeleton overlay window is open (titlebar toggle state).</summary>
    public Func<bool>? GetSkeletonOverlayOn { get; set; }

    /// <summary>Raised when the titlebar skeleton-overlay toggle is clicked.</summary>
    public event Action<bool>? OnSkeletonOverlayToggled;

    public event Action? OnSettingsRequested;

    /// <summary>Raised by both creation affordances — the titlebar plus and the
    /// ACTORS header plus.</summary>
    public event Action? OnSpawnBrowserRequested;

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
        PoseLibraryPane libraryPane,
        PoseFileInspectorSection poseFileSection,
        Application.Animation.AnimationSession animation,
        Game.Animation.AnimationCatalogLoader animationCatalog,
        PoseRailPane poseRail,
        GraphicalBonePane graphicalBonePane,
        SkeletonOverlayPresentation overlayPresentation)
        : base($"{PluginConstants.PluginName}###poser_main_window",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        Size = new Vector2(DefaultWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = ExpandedSizeConstraints();

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
        _poseInspector = poseInspector;
        _animationPane = animationPane;
        _appearancePane = appearancePane;
        _libraryPane = libraryPane;
        // The library's "Add source…" and its empty state both mean the same
        // thing the titlebar gear does, so they travel the one settings route.
        _libraryPane.OnSettingsRequested += () => OnSettingsRequested?.Invoke();
        _poseFileSection = poseFileSection;
        _animation = animation;
        _overlayPresentation = overlayPresentation;
        _animationCatalog = animationCatalog;
        _poseInspector.DrawMapInline = graphicalBonePane.DrawInline;
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
        // The switch's polarity is "physics simulating"; the service's is
        // "freeze requested".
        _vm.OnPhysics = on =>
        {
            if (SelectedActorId() is { } actor)
                _animation.SetPhysicsFrozen(actor, !on);
        };
        _vm.OnUndo = Undo;
        _vm.OnRedo = Redo;
        _vm.OnSkeletonOverlay = on => OnSkeletonOverlayToggled?.Invoke(on);
        _vm.OnSettings = () => OnSettingsRequested?.Invoke();
        _vm.OnBurger = anchor =>
        {
            _shellMenuAnchor = anchor;
            _shellMenuOpenRequested = true;
        };
        _vm.OnHideUi = () => IsOpen = false;
        // The sidebar's add affordance. Creation lives where the created
        // thing will appear, so the ACTORS
        // header owns it rather than a separate spawn menu.
        _vm.OnSectionPlus = _ => OnSpawnBrowserRequested?.Invoke();
        // Only the LIBRARY header is selectable, so no other index can arrive.
        _vm.OnSectionSelected = index =>
        {
            if (index == LibrarySectionIndex)
                ShowLibrary();
        };
        _vm.OnSpawn = () => OnSpawnBrowserRequested?.Invoke();
        _vm.OnRowClicked = OnRowClicked;
        _vm.OnRowExpandToggled = row =>
        {
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
        // restore write Size.
        SizeConstraints = _collapsed
            ? new WindowSizeConstraints
            {
                MinimumSize = new Vector2(MinimumWidth, AppShellView.TitlebarHeight),
                MaximumSize = new Vector2(float.MaxValue, AppShellView.TitlebarHeight),
            }
            : ExpandedSizeConstraints();

        // Collapse and restore go through the Dalamud window size system;
        // ImGui.SetWindowSize inside Draw loses to it.
        if (_collapsed)
        {
            Size = new Vector2(_lastWidth, AppShellView.TitlebarHeight);
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

        // The shell draws its own chassis; keep child regions transparent and
        // give the hosted legacy panes the themed widget colors they expect.
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

    private static WindowSizeConstraints ExpandedSizeConstraints()
        => new()
        {
            MinimumSize = new Vector2(MinimumWidth, MinHeight),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

    public override void Draw()
    {
        float gs = ImGuiHelpers.GlobalScale;
        _lastWidth = ImGui.GetWindowSize().X / gs;
        _lastHeight = ImGui.GetWindowSize().Y / gs;
        _overlayPresentation.Reconcile(_scene.Snapshot);
        ReconcilePendingSpawn();
        BuildViewModel();
        AppShellView.Draw(_vm, ImGui.GetWindowPos(), ImGui.GetWindowSize());
        DrawShellMenu();
        DrawActorContextMenu();
        DrawBoneContextMenu();
        DrawOverlayContextMenu();
        DrawRenameModal();
        // Both file-dialog pumps live at the shell, so a dialog opened from a
        // tab or a context menu survives whatever the user does to that
        // surface next.
        _appearancePane.DrawBrowsers();
        _poseFileSection.DrawBrowsers();
        // Unconditional, exactly like the dialog pumps: a library spawn binds
        // its actor frames later, and leaving library mode must not strand it.
        _libraryPane.Tick();
    }

    /// <summary>Puts the workspace into library mode. Openers only — a second
    /// request must not toggle a library the user is already looking at — and
    /// the actor selection is deliberately left alone.</summary>
    public void ShowLibrary()
    {
        _libraryMode = true;
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
        _vm.DrawRail = _collapsed ? null : _poseRail.Draw;

        _vm.GizmoOperation = (int)_editorState.TransformTool;
        _vm.GizmoSpace = (int)_editorState.TransformOrientation;
        _vm.RotationPivot = (int)_editorState.RotationPivot;
        _vm.SymmetryMode = (int)_editorState.SymmetryMode;
        // The pivot selector appears only where pivot choice changes the
        // active transform meaning: Rotate tool with a resolvable bone
        // selection. Parent needs a valid parent on the effective primary.
        var effective = Application.Transforms.TransformTargetResolver.Resolve(
            _selection.Selected, _scene.Snapshot);
        bool boneRotate = _editorState.TransformTool == TransformTool.Rotate &&
            effective is { Primary.Kind: Domain.Identity.TransformTargetKind.Bone };
        _vm.RotationPivotEnabled = boneRotate;
        _vm.RotationPivotParentAvailable = false;
        if (boneRotate &&
            effective!.Primary.Bone is { } effectiveBone)
        {
            foreach (var actor in _scene.Snapshot.Actors)
            {
                if (actor.Id.LogicalId != effectiveBone.Skeleton.Actor.LogicalId ||
                    actor.GetSkeleton(effectiveBone.Slot) is not { } skeleton)
                    continue;
                foreach (var bone in skeleton.Bones)
                {
                    if (!bone.Id.Equals(effectiveBone))
                        continue;
                    _vm.RotationPivotParentAvailable = bone.Parent != null;
                    break;
                }
                break;
            }
        }
        var toolbarActor = SelectedActorId();
        _vm.PhysicsAvailable = toolbarActor is { } actorId
            && _animation.IsSupported(actorId);
        // OwnsPhysics means "this actor holds a freeze", so the switch is ON
        // unless the selected actor froze; no actor shows the game default,
        // physics simulating (disabled either way via PhysicsAvailable).
        _vm.PhysicsOn = toolbarActor is not { } physicsActor
            || !_animation.OwnsPhysics(physicsActor);
        _vm.SkeletonOverlayOn = GetSkeletonOverlayOn?.Invoke() ?? false;
        _vm.CanUndo = _cleanTransforms.CanUndo;
        _vm.CanRedo = _cleanTransforms.CanRedo;
        _vm.ShowPopOut = false;
        // Entity creation has two entry points by design (approved shell): the
        // titlebar action and the ACTORS header. Both open the SAME surface,
        // the spawn browser. Cameras, lights and references stay absent (not
        // disabled) there until their runtime entity types exist.
        _vm.ShowSpawn = true;
        _vm.ShowProject = false;

        BuildSidebar(primary);
        BuildTabs(primary);
        ApplyTabLayout(_libraryMode ? "Library" : _activeTab);
        BuildStatus(primary);
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

    private void BuildSidebar(SelectionId? primary)
    {
        _vm.Sections.Clear();
        // The library is a place in the sidebar, not a window: its header IS
        // the affordance, and it stands above the scene it poses.
        _librarySection.Active = _libraryMode;
        _vm.Sections.Add(_librarySection);

        string filter = _vm.SidebarSearch.Trim();
        bool filtering = filter.Length > 0;

        var actors = new ShellSidebarSection { Title = "ACTORS", ShowPlus = true };
        foreach (var actor in _scene.Snapshot.Actors)
        {
            var actorKey = "actor:" + actor.Id.LogicalId;
            string actorLabel = ActorDisplayName(actor);

            var groups = new List<(Core.BoneInfo.BoneCategory Cat, List<BoneDescriptor> Bones)>();
            var skeleton = actor.CharacterSkeleton;
            if (skeleton != null)
            {
                foreach (var bone in skeleton.Bones)
                {
                    if (bone.IsHidden) continue;
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
            bool hasMatchingBone = groups.Exists(group =>
                MatchesSidebarFilter(filter, Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(group.Cat), group.Cat.ToString())
                || group.Bones.Exists(bone => MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName)));
            bool hasMatchingAux = auxSkeletons.Exists(aux =>
                MatchesSidebarFilter(filter, SlotLabel(aux.Id.Slot))
                || aux.Bones.Any(bone => !bone.IsHidden &&
                    MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName)));
            if (filtering && !actorMatches && !hasMatchingBone && !hasMatchingAux)
                continue;

            // Actor roots first appear collapsed; lineage keys survive
            // refreshes, so a scene refresh cannot reset existing disclosure.
            // Only explicit disclosure clicks expand — external bone selection
            // (map, matrix, overlay, gizmo) never changes tree disclosure.
            if (_knownActorNodes.Add(actorKey))
                _collapsedNodes.Add(actorKey);
            bool expanded = filtering || !_collapsedNodes.Contains(actorKey);
            var actorSelectionId = SelectionId.ForActor(actor.Id);
            var resolvedActor = _bindings.Resolve(actor.Id);
            bool actorVisible = resolvedActor.Success
                ? _spawnService.IsVisible(resolvedActor.Value!)
                : !actor.IsHidden;
            actors.Rows.Add(new ShellSidebarRow
            {
                Label = actorLabel,
                Count = "",
                Icon = actor.IsCompanion ? TablerIcon.Paw : TablerIcon.User,
                // The disclosure affordance is permanent; an unresolved
                // skeleton only disables it until the snapshot exposes bones.
                HasChildren = true,
                ExpanderDisabled = skeleton == null,
                Expanded = expanded,
                Active = _selection.IsSelected(actorSelectionId),
                Tag = actorSelectionId,
                ActorActions = true,
                ActorVisible = actorVisible,
                ActorPaused = _animation.IsPaused(actor.Id),
            });

            // The actor folds DIRECTLY into bone categories (no skeleton
            // node), categories into bones. Category set = curated grouping;
            // the Ktisis-definitions toggle swaps the set once its data lands.
            if (expanded && skeleton != null && (!filtering || hasMatchingBone))
            {
                bool auxFollows = auxSkeletons.Count > 0 &&
                    (!filtering || hasMatchingAux);
                var displayedGroups = new List<(Core.BoneInfo.BoneCategory Cat, List<BoneDescriptor> Visible, List<BoneDescriptor> All)>();
                foreach (var (cat, bones) in groups)
                {
                    string categoryLabel = Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(cat);
                    bool categoryMatches = filtering && MatchesSidebarFilter(filter, categoryLabel, cat.ToString());
                    var visibleBones = !filtering || categoryMatches
                        ? bones
                        : bones.FindAll(bone => MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName));
                    if (!filtering || visibleBones.Count > 0)
                        displayedGroups.Add((cat, visibleBones, bones));
                }

                for (int g = 0; g < displayedGroups.Count; g++)
                {
                    var (cat, visibleBones, allBones) = displayedGroups[g];
                    var catKey = actorKey + "/cat:" + cat;
                    if (_knownCategoryNodes.Add(catKey))
                        _collapsedNodes.Add(catKey);
                    bool catExpanded = filtering || !_collapsedNodes.Contains(catKey);
                    bool catLast = g == displayedGroups.Count - 1 && !auxFollows;
                    string categoryLabel = Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(cat);
                    // When a category contains a bone whose display name IS
                    // the category name (Root → n_root "Root"), the two rows
                    // are redundant: the bone becomes the category row. Its
                    // body selects the bone (Tag) while its chevron toggles
                    // the category (ExpandKey) — one Root, not Root > Root.
                    var mergedBone = allBones.Find(bone => bone.DisplayName == categoryLabel);
                    if (mergedBone != null)
                    {
                        var mergedId = SelectionId.ForBone(mergedBone.Id);
                        actors.Rows.Add(new ShellSidebarRow
                        {
                            Label = categoryLabel,
                            Count = "",
                            Depth = 1,
                            HasChildren = true,
                            Expanded = catExpanded,
                            IsLastChild = catLast,
                            Active = _selection.IsSelected(mergedId),
                            Tag = mergedId,
                            ExpandKey = catKey,
                            OverlayBones = allBones.Select(bone => bone.Id).ToArray(),
                        });
                    }
                    else
                    {
                        actors.Rows.Add(new ShellSidebarRow
                        {
                            Label = categoryLabel,
                            Count = "",
                            Depth = 1,
                            HasChildren = true,
                            Expanded = catExpanded,
                            IsLastChild = catLast,
                            Tag = catKey,
                            OverlayBones = allBones.Select(bone => bone.Id).ToArray(),
                        });
                    }
                    if (!catExpanded) continue;
                    var childBones = mergedBone == null
                        ? visibleBones
                        : visibleBones.FindAll(bone => !bone.Id.Equals(mergedBone.Id));
                    for (int b = 0; b < childBones.Count; b++)
                    {
                        var boneSelectionId = SelectionId.ForBone(childBones[b].Id);
                        actors.Rows.Add(new ShellSidebarRow
                        {
                            Label = childBones[b].DisplayName,
                            Count = "",
                            Depth = 2,
                            IsLastChild = b == childBones.Count - 1,
                            TreeLines = new[] { false, !catLast },
                            Active = _selection.IsSelected(boneSelectionId),
                            Tag = boneSelectionId,
                            OverlayBones = new[] { childBones[b].Id },
                        });
                    }
                }
            }

            if (expanded && (!filtering || hasMatchingAux))
                AddAuxiliarySlotGroups(actors, actorKey, auxSkeletons, filter, filtering);
        }
        _vm.Sections.Add(actors);
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
        bool filtering)
    {
        var shown = new List<(SkeletonDescriptor Aux, List<BoneDescriptor> Visible, List<BoneDescriptor> Matching, bool GroupMatches)>();
        foreach (var aux in auxSkeletons)
        {
            var visible = aux.Bones.Where(bone => !bone.IsHidden).ToList();
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
                Depth = 1,
                HasChildren = true,
                Expanded = slotExpanded,
                IsLastChild = groupLast,
                Tag = slotKey,
                OverlayBones = visible.Select(bone => bone.Id).ToArray(),
            });
            if (!slotExpanded)
                continue;

            if (filtering && !groupMatches)
            {
                // Temporary filtered reveal: matching bones flat.
                for (int b = 0; b < matching.Count; b++)
                    section.Rows.Add(BoneRow(
                        matching[b], 2, b == matching.Count - 1,
                        new[] { false, !groupLast }, hasChildren: false,
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

            void Emit(BoneDescriptor bone, int depth, bool isLast, bool[] lines)
            {
                bool hasKids = children.ContainsKey(bone.Id);
                var boneKey = slotKey + "/bone:" + bone.Id.PartialId + ":" + bone.Id.BoneIndex;
                // Every disclosure seeds COLLAPSED, hierarchy nodes included.
                if (hasKids && _knownCategoryNodes.Add(boneKey))
                    _collapsedNodes.Add(boneKey);
                bool boneExpanded = !_collapsedNodes.Contains(boneKey);
                section.Rows.Add(BoneRow(
                    bone, depth, isLast, lines,
                    hasKids, boneExpanded, hasKids ? boneKey : null));
                if (!hasKids || !boneExpanded)
                    return;
                var kids = children[bone.Id];
                var childLines = lines.Append(!isLast).ToArray();
                for (int k = 0; k < kids.Count; k++)
                    Emit(kids[k], depth + 1, k == kids.Count - 1, childLines);
            }

            var rootLines = new[] { false, !groupLast };
            for (int r = 0; r < roots.Count; r++)
                Emit(roots[r], 2, r == roots.Count - 1, rootLines);
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

    /// <summary>Nickname, else the anonymous mask when enabled, else the
    /// cleaned snapshot name — one stable-id display API for every surface.</summary>
    private static string ActorDisplayName(ActorDescriptor actor)
        => Config.ConfigurationService.Instance.GetDisplayName(
            actor.Id.LogicalId, DisplayName(actor.Name));

    /// <summary>Strips the raw object-index suffix ("Name (201)") for display.</summary>
    private static string DisplayName(string name)
        => System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");

    private void BuildTabs(SelectionId? primary)
    {
        // Tabs are rebuilt each frame; the active one is preserved so a
        // selection change cannot silently throw the user back to Pose.
        _vm.Tabs.Clear();
        if (_libraryMode)
        {
            // One tab, and it is the mode itself: _activeTab is left untouched,
            // so leaving the library returns the tab the user was on.
            _vm.Tabs.Add(_libraryTab);
            return;
        }
        if (_activeTab is not ("Pose" or "Animation" or "Appearance"))
            _activeTab = "Pose";
        _vm.Tabs.Add(new ShellTab { Label = "Pose", Active = _activeTab == "Pose" });
        _vm.Tabs.Add(new ShellTab { Label = "Animation", Active = _activeTab == "Animation" });
        _vm.Tabs.Add(new ShellTab { Label = "Appearance", Active = _activeTab == "Appearance" });
    }

    private void BuildStatus(SelectionId? primary)
    {
        ActorDescriptor? selectedActor = null;
        if (primary is { Kind: SceneEntityKind.Bone, Bone: { } bone })
        {
            selectedActor = FindActor(bone.Skeleton.Actor.LogicalId);
        }
        else if (primary is { Kind: SceneEntityKind.Actor, Actor: { } actorId })
        {
            selectedActor = FindActor(actorId.LogicalId);
        }

        int actorCount = _scene.Snapshot.Actors.Count;
        _vm.StatusLeft = actorCount == 1 ? "1 actor" : $"{actorCount} actors";

        int bones = selectedActor?.Skeletons.Sum(s => s.Bones.Count) ?? 0;
        _vm.StatusRight = bones > 0
            ? $"{bones} bones · {ImGui.GetIO().Framerate:0} fps"
            : $"{ImGui.GetIO().Framerate:0} fps";
    }

    private ActorDescriptor? FindActor(Guid lineage)
    {
        foreach (var actor in _scene.Snapshot.Actors)
            if (actor.Id.LogicalId == lineage)
                return actor;
        return null;
    }

    // ── shell callbacks ──────────────────────────────────────────────────

    private void OnTabClicked(int index)
    {
        // Library mode presents its own single tab; clicking it changes
        // nothing, and the selection-typed tab set is untouched underneath.
        if (_libraryMode) return;
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

    private void ApplyTabLayout(string tab)
    {
        // The library scrolls its own grid, so it takes the fixed viewport the
        // Pose tab takes.
        _vm.ContentOwnsViewport = tab is "Pose" or "Library";
        _vm.ContentUsesPage =
            tab is "Animation" or "Appearance";
    }

    private void OnRowClicked(ShellSidebarRow row)
    {
        // Selecting anything in the scene is leaving the library: the two are
        // alternatives in one workspace.
        ExitLibraryMode();
        if (row.Tag is string catKey2)
        {
            if (!_collapsedNodes.Add(catKey2)) _collapsedNodes.Remove(catKey2);
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

        _poseInspector.Draw(origin, size);
    }

    private ActorId? SelectedActorId() =>
        _selection.Primary switch
        {
            { Kind: SceneEntityKind.Actor, Actor: { } actor } => actor,
            { Kind: SceneEntityKind.Bone, Bone: { } bone } =>
                bone.Skeleton.Actor,
            _ => null,
        };

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

    /// <summary>Second half of <see cref="SelectSpawned"/>: once the scene
    /// refresh has bound the new actor, select it and forget it.</summary>
    private void ReconcilePendingSpawn()
    {
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
        SpawnActor,
        ImportPose,
        ExportPose,
        AutoSaves,
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
        if (_shellMenuRowsBuilt && poseTarget == _shellMenuPoseTarget)
            return;
        _shellMenuRowsBuilt = true;
        _shellMenuPoseTarget = poseTarget;

        _shellMenuItems[(int)ShellCommand.ShowLibrary] =
            new ContextMenuItem("Show library", TablerIcon.Photo);
        _shellMenuItems[(int)ShellCommand.SpawnActor] =
            new ContextMenuItem("Spawn actor…", TablerIcon.UserPlus);
        _shellMenuItems[(int)ShellCommand.ImportPose] =
            new ContextMenuItem(
                "Import pose…", TablerIcon.Download, disabled: !poseTarget);
        _shellMenuItems[(int)ShellCommand.ExportPose] =
            new ContextMenuItem(
                "Export pose…", TablerIcon.DeviceFloppy, disabled: !poseTarget);
        _shellMenuItems[(int)ShellCommand.AutoSaves] =
            new ContextMenuItem(
                "Auto-saves…", TablerIcon.ArrowBackUp, disabled: !poseTarget);
        _shellMenuItems[(int)ShellCommand.SettingsSeparator] =
            ContextMenuItem.Separator;
        _shellMenuItems[(int)ShellCommand.OpenSettings] =
            new ContextMenuItem("Open settings", TablerIcon.Settings);
    }

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
            case ShellCommand.SpawnActor:
                OnSpawnBrowserRequested?.Invoke();
                break;
            case ShellCommand.ImportPose:
                if (SelectedSkeleton() is { } importSkeleton)
                    _poseFileSection.OpenImport(importSkeleton);
                break;
            case ShellCommand.ExportPose:
                if (SelectedSkeleton() is { } exportSkeleton)
                    _poseFileSection.OpenExport(exportSkeleton);
                break;
            case ShellCommand.AutoSaves:
                if (SelectedSkeleton() is { } recoverSkeleton)
                    _poseFileSection.OpenAutoSaves(recoverSkeleton);
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
            new(_animation.IsPaused(actorId) ? "Resume animation" : "Pause animation",
                TablerIcon.PlayerPlay),
            new("Rename…", TablerIcon.Edit),
            new("Clone", TablerIcon.Stack2),
            ContextMenuItem.Separator,
            new("Detach companion", TablerIcon.X),
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
                _renameValue = Config.ConfigurationService.Instance.GetNickname(actorId.LogicalId)
                    ?? DisplayName(actor.Name);
                _renameOpen = true;
            },
            () =>
            {
                var clone = _spawnService.CloneActor(actor);
                if (clone != null && _bindings.GetActorId(clone) is { } cloneId)
                    _selection.Select(SelectionId.ForActor(cloneId));
            },
            null, // separator
            () => _spawnService.DestroyCompanion(actor),
        };

        // Pose files belong to the actor, not to whatever is selected, so the
        // actor itself is where they are reachable.
        items.Add(ContextMenuItem.Separator);
        items.Add(new ContextMenuItem(
            "Import pose…", TablerIcon.Download, disabled: !actor.HasSkeleton));
        items.Add(new ContextMenuItem(
            "Export pose…", TablerIcon.DeviceFloppy,
            disabled: !actor.HasSkeleton));
        actions.Add(null); // separator
        actions.Add(() =>
        {
            if (actor.Skeleton is { } importSkeleton)
                _poseFileSection.OpenImport(importSkeleton);
        });
        actions.Add(() =>
        {
            if (actor.Skeleton is { } exportSkeleton)
                _poseFileSection.OpenExport(exportSkeleton);
        });

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
        bool ownerPresent = _scene.Snapshot.Actors.Any(actor =>
            actor.Skeletons.Any(skeleton =>
                skeleton.Bones.Any(candidate =>
                    bones.Contains(candidate.Id))));
        if (!ownerPresent)
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
        };
        if (_overlayCtxOpenRequested)
        {
            _overlayCtxOpenRequested = false;
            Crystarium.FloatingMenu.Open(
                "##overlay-ctx", ImGui.GetMousePos(), items);
        }
        if (Crystarium.FloatingMenu.Draw("##overlay-ctx") == 0)
            _overlayPresentation.SetVisible(bones, !visible);
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
