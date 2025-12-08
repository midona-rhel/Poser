using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Poser.Core;
using Poser.Data;
using Poser.Data.Config;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Bone category list that displays bones in a hierarchical category structure.
/// Uses the reusable EntityListItem component for consistent UI.
/// </summary>
public class BoneCategoryList
{
    private readonly IEditorState _editorState;
    private readonly CategoryConfig _categoryConfig;
    private readonly Dictionary<EntityId, HashSet<string>> _collapsedCategories = new();

    public BoneCategoryList(IEditorState editorState)
    {
        _editorState = editorState;
        _categoryConfig = CategoryReader.ReadEmbeddedResource();
    }

    public void Draw(Skeleton skeleton, int baseDepth, Vector4 tabHovered, Vector4 tabActive)
    {
        // Get or create collapsed state for this skeleton
        // By default, all categories start collapsed
        if (!_collapsedCategories.TryGetValue(skeleton.Id, out var collapsedSet))
        {
            collapsedSet = new HashSet<string>();
            // Add all root categories as collapsed by default
            foreach (var cat in _categoryConfig.RootCategories)
            {
                AddAllCategoryIds(cat, collapsedSet);
            }
            _collapsedCategories[skeleton.Id] = collapsedSet;
        }

        // Build a lookup of bone name -> Bone entity
        var bonesByName = new Dictionary<string, Bone>();
        GatherBones(skeleton, bonesByName);

        // Draw root categories
        foreach (var category in _categoryConfig.RootCategories)
        {
            // Skip NSFW categories
            if (category.IsNsfw)
                continue;

            DrawCategory(skeleton, category, bonesByName, baseDepth, collapsedSet, tabHovered, tabActive);
        }
    }

    private void GatherBones(Skeleton skeleton, Dictionary<string, Bone> bonesByName)
    {
        void ProcessEntity(IEntity entity)
        {
            if (entity is Bone bone && !bone.IsHiddenBone)
            {
                if (!bonesByName.ContainsKey(bone.BoneName))
                {
                    bonesByName[bone.BoneName] = bone;
                }
            }

            foreach (var child in entity.Children)
            {
                ProcessEntity(child);
            }
        }

        foreach (var child in skeleton.Children)
        {
            ProcessEntity(child);
        }
    }

    private void DrawCategory(
        Skeleton skeleton,
        BoneCategory category,
        Dictionary<string, Bone> bonesByName,
        int depth,
        HashSet<string> collapsedSet,
        Vector4 tabHovered,
        Vector4 tabActive)
    {
        // Check if this category has any bones that exist in the skeleton
        var categoryBones = GetBonesInCategory(category, bonesByName);
        if (categoryBones.Count == 0 && category.Children.Count == 0)
            return;

        // Check for children that have bones
        bool hasVisibleChildren = category.Children.Any(c =>
            !c.IsNsfw && (GetBonesInCategory(c, bonesByName).Count > 0 || c.Children.Count > 0));

        // Skip empty categories with no visible children
        if (categoryBones.Count == 0 && !hasVisibleChildren)
            return;

        string categoryKey = $"{skeleton.Id}_{category.Id}";
        bool isCollapsed = !_editorState.DebugMode && collapsedSet.Contains(category.Id);
        bool hasContent = categoryBones.Count > 0 || hasVisibleChildren;
        bool isSelected = _editorState.IsCategorySelected(category.Id);

        // Get all bones for visibility toggle
        var allCategoryBones = GetAllBonesInCategoryRecursive(category, bonesByName);
        bool allVisible = allCategoryBones.Count > 0 && allCategoryBones.All(b => b.IsVisible);

        var config = new EntityListItemConfig
        {
            Id = categoryKey,
            Name = category.DisplayName,
            Icon = FontAwesomeIcon.CircleNodes,
            IconColor = UIConstants.SkeletonColor,
            Depth = depth,
            IsSelected = isSelected,
            IsCollapsible = hasContent,
            IsCollapsed = isCollapsed,
            ShowFreezeCheckbox = false,
            ShowVisibilityCheckbox = allCategoryBones.Count > 0,
            IsVisible = allVisible
        };

        var result = EntityListItem.Draw(config, tabHovered, tabActive);

        // Handle interactions
        if (result.CollapseToggled)
        {
            if (collapsedSet.Contains(category.Id))
                collapsedSet.Remove(category.Id);
            else
                collapsedSet.Add(category.Id);
        }

        if (result.Clicked)
        {
            // Select this category in EditorState
            _editorState.SelectCategory(category.Id, skeleton);
        }

        if (result.VisibilityToggled)
        {
            foreach (var bone in allCategoryBones)
            {
                bone.IsVisible = result.NewVisibilityValue;
            }
        }

        // Draw children if not collapsed
        if (!isCollapsed && hasContent)
        {
            // Draw child categories first
            foreach (var childCategory in category.Children)
            {
                if (childCategory.IsNsfw)
                    continue;

                DrawCategory(skeleton, childCategory, bonesByName, depth + 1, collapsedSet, tabHovered, tabActive);
            }

            // Then draw bones directly in this category
            foreach (var bone in categoryBones.OrderBy(b => b.GetFriendlyName()))
            {
                DrawBone(bone, depth + 1, tabHovered, tabActive);
            }
        }
    }

