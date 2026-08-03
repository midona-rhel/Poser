using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Poser.Config;
using Poser.Core;
using Poser.Entities;
using Poser.Services;

namespace Poser.Files;

/// <summary>
/// Timed pose auto-save (GAP 4). Exports every actor with Poser-authored edits
/// through <see cref="IPoseFileService.ExportPose"/> into
/// <c>&lt;pluginConfigDir&gt;/AutoSaves/&lt;utc timestamp&gt;/&lt;actor&gt;.pose</c>
/// while in GPose, and once on GPose exit.
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

    public int SaveNow(string reason)
    {
        var saved = 0;
        try
        {
            var candidates = new List<IActor>();
            foreach (var actor in _actors().Actors)
            {
                try
                {
                    if (HasAuthoredEdits(actor))
                        candidates.Add(actor);
                }
                catch (Exception ex)
                {
                    _log.Error(
                        $"Auto-save ({reason}): could not inspect actor '{actor.Name}': {ex.Message}");
                }
            }

            if (candidates.Count == 0)
            {
                _log.Debug($"Auto-save ({reason}): no actors with authored edits, skipping");
                return 0;
            }

            var nowUtc = _clock();
            var folder = CreateSnapshotFolder(nowUtc);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var actor in candidates)
            {
                var path = string.Empty;
                try
                {
                    path = Path.Combine(folder, UniqueFileName(actor.Name, used) + ".pose");
                    if (_poseFiles().ExportPose(_skeletons().GetSkeletons(actor), path))
                    {
                        saved++;
                    }
                    else
                    {
                        // ExportPose logs the underlying failure; this adds the
                        // auto-save context it cannot see.
                        _log.Error(
                            $"Auto-save ({reason}): export failed for actor '{actor.Name}' -> {path}");
                    }
                }
                catch (Exception ex)
                {
                    _log.Error(
                        $"Auto-save ({reason}): actor '{actor.Name}' -> {path} threw: {ex.Message}");
                }
            }

            _log.Info($"Auto-saved {saved}/{candidates.Count} actor(s) to {folder} ({reason})");
            LastSaveUtc = nowUtc;
            Prune();
        }
        catch (Exception ex)
        {
            // Runs on the framework tick: nothing here may escape.
            _log.Error($"Auto-save ({reason}) failed: {ex}");
        }

        return saved;
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
    /// Disk-based retention: the newest <c>MaxAutoSaves</c> folders by name
    /// (== by time) are kept, everything older is deleted. Reading the disk
    /// rather than a session queue is what makes retention hold across restarts.
    /// </summary>
    private void Prune()
    {
        var keep = Math.Max(1, Settings.MaxAutoSaves);

        List<string> stale;
        try
        {
            stale = Directory.EnumerateDirectories(RootDirectory)
                .OrderByDescending(dir => Path.GetFileName(dir) ?? string.Empty, StringComparer.Ordinal)
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
