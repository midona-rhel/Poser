using System.Collections.Generic;
using System.Numerics;
using Poser.Entities;
using Poser.Services;

namespace Poser.Core;

/// <summary>
/// Tracks editor-wide state: gizmo settings.
/// UI components call methods directly.
///
/// NOTE: Selection is handled by ISelectionService, not here.
/// This class only tracks editor tool settings.
/// </summary>
public class EditorState : IEditorState
{
    private readonly List<PivotPoint> _pivotPoints = new();

    public TransformPivot TransformPivot { get; set; } = TransformPivot.Local;
    public TransformOrientation TransformOrientation { get; set; } = TransformOrientation.Local;
    public TransformTool TransformTool { get; set; } = TransformTool.Rotate;
    public bool DebugMode { get; set; } = false;
    public BoneDisplayMode BoneDisplayMode { get; set; } = BoneDisplayMode.Category;
    public SymmetryMode SymmetryMode { get; set; } = SymmetryMode.Off;
    public SkeletonViewMode SkeletonViewMode { get; set; } = SkeletonViewMode.Dots;
    public bool ShowSelectedBonesOnly { get; set; } = false;

    public IEntity? OrbitTarget { get; set; }
    public IReadOnlyList<PivotPoint> PivotPoints => _pivotPoints.AsReadOnly();

    public PivotPoint CreatePivotPoint(Vector3 position, IBone? parentBone = null, string? name = null)
    {
        var pivotName = name ?? $"Pivot {_pivotPoints.Count + 1}";
        var pivot = new PivotPoint(position, pivotName);

        if (parentBone != null)
        {
            pivot.ParentBone = parentBone;
        }

        _pivotPoints.Add(pivot);
        return pivot;
    }

    public void DeletePivotPoint(PivotPoint pivotPoint)
    {
        if (OrbitTarget == pivotPoint)
            OrbitTarget = null;

        _pivotPoints.Remove(pivotPoint);
        pivotPoint.Dispose();
    }
}
