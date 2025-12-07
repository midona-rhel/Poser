using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Core;
using Poser.Core.BoneInfo;
using Poser.Entities;
using Poser.History;
using Poser.Services;
using Poser.UI.Controls;

namespace Poser.UI.Components;

/// <summary>
/// Unified entity list showing all scene entities (actors, camera, etc.) in a hierarchical table.
/// </summary>
public class EntityList : IDisposable
{
    private const float CheckboxColumnWidth = 32f;
    private const float CellPaddingX = 4f;

    private readonly IActorManager _actorManager;
    private readonly IAnimationService _animationService;
    private readonly IActorSpawnService _spawnService;
    private readonly IHistoryService _historyService;
    private readonly ICameraService _cameraService;
    private readonly IGPoseService _gPoseService;
    private readonly ISkeletonService _skeletonService;
    private readonly IEditorState _editorState;
    private readonly IEventBus _eventBus;

    private List<IActor> _actors = new();
    private bool _isCollapsed = false;

    // Category view state - tracks which categories are collapsed per skeleton
    private readonly Dictionary<EntityId, HashSet<BoneCategory>> _collapsedCategories = new();

    public EntityList(
        IActorManager actorManager,
        IAnimationService animationService,
        IActorSpawnService spawnService,
        IHistoryService historyService,
        ICameraService cameraService,
        IGPoseService gPoseService,
        ISkeletonService skeletonService,
        IEditorState editorState,
        IEventBus eventBus)
    {
        _actorManager = actorManager;
        _animationService = animationService;
        _spawnService = spawnService;
        _historyService = historyService;
        _cameraService = cameraService;
        _gPoseService = gPoseService;
        _skeletonService = skeletonService;
        _editorState = editorState;
        _eventBus = eventBus;

        _eventBus.Subscribe<ActorListChangedEvent>(OnActorListChanged);
        _actors = _actorManager.Actors.ToList();
    }

    private void OnActorListChanged(ActorListChangedEvent evt)
    {
        _actors = evt.Actors.ToList();
    }

    public void Draw()
    {
        float checkboxColWidth = CheckboxColumnWidth * ImGuiHelpers.GlobalScale;
        float cellPadding = CellPaddingX * ImGuiHelpers.GlobalScale;

        var brighterBg = ImPoser.GetBrighterTableBg();
        var tabHovered = ImPoser.GetTabHoveredColor();
        var tabActive = ImPoser.GetTabActiveColor();

        // Count total top-level entities (actors + camera)
        int totalEntities = _actors.Count + (_gPoseService.IsGPosing ? 1 : 0);

        using (ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(cellPadding, 4f * ImGuiHelpers.GlobalScale)))
        using (ImRaii.PushColor(ImGuiCol.TableRowBg, brighterBg))
        using (ImRaii.PushColor(ImGuiCol.TableRowBgAlt, brighterBg))
        {
            // No BordersInnerV - cleaner look for hierarchy
            var tableFlags = ImGuiTableFlags.RowBg;

            // 3 columns: name (with collapse+icon+indent), visibility, freeze
            if (ImGui.BeginTable("##entities_table", 3, tableFlags))
            {
                ImGui.TableSetupColumn("##name", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("##visible", ImGuiTableColumnFlags.WidthFixed, checkboxColWidth);
                ImGui.TableSetupColumn("##freeze", ImGuiTableColumnFlags.WidthFixed, checkboxColWidth);

                // Header row
                ImGui.TableNextRow();

                // Name column with collapse button
                ImGui.TableNextColumn();
                float buttonSize = ImGui.GetFrameHeight();
                var arrowIcon = _isCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
                if (ImPoser.IconButton("entities_collapse", arrowIcon, new Vector2(buttonSize, buttonSize)))
                {
                    _isCollapsed = !_isCollapsed;
                }
                ImGui.SameLine();
                ImGui.AlignTextToFramePadding();
                ImGui.TextDisabled($"Entities ({totalEntities})");

                // Visibility column header
                ImGui.TableNextColumn();
                ImPoser.CenterIconInCell(FontAwesomeIcon.Eye, null, "Visibility");

                // Freeze column header
                ImGui.TableNextColumn();
                ImPoser.CenterIconInCell(FontAwesomeIcon.Snowflake, null, "Freeze animation");

                // Data rows (if not collapsed)
                if (!_isCollapsed)
                {
                    // Draw camera first (non-collapsible)
                    if (_gPoseService.IsGPosing)
                    {
                        DrawCameraRow(tabHovered, tabActive);
                    }

                    // Draw actors with their children
                    for (int i = 0; i < _actors.Count; i++)
                    {
                        var actor = _actors[i];
                        DrawEntityRow(actor, 0, i, tabHovered, tabActive);
                    }
                }

                ImGui.EndTable();
            }
        }

        if (!_isCollapsed && totalEntities == 0)
        {
            ImGui.TextDisabled("No entities in scene");
        }
    }

