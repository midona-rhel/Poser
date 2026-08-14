using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Config;
using Poser.Files;
using Poser.Services;

namespace Poser.Game.Scene;

/// <summary>What the last whole-shot snapshot attempt did.</summary>
public enum SceneAutoSaveStatus
{
    /// <summary>Nothing has been attempted this session.</summary>
    Idle,

    /// <summary>A snapshot is on disk at <c>Path</c>.</summary>
    Written,

    /// <summary>The tick deliberately did nothing, with a stated reason —
    /// an empty shot, a running scene operation, a busy pose import.</summary>
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
        new(SceneAutoSaveStatus.Idle, "No whole-shot snapshot has been taken yet.");

    public IReadOnlyList<string> Evidence =>
        RecoveryEvidencePaths ?? Array.Empty<string>();
}

/// <summary>
/// Whole-shot crash insurance: on the ordinary auto-save cadence, the complete
/// scene is captured (read-only, pointer-free, on the framework thread) and
/// written to its OWN root, separate from the per-actor pose auto-saves,
/// through the same bounded atomic store the user-driven save uses.
///
/// It is deliberately not the user-facing <see cref="SceneWorkflow"/>: a
/// snapshot must never occupy that single-flight slot, clobber the progress a
/// user is reading, or publish a receipt for something the user did not ask
/// for. It instead SKIPS whenever a scene operation is running, so it can
/// never snapshot a half-restored shot.
///
/// Layout mirrors the pose auto-saves' user-decided shape:
/// <c>&lt;pluginConfigDir&gt;/SceneAutoSaves/&lt;yyyy-MM-dd&gt;/&lt;HH-mm-ss&gt; Scene.poserscene</c>
/// — one folder per LOCAL day, 24-hour prefix so name order is time order.
/// Retention counts FILES from disk (one file is one save event here), newest
/// first by write date, and a day folder left empty goes with them.
/// </summary>
public sealed class SceneAutoSaveService : IDisposable
{
    private const string FolderName = "SceneAutoSaves";

    private readonly IPluginLog _log;
    private readonly IFramework? _framework;
    private readonly IGPoseService _gpose;
    private readonly ConfigurationService _configuration;
    private readonly Func<Guid, string?, SceneCaptureOutcome> _capture;
    private readonly SceneFileStore _store;
    private readonly Func<bool> _sceneOperationRunning;
    private readonly Func<DateTime> _clock;
    private readonly Func<Action, bool> _dispatch;
    private readonly object _gate = new();

    /// <summary>The document identity every snapshot of this session reuses,
    /// so successive snapshots are versions of ONE scene rather than
    /// unrelated documents.</summary>
    private Guid _sceneId = Guid.NewGuid();

    private DateTime? _nextDueUtc;
    private bool _wasGPosing;
    private bool _writing;
    private bool _disposed;
    private SceneAutoSaveResult _lastResult = SceneAutoSaveResult.Idle;

    public SceneAutoSaveService(
        IPluginLog log,
        IFramework framework,
        IGPoseService gpose,
        ConfigurationService configuration,
        SceneCaptureService capture,
        SceneWorkflow workflow,
        IDalamudPluginInterface pluginInterface)
        : this(
            log,
            framework,
            gpose,
            configuration,
            capture.Capture,
            () => workflow.Busy,
            System.IO.Path.Combine(
                pluginInterface.GetPluginConfigDirectory(), FolderName))
    {
    }

    /// <summary>Test seam: an explicit root, an injectable clock, an optional
    /// framework (null means the caller drives <see cref="Tick"/>), and an
    /// injectable dispatch so a test can run the write inline.</summary>
    internal SceneAutoSaveService(
        IPluginLog log,
        IFramework? framework,
        IGPoseService gpose,
        ConfigurationService configuration,
        Func<Guid, string?, SceneCaptureOutcome> capture,
        Func<bool> sceneOperationRunning,
        string rootDirectory,
        Func<DateTime>? utcClock = null,
        Func<Action, bool>? dispatch = null,
        SceneFileStore? store = null)
    {
        _log = log;
        _framework = framework;
        _gpose = gpose;
        _configuration = configuration;
        _capture = capture;
        _sceneOperationRunning = sceneOperationRunning;
        RootDirectory = rootDirectory;
        _clock = utcClock ?? (() => DateTime.UtcNow);
        _dispatch = dispatch ?? (work =>
        {
            _ = Task.Run(work);
            return true;
        });
        _store = store ?? SceneFileStore.Default;

        if (_framework is not null)
            _framework.Update += OnFrameworkUpdate;
    }

    public string RootDirectory { get; }

    public SceneAutoSaveResult LastResult
    {
        get
        {
            lock (_gate)
                return _lastResult;
        }
    }

    /// <summary>Raised after every published result; the UI reads the
    /// immutable record, never service internals.</summary>
    public event Action? Changed;

    private AutoSaveConfiguration Settings => _configuration.Config.AutoSave;

    private void OnFrameworkUpdate(IFramework framework) => Tick(_clock());

    /// <summary>
    /// The same interval shape the pose auto-save uses: idle disarms the
    /// timer, and the first armed tick schedules one full interval out so
    /// entering GPose never snapshots immediately.
    /// </summary>
    internal void Tick(DateTime nowUtc)
    {
        bool gposing = _gpose.IsGPosing;
        if (gposing && !_wasGPosing)
        {
            // A new session is a new document: its snapshots must not claim
            // to be later versions of the previous session's scene.
            _sceneId = Guid.NewGuid();
        }
        _wasGPosing = gposing;

        var settings = Settings;
        if (_disposed || !settings.Enabled || !settings.SceneSnapshots || !gposing)
        {
            _nextDueUtc = null;
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.IntervalSeconds));
        if (_nextDueUtc is null)
        {
            _nextDueUtc = nowUtc + interval;
            return;
        }
        if (nowUtc < _nextDueUtc.Value)
            return;
        _nextDueUtc = nowUtc + interval;

