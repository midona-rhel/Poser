using System;
using System.Collections.Generic;
using System.Linq;
using Poser.Documents.Config;

namespace Poser.Application.Settings;

/// <summary>
/// Loads, migrates, and saves configuration, and notifies readers when it changes.
/// </summary>
public class ConfigurationService : IDisposable
{
    public PoserConfiguration Config { get; private set; }

    private readonly IConfigurationPersistence _persistence;

    public event Action? OnConfigurationChanged;

    /// <summary>
    /// Failure detail returned by configuration storage, displayed in Settings.
    /// Empty when loading succeeded.
    /// </summary>
    public string LoadFailure { get; private set; } = string.Empty;

    public ConfigurationService(IConfigurationPersistence persistence)
    {
        _persistence = persistence;
        var loaded = persistence.Load();
        Config = loaded.Configuration;
        LoadFailure = loaded.Failure;
        MigrateConfig();

        // Filled in memory only. It is written to disk with the next save.
        Config.Library.EnsureDefaults();

        // Saved at once: an id that changed between sessions would make your own
        // scenes look shared.
        if (Config.Library.InstallId == Guid.Empty)
        {
            Config.Library.InstallId = Guid.NewGuid();
            Save(notify: false);
        }

    }

    /// <summary>
    /// Brings saved settings from an older Poser up to
    /// <see cref="PoserConfiguration.LatestVersion"/>, one version at a time, so
    /// settings that skipped a release still get every step in order.
    ///
    /// <para>Version 2: the bone colors reset once to the current defaults,
    /// which also makes selected and hovered bones follow the accent color.
    /// Sizes, opacity and every other setting keep their saved values.</para>
    ///
    /// <para>Version 3: each action gets a first and a second key binding. The
    /// saved key binding becomes the first.</para>
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
        // A bone dot size still at the old default takes the new default. A size someone chose stays.
        if (Config.Version < 4
            && Math.Abs(Config.Skeleton.BoneDotRadius - 3.4f) < 0.01f)
            Config.Skeleton.BoneDotRadius = new SkeletonConfiguration().BoneDotRadius;

        // The old default depth was 200; a config still at it takes the
        // new default. A depth the user chose stays.
        if (Config.Version < 5 && Config.UndoDepth == 200)
            Config.UndoDepth = 500;
        Config.Version = PoserConfiguration.LatestVersion;
        Save();
    }

    public void Save() => Save(notify: true);

    public void Save(bool notify)
    {
        _persistence.Save(Config);
        if (notify) OnConfigurationChanged?.Invoke();
    }

    public void ApplyChange(bool save = true)
    {
        if (save)
            Save(notify: false);

        OnConfigurationChanged?.Invoke();
    }

    public void Reset()
    {
        // The install id is not a setting: it tells your own scenes from shared ones.
        var install = Config.Library.InstallId;
        Config = new PoserConfiguration();
        Config.Library.InstallId = install;
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

    // The random names for anonymous mode, one per actor for the whole GPose
    // session, so an actor keeps the same random name. They are filed by the
    // actor's lasting id, which stays the same when the actor is redrawn.
    private readonly Dictionary<Guid, string> _lineageAnonymousNames = new();

    /// <summary>
    /// The name to show for an actor. A nickname given in Poser comes first,
    /// then a random name when <c>Display.AnonymousMode</c> is on, then the
    /// actor's name in the scene, which the caller passes in.
    /// </summary>
    public string GetDisplayName(Guid actorLineage, string rawName)
    {
        if (_lineageNicknames.TryGetValue(actorLineage, out var nickname))
            return nickname;

        if (!Config.Display.AnonymousMode)
            return StripObjectIndex(rawName);

        if (!_lineageAnonymousNames.TryGetValue(actorLineage, out var anonName))
        {
            anonName = GenerateRandomName();
            _lineageAnonymousNames[actorLineage] = anonName;
        }
        return anonName;
    }

    /// <summary>Removes the object number at the end of a scene name
    /// ("Name (201)"). Only this method does it, so the name looks the same
    /// everywhere.</summary>
    public static string StripObjectIndex(string rawName)
    {
        int open = rawName.LastIndexOf('(');
        if (open <= 0 || open >= rawName.Length - 2 || rawName[^1] != ')')
            return rawName;
        for (int i = open + 1; i < rawName.Length - 1; i++)
            if (rawName[i] is < '0' or > '9')
                return rawName;
        return rawName.AsSpan(0, open).TrimEnd().ToString();
    }

    // Nicknames, filed by the actor's lasting id.
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

    public void ResetSessionNames()
    {
        // In the next GPose session, a lasting id can belong to a different
        // actor. So names survive an actor leaving or being undone within one
        // session, but not a new session.
        _lineageNicknames.Clear();
        _lineageAnonymousNames.Clear();
    }

    private static readonly Random _random = new();

    private static string GenerateRandomName()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, 5).Select(_ => chars[_random.Next(chars.Length)]).ToArray());
    }

    #endregion
}
