using System;
using Poser.Core;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Service for manipulating bone transforms.
/// Simple delta-based system like Brio - bones rotate around themselves.
/// </summary>
public interface IBonePosingService : IDisposable
{
    /// <summary>
    /// Event fired when a bone transform is modified.
    /// </summary>
    event Action<IBone>? OnBoneTransformChanged;

    /// <summary>
    /// Get the pose info for a skeleton.
    /// </summary>
    SkeletonPoseInfo GetPoseInfo(ISkeleton skeleton);

    /// <summary>
    /// Apply a transform to a bone. Calculates delta from original and stacks it.
    /// </summary>
    /// <param name="bone">The bone to transform.</param>
    /// <param name="newTransform">The new absolute transform.</param>
    /// <param name="originalTransform">The original transform before modification.</param>
    void ApplyTransform(IBone bone, Transform newTransform, Transform originalTransform);

    /// <summary>
    /// Reset a bone to its original pose.
    /// </summary>
    void ResetBone(IBone bone);

    /// <summary>
    /// Reset all bones in a skeleton to their original poses.
    /// </summary>
    void ResetSkeleton(ISkeleton skeleton);

    /// <summary>
    /// Check if a bone has any pose modifications.
    /// </summary>
    bool HasModifications(IBone bone);

    /// <summary>
    /// Get the current transform modification for a bone (if any).
    /// </summary>
    Transform? GetModification(IBone bone);

    /// <summary>
    /// Register a skeleton for cache updates in the FinalizeSkeletons hook.
    /// Call this for skeletons with visible overlays or active gizmo manipulation.
    /// </summary>
    void RegisterSkeletonForCacheUpdate(ISkeleton skeleton);

    /// <summary>
    /// Snapshot all bones in a skeleton at their current transforms.
    /// This freezes the entire skeleton including gaze bones.
    /// </summary>
    void SnapshotSkeleton(ISkeleton skeleton);
}
