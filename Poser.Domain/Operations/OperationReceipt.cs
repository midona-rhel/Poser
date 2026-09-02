using Poser.Domain.Transforms;
using Poser.Domain.Identity;

namespace Poser.Domain.Operations;

/// <summary>
/// Exact logical identity of one application session.
///
/// The GUID is an identity token, not a clock, native address, or
/// <see cref="Poser.Application.Scene.SceneSession.Revision"/>. The default value is explicit
/// invalid state so callers must obtain a generated or validated value. Record
/// equality, hashing, and deconstruction all use the exact GUID; no ordering
/// is provided because session identity has no precedence relationship.
/// </summary>
public readonly record struct SessionGeneration
{
    private SessionGeneration(Guid value) => Value = value;

    /// <summary>The exact opaque identity of the session.</summary>
    public Guid Value { get; }

    /// <summary>Whether this value can identify an application session.</summary>
    public bool IsValid => Value != Guid.Empty;

    /// <summary>The invalid default value, useful for explicit absence checks.</summary>
    public static SessionGeneration Default => default;

    /// <summary>Creates a fresh identity without consulting runtime state.</summary>
    public static SessionGeneration New() => Create(Guid.NewGuid());

    /// <summary>Creates a session identity from a non-empty exact GUID.</summary>
    public static SessionGeneration Create(Guid value)
    {
        if (value == Guid.Empty)
            throw new ArgumentException(
                "A session generation requires a non-empty identity.",
                nameof(value));

        return new SessionGeneration(value);
    }

    /// <summary>Returns the exact identity token for value deconstruction.</summary>
    public void Deconstruct(out Guid value) => value = Value;

    public override string ToString() =>
        IsValid ? Value.ToString("D") : "default";
}

/// <summary>
/// A workflow-owner-local operation token. <see cref="First"/> and
/// <see cref="Next"/> define the only sequencing convention; the type has no
/// clock, registry, or shared mutable counter. Default is invalid, and numeric
/// comparison is intentional token ordering only, not global operation order.
/// </summary>
public readonly record struct OperationEpoch : IComparable<OperationEpoch>, IComparable
{
    private OperationEpoch(ulong value) => Value = value;

    /// <summary>The owner-local ordinal carried by the operation.</summary>
    public ulong Value { get; }

    /// <summary>Whether this value is a usable operation epoch.</summary>
    public bool IsValid => Value != 0;

    /// <summary>The first valid epoch for one owner-local sequence.</summary>
    public static OperationEpoch First => new(1);

    /// <summary>Creates a valid epoch from a non-zero owner-local ordinal.</summary>
    public static OperationEpoch Create(ulong value)
    {
        if (value == 0)
            throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "An operation epoch must be non-zero.");

        return new OperationEpoch(value);
    }

    /// <summary>
    /// Advances this owner's epoch. An invalid default cannot enter the
    /// sequence, and overflow is surfaced instead of wrapping to an old token.
    /// </summary>
    public OperationEpoch Next()
    {
        if (!IsValid)
            throw new InvalidOperationException(
                "An invalid operation epoch cannot be advanced.");

        return Create(checked(Value + 1));
    }

    public int CompareTo(OperationEpoch other) => Value.CompareTo(other.Value);

    int IComparable.CompareTo(object? obj) => obj switch
    {
        null => 1,
        OperationEpoch other => CompareTo(other),
        _ => throw new ArgumentException(
            $"Object must be an {nameof(OperationEpoch)}.",
            nameof(obj)),
    };

    public static bool operator <(OperationEpoch left, OperationEpoch right) =>
        left.CompareTo(right) < 0;

    public static bool operator <=(OperationEpoch left, OperationEpoch right) =>
        left.CompareTo(right) <= 0;

    public static bool operator >(OperationEpoch left, OperationEpoch right) =>
        left.CompareTo(right) > 0;

    public static bool operator >=(OperationEpoch left, OperationEpoch right) =>
        left.CompareTo(right) >= 0;

    /// <summary>Returns the owner-local ordinal for value deconstruction.</summary>
    public void Deconstruct(out ulong value) => value = Value;

    public override string ToString() =>
        IsValid ? Value.ToString() : "default";
}

