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
/// The ONE stable-id path the UI uses to read and write per-chain IK
/// configuration. Implemented by the game runtime; the UI never exchanges
/// retained entities, and configuration changes are rejected while a
/// transform gesture is active. IK stays session-only: no export, stash, or
/// history participation.
/// </summary>
public interface IIkConfigurationPort
{
    /// <summary>Whether the target is a supported, resolvable IK endpoint
    /// on its own exact slot skeleton.</summary>
    bool IsSupported(TransformTargetId target);

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
