using System.Numerics;
using Dalamud.Interface;
using Poser.Entities;
using Poser.Services;

namespace Poser.UI.Controls;

/// <summary>
/// Tree list item for pivot points in the entity hierarchy.
/// </summary>
public class PivotPointListItem : TreeListItem
{
    private readonly PivotPoint _pivot;
    private readonly ISelectionService _selectionService;
    private readonly IEditorState _editorState;

    public PivotPointListItem(
        PivotPoint pivot,
        int depth,
        ISelectionService selectionService,
        IEditorState editorState)
        : base(depth)
    {
        _pivot = pivot;
        _selectionService = selectionService;
        _editorState = editorState;
    }

    public override string Id => $"pivot_{_pivot.Id}";
    public override string Name => _pivot.Name;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Crosshairs;
    public override Vector4 IconColor => new(1.0f, 0.5f, 0.0f, 1.0f); // Orange
    public override bool IsCollapsible => false;
    public override bool ShowVisibilityCheckbox => true;
    public override bool ShowFreezeCheckbox => false;
    public override bool IsFrozen => false;
    public override bool IsVisible => _pivot.IsVisible;

    public override bool IsSelected(ISelectionService selection)
    {
        return _pivot == _editorState.OrbitTarget;
    }

    protected override void HandleResult(EntityListItemResult result, ISelectionService selection)
    {
        base.HandleResult(result, selection);

        if (result.Clicked)
        {
            // Clicking selects this as the orbit target and switches to Target pivot mode
            _editorState.OrbitTarget = _pivot;
            _editorState.TransformPivot = TransformPivot.Target;
        }

        if (result.VisibilityToggled)
        {
            _pivot.IsVisible = !_pivot.IsVisible;
        }
    }

    public PivotPoint Pivot => _pivot;
}
