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

    /// <summary>Additive evidence, excluded from legacy positional equality.</summary>
    public TransformRecoveryReceipt? Recovery { get; init; }

    /// <summary>Additive operation evidence, excluded from legacy positional
    /// equality, hashing, and deconstruction.</summary>
    public OperationReceipt? OperationReceipt { get; init; }

    public bool Equals(GestureResult other) =>
        Success == other.Success &&
        Detail == other.Detail &&
        GestureId == other.GestureId;

    public override int GetHashCode() =>
        HashCode.Combine(Success, Detail, GestureId);
}
