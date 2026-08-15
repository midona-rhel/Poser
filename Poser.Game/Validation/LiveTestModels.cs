using System;
using System.Collections.Generic;

namespace Poser.Game.Validation;

/// <summary>Configuration for one live acceptance run.</summary>
public sealed record LiveTestOptions
{
    public const int AcceptanceIterations = 8;

    /// <summary>Optional group or full scenario id.</summary>
    public string? Selector { get; init; }

    /// <summary>Every selected scenario is executed this many times.</summary>
    public int Iterations { get; init; } = AcceptanceIterations;

}

/// <summary>One scenario iteration's durable result.</summary>
public sealed record LiveTestResult(
    string ScenarioId,
    string Group,
    string Name,
    int Iteration,
    bool? Passed,
    string Detail,
    double DurationMilliseconds,
    string? BeforeSnapshot,
    string? AfterSnapshot,
    IReadOnlyList<string> InvariantFailures);

/// <summary>JSON-friendly transform representation used in snapshots and reports.</summary>
public sealed record LiveTransformState(
    float PositionX, float PositionY, float PositionZ,
    float RotationX, float RotationY, float RotationZ, float RotationW,
    float ScaleX, float ScaleY, float ScaleZ)
{
    public static LiveTransformState From(Transform value) => new(
        value.Position.X, value.Position.Y, value.Position.Z,
        value.Rotation.X, value.Rotation.Y, value.Rotation.Z, value.Rotation.W,
        value.Scale.X, value.Scale.Y, value.Scale.Z);
}

public sealed record LivePoseStackState(
    string Components,
    string? Layer,
    LiveTransformState Transform);

public sealed record LiveBoneState(
    string Id,
    string Name,
    int PartialId,
    int BoneIndex,
    string? Parent,
    LiveTransformState Transform,
    LiveTransformState RawTransform,
    IReadOnlyList<LivePoseStackState> PoseStacks);

public sealed record LiveSkeletonState(
    string Id,
    string ActorId,
    bool IsValid,
    int BoneCount,
    IReadOnlyList<LiveBoneState> Bones);

public sealed record LiveActorState(
    string Id,
    long Address,
    string Name,
    string Kind,
    bool IsPosing,
    bool IsVisible,
    LiveTransformState Transform);

/// <summary>
/// Complete state captured at a scenario boundary. The test skeleton includes
/// every bone and every pose layer so a report can diagnose drift after the run.
/// </summary>
public sealed record LiveTestSnapshot(
    string SnapshotId,
    DateTimeOffset TimestampUtc,
    string ScenarioId,
    int Iteration,
    string Phase,
    IReadOnlyList<LiveActorState> Actors,
    IReadOnlyList<string> Selection,
    LiveSkeletonState? TestSkeleton);
