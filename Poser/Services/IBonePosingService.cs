using System;
using Poser.Core;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// Service for manipulating bone transforms.
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
    /// Apply a rotation delta to a bone.
    /// </summary>
    /// <param name="bone">The bone to rotate.</param>
    /// <param name="rotationDelta">The rotation delta to apply.</param>
    /// <param name="propagate">Whether to propagate to child bones.</param>
    void ApplyRotation(IBone bone, System.Numerics.Quaternion rotationDelta, bool propagate = true);

    /// <summary>
    /// Apply a position delta to a bone.
    /// </summary>
    /// <param name="bone">The bone to move.</param>
    /// <param name="positionDelta">The position delta to apply.</param>
    /// <param name="propagate">Whether to propagate to child bones.</param>
    void ApplyPosition(IBone bone, System.Numerics.Vector3 positionDelta, bool propagate = true);

    /// <summary>
    /// Apply a full transform to a bone.
    /// </summary>
    /// <param name="bone">The bone to transform.</param>
    /// <param name="transform">The transform delta to apply.</param>
    /// <param name="originalTransform">The original transform before modification (pass null to apply delta directly).</param>
    /// <param name="propagate">Components to propagate.</param>
    void ApplyTransform(IBone bone, Transform transform, Transform? originalTransform = null, TransformComponents propagate = TransformComponents.Position | TransformComponents.Rotation);

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
