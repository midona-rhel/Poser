using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Poser.Core;
using Poser.Entities;

namespace Poser.Config;

/// <summary>
/// Service for managing plugin configuration with persistence.
/// Follows Brio's pattern for configuration management.
/// </summary>
public class ConfigurationService : IDisposable
{
    public PoserConfiguration Config { get; private set; }

    private readonly IDalamudPluginInterface _pluginInterface;

    public static ConfigurationService Instance { get; private set; } = null!;

    public event Action? OnConfigurationChanged;

    /// <summary>
    /// Empty in the normal case. Otherwise: the stored config could not be
    /// read, defaults are in force, and this sentence names the backup the
    /// old file was copied to. Settings shows it — a config that quietly
    /// evaporates has to leave something behind to look at (Ktisis's
    /// backup-on-failure).
    /// </summary>
    public string LoadFailure { get; private set; } = string.Empty;

    public ConfigurationService(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        _pluginInterface = pluginInterface;
        Config = LoadOrRecover();
        MigrateConfig();

        // Seeded in memory only; it persists with the next save the user causes.
        Config.Library.EnsureDefaults();

        // The friendly-name switch is a field on the bone tables, because the
        // bones read it per frame; this is where the stored value reaches it.
        Core.BoneInfo.BoneInfoService.ShowFriendlyNames =
            Config.Skeleton.ShowFriendlyBoneNames;
    }

    /// <summary>
    /// The stored config, or defaults with the unreadable file preserved
    /// beside it. Three failures are one case: a throwing deserialize, a null
    /// result, and a result of the wrong type — in every one the user's
    /// settings are about to be replaced by defaults and the next save would
    /// overwrite the only copy, so the file is copied first and the reason is
    /// kept for the settings page to state.
    /// </summary>
    private PoserConfiguration LoadOrRecover()
    {
        object? stored = null;
        string reason = string.Empty;
        try
        {
            stored = _pluginInterface.GetPluginConfig();
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        if (stored is PoserConfiguration config)
            return config;

        // No file at all is a first run, not a failure: nothing was lost and
        // there is nothing to back up.
        var file = _pluginInterface.ConfigFile;
        if (file is not { Exists: true })
            return new PoserConfiguration();

        LoadFailure = BackUp(file, reason);
        return new PoserConfiguration();
    }

    /// <summary>Copies the unreadable config beside itself and reports what
    /// happened in one sentence. A failure to copy is itself reported rather
    /// than swallowed — the user is being told their settings are gone, and
    /// "there is a backup" has to be true.</summary>
    private static string BackUp(System.IO.FileInfo file, string reason)
    {
        string detail = reason.Length > 0 ? $" ({reason})" : string.Empty;
        try
        {
            string backup = file.FullName + ".bak-"
                + DateTime.Now.ToString(
                    "yyyyMMdd-HHmmss",
                    System.Globalization.CultureInfo.InvariantCulture);
            System.IO.File.Copy(file.FullName, backup, overwrite: true);
            return "Your settings could not be read and have been reset to "
                + $"defaults{detail}. The old file was saved as {backup}.";
        }
        catch (Exception ex)
        {
            return "Your settings could not be read and have been reset to "
                + $"defaults{detail}. Backing the old file up also failed: "
                + ex.Message;
        }
    }

    /// <summary>
    /// The stored config walks UP to <see cref="PoserConfiguration.LatestVersion"/>
    /// one step at a time, so a config that skipped a release still receives
    /// every step in order.
    ///
    /// <para>Version 2: the overlay color redesign replaces the old defaults,
    /// so stored overlay colors reset once to the new palette (this also
    /// re-enables accent-following for selected/hovered). Sizes, opacity, and
    /// every non-color setting keep their stored values.</para>
    ///
    /// <para>Version 3: the single keybind per action becomes a primary and a
    /// secondary slot; the stored chord becomes the primary.</para>
    /// </summary>
    private void MigrateConfig()
    {
        if (Config.Version >= PoserConfiguration.LatestVersion)
            return;

        if (Config.Version < 2)
        {
            var defaults = new SkeletonConfiguration();
            var skeleton = Config.Skeleton;
            skeleton.BoneColor = defaults.BoneColor;
            skeleton.BoneOutlineColor = defaults.BoneOutlineColor;
            skeleton.SelectedBoneColor = defaults.SelectedBoneColor;
            skeleton.ModifiedBoneColor = defaults.ModifiedBoneColor;
            skeleton.HoveredBoneColor = defaults.HoveredBoneColor;
            skeleton.IkChainColor = defaults.IkChainColor;
            skeleton.MirroredBoneColor = defaults.MirroredBoneColor;
        }

        if (Config.Version < 3)
            Config.UI.MigrateKeybindsToSlots();

        Config.Version = PoserConfiguration.LatestVersion;
        Save();
    }

    public void Save()
    {
        _pluginInterface.SavePluginConfig(Config);
        OnConfigurationChanged?.Invoke();
    }

    public void ApplyChange(bool save = true)
    {
        if (save)
            Save();

        OnConfigurationChanged?.Invoke();
    }

    public void Reset()
    {
        Config = new PoserConfiguration();
        ApplyChange();
    }

    public void ResetSkeleton()
    {
        Config.Skeleton = new SkeletonConfiguration();
        ApplyChange();
    }

    public void ResetDisplay()
    {
        Config.Display = new DisplayConfiguration();
        ApplyChange();
    }

    public void ResetUI()
    {
        Config.UI = new UIConfiguration();
        ApplyChange();
    }

    public void Dispose()
    {
        Save();
    }

    #region Anonymous Mode

    // Lineage-keyed anonymous masks: stable per logical actor for one
    // session, so the same masked actor keeps the same masked name.
    private readonly Dictionary<Guid, string> _lineageAnonymousNames = new();

    /// <summary>
    /// Stable-id display name: the user nickname wins, then the anonymous
    /// mask when <c>Display.AnonymousMode</c> is enabled, then the raw scene
    /// name. The caller supplies the raw name from the scene snapshot.
    /// </summary>
    public string GetDisplayName(Guid actorLineage, string rawName)
    {
        if (_lineageNicknames.TryGetValue(actorLineage, out var nickname))
            return nickname;

        if (!Config.Display.AnonymousMode)
            return rawName;

        if (!_lineageAnonymousNames.TryGetValue(actorLineage, out var anonName))
        {
            anonName = GenerateRandomName();
            _lineageAnonymousNames[actorLineage] = anonName;
        }
        return anonName;
    }

    // Lineage-keyed nicknames: the stable-id UI keys display names by the
    // actor's logical lineage Guid instead of a legacy entity id.
    private readonly Dictionary<Guid, string> _lineageNicknames = new();

    public void SetNickname(Guid actorLineage, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            _lineageNicknames.Remove(actorLineage);
        else
            _lineageNicknames[actorLineage] = name.Trim();
    }

    public string? GetNickname(Guid actorLineage) =>
        _lineageNicknames.TryGetValue(actorLineage, out var name) ? name : null;

    private static readonly Random _random = new();

    private static string GenerateRandomName()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, 5).Select(_ => chars[_random.Next(chars.Length)]).ToArray());
    }

    #endregion
}
