using System.Collections.Generic;
using System.Numerics;
using Poser.Entities;

namespace Poser.Core;

/// <summary>
/// Shared state for bone gizmo drag operations.
/// Captures initial positions/rotations at drag start for accurate orbit calculations.
/// </summary>
public class DragState
{
    /// <summary>Gizmo transform at drag start.</summary>
    public Transform? DragStartGizmo { get; set; }

    /// <summary>Bone position relative to parent at drag start.</summary>
    public Dictionary<IBone, Vector3>? RelativePositions { get; set; }

    /// <summary>Bone position relative to gizmo at drag start (for Average pivot).</summary>
    public Dictionary<IBone, Vector3>? RelativeToGizmo { get; set; }

    /// <summary>Bone rotations at drag start.</summary>
    public Dictionary<IBone, Quaternion>? BoneRotations { get; set; }

    /// <summary>Parent position at drag start.</summary>
    public Dictionary<IBone, Vector3>? ParentPositions { get; set; }

    /// <summary>Full bone transforms at drag start.</summary>
    public Dictionary<IBone, Transform>? BoneTransforms { get; set; }

    /// <summary>Bone modifications at drag start (for history).</summary>
    public Dictionary<IBone, Transform>? StartModifications { get; set; }

    /// <summary>Whether a drag operation is currently active.</summary>
    public bool IsActive => StartModifications != null;

    /// <summary>
    /// Initialize drag state for a new drag operation.
    /// </summary>
    public void Initialize(Transform gizmoTransform)
    {
        DragStartGizmo = gizmoTransform;
        RelativePositions = new Dictionary<IBone, Vector3>();
        RelativeToGizmo = new Dictionary<IBone, Vector3>();
        BoneRotations = new Dictionary<IBone, Quaternion>();
        ParentPositions = new Dictionary<IBone, Vector3>();
        BoneTransforms = new Dictionary<IBone, Transform>();
        StartModifications = new Dictionary<IBone, Transform>();
    }

    /// <summary>
    /// Clear all drag state.
    /// </summary>
    public void Clear()
    {
        DragStartGizmo = null;
        RelativePositions = null;
        RelativeToGizmo = null;
        BoneRotations = null;
        ParentPositions = null;
        BoneTransforms = null;
        StartModifications = null;
    }
}