        SnapshotNow();
    }

    /// <summary>
    /// Captures the shot inline (this runs on the framework thread) and hands
    /// the immutable document to the writer. Only one write is in flight: a
    /// tick arriving over a running write is skipped by name rather than
    /// queued, so a slow disk can never grow an unbounded backlog.
    /// </summary>
    internal void SnapshotNow()
    {
        lock (_gate)
        {
            if (_writing)
            {
                Publish(new SceneAutoSaveResult(
                    SceneAutoSaveStatus.Skipped,
                    "The previous whole-shot snapshot is still being written."));
                return;
            }
        }

        if (_sceneOperationRunning())
        {
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Skipped,
                "A scene save or load is running; a snapshot now could capture a half-restored shot."));
            return;
        }

        var captured = _capture(_sceneId, "Automatic whole-shot snapshot");
        if (!captured.Success || captured.Scene is not { } scene)
        {
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Failed,
                captured.Detail ?? "The shot could not be captured."));
            return;
        }

        // Nothing to insure against: an empty shot writes no file and leaves
        // no folder behind, exactly as the pose auto-save does with no
        // qualifying actor.
        if (scene.Actors.Count == 0 && scene.Props.Count == 0 &&
            scene.Lights.Count == 0 && scene.Cameras.Count == 0)
        {
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Skipped,
                "The shot is empty; there is nothing to snapshot."));
            return;
        }

        lock (_gate)
            _writing = true;

        var localNow = _clock().ToLocalTime();
        if (!_dispatch(() => WriteAndPrune(scene, localNow)))
        {
            lock (_gate)
                _writing = false;
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Failed,
                "The snapshot writer could not be dispatched."));
        }
    }

    private void WriteAndPrune(SceneFile scene, DateTime localNow)
    {
        try
        {
            var folder = System.IO.Path.Combine(
                RootDirectory, localNow.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(folder);
            var path = UniquePath(folder, localNow);

            var written = _store.Write(scene, path);
            if (!written.Succeeded)
            {
                var evidence = written.RecoveryEvidencePaths;
                Publish(new SceneAutoSaveResult(
                    evidence.Count > 0
                        ? SceneAutoSaveStatus.RecoveryRequired
                        : SceneAutoSaveStatus.Failed,
                    $"The whole-shot snapshot could not be written: " +
                    $"{written.Failure!.Detail}",
                    path,
                    evidence));
                return;
            }

            Prune(Math.Max(1, Settings.MaxSceneSnapshots));
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Written,
                $"Snapshotted {scene.Actors.Count} actors, {scene.Props.Count} props, " +
                $"{scene.Lights.Count} lights and {scene.Cameras.Count} cameras.",
                path));
        }
        catch (Exception ex)
        {
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Failed,
                $"The whole-shot snapshot failed unexpectedly: {ex.Message}"));
        }
        finally
        {
            lock (_gate)
                _writing = false;
        }
    }

    /// <summary>A second snapshot inside the same second suffixes rather than
    /// overwriting — same convention the pose auto-save uses.</summary>
    private static string UniquePath(string folder, DateTime localNow)
    {
        string stem = $"{localNow:HH-mm-ss} Scene";
        string candidate = System.IO.Path.Combine(
            folder, stem + SceneFile.Extension);
        for (int suffix = 2; File.Exists(candidate) && suffix < 100; suffix++)
        {
            candidate = System.IO.Path.Combine(
                folder, $"{stem} ({suffix}){SceneFile.Extension}");
        }
        return candidate;
    }

    /// <summary>
    /// Retention from DISK, never from an in-memory list: one snapshot file is
    /// one save event, newest first by write date so the order survives a
    /// restart, and a day folder whose last snapshot is pruned goes with it.
    /// Every IO failure is logged with its path and never aborts the sweep.
    /// </summary>
    internal void Prune(int keep)
    {
        List<FileInfo> snapshots;
        try
        {
            var root = new DirectoryInfo(RootDirectory);
            if (!root.Exists)
                return;
            snapshots = root
                .EnumerateFiles("*" + SceneFile.Extension, SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.FullName, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Error($"Scene auto-save: could not enumerate '{RootDirectory}': {ex.Message}");
            return;
        }

        foreach (var stale in snapshots.Skip(keep))
        {
            try
            {
                stale.Delete();
            }
            catch (Exception ex)
            {
                _log.Error($"Scene auto-save: could not delete '{stale.FullName}': {ex.Message}");
            }
        }

        try
        {
            foreach (var day in new DirectoryInfo(RootDirectory).EnumerateDirectories())
            {
                try
                {
                    if (!day.EnumerateFileSystemInfos().Any())
                        day.Delete();
                }
                catch (Exception ex)
                {
                    _log.Error(
                        $"Scene auto-save: could not remove '{day.FullName}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Scene auto-save: could not sweep '{RootDirectory}': {ex.Message}");
        }
    }

    private void Publish(SceneAutoSaveResult result)
    {
        lock (_gate)
            _lastResult = result;
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // An observer failure never poisons the snapshot cadence.
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_framework is not null)
            _framework.Update -= OnFrameworkUpdate;
    }
}
