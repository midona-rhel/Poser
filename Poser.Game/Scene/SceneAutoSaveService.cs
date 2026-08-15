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

/// <summary>
/// Whole-scene crash insurance: on the ordinary auto-save cadence, the complete
/// scene is captured (read-only, pointer-free, on the framework thread) and
/// written to its OWN root, separate from the per-actor pose auto-saves,
/// through the same bounded atomic store the user-driven save uses.
///
/// It is deliberately not the user-facing <see cref="SceneWorkflow"/>: a
/// snapshot must never occupy that single-flight slot, clobber the progress a
/// user is reading, or publish a receipt for something the user did not ask
/// for. It instead SKIPS whenever a scene operation is running, so it can
/// never snapshot a half-restored scene.
///
/// A snapshot is a VERSION, so an interval that finds the scene exactly as the
/// last written snapshot left it writes nothing: the cadence exists to insure
/// against losing work, and re-filing an unchanged scene insures nothing while
/// pushing the retention window until it holds nothing but copies of one
/// moment. One user act therefore produces at most one snapshot, however long
/// they then leave the scene alone.
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
    /// <summary>ARMS a capture: the bone-transform caches a scene serializes
    /// are refreshed first and the outcome arrives through the callback a few
    /// ticks later, so a snapshot never files a never-posed actor's
    /// skeleton-build-time bones. Returns the refusal detail, or null when
    /// armed.</summary>
    private readonly Func<Guid, string?, Action<SceneCaptureOutcome>, string?> _capture;
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

    /// <summary>Content identity of the snapshot last written to disk, or null
    /// when this session has written none. Guarded by <see cref="_gate"/>: it
    /// is set on the writer and read there too.</summary>
    private string? _writtenSignature;

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
            capture.BeginCapture,
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
        Func<Guid, string?, Action<SceneCaptureOutcome>, string?> capture,
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
    /// ARMS the capture (this runs on the framework thread) and hands the
    /// immutable document to the writer once it lands. Only one write is in
    /// flight: a tick arriving over a running write is skipped by name rather
    /// than queued, so a slow disk can never grow an unbounded backlog.
    ///
    /// <para>The arm shares the ONE refresh slot with user-driven pose exports
    /// and scene saves, so a tick landing on top of one is REFUSED and skipped
    /// by name. A snapshot deferring to the user's own export is the right
    /// trade: the alternative is filing bone values the game never showed.</para>
    /// </summary>
    internal void SnapshotNow()
    {
        lock (_gate)
        {
            if (_writing)
            {
                Publish(new SceneAutoSaveResult(
                    SceneAutoSaveStatus.Skipped,
                    "The previous whole-scene snapshot is still being written."));
                return;
            }
        }

        if (_sceneOperationRunning())
        {
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Skipped,
                "A scene save or load is running; a snapshot now could capture a half-restored scene."));
            return;
        }

        if (_capture(_sceneId, "Automatic whole-scene snapshot", OnCaptured)
            is { } refusal)
        {
            Publish(new SceneAutoSaveResult(SceneAutoSaveStatus.Skipped, refusal));
        }
    }

    /// <summary>The armed capture landed: from here on this is exactly the
    /// path a synchronous capture took.</summary>
    private void OnCaptured(SceneCaptureOutcome captured)
    {
        if (_disposed)
            return;
        if (!captured.Success || captured.Scene is not { } scene)
        {
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Failed,
                captured.Detail ?? "The scene could not be captured."));
            return;
        }

        // Nothing to insure against: an empty scene writes no file and leaves
        // no folder behind, exactly as the pose auto-save does with no
        // qualifying actor.
        if (scene.Actors.Count == 0 && scene.Props.Count == 0 &&
            scene.Lights.Count == 0 && scene.Cameras.Count == 0)
        {
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Skipped,
                "The scene is empty; there is nothing to snapshot."));
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
            // The comparison runs HERE, on the writer, because that is where
            // the cost of describing a whole scene belongs — never on the
            // framework thread the capture ran on.
            var signature = Signature(scene);
            if (signature != null)
            {
                string? filed;
                lock (_gate)
                    filed = _writtenSignature;
                if (string.Equals(signature, filed, StringComparison.Ordinal))
                {
                    Publish(new SceneAutoSaveResult(
                        SceneAutoSaveStatus.Skipped,
                        "The scene is exactly as the last snapshot left it; " +
                        "there is nothing new to insure.",
                        null));
                    return;
                }
            }

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
                    $"The whole-scene snapshot could not be written: " +
                    $"{written.Failure!.Detail}",
                    path,
                    evidence));
                return;
            }

            lock (_gate)
                _writtenSignature = signature;
            Prune(Math.Max(1, Settings.MaxSceneSnapshots));
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Written,
                $"Snapshotted {scene.Actors.Count} actors, {scene.Props.Count} objects, " +
                $"{scene.Lights.Count} lights and {scene.Cameras.Count} cameras.",
                path));
        }
        catch (Exception ex)
        {
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Failed,
                $"The whole-scene snapshot failed unexpectedly: {ex.Message}"));
        }
        finally
        {
            lock (_gate)
                _writing = false;
        }
    }

    /// <summary>
    /// Content identity of one captured scene, with the capture stamp set
    /// aside: two ticks over an untouched scene differ only in when they ran,
    /// and that is not a change worth a file.
    ///
    /// <para>It hashes the WHOLE document, embedded poses included, rather
    /// than a summary of it: a summary that missed a moved bone would drop
    /// the user's work, which is far worse than a duplicate file. A document
    /// this cannot describe answers null, and a null signature never matches
    /// anything — an unreadable scene is written, not skipped.</para>
    /// </summary>
    private static string? Signature(SceneFile scene)
    {
        var savedAt = scene.SavedAt;
        scene.SavedAt = null;
        try
        {
            return Convert.ToHexString(System.Security.Cryptography.SHA256
                .HashData(System.Text.Json.JsonSerializer
                    .SerializeToUtf8Bytes(scene)));
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            scene.SavedAt = savedAt;
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
