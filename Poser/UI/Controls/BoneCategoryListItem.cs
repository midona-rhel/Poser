using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Poser.Data.Config;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tree list item for bone categories. Children are nested categories and bones.
/// </summary>
public class BoneCategoryListItem : TreeListItem
{
    private readonly BoneCategory _category;
    private readonly Skeleton _skeleton;
    private readonly ISelectionService _selectionService;
    private readonly List<Bone> _directBones = new();

    public bool HasContent { get; }

    public BoneCategoryListItem(BoneCategory category, Skeleton skeleton, int depth, ISelectionService selectionService)
        : base(depth)
    {
        _category = category;
        _skeleton = skeleton;
        _selectionService = selectionService;

        // Build bone lookup from skeleton
        var bonesByName = new Dictionary<string, Bone>();
        GatherBones(skeleton, bonesByName);

        // Get bones directly in this category
        foreach (var boneName in category.Bones)
        {
            if (bonesByName.TryGetValue(boneName, out var bone))
            {
                _directBones.Add(bone);
            }
        }

        // Add child categories
        foreach (var childCategory in category.Children)
        {
            // Skip NSFW categories unless setting is enabled
            if (childCategory.IsNsfw && !(PoserSettings.Instance?.ShowNsfwBones ?? false))
                continue;

            var childItem = new BoneCategoryListItem(childCategory, skeleton, depth + 1, selectionService);
            if (childItem.HasContent)
            {
                Children.Add(childItem);
            }
        }

        // Add bone items for direct bones
        foreach (var bone in _directBones.OrderBy(b => b.GetFriendlyName()))
        {
            Children.Add(new BoneListItem(bone, depth + 1, selectionService));
        }

        HasContent = Children.Count > 0;

        // Start collapsed
        IsCollapsed = true;
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

    public override string Id => $"{_skeleton.Id}_{_category.Id}";
    public override string Name => _category.DisplayName;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.CircleNodes;
    public override Vector4 IconColor => UIConstants.SkeletonColor;
    public override bool IsCollapsible => Children.Count > 0;
    public override bool ShowVisibilityCheckbox => GetAllBones().Any();
    public override bool ShowFreezeCheckbox => false;
    public override bool IsFrozen => false;

    public override bool IsVisible
    {
        get
        {
            var bones = GetAllBones().ToList();
            return bones.Count > 0 && bones.All(b => b.IsVisible);
        }
    }

    public override bool IsSelected(ISelectionService selection) => false;

    protected override void HandleResult(EntityListItemResult result, ISelectionService selection)
    {
        base.HandleResult(result, selection);

        if (result.Clicked)
        {
            // Select all bones in this category
            var bones = GetAllBones().ToList();
            if (bones.Count > 0)
            {
                selection.Select(bones[0]);
                for (int i = 1; i < bones.Count; i++)
                {
                    selection.AddToSelection(bones[i]);
                }
            }
        }

        if (result.VisibilityToggled)
        {
            foreach (var bone in GetAllBones())
            {
                bone.IsVisible = result.NewVisibilityValue;
            }
        }
    }

    /// <summary>
    /// Get all bones in this category and child categories recursively.
    /// </summary>
    public IEnumerable<Bone> GetAllBones()
    {
        // Return direct bones
        foreach (var bone in _directBones)
        {
            yield return bone;
        }

        // Return bones from child categories (not BoneListItems - those wrap _directBones)
        foreach (var child in Children)
        {
            if (child is BoneCategoryListItem childCategory)
            {
                foreach (var bone in childCategory.GetAllBones())
                {
                    yield return bone;
                }
            }
        }
    }
}
