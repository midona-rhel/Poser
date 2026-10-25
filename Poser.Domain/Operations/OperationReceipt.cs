using Poser.Domain.Transforms;
using Poser.Domain.Identity;

namespace Poser.Domain.Operations;

/// <summary>
/// Identifies an application session. The default empty GUID is invalid.
/// This identifies a session; it does not indicate time or order.
/// </summary>
public readonly record struct SessionGeneration
{
    private SessionGeneration(Guid value) => Value = value;

    /// <summary>The session ID.</summary>
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
/// An operation sequence number maintained by one workflow.
/// Numbers from different workflows cannot establish a shared order; zero is invalid.
/// </summary>
public readonly record struct OperationEpoch : IComparable<OperationEpoch>, IComparable
{
    private OperationEpoch(ulong value) => Value = value;

    /// <summary>The sequence number within its workflow.</summary>
    public ulong Value { get; }

    /// <summary>Whether this value is a usable operation epoch.</summary>
    public bool IsValid => Value != 0;

    /// <summary>The first operation number in a workflow.</summary>
    public static OperationEpoch First => new(1);

    /// <summary>Creates a sequence number; zero is invalid.</summary>
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

    /// <summary>Returns the sequence number for tuple-style assignment.</summary>
    public void Deconstruct(out ulong value) => value = Value;

    public override string ToString() =>
        IsValid ? Value.ToString() : "default";
}

/// <summary>
/// Recorded operation status. Pending means unfinished; the other values describe
/// the attempt's result, including whether a separate recovery attempt is needed.
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
/// Result of one operation attempt, tied to its session and actor version.
/// Factories check required IDs and consistency between status and rollback data.
/// Recovery objects compare by reference, not by their individual contents.
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

    /// <summary>Sequence number within the workflow.</summary>
    public OperationEpoch OperationEpoch { get; }

    /// <summary>The exact application-session identity.</summary>
    public SessionGeneration SessionGeneration { get; }

    /// <summary>The exact Domain actor generation targeted by the operation.</summary>
    public ActorId TargetActorId { get; }

    /// <summary>The operation's validated state.</summary>
    public OperationReceiptState State { get; }

    /// <summary>Explanation shown to the user; failures require one.</summary>
    public string? Detail { get; }

    /// <summary>
    /// Rollback results. An incomplete rollback is allowed only with RecoveryRequired status.
    /// </summary>
    public TransformRecoveryReceipt? Recovery { get; }

    /// <summary>
    /// Creates a result after checking IDs, status, detail, and rollback data.
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
    /// Records a completed rollback, optionally including its results.
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
    /// Records an incomplete rollback with its results and an explanation of what remains.
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
    /// Records cancellation, optionally including completed rollback results.
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

    /// <summary>
    /// Returns all fields for tuple-style assignment.
    /// </summary>
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
