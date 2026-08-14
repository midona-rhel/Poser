using Poser.Domain.Identity;
using Poser.Domain.Posing;

namespace Poser.Application.Posing;

public readonly record struct IkPortResult(
    bool Success,
    string? Detail = null)
{
    public static IkPortResult Ok() => new(true);
    public static IkPortResult Fail(string detail) => new(false, detail);
}

/// <summary>
/// One bone carrying IK configuration on a skeleton, with the canonical names
/// of the bones its solver moves. The list is what every all-chains surface
/// reads — the overlay's armed tinting, and any enable-all / disable-all
/// control — so none of them has to probe bone by bone.
/// </summary>
public readonly record struct IkChainSummary(
    BoneId Endpoint,
    IkChainConfig Config,
    IReadOnlyList<string> Bones);

/// <summary>
/// The ONE stable-id path the UI uses to read and write per-chain IK
/// configuration. Implemented by the game runtime; the UI never exchanges
/// retained entities, and configuration changes are rejected while a
/// transform gesture is active. IK stays session-only: no export, stash, or
/// history participation.
/// </summary>
public interface IIkConfigurationPort
{
    /// <summary>Whether IK can be armed on the target at all: a declared arm
    /// or leg endpoint, or — Brio's rule — any resolvable bone with a parent
    /// for CCD to bend.</summary>
    bool IsSupported(TransformTargetId target);

    /// <summary>Every configured chain on the skeleton, armed or not.</summary>
    IReadOnlyList<IkChainSummary> Chains(SkeletonId skeleton);

    /// <summary>Whether the endpoint's mandatory Two Joint chain resolves
    /// exactly; Two Joint is offered only when true.</summary>
    bool IsTwoJointAvailable(TransformTargetId target);

    /// <summary>The target's current configuration, or its chain defaults
    /// when none is stored; null for unsupported targets.</summary>
    IkChainConfig? Get(TransformTargetId target);

    /// <summary>Validates and stores the configuration. Entering Fixed mode
    /// or enabling a Fixed chain captures the current effective target.</summary>
    IkPortResult Set(TransformTargetId target, IkChainConfig config);

    /// <summary>Restores the chain's defaults while preserving its current
    /// Enabled state.</summary>
    IkPortResult ResetDefaults(TransformTargetId target);
}
