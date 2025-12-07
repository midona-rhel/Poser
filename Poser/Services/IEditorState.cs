using System;
using Poser.Entities;

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
/// Bone display mode for skeleton hierarchy.
/// </summary>
public enum BoneDisplayMode
{
    /// <summary>Show bones in their natural hierarchy.</summary>
    Hierarchy,
    /// <summary>Group bones by category (Head, Arms, Legs, etc.).</summary>
    Category
}

/// <summary>
/// Tracks editor-wide state like pivot mode, tool selection, etc.
/// </summary>
public interface IEditorState
{
    /// <summary>Current pivot mode for transforms.</summary>
    PivotMode PivotMode { get; set; }

    /// <summary>Debug mode - expands all entities and logs untranslated bones.</summary>
    bool DebugMode { get; set; }

    /// <summary>Bone display mode - hierarchy or category grouping.</summary>
    BoneDisplayMode BoneDisplayMode { get; set; }

    /// <summary>Currently selected bone (if any).</summary>
    IBone? SelectedBone { get; set; }

    /// <summary>Event fired when bone selection changes.</summary>
    event Action<IBone?>? OnBoneSelectionChanged;

    /// <summary>Select a bone.</summary>
    void SelectBone(IBone? bone);

    /// <summary>Clear bone selection.</summary>
    void ClearBoneSelection();
}
