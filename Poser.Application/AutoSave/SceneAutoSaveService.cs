using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Poser.Application.Scene;
using Poser.Documents.AutoSave;
using Poser.Documents.Config;
using Poser.Documents.Files;
using Poser.Application.Settings;

namespace Poser.Application.AutoSave;

/// <summary>Schedules, writes, and reports whole-scene autosaves.</summary>
public sealed class SceneAutoSaveService : IDisposable
{
    private readonly ConfigurationService _configuration;
    /// <summary>Requests an asynchronous scene capture after refreshing pose
    /// caches. The callback receives the completed document. A non-null return
    /// explains why capture could not be started.</summary>
    private readonly Func<Guid, string?, Action<SceneCaptureOutcome>, string?> _capture;
    private readonly SceneAutoSaveStore _store;
    private readonly Action<string> _warning;
    private readonly Func<bool> _sceneOperationRunning;
    private readonly Func<DateTime> _clock;
    private readonly Func<Action, bool> _dispatch;
    private readonly object _gate = new();
    private readonly object _diskGate = new();
    private Guid? _cleanedSession;

    /// <summary>Stops future captures before actors are removed. If CleanOnExit
    /// is enabled, waits for the active write and then removes the snapshots.</summary>
    public void CompleteForExit()
    {
        _wasGPosing = false;
        _nextDueUtc = null;
        if (!Settings.Enabled || !Settings.CleanOnExit) return;
        lock (_diskGate)
        {
            _cleanedSession = _sceneId;
            _store.Prune(0);
            lock (_gate) _writtenIdentity = null;
        }
    }

    /// <summary>Document ID shared by snapshots from the current GPose session.</summary>
    private Guid _sceneId = Guid.NewGuid();

    private DateTime? _nextDueUtc;
    private bool _wasGPosing;
    private bool _writing;
    private bool _disposed;
    private SceneAutoSaveResult _lastResult = SceneAutoSaveResult.Idle;

    /// <summary>Content identity of the snapshot last written to disk, or null
    /// when this session has written none. Guarded by <see cref="_gate"/>: it
    /// is set on the writer and read there too.</summary>
    private SceneAutoSaveIdentity? _writtenIdentity;

    public SceneAutoSaveService(
        ConfigurationService configuration,
        Func<Guid, string?, Action<SceneCaptureOutcome>, string?> capture,
        Func<bool> sceneOperationRunning,
        SceneAutoSaveStore store,
        Action<string> warning,
        Func<DateTime>? utcClock = null,
        Func<Action, bool>? dispatch = null)
    {
        _configuration = configuration;
        _capture = capture;
        _sceneOperationRunning = sceneOperationRunning;
        RootDirectory = store.RootDirectory;
        _clock = utcClock ?? (() => DateTime.UtcNow);
        _dispatch = dispatch ?? (work =>
        {
            _ = Task.Run(work);
            return true;
        });
        _store = store;
        _warning = warning;
    }

    public string RootDirectory { get; }

    /// <summary>Most recent autosave result, used for status reporting and diagnostics.</summary>
    public SceneAutoSaveResult LastResult
    {
        get
        {
            lock (_gate)
                return _lastResult;
        }
    }

#if DEBUG
    /// <summary>Most recent writer result exposed to the debug bridge.</summary>
    public SceneAutoSaveResult DebugLastResult => LastResult;
#endif

    private AutoSaveConfiguration Settings => _configuration.Config.AutoSave;

    /// <summary>
    /// Disables the timer outside GPose. Entering GPose schedules the first
    /// snapshot after one complete interval.
    /// </summary>
    public void Tick(DateTime nowUtc, bool gposing)
    {
        if (gposing && !_wasGPosing)
        {
            // A new session is a new document: its snapshots must not claim
            // to be later versions of the previous session's scene.
            _sceneId = Guid.NewGuid();
        }
        _wasGPosing = gposing;

        var settings = Settings;
        if (_disposed || !settings.Enabled || !gposing)
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
    /// Starts one capture on the framework thread and passes the completed
    /// document to the writer. If a write, scene operation, or shared pose-cache
    /// refresh is already running, this autosave is reported as skipped rather
    /// than queued.
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

        var session = _sceneId;
        if (_capture(session, "Automatic whole-scene snapshot", captured =>
            {
                // A deferred capture may complete after exit/re-entry. Never
                // publish it as progress for the replacement session.
                if (!_disposed && _wasGPosing && session == _sceneId)
                    OnCaptured(captured);
            })
            is { } refusal)
        {
            Publish(new SceneAutoSaveResult(SceneAutoSaveStatus.Skipped, refusal));
        }
    }

    /// <summary>Handles a completed in-memory capture.</summary>
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

        // Skip scenes without any listed entities. Environment-only changes
        // do not satisfy this check.
        if (scene.Actors.Count == 0 && scene.Props.Count == 0 &&
            scene.Lights.Count == 0 && scene.Cameras.Count == 0 &&
            scene.Overlays is not { Count: > 0 } && scene.WorldObjects is not { Count: > 0 })
        {
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Skipped,
                "The scene is empty; there is nothing to snapshot."));
            return;
        }

        lock (_gate)
            _writing = true;

        var localNow = _clock().ToLocalTime();
        var keep = Math.Max(1, Settings.MaxSceneSnapshots);
        string? dispatchFailure = null;
        try
        {
            var session = _sceneId;
            if (!_dispatch(() =>
            {
                lock (_diskGate)
                {
                    if (_cleanedSession == session)
                    {
                        lock (_gate) _writing = false;
                        return;
                    }
                    WriteAndPrune(scene, localNow, keep);
                }
            }))
                dispatchFailure = "The snapshot writer could not be dispatched.";
        }
        catch (Exception ex)
        {
            dispatchFailure = $"The snapshot writer could not be dispatched: {ex.Message}";
        }
        if (dispatchFailure is not null)
        {
            lock (_gate)
                _writing = false;
            Publish(new SceneAutoSaveResult(
                SceneAutoSaveStatus.Failed,
                dispatchFailure));
        }
    }

    private void WriteAndPrune(SceneFile scene, DateTime localNow, int keep)
    {
        try
        {
            // Calculate the content identity on the writer thread because it
            // walks the complete scene document.
            var identity = SceneAutoSaveStore.CaptureIdentity(scene);
            if (identity != null)
            {
                SceneAutoSaveIdentity? filed;
                lock (_gate)
                    filed = _writtenIdentity;
                if (filed != null && SceneAutoSaveStore.SemanticallyEquals(identity, filed))
                {
                    Publish(new SceneAutoSaveResult(
                        SceneAutoSaveStatus.Skipped,
                        "The scene is exactly as the last snapshot left it; " +
                        "there is nothing new to insure.",
                        null));
                    return;
                }
            }

            var (path, written) = _store.Write(scene, localNow);
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
                _writtenIdentity = identity;
            _store.Prune(keep);
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

    /// <summary>Updates status and logs failures, including paths of files
    /// retained for recovery.</summary>
    private void Publish(SceneAutoSaveResult result)
    {
        lock (_gate)
            _lastResult = result;
        if (result.Status is SceneAutoSaveStatus.Failed or SceneAutoSaveStatus.RecoveryRequired)
        {
            var evidence = result.Evidence.Count > 0
                ? $" Recovery evidence: {string.Join(", ", result.Evidence)}"
                : string.Empty;
            var path = result.Path is { } target ? $" ({target})" : string.Empty;
            _warning($"Scene auto-save {result.Status}{path}: {result.Detail}{evidence}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
