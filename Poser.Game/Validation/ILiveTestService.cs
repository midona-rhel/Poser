using System;
using System.Threading.Tasks;

namespace Poser.Game.Validation;

/// <summary>
/// Runs repeatable, scenario-based acceptance checks against the live game.
/// This is the authoritative verification boundary for native behavior.
/// </summary>
public interface ILiveTestService
{
    bool IsRunning { get; }
    string? LastRunDirectory { get; }
    LiveTestRunReport? LastRun { get; }

    void Cancel();

    Task<LiveTestRunReport> RunAsync(
        LiveTestOptions options,
        Action<string>? progress = null);
}
