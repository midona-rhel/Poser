#if DEBUG
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Poser.Application.AutoSave;
using Poser.Application.Library;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private const string DisposableAutoSaveSegment =
        "/artifacts/refactor-review-481/autosave-disposable/";

    private IAutoSaveService BridgeAutoSaves =>
        (IAutoSaveService)_services.GetService(typeof(IAutoSaveService))!;

    private AutoSaveLibrary BridgeAutoSaveLibrary =>
        (AutoSaveLibrary)_services.GetService(typeof(AutoSaveLibrary))!;

    private ILibraryFileOperations BridgeLibraryFiles =>
        (ILibraryFileOperations)_services.GetService(typeof(ILibraryFileOperations))!;

    private object AutoSaveConfigCommand(Dictionary<string, string> query)
    {
        if (!query.TryGetValue("mode", out var mode))
            return AutoSaveConfigState();
        bool isolate = mode.Equals("isolate", StringComparison.OrdinalIgnoreCase);
        bool restore = mode.Equals("restore", StringComparison.OrdinalIgnoreCase);
        if (!isolate && !restore)
            return new { accepted = false, reason = "mode must be isolate or restore." };
        if (!TryBooleanSetting(query, "enabled", out var enabled)
            || !TryPositiveInt(query, "intervalSeconds", out var interval)
            || !TryPositiveInt(query, "maxAutoSaves", out var max)
            || !TryBooleanSetting(query, "cleanOnExit", out var clean)
            || !TryBooleanSetting(query, "sceneSnapshots", out var scenes)
            || !TryPositiveInt(query, "maxSceneSnapshots", out var maxScenes)
            || !query.TryGetValue("root", out var root)
            || !Path.IsPathFullyQualified(root))
            return new { accepted = false, reason = "A complete, valid autosave configuration is required." };

        root = Path.GetFullPath(root);
        if (isolate && !IsDisposableAutoSavePath(root, allowRoot: true))
            return new { accepted = false,
                reason = "Isolation roots must be beneath artifacts/refactor-review-481/autosave-disposable/." };

        var settings = _configuration.Config.AutoSave;
        settings.Enabled = enabled;
        settings.IntervalSeconds = interval;
        settings.MaxAutoSaves = max;
        settings.CleanOnExit = clean;
        settings.SceneSnapshots = scenes;
        settings.MaxSceneSnapshots = maxScenes;
        settings.RootDirectory = root;
        _configuration.Save();
        return new { accepted = true, applied = true, mode, reloadRequired = true,
            state = AutoSaveConfigState() };
    }

    private object AutoSaveConfigState()
    {
        var settings = _configuration.Config.AutoSave;
        string configured = string.IsNullOrWhiteSpace(settings.RootDirectory)
            ? string.Empty : Path.GetFullPath(settings.RootDirectory);
        string effective = Path.GetFullPath(BridgeAutoSaves.RootDirectory);
        return new
        {
            enabled = settings.Enabled,
            intervalSeconds = settings.IntervalSeconds,
            maxAutoSaves = settings.MaxAutoSaves,
            cleanOnExit = settings.CleanOnExit,
            sceneSnapshots = settings.SceneSnapshots,
            maxSceneSnapshots = settings.MaxSceneSnapshots,
            configuredRoot = configured,
            effectiveServiceRoot = effective,
            reloadRequired = !string.Equals(configured, effective, StringComparison.OrdinalIgnoreCase),
            sceneRootIsolationSupported = false,
        };
    }

    private object AutoSaveStatus()
    {
        var service = BridgeAutoSaves;
        return new
        {
            root = Path.GetFullPath(service.RootDirectory),
            terminal = service.LastTerminalResult,
            health = service.LastHealthRecord,
            // SceneAutoSaveStore is fixed to plugin config at composition time;
            // the bridge deliberately offers no failure injection against it.
            sceneRootIsolationSupported = false,
        };
    }

    private object AutoSaveLibraryCommand(Dictionary<string, string> query)
    {
        string action = query.GetValueOrDefault("action", "read");
        if (action.Equals("refresh", StringComparison.OrdinalIgnoreCase))
        {
            BridgeAutoSaveLibrary.RequestScan();
            return new { accepted = true, outcome = "Pending" };
        }
        if (action.Equals("read", StringComparison.OrdinalIgnoreCase))
        {
            var latest = BridgeAutoSaveLibrary.TakeLatest();
            return new { accepted = true, outcome = latest is null ? "Pending" : "Applied",
                folders = latest };
        }
        if (!action.Equals("delete", StringComparison.OrdinalIgnoreCase))
            return new { accepted = false, outcome = "Refused", reason = "Unknown autosave-library action." };
        if (!query.TryGetValue("path", out var requested) || !Path.IsPathFullyQualified(requested))
            return new { accepted = false, outcome = "Refused", reason = "An absolute pose path is required." };
        string path = Path.GetFullPath(requested);
        string root = Path.GetFullPath(BridgeAutoSaves.RootDirectory);
        if (!IsStrictDescendant(path, root) || !IsDisposableAutoSavePath(path, allowRoot: false)
            || !path.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            return new { accepted = false, outcome = "Refused",
                reason = "Only an existing disposable .pose file beneath the effective autosave root may be deleted." };
        var result = BridgeLibraryFiles.Delete(path);
        if (result.Succeeded)
            BridgeAutoSaveLibrary.RequestScan();
        return new { accepted = result.Succeeded, outcome = result.Succeeded ? "Applied" : "Refused",
            reason = result.Detail, path };
    }

    private static bool IsDisposableAutoSavePath(string path, bool allowRoot)
    {
        string normalized = Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/') + "/";
        int marker = normalized.IndexOf(DisposableAutoSaveSegment, StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        int tail = marker + DisposableAutoSaveSegment.Length;
        return allowRoot || normalized.Length > tail;
    }

    private static bool IsStrictDescendant(string path, string root)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root))
            + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryBooleanSetting(Dictionary<string, string> query, string key, out bool value)
    {
        value = default;
        return query.TryGetValue(key, out var text) && TryBoolean(text, out value);
    }

    private static bool TryPositiveInt(Dictionary<string, string> query, string key, out int value)
    {
        value = default;
        return query.TryGetValue(key, out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
            && value >= 1;
    }
}
#endif
