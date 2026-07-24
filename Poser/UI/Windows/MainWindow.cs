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
    private const float MinimumWidthWithInspector = 1110f;
    private const float MinimumWidthWithoutInspector =
        MinimumWidthWithInspector - AppShellView.RailWidth;
    private const float DefaultWidth = MinimumWidthWithInspector + 50f;
    private const float DefaultHeight = 660f;
    private const float MinHeight = 520f;

    private readonly IGPoseService _gPoseService;
    private readonly IActorManager _actorManager;
    private readonly IActorSpawnService _spawnService;
    private readonly SceneSession _scene;
    private readonly SelectionSession _selection;
    private readonly StableBindingRegistry _bindings;

    // actor context menu + rename modal: stable ids only; the lifetime
    // services still take legacy actors, so ids resolve per frame through the
    // binding registry and the pointer never persists in UI state.
    private ActorId? _ctxActorId;
    private bool _ctxOpenRequested;
    private BoneId? _ctxBoneId;
    private bool _boneCtxOpenRequested;
    private bool _renameOpen;
    private string _renameValue = "";
    private ActorId? _renameTarget;
    private readonly IEditorState _editorState;
    private readonly CleanTransformFacade _cleanTransforms;
    private readonly CleanPoseFacade _cleanPose;
    private readonly IBonePosingService _bonePosingService;

    // M2 panes on the verified grammar (Migrations #7-#11).
    private readonly PoseInspectorPane _poseInspector;
    private readonly PoseRailPane _poseRail;
    private bool _collapsed;
    private float _savedHeight = DefaultHeight;
    private bool _inspectorWasVisible = true;
    private readonly HashSet<string> _collapsedNodes = new();
    private readonly HashSet<string> _knownCategoryNodes = new();
    private readonly HashSet<string> _knownActorNodes = new();
    // The bone whose actor/category were last revealed by an external
    // selection; reveal fires only when the primary bone changes.
    private Domain.Identity.BoneId? _lastRevealedBone;
    private float _sidebarWidth = 280f;
    private readonly AppShellViewModel _vm = new();
    private string _activeTab = "Pose";

    /// <summary>Reports whether the skeleton overlay window is open (titlebar toggle state).</summary>
    public Func<bool>? GetSkeletonOverlayOn { get; set; }

    /// <summary>Raised when the titlebar skeleton-overlay toggle is clicked.</summary>
    public event Action<bool>? OnSkeletonOverlayToggled;

    public event Action? OnSettingsRequested;

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
        PoseRailPane poseRail,
        GraphicalBonePane graphicalBonePane)
        : base($"{PluginConstants.PluginName}###poser_main_window",
            ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoCollapse |
            ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse |
            ImGuiWindowFlags.NoBackground)
    {
        Size = new Vector2(DefaultWidth, DefaultHeight);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = ExpandedSizeConstraints(inspectorVisible: true);

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
        _poseInspector.DrawMapInline = graphicalBonePane.DrawInline;
        _poseInspector.DescriptorDisplayName = ActorDisplayName;
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
        _vm.OnLinked = on => _bonePosingService.LinkedBonesEnabled = on;
        _vm.OnUndo = Undo;
        _vm.OnRedo = Redo;
        _vm.OnSkeletonOverlay = on => OnSkeletonOverlayToggled?.Invoke(on);
        _vm.OnSettings = () => OnSettingsRequested?.Invoke();
        _vm.OnHideUi = () => IsOpen = false;
        _vm.OnSelectTarget = () =>
        {
            if (_actorManager.GetGPoseTarget() is { } target &&
                _bindings.GetActorId(target) is { } targetId)
                _selection.Select(SelectionId.ForActor(targetId));
        };
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
                _boneCtxOpenRequested = true;
            }
        };
        _vm.DrawContent = DrawTabContent;
    }

    public override void PreDraw()
    {
        base.PreDraw();

        bool inspectorVisible = HasInspectorForActiveTab();
        float minimumWidth = MinimumWidthFor(inspectorVisible);

        // A collapsed shell overrides only the vertical constraint so ImGui
        // cannot hold it open at the normal 520 px minimum.
        SizeConstraints = _collapsed
            ? new WindowSizeConstraints
            {
                MinimumSize = new Vector2(minimumWidth, AppShellView.TitlebarHeight),
                MaximumSize = new Vector2(float.MaxValue, AppShellView.TitlebarHeight),
            }
            : ExpandedSizeConstraints(inspectorVisible);

        // Preserve the editor width across inspector transitions. Constraints
        // alone can grow a window but never know when to shrink it again.
        float? inspectorTransitionWidth = null;
        if (!_collapsed && inspectorVisible != _inspectorWasVisible)
        {
            float delta = inspectorVisible
                ? AppShellView.RailWidth
                : -AppShellView.RailWidth;
            inspectorTransitionWidth = Math.Max(minimumWidth, _lastWidth + delta);
        }
        _inspectorWasVisible = inspectorVisible;

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
        else if (inspectorTransitionWidth is { } transitionWidth)
        {
            Size = new Vector2(transitionWidth, _lastHeight);
            SizeCondition = ImGuiCond.Always;
        }
        else
        {
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        // The shell draws its own chassis; keep child regions transparent and
        // give the hosted legacy panes the themed widget colors they expect.
        ImGui.PushStyleColor(ImGuiCol.ChildBg, Vector4.Zero);
        ImGui.PushStyleColor(ImGuiCol.Text, Norvrandt.Sheet.CurrentTheme.Text);
        ImGui.PushStyleColor(ImGuiCol.TextDisabled, Norvrandt.Sheet.CurrentTheme.TextDim);
        ImGui.PushStyleColor(ImGuiCol.Border, Norvrandt.Sheet.CurrentTheme.Border);
        ImGui.PushStyleColor(ImGuiCol.Button, Norvrandt.Sheet.CurrentTheme.SurfaceRaised);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Norvrandt.Sheet.CurrentTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Norvrandt.Sheet.CurrentTheme.AccentActive);
        ImGui.PushStyleColor(ImGuiCol.FrameBg, Norvrandt.Sheet.CurrentTheme.SurfaceSunken);
        ImGui.PushStyleColor(ImGuiCol.Header, Norvrandt.Sheet.CurrentTheme.Accent);
        ImGui.PushStyleColor(ImGuiCol.HeaderHovered, Norvrandt.Sheet.CurrentTheme.AccentHover);
        ImGui.PushStyleColor(ImGuiCol.HeaderActive, Norvrandt.Sheet.CurrentTheme.AccentActive);

        // The shell IS the window chrome — the ImGui window must contribute
        // nothing; the retained shell owns its padding and borders.
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 10f * ImGuiHelpers.GlobalScale);
    }

    private bool _restorePending;
    private float _lastWidth = DefaultWidth;
    private float _lastHeight = DefaultHeight;

    private bool HasInspectorForActiveTab()
        => _activeTab == "Pose";

    private static float MinimumWidthFor(bool inspectorVisible)
        => inspectorVisible
            ? MinimumWidthWithInspector
            : MinimumWidthWithoutInspector;

    private static WindowSizeConstraints ExpandedSizeConstraints(bool inspectorVisible)
        => new()
        {
            MinimumSize = new Vector2(MinimumWidthFor(inspectorVisible), MinHeight),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

    public override void Draw()
    {
        float gs = ImGuiHelpers.GlobalScale;
        _lastWidth = ImGui.GetWindowSize().X / gs;
        _lastHeight = ImGui.GetWindowSize().Y / gs;
        BuildViewModel();
        AppShellView.Draw(_vm, ImGui.GetWindowPos(), ImGui.GetWindowSize());
        DrawActorContextMenu();
        DrawBoneContextMenu();
        DrawRenameModal();

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
        _vm.ContentOwnsViewport = _activeTab == "Pose";
        _vm.DrawRail = _collapsed ? null : _poseRail.Draw;
        _vm.GizmoOperation = (int)_editorState.TransformTool;
        _vm.GizmoSpace = (int)_editorState.TransformOrientation;
        _vm.RotationPivot = (int)_editorState.RotationPivot;
        _vm.SymmetryMode = (int)_editorState.SymmetryMode;
        _vm.LinkedOn = _bonePosingService.LinkedBonesEnabled;
        // The pivot selector appears only where pivot choice changes the
        // active transform meaning: Rotate tool with a resolvable bone
        // selection. Parent needs a valid parent on the effective primary.
        var effective = Application.Transforms.TransformTargetResolver.Resolve(
            _selection.Selected, _scene.Snapshot);
        bool boneRotate = _editorState.TransformTool == TransformTool.Rotate &&
            effective is { Primary.Kind: Domain.Identity.TransformTargetKind.Bone };
        _vm.ShowRotationPivot = boneRotate;
        _vm.RotationPivotParentAvailable = false;
        if (boneRotate &&
            effective!.Primary.Bone is { } effectiveBone)
        {
            foreach (var actor in _scene.Snapshot.Actors)
            {
                if (actor.Id.LogicalId != effectiveBone.Skeleton.Actor.LogicalId ||
                    actor.Skeleton is not { } skeleton)
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
        _vm.SkeletonOverlayOn = GetSkeletonOverlayOn?.Invoke() ?? false;
        _vm.CanUndo = _cleanTransforms.CanUndo;
        _vm.CanRedo = _cleanTransforms.CanRedo;
        _vm.ShowPopOut = false;
        _vm.ShowSpawn = false;
        _vm.ShowProject = false;

        BuildSidebar(primary);
        BuildTabs(primary);
        BuildCrumbAndStatus(primary);
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
        BoneId? selectedBone = primary is { Kind: SceneEntityKind.Bone, Bone: { } primaryBone }
            ? primaryBone
            : null;
        // External bone selection reveals its actor and category exactly once,
        // at the moment the primary bone changes. Re-forcing the reveal every
        // frame would undo a user's collapse while the selection is unchanged.
        bool revealNow = selectedBone is { } newBone &&
            !(_lastRevealedBone is { } lastBone && lastBone.Equals(newBone));
        _lastRevealedBone = selectedBone;
        string filter = _vm.SidebarSearch.Trim();
        bool filtering = filter.Length > 0;

        var actors = new ShellSidebarSection { Title = "ACTORS", ShowPlus = false };
        foreach (var actor in _scene.Snapshot.Actors)
        {
            var actorKey = "actor:" + actor.Id.LogicalId;
            string actorLabel = ActorDisplayName(actor);

            var groups = new List<(Core.BoneInfo.BoneCategory Cat, List<BoneDescriptor> Bones)>();
            var skeleton = actor.Skeleton;
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

            bool actorMatches = MatchesSidebarFilter(filter, actorLabel, actor.Name);
            bool hasMatchingBone = groups.Exists(group =>
                MatchesSidebarFilter(filter, Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(group.Cat), group.Cat.ToString())
                || group.Bones.Exists(bone => MatchesSidebarFilter(filter, bone.DisplayName, bone.Id.CanonicalName)));
            if (filtering && !actorMatches && !hasMatchingBone)
                continue;

            bool ownsSelectedBone = selectedBone is { } ownedBone &&
                ownedBone.Skeleton.Actor.LogicalId == actor.Id.LogicalId;
            // Actor roots first appear collapsed; lineage keys survive
            // refreshes, so a scene refresh cannot reset existing disclosure.
            if (_knownActorNodes.Add(actorKey) && !(revealNow && ownsSelectedBone))
                _collapsedNodes.Add(actorKey);
            if (revealNow && ownsSelectedBone)
                _collapsedNodes.Remove(actorKey);
            bool expanded = filtering || !_collapsedNodes.Contains(actorKey);
            var actorSelectionId = SelectionId.ForActor(actor.Id);
            actors.Rows.Add(new ShellSidebarRow
            {
                Label = actorLabel,
                Count = actor.IsHidden ? "hidden" : ActorCount(actor),
                Icon = actor.IsCompanion ? TablerIcon.Paw : TablerIcon.User,
                // The disclosure affordance is permanent; an unresolved
                // skeleton only disables it until the snapshot exposes bones.
                HasChildren = true,
                ExpanderDisabled = skeleton == null,
                Expanded = expanded,
                Active = _selection.IsSelected(actorSelectionId),
                Tag = actorSelectionId,
            });

            // M11: the actor folds DIRECTLY into bone categories (no skeleton
            // node), categories into bones. Category set = curated grouping;
            // the Ktisis-definitions toggle swaps the set once its data lands.
            if (expanded && skeleton != null && (!filtering || hasMatchingBone))
            {
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
                    bool containsSelectedBone = selectedBone is { } revealBone &&
                        allBones.Exists(bone => bone.Id.Equals(revealBone));
                    if (_knownCategoryNodes.Add(catKey) && !(revealNow && containsSelectedBone))
                        _collapsedNodes.Add(catKey);
                    if (revealNow && containsSelectedBone)
                        _collapsedNodes.Remove(catKey);
                    bool catExpanded = filtering || !_collapsedNodes.Contains(catKey);
                    bool catLast = g == displayedGroups.Count - 1;
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
                            Count = (allBones.Count - 1).ToString(),
                            Depth = 1,
                            HasChildren = true,
                            Expanded = catExpanded,
                            IsLastChild = catLast,
                            Active = _selection.IsSelected(mergedId),
                            Tag = mergedId,
                            ExpandKey = catKey,
                        });
                    }
                    else
                    {
                        actors.Rows.Add(new ShellSidebarRow
                        {
                            Label = categoryLabel,
                            Count = allBones.Count.ToString(),
                            Depth = 1,
                            HasChildren = true,
                            Expanded = catExpanded,
                            IsLastChild = catLast,
                            Tag = catKey,
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
                            // modded/unlocalized bones: DisplayName == canonical — one is enough
                            Count = childBones[b].DisplayName == childBones[b].Id.CanonicalName
                                ? ""
                                : childBones[b].Id.CanonicalName,
                            Depth = 2,
                            IsLastChild = b == childBones.Count - 1,
                            TreeLines = new[] { false, !catLast },
                            Active = _selection.IsSelected(boneSelectionId),
                            Tag = boneSelectionId,
                        });
                    }
                }
            }
        }
        _vm.Sections.Add(actors);
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

    private static string ActorCount(ActorDescriptor actor)
    {
        if (actor.IsCompanion) return "minion";
        if (actor.IsPlayer) return "player";
        return "npc";
    }

    private void BuildTabs(SelectionId? primary)
    {
        _vm.Tabs.Clear();
        _activeTab = "Pose";
        _vm.Tabs.Add(new ShellTab { Label = "Pose", Active = true });
    }

    private void BuildCrumbAndStatus(SelectionId? primary)
    {
        ActorDescriptor? crumbActor = null;
        BoneDescriptor? crumbBone = null;
        if (primary is { Kind: SceneEntityKind.Bone, Bone: { } bone })
        {
            crumbActor = FindActor(bone.Skeleton.Actor.LogicalId);
            crumbBone = crumbActor?.Skeleton?.Bones
                .FirstOrDefault(candidate => candidate.Id.Equals(bone));
        }
        else if (primary is { Kind: SceneEntityKind.Actor, Actor: { } actorId })
        {
            crumbActor = FindActor(actorId.LogicalId);
        }

        if (crumbBone != null && crumbActor != null)
        {
            _vm.CrumbPrefix = $"{ActorDisplayName(crumbActor)} · ";
            _vm.CrumbBold = crumbBone.DisplayName;
        }
        else if (crumbActor != null)
        {
            _vm.CrumbPrefix = "";
            _vm.CrumbBold = ActorDisplayName(crumbActor);
        }
        else
        {
            _vm.CrumbPrefix = "";
            _vm.CrumbBold = "";
        }

        int actorCount = _scene.Snapshot.Actors.Count;
        _vm.StatusLeft = actorCount == 1 ? "1 actor" : $"{actorCount} actors";

        int bones = crumbActor?.Skeleton?.Bones.Count ?? 0;
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
        if (index < 0 || index >= _vm.Tabs.Count) return;
        var label = _vm.Tabs[index].Label;

        _activeTab = label;
    }

    private void OnRowClicked(ShellSidebarRow row)
    {
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

    // ── typed tab content hosted inside the shell ──────────────────────

    private void DrawTabContent(Vector2 origin, Vector2 size)
    {
        if (!_gPoseService.IsGPosing)
        {
            ViewText.Label(origin + new Vector2(0f, 8f) * ImGuiHelpers.GlobalScale,
                "Enter GPose to start posing.", 12f, FontWeight.Regular, new Vector4(1f, 1f, 1f, 0.5f));
            return;
        }

        ImGui.SetCursorScreenPos(origin);

        _poseInspector.SetSelection(_selection.Primary);
        _poseInspector.Draw(origin, size);
    }

    /// <summary>Right-click actor menu: the lifetime actions that were stranded
    /// without a sidebar affordance (target / visibility / rename / clone / companion / despawn).
    /// The menu state is a stable ActorId; the legacy lifetime services still
    /// take live actors, so the id resolves through the binding registry for
    /// the duration of one frame and is dropped when resolution fails.</summary>
    private void DrawActorContextMenu()
    {
        if (_ctxOpenRequested)
        {
            ImGui.OpenPopup("##actor-ctx");
            _ctxOpenRequested = false;
        }
        if (_ctxActorId is not { } actorId) return;
        var resolved = _bindings.Resolve(actorId);
        if (!resolved.Success)
        {
            _ctxActorId = null;
            return;
        }
        var actor = resolved.Value!;

        var items = new List<ContextMenuItem>
        {
            new("Set game target", TablerIcon.Eye),
            new(!_spawnService.IsVisible(actor) ? "Show" : "Hide", !_spawnService.IsVisible(actor) ? TablerIcon.Eye : TablerIcon.EyeOff),
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

        int clicked = Crystarium.ContextMenu("##actor-ctx", items.ToArray());
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
        if (_boneCtxOpenRequested)
        {
            ImGui.OpenPopup("##bone-ctx");
            _boneCtxOpenRequested = false;
        }
        if (_ctxBoneId is not { } boneId)
            return;

        var owner = FindActor(boneId.Skeleton.Actor.LogicalId);
        var bones = owner?.Skeleton?.Bones;
        var descriptor = bones?.FirstOrDefault(candidate => candidate.Id.Equals(boneId));
        if (bones == null || descriptor == null)
        {
            _ctxBoneId = null;
            return;
        }

        var mirrorName = _bonePosingService.GetMirrorBoneName(boneId.CanonicalName);
        var mirror = mirrorName == null
            ? null
            : bones.FirstOrDefault(candidate =>
                candidate.Id.CanonicalName == mirrorName &&
                candidate.Id.PartialId == boneId.PartialId);
        bool hasChildren = bones.Any(candidate => candidate.Parent?.Equals(boneId) == true);

        var items = new[]
        {
            new ContextMenuItem("Select parent", TablerIcon.ArrowUp, disabled: descriptor.Parent == null),
            new ContextMenuItem("Select children", TablerIcon.Sitemap, disabled: !hasChildren),
            new ContextMenuItem("Select mirrored bone", TablerIcon.ArrowsMove, disabled: mirror == null),
            ContextMenuItem.Separator,
            new ContextMenuItem("Flip bone", TablerIcon.Rotate),
            new ContextMenuItem("Reset bone", TablerIcon.Refresh, danger: true),
        };

        int clicked = Crystarium.ContextMenu("##bone-ctx", items);
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
            case 4:
                _cleanPose.FlipBone(
                    TransformTargetId.ForBone(boneId),
                    descriptor.DisplayName);
                break;
            case 5:
                _cleanPose.ResetBone(
                    TransformTargetId.ForBone(boneId),
                    descriptor.DisplayName);
                break;
        }
    }

    private void DrawRenameModal()
    {
        if (!_renameOpen || _renameTarget is not { } target) return;
        Crystarium.Modal("##rename-actor", ref _renameOpen, "Rename actor", () =>
        {
            Crystarium.TextInput("##rename-input", ref _renameValue);
            ImGui.Dummy(new Vector2(0f, 8f * ImGuiHelpers.GlobalScale));
            if (Crystarium.Button("Save", new ButtonProps { Id = "rename-save", Classes = Cls.Primary }))
            {
                Config.ConfigurationService.Instance.SetNickname(target.LogicalId, _renameValue);
                _renameOpen = false;
            }
            ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
            if (Crystarium.Button("Clear", new ButtonProps { Id = "rename-clear",
                Tooltip = "Remove the nickname and show the real name" }))
            {
                Config.ConfigurationService.Instance.SetNickname(target.LogicalId, null);
                _renameOpen = false;
            }
        });
    }

}
