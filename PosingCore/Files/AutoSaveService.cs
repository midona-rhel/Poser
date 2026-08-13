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

    /// <summary>0 idle, 1 a snapshot is still being written. A snapshot that
    /// arrives while the previous one is in flight is DROPPED, not queued: the
    /// next interval is a fresher capture than any backlog entry.</summary>
    private int _writeInFlight;

    public string RootDirectory { get; }

    public DateTime? LastSaveUtc { get; private set; }

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
    /// Exactly one final-capture attempt for this call. GPoseService calls this
    /// before it publishes the legacy exit event, so the attempt does not
    /// depend on EventBus subscription order. If an earlier worker dispatch is
    /// still in flight, the latch returns NotCaptured without reading actor
    /// state or claiming immutable capture occurred.
    /// </summary>
    public AutoSaveCaptureResult CaptureForExit()
    {
        _nextDueUtc = null;

        var settings = Settings;
        if (!settings.Enabled)
            return AutoSaveCaptureResult.NotCaptured("Auto-save is disabled.");

        if (settings.CleanOnExit)
        {
            CleanAll();
            return AutoSaveCaptureResult.NotCaptured(
                "Clean-on-exit is enabled; snapshots were cleaned.");
        }

        return CaptureAndDispatch("gpose-exit");
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
    /// "nothing had authored edits" and "the previous snapshot is still being
    /// written", both of which are no-ops by design.
    /// </summary>
    public int SaveNow(string reason) => CaptureAndDispatch(reason).CapturedActors;

    private AutoSaveCaptureResult CaptureAndDispatch(string reason)
    {
        if (Interlocked.CompareExchange(ref _writeInFlight, 1, 0) != 0)
            return AutoSaveCaptureResult.NotCaptured(
                "A previous auto-save dispatch is still in flight.");

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

            try
            {
                dispatchAccepted = _dispatch(
                    () => WriteSnapshot(reason, nowUtc, keep, captured));
            }
            catch (Exception ex)
            {
                _log.Error($"Auto-save ({reason}) dispatch failed: {ex}");
                return AutoSaveCaptureResult.Failure(
                    $"Auto-save ({reason}) dispatch failed: {ex.Message}",
                    captured.Count);
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
        finally
        {
            if (!dispatchAccepted)
                Interlocked.Exchange(ref _writeInFlight, 0);
        }
    }

    /// <summary>
    /// Worker half: serialization, folder creation, the writes and retention.
    /// Touches nothing but the captured data and the disk. Failure semantics
    /// are the inline ones — every failure is swallowed and logged, and one bad
    /// actor never aborts the rest of the snapshot.
    /// </summary>
    private void WriteSnapshot(
        string reason,
        DateTime nowUtc,
        int keep,
        List<CapturedPose> captured)
    {
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
                        // PoseFile.Save swallows the underlying failure; this
                        // adds the auto-save context it cannot see.
                        _log.Error(
                            $"Auto-save ({reason}): export failed for actor '{entry.ActorName}' -> {path}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(
                        $"Auto-save ({reason}): actor '{entry.ActorName}' -> {path} threw: {ex.Message}");
                }
            }

            _log.Info($"Auto-saved {saved}/{captured.Count} actor(s) to {dayFolder} ({reason})");
            Prune(keep);
        }
        catch (Exception ex)
        {
            _log.Error($"Auto-save ({reason}) failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _writeInFlight, 0);
        }
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
    private void Prune(int keep)
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
            return;
        }

        var stale = events
            .OrderByDescending(entry => entry.AtUtc)
            .ThenByDescending(entry => entry.Key, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();

        var pruned = 0;
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
                _log.Error(
                    $"Auto-save: could not remove empty day folder '{dir}': {ex.Message}");
            }
        }

        if (pruned > 0)
            _log.Debug($"Auto-save pruned {pruned} old save(s).");
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

    private void CleanAll()
    {
        List<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(RootDirectory).ToList();
        }
        catch (Exception ex)
        {
            _log.Error($"Auto-save: could not enumerate '{RootDirectory}' to clean: {ex.Message}");
            return;
        }

        var deleted = 0;
        foreach (var dir in folders)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex)
            {
                _log.Error($"Auto-save: could not delete '{dir}': {ex.Message}");
            }
        }

        _log.Info($"Auto-save cleaned {deleted} snapshot folder(s) on leaving GPose.");
    }

    /// <summary>
    /// Unhook the framework tick only. An in-flight <see cref="WriteSnapshot"/>
    /// is deliberately not waited on: it holds a detached copy and only touches
    /// the disk.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_framework != null)
            _framework.Update -= OnFrameworkUpdate;
        GC.SuppressFinalize(this);
    }
}
