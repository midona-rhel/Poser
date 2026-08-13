using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Config;
using Poser.Entities;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Timed pose auto-save (GAP 4). Exports every actor with Poser-authored edits
/// into
/// <c>&lt;pluginConfigDir&gt;/AutoSaves/&lt;local day&gt;/&lt;HH-mm-ss&gt; &lt;actor&gt;.pose</c>
/// while in GPose, and requests one final capture attempt on GPose exit.
///
/// <para>SPLIT ACROSS TWO THREADS. <see cref="SaveNow"/> runs on the framework
/// tick and does only what needs live game state: the authored-edit scan and
/// <see cref="IPoseFileService.CreatePoseFile"/>. A <see cref="PoseFile"/> is
/// plain data the moment it is built, so JSON, folder creation, the writes and
/// retention all run on a worker (<see cref="WriteSnapshot"/>) instead of
/// hitching a frame once per interval. <see cref="IPoseFileService.ExportPose"/>
/// is not used because it fuses the capture and the write.</para>
///
/// Deliberate deviations from the references:
/// <list type="bullet">
/// <item>ONE FOLDER PER DAY, files prefixed with the save time (user call,
/// 2026-08-08: both references write a folder per save, which at a one-minute
/// interval buries a session under dozens of sibling folders). Local time,
/// because the layout exists to be browsed and "that evening's folder" is a
/// local-calendar notion; the 24-hour prefix keeps name order == time order
/// within a day, and the same-second suffix in
/// <see cref="SnapshotFilePath"/> also covers the DST fold's replayed hour.</item>
/// <item>Retention is computed from what is on disk, not from an in-memory
/// queue, so it still holds after a plugin restart (Ktisis' does not). It
/// counts SAVE EVENTS — a time-prefix group of files, or one whole folder of
/// the old one-folder-per-save layout, which is how pre-existing snapshots
/// age out with no migration.</item>
/// <item>One actor failing to export never aborts the rest of the snapshot
/// (Brio aborts the whole save on a single bad filename).</item>
/// <item>Nothing is written when no actor has authored edits, so no empty
/// folders accumulate (Ktisis leaves them).</item>
/// </list>
///
/// <para>The application lifecycle coordinator requests exactly one final
/// capture attempt before publishing the legacy GPose exit event. The scene
/// services remain factories so the capture reads their current state without
/// making composition depend on an event-subscriber order.</para>
/// </summary>
public class AutoSaveService : IAutoSaveService
{
    private const string AutoSaveFolderName = "AutoSaves";
    private const string DayFolderFormat = "yyyy-MM-dd";
    private const string TimePrefixFormat = "HH-mm-ss";
    /// <summary>Rendered length of <see cref="TimePrefixFormat"/>.</summary>
    private const int TimePrefixLength = 8;

    private readonly IPluginLog _log;
    private readonly IFramework? _framework;
    private readonly IGPoseService _gpose;
    private readonly Func<IActorManager> _actors;
    private readonly Func<ISkeletonService> _skeletons;
    private readonly Func<IBonePosingService> _bonePosing;
    private readonly Func<IPoseFileService> _poseFiles;
    private readonly ConfigurationService _configuration;
    private readonly Func<DateTime> _clock;
    private readonly Func<Action, bool> _dispatch;

    private DateTime? _nextDueUtc;
    private bool _disposed;

    private readonly object _queueGate = new();
    private SnapshotJob? _pendingPeriodic;
    private SnapshotJob? _finalJob;
    private Task? _writerTask;
    private bool _writerRunning;
    private bool _exitReserved;
    private bool _exitCompleted;
    private bool _cleanOnExit;
    private bool _finalCaptureStarted;
    private AutoSaveCaptureResult _finalCapture;
    private bool _hasFinalCapture;
    private string? _workerFailure;
    private AutoSaveTerminalResult _lastTerminalResult =
        AutoSaveTerminalResult.PendingResult;

