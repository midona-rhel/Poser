using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Poser.Core;
using Poser.Entities;
using Poser.Game;
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
    private readonly ISkeletonService _skeletonService;
    private readonly IActorSpawnService _spawnService;

    // actor context menu + rename modal (M12: lifetime actions)
    private IActor? _ctxActor;
    private bool _ctxOpenRequested;
    private IBone? _ctxBone;
    private bool _boneCtxOpenRequested;
    private bool _renameOpen;
    private string _renameValue = "";
    private IActor? _renameTarget;
    private readonly ISelectionService _selectionService;
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
        ISkeletonService skeletonService,
        ISelectionService selectionService,
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
        _skeletonService = skeletonService;
        _selectionService = selectionService;
        _editorState = editorState;
        _cleanTransforms = cleanTransforms;
        _cleanPose = cleanPose;
        _bonePosingService = bonePosingService;

        _spawnService = spawnService;
        _poseInspector = poseInspector;
        _poseInspector.DrawMapInline = graphicalBonePane.DrawInline;
        _poseInspector.ActorsProvider = () => _actorManager.Actors;
        _poseInspector.ActorDisplayNameProvider = ActorDisplayName;

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
        _vm.OnUndo = Undo;
        _vm.OnRedo = Redo;
        _vm.OnSkeletonOverlay = on => OnSkeletonOverlayToggled?.Invoke(on);
        _vm.OnSettings = () => OnSettingsRequested?.Invoke();
        _vm.OnHideUi = () => IsOpen = false;
        _vm.OnSelectTarget = () =>
        {
            var target = _actorManager.GetGPoseTarget();
            if (target != null)
                _selectionService.Select(target);
        };
        _vm.OnRowClicked = OnRowClicked;
        _vm.OnRowExpandToggled = row =>
        {
            if (row.Tag is string key && !_collapsedNodes.Add(key))
                _collapsedNodes.Remove(key);
            else if (row.Tag is IActor actor)
            {
                var akey = "actor:" + actor.Id.Unique;
                if (!_collapsedNodes.Add(akey)) _collapsedNodes.Remove(akey);
            }
        };
        _vm.OnSidebarResize = w => _sidebarWidth = w;
        _vm.OnRowContextMenu = row =>
        {
            if (row.Tag is IActor ctxActor)
            {
                _ctxActor = ctxActor;
                _ctxOpenRequested = true;
            }
            else if (row.Tag is IBone ctxBone)
            {
                _ctxBone = ctxBone;
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
        var primary = _selectionService.Primary;

        _vm.GPoseActive = _gPoseService.IsGPosing;
        _vm.SidebarWidthPx = _sidebarWidth;
        _vm.Collapsed = _collapsed;
        _vm.ContentOwnsViewport = _activeTab == "Pose";
        _vm.DrawRail = _collapsed ? null : _poseRail.Draw;
        _vm.GizmoOperation = (int)_editorState.TransformTool;
        _vm.GizmoSpace = (int)_editorState.TransformOrientation;
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

    private void BuildSidebar(IEntity? primary)
    {
        _vm.Sections.Clear();
        var selectedBone = primary as IBone;
        string filter = _vm.SidebarSearch.Trim();
        bool filtering = filter.Length > 0;

        var actors = new ShellSidebarSection { Title = "ACTORS", ShowPlus = false };
        foreach (var actor in _actorManager.Actors)
        {
            var actorKey = "actor:" + actor.Id.Unique;
            string actorLabel = ActorDisplayName(actor);

            var groups = new List<(Core.BoneInfo.BoneCategory Cat, List<IBone> Bones)>();
            // The main selection surface owns skeleton discovery. The optional
            // world overlay must never be required for bones to appear.
            var skeleton = _skeletonService.GetSkeleton(actor) is { IsValid: true } validSkeleton
                ? validSkeleton
                : null;
            if (skeleton != null)
            {
                foreach (var bone in skeleton.Bones)
                {
                    if (bone.IsHiddenBone) continue;
                    var cat = Core.BoneInfo.BoneInfoService.GetCategory(bone.BoneName);
                    var slot = groups.FindIndex(g => g.Cat == cat);
                    if (slot < 0) { groups.Add((cat, new List<IBone>())); slot = groups.Count - 1; }
                    groups[slot].Bones.Add(bone);
                }
                groups.Sort((a, b) => ((int)a.Cat).CompareTo((int)b.Cat));
            }

            bool actorMatches = MatchesSidebarFilter(filter, actorLabel, actor.Name);
            bool hasMatchingBone = groups.Exists(group =>
                MatchesSidebarFilter(filter, Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(group.Cat), group.Cat.ToString())
                || group.Bones.Exists(bone => MatchesSidebarFilter(filter, bone.Name, bone.BoneName)));
            if (filtering && !actorMatches && !hasMatchingBone)
                continue;

            bool ownsSelectedBone = selectedBone != null && ReferenceEquals(selectedBone.Skeleton.Actor, actor);
            if (ownsSelectedBone)
                _collapsedNodes.Remove(actorKey);
            bool expanded = filtering || !_collapsedNodes.Contains(actorKey);
            actors.Rows.Add(new ShellSidebarRow
            {
                Label = actorLabel,
                Count = !_spawnService.IsVisible(actor) ? "hidden" : ActorCount(actor),
                Icon = actor.IsCompanion ? TablerIcon.Paw : TablerIcon.User,
                HasChildren = skeleton != null,
                Expanded = expanded,
                Active = _selectionService.IsSelected(actor),
                Tag = actor,
            });

            // M11: the actor folds DIRECTLY into bone categories (no skeleton
            // node), categories into bones. Category set = curated grouping;
            // the Ktisis-definitions toggle swaps the set once its data lands.
            if (expanded && skeleton != null && (!filtering || hasMatchingBone))
            {
                var displayedGroups = new List<(Core.BoneInfo.BoneCategory Cat, List<IBone> Visible, List<IBone> All)>();
                foreach (var (cat, bones) in groups)
                {
                    string categoryLabel = Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(cat);
                    bool categoryMatches = filtering && MatchesSidebarFilter(filter, categoryLabel, cat.ToString());
                    var visibleBones = !filtering || categoryMatches
                        ? bones
                        : bones.FindAll(bone => MatchesSidebarFilter(filter, bone.Name, bone.BoneName));
                    if (!filtering || visibleBones.Count > 0)
                        displayedGroups.Add((cat, visibleBones, bones));
                }

                for (int g = 0; g < displayedGroups.Count; g++)
                {
                    var (cat, visibleBones, allBones) = displayedGroups[g];
                    var catKey = actorKey + "/cat:" + cat;
                    bool containsSelectedBone = selectedBone != null && allBones.Exists(bone => ReferenceEquals(bone, selectedBone));
                    if (_knownCategoryNodes.Add(catKey) && !containsSelectedBone)
                        _collapsedNodes.Add(catKey);
                    if (containsSelectedBone)
                        _collapsedNodes.Remove(catKey);
                    bool catExpanded = filtering || !_collapsedNodes.Contains(catKey);
                    bool catLast = g == displayedGroups.Count - 1;
                    actors.Rows.Add(new ShellSidebarRow
                    {
                        Label = Core.BoneInfo.BoneInfoService.GetCategoryDisplayName(cat),
                        Count = allBones.Count.ToString(),
                        Depth = 1,
                        HasChildren = true,
                        Expanded = catExpanded,
                        IsLastChild = catLast,
                        Tag = catKey,
                    });
                    if (!catExpanded) continue;
                    for (int b = 0; b < visibleBones.Count; b++)
                    {
                        actors.Rows.Add(new ShellSidebarRow
                        {
                            Label = visibleBones[b].Name,
                            // modded/unlocalized bones: Name == BoneName — one is enough
                            Count = visibleBones[b].Name == visibleBones[b].BoneName ? "" : visibleBones[b].BoneName,
                            Depth = 2,
                            IsLastChild = b == visibleBones.Count - 1,
                            TreeLines = new[] { false, !catLast },
                            Active = _selectionService.IsSelected(visibleBones[b]),
                            Tag = visibleBones[b],
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

    /// <summary>Returns the configured nickname or the cleaned live actor name.</summary>
    private static string ActorDisplayName(IActor actor)
        => Config.ConfigurationService.Instance.HasNickname(actor)
            ? Config.ConfigurationService.Instance.GetDisplayName(actor)
            : DisplayName(actor.Name);

    /// <summary>Strips the raw object-index suffix ("Name (201)") for display.</summary>
    private static string DisplayName(string name)
        => System.Text.RegularExpressions.Regex.Replace(name, @"\s*\(\d+\)$", "");

    private static string ActorCount(IActor actor)
    {
        if (actor.IsCompanion) return "minion";
        if (actor.IsPlayer) return "player";
        return "npc";
    }

    private void BuildTabs(IEntity? primary)
    {
        _vm.Tabs.Clear();
        _activeTab = "Pose";
        _vm.Tabs.Add(new ShellTab { Label = "Pose", Active = true });
    }

    private void BuildCrumbAndStatus(IEntity? primary)
    {
        switch (primary)
        {
            case IBone bone:
                _vm.CrumbPrefix = $"{ActorDisplayName(bone.Skeleton.Actor)} · ";
                _vm.CrumbBold = bone.Name;
                break;
            case null:
                _vm.CrumbPrefix = "";
                _vm.CrumbBold = "";
                break;
            case IActor actor:
                _vm.CrumbPrefix = "";
                _vm.CrumbBold = ActorDisplayName(actor);
                break;
            default:
                _vm.CrumbPrefix = "";
                _vm.CrumbBold = DisplayName(primary.Name);
                break;
        }

        int actorCount = _actorManager.Actors.Count;
        _vm.StatusLeft = actorCount == 1 ? "1 actor" : $"{actorCount} actors";

        int bones = 0;
        var skeletonOwner = primary as ISkeleton
            ?? (primary as IBone)?.Skeleton
            ?? (primary as IActor)?.Skeleton;
        if (skeletonOwner is { IsValid: true })
            bones = skeletonOwner.Bones.Count;
        _vm.StatusRight = bones > 0
            ? $"{bones} bones · {ImGui.GetIO().Framerate:0} fps"
            : $"{ImGui.GetIO().Framerate:0} fps";
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

        if (row.Tag is not IEntity entity) return;

        var io = ImGui.GetIO();
        if (io.KeyShift && _selectionService.LastClicked is { } anchor)
        {
            // Range order follows the rows currently visible to the user;
            // collapsed and filtered-out entities are deliberately excluded.
            var displayOrder = new List<IEntity>();
            foreach (var section in _vm.Sections)
                foreach (var visibleRow in section.Rows)
                    if (visibleRow.Tag is IEntity visibleEntity)
                        displayOrder.Add(visibleEntity);
            _selectionService.SelectRange(anchor, entity, displayOrder);
        }
        else if (io.KeyCtrl)
        {
            _selectionService.ToggleSelection(entity);
        }
        else
        {
            _selectionService.Select(entity);
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

        var primary = _selectionService.Primary;
        ImGui.SetCursorScreenPos(origin);

        _poseInspector.SetEntity(primary);
        _poseInspector.Draw(origin, size);
    }

    /// <summary>Right-click actor menu: the lifetime actions that were stranded
    /// without a sidebar affordance (target / visibility / rename / clone / companion / despawn).</summary>
    private void DrawActorContextMenu()
    {
        if (_ctxOpenRequested)
        {
            ImGui.OpenPopup("##actor-ctx");
            _ctxOpenRequested = false;
        }
        if (_ctxActor is not { } actor) return;

        var items = new System.Collections.Generic.List<ContextMenuItem>
        {
            new("Set game target", TablerIcon.Eye),
            new(!_spawnService.IsVisible(actor) ? "Show" : "Hide", !_spawnService.IsVisible(actor) ? TablerIcon.Eye : TablerIcon.EyeOff),
            new("Rename…", TablerIcon.Edit),
            new("Clone", TablerIcon.Stack2),
            ContextMenuItem.Separator,
            new("Detach companion", TablerIcon.X),
        };
        var actions = new System.Collections.Generic.List<Action?>
        {
            () => _actorManager.SetGPoseTarget(actor),
            () => _spawnService.SetVisibility(actor, !_spawnService.IsVisible(actor)),
            () =>
            {
                _renameTarget = actor;
                _renameValue = Config.ConfigurationService.Instance.HasNickname(actor)
                    ? Config.ConfigurationService.Instance.GetDisplayName(actor)
                    : DisplayName(actor.Name);
                _renameOpen = true;
            },
            () =>
            {
                var clone = _spawnService.CloneActor(actor);
                if (clone != null) _selectionService.Select(clone);
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
                _selectionService.ClearSelection();
            });
        }

        int clicked = Crystarium.ContextMenu("##actor-ctx", items.ToArray());
        if (clicked >= 0 && clicked < actions.Count)
            actions[clicked]?.Invoke();
    }

    /// <summary>
    /// Right-click bone menu for hierarchy navigation and bone-local operations.
    /// These actions retain the same selection service and posing stack used by
    /// the matrix, overlay, and inspector.
    /// </summary>
    private void DrawBoneContextMenu()
    {
        if (_boneCtxOpenRequested)
        {
            ImGui.OpenPopup("##bone-ctx");
            _boneCtxOpenRequested = false;
        }
        if (_ctxBone is not { } bone)
            return;

        var mirrorName = _bonePosingService.GetMirrorBoneName(bone.BoneName);
        var mirror = mirrorName == null
            ? null
            : bone.Skeleton.Bones.FirstOrDefault(candidate =>
                candidate.BoneName == mirrorName &&
                candidate.PartialId == bone.PartialId);

        var items = new[]
        {
            new ContextMenuItem("Select parent", TablerIcon.ArrowUp, disabled: bone.ParentBone == null),
            new ContextMenuItem("Select children", TablerIcon.Sitemap, disabled: bone.ChildBones.Count == 0),
            new ContextMenuItem("Select mirrored bone", TablerIcon.ArrowsMove, disabled: mirror == null),
            ContextMenuItem.Separator,
            new ContextMenuItem("Flip bone", TablerIcon.Rotate),
            new ContextMenuItem("Reset bone", TablerIcon.Refresh, danger: true),
        };

        int clicked = Crystarium.ContextMenu("##bone-ctx", items);
        switch (clicked)
        {
            case 0 when bone.ParentBone != null:
                _selectionService.Select(bone.ParentBone);
                break;
            case 1:
                _selectionService.Select(bone);
                foreach (var candidate in bone.Skeleton.Bones)
                {
                    for (var parent = candidate.ParentBone; parent != null; parent = parent.ParentBone)
                    {
                        if (!ReferenceEquals(parent, bone))
                            continue;
                        _selectionService.AddToSelection(candidate);
                        break;
                    }
                }
                break;
            case 2 when mirror != null:
                _selectionService.Select(mirror);
                break;
            case 4:
                _cleanPose.FlipBone(bone);
                break;
            case 5:
                _cleanPose.ResetBone(bone);
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
                Config.ConfigurationService.Instance.SetNickname(target, _renameValue);
                _renameOpen = false;
            }
            ImGui.SameLine(0f, 8f * ImGuiHelpers.GlobalScale);
            if (Crystarium.Button("Clear", new ButtonProps { Id = "rename-clear",
                Tooltip = "Remove the nickname and show the real name" }))
            {
                Config.ConfigurationService.Instance.SetNickname(target, null);
                _renameOpen = false;
            }
        });
    }

}