/// <summary>
/// Current state of one operation receipt. Every state except Pending is
/// terminal; Pending is the explicit non-terminal acknowledgement state.
/// </summary>
public enum OperationReceiptState
{
    Pending,
    Applied,
    RolledBack,
    Failed,
    RecoveryRequired,
    Cancelled,
}

/// <summary>
/// Immutable Application-owned read model for one exact operation attempt.
/// Construction is private; the named factories and <see cref="Create"/>
/// validate identity and state/evidence combinations before a receipt exists.
/// Record equality includes the existing recovery receipt's reference identity,
/// because that receipt is the exact retry/evidence token. All properties are
/// get-only, so an empty <c>with</c> expression can make an equivalent copy but
/// cannot alter a valid receipt into a contradictory state. Deconstruction
/// exposes every carried field in the same order as the identity contract.
/// </summary>
public sealed record OperationReceipt
{
    private OperationReceipt(
        Guid operationId,
        OperationEpoch operationEpoch,
        SessionGeneration sessionGeneration,
        ActorId targetActorId,
        OperationReceiptState state,
        string? detail,
        TransformRecoveryReceipt? recovery)
    {
        OperationId = operationId;
        OperationEpoch = operationEpoch;
        SessionGeneration = sessionGeneration;
        TargetActorId = targetActorId;
        State = state;
        Detail = detail;
        Recovery = recovery;
    }

    /// <summary>The exact logical operation identity.</summary>
    public Guid OperationId { get; }

    /// <summary>The workflow owner's exact operation epoch.</summary>
    public OperationEpoch OperationEpoch { get; }

    /// <summary>The exact application-session identity.</summary>
    public SessionGeneration SessionGeneration { get; }

    /// <summary>The exact Domain actor generation targeted by the operation.</summary>
    public ActorId TargetActorId { get; }

    /// <summary>The operation's validated state.</summary>
    public OperationReceiptState State { get; }

    /// <summary>Optional human/read-model detail; failures require one.</summary>
    public string? Detail { get; }

    /// <summary>
    /// Existing immutable transform-recovery evidence. Incomplete evidence is
    /// legal only with <see cref="OperationReceiptState.RecoveryRequired"/>.
    /// </summary>
    public TransformRecoveryReceipt? Recovery { get; }

    /// <summary>
    /// Creates a receipt after enforcing exact identity and state/evidence
    /// invariants. This is a value factory, not an operation owner or manager.
    /// </summary>
    public static OperationReceipt Create(
        Guid operationId,
        OperationEpoch operationEpoch,
        SessionGeneration sessionGeneration,
        ActorId targetActorId,
        OperationReceiptState state,
        string? detail = null,
        TransformRecoveryReceipt? recovery = null)
    {
        Validate(
            operationId,
            operationEpoch,
            sessionGeneration,
            targetActorId,
            state,
            detail,
            recovery);
        return new OperationReceipt(
            operationId,
            operationEpoch,
            sessionGeneration,
            targetActorId,
            state,
            detail,
            recovery);
    }

    /// <summary>Creates a pending receipt without recovery evidence.</summary>
    public static OperationReceipt Pending(
        Guid operationId,
        OperationEpoch operationEpoch,
        SessionGeneration sessionGeneration,
        ActorId targetActorId,
        string? detail = null) =>
        Create(
            operationId,
            operationEpoch,
            sessionGeneration,
            targetActorId,
            OperationReceiptState.Pending,
            detail);

    /// <summary>Creates an applied terminal receipt.</summary>
    public static OperationReceipt Applied(
        Guid operationId,
        OperationEpoch operationEpoch,
        SessionGeneration sessionGeneration,
        ActorId targetActorId,
        string? detail = null) =>
        Create(
            operationId,
            operationEpoch,
            sessionGeneration,
            targetActorId,
            OperationReceiptState.Applied,
            detail);

    /// <summary>
    /// Creates a rolled-back receipt. Complete recovery evidence may be kept as
    /// proof of the restore sweep; incomplete evidence requires the recovery
    /// state instead.
    /// </summary>
    public static OperationReceipt RolledBack(
        Guid operationId,
        OperationEpoch operationEpoch,
        SessionGeneration sessionGeneration,
        ActorId targetActorId,
        string? detail = null,
        TransformRecoveryReceipt? recovery = null) =>
        Create(
            operationId,
            operationEpoch,
            sessionGeneration,
            targetActorId,
            OperationReceiptState.RolledBack,
            detail,
            recovery);

