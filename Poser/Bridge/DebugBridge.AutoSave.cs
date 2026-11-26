#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using Dalamud.Plugin;
using Poser.Application.AutoSave;
using Poser.Application.Library;

namespace Poser.Bridge;

public sealed partial class DebugBridge
{
    private AutoSaveLibrary BridgeAutoSaveLibrary =>
        (AutoSaveLibrary)_services.GetService(typeof(AutoSaveLibrary))!;

    private object SceneAutoSaveConfigCommand(Dictionary<string, string> query)
    {
        string? previous = global::Poser.Lifecycle.DebugSceneAutoSaveRoot.Current;
        if (query.TryGetValue("mode", out var mode))
        {
            if (mode.Equals("isolate", StringComparison.OrdinalIgnoreCase))
            {
                if (!query.TryGetValue("root", out var requested)
                    || !global::Poser.Lifecycle.DebugSceneAutoSaveRoot.TryValidate(requested, out var isolated))
                    return new { accepted = false,
                        reason = "Isolation requires an absolute descendant of artifacts/refactor-review-481/autosave-disposable/." };
                global::Poser.Lifecycle.DebugSceneAutoSaveRoot.Set(isolated);
            }
            else if (mode.Equals("restore", StringComparison.OrdinalIgnoreCase))
            {
                string restored = query.GetValueOrDefault("root", "");
                if (restored.Length > 0
                    && !global::Poser.Lifecycle.DebugSceneAutoSaveRoot.TryValidate(restored, out restored))
                    return new { accepted = false,
                        reason = "A restored override must remain in the disposable autosave subtree." };
                global::Poser.Lifecycle.DebugSceneAutoSaveRoot.Set(restored.Length == 0 ? null : restored);
            }
            else return new { accepted = false, reason = "mode must be isolate or restore." };
        }

        var service = (SceneAutoSaveService)_services.GetService(typeof(SceneAutoSaveService))!;
        var result = service.DebugLastResult;
        var settings = _configuration.Config.AutoSave;
        string defaultRoot = Path.GetFullPath(Path.Combine(
            ((IDalamudPluginInterface)_services.GetService(typeof(IDalamudPluginInterface))!)
                .GetPluginConfigDirectory(), "SceneAutoSaves"));
        string desiredRoot = global::Poser.Lifecycle.DebugSceneAutoSaveRoot.Resolve(defaultRoot);
        string effectiveRoot = Path.GetFullPath(service.RootDirectory);
        string? warningText = result.Status is SceneAutoSaveStatus.Failed
            or SceneAutoSaveStatus.RecoveryRequired
            ? $"Scene auto-save {result.Status}"
                + (result.Path is { } failedPath ? $" ({failedPath})" : string.Empty)
                + $": {result.Detail}"
                + (result.Evidence.Count > 0
                    ? $" Recovery evidence: {string.Join(", ", result.Evidence)}"
                    : string.Empty)
            : null;
        return new
        {
            accepted = true,
            previousOverride = previous,
            configuredOverride = global::Poser.Lifecycle.DebugSceneAutoSaveRoot.Current,
            defaultRoot,
            desiredRoot,
            effectiveRoot,
            reloadRequired = !string.Equals(desiredRoot, effectiveRoot,
                StringComparison.OrdinalIgnoreCase),
            cadence = new
            {
                settings.Enabled,
                settings.IntervalSeconds,
                settings.MaxSceneSnapshots,
            },
            lastResult = new
            {
                status = result.Status.ToString(),
                result.Detail,
                result.Path,
                evidence = result.Evidence,
            },
            warningPrefix = "Scene auto-save ",
            warningText,
        };
    }

    private object AutoSaveStatus()
    {
        var service = BridgeAutoSaveLibrary;
        return new
        {
            root = Path.GetFullPath(service.RootDirectory),
            // Isolation is selected by /sceneautosaveconfig and takes effect
            // only after reload; this endpoint never swaps the running store.
            sceneRootIsolationSupported = true,
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
        return new { accepted = false, outcome = "Refused", reason = "Unknown autosave-library action." };
    }
}
#endif
