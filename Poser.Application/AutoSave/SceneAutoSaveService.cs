using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Poser.Config;
using Poser.Files;
using Poser.Application.Scene;

namespace Poser.Application.AutoSave;

/// <summary>Whole-scene autosave cadence, admission, deduplication and progress.</summary>
public sealed class SceneAutoSaveService : ISceneAutoSave
{
    private readonly ConfigurationService _configuration;
    /// <summary>ARMS a capture: the bone-transform caches a scene serializes
    /// are refreshed first and the outcome arrives through the callback a few
    /// ticks later, so a snapshot never files a never-posed actor's
    /// skeleton-build-time bones. Returns the refusal detail, or null when
    /// armed.</summary>
    private readonly Func<Guid, string?, Action<SceneCaptureOutcome>, string?> _capture;
    private readonly SceneAutoSaveStore _store;
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
        ConfigurationService configuration,
        Func<Guid, string?, Action<SceneCaptureOutcome>, string?> capture,
        Func<bool> sceneOperationRunning,
        SceneAutoSaveStore store,
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

    /// <summary>
    /// The same interval shape the pose auto-save uses: idle disarms the
    /// timer, and the first armed tick schedules one full interval out so
    /// entering GPose never snapshots immediately.
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
            if (!_dispatch(() => WriteAndPrune(scene, localNow, keep)))
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
            // The comparison runs HERE, on the writer, because that is where
            // the cost of describing a whole scene belongs — never on the
            // framework thread the capture ran on.
            var signature = SceneAutoSaveStore.Signature(scene);
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
                _writtenSignature = signature;
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
    }
}