    /// <summary>Creates a failed terminal receipt with a required detail.</summary>
    public static OperationReceipt Failed(
        Guid operationId,
        OperationEpoch operationEpoch,
        SessionGeneration sessionGeneration,
        ActorId targetActorId,
        string? detail,
        TransformRecoveryReceipt? recovery = null) =>
        Create(
            operationId,
            operationEpoch,
            sessionGeneration,
            targetActorId,
            OperationReceiptState.Failed,
            detail,
            recovery);

    /// <summary>
    /// Creates a recovery-required receipt with required incomplete evidence and
    /// a detail describing the outstanding obligation.
    /// </summary>
    public static OperationReceipt RecoveryRequired(
        Guid operationId,
        OperationEpoch operationEpoch,
        SessionGeneration sessionGeneration,
        ActorId targetActorId,
        string? detail,
        TransformRecoveryReceipt? recovery) =>
        Create(
            operationId,
            operationEpoch,
            sessionGeneration,
            targetActorId,
            OperationReceiptState.RecoveryRequired,
            detail,
            recovery);

    /// <summary>
    /// Creates a cancelled receipt. Complete recovery evidence may document the
    /// restore performed during cancellation.
    /// </summary>
    public static OperationReceipt Cancelled(
        Guid operationId,
        OperationEpoch operationEpoch,
        SessionGeneration sessionGeneration,
        ActorId targetActorId,
        string? detail = null,
        TransformRecoveryReceipt? recovery = null) =>
        Create(
            operationId,
            operationEpoch,
            sessionGeneration,
            targetActorId,
            OperationReceiptState.Cancelled,
            detail,
            recovery);

    /// <summary>Deconstructs the complete immutable receipt read model.</summary>
    public void Deconstruct(
        out Guid operationId,
        out OperationEpoch operationEpoch,
        out SessionGeneration sessionGeneration,
        out ActorId targetActorId,
        out OperationReceiptState state,
        out string? detail,
        out TransformRecoveryReceipt? recovery)
    {
        operationId = OperationId;
        operationEpoch = OperationEpoch;
        sessionGeneration = SessionGeneration;
        targetActorId = TargetActorId;
        state = State;
        detail = Detail;
        recovery = Recovery;
    }

    private static void Validate(
        Guid operationId,
        OperationEpoch operationEpoch,
        SessionGeneration sessionGeneration,
        ActorId targetActorId,
        OperationReceiptState state,
        string? detail,
        TransformRecoveryReceipt? recovery)
    {
        if (operationId == Guid.Empty)
            throw new ArgumentException(
                "An operation receipt requires a non-empty operation id.",
                nameof(operationId));
        if (!operationEpoch.IsValid)
            throw new ArgumentException(
                "An operation receipt requires a valid operation epoch.",
                nameof(operationEpoch));
        if (!sessionGeneration.IsValid)
            throw new ArgumentException(
                "An operation receipt requires a valid session generation.",
                nameof(sessionGeneration));
        if (targetActorId.LogicalId == Guid.Empty)
            throw new ArgumentException(
                "An operation receipt requires an exact actor identity.",
                nameof(targetActorId));
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(
                nameof(state),
                state,
                "The operation receipt state is not defined.");

        if (state is OperationReceiptState.Failed or
            OperationReceiptState.RecoveryRequired)
        {
            RequireDetail(detail);
        }

        if (state is OperationReceiptState.Pending or
            OperationReceiptState.Applied)
        {
            if (recovery is not null)
                throw new ArgumentException(
                    $"{state} cannot carry recovery evidence.",
                    nameof(recovery));
        }

        if (state == OperationReceiptState.RecoveryRequired)
        {
            if (recovery is null)
                throw new ArgumentNullException(
                    nameof(recovery),
                    "RecoveryRequired requires recovery evidence.");
            if (recovery.Complete)
                throw new ArgumentException(
                    "RecoveryRequired requires incomplete recovery evidence.",
                    nameof(recovery));
        }
        else if (recovery is not null && !recovery.Complete)
        {
            throw new ArgumentException(
                "Incomplete recovery evidence requires RecoveryRequired state.",
                nameof(recovery));
        }
    }

    private static void RequireDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            throw new ArgumentException(
                "This operation state requires a non-empty detail.",
                nameof(detail));
    }
}
