using System;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Transform pivot - the center point around which transforms occur.
/// </summary>
public enum TransformPivot
{
    /// <summary>Transform around each object's own origin.</summary>
    Individual,
    /// <summary>Transform around the parent bone's position.</summary>
    Parent,
    /// <summary>Transform around the median center of all selected objects.</summary>
    Median
}

/// <summary>
/// Transform orientation - which coordinate axes to use for transforms.
/// </summary>
public enum TransformOrientation
{
    /// <summary>Use the object's local coordinate axes.</summary>
    Local,
    /// <summary>Use world coordinate axes.</summary>
    Global,
    /// <summary>Use the parent bone's coordinate axes.</summary>
    Parent
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
    /// <summary>Transform pivot - the center point for transforms.</summary>
    TransformPivot TransformPivot { get; set; }

    /// <summary>Transform orientation - which axes to use for transforms.</summary>
    TransformOrientation TransformOrientation { get; set; }

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
