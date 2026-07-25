using System.Numerics;

namespace Poser.Domain.Posing;

public enum IkSolver
{
    TwoJoint,
    Ccd,
}

public enum IkTargetMode
{
    /// <summary>Target follows animation: animated endpoint position plus
    /// the authored translation delta, evaluated every frame.</summary>
    Relative,

    /// <summary>Target holds the endpoint at an exact skeleton model-space
    /// point captured when the mode was entered or the chain enabled.</summary>
    Fixed,
}

/// <summary>
/// One validated per-chain IK configuration carrying BOTH solver settings so
/// switching solver never discards tuning. Session-only: never exported,
/// stashed, or recorded in transform history.
/// </summary>
public sealed record IkChainConfig(
    bool Enabled,
    bool EnforceConstraints,
    IkSolver Solver,
    IkTargetMode TargetMode,
    int CcdDepth,
    int CcdIterations,
    float CcdGain,
    float FirstJointGain,
    float SecondJointGain,
    float EndJointGain,
    float HingeMinDegrees,
    float HingeMaxDegrees,
    Vector3 HingeAxis,
    bool EnforceEndRotation)
{
    public const int MinDepth = 1;
    public const int MaxDepth = 20;
    public const int MinIterations = 1;
    public const int MaxIterations = 60;

    /// <summary>Null when valid, else the rejection reason. Invalid values
    /// never reach the native boundary.</summary>
    public string? Validate()
    {
        if (CcdDepth is < MinDepth or > MaxDepth)
            return $"CCD depth must be {MinDepth}..{MaxDepth}.";
        if (CcdIterations is < MinIterations or > MaxIterations)
            return $"CCD iterations must be {MinIterations}..{MaxIterations}.";
        if (!IsUnit(CcdGain) || !IsUnit(FirstJointGain) ||
            !IsUnit(SecondJointGain) || !IsUnit(EndJointGain))
            return "Gains must be finite values in 0..1.";
        if (!IsDegrees(HingeMinDegrees) || !IsDegrees(HingeMaxDegrees))
            return "Hinge angles must be finite values in 0..180.";
        if (HingeMinDegrees > HingeMaxDegrees)
            return "Hinge minimum cannot exceed the maximum.";
        if (!float.IsFinite(HingeAxis.X) || !float.IsFinite(HingeAxis.Y) ||
            !float.IsFinite(HingeAxis.Z) ||
            HingeAxis.LengthSquared() < 1e-6f)
            return "Hinge axis must be a non-zero finite vector.";
        return null;
    }

    /// <summary>The same configuration with a normalized hinge axis.</summary>
    public IkChainConfig Normalized() =>
        this with { HingeAxis = Vector3.Normalize(HingeAxis) };

    private static bool IsUnit(float value) =>
        float.IsFinite(value) && value is >= 0f and <= 1f;

    private static bool IsDegrees(float value) =>
        float.IsFinite(value) && value is >= 0f and <= 180f;

    /// <summary>Defaults preserving current Live IK behavior: Two Joint,
    /// Relative, constraints on, unit gains, full hinge range, end rotation
    /// off; CCD depth 3, iterations 8, gain 0.5.</summary>
    public static IkChainConfig DefaultsFor(bool isArm, bool enabled = false) => new(
        Enabled: enabled,
        EnforceConstraints: true,
        Solver: IkSolver.TwoJoint,
        TargetMode: IkTargetMode.Relative,
        CcdDepth: 3,
        CcdIterations: 8,
        CcdGain: 0.5f,
        FirstJointGain: 1f,
        SecondJointGain: 1f,
        EndJointGain: 1f,
        HingeMinDegrees: 0f,
        HingeMaxDegrees: 180f,
        HingeAxis: isArm ? Vector3.UnitZ : -Vector3.UnitZ,
        EnforceEndRotation: false);
}

/// <summary>
/// The four supported chain definitions. Every member resolves inside the
/// endpoint's exact skeleton and partial — never another slot.
/// </summary>
public sealed record IkChainDefinition(
    string Endpoint,
    IReadOnlyList<string> EndpointAliases,
    string FirstJoint,
    string? FirstTwist,
    string SecondJoint,
    string? SecondTwist,
    bool IsArm);

public static class IkChains
{
    // Arm: j_ude_a (twist n_hkata), j_ude_b (twist n_hhiji), end j_te
    // (Ktisis-compatible alias j_hand). Leg: j_asi_a, j_asi_b (twist
    // j_asi_c), end j_asi_d (alias j_foot) — the Ktisis Categories chains.
    private static readonly IkChainDefinition[] Definitions =
    {
        Arm("l"), Arm("r"), Leg("l"), Leg("r"),
    };

    public static readonly string[] SupportedEndpoints =
        { "j_te_l", "j_te_r", "j_asi_d_l", "j_asi_d_r" };

    private static IkChainDefinition Arm(string side) => new(
        $"j_te_{side}",
        new[] { $"j_hand_{side}" },
        $"j_ude_a_{side}",
        $"n_hkata_{side}",
        $"j_ude_b_{side}",
        $"n_hhiji_{side}",
        IsArm: true);

    private static IkChainDefinition Leg(string side) => new(
        $"j_asi_d_{side}",
        new[] { $"j_foot_{side}" },
        $"j_asi_a_{side}",
        null,
        $"j_asi_b_{side}",
        $"j_asi_c_{side}",
        IsArm: false);

    public static IkChainDefinition? ForEndpoint(string boneName)
    {
        foreach (var definition in Definitions)
        {
            if (definition.Endpoint == boneName)
                return definition;
            foreach (var alias in definition.EndpointAliases)
                if (alias == boneName)
                    return definition;
        }
        return null;
    }

    public static bool IsSupportedEndpoint(string boneName) =>
        ForEndpoint(boneName) != null;
}

/// <summary>One immutable solve request: target, optional end rotation,
/// the validated configuration, and the resolved chain.</summary>
public readonly record struct IkSolveRequest(
    Vector3 Target,
    Quaternion TargetRotation,
    IkChainConfig Config,
    IkResolvedChain Chain);

/// <summary>Native bone indices of one resolved chain (same skeleton, same
/// partial as the endpoint); -1 marks a missing optional twist.</summary>
public readonly record struct IkResolvedChain(
    short FirstJoint,
    short FirstTwist,
    short SecondJoint,
    short SecondTwist,
    short EndBone)
{
    public bool TwoJointAvailable =>
        FirstJoint >= 0 && SecondJoint >= 0 && EndBone >= 0;
}
