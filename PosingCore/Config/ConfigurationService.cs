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

    public ConfigurationService(IDalamudPluginInterface pluginInterface)
    {
        Instance = this;
        _pluginInterface = pluginInterface;
        Config = _pluginInterface.GetPluginConfig() as PoserConfiguration ?? new PoserConfiguration();
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

    private readonly Dictionary<EntityId, string> _anonymousNames = new();
    private static readonly Random _random = new();

    /// <summary>
    /// Gets the display name for an entity. Returns anonymous name if AnonymousMode is enabled.
    /// </summary>
    public string GetDisplayName(IEntity entity)
    {
        if (!Config.Display.AnonymousMode)
            return entity.Name;

        if (!_anonymousNames.TryGetValue(entity.Id, out var anonName))
        {
            anonName = GenerateRandomName();
            _anonymousNames[entity.Id] = anonName;
        }
        return anonName;
    }

    private static string GenerateRandomName()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return new string(Enumerable.Range(0, 5).Select(_ => chars[_random.Next(chars.Length)]).ToArray());
    }

    #endregion
}