    public string RootDirectory { get; }

    public DateTime? LastSaveUtc { get; private set; }

    public AutoSaveTerminalResult LastTerminalResult
    {
        get
        {
            lock (_queueGate)
                return _lastTerminalResult;
        }
    }

    private readonly record struct SnapshotJob(
        string Reason,
        DateTime NowUtc,
        int Keep,
        IReadOnlyList<CapturedPose> Captured,
        bool IsFinal);

    private readonly record struct WorkerResult(bool Success, string? Detail);

    public AutoSaveService(
        IPluginLog log,
        IFramework framework,
        IGPoseService gpose,
        Func<IActorManager> actors,
        Func<ISkeletonService> skeletons,
        Func<IBonePosingService> bonePosing,
        Func<IPoseFileService> poseFiles,
        ConfigurationService configuration,
        IDalamudPluginInterface pluginInterface)
        : this(
            log,
            framework,
            gpose,
            actors,
            skeletons,
            bonePosing,
            poseFiles,
            configuration,
            Path.Combine(pluginInterface.GetPluginConfigDirectory(), AutoSaveFolderName))
    {
    }

    /// <summary>
    /// Test seam: explicit root directory, an injectable UTC clock, and an
    /// optional framework (null means the caller drives <see cref="Tick"/>
    /// itself instead of the service subscribing to the game tick).
    /// </summary>
    internal AutoSaveService(
        IPluginLog log,
        IFramework? framework,
        IGPoseService gpose,
        Func<IActorManager> actors,
        Func<ISkeletonService> skeletons,
        Func<IBonePosingService> bonePosing,
        Func<IPoseFileService> poseFiles,
        ConfigurationService configuration,
        string rootDirectory,
        Func<DateTime>? utcClock = null,
        Func<Action, bool>? dispatch = null)
    {
        _log = log;
        _framework = framework;
        _gpose = gpose;
        _actors = actors;
        _skeletons = skeletons;
        _bonePosing = bonePosing;
        _poseFiles = poseFiles;
        _configuration = configuration;
        _clock = utcClock ?? (() => DateTime.UtcNow);
        _dispatch = dispatch ?? (work =>
        {
            _ = Task.Run(work);
            return true;
        });
        RootDirectory = rootDirectory;

        try
        {
            Directory.CreateDirectory(RootDirectory);
        }
        catch (Exception ex)
        {
            // A missing root is not fatal at construction: each save retries the
            // per-snapshot create and logs there.
            _log.Error($"Auto-save: could not create '{RootDirectory}': {ex.Message}");
        }

        if (_framework != null)
            _framework.Update += OnFrameworkUpdate;
    }

    private AutoSaveConfiguration Settings => _configuration.Config.AutoSave;

    private void ResetCompletedExitForNewSession()
    {
        lock (_queueGate)
        {
            if (!_exitCompleted || _writerRunning)
                return;

            _exitReserved = false;
            _exitCompleted = false;
            _cleanOnExit = false;
            _finalCaptureStarted = false;
            _hasFinalCapture = false;
            _workerFailure = null;
            _lastTerminalResult = AutoSaveTerminalResult.PendingResult;
        }
    }

    private void OnFrameworkUpdate(IFramework framework) => Tick(_clock());

