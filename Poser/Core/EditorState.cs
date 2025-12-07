using Poser.Services;

namespace Poser.Core;

/// <summary>
/// Tracks editor-wide state like pivot mode, tool selection, etc.
/// </summary>
public class EditorState : IEditorState
{
    public PivotMode PivotMode { get; set; } = PivotMode.Local;
}