    private void DrawBone(Bone bone, int depth, Vector4 tabHovered, Vector4 tabActive)
    {
        bool isSelected = _editorState.IsSelected(bone);

        var config = new EntityListItemConfig
        {
            Id = bone.Id.ToString(),
            Name = bone.GetFriendlyName(),
            Icon = FontAwesomeIcon.Circle,
            IconColor = UIConstants.SkeletonColor,
            Depth = depth,
            IsSelected = isSelected,
            IsCollapsible = false,
            IsCollapsed = false,
            ShowFreezeCheckbox = false,
            ShowVisibilityCheckbox = true,
            IsVisible = bone.IsVisible
        };

        var result = EntityListItem.Draw(config, tabHovered, tabActive);

        if (result.Clicked)
        {
            if (result.CtrlHeld)
                _editorState.ToggleSelection(bone);
            else
                _editorState.Select(bone);
        }

        if (result.VisibilityToggled)
        {
            bone.IsVisible = result.NewVisibilityValue;
        }
    }

    private List<Bone> GetBonesInCategory(BoneCategory category, Dictionary<string, Bone> bonesByName)
    {
        var result = new List<Bone>();

        foreach (var boneName in category.Bones)
        {
            if (bonesByName.TryGetValue(boneName, out var bone))
            {
                result.Add(bone);
            }
        }

        return result;
    }

    private List<Bone> GetAllBonesInCategoryRecursive(BoneCategory category, Dictionary<string, Bone> bonesByName)
    {
        var result = new List<Bone>();

        foreach (var boneName in category.Bones)
        {
            if (bonesByName.TryGetValue(boneName, out var bone))
            {
                result.Add(bone);
            }
        }

        foreach (var child in category.Children)
        {
            if (!child.IsNsfw)
            {
                result.AddRange(GetAllBonesInCategoryRecursive(child, bonesByName));
            }
        }

        return result;
    }

    public void ClearState(EntityId skeletonId)
    {
        _collapsedCategories.Remove(skeletonId);
    }

    /// <summary>
    /// Get all bones in a category (and its children) for the given skeleton.
    /// Used by GizmoOverlayWindow to resolve category selection to bones.
    /// </summary>
    public List<Bone> GetBonesForCategory(string categoryId, Skeleton skeleton)
    {
        var bonesByName = new Dictionary<string, Bone>();
        GatherBones(skeleton, bonesByName);

        var category = FindCategory(categoryId, _categoryConfig.RootCategories);
        if (category == null)
            return new List<Bone>();

        return GetAllBonesInCategoryRecursive(category, bonesByName);
    }

    private BoneCategory? FindCategory(string categoryId, IEnumerable<BoneCategory> categories)
    {
        foreach (var cat in categories)
        {
            if (cat.Id == categoryId)
                return cat;

            var found = FindCategory(categoryId, cat.Children);
            if (found != null)
                return found;
        }
        return null;
    }

    private static void AddAllCategoryIds(BoneCategory category, HashSet<string> ids)
    {
        ids.Add(category.Id);
        foreach (var child in category.Children)
        {
            AddAllCategoryIds(child, ids);
        }
    }
}
