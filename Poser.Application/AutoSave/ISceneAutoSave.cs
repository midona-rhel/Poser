namespace Poser.Application.AutoSave;

/// <summary>What the last whole-scene snapshot attempt did.</summary>
public enum SceneAutoSaveStatus
{
    /// <summary>Nothing has been attempted this session.</summary>
    Idle,

    /// <summary>A snapshot is on disk at <c>Path</c>.</summary>
    Written,

    /// <summary>The tick deliberately did nothing, with a stated reason —
    /// an empty scene, an unchanged scene, a running scene operation, a busy
    /// pose import.</summary>
    Skipped,

    /// <summary>Capture or the write refused; nothing new is on disk.</summary>
    Failed,

    /// <summary>The write left surviving temp/backup bytes whose fate is
    /// unknown. <c>RecoveryEvidencePaths</c> names every one.</summary>
    RecoveryRequired,
}

/// <summary>Immutable read model of the last snapshot attempt.</summary>
public sealed record SceneAutoSaveResult(
    SceneAutoSaveStatus Status,
    string Detail,
    string? Path = null,
    IReadOnlyList<string>? RecoveryEvidencePaths = null)
{
    public static readonly SceneAutoSaveResult Idle =
        new(SceneAutoSaveStatus.Idle, "No whole-scene snapshot has been taken yet.");

    public IReadOnlyList<string> Evidence =>
        RecoveryEvidencePaths ?? Array.Empty<string>();
}

/// <summary>Application-owned snapshot progress; observing it never drives the operation.</summary>
public interface ISceneAutoSave : IDisposable
{
    string RootDirectory { get; }
    SceneAutoSaveResult LastResult { get; }
    event Action? Changed;
}
