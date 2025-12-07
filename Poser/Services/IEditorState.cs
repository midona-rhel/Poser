namespace Poser.Services;

/// <summary>
/// Pivot point mode for transform operations.
/// </summary>
public enum PivotMode
{
    /// <summary>Transform around each object's local origin.</summary>
    Local,
    /// <summary>Transform around world origin.</summary>
    World,
    /// <summary>Transform around the average center of selected objects.</summary>
    Average
}

/// <summary>
/// Tracks editor-wide state like pivot mode, tool selection, etc.
/// </summary>
public interface IEditorState
{
    /// <summary>Current pivot mode for transforms.</summary>
    PivotMode PivotMode { get; set; }
}
