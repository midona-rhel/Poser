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
/// Per-chain IK configuration carrying both solver settings so switching
/// solver never discards tuning. Validation is explicit; this is session-only
/// and is never exported, stashed, or recorded in transform history.
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
        if (Solver is not (IkSolver.TwoJoint or IkSolver.Ccd))
            return "IK solver is unsupported.";
        if (TargetMode is not (IkTargetMode.Relative or IkTargetMode.Fixed))
            return "IK target mode is unsupported.";
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
        if (!IsValidHingeAxis(HingeAxis))
            return "Hinge axis must be a non-zero finite vector.";
        return null;
    }

    /// <summary>The same valid configuration with a normalized hinge axis.</summary>
    public IkChainConfig Normalized()
    {
        if (Validate() is { } error)
            throw new ArgumentOutOfRangeException(nameof(HingeAxis), error);
        return this with { HingeAxis = NormalizeHingeAxis(HingeAxis) };
    }

    private static bool IsUnit(float value) =>
        float.IsFinite(value) && value is >= 0f and <= 1f;

    private static bool IsDegrees(float value) =>
        float.IsFinite(value) && value is >= 0f and <= 180f;

    private static bool IsValidHingeAxis(Vector3 value)
    {
        if (!Transforms.TransformMath.IsFinite(value))
            return false;

        var largest = MathF.Max(
            MathF.Abs(value.X),
            MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));
        if (largest <= 0f)
            return false;

        var scaled = value / largest;
        var scaledLength = MathF.Sqrt(scaled.LengthSquared());
        return float.IsFinite(scaledLength) &&
            scaledLength > 0f &&
            largest >= 0.001f / scaledLength;
    }

    private static Vector3 NormalizeHingeAxis(Vector3 value)
    {
        var largest = MathF.Max(
            MathF.Abs(value.X),
            MathF.Max(MathF.Abs(value.Y), MathF.Abs(value.Z)));
        var scaled = value / largest;
        var scaledLength = MathF.Sqrt(scaled.LengthSquared());
        return scaled / scaledLength;
    }

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
public sealed record IkChainDefinition
{
    public IkChainDefinition(
        string Endpoint,
        IReadOnlyList<string> EndpointAliases,
        string FirstJoint,
        string? FirstTwist,
        string SecondJoint,
        string? SecondTwist,
        bool IsArm)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Endpoint);
        ArgumentNullException.ThrowIfNull(EndpointAliases);
        if (EndpointAliases.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException(
                "IK endpoint aliases cannot be blank.",
                nameof(EndpointAliases));

        this.Endpoint = Endpoint;
        this.EndpointAliases = Array.AsReadOnly(EndpointAliases.ToArray());
        this.FirstJoint = FirstJoint;
        this.FirstTwist = FirstTwist;
        this.SecondJoint = SecondJoint;
        this.SecondTwist = SecondTwist;
        this.IsArm = IsArm;
    }

    public string Endpoint { get; }
    public IReadOnlyList<string> EndpointAliases { get; }
    public string FirstJoint { get; }
    public string? FirstTwist { get; }
    public string SecondJoint { get; }
    public string? SecondTwist { get; }
    public bool IsArm { get; }
}

public static class IkChains
{
    // Arm: j_ude_a (twist n_hkata), j_ude_b (twist n_hhiji), end j_te
    // (Ktisis-compatible alias j_hand). Leg: j_asi_a, j_asi_b (twist
    // j_asi_c), end j_asi_d (alias j_foot) — the Ktisis Categories chains.
    private static readonly IReadOnlyList<IkChainDefinition> Definitions =
        Array.AsReadOnly(new[]
        {
            Arm("l"), Arm("r"), Leg("l"), Leg("r"),
        });

    /// <summary>Read-only endpoint projection of the fixed definitions.</summary>
    public static IReadOnlyList<string> SupportedEndpoints { get; } =
        Array.AsReadOnly(Definitions.Select(definition => definition.Endpoint).ToArray());

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

