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
    public TransformOrientation TransformOrientation { get; set; } = TransformOrientation.Local;
    public TransformTool TransformTool { get; set; } = TransformTool.Rotate;
    public bool DebugMode { get; set; } = false;
    public BoneDisplayMode BoneDisplayMode { get; set; } = BoneDisplayMode.Category;
    public SkeletonViewMode SkeletonViewMode { get; set; } = SkeletonViewMode.Default;
    public bool ShowSelectedBonesOnly { get; set; } = false;
    public SymmetryMode SymmetryMode { get; set; } = SymmetryMode.Off;
}
