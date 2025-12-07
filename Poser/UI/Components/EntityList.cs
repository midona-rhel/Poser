using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Poser.Controllers;
using Poser.Core;
using Poser.Core.BoneInfo;
using Poser.Entities;
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
    private readonly ICameraService _cameraService;
    private readonly IGPoseService _gPoseService;
    private readonly ISkeletonService _skeletonService;
    private readonly IEditorState _editorState;
    private readonly IEventBus _eventBus;
    private readonly IPosingController _controller;

    private List<IActor> _actors = new();
    private bool _isCollapsed = false;

    // Category view state - tracks which categories are collapsed per skeleton
    private readonly Dictionary<EntityId, HashSet<BoneCategory>> _collapsedCategories = new();
    // Subcategory view state - tracks which subcategories are collapsed per skeleton
    private readonly Dictionary<EntityId, HashSet<BoneSubcategory>> _collapsedSubcategories = new();

    public EntityList(
        IActorManager actorManager,
        IAnimationService animationService,
        IActorSpawnService spawnService,
        ICameraService cameraService,
        IGPoseService gPoseService,
        ISkeletonService skeletonService,
        IEditorState editorState,
        IEventBus eventBus,
        IPosingController controller)
    {
        _actorManager = actorManager;
        _animationService = animationService;
        _spawnService = spawnService;
        _cameraService = cameraService;
        _gPoseService = gPoseService;
        _skeletonService = skeletonService;
        _editorState = editorState;
        _eventBus = eventBus;
        _controller = controller;

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

        // Determine icon color - skeletons and bones get light blue, others based on visibility
        Vector4 iconColor;
        if (entity.EntityType == EntityType.Skeleton || entity.EntityType == EntityType.Bone)
        {
            iconColor = UIConstants.SkeletonColor;
        }
        else
        {
            iconColor = isVisible ? UIConstants.DefaultIconColor : UIConstants.HiddenIconColor;
        }

        // Text color - bones and skeletons always use normal color, actors based on visibility
        Vector4 textColor;
        if (entity.EntityType == EntityType.Skeleton || entity.EntityType == EntityType.Bone)
        {
            textColor = ImGui.GetStyle().Colors[(int)ImGuiCol.Text];
        }
        else
        {
            textColor = isVisible ? ImGui.GetStyle().Colors[(int)ImGuiCol.Text] : new Vector4(0.5f, 0.5f, 0.5f, 0.7f);
        }

        // Check if this bone is selected
        bool isBoneSelected = entity is Bone bone && _editorState.SelectedBone == bone;

        ImGui.TableNextRow();

        // Apply selection highlight if needed (for actors or bones)
        if (isSelected || isBoneSelected)
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

        // Name (clickable for selection)
        // Make Selectable completely transparent - row background handles all highlighting
        using (ImRaii.PushColor(ImGuiCol.Header, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.Text, textColor))
        {
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable($"{entity.Name}##{entity.Id}", false))
            {
                HandleEntityClick(entity, index);
            }

            // Set row background based on hover/selection state
            if (ImGui.IsItemHovered())
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabHovered));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabHovered));
            }
        }

        // Visibility checkbox column
        ImGui.TableNextColumn();
        if (actor != null)
        {
            bool visible = isVisible;
            if (DrawCheckbox($"visible_{entity.Id}", ref visible))
            {
                _controller.SetActorVisibility(actor, visible);
            }
        }
        else if (entity is Skeleton skeleton)
        {
            bool overlayVisible = skeleton.IsOverlayVisible;
            if (DrawCheckbox($"overlay_{entity.Id}", ref overlayVisible))
            {
                skeleton.IsOverlayVisible = overlayVisible;

                // Auto-freeze animation when enabling overlay for posing
                if (overlayVisible && !_animationService.IsFrozen(skeleton.Actor))
                {
                    _controller.SetFrozen(skeleton.Actor, true);
                }
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
                        _controller.SetFrozen(selectedActor, frozen);
                    }
                }
                else
                {
                    _controller.SetFrozen(actor, frozen);
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
        // Gather all bones and group by category and subcategory
        var bonesByCategory = new Dictionary<BoneCategory, List<Bone>>();
        var bonesBySubcategory = new Dictionary<BoneCategory, Dictionary<BoneSubcategory, List<Bone>>>();

        void GatherBones(IEntity entity)
        {
            if (entity is Bone bone && !bone.IsHiddenBone)
            {
                var category = bone.Category;
                var subcategory = BoneInfoService.GetSubcategory(bone.BoneName);

                if (!bonesByCategory.ContainsKey(category))
                {
                    bonesByCategory[category] = new List<Bone>();
                    bonesBySubcategory[category] = new Dictionary<BoneSubcategory, List<Bone>>();
                }
                bonesByCategory[category].Add(bone);

                // Group by subcategory within category
                if (!bonesBySubcategory[category].ContainsKey(subcategory))
                {
                    bonesBySubcategory[category][subcategory] = new List<Bone>();
                }
                bonesBySubcategory[category][subcategory].Add(bone);
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
        if (!_collapsedCategories.TryGetValue(skeleton.Id, out var collapsedCats))
        {
            collapsedCats = new HashSet<BoneCategory>(Enum.GetValues<BoneCategory>());
            _collapsedCategories[skeleton.Id] = collapsedCats;
        }
        if (!_collapsedSubcategories.TryGetValue(skeleton.Id, out var collapsedSubs))
        {
            collapsedSubs = new HashSet<BoneSubcategory>(Enum.GetValues<BoneSubcategory>());
            _collapsedSubcategories[skeleton.Id] = collapsedSubs;
        }

        // Draw categories in enum order
        foreach (BoneCategory category in Enum.GetValues<BoneCategory>())
        {
            if (!bonesByCategory.TryGetValue(category, out var bones) || bones.Count == 0)
                continue;

            var subcatDict = bonesBySubcategory[category];
            DrawCategoryGroup(skeleton.Id, category, bones.Count, subcatDict, depth, collapsedCats, collapsedSubs, tabHovered, tabActive);
        }
    }

    private void DrawCategoryGroup(EntityId skeletonId, BoneCategory category, int totalCount,
        Dictionary<BoneSubcategory, List<Bone>> subcatDict, int depth,
        HashSet<BoneCategory> collapsedCats, HashSet<BoneSubcategory> collapsedSubs,
        Vector4 tabHovered, Vector4 tabActive)
    {
        bool isCollapsed = !_editorState.DebugMode && collapsedCats.Contains(category);

        // Check if this category has a real root bone
        var rootBoneName = BoneInfoService.GetCategoryRootBone(category);
        Bone? rootBone = null;

        if (rootBoneName != null)
        {
            // Find the root bone in the subcategory dictionary
            foreach (var bones in subcatDict.Values)
            {
                rootBone = bones.FirstOrDefault(b => b.BoneName == rootBoneName);
                if (rootBone != null)
                    break;
            }
        }

        // If we have a real root bone, draw it as a selectable bone header
        if (rootBone != null)
        {
            DrawBoneAsGroupHeader(rootBone, skeletonId, category, totalCount, subcatDict, depth,
                collapsedCats, collapsedSubs, tabHovered, tabActive, isCollapsed);
        }
        else
        {
            // Abstract category (Equipment, Other) - keep current behavior
            DrawAbstractCategoryHeader(skeletonId, category, totalCount, subcatDict, depth,
                collapsedCats, collapsedSubs, tabHovered, tabActive, isCollapsed);
        }
    }

    private void DrawBoneAsGroupHeader(Bone rootBone, EntityId skeletonId, BoneCategory category, int totalCount,
        Dictionary<BoneSubcategory, List<Bone>> subcatDict, int depth,
        HashSet<BoneCategory> collapsedCats, HashSet<BoneSubcategory> collapsedSubs,
        Vector4 tabHovered, Vector4 tabActive, bool isCollapsed)
    {
        bool isSelected = _editorState.SelectedBone == rootBone;

        ImGui.TableNextRow();

        // Apply selection highlight
        if (isSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabActive));
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabActive));
        }

        ImGui.TableNextColumn();

        float buttonSize = ImGui.GetFrameHeight();

        // Apply indentation
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
            if (collapsedCats.Contains(category))
                collapsedCats.Remove(category);
            else
                collapsedCats.Add(category);
        }

        ImGui.SameLine();

        // Bone icon
        ImGui.AlignTextToFramePadding();
        var boneIcon = rootBone.ChildBones.Count > 0 ? FontAwesomeIcon.CircleNodes : FontAwesomeIcon.Bone;
        ImPoser.FontIcon(boneIcon, UIConstants.SkeletonColor);

        ImGui.SameLine();

        // Bone name (clickable for selection) - show friendly name with count
        using (ImRaii.PushColor(ImGuiCol.Header, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, Vector4.Zero))
        {
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable($"{rootBone.Name} ({totalCount})##{rootBone.Id}", false))
            {
                _editorState.SelectBone(rootBone);
            }

            // Set row background based on hover state
            if (ImGui.IsItemHovered())
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabHovered));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabHovered));
            }
        }

        // Empty visibility/freeze columns
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();

        // Draw children if not collapsed
        if (!isCollapsed)
        {
            DrawCategoryChildren(skeletonId, category, subcatDict, depth, collapsedSubs, tabHovered, tabActive, rootBone);
        }
    }

    private void DrawAbstractCategoryHeader(EntityId skeletonId, BoneCategory category, int totalCount,
        Dictionary<BoneSubcategory, List<Bone>> subcatDict, int depth,
        HashSet<BoneCategory> collapsedCats, HashSet<BoneSubcategory> collapsedSubs,
        Vector4 tabHovered, Vector4 tabActive, bool isCollapsed)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        float buttonSize = ImGui.GetFrameHeight();

        // Apply indentation
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
            if (collapsedCats.Contains(category))
                collapsedCats.Remove(category);
            else
                collapsedCats.Add(category);
        }

        ImGui.SameLine();

        // Category icon
        ImGui.AlignTextToFramePadding();
        ImPoser.FontIcon(FontAwesomeIcon.Bone, UIConstants.SkeletonColor);

        ImGui.SameLine();

        // Category name with count
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{BoneInfoService.GetCategoryDisplayName(category)} ({totalCount})");

        // Empty visibility/freeze columns
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();

        // Draw children if not collapsed
        if (!isCollapsed)
        {
            DrawCategoryChildren(skeletonId, category, subcatDict, depth, collapsedSubs, tabHovered, tabActive, null);
        }
    }

    private void DrawCategoryChildren(EntityId skeletonId, BoneCategory category,
        Dictionary<BoneSubcategory, List<Bone>> subcatDict, int depth,
        HashSet<BoneSubcategory> collapsedSubs, Vector4 tabHovered, Vector4 tabActive, Bone? rootBone)
    {
        // Check if we have any non-None subcategories
        bool hasSubcategories = subcatDict.Keys.Any(s => s != BoneSubcategory.None);

        if (hasSubcategories)
        {
            // Draw subcategories first (skip None, draw it last or mixed with others)
            foreach (BoneSubcategory subcategory in Enum.GetValues<BoneSubcategory>())
            {
                if (subcategory == BoneSubcategory.None)
                    continue;

                if (!subcatDict.TryGetValue(subcategory, out var subBones) || subBones.Count == 0)
                    continue;

                // Filter out root bone from children list
                var filteredBones = rootBone != null
                    ? subBones.Where(b => b != rootBone).ToList()
                    : subBones;

                if (filteredBones.Count > 0)
                {
                    DrawSubcategoryGroup(skeletonId, category, subcategory, filteredBones, depth + 1, collapsedSubs, tabHovered, tabActive);
                }
            }

            // Draw bones without subcategory (None) at the end
            if (subcatDict.TryGetValue(BoneSubcategory.None, out var noneBones) && noneBones.Count > 0)
            {
                var filteredNone = rootBone != null
                    ? noneBones.Where(b => b != rootBone).OrderBy(b => b.GetFriendlyName()).ToList()
                    : noneBones.OrderBy(b => b.GetFriendlyName()).ToList();

                foreach (var bone in filteredNone)
                {
                    DrawBoneInCategoryView(bone, category, depth + 1, tabHovered, tabActive);
                }
            }
        }
        else
        {
            // No subcategories, draw bones in hierarchy if we have a root bone
            var allBones = subcatDict.Values.SelectMany(b => b).ToList();

            if (rootBone != null)
            {
                // Use hierarchy - draw root bone's children recursively
                var categoryBones = new HashSet<Bone>(allBones.Where(b => b != rootBone));
                foreach (var childBone in rootBone.ChildBones.Cast<Bone>().OrderBy(b => b.GetFriendlyName()))
                {
                    if (categoryBones.Contains(childBone))
                    {
                        DrawBoneHierarchy(childBone, categoryBones, category, depth + 1, tabHovered, tabActive);
                    }
                }
            }
            else
            {
                // No root bone, draw flat list
                foreach (var bone in allBones.OrderBy(b => b.GetFriendlyName()))
                {
                    DrawBoneInCategoryView(bone, category, depth + 1, tabHovered, tabActive);
                }
            }
        }
    }

    private void DrawSubcategoryGroup(EntityId skeletonId, BoneCategory category, BoneSubcategory subcategory,
        List<Bone> bones, int depth, HashSet<BoneSubcategory> collapsedSubs,
        Vector4 tabHovered, Vector4 tabActive)
    {
        bool isCollapsed = !_editorState.DebugMode && collapsedSubs.Contains(subcategory);

        // Check if this subcategory has a real root bone
        var rootBoneName = BoneInfoService.GetSubcategoryRootBone(subcategory);
        Bone? rootBone = rootBoneName != null
            ? bones.FirstOrDefault(b => b.BoneName == rootBoneName)
            : null;

        // If we have a real root bone, draw it as a selectable bone header
        if (rootBone != null)
        {
            DrawSubcategoryBoneHeader(rootBone, skeletonId, category, subcategory, bones, depth,
                collapsedSubs, tabHovered, tabActive, isCollapsed);
        }
        else
        {
            // Abstract subcategory - keep current behavior
            DrawAbstractSubcategoryHeader(skeletonId, category, subcategory, bones, depth,
                collapsedSubs, tabHovered, tabActive, isCollapsed);
        }
    }

    private void DrawSubcategoryBoneHeader(Bone rootBone, EntityId skeletonId, BoneCategory category,
        BoneSubcategory subcategory, List<Bone> bones, int depth,
        HashSet<BoneSubcategory> collapsedSubs, Vector4 tabHovered, Vector4 tabActive, bool isCollapsed)
    {
        bool isSelected = _editorState.SelectedBone == rootBone;

        ImGui.TableNextRow();

        // Apply selection highlight
        if (isSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabActive));
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabActive));
        }

        ImGui.TableNextColumn();

        float buttonSize = ImGui.GetFrameHeight();

        // Apply indentation
        var halfSpacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        for (int i = 0; i < depth; i++)
        {
            ImGui.Dummy(new Vector2(buttonSize + halfSpacing, buttonSize));
            ImGui.SameLine(0, 0);
        }

        // Collapse button
        var arrowIcon = isCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
        if (ImPoser.IconButton($"subcat_{skeletonId}_{category}_{subcategory}", arrowIcon, new Vector2(buttonSize, buttonSize)))
        {
            if (collapsedSubs.Contains(subcategory))
                collapsedSubs.Remove(subcategory);
            else
                collapsedSubs.Add(subcategory);
        }

        ImGui.SameLine();

        // Bone icon
        ImGui.AlignTextToFramePadding();
        var boneIcon = rootBone.ChildBones.Count > 0 ? FontAwesomeIcon.CircleNodes : FontAwesomeIcon.Bone;
        ImPoser.FontIcon(boneIcon, UIConstants.SkeletonColor);

        ImGui.SameLine();

        // Bone name (clickable for selection) - show friendly name with count
        using (ImRaii.PushColor(ImGuiCol.Header, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, Vector4.Zero))
        {
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable($"{rootBone.Name} ({bones.Count})##{rootBone.Id}_subcat", false))
            {
                _editorState.SelectBone(rootBone);
            }

            // Set row background based on hover state
            if (ImGui.IsItemHovered())
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabHovered));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabHovered));
            }
        }

        // Empty visibility/freeze columns
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();

        // Draw children if not collapsed - use actual bone hierarchy
        if (!isCollapsed)
        {
            // Get bones in this subcategory as a set for filtering
            var subcategoryBones = new HashSet<Bone>(bones);

            // Draw direct children of root bone that are in this subcategory, recursively
            foreach (var childBone in rootBone.ChildBones.Cast<Bone>().OrderBy(b => b.GetFriendlyName()))
            {
                if (subcategoryBones.Contains(childBone))
                {
                    DrawBoneHierarchy(childBone, subcategoryBones, category, depth + 1, tabHovered, tabActive);
                }
            }
        }
    }

    private void DrawBoneHierarchy(Bone bone, HashSet<Bone> allowedBones, BoneCategory category, int depth,
        Vector4 tabHovered, Vector4 tabActive)
    {
        // Draw this bone
        bool hasVisibleChildren = bone.ChildBones.Cast<Bone>().Any(c => allowedBones.Contains(c));
        DrawBoneInHierarchyView(bone, category, depth, hasVisibleChildren, tabHovered, tabActive);

        // Recursively draw children that are in the allowed set
        foreach (var childBone in bone.ChildBones.Cast<Bone>().OrderBy(b => b.GetFriendlyName()))
        {
            if (allowedBones.Contains(childBone))
            {
                DrawBoneHierarchy(childBone, allowedBones, category, depth + 1, tabHovered, tabActive);
            }
        }
    }

    private void DrawBoneInHierarchyView(Bone bone, BoneCategory category, int depth, bool hasChildren,
        Vector4 tabHovered, Vector4 tabActive)
    {
        bool isSelected = _editorState.SelectedBone == bone;

        ImGui.TableNextRow();

        // Apply selection highlight
        if (isSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabActive));
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabActive));
        }

        ImGui.TableNextColumn();

        float buttonSize = ImGui.GetFrameHeight();

        // Apply indentation
        var halfSpacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        for (int i = 0; i < depth; i++)
        {
            ImGui.Dummy(new Vector2(buttonSize + halfSpacing, buttonSize));
            ImGui.SameLine(0, 0);
        }

        // Dot or connector (no collapse for individual bones in this view)
        ImPoser.TextOverIconButton($"dot_{bone.Id}_hier", FontAwesomeIcon.CaretDown, hasChildren ? "├" : "·", new Vector2(buttonSize, buttonSize));

        ImGui.SameLine();

        // Bone icon
        ImGui.AlignTextToFramePadding();
        var boneIcon = hasChildren ? FontAwesomeIcon.CircleNodes : FontAwesomeIcon.Bone;
        ImPoser.FontIcon(boneIcon, UIConstants.SkeletonColor);

        ImGui.SameLine();

        // Bone name (clickable for selection)
        using (ImRaii.PushColor(ImGuiCol.Header, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, Vector4.Zero))
        {
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable($"{bone.Name}##{bone.Id}_hier", false))
            {
                _editorState.SelectBone(bone);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabHovered));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabHovered));
            }
        }

        // Empty columns
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
    }

    private void DrawAbstractSubcategoryHeader(EntityId skeletonId, BoneCategory category,
        BoneSubcategory subcategory, List<Bone> bones, int depth,
        HashSet<BoneSubcategory> collapsedSubs, Vector4 tabHovered, Vector4 tabActive, bool isCollapsed)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        float buttonSize = ImGui.GetFrameHeight();

        // Apply indentation
        var halfSpacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        for (int i = 0; i < depth; i++)
        {
            ImGui.Dummy(new Vector2(buttonSize + halfSpacing, buttonSize));
            ImGui.SameLine(0, 0);
        }

        // Collapse button
        var arrowIcon = isCollapsed ? FontAwesomeIcon.CaretRight : FontAwesomeIcon.CaretDown;
        if (ImPoser.IconButton($"subcat_{skeletonId}_{category}_{subcategory}", arrowIcon, new Vector2(buttonSize, buttonSize)))
        {
            if (collapsedSubs.Contains(subcategory))
                collapsedSubs.Remove(subcategory);
            else
                collapsedSubs.Add(subcategory);
        }

        ImGui.SameLine();

        // Subcategory icon (bone icon in light blue)
        ImGui.AlignTextToFramePadding();
        ImPoser.FontIcon(FontAwesomeIcon.Bone, UIConstants.SkeletonColor);

        ImGui.SameLine();

        // Subcategory name with count
        ImGui.AlignTextToFramePadding();
        ImGui.Text($"{BoneInfoService.GetSubcategoryDisplayName(subcategory)} ({bones.Count})");

        // Empty visibility/freeze columns
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();

        // Draw bones if not collapsed
        if (!isCollapsed)
        {
            foreach (var bone in bones.OrderBy(b => b.GetFriendlyName()))
            {
                DrawBoneInCategoryView(bone, category, depth + 1, tabHovered, tabActive);
            }
        }
    }

    private void DrawBoneInCategoryView(Bone bone, BoneCategory category, int depth, Vector4 tabHovered, Vector4 tabActive)
    {
        bool isSelected = _editorState.SelectedBone == bone;

        ImGui.TableNextRow();

        // Apply selection highlight
        if (isSelected)
        {
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabActive));
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabActive));
        }

        ImGui.TableNextColumn();

        float buttonSize = ImGui.GetFrameHeight();

        // Apply indentation: each level = button size + half item spacing
        var halfSpacing = ImGui.GetStyle().ItemSpacing.X * 0.5f;
        for (int i = 0; i < depth; i++)
        {
            ImGui.Dummy(new Vector2(buttonSize + halfSpacing, buttonSize));
            ImGui.SameLine(0, 0);
        }

        // Dot (no children in flat category view)
        ImPoser.TextOverIconButton($"dot_{bone.Id}", FontAwesomeIcon.CaretDown, "·", new Vector2(buttonSize, buttonSize));

        ImGui.SameLine();

        // Bone icon - circle-nodes if has children, bone otherwise
        ImGui.AlignTextToFramePadding();
        var boneIcon = bone.ChildBones.Count > 0 ? FontAwesomeIcon.CircleNodes : FontAwesomeIcon.Bone;
        ImPoser.FontIcon(boneIcon, UIConstants.SkeletonColor);

        ImGui.SameLine();

        // Bone name (clickable for selection)
        // Make Selectable completely transparent - row background handles all highlighting
        using (ImRaii.PushColor(ImGuiCol.Header, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderHovered, Vector4.Zero))
        using (ImRaii.PushColor(ImGuiCol.HeaderActive, Vector4.Zero))
        {
            ImGui.AlignTextToFramePadding();
            if (ImGui.Selectable($"{bone.Name}##{bone.Id}", false))
            {
                _editorState.SelectBone(bone);
            }

            // Set row background based on hover state
            if (ImGui.IsItemHovered())
            {
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg0, ImGui.GetColorU32(tabHovered));
                ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(tabHovered));
            }
        }

        // Empty columns
        ImGui.TableNextColumn();
        ImGui.TableNextColumn();
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
        // Handle bone selection
        if (entity is IBone clickedBone)
        {
            _editorState.SelectBone(clickedBone);
            return;
        }

        // Handle actor selection
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
            EntityType.Skeleton => FontAwesomeIcon.CircleNodes,
            EntityType.Bone => entity is Bone bone && bone.ChildBones.Count > 0
                ? FontAwesomeIcon.CircleNodes
                : FontAwesomeIcon.Bone,
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
