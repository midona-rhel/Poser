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

    /// <summary>
    /// Returns success and detail without the affected-bone count or recovery data.
    /// </summary>
    public Outcome Outcome => new(Success, Detail);

    /// <summary>
    /// Details needed to retry an incomplete rollback. Not included in equality or hashing.
    /// </summary>
    public TransformRecoveryReceipt? Recovery { get; init; }

    /// <summary>
    /// Per-target operation results. Not included in equality, hashing, or deconstruction.
    /// </summary>
    public OperationReceipt? OperationReceipt { get; init; }

    public bool Equals(PoseEditResult other) =>
        Success == other.Success &&
        Affected == other.Affected &&
        Detail == other.Detail;

    public override int GetHashCode() =>
        HashCode.Combine(Success, Affected, Detail);
}
