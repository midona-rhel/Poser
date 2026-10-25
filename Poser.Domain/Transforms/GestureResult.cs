using System;
using Poser.Domain.Operations;

namespace Poser.Domain.Transforms;

public readonly record struct TransformGestureId(Guid Value)
{
    public static TransformGestureId New() => new(Guid.NewGuid());
}

public readonly record struct GestureResult(
    bool Success,
    string? Detail = null,
    TransformGestureId? GestureId = null)
{
    public static GestureResult Ok(TransformGestureId? id = null) =>
        new(true, null, id);
    public static GestureResult Fail(string detail) =>
        new(false, detail);
    public static GestureResult From(Outcome outcome) => new(outcome.Success, outcome.Detail);

    /// <summary>
    /// Success and detail without the gesture ID or recovery information.
    /// </summary>
    public Outcome Outcome => new(Success, Detail);

    /// <summary>
    /// Results of restoration attempts. Excluded from this result's equality and hash.
    /// </summary>
    public TransformRecoveryReceipt? Recovery { get; init; }

    /// <summary>
    /// Detailed operation status. Excluded from equality, hashing and positional deconstruction.
    /// </summary>
    public OperationReceipt? OperationReceipt { get; init; }

    public bool Equals(GestureResult other) =>
        Success == other.Success &&
        Detail == other.Detail &&
        GestureId == other.GestureId;

    public override int GetHashCode() =>
        HashCode.Combine(Success, Detail, GestureId);
}
