using System.Linq;
using System.Numerics;
using Dalamud.Interface;
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

    public SkeletonListItem(Skeleton skeleton, int depth, CategoryConfig categoryConfig, ISelectionService selectionService)
        : base(depth)
    {
        _skeleton = skeleton;

        // Build children from category config
        foreach (var category in categoryConfig.RootCategories)
        {
            if (category.IsNsfw)
                continue;

            var categoryItem = new BoneCategoryListItem(category, skeleton, depth + 1, selectionService);
            if (categoryItem.HasContent)
            {
                Children.Add(categoryItem);
            }
        }

        // Start collapsed
        IsCollapsed = true;
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