    /// <summary>
    /// Interval logic. Idle (not enabled, or not in GPose) disarms the timer;
    /// the first tick after arming schedules one full interval out, so entering
    /// GPose never saves immediately (parity with both references).
    /// </summary>
    internal void Tick(DateTime nowUtc)
    {
        var settings = Settings;
        if (!settings.Enabled || !_gpose.IsGPosing)
        {
            _nextDueUtc = null;
            return;
        }

        ResetCompletedExitForNewSession();
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.IntervalSeconds));

        if (_nextDueUtc is null)
        {
            _nextDueUtc = nowUtc + interval;
            return;
        }

        if (nowUtc < _nextDueUtc.Value)
            return;

        _nextDueUtc = nowUtc + interval;
        SaveNow("interval");
    }

    /// <summary>
    /// Reserves exactly one final-capture attempt for this exit edge. The
    /// reservation is independent of an active periodic write; the immutable
    /// final job waits behind that write. A duplicate call returns the original
    /// compatibility result without recapturing live state.
    /// </summary>
    public AutoSaveCaptureResult CaptureForExit()
    {
        lock (_queueGate)
        {
            if (_hasFinalCapture)
                return _finalCapture;
            if (_finalCaptureStarted)
                return AutoSaveCaptureResult.NotCaptured(
                    "Final auto-save capture is already in progress.");

            _finalCaptureStarted = true;
            _exitReserved = true;
            _cleanOnExit = Settings.Enabled && Settings.CleanOnExit;
            _nextDueUtc = null;
            _pendingPeriodic = null;
        }

        var settings = Settings;
        AutoSaveCaptureResult result;
        if (!settings.Enabled)
        {
            result = AutoSaveCaptureResult.NotCaptured("Auto-save is disabled.");
        }
        else if (settings.CleanOnExit)
        {
            result = AutoSaveCaptureResult.NotCaptured(
                "Clean-on-exit is enabled; cleanup is pending.");
        }
        else
        {
            result = CaptureAndDispatch("gpose-exit", isFinal: true);
        }

        lock (_queueGate)
        {
            _finalCapture = result;
            _hasFinalCapture = true;
            _lastTerminalResult = AutoSaveTerminalResult.PendingResult;
        }

        // Clean-on-exit has no final pose reservation, but direct callers still
        // receive the historical synchronous cleanup behavior. The lifecycle
        // port calls CompleteForExit again, which is idempotent.
        if (!settings.Enabled || _cleanOnExit)
            CompleteForExit();

        return result;
    }

    /// <summary>
    /// One captured actor. The <see cref="PoseFile"/> is already detached from
    /// game memory — <see cref="IPoseFileService.CreatePoseFile"/> copies bone
    /// transforms into plain dictionaries — which is exactly what lets the
    /// write half run off the framework thread.
    /// </summary>
    private readonly record struct CapturedPose(
        string ActorName,
        string FileName,
        PoseFile Pose);

    /// <summary>
    /// Returns the number of actors CAPTURED, not the number of files that
    /// landed: the writes outlive this call. Zero therefore also covers
    /// "nothing had authored edits" and "a periodic item was coalesced into the
    /// bounded pending slot", both of which may produce zero.
    /// </summary>
    public int SaveNow(string reason) =>
        CaptureAndDispatch(reason, isFinal: false).CapturedActors;

    private AutoSaveCaptureResult CaptureAndDispatch(string reason, bool isFinal)
    {
        lock (_queueGate)
        {
            if (_disposed || (_exitReserved && !isFinal))
                return AutoSaveCaptureResult.NotCaptured(
                    "Auto-save admission is closed.");
        }

        var dispatchAccepted = false;
        try
        {
            var captured = new List<CapturedPose>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? captureFailure = null;

            foreach (var actor in _actors().Actors)
            {
                try
                {
                    if (!HasAuthoredEdits(actor))
                        continue;

                    // Both halves of the capture read live game state, so both
                    // stay here; only the resulting PoseFile crosses over.
                    captured.Add(new CapturedPose(
                        actor.Name,
                        UniqueFileName(actor.Name, used) + ".pose",
                        _poseFiles().CreatePoseFile(_skeletons().GetSkeletons(actor))));
                }
                catch (Exception ex)
                {
                    captureFailure ??= ex.Message;
                    _log.Error(
                        $"Auto-save ({reason}): could not inspect actor '{actor.Name}': {ex.Message}");
                }
            }

            if (captured.Count == 0)
            {
                if (captureFailure != null)
                {
                    return AutoSaveCaptureResult.Failure(
                        $"Auto-save ({reason}) could not capture an actor: {captureFailure}");
                }

                _log.Debug($"Auto-save ({reason}): no actors with authored edits, skipping");
                return AutoSaveCaptureResult.NotCaptured(
                    "No actors had authored edits.");
            }

            // Read on this thread so the worker never touches configuration.
            var keep = Math.Max(1, Settings.MaxAutoSaves);
            var nowUtc = _clock();

            var job = new SnapshotJob(reason, nowUtc, keep, captured, isFinal);
            lock (_queueGate)
            {
                if (_disposed || (_exitReserved && !isFinal))
                    return AutoSaveCaptureResult.NotCaptured(
                        "Auto-save admission is closed.");

                if (isFinal)
                    _finalJob = job;
                else
                    _pendingPeriodic = job;

                dispatchAccepted = EnsureWriterLocked();
            }

            if (!dispatchAccepted)
            {
                lock (_queueGate)
                {
                    if (isFinal)
                        _finalJob = null;
                    else if (_pendingPeriodic.Equals(job))
                        _pendingPeriodic = null;
                    _workerFailure ??= $"Auto-save ({reason}) dispatch was not accepted.";
                }
                return AutoSaveCaptureResult.Captured(
                    captured.Count,
                    $"Auto-save ({reason}) dispatch was not accepted.");
            }

            if (dispatchAccepted)
            {
                LastSaveUtc = nowUtc;
                if (captureFailure != null)
                {
                    return AutoSaveCaptureResult.Failure(
                        $"Auto-save ({reason}) captured {captured.Count} actor(s), " +
                        $"but another actor failed: {captureFailure}",
                        captured.Count,
                        dispatchAccepted: true);
                }

                return AutoSaveCaptureResult.DispatchStarted(captured.Count);
            }

            if (captureFailure != null)
            {
                return AutoSaveCaptureResult.Failure(
                    $"Auto-save ({reason}) captured {captured.Count} actor(s), " +
                    $"but another actor failed: {captureFailure}",
                    captured.Count);
            }

            return AutoSaveCaptureResult.Captured(captured.Count);
        }
        catch (Exception ex)
        {
            _log.Error($"Auto-save ({reason}) failed: {ex}");
            return AutoSaveCaptureResult.Failure(
                $"Auto-save ({reason}) failed: {ex.Message}");
        }
    }

    private bool EnsureWriterLocked()
    {
        if (_writerRunning)
        {
            _lastTerminalResult = AutoSaveTerminalResult.PendingResult;
            return true;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _writerRunning = true;
        try
        {
            var accepted = _dispatch(() => WorkerDrain(completion));
            if (!accepted)
            {
                _writerRunning = false;
                completion.TrySetResult(false);
                _log.Error("Auto-save worker dispatch was not accepted.");
                return false;
            }

            // The task is retained even when the test dispatcher invokes the
            // callback synchronously; unload always owns the join boundary.
            _writerTask = completion.Task;
            _lastTerminalResult = AutoSaveTerminalResult.PendingResult;
            return true;
        }
        catch (Exception ex)
        {
            _writerRunning = false;
            completion.TrySetException(ex);
            _log.Error($"Auto-save worker dispatch failed: {ex.Message}");
            return false;
        }
    }

    private void WorkerDrain(TaskCompletionSource<bool> completion)
    {
        var success = true;
        try
        {
            while (true)
            {
                SnapshotJob? job;
                lock (_queueGate)
                {
                    job = _pendingPeriodic ?? _finalJob;
                    if (job is null)
                    {
                        _writerRunning = false;
                        _lastTerminalResult = success
                            ? AutoSaveTerminalResult.Written()
                            : AutoSaveTerminalResult.RecoveryRequired(
                                _workerFailure ?? "Auto-save worker failed.");
                        completion.TrySetResult(success);
                        return;
                    }

                    if (job.Value.IsFinal)
                        _finalJob = null;
                    else
                        _pendingPeriodic = null;
                }

                var result = WriteSnapshot(
                    job.Value.Reason,
                    job.Value.NowUtc,
                    job.Value.Keep,
                    job.Value.Captured);
                if (!result.Success)
                {
                    success = false;
                    lock (_queueGate)
                        _workerFailure ??= result.Detail;
                }
            }
        }
        catch (Exception ex)
        {
            lock (_queueGate)
            {
                success = false;
                _workerFailure ??= ex.Message;
                _writerRunning = false;
                _lastTerminalResult = AutoSaveTerminalResult.RecoveryRequired(ex.Message);
            }
            completion.TrySetResult(false);
        }
    }

    /// <summary>
    /// Closes admission, joins every owned writer, then performs clean-on-exit
    /// cleanup if requested. No timeout path releases ownership of the writer.
    /// </summary>
    public AutoSaveTerminalResult CompleteForExit()
    {
        Task? writer;
        bool clean;
        lock (_queueGate)
        {
            if (_exitCompleted)
                return _lastTerminalResult;

            _exitReserved = true;
            _pendingPeriodic = null;
            clean = _cleanOnExit;
            writer = _writerTask;
        }

        try
        {
            writer?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            lock (_queueGate)
                _workerFailure ??= ex.Message;
        }

        AutoSaveTerminalResult result;
        lock (_queueGate)
        {
            if (_writerRunning || _finalJob is not null || _pendingPeriodic is not null)
            {
                // A callback can only reach here if a custom dispatcher violated
                // its ownership contract. Keep the service in recovery rather
                // than claiming that unload is safe.
                _workerFailure ??= "Auto-save worker did not reach a terminal state.";
            }

            result = _workerFailure is not null
                ? AutoSaveTerminalResult.RecoveryRequired(_workerFailure)
                : _hasFinalCapture &&
                  _finalCapture.Status == AutoSaveCaptureStatus.Failure
                    ? AutoSaveTerminalResult.RecoveryRequired(
                        _finalCapture.Detail ?? "Final auto-save capture failed.")
                : clean
                    ? AutoSaveTerminalResult.PendingResult
                    : _hasFinalCapture &&
                      (_finalCapture.Status is AutoSaveCaptureStatus.Captured
                        or AutoSaveCaptureStatus.DispatchStarted)
                        ? AutoSaveTerminalResult.Written()
                        : AutoSaveTerminalResult.NotAttempted();
        }

        if (clean && result.Status != AutoSaveTerminalStatus.RecoveryRequired)
        {
            if (CleanAll())
                result = AutoSaveTerminalResult.Cleaned();
            else
                result = AutoSaveTerminalResult.RecoveryRequired(
                    "Clean-on-exit could not remove every snapshot.");
        }

        lock (_queueGate)
        {
            _lastTerminalResult = result;
            _exitCompleted = true;
            return result;
        }
    }

    internal bool WaitForIdle(TimeSpan timeout)
    {
        Task? writer;
        lock (_queueGate)
            writer = _writerTask;
        return writer is null || writer.Wait(timeout);
    }

    /// <summary>
    /// Worker half: serialization, folder creation, the writes and retention.
    /// Touches nothing but the captured data and the disk. Failure semantics
    /// are the inline ones — every failure is swallowed and logged, and one bad
    /// actor never aborts the rest of the snapshot.
    /// </summary>
    private WorkerResult WriteSnapshot(
        string reason,
        DateTime nowUtc,
        int keep,
        IReadOnlyList<CapturedPose> captured)
    {
        var success = true;
        string? failure = null;
        try
        {
            var local = nowUtc.ToLocalTime();
            var dayFolder = Path.Combine(
                RootDirectory,
                local.ToString(DayFolderFormat, CultureInfo.InvariantCulture));
            Directory.CreateDirectory(dayFolder);
            var prefix = local.ToString(TimePrefixFormat, CultureInfo.InvariantCulture);
            var saved = 0;

            foreach (var entry in captured)
            {
                var path = SnapshotFilePath(dayFolder, prefix, entry.FileName);
                try
                {
                    if (entry.Pose.Save(path))
                    {
                        saved++;
                    }
                    else
                    {
                        success = false;
                        failure ??= $"export failed for actor '{entry.ActorName}'";
                        // PoseFile.Save swallows the underlying failure; this
                        // adds the auto-save context it cannot see.
                        _log.Error(
                            $"Auto-save ({reason}): export failed for actor '{entry.ActorName}' -> {path}");
                    }
                }
                catch (Exception ex)
                {
                    success = false;
                    failure ??= ex.Message;
                    _log.Error(
                        $"Auto-save ({reason}): actor '{entry.ActorName}' -> {path} threw: {ex.Message}");
                }
            }

            _log.Info($"Auto-saved {saved}/{captured.Count} actor(s) to {dayFolder} ({reason})");
            if (!Prune(keep))
            {
                success = false;
                failure ??= "retention pruning failed";
            }
        }
        catch (Exception ex)
        {
            success = false;
            failure ??= ex.Message;
            _log.Error($"Auto-save ({reason}) failed: {ex}");
        }
        return new WorkerResult(success, failure);
    }

    /// <summary>
    /// Mirrors <c>CleanPoseFacade.HasAuthoredEdits</c>: any bone of any present
    /// slot carrying an unnamed (user-authored, not service-owned) layer.
    /// </summary>
    private bool HasAuthoredEdits(IActor actor)
    {
        var bonePosing = _bonePosing();
        return _skeletons().GetSkeletons(actor).Any(skeleton =>
            bonePosing.GetPoseInfo(skeleton).AllPoses
                .Any(pose => pose.Stacks.Any(stack => stack.Layer == null)));
    }

    /// <summary>
    /// <c>"HH-mm-ss Actor.pose"</c> inside the day folder. A collision (an
    /// exit save landing in the same second as an interval save, or the DST
    /// fold replaying an hour) gets a " (2)" suffix before the extension, so
    /// nothing is ever overwritten.
    /// </summary>
    private static string SnapshotFilePath(string dayFolder, string prefix, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var candidate = Path.Combine(dayFolder, $"{prefix} {stem}{extension}");
        for (var suffix = 2; File.Exists(candidate); suffix++)
            candidate = Path.Combine(dayFolder, $"{prefix} {stem} ({suffix}){extension}");
        return candidate;
    }

    /// <summary>
    /// Ktisis <c>FormatService.StripInvalidChars</c> parity, plus in-snapshot
    /// de-duplication so two actors with the same name both survive.
    /// </summary>
    private static string UniqueFileName(string actorName, HashSet<string> used)
    {
        var name = Sanitize(actorName);
        if (used.Add(name))
            return name;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{name} ({suffix})";
            if (used.Add(candidate))
                return candidate;
        }
    }

    private static string Sanitize(string actorName)
    {
        if (string.IsNullOrWhiteSpace(actorName))
            return "Actor";

        var invalid = Path.GetInvalidFileNameChars();
        var chars = actorName.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }

        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "Actor" : sanitized;
    }

    /// <summary>
    /// Disk-based retention: the newest <c>MaxAutoSaves</c> SAVE EVENTS by date
    /// are kept, everything older is deleted. One event is what a single save
    /// wrote — the files sharing one time prefix inside a day folder, or one
    /// whole folder of the old one-folder-per-save layout, which is how
    /// pre-existing snapshots join the same ordering and age out without a
    /// migration. Reading the disk rather than a session queue is what makes
    /// retention hold across restarts.
    ///
    /// <para>Date, not name (Brio's semantic): a save is written once and never
    /// touched again, so its last-write time IS the save date, and a folder or
    /// file the user renamed keeps its true age instead of being sorted by
    /// whatever it is now called. Ties break on key, descending, so the order
    /// is total even at one-second stamp granularity. A day folder whose last
    /// event was pruned goes with it.</para>
    /// </summary>
    private bool Prune(int keep)
    {
        var events = new List<(DateTime AtUtc, string Key, string? LegacyDir, List<string>? Files)>();
        var dayFolders = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory))
            {
                var name = Path.GetFileName(dir) ?? string.Empty;
                if (!IsDayFolder(name))
                {
                    // Old layout: the folder is the save.
                    events.Add((Directory.GetLastWriteTimeUtc(dir), name, dir, null));
                    continue;
                }

                dayFolders.Add(dir);
                foreach (var group in Directory.EnumerateFiles(dir)
                             .GroupBy(file => EventKey(Path.GetFileName(file))))
                {
                    var files = group.ToList();
                    var newest = DateTime.MinValue;
                    foreach (var file in files)
                    {
                        var at = File.GetLastWriteTimeUtc(file);
                        if (at > newest)
                            newest = at;
                    }
                    events.Add((newest, $"{name}/{group.Key}", null, files));
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Auto-save: could not enumerate '{RootDirectory}' to prune: {ex.Message}");
            return false;
        }

        var stale = events
            .OrderByDescending(entry => entry.AtUtc)
            .ThenByDescending(entry => entry.Key, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();

        var pruned = 0;
        var success = true;
        foreach (var (_, _, legacyDir, files) in stale)
        {
            try
            {
                if (legacyDir != null)
                    Directory.Delete(legacyDir, recursive: true);
                else
                    foreach (var file in files!)
                        File.Delete(file);
                pruned++;
            }
            catch (Exception ex)
            {
                success = false;
                _log.Error(
                    $"Auto-save: could not prune '{legacyDir ?? files![0]}': {ex.Message}");
            }
        }

        foreach (var dir in dayFolders)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                success = false;
                _log.Error(
                    $"Auto-save: could not remove empty day folder '{dir}': {ex.Message}");
            }
        }

        if (pruned > 0)
            _log.Debug($"Auto-save pruned {pruned} old save(s).");
        return success;
    }

    private static bool IsDayFolder(string name) =>
        DateTime.TryParseExact(
            name,
            DayFolderFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);

    /// <summary>Files sharing one valid <c>"HH-mm-ss "</c> prefix are one save;
    /// anything else (a user-renamed file) is its own event under its full
    /// name.</summary>
    private static string EventKey(string? fileName)
    {
        if (fileName != null &&
            fileName.Length > TimePrefixLength &&
            fileName[TimePrefixLength] == ' ' &&
            DateTime.TryParseExact(
                fileName[..TimePrefixLength],
                TimePrefixFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            return fileName[..TimePrefixLength];
        return fileName ?? string.Empty;
    }

    private bool CleanAll()
    {
        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(RootDirectory).ToList();
        }
        catch (Exception ex)
        {
            _log.Error($"Auto-save: could not enumerate '{RootDirectory}' to clean: {ex.Message}");
            return false;
        }

        var deleted = 0;
        var success = true;
        foreach (var dir in folders)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex)
            {
                success = false;
                _log.Error($"Auto-save: could not delete '{dir}': {ex.Message}");
            }
        }

        _log.Info($"Auto-save cleaned {deleted} snapshot folder(s) on leaving GPose.");
        return success && !Directory.EnumerateDirectories(RootDirectory).Any();
    }

    /// <summary>Close admission and join the owned worker before disposal.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_queueGate)
        {
            _disposed = true;
            _exitReserved = true;
            _pendingPeriodic = null;
        }

        if (_framework != null)
            _framework.Update -= OnFrameworkUpdate;
        CompleteForExit();
        GC.SuppressFinalize(this);
    }
}
