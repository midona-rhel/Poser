using System;
using System.Collections.Generic;
using Poser.Core;
using Poser.Entities;

namespace Poser.Services;

/// <summary>
/// One post-animation pose evaluation observed at the native boundary.
/// The animated baseline is captured after the game updates the skeleton and
/// before Poser applies persistent pose layers. The evaluated transform is
/// captured after those layers have been applied.
/// </summary>
public readonly record struct BoneEvaluationObservation(
    long Sequence,
    Transform AnimatedBaseline,
    Transform EvaluatedTransform,
    Transform AppliedDelta,
    int StackCount);

/// <summary>
/// Service for manipulating bone transforms.
/// Simple delta-based system like Brio - bones rotate around themselves.
/// </summary>
public interface IBonePosingService : IDisposable
{
    // Bone transform changes are published via EventBus: BoneTransformChangedEvent

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

    /// <summary>Captures the complete ordered pose stack for transform history.</summary>
    IReadOnlyList<BonePoseTransformInfo> CapturePoseStacks(IBone bone);

    /// <summary>
    /// Restores interactive stacks from a historical snapshot without replacing
    /// current named layers such as expression blending.
    /// </summary>
    void RestorePoseStacks(IBone bone, IReadOnlyList<BonePoseTransformInfo> stacks);

    /// <summary>The bone's current chain configuration (stored value or
    /// chain defaults); null when the bone is not a supported, resolvable
    /// IK endpoint on its own skeleton.</summary>
    Poser.Domain.Posing.IkChainConfig? GetIkConfiguration(IBone bone);

    /// <summary>Validates and stores the chain configuration; returns null
    /// on success or the rejection reason. Entering Fixed mode or enabling
    /// a Fixed chain captures the current effective target; disabling
    /// retains tuning but clears the capture.</summary>
    string? SetIkConfiguration(IBone bone, Poser.Domain.Posing.IkChainConfig config);

    /// <summary>Whether the endpoint's mandatory Two Joint chain resolves
    /// exactly on its own skeleton and partial.</summary>
    bool IsIkTwoJointAvailable(IBone bone);

    /// <summary>Disables and clears every chain configuration of the
    /// skeleton (Reset All).</summary>
    void ClearIkConfigurations(ISkeleton skeleton);

    /// <summary>
    /// Get the IK configuration for a bone.
    /// </summary>
    /// <summary>
    /// True when any bone on the skeleton has IK enabled (used to guard the
    /// post-import face reconcile, which would fight live IK).
    /// </summary>
    bool HasEnabledIk(ISkeleton skeleton);

    /// <summary>
    /// Register a skeleton for cache updates in the FinalizeSkeletons hook.
    /// Call this for skeletons with visible overlays or active gizmo manipulation.
    /// </summary>
    void RegisterSkeletonForCacheUpdate(ISkeleton skeleton);

    /// <summary>
    /// Keeps a skeleton in the per-frame apply pass for the next
    /// <paramref name="frames"/> framework ticks even when it carries no pose
    /// stack and no armed IK chain — the only way to guarantee that the
    /// per-bone transform caches an absolute write is diffed against are
    /// refreshed on a tick where the skeleton would otherwise go idle.
    /// </summary>
    void HoldSkeletonUpdates(ISkeleton skeleton, int frames);

    /// <summary>
    /// Gets the most recent pre-layer/post-layer observation for a modified
    /// concrete bone. Observations are produced only by the native skeleton
    /// update hook and therefore prove the runtime application path executed.
    /// </summary>
    bool TryGetEvaluationObservation(
        IBone bone,
        out BoneEvaluationObservation observation);

    /// <summary>
    /// Snapshot all bones in a skeleton at their current transforms.
    /// This freezes the entire skeleton including gaze bones.
    /// </summary>
    void SnapshotSkeleton(ISkeleton skeleton);

    /// <summary>
    /// Flips a bone's rotation (X = 180 - X, Y = -Y).
    /// Used to mirror pose on a single bone.
    /// </summary>
    void FlipBone(IBone bone);

    /// <summary>
    /// Mirrors the entire pose by swapping left/right bone transforms.
    /// </summary>
    void MirrorPose(ISkeleton skeleton);

    /// <summary>
    /// Gets the mirror bone name for a given bone (swaps _l and _r suffixes).
    /// Returns null if no mirror exists.
    /// </summary>
    string? GetMirrorBoneName(string boneName);

    /// <summary>
    /// Begin an orbit drag: the bones rotate around <paramref name="pivot"/>
    /// (typically the primary bone's parent). The session snapshots base
    /// transforms and existing stack deltas; feed it the TOTAL drag rotation
    /// each frame. Orbit now runs through the clean transform gesture with a
    /// frozen pivot; this comment block documents the retained pivot helpers.
    /// </summary>

    /// <summary>
    /// Linked bones (Anamnesis parity): posing one bone in a link set (both
    /// eyes; Viera ear-variant chains) applies the same delta to the others.
    /// Default on; per-session toggle.
    /// </summary>
    bool LinkedBonesEnabled { get; set; }

    /// <summary>Bulk IK (Ktisis parity): arm/disarm IK on every eligible chain end (hands/feet).</summary>


    /// <summary>Reset only the bones of one region: "body", "face" or "hair" (Anamnesis per-partial reference pose parity).</summary>
    int ResetRegion(ISkeleton skeleton, string region);
}
