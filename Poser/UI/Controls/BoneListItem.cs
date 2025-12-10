using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tree list item for individual bones. Leaf node, no children.
/// </summary>
public class BoneListItem : TreeListItem
{
    private readonly ISelectionService _selectionService;

    public Bone Bone { get; }

    public BoneListItem(Bone bone, int depth, ISelectionService selectionService)
        : base(depth)
    {
        Bone = bone;
        _selectionService = selectionService;
    }

    public override string Id => Bone.Id.ToString();
    public override string Name => Bone.GetFriendlyName();
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Circle;
    public override Vector4 IconColor => UIConstants.SkeletonColor;
    public override bool IsCollapsible => false;
    public override bool ShowVisibilityCheckbox => true;
    public override bool ShowFreezeCheckbox => false;
    public override bool IsFrozen => false;
    public override bool IsVisible => Bone.IsVisible;

    public override bool IsSelected(ISelectionService selection) => selection.IsSelected(Bone);

    protected override void HandleResult(EntityListItemResult result, ISelectionService selection)
    {
        base.HandleResult(result, selection);

        if (result.Clicked)
        {
            if (result.ShiftHeld && selection.LastClicked is IBone lastBone && lastBone.Skeleton == Bone.Skeleton)
            {
                // Shift-select: range within same skeleton only
                var displayOrder = Bone.Skeleton.Bones.Cast<IEntity>();
                selection.SelectRange(lastBone, Bone, displayOrder);
            }
            else if (result.CtrlHeld)
            {
                selection.ToggleSelection(Bone);
            }
            else
            {
                selection.Select(Bone);
            }
        }

        if (result.VisibilityToggled)
        {
            Bone.IsVisible = result.NewVisibilityValue;
        }
    }

    public override void SetVisibilityRecursive(bool visible)
    {
        Bone.IsVisible = visible;
        base.SetVisibilityRecursive(visible);
    }
}
