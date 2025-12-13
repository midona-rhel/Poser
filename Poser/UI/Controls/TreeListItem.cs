using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Base class for tree list items. Each item wraps data of type T,
/// knows its depth, can have children, and defines its own rendering properties.
/// </summary>
public abstract class TreeListItem
{
    public int Depth { get; }
    public List<TreeListItem> Children { get; } = new();
    public bool IsCollapsed { get; set; }

    protected TreeListItem(int depth)
    {
        Depth = depth;
    }

    // Abstract properties - subclasses define these
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract FontAwesomeIcon Icon { get; }
    public abstract Vector4 IconColor { get; }
    public abstract bool IsCollapsible { get; }
    public abstract bool ShowVisibilityCheckbox { get; }
    public abstract bool ShowFreezeCheckbox { get; }
    public abstract bool IsFrozen { get; }
    public abstract bool IsVisible { get; }
    public abstract bool IsSelected(ISelectionService selection);

    /// <summary>
    /// Draw this item and all children recursively.
    /// Must be called within a table context.
    /// </summary>
    public void Draw(ISelectionService selection)
    {
        var config = new EntityListItemConfig
        {
            Id = Id,
            Name = Name,
            Icon = Icon,
            IconColor = IconColor,
            Depth = Depth,
            IsSelected = IsSelected(selection),
            IsCollapsible = IsCollapsible,
            IsCollapsed = IsCollapsed,
            ShowFreezeCheckbox = ShowFreezeCheckbox,
            IsFrozen = IsFrozen,
            ShowVisibilityCheckbox = ShowVisibilityCheckbox,
            IsVisible = IsVisible
        };

        var result = EntityListItem.Draw(config);
        HandleResult(result, selection);

        // Draw children if not collapsed
        if (!IsCollapsed)
        {
            foreach (var child in Children)
            {
                child.Draw(selection);
            }
        }
    }

    /// <summary>
    /// Handle interaction results from the drawn item.
    /// Override in subclasses for custom behavior.
    /// </summary>
    protected virtual void HandleResult(EntityListItemResult result, ISelectionService selection)
    {
        if (result.CollapseToggled)
        {
            IsCollapsed = !IsCollapsed;
        }
    }

    /// <summary>
    /// Set visibility on this item and all children recursively.
    /// </summary>
    public virtual void SetVisibilityRecursive(bool visible)
    {
        foreach (var child in Children)
        {
            child.SetVisibilityRecursive(visible);
        }
    }
}
