using System;
using Poser.Domain.Operations;
using Poser.Domain.Transforms;

namespace Poser.Domain.Posing;

public enum PoseRegion
{
    All,
    Body,
    Face,
    Hair,
}

public readonly record struct PoseEditResult(
    bool Success,
    int Affected,
    string? Detail = null)
{
    public static PoseEditResult Ok(int affected) =>
        new(true, affected);

    public static PoseEditResult Fail(string detail) =>
        new(false, 0, detail);

    /// <summary>Additive evidence, excluded from legacy positional equality.</summary>
    public TransformRecoveryReceipt? Recovery { get; init; }

    /// <summary>Additive operation evidence, excluded from legacy positional
    /// equality, hashing, and deconstruction.</summary>
    public OperationReceipt? OperationReceipt { get; init; }

    public bool Equals(PoseEditResult other) =>
        Success == other.Success &&
        Affected == other.Affected &&
        Detail == other.Detail;

    public override int GetHashCode() =>
        HashCode.Combine(Success, Affected, Detail);
}
