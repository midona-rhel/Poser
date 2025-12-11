using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Poser.Config;
using Poser.Data.Config;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tree list item for skeleton. Children are bone categories.
/// </summary>
public class SkeletonListItem : TreeListItem
{
    private readonly Skeleton _skeleton;
    private readonly CategoryConfig _categoryConfig;
    private readonly ISelectionService _selectionService;
    private readonly int _depth;

    public SkeletonListItem(Skeleton skeleton, int depth, CategoryConfig categoryConfig, ISelectionService selectionService)
        : base(depth)
    {
        _skeleton = skeleton;
        _categoryConfig = categoryConfig;
        _selectionService = selectionService;
        _depth = depth;

        // Build children from category config
        BuildCategories();

        // Start collapsed
        IsCollapsed = true;
    }

    /// <summary>
    /// Builds category children based on current NSFW setting.
    /// </summary>
    private void BuildCategories()
    {
        bool showNsfw = ConfigurationService.Instance?.Config.Display.ShowNsfwBones ?? false;

        foreach (var category in _categoryConfig.RootCategories)
        {
            // Skip NSFW categories unless setting is enabled
            if (category.IsNsfw && !showNsfw)
                continue;

            var categoryItem = new BoneCategoryListItem(category, _skeleton, _depth + 1, _selectionService);
            if (categoryItem.HasContent)
            {
                Children.Add(categoryItem);
            }
        }
    }

    /// <summary>
    /// Rebuilds category children based on current NSFW setting.
    /// Called when NSFW setting changes.
    /// </summary>
    public void RebuildCategories()
    {
        Children.Clear();
        BuildCategories();
    }

    /// <summary>
    /// Hides all bones that belong to NSFW categories.
    /// Called when NSFW setting is turned off.
    /// </summary>
    public void HideNsfwBones()
    {
        // Build bone lookup
        var bonesByName = new Dictionary<string, Bone>();
        GatherBones(_skeleton, bonesByName);

        // Iterate NSFW categories and hide their bones
        foreach (var category in _categoryConfig.RootCategories)
        {
            if (category.IsNsfw)
            {
                HideBonesInCategory(category, bonesByName);
            }
        }
    }

    private void HideBonesInCategory(BoneCategory category, Dictionary<string, Bone> bonesByName)
    {
        // Hide direct bones
        foreach (var boneName in category.Bones)
        {
            if (bonesByName.TryGetValue(boneName, out var bone))
            {
                bone.IsVisible = false;
            }
        }

        // Recurse into child categories
        foreach (var child in category.Children)
        {
            HideBonesInCategory(child, bonesByName);
        }
    }

    private void GatherBones(Skeleton skeleton, Dictionary<string, Bone> bonesByName)
    {
        void ProcessEntity(IEntity entity)
        {
            if (entity is Bone bone)
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

    public override string Id => _skeleton.Id.ToString();
    public override string Name => "Skeleton";
    public override FontAwesomeIcon Icon => FontAwesomeIcon.CircleNodes;
    public override Vector4 IconColor => UIConstants.SkeletonColor;
    public override bool IsCollapsible => Children.Count > 0;
    public override bool ShowVisibilityCheckbox => true;
    public override bool ShowFreezeCheckbox => false;
    public override bool IsFrozen => false;

    public override bool IsVisible
    {
        get
        {
            // Skeleton is visible if any bone is visible
            return GetAllBones().Any(b => b.IsVisible);
        }
    }

    public override bool IsSelected(ISelectionService selection) => selection.IsSelected(_skeleton);

    protected override void HandleResult(EntityListItemResult result, ISelectionService selection)
    {
        base.HandleResult(result, selection);

        if (result.Clicked)
        {
            if (result.CtrlHeld)
                selection.ToggleSelection(_skeleton);
            else
                selection.Select(_skeleton);
        }

        if (result.VisibilityToggled)
        {
            SetVisibilityRecursive(result.NewVisibilityValue);
        }
    }

    public override void SetVisibilityRecursive(bool visible)
    {
        foreach (var bone in GetAllBones())
        {
            bone.IsVisible = visible;
        }
        base.SetVisibilityRecursive(visible);
    }

    private System.Collections.Generic.IEnumerable<Bone> GetAllBones()
    {
        foreach (var child in Children)
        {
            if (child is BoneCategoryListItem category)
            {
                foreach (var bone in category.GetAllBones())
                {
                    yield return bone;
                }
            }
        }
    }
}