    private void DrawCameraRow(Vector4 tabHovered, Vector4 tabActive)
    {
        ImGui.TableNextRow();

        // Name column with small dot (non-collapsible indicator), icon, and name
        ImGui.TableNextColumn();
        float buttonSize = ImGui.GetFrameHeight();

        // Small dot for non-collapsible - use text over icon button for consistent sizing
        ImPoser.TextOverIconButton("camera_dot", FontAwesomeIcon.CaretDown, "·", new Vector2(buttonSize, buttonSize));

        ImGui.SameLine();

        // Camera icon
        ImGui.AlignTextToFramePadding();
        ImPoser.FontIcon(FontAwesomeIcon.Camera, UIConstants.DefaultIconColor);

        ImGui.SameLine();

        // Name
        ImGui.AlignTextToFramePadding();
        ImGui.Text("Camera");

        // Show position as tooltip
        if (ImGui.IsItemHovered())
        {
            var pos = _cameraService.GetCameraPosition();
            ImGui.SetTooltip($"Position: {pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}");
        }

        // Visibility column - empty for camera
        ImGui.TableNextColumn();

        // Freeze column - empty for camera
        ImGui.TableNextColumn();
    }

    private void DrawEntityRow(IEntity entity, int depth, int index, Vector4 tabHovered, Vector4 tabActive)
    {
        // Get actor-specific state
        IActor? actor = entity as IActor;
        bool isSelected = actor != null && _actorManager.IsSelected(actor);
        bool isFrozen = actor != null && _animationService.IsFrozen(actor);
        bool isVisible = actor != null && _spawnService.IsVisible(actor);

        // Ensure skeleton is created for actors
        if (actor != null)
        {
            _skeletonService.GetSkeleton(actor);
        }

        // Determine icon color - skeletons get light blue, others based on visibility
        Vector4 iconColor;
        if (entity.EntityType == EntityType.Skeleton)
        {
            iconColor = UIConstants.SkeletonColor;
        }
        else
        {
            iconColor = isVisible ? UIConstants.DefaultIconColor : UIConstants.HiddenIconColor;
        }
        Vector4 textColor = isVisible ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : new Vector4(0.5f, 0.5f, 0.5f, 0.7f);

        ImGui.TableNextRow();

        // Apply selection highlight if needed
        if (isSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabActive));
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabActive));
        }

        // Name column with collapse button, icon, and name - all indented together
        ImGui.TableNextColumn();
        float buttonSize = ImGui.GetFrameHeight();

        // Apply indentation: each level = button size + half item spacing
        var halfSpacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        for (int i = 0; i < depth; i++)
        {
            ImGui.Dummy(new Vector2(buttonSize + halfSpacing, buttonSize));
            ImGui.SameLine(0, 0);
        }

        // Collapse button or dot
        // In debug mode, force everything to be expanded
        bool effectiveCollapsed = _editorState.DebugMode ? false : entity.IsCollapsed;

        if (entity.IsCollapsible)
        {
            var arrowIcon = effectiveCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
            if (ImPoser.IconButton($"collapse_{entity.Id}", arrowIcon, new Vector2(buttonSize, buttonSize)))
            {
                entity.IsCollapsed = !entity.IsCollapsed;
            }
        }
        else
        {
            // Non-collapsible: show dot for consistent sizing
            ImPoser.TextOverIconButton($"dot_{entity.Id}", FontAwesomeIcon.CaretDown, "·", new Vector2(buttonSize, buttonSize));
        }

        ImGui.SameLine();

        // Entity icon
        ImGui.AlignTextToFramePadding();
        var icon = GetIconForEntity(entity);
        ImPoser.FontIcon(icon, iconColor);

        ImGui.SameLine();

        // Name (clickable for selection, but no SpanAllColumns to avoid black box)
        using (ImRaii.PushColor(ImGuiCol.Text, textColor))
        {
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable($"{entity.Name}##{entity.Id}", isSelected))
            {
                HandleEntityClick(entity, index);
            }
        }

        // Visibility checkbox column
        ImGui.TableNextColumn();
        if (actor != null)
        {
            bool visible = isVisible;
            if (DrawCheckbox($"visible_{entity.Id}", ref visible))
            {
                _spawnService.SetVisibility(actor, visible);
                var action = new VisibilityAction(_spawnService, actor, visible);
                _historyService.Record(action);
            }
        }
        else if (entity is Skeleton skeleton)
        {
            bool overlayVisible = skeleton.IsOverlayVisible;
            if (DrawCheckbox($"overlay_{entity.Id}", ref overlayVisible))
            {
                skeleton.IsOverlayVisible = overlayVisible;
            }
        }

        // Freeze checkbox column
        ImGui.TableNextColumn();
        if (actor != null)
        {
            bool frozen = isFrozen;
            if (DrawCheckbox($"freeze_{entity.Id}", ref frozen))
            {
                if (isSelected)
                {
                    foreach (var selectedActor in _actorManager.SelectedActors)
                    {
                        if (frozen)
                            _animationService.Freeze(selectedActor);
                        else
                            _animationService.Unfreeze(selectedActor);
                    }
                }
                else
                {
                    if (frozen)
                        _animationService.Freeze(actor);
                    else
                        _animationService.Unfreeze(actor);
                }
            }
        }

        // Draw children if not collapsed (or if debug mode forces expansion)
        bool showChildren = _editorState.DebugMode || !entity.IsCollapsed;
        if (showChildren)
        {
            // Special handling for Skeleton in Category mode
            if (entity is Skeleton skeleton && _editorState.BoneDisplayMode == BoneDisplayMode.Category)
            {
                DrawSkeletonCategoryView(skeleton, depth + 1, tabHovered, tabActive);
            }
            else if (entity.Children.Count > 0)
            {
                int childIndex = 0;
                foreach (var child in entity.Children)
                {
                    DrawEntityRow(child, depth + 1, childIndex++, tabHovered, tabActive);
                }
            }
        }
    }

    private void DrawSkeletonCategoryView(Skeleton skeleton, int depth, Vector4 tabHovered, Vector4 tabActive)
    {
        // Gather all bones and group by category, tracking which are "root" bones for that category
        // A bone is a category root if its parent is in a different category (or has no parent)
        var bonesByCategory = new Dictionary<BoneCategory, List<Bone>>();
        var categoryRoots = new Dictionary<BoneCategory, List<Bone>>();

        void GatherBones(IEntity entity)
        {
            if (entity is Bone bone && !bone.IsHiddenBone)
            {
                var category = bone.Category;
                if (!bonesByCategory.ContainsKey(category))
                {
                    bonesByCategory[category] = new List<Bone>();
                    categoryRoots[category] = new List<Bone>();
                }
                bonesByCategory[category].Add(bone);

                // Check if this is a root for its category
                var parentBone = bone.ParentBone as Bone;
                bool isRootForCategory = parentBone == null ||
                                         parentBone.IsHiddenBone ||
                                         parentBone.Category != category;
                if (isRootForCategory)
                {
                    categoryRoots[category].Add(bone);
                }
            }

            foreach (var child in entity.Children)
            {
                GatherBones(child);
            }
        }

        foreach (var child in skeleton.Children)
        {
            GatherBones(child);
        }

        // Get collapsed state for this skeleton (default all categories to collapsed)
        if (!_collapsedCategories.TryGetValue(skeleton.Id, out var collapsedSet))
        {
            collapsedSet = new HashSet<BoneCategory>(Enum.GetValues<BoneCategory>());
            _collapsedCategories[skeleton.Id] = collapsedSet;
        }

        // Draw categories in enum order
        foreach (BoneCategory category in Enum.GetValues<BoneCategory>())
        {
            if (!bonesByCategory.TryGetValue(category, out var bones) || bones.Count == 0)
                continue;

            var roots = categoryRoots[category];
            DrawCategoryGroup(skeleton.Id, category, bones.Count, roots, depth, collapsedSet, tabHovered, tabActive);
        }
    }

    private void DrawCategoryGroup(EntityId skeletonId, BoneCategory category, int totalCount, List<Bone> rootBones, int depth, HashSet<BoneCategory> collapsedSet, Vector4 tabHovered, Vector4 tabActive)
    {
        bool isCollapsed = !_editorState.DebugMode && collapsedSet.Contains(category);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        float buttonSize = ImGui.GetFrameHeight();

        // Apply indentation: each level = button size + half item spacing
        var halfSpacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        for (int i = 0; i < depth; i++)
        {
            ImGui.Dummy(new Vector2(buttonSize + halfSpacing, buttonSize));
            ImGui.SameLine(0, 0);
        }

        // Collapse button
        var arrowIcon = isCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
        if (ImPoser.IconButton($"cat_{skeletonId}_{category}", arrowIcon, new Vector2(buttonSize, buttonSize)))
        {
            if (collapsedSet.Contains(category))
                collapsedSet.Remove(category);
            else
                collapsedSet.Add(category);
        }

        ImGui.SameLine();

        // Category icon
        ImGui.AlignTextToFramePadding();
        ImPoser.FontIcon(FontAwesomeIcon.CircleNodes, UIConstants.DefaultIconColor);

        ImGui.SameLine();

        // Category name with count
        ImGui.AlignTextToFramePadding();
        ImGui.TextDisabled($"{BoneInfoService.GetCategoryDisplayName(category)} ({totalCount})");

        // Empty visibility/freeze columns
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();

        // Draw root bones with hierarchy if not collapsed
        if (!isCollapsed)
        {
            // Sort root bones alphabetically
            var sortedRoots = rootBones.OrderBy(b => b.GetFriendlyName()).ToList();

            for (int i = 0; i < sortedRoots.Count; i++)
            {
                DrawBoneInCategoryView(sortedRoots[i], category, depth + 1, tabHovered, tabActive);
            }
        }
    }

    private void DrawBoneInCategoryView(Bone bone, BoneCategory category, int depth, Vector4 tabHovered, Vector4 tabActive)
    {
        // Get children that are in the same category
        var sameCategChildren = bone.ChildBones
            .OfType<Bone>()
            .Where(b => !b.IsHiddenBone && b.Category == category)
            .ToList();

        bool hasChildren = sameCategChildren.Count > 0;
        bool effectiveCollapsed = _editorState.DebugMode ? false : bone.IsCollapsed;

        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        float buttonSize = ImGui.GetFrameHeight();

        // Apply indentation: each level = button size + half item spacing
        var halfSpacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        for (int i = 0; i < depth; i++)
        {
            ImGui.Dummy(new Vector2(buttonSize + halfSpacing, buttonSize));
            ImGui.SameLine(0, 0);
        }

        // Collapse button or dot
        if (hasChildren)
        {
            var arrowIcon = effectiveCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
            if (ImPoser.IconButton($"collapse_{bone.Id}", arrowIcon, new Vector2(buttonSize, buttonSize)))
            {
                bone.IsCollapsed = !bone.IsCollapsed;
            }
        }
        else
        {
            ImPoser.TextOverIconButton($"dot_{bone.Id}", FontAwesomeIcon.CaretDown, "·", new Vector2(buttonSize, buttonSize));
        }

        ImGui.SameLine();

        // Bone icon
        ImGui.AlignTextToFramePadding();
        ImPoser.FontIcon(FontAwesomeIcon.Bone, UIConstants.DefaultIconColor);

        ImGui.SameLine();

        // Bone name
        ImGui.AlignTextToFramePadding();
        ImGui.Text(bone.Name);

        // Empty columns
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();

        // Draw children if not collapsed
        if (hasChildren && !effectiveCollapsed)
        {
            var sortedChildren = sameCategChildren.OrderBy(b => b.GetFriendlyName()).ToList();
            foreach (var child in sortedChildren)
            {
                DrawBoneInCategoryView(child, category, depth + 1, tabHovered, tabActive);
            }
        }
    }

    private bool DrawCheckbox(string id, ref bool value)
    {
        float cellWidth = ImGui.GetContentRegionAvail().X;
        float checkboxWidth = ImGui.GetFrameHeight();
        float offsetX = (cellWidth - checkboxWidth) / 2;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offsetX);

        return ImGui.Checkbox($"##{id}", ref value);
    }

    private void HandleEntityClick(IEntity entity, int index)
    {
        // Only handle actor selection for now
        if (entity is not IActor actor)
            return;

        var io = ImGui.GetIO();
        bool ctrlHeld = io.KeyCtrl;
        bool shiftHeld = io.KeyShift;

        if (ctrlHeld)
        {
            if (_actorManager.IsSelected(actor))
                _actorManager.RemoveFromSelection(actor);
            else
                _actorManager.AddToSelection(actor);
        }
        else if (shiftHeld && _actorManager.SelectedActors.Count > 0)
        {
            var firstSelected = _actorManager.SelectedActors.First();
            var firstIndex = _actors.IndexOf(firstSelected);
            if (firstIndex >= 0 && index >= 0 && index < _actors.Count)
            {
                int start = Math.Min(firstIndex, index);
                int end = Math.Max(firstIndex, index);

                var rangeActors = new List<IActor>();
                for (int j = start; j <= end; j++)
                {
                    rangeActors.Add(_actors[j]);
                }
                _actorManager.SelectMultiple(rangeActors);
            }
        }
        else
        {
            _actorManager.Select(actor);
        }
    }

    private static FontAwesomeIcon GetIconForEntity(IEntity entity)
    {
        return entity.EntityType switch
        {
            EntityType.Player => FontAwesomeIcon.User,
            EntityType.Npc => FontAwesomeIcon.UserShield,
            EntityType.Companion => FontAwesomeIcon.Paw,
            EntityType.Camera => FontAwesomeIcon.Camera,
            EntityType.Skeleton => FontAwesomeIcon.CircleNodes, // circle-nodes icon for skeleton root
            EntityType.Bone => FontAwesomeIcon.Bone,
            _ => entity switch
            {
                IActor actor => actor.ObjectKind switch
                {
                    Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Player => FontAwesomeIcon.User,
                    Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion => FontAwesomeIcon.Paw,
                    Dalamud.Game.ClientState.Objects.Enums.ObjectKind.MountType => FontAwesomeIcon.Horse,
                    Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Ornament => FontAwesomeIcon.Gem,
                    Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc => FontAwesomeIcon.UserShield,
                    Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc => FontAwesomeIcon.UserTie,
                    Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Retainer => FontAwesomeIcon.Store,
                    _ => FontAwesomeIcon.User
                },
                _ => FontAwesomeIcon.QuestionCircle
            }
        };
    }

    public void Dispose()
    {
        _eventBus.Unsubscribe<ActorListChangedEvent>(OnActorListChanged);
    }
}
