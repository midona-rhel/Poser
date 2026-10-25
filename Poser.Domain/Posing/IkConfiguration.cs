using System.Numerics;

namespace Poser.Domain.Posing;

public enum IkSolver
{
    TwoJoint,
    Ccd,
    /// <summary>
    /// Solves a chain by repeatedly reaching from its tip toward its root and back.
    /// </summary>
    Fabrik,
    /// <summary>A rope: both ends known, the links' total length fixed,
    /// the slack hanging as a catenary in the vertical plane through the
    /// ends; straight when the ends are further apart than the rope.</summary>
    Rope,
}

public enum IkTargetMode
{
    /// <summary>
    /// Keeps the target relative to the actor, with ActorAnchor recording how parent motion carries it.
    /// </summary>
    Actor,

    /// <summary>
    /// Keeps the target at a world position while the actor moves. Dragging changes that position.
    /// </summary>
    World,

    /// <summary>The target follows another bone, on any actor, keeping
    /// the offset the tip had from it when the bone was picked. A drag
    /// changes the offset.</summary>
    Bone,
    /// <summary>Follows a scene object's spatial point and rotation, retaining
    /// the endpoint's authored offset without requiring a skeleton.</summary>
    Entity,
}

/// <summary>
/// Settings and saved targets for one IK chain. Settings for inactive solvers
/// are retained so switching solvers does not discard their tuning.
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
    bool EnforceEndRotation,
    float SwivelDegrees = 0f,
    bool HoldRotation = true)
{
    public int ParentDepth { get; init; } = 3;
    public int ChildDepth { get; init; }
    public FabrikControl? Fabrik { get; init; }
    public IkActorAnchor? ActorAnchor { get; init; }
    /// <summary>Two Joint: the end bone as it stood at the last End rotation
    /// switch. Null until the first switch.</summary>
    public IkEndRotationAnchor? EndAnchor { get; init; }
    public bool Collisions { get; init; }
    /// <summary>
    /// Two Joint uses EnforceEndRotation; the other solvers use HoldRotation.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HoldsEndRotation => Solver == IkSolver.TwoJoint ? EnforceEndRotation : HoldRotation;
    /// <summary>World-space segment radius in yalms, independent of actor scale.</summary>
    public float CollisionRadius { get; init; } = .02f;
    public const int MinDepth = 1;
    /// <summary>
    /// CCD depth limit. A historical test reported nonfinite results at 40 bones;
    /// that observation does not establish the precise failing depth.
    /// </summary>
    public const int MaxCcdDepth = 20;
    public const int MaxDepth = 50;

    public static int MaxDepthFor(IkSolver solver) =>
        solver == IkSolver.Ccd ? MaxCcdDepth : MaxDepth;
    public const float MaxSwivelDegrees = 180f;
    public const int MinIterations = 1;
    public const int MaxIterations = 60;

    /// <summary>
    /// Returns the first configuration error, or null if these checks pass.
    /// Callers must perform this validation before using the settings.
    /// </summary>
    public string? Validate()
    {
        if (!float.IsFinite(CollisionRadius) || CollisionRadius is < 0 or > 5)
            return "Collision radius must be between 0 and 5 yalms.";
        if (ParentDepth < 0 || ChildDepth < 0 || ParentDepth + ChildDepth > MaxDepth)
            return $"FABRIK parent and child depth must total at most {MaxDepth} links.";
        if (Fabrik?.Validate() is { } fabrikError) return fabrikError;
        if (ActorAnchor is { } anchor && !anchor.IsValid)
            return "The actor IK target must contain finite transforms.";
        if (EndAnchor is { } endAnchor && !endAnchor.IsValid)
            return "The end rotation record must contain finite rotations.";
        if (Solver is not (IkSolver.TwoJoint or IkSolver.Ccd or IkSolver.Fabrik or IkSolver.Rope))
            return "IK solver is unsupported.";
        if (!float.IsFinite(SwivelDegrees)
            || SwivelDegrees is < -MaxSwivelDegrees or > MaxSwivelDegrees)
            return $"Swivel must be within ±{MaxSwivelDegrees}°.";
        if (TargetMode is not (IkTargetMode.Actor or IkTargetMode.World or IkTargetMode.Bone or IkTargetMode.Entity))
            return "IK target mode is unsupported.";
        if (CcdDepth is < MinDepth or > MaxDepth)
            return $"Depth must be {MinDepth}..{MaxDepth}.";
        if (Solver == IkSolver.Ccd && CcdDepth > MaxCcdDepth)
            return $"CCD bends {MaxCcdDepth} bones at most; FABRIK goes deeper.";
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

    /// <summary>
    /// Rejects Two Joint for bones without a declared arm or leg chain.
    /// The other solvers can walk the bone hierarchy without named limb joints.
    /// </summary>
    public string? ValidateUndeclared() =>
        Solver == IkSolver.TwoJoint
            ? "Only a chain solver (CCD or FABRIK) works on a bone with no arm or leg chain."
            : Validate();

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

    /// <summary>
    /// Default arm or leg settings, including Two Joint and enabled end rotation.
    /// </summary>
    public static IkChainConfig DefaultsFor(bool isArm, bool enabled = false) => new(
        Enabled: enabled,
        EnforceConstraints: true,
        Solver: IkSolver.TwoJoint,
        TargetMode: IkTargetMode.Actor,
        CcdDepth: 3,
        CcdIterations: 8,
        CcdGain: 0.5f,
        FirstJointGain: 1f,
        SecondJointGain: 1f,
        EndJointGain: 1f,
        HingeMinDegrees: 0f,
        HingeMaxDegrees: 180f,
        HingeAxis: isArm ? Vector3.UnitZ : -Vector3.UnitZ,
        EnforceEndRotation: true);

    /// <summary>
    /// Uses FABRIK by default for a bone without a declared arm or leg chain.
    /// </summary>
    public static IkChainConfig DefaultsForChain(bool enabled = false) =>
        DefaultsFor(isArm: true, enabled) with { Solver = IkSolver.Fabrik };
}

/// <summary>
/// Named joints and optional twist bones for an arm or leg.
/// The resolver must find them within the endpoint's skeleton and partial skeleton.
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
    // j_asi_c), end j_asi_d (alias j_foot): the Ktisis Categories chains.
    private static readonly IReadOnlyList<IkChainDefinition> Definitions =
        Array.AsReadOnly(new[]
        {
            Arm("l"), Arm("r"), Leg("l"), Leg("r"),
        });

    /// <summary>
    /// Primary endpoint names from the built-in limb definitions, excluding aliases.
    /// </summary>
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
}

/// <summary>
/// Target and solver settings, resolved joint indices, and optional collision state reused between solves.
/// </summary>
public readonly record struct IkSolveRequest(
    Vector3 Target,
    Quaternion TargetRotation,
    IkChainConfig Config,
    IkResolvedChain Chain,
    Vector3? RootTarget = null,
    Quaternion? RootRotation = null,
    Vector3? TipTarget = null,
    IIkCollisionState? CollisionState = null);

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
