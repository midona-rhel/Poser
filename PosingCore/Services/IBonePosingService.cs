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
/// How a registered batch of transitive actions ended: <paramref name="Executed"/>
/// is true when an apply pass ran it, false when the interval ended without one
/// (the skeleton left the update set, gpose ended, the skeleton was replaced).
/// </summary>
public readonly record struct TransitiveActionOutcome(
    ISkeleton Skeleton,
    bool Executed);

/// <summary>
/// One bone carrying stored IK configuration, with the bones its solver moves
/// — the endpoint's declared joints and twists for Two Joint, or the parents
/// CCD walks into at the configured depth.
/// </summary>
public readonly record struct IkConfiguredChain(
    IBone Endpoint,
    Poser.Domain.Posing.IkChainConfig Config,
    IReadOnlyList<string> Bones);

/// <summary>
/// Service for manipulating bone transforms.
/// Simple delta-based system like Brio - bones rotate around themselves.
/// </summary>
public interface IBonePosingService : IDisposable
{
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

    /// <summary>The bone's current chain configuration (stored value or its
    /// defaults); null when IK cannot be armed here at all — a virtual bone,
    /// or a bone with no parent for CCD to walk into.</summary>
    Poser.Domain.Posing.IkChainConfig? GetIkConfiguration(IBone bone);

    /// <summary>Every bone of the skeleton that carries stored IK
    /// configuration, armed or not, each with the bones its solver moves.
    /// One enumeration per skeleton is what keeps the overlay and any
    /// all-on/all-off control off a per-bone probe now that CCD can be armed
    /// on any bone.</summary>
    IReadOnlyList<IkConfiguredChain> GetIkChains(ISkeleton skeleton);

    /// <summary>Validates and stores the chain configuration; returns null
    /// on success or the rejection reason. Entering Fixed mode or enabling
    /// a Fixed chain captures the current effective target; disabling
    /// retains tuning but clears the capture.</summary>
    string? SetIkConfiguration(IBone bone, Poser.Domain.Posing.IkChainConfig config);

    /// <summary>Whether the endpoint's mandatory Two Joint chain resolves
    /// exactly on its own skeleton and partial.</summary>
    bool IsIkTwoJointAvailable(IBone bone);

    /// <summary>Points the chain at another bone (Bone mode): the endpoint
    /// keeps the offset it has from that bone now and follows it. Null on
    /// success, else the refusal.</summary>
    string? SetIkBoneTarget(IBone endpoint, IBone target);

    /// <summary>The bone a Bone-mode chain follows, if one was picked.</summary>
    IBone? GetIkBoneTarget(IBone endpoint);

    /// <summary>Disables and clears every chain configuration of the
    /// skeleton (Reset All).</summary>
    void ClearIkConfigurations(ISkeleton skeleton);

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
    /// Keeps a skeleton in the APPLY pass for one frame so that
    /// <see cref="IBone.LastRawTransform"/> is refreshed for every one of its
    /// bones. Only the apply pass writes that cache, and the pass only visits
    /// skeletons that already carry a stack, an armed chain, or a registered
    /// batch — so a skeleton nobody has posed yet reports the value it was
    /// built with, forever. An operation that reads a raw basis across several
    /// frames (a bake settling) re-requests every tick it waits.
    ///
    /// The lease is one frame, and holds nothing: no stack is created, no
    /// action is queued, and the skeleton drops straight back out when the
    /// requests stop.
    /// </summary>
    void RequestRawTransformRefresh(ISkeleton skeleton);

    /// <summary>
    /// Registers an action to run INSIDE the next apply pass, once per bone of
    /// this slot skeleton, at the point where that bone's existing stacks have
    /// been applied and its transform caches refreshed — Brio's
    /// <c>SkeletonPosingCapability.RegisterTransitiveAction</c>.
    ///
    /// This is the only way to write an absolute value onto a bone whose
    /// correct basis is the pass's own running state: the action sees
    /// <see cref="IBone.LastRawTransform"/> already updated for this bone AND
    /// for every parent the same pass has already written, and any stack it
    /// appends is applied immediately, in that bone's turn, before the pass
    /// moves on to its children.
    ///
    /// Registering also guarantees the skeleton is in the pass that consumes
    /// the batch, even when it holds no stack and no armed chain. The batch is
    /// dropped when the posing interval ends, whether or not a pass ran it;
    /// <see cref="TransitiveActionsEnded"/> reports which.
    /// </summary>
    void RegisterTransitiveAction(
        ISkeleton skeleton,
        Action<IBone, BonePoseInfo> action);

    /// <summary>
    /// Raised once for every batch registered through
    /// <see cref="RegisterTransitiveAction"/> when the posing interval that
    /// owned it ends. Raised from the native skeleton hooks, NOT from the
    /// framework thread: a handler may record and defer, nothing more.
    /// </summary>
    event Action<TransitiveActionOutcome>? TransitiveActionsEnded;

    /// <summary>
    /// Gets the most recent pre-layer/post-layer observation for a modified
    /// concrete bone. Observations are produced only by the native skeleton
    /// update hook and therefore prove the runtime application path executed.
    /// </summary>
    bool TryGetEvaluationObservation(
        IBone bone,
        out BoneEvaluationObservation observation);

    /// <summary>
    /// Flips a bone's rotation (X = 180 - X, Y = -Y).
    /// Used to mirror pose on a single bone.
    /// </summary>
    void FlipBone(IBone bone);

    /// <summary>
    /// Gets the mirror bone name for a given bone (swaps _l and _r suffixes).
    /// Returns null if no mirror exists.
    /// </summary>
    string? GetMirrorBoneName(string boneName);

    /// <summary>
    /// Linked bones (Anamnesis parity): posing one bone in a link set (both
    /// eyes; Viera ear-variant chains) applies the same delta to the others.
    /// Default on; per-session toggle.
    /// </summary>
    bool LinkedBonesEnabled { get; set; }

}
