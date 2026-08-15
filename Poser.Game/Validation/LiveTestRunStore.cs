using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Poser.Game.Validation;

/// <summary>
/// Persists the authoritative live-test verdict with atomic same-volume
/// replacement and recovers stale running reports after plugin restart.
/// </summary>
internal sealed class LiveTestRunStore
{
    public const string AuthorityFileName = "run.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _rootDirectory;

    public LiveTestRunStore(string pluginConfigDirectory)
    {
        _rootDirectory = Path.Combine(pluginConfigDirectory, "live-tests");
        Directory.CreateDirectory(_rootDirectory);
    }

    public LiveTestRunReport? ReadLatest()
    {
        if (!Directory.Exists(_rootDirectory))
            return null;

        foreach (var directory in Directory
                     .EnumerateDirectories(_rootDirectory)
                     .OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            var report = TryRead(directory);
            if (report != null)
                return report;
        }

        return null;
    }

    public LiveTestRunReport? RecoverLatestInterrupted()
    {
        var latest = ReadLatest();
        if (latest is not { Outcome: LiveTestRunOutcome.Running })
            return latest;

        var recovered = latest with
        {
            CompletedUtc = DateTimeOffset.UtcNow,
            Outcome = LiveTestRunOutcome.Interrupted,
            Detail =
                "A new Poser instance found this run still marked running; the prior game/plugin instance ended before recording a terminal verdict.",
            AcceptanceQualified = false,
        };
        Write(recovered);
        return recovered;
    }

    public LiveTestRunReport? TryRead(string artifactDirectory)
    {
        var path = Path.Combine(artifactDirectory, AuthorityFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<LiveTestRunReport>(
                File.ReadAllText(path),
                JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public void Write(LiveTestRunReport report)
    {
        Directory.CreateDirectory(report.ArtifactDirectory);
        var path = Path.Combine(
            report.ArtifactDirectory,
            AuthorityFileName);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
