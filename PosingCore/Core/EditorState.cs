using System.Numerics;
using Poser.Services;

namespace Poser.Core;

/// <summary>
/// Tracks editor-wide state: gizmo settings.
/// UI components call methods directly.
///
/// NOTE: Selection is handled by the application SelectionSession, not here.
/// This class only tracks editor tool settings.
/// </summary>
public class EditorState : IEditorState
{
    public bool IkEnabled { get; set; }

    public TransformOrientation TransformOrientation { get; set; } = TransformOrientation.Local;
    public TransformTool TransformTool { get; set; } = TransformTool.Rotate;
    public bool DebugMode { get; set; } = false;
    public BoneDisplayMode BoneDisplayMode { get; set; } = BoneDisplayMode.Category;
    public SkeletonViewMode SkeletonViewMode { get; set; } = SkeletonViewMode.Default;
    public bool ShowSelectedBonesOnly { get; set; } = false;
    public SymmetryMode SymmetryMode { get; set; } = SymmetryMode.Off;

    public bool OrbitBoneRotation { get; set; }
    public OrbitPivotMode OrbitPivot { get; set; } = OrbitPivotMode.Parent;
    public Vector3 CustomOrbitPivot { get; set; }
    public OrbitStrategy OrbitStrategy { get; set; } = OrbitStrategy.SnapshotAbsolute;
}
