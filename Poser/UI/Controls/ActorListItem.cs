using System.Numerics;
using Dalamud.Interface;
using Poser.Data.Config;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tree list item for actors (players, NPCs, companions, etc.)
/// </summary>
public class ActorListItem : TreeListItem
{
    private readonly IActor _actor;
    private readonly IAnimationService _animationService;

    public ActorListItem(
        IActor actor,
        int depth,
        IAnimationService animationService,
        ISkeletonService skeletonService,
        ISelectionService selectionService,
        CategoryConfig categoryConfig)
        : base(depth)
    {
        _actor = actor;
        _animationService = animationService;

        // Add skeleton as child if actor has one
        var skeleton = skeletonService.GetSkeleton(actor) as Skeleton;
        if (skeleton != null && skeleton.IsValid)
        {
            var skeletonItem = new SkeletonListItem(skeleton, depth + 1, categoryConfig, selectionService);
            Children.Add(skeletonItem);
        }
    }

    public override string Id => _actor.Id.ToString();
    public override string Name => _actor.Name;

    public override FontAwesomeIcon Icon => _actor.ActorKind switch
    {
        ActorKind.Player => FontAwesomeIcon.User,
        ActorKind.Companion => FontAwesomeIcon.Paw,
        ActorKind.Mount => FontAwesomeIcon.Horse,
        ActorKind.Ornament => FontAwesomeIcon.Gem,
        ActorKind.BattleNpc => FontAwesomeIcon.UserShield,
        ActorKind.EventNpc => FontAwesomeIcon.UserTie,
        ActorKind.Retainer => FontAwesomeIcon.Store,
        _ => FontAwesomeIcon.User
    };

    public override Vector4 IconColor => _actor.IsVisible
        ? UIConstants.DefaultIconColor
        : UIConstants.HiddenIconColor;

    public override bool IsCollapsible => Children.Count > 0;
    public override bool ShowVisibilityCheckbox => true;
    public override bool ShowFreezeCheckbox => true;
    public override bool IsFrozen => _animationService.IsFrozen(_actor);
    public override bool IsVisible => _actor.IsVisible;

    public override bool IsSelected(ISelectionService selection) => selection.IsSelected(_actor);

    protected override void HandleResult(EntityListItemResult result, ISelectionService selection)
    {
        base.HandleResult(result, selection);

        if (result.Clicked)
        {
            if (result.CtrlHeld)
                selection.ToggleSelection(_actor);
            else
                selection.Select(_actor);
        }

        if (result.FreezeToggled)
        {
            _animationService.ToggleFreeze(_actor);
        }

        if (result.VisibilityToggled)
        {
            _actor.IsVisible = result.NewVisibilityValue;
            SetVisibilityRecursive(result.NewVisibilityValue);
        }
    }

    public override void SetVisibilityRecursive(bool visible)
    {
        _actor.IsVisible = visible;
        base.SetVisibilityRecursive(visible);
    }
}