    /// <summary>
    /// Validates exact fixed-chain structure, including endpoint, aliases,
    /// joint names, twist names, and arm/leg identity.
    /// </summary>
    public static bool IsSupportedDefinition(IkChainDefinition? definition)
    {
        if (definition is null)
            return false;

        return Definitions.Any(expected =>
            string.Equals(expected.Endpoint, definition.Endpoint, StringComparison.Ordinal) &&
            expected.EndpointAliases.SequenceEqual(
                definition.EndpointAliases,
                StringComparer.Ordinal) &&
            string.Equals(expected.FirstJoint, definition.FirstJoint, StringComparison.Ordinal) &&
            string.Equals(expected.FirstTwist, definition.FirstTwist, StringComparison.Ordinal) &&
            string.Equals(expected.SecondJoint, definition.SecondJoint, StringComparison.Ordinal) &&
            string.Equals(expected.SecondTwist, definition.SecondTwist, StringComparison.Ordinal) &&
            expected.IsArm == definition.IsArm);
    }

    public static bool IsSupportedEndpoint(string boneName) =>
        ForEndpoint(boneName) != null;
}

public enum IkPreset
{
    Defaults,
}

public enum IkPolicyOutcome
{
    Supported,
    UnsupportedEndpoint,
    UnsupportedPreset,
    InvalidConfiguration,
}

/// <summary>Pure fixed-preset/configuration decision with no native effects.</summary>
public readonly record struct IkPolicyResult(
    IkPolicyOutcome Outcome,
    IkChainDefinition? Definition,
    IkChainConfig? Configuration,
    string? Detail)
{
    public bool Success =>
        Outcome == IkPolicyOutcome.Supported &&
        IkChains.IsSupportedDefinition(Definition) &&
        Configuration is not null &&
        Configuration.Validate() is null &&
        Detail is null;

    internal static IkPolicyResult CreateSupported(
        IkChainDefinition definition,
        IkChainConfig configuration)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(configuration);
        if (!IkChains.IsSupportedDefinition(definition))
            throw new ArgumentException(
                "IK definition is not one of the fixed supported chains.",
                nameof(definition));
        if (configuration.Validate() is { } error)
            throw new ArgumentException(error, nameof(configuration));
        return new(
            IkPolicyOutcome.Supported,
            definition,
            configuration,
            null);
    }
}

/// <summary>Owns endpoint and configuration acceptance for fixed IK policy.</summary>
public static class IkPolicy
{
    public static IkPolicyResult Resolve(
        string? endpoint,
        IkPreset preset)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            IkChains.ForEndpoint(endpoint) is not { } definition)
        {
            return new(
                IkPolicyOutcome.UnsupportedEndpoint,
                null,
                null,
                "IK requires a supported IK endpoint.");
        }

        if (preset != IkPreset.Defaults)
        {
            return new(
                IkPolicyOutcome.UnsupportedPreset,
                definition,
                null,
                "Only the fixed default IK preset is supported.");
        }

        var configuration = IkChainConfig.DefaultsFor(definition.IsArm);
        var error = configuration.Validate();
        if (error is not null)
        {
            return new(
                IkPolicyOutcome.InvalidConfiguration,
                definition,
                null,
                error);
        }

        return IkPolicyResult.CreateSupported(
            definition,
            configuration.Normalized());
    }

    public static IkPolicyResult Validate(
        string? endpoint,
        IkChainConfig configuration)
    {
        if (string.IsNullOrWhiteSpace(endpoint) ||
            IkChains.ForEndpoint(endpoint) is not { } definition)
        {
            return new(
                IkPolicyOutcome.UnsupportedEndpoint,
                null,
                null,
                "IK requires a supported IK endpoint.");
        }

        var error = configuration.Validate();
        if (error is not null)
        {
            return new(
                IkPolicyOutcome.InvalidConfiguration,
                definition,
                null,
                error);
        }

        return IkPolicyResult.CreateSupported(
            definition,
            configuration.Normalized());
    }
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
