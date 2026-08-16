using System;
using System.Collections.Generic;

namespace Poser.Game.Validation;

/// <summary>The durable lifecycle state of one live-test invocation.</summary>
public enum LiveTestRunOutcome
{
    Running,
    Succeeded,
    Failed,
    Incomplete,
    Cancelled,
    Interrupted,
    RunnerError,
}

/// <summary>
/// Authoritative machine-readable verdict for one live-test invocation.
/// Scenario events are evidence; this report alone defines run success.
/// </summary>
public sealed record LiveTestRunReport(
    int SchemaVersion,
    string RunId,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    LiveTestRunOutcome Outcome,
    string? Detail,
    LiveTestOptions Options,
    int ExpectedScenarioExecutions,
    int CompletedScenarioExecutions,
    int Passed,
    int Failed,
    int Skipped,
    bool RepetitionRequirementMet,
    bool AcceptanceQualified,
    IReadOnlyList<LiveTestResult> Results,
    string ArtifactDirectory)
{
    public const int CurrentSchemaVersion = 1;

    public bool IsTerminal => Outcome != LiveTestRunOutcome.Running;

    public bool IsSuccessful => Outcome == LiveTestRunOutcome.Succeeded;
}
