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
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Timed pose auto-save (GAP 4). Exports every actor with Poser-authored edits
/// into
/// <c>&lt;pluginConfigDir&gt;/AutoSaves/&lt;utc timestamp&gt;/&lt;actor&gt;.pose</c>
/// while in GPose, and once on GPose exit.
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
/// <item>Folder names are UTC and 24-hour, so name order == time order. Ktisis
/// and Brio both format with 12-hour <c>hh</c>, which collides across noon.</item>
/// <item>Retention is computed from what is on disk, not from an in-memory
/// queue, so it still holds after a plugin restart (Ktisis' does not).</item>
/// <item>One actor failing to export never aborts the rest of the snapshot
/// (Brio aborts the whole save on a single bad filename).</item>
/// <item>Nothing is written when no actor has authored edits, so no empty
/// folders accumulate (Ktisis leaves them).</item>
/// </list>
///
/// <para>WHY THE SCENE SERVICES ARE INJECTED AS FACTORIES: on GPose exit
/// <c>ActorManager</c>, <c>SkeletonService</c>, <c>BonePosingService</c> and
/// <c>PosingService</c> all wipe their state from their own
/// <c>GPoseStateChangedEvent</c> handlers. The EventBus dispatches in
/// subscription order, and a constructor argument is always constructed — and
/// therefore subscribed — before the constructor that consumes it. Taking those
/// services directly would guarantee this service subscribed last and found an
/// empty scene, making the exit snapshot a permanent no-op. Resolving them
/// lazily lets this service subscribe first (see the eager resolve in
/// <c>Poser.Poser</c>) and read the still-intact pose on the way out.</para>
/// </summary>
public class AutoSaveService : IAutoSaveService
{
    private const string AutoSaveFolderName = "AutoSaves";

    private readonly IPluginLog _log;
    private readonly IFramework? _framework;
    private readonly IEventBus _eventBus;
    private readonly IGPoseService _gpose;
    private readonly Func<IActorManager> _actors;
    private readonly Func<ISkeletonService> _skeletons;
    private readonly Func<IBonePosingService> _bonePosing;
    private readonly Func<IPoseFileService> _poseFiles;
    private readonly ConfigurationService _configuration;
    private readonly Func<DateTime> _clock;

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
        IEventBus eventBus,
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
            eventBus,
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
        IEventBus eventBus,
        IGPoseService gpose,
        Func<IActorManager> actors,
        Func<ISkeletonService> skeletons,
        Func<IBonePosingService> bonePosing,
        Func<IPoseFileService> poseFiles,
        ConfigurationService configuration,
        string rootDirectory,
        Func<DateTime>? utcClock = null)
    {
        _log = log;
        _framework = framework;
        _eventBus = eventBus;
        _gpose = gpose;
        _actors = actors;
        _skeletons = skeletons;
        _bonePosing = bonePosing;
        _poseFiles = poseFiles;
        _configuration = configuration;
        _clock = utcClock ?? (() => DateTime.UtcNow);
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
        _eventBus.Subscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
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
    /// GPose exit is the one edge that also covers Ktisis' save-on-disconnect
    /// and save-on-posing-disable: in Poser's model both surface here.
    /// </summary>
    internal void OnGPoseStateChanged(GPoseStateChangedEvent e)
    {
        if (e.IsGPosing)
            return;

        _nextDueUtc = null;

        var settings = Settings;
        if (!settings.Enabled)
            return;

        if (settings.CleanOnExit)
        {
            CleanAll();
            return;
        }

        SaveNow("gpose-exit");
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
    public int SaveNow(string reason)
    {
        if (Interlocked.CompareExchange(ref _writeInFlight, 1, 0) != 0)
            return 0;

        var dispatched = false;
        try
        {
            var captured = new List<CapturedPose>();
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    _log.Error(
                        $"Auto-save ({reason}): could not inspect actor '{actor.Name}': {ex.Message}");
                }
            }

            if (captured.Count == 0)
            {
                _log.Debug($"Auto-save ({reason}): no actors with authored edits, skipping");
                return 0;
            }

            // Read on this thread so the worker never touches configuration.
            var keep = Math.Max(1, Settings.MaxAutoSaves);
            var nowUtc = _clock();
            LastSaveUtc = nowUtc;

            Task.Run(() => WriteSnapshot(reason, nowUtc, keep, captured));
            dispatched = true;
            return captured.Count;
        }
        catch (Exception ex)
        {
            // Runs on the framework tick: nothing here may escape.
            _log.Error($"Auto-save ({reason}) failed: {ex}");
            return 0;
        }
        finally
        {
            if (!dispatched)
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
            var folder = CreateSnapshotFolder(nowUtc);
            var saved = 0;

            foreach (var entry in captured)
            {
                var path = Path.Combine(folder, entry.FileName);
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

            _log.Info($"Auto-saved {saved}/{captured.Count} actor(s) to {folder} ({reason})");
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
    /// UTC, 24-hour, name-sortable. A collision (an exit save landing in the
    /// same second as an interval save) gets a " (2)" suffix, which still sorts
    /// after the unsuffixed name, i.e. newest-first stays correct.
    /// </summary>
    private string CreateSnapshotFolder(DateTime nowUtc)
    {
        var baseName = nowUtc.ToString("yyyy-MM-dd HH-mm-ss'Z'", CultureInfo.InvariantCulture);
        var candidate = Path.Combine(RootDirectory, baseName);
        for (var suffix = 2; Directory.Exists(candidate); suffix++)
            candidate = Path.Combine(RootDirectory, $"{baseName} ({suffix})");

        Directory.CreateDirectory(candidate);
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
    /// Disk-based retention: the newest <c>MaxAutoSaves</c> folders BY DATE are
    /// kept, everything older is deleted. Reading the disk rather than a session
    /// queue is what makes retention hold across restarts.
    ///
    /// <para>Date, not name (Brio's semantic): a snapshot folder is written once
    /// and never touched again, so its last-write time IS the snapshot date, and
    /// a folder the user renamed keeps its true age instead of being sorted by
    /// whatever it is now called. Ties break on name, descending, so the order
    /// is total even at one-second stamp granularity.</para>
    /// </summary>
    private void Prune(int keep)
    {
        List<string> stale;
        try
        {
            stale = Directory.EnumerateDirectories(RootDirectory)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .ThenByDescending(dir => Path.GetFileName(dir) ?? string.Empty, StringComparer.Ordinal)
                .Skip(keep)
                .ToList();
        }
        catch (Exception ex)
        {
            _log.Error($"Auto-save: could not enumerate '{RootDirectory}' to prune: {ex.Message}");
            return;
        }

        var pruned = 0;
        foreach (var dir in stale)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                pruned++;
            }
            catch (Exception ex)
            {
                _log.Error($"Auto-save: could not prune '{dir}': {ex.Message}");
            }
        }

        if (pruned > 0)
            _log.Debug($"Auto-save pruned {pruned} old snapshot folder(s).");
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
    /// Unhook only. Neither reference saves on dispose and DI teardown order is
    /// uncontrolled, so a dispose-time export could touch already-dead services.
    /// An in-flight <see cref="WriteSnapshot"/> is deliberately NOT waited on:
    /// it holds a detached copy and only touches the disk, so letting it finish
    /// is both safe and the only way the last snapshot survives teardown.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_framework != null)
            _framework.Update -= OnFrameworkUpdate;
        _eventBus.Unsubscribe<GPoseStateChangedEvent>(OnGPoseStateChanged);
        GC.SuppressFinalize(this);
    }
}
