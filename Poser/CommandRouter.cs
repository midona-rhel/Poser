using Dalamud.Plugin.Services;
using Poser.Game.Validation;
using Poser.Services;
using System;
using System.Linq;

namespace Poser;

/// <summary>
/// Narrow text adapter for opening Poser and running the focused live harness.
/// Product features are exposed through the UI, not a parallel debug console.
/// </summary>
public sealed class CommandRouter
{
    private readonly IUIManager _ui;
    private readonly ILiveTestService _liveTests;
    private readonly IChatGui _chat;

    public CommandRouter(
        IUIManager ui,
        ILiveTestService liveTests,
        IChatGui chat)
    {
        _ui = ui;
        _liveTests = liveTests;
        _chat = chat;
    }

    public void Handle(string args)
    {
        var parts = args.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            _ui.ToggleMainWindow();
            return;
        }

        try
        {
            switch (parts[0].ToLowerInvariant())
            {
                case "test":
                case "selftest":
                    HandleLiveTest(parts);
                    break;
                case "help":
                    PrintHelp();
                    break;
                default:
                    Print($"Unknown command '{parts[0]}'.");
                    PrintHelp();
                    break;
            }
        }
        catch (Exception error)
        {
            Print($"Command failed: {error.Message}");
        }
    }

    private void HandleLiveTest(string[] parts)
    {
        if (IsArgument(parts, 1, "status"))
        {
            PrintLiveTestStatus(_liveTests.LastRun);
            return;
        }
        if (IsArgument(parts, 1, "cancel"))
        {
            if (!_liveTests.IsRunning)
            {
                Print("No live test suite is running.");
                return;
            }

            _liveTests.Cancel();
            Print("Live test cancellation requested.");
            return;
        }
        if (_liveTests.IsRunning)
        {
            Print("Live test suite is already running.");
            return;
        }

        var tokens = parts.Skip(1).ToArray();
        string? selector = null;
        int? requestedIterations = null;
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index].Equals(
                    "--iterations",
                    StringComparison.OrdinalIgnoreCase) &&
                index + 1 < tokens.Length &&
                int.TryParse(tokens[++index], out var parsed))
            {
                requestedIterations = parsed;
                continue;
            }

            selector ??= tokens[index];
        }

        bool full = selector?.Equals(
            "full",
            StringComparison.OrdinalIgnoreCase) == true;
        if (full)
            selector = null;
        else
            selector ??= LiveScenarioCatalog.BasicSelector;

        bool basic = !full && selector!.Equals(
            LiveScenarioCatalog.BasicSelector,
            StringComparison.OrdinalIgnoreCase);
        int iterations = requestedIterations ??
                         (basic
                             ? 1
                             : LiveTestOptions.AcceptanceIterations);
        var options = new LiveTestOptions
        {
            Selector = selector,
            Iterations = iterations,
        };
        string? displaySelector = basic
            ? "basic posing smoke suite"
            : full
                ? "focused rewrite gate"
                : selector;

        Print(
            $"Live test starting: {displaySelector}, " +
            $"{Math.Clamp(iterations, 1, 100)} iteration(s) each.");

        _ = _liveTests.RunAsync(options, Print).ContinueWith(task =>
        {
            if (task.IsFaulted)
            {
                Print(
                    $"Live test runner failed: " +
                    $"{task.Exception?.GetBaseException().Message}");
                return;
            }

            var report = task.Result;
            PrintLiveTestStatus(report);
            foreach (var result in report.Results.Where(
                         result => result.Passed == false))
            {
                Print(
                    $"  FAIL {result.ScenarioId} iteration " +
                    $"{result.Iteration}: {result.Detail}");
            }
        });
    }

    private void PrintLiveTestStatus(LiveTestRunReport? report)
    {
        if (report == null)
        {
            Print("No persisted live test run was found.");
            return;
        }

        Print(
            $"Live test {report.Outcome}: " +
            $"success={(report.IsSuccessful ? "yes" : "no")}, " +
            $"acceptance={(report.AcceptanceQualified ? "yes" : "no")}, " +
            $"scenarios={report.CompletedScenarioExecutions}/" +
            $"{report.ExpectedScenarioExecutions}, rows={report.Passed} passed/" +
            $"{report.Failed} failed/{report.Skipped} skipped.");
        if (!string.IsNullOrWhiteSpace(report.Detail))
            Print($"  {report.Detail}");
        Print($"  Artifacts: {report.ArtifactDirectory}");
    }

    private static bool IsArgument(
        string[] parts,
        int index,
        string expected) =>
        parts.ElementAtOrDefault(index)?.Equals(
            expected,
            StringComparison.OrdinalIgnoreCase) == true;

    private void PrintHelp()
        => Print(
            "Commands: /poser · /poser test " +
            "[basic|full|status|cancel|scenario] [--iterations N]");

    private void Print(string message)
        => _chat.Print($"[Poser] {message}");
}
